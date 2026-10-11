using System.Security.Cryptography;
using System.Text.Json;
using SonnetDB.Cdc;
using SonnetDB.Streaming;

namespace SonnetDB.Core.Tests.Cdc;

public sealed class CdcStreamingBridgeTests
{
    [Fact]
    public async Task PumpOnceAsync_WithGappedSourceOffsets_PreservesCdcPayloadAndContiguousTargetSequenceAfterReopen()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var options = new CdcStreamingBridgeOptions { MaxBatchEvents = 1 };
        await using (var source = new CdcEventSpool(directory.SourcePath))
        await using (var target = await OpenTargetAsync(directory, deadline.Token))
        await using (var bridge = await CdcStreamingBridge.OpenAsync(directory.StatePath, source, target, Binding(), options, deadline.Token))
        {
            await source.AppendAsync(Event(10) with { Sequence = 101 }, deadline.Token);
            await source.AppendAsync(Event(20) with { Sequence = 5 }, deadline.Token);
            CdcStreamingBridgeResult first = await bridge.PumpOnceAsync(bridge.GetState().Revision, deadline.Token);
            Assert.Equal(1, first.PublishedEvents);
            Assert.Equal(10, first.CommittedSourceOffset);
            Assert.Equal(0, first.LastTargetSequence);
            Assert.True(first.HasMore);
            await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.PumpOnceAsync(0, deadline.Token).AsTask());
            StreamingDeliveryBatch batch = (await target.ReadBatchAsync(deadline.Token))!;
            StreamingEvent mapped = Assert.Single(batch.Events);
            Assert.Equal("cdc-10", mapped.EventId);
            Assert.Equal(0, mapped.Sequence);
            Assert.Equal(101, CdcEventCodec.Decode(mapped.Payload).Sequence);
            Assert.Equal(new CdcCheckpoint(7, 10), CdcEventCodec.Decode(mapped.Payload).Metadata.Checkpoint);
            await target.AcknowledgeAsync(batch.DeliveryId, deadline.Token);
            CdcStreamingBridgeResult second = await bridge.PumpOnceAsync(bridge.GetState().Revision, deadline.Token);
            Assert.Equal(20, second.CommittedSourceOffset);
            Assert.Equal(1, second.LastTargetSequence);
            Assert.False(second.HasMore);
        }

        await using var reopenedSource = new CdcEventSpool(directory.SourcePath);
        await using var reopenedTarget = await OpenTargetAsync(directory, deadline.Token);
        await using var reopenedBridge = await CdcStreamingBridge.OpenAsync(directory.StatePath, reopenedSource,
            reopenedTarget, Binding(), options, deadline.Token);
        Assert.Equal(20, reopenedBridge.GetState().CommittedSourceOffset);
        StreamingDeliveryBatch pending = (await reopenedTarget.ReadBatchAsync(deadline.Token))!;
        Assert.Equal(1, Assert.Single(pending.Events).Sequence);
        Assert.Equal(5, CdcEventCodec.Decode(pending.Events[0].Payload).Sequence);
        await reopenedTarget.AcknowledgeAsync(pending.DeliveryId, deadline.Token);
        await reopenedSource.AppendAsync(Event(40), deadline.Token);
        CdcStreamingBridgeResult final = await reopenedBridge.PumpOnceAsync(reopenedBridge.GetState().Revision, deadline.Token);
        Assert.Equal(2, final.LastTargetSequence);
        Assert.Equal(40, final.CommittedSourceOffset);
        Assert.Equal(0, reopenedSource.EventCount);
    }

    [Theory]
    [InlineData("OutboxCreated")]
    [InlineData("TargetIntentSaved")]
    [InlineData("TargetAccepted")]
    [InlineData("TargetReceiptCompleted")]
    [InlineData("OutboxProgressSaved")]
    [InlineData("SourceAcknowledged")]
    [InlineData("BridgeCommitted")]
    public async Task OpenAsync_AfterInterruptedPersistentBoundary_RecoversWithoutDuplicatingTarget(string boundary)
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var source = new CdcEventSpool(directory.SourcePath))
        await using (var target = await OpenTargetAsync(directory, deadline.Token))
        await using (var bridge = await CdcStreamingBridge.OpenCoreAsync(directory.StatePath, source, target, Binding(),
            null, deadline.Token, step =>
            {
                if (step == boundary)
                    throw new IOException("Injected durable boundary interruption.");
            }))
        {
            await source.AppendAsync(Event(10), deadline.Token);
            await Assert.ThrowsAsync<IOException>(() => bridge.PumpOnceAsync(0, deadline.Token).AsTask());
        }

        await using var reopenedSource = new CdcEventSpool(directory.SourcePath);
        await using var reopenedTarget = await OpenTargetAsync(directory, deadline.Token);
        await using var recovered = await CdcStreamingBridge.OpenAsync(directory.StatePath, reopenedSource,
            reopenedTarget, Binding(), cancellationToken: deadline.Token);
        Assert.Equal(10, recovered.GetState().CommittedSourceOffset);
        Assert.Equal(0, recovered.GetState().LastTargetSequence);
        Assert.Equal(0, recovered.GetState().OutboxEventCount);
        Assert.Equal(new CdcCheckpoint(7, 10), reopenedSource.AcknowledgedCheckpoint);
        Assert.Equal(1, reopenedTarget.PendingEventCount);
        StreamingDeliveryBatch batch = (await reopenedTarget.ReadBatchAsync(deadline.Token))!;
        Assert.Equal("cdc-10", Assert.Single(batch.Events).EventId);
        Assert.Equal(0, batch.Events[0].Sequence);
        await reopenedTarget.AcknowledgeAsync(batch.DeliveryId, deadline.Token);
        Assert.Equal(0, reopenedTarget.StoredBytes);
    }

    [Fact]
    public async Task OpenAsync_AfterCompletedTargetReceiptAndConsumerReclamation_UsesDurableProofBeforeSourceAck()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await CreateInterruptedAsync(directory, "TargetReceiptCompleted", deadline.Token);
        await using (var target = await OpenTargetAsync(directory, deadline.Token))
        {
            StreamingDeliveryBatch batch = (await target.ReadBatchAsync(deadline.Token))!;
            await target.AcknowledgeAsync(batch.DeliveryId, deadline.Token);
            Assert.Equal(0, target.StoredBytes);
        }

        await using var source = new CdcEventSpool(directory.SourcePath);
        await using var reopenedTarget = await OpenTargetAsync(directory, deadline.Token);
        await using var recovered = await CdcStreamingBridge.OpenAsync(directory.StatePath, source, reopenedTarget,
            Binding(), cancellationToken: deadline.Token);
        Assert.Equal(10, recovered.GetState().CommittedSourceOffset);
        Assert.Equal(0, source.EventCount);
        Assert.Equal(0, reopenedTarget.PendingEventCount);
        Assert.Equal(0, reopenedTarget.LastAcceptedSequence);
        Assert.Equal(0, reopenedTarget.Checkpoint.CommittedSequence);
    }

    [Fact]
    public async Task OpenAsync_WithUnknownTargetReceiptAndReclaimedEvent_FailsClosedAndRetainsSourceOutbox()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await CreateInterruptedAsync(directory, "TargetAccepted", deadline.Token);
        // 故意违反先恢复桥接再启动消费者的顺序，验证无法证明发布身份时绝不源 ACK 或重发。
        await using (var target = await OpenTargetAsync(directory, deadline.Token))
        {
            StreamingDeliveryBatch batch = (await target.ReadBatchAsync(deadline.Token))!;
            await target.AcknowledgeAsync(batch.DeliveryId, deadline.Token);
        }

        await using var source = new CdcEventSpool(directory.SourcePath);
        await using var reopenedTarget = await OpenTargetAsync(directory, deadline.Token);
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() => CdcStreamingBridge.OpenAsync(
            directory.StatePath, source, reopenedTarget, Binding(), cancellationToken: deadline.Token).AsTask());
        Assert.Contains("恢复交接", exception.Message);
        Assert.Null(source.AcknowledgedCheckpoint);
        Assert.Equal(1, source.EventCount);
        Assert.Equal(0, reopenedTarget.PendingEventCount);
        Assert.True(File.Exists(directory.StatePath));
    }

    [Fact]
    public async Task PumpOnceAsync_WithMixedSchemaInOneBatch_RejectsWholeBatchBeforePublishingOrAck()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var source = new CdcEventSpool(directory.SourcePath);
        await using var target = await OpenTargetAsync(directory, deadline.Token);
        await using var bridge = await CdcStreamingBridge.OpenAsync(directory.StatePath, source, target, Binding(),
            cancellationToken: deadline.Token);
        await source.AppendAsync(Event(10), deadline.Token);
        await source.AppendAsync(Event(20) with { Metadata = Event(20).Metadata with { Schema = "other" } }, deadline.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => bridge.PumpOnceAsync(0, deadline.Token).AsTask());
        Assert.Equal(0, target.PendingEventCount);
        Assert.Null(source.AcknowledgedCheckpoint);
        Assert.Equal(0, bridge.GetState().Revision);
    }

    [Fact]
    public async Task OpenAsync_WithMultiplePartitionsOrWrongSavedBinding_RejectsWithoutAck()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var source = new CdcEventSpool(directory.SourcePath);
        await using var target = await OpenTargetAsync(directory, deadline.Token);
        await using (var initial = await CdcStreamingBridge.OpenAsync(directory.StatePath, source, target, Binding(),
            cancellationToken: deadline.Token))
        {
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => CdcStreamingBridge.OpenAsync(directory.StatePath, source,
            target, Binding() with { Source = "another" }, cancellationToken: deadline.Token).AsTask());
        await source.AppendAsync(Event(10), deadline.Token);
        CdcEvent other = Event(20) with { Metadata = Event(20).Metadata with { Checkpoint = new(8, 20) } };
        await source.AppendAsync(other, deadline.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => CdcStreamingBridge.OpenAsync(directory.StatePath, source,
            target, Binding(), cancellationToken: deadline.Token).AsTask());
        Assert.Empty(source.AcknowledgedCheckpoints);
        Assert.Equal(0, target.PendingEventCount);
    }

    [Fact]
    public async Task OpenAsync_WithWrongTargetDefinitionOrIndependentBridge_RejectsTargetBinding()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var source = new CdcEventSpool(directory.SourcePath);
        await using var target = await OpenTargetAsync(directory, deadline.Token);
        await using (var initial = await CdcStreamingBridge.OpenAsync(directory.StatePath, source, target, Binding(),
            cancellationToken: deadline.Token))
        {
        }

        await using var otherTarget = await FileStreamingSubscription.OpenAsync(Path.Combine(directory.Root, "other-target"),
            Definition() with { StreamName = "different-stream" }, cancellationToken: deadline.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => CdcStreamingBridge.OpenAsync(directory.StatePath, source,
            otherTarget, Binding(), cancellationToken: deadline.Token).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => CdcStreamingBridge.OpenAsync(Path.Combine(directory.Root, "other-bridge.json"),
            source, target, Binding() with { BridgeId = "another-bridge" }, cancellationToken: deadline.Token).AsTask());
    }

    [Fact]
    public async Task PumpOnceAsync_WithSourceOrTargetAdvancedOutsideBridge_RejectsConditionalBoundary()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var source = new CdcEventSpool(directory.SourcePath);
        await using var target = await OpenTargetAsync(directory, deadline.Token);
        await using var bridge = await CdcStreamingBridge.OpenAsync(directory.StatePath, source, target, Binding(),
            cancellationToken: deadline.Token);
        await source.AppendAsync(Event(10), deadline.Token);
        await target.PublishAsync(new("external-event", 0, Utc(), [1]), deadline.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => bridge.PumpOnceAsync(0, deadline.Token).AsTask());
        Assert.Null(source.AcknowledgedCheckpoint);
        Assert.Equal(1, source.EventCount);
    }

    [Fact]
    public async Task PumpOnceAsync_WithExternallyAcknowledgedSource_RejectsBeforePublishing()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var source = new CdcEventSpool(directory.SourcePath);
        await using var target = await OpenTargetAsync(directory, deadline.Token);
        await using var bridge = await CdcStreamingBridge.OpenAsync(directory.StatePath, source, target, Binding(),
            cancellationToken: deadline.Token);
        await source.AppendAsync(Event(10), deadline.Token);
        await source.AcknowledgeAsync(new(7, 10), deadline.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => bridge.PumpOnceAsync(0, deadline.Token).AsTask());
        Assert.Equal(0, target.PendingEventCount);
    }

    [Fact]
    public async Task PumpOnceAsync_WithCancellationBeforeOrAfterTargetProof_LeavesRecoverableSource()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancelled = new CancellationTokenSource();
        using var duringPublish = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        cancelled.Cancel();
        await using (var source = new CdcEventSpool(directory.SourcePath))
        await using (var target = await OpenTargetAsync(directory, deadline.Token))
        await using (var bridge = await CdcStreamingBridge.OpenCoreAsync(directory.StatePath, source, target, Binding(),
            null, deadline.Token, step =>
            {
                if (step == "TargetReceiptCompleted")
                    duringPublish.Cancel();
            }))
        {
            await source.AppendAsync(Event(10), deadline.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.PumpOnceAsync(0, cancelled.Token).AsTask());
            Assert.Equal(0, bridge.GetState().Revision);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.PumpOnceAsync(0, duringPublish.Token).AsTask());
            Assert.Null(source.AcknowledgedCheckpoint);
        }

        await using var reopenedSource = new CdcEventSpool(directory.SourcePath);
        await using var reopenedTarget = await OpenTargetAsync(directory, deadline.Token);
        await using var recovered = await CdcStreamingBridge.OpenAsync(directory.StatePath, reopenedSource, reopenedTarget,
            Binding(), cancellationToken: deadline.Token);
        Assert.Equal(10, recovered.GetState().CommittedSourceOffset);
        Assert.Equal(1, reopenedTarget.PendingEventCount);
    }

    [Fact]
    public async Task PumpOnceAsync_WithInsufficientOutboxCapacity_PreservesOriginalSourceAndTarget()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var source = new CdcEventSpool(directory.SourcePath);
        await using var target = await OpenTargetAsync(directory, deadline.Token);
        var options = new CdcStreamingBridgeOptions { MaxStateBytes = 2048 };
        await using var bridge = await CdcStreamingBridge.OpenAsync(directory.StatePath, source, target, Binding(), options, deadline.Token);
        await source.AppendAsync(Event(10) with { AfterJson = "{\"large\":\"" + new string('x', 4000) + "\"}" }, deadline.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => bridge.PumpOnceAsync(0, deadline.Token).AsTask());
        Assert.Equal(0, target.PendingEventCount);
        Assert.Null(source.AcknowledgedCheckpoint);
        Assert.Equal(0, bridge.GetState().Revision);
    }

    [Fact]
    public async Task OpenAsync_AfterTargetCapacityBackpressure_ResumesOutboxAfterConsumerReclaimsSpace()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var source = new CdcEventSpool(directory.SourcePath);
        var definition = StreamingSubscriptionDefinition.Create("bridge-target", "device-changes", batchSize: 1, capacity: 1);
        await using var target = await FileStreamingSubscription.OpenAsync(directory.TargetPath, definition, cancellationToken: deadline.Token);
        await using (var bridge = await CdcStreamingBridge.OpenAsync(directory.StatePath, source, target, Binding(),
            cancellationToken: deadline.Token))
        {
            await source.AppendAsync(Event(10), deadline.Token);
            await source.AppendAsync(Event(20), deadline.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.PumpOnceAsync(0, deadline.Token).AsTask());
            Assert.Equal(2, bridge.GetState().OutboxEventCount);
            Assert.Equal(1, bridge.GetState().PublishedOutboxEvents);
            Assert.Null(source.AcknowledgedCheckpoint);
            Assert.Equal(1, target.PendingEventCount);
        }

        StreamingDeliveryBatch first = (await target.ReadBatchAsync(deadline.Token))!;
        await target.AcknowledgeAsync(first.DeliveryId, deadline.Token);
        await using var recovered = await CdcStreamingBridge.OpenAsync(directory.StatePath, source, target, Binding(),
            cancellationToken: deadline.Token);
        Assert.Equal(20, recovered.GetState().CommittedSourceOffset);
        Assert.Equal(1, recovered.GetState().LastTargetSequence);
        Assert.Equal(1, target.PendingEventCount);
        Assert.Equal("cdc-20", Assert.Single((await target.ReadBatchAsync(deadline.Token))!.Events).EventId);
    }

    [Fact]
    public async Task PumpOnceAsync_WithHeldSourceGate_StopsAtOperationTimeoutWithoutPublishing()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var source = new CdcEventSpool(directory.SourcePath);
        await using var target = await OpenTargetAsync(directory, deadline.Token);
        var options = new CdcStreamingBridgeOptions { OperationTimeoutMilliseconds = 1000 };
        await using var bridge = await CdcStreamingBridge.OpenAsync(directory.StatePath, source, target, Binding(), options, deadline.Token);
        await source.AppendAsync(Event(10), deadline.Token);
        SemaphoreSlim sourceGate = CdcLocalPipelineGate.For(source);
        await sourceGate.WaitAsync(deadline.Token);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => bridge.PumpOnceAsync(0, deadline.Token).AsTask());
            Assert.Equal(0, bridge.GetState().Revision);
            Assert.Equal(0, target.PendingEventCount);
            Assert.Null(source.AcknowledgedCheckpoint);
        }
        finally
        {
            sourceGate.Release();
        }
    }

    [Fact]
    public async Task OpenAsync_AfterCompletedPrefixReclamation_ResumesRemainingOutboxAndAcknowledgesWholeSourceBatch()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var source = new CdcEventSpool(directory.SourcePath))
        await using (var target = await OpenTargetAsync(directory, deadline.Token))
        await using (var bridge = await CdcStreamingBridge.OpenCoreAsync(directory.StatePath, source, target, Binding(),
            null, deadline.Token, step =>
            {
                if (step == "TargetReceiptCompleted")
                    throw new IOException("Interrupted after first completed target receipt.");
            }))
        {
            await source.AppendAsync(Event(10), deadline.Token);
            await source.AppendAsync(Event(20), deadline.Token);
            await Assert.ThrowsAsync<IOException>(() => bridge.PumpOnceAsync(0, deadline.Token).AsTask());
            Assert.Equal(2, bridge.GetState().OutboxEventCount);
            Assert.Equal(0, bridge.GetState().PublishedOutboxEvents);
        }

        await using var reopenedSource = new CdcEventSpool(directory.SourcePath);
        await using var reopenedTarget = await OpenTargetAsync(directory, deadline.Token);
        StreamingDeliveryBatch prefix = (await reopenedTarget.ReadBatchAsync(deadline.Token))!;
        await reopenedTarget.AcknowledgeAsync(prefix.DeliveryId, deadline.Token);
        await using var recovered = await CdcStreamingBridge.OpenAsync(directory.StatePath, reopenedSource,
            reopenedTarget, Binding(), cancellationToken: deadline.Token);
        Assert.Equal(20, recovered.GetState().CommittedSourceOffset);
        Assert.Equal(1, recovered.GetState().LastTargetSequence);
        Assert.Equal(0, reopenedSource.EventCount);
        Assert.Equal("cdc-20", Assert.Single((await reopenedTarget.ReadBatchAsync(deadline.Token))!.Events).EventId);
    }

    [Theory]
    [InlineData("TargetReceiptCompleted")]
    [InlineData("SourceAcknowledged")]
    public async Task OpenAsync_WithMissingBridgeStateAndPersistentTargetProof_RejectsReinitialization(string boundary)
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await CreateInterruptedAsync(directory, boundary, deadline.Token);
        File.Delete(directory.StatePath);
        await using var source = new CdcEventSpool(directory.SourcePath);
        await using var target = await OpenTargetAsync(directory, deadline.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => CdcStreamingBridge.OpenAsync(directory.StatePath, source,
            target, Binding(), cancellationToken: deadline.Token).AsTask());
        Assert.False(File.Exists(directory.StatePath));
        // 同时丢失 bridge lease 标记时，目标凭据仍阻止重建，且不能先保存初始化状态再拒绝。
        File.Delete(directory.StatePath + ".lock");
        await Assert.ThrowsAsync<InvalidDataException>(() => CdcStreamingBridge.OpenAsync(directory.StatePath, source,
            target, Binding(), cancellationToken: deadline.Token).AsTask());
        Assert.False(File.Exists(directory.StatePath));
        Assert.Equal(0, target.LastAcceptedSequence);
        Assert.Equal(1, target.PendingEventCount);
        Assert.Equal(boundary == "SourceAcknowledged" ? 0 : 1, source.EventCount);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("checkpoint")]
    [InlineData("lease")]
    public async Task OpenAsync_WithWindowsCaseVariantSourceCollision_RejectsBeforeWritingState(string collision)
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var source = new CdcEventSpool(directory.SourcePath);
        await source.AppendAsync(Event(10), deadline.Token);
        await using var target = await OpenTargetAsync(directory, deadline.Token);
        string path = (collision == "data" ? source.FilePath : collision == "checkpoint" ? source.CheckpointPath : source.LeasePath).ToUpperInvariant();
        await Assert.ThrowsAsync<ArgumentException>(() => CdcStreamingBridge.OpenAsync(path, source, target,
            Binding(), cancellationToken: deadline.Token).AsTask());
        Assert.Equal("cdc-10", Assert.Single((await source.ReplayBatchAsync(cancellationToken: deadline.Token)).Events).EventId);
    }

    [Fact]
    public async Task OpenAsync_WithBridgeLeaseCollidingWithSourceData_RejectsBeforeAcquiringLease()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var source = new CdcEventSpool(directory.StatePath + ".lock");
        await source.AppendAsync(Event(10), deadline.Token);
        await using var target = await OpenTargetAsync(directory, deadline.Token);
        await Assert.ThrowsAsync<ArgumentException>(() => CdcStreamingBridge.OpenAsync(directory.StatePath, source, target,
            Binding(), cancellationToken: deadline.Token).AsTask());
        Assert.False(File.Exists(directory.StatePath));
        Assert.Equal(1, source.EventCount);
    }

    [Fact]
    public async Task OpenAsync_WithSourceAtTargetReceiptPath_RejectsWithoutOverwritingSourceFrames()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var target = await OpenTargetAsync(directory, deadline.Token);
        await using var source = new CdcEventSpool(Path.Combine(directory.TargetPath, "cdc-bridge-receipt.json"));
        await source.AppendAsync(Event(10), deadline.Token);
        await Assert.ThrowsAsync<ArgumentException>(() => CdcStreamingBridge.OpenAsync(directory.StatePath, source, target,
            Binding(), cancellationToken: deadline.Token).AsTask());
        Assert.Equal("cdc-10", Assert.Single((await source.ReplayBatchAsync(cancellationToken: deadline.Token)).Events).EventId);
        Assert.Equal(0, target.PendingEventCount);
    }

    [Fact]
    public async Task OpenAsync_WithValidHashButCorruptOutboxSequence_RejectsWithoutSourceAck()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await CreateInterruptedAsync(directory, "OutboxCreated", deadline.Token);
        CdcStreamingBridgeStateEnvelope envelope = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(directory.StatePath, deadline.Token),
            CdcStreamingBridgeJsonContext.Default.CdcStreamingBridgeStateEnvelope)!;
        CdcStreamingBridgeOutboxEvent item = envelope.State.Outbox!.Events[0];
        CdcStreamingBridgeFileState corrupt = envelope.State with
        {
            Outbox = envelope.State.Outbox with { Events = [item with { Event = item.Event with { Sequence = 99 } }] },
        };
        byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(corrupt, CdcStreamingBridgeJsonContext.Default.CdcStreamingBridgeFileState);
        await File.WriteAllBytesAsync(directory.StatePath, JsonSerializer.SerializeToUtf8Bytes(new CdcStreamingBridgeStateEnvelope(corrupt,
            Convert.ToHexString(SHA256.HashData(stateBytes))), CdcStreamingBridgeJsonContext.Default.CdcStreamingBridgeStateEnvelope), deadline.Token);
        await using var source = new CdcEventSpool(directory.SourcePath);
        await using var target = await OpenTargetAsync(directory, deadline.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => CdcStreamingBridge.OpenAsync(directory.StatePath, source, target,
            Binding(), cancellationToken: deadline.Token).AsTask());
        Assert.Null(source.AcknowledgedCheckpoint);
        Assert.Equal(0, target.PendingEventCount);
    }

    private static async Task CreateInterruptedAsync(TemporaryDirectory directory, string boundary, CancellationToken token)
    {
        await using var source = new CdcEventSpool(directory.SourcePath);
        await using var target = await OpenTargetAsync(directory, token);
        await using var bridge = await CdcStreamingBridge.OpenCoreAsync(directory.StatePath, source, target, Binding(), null,
            token, step =>
            {
                if (step == boundary)
                    throw new IOException("Injected durable boundary interruption.");
            });
        await source.AppendAsync(Event(10), token);
        await Assert.ThrowsAsync<IOException>(() => bridge.PumpOnceAsync(0, token).AsTask());
    }

    private static ValueTask<FileStreamingSubscription> OpenTargetAsync(TemporaryDirectory directory, CancellationToken token)
        => FileStreamingSubscription.OpenAsync(directory.TargetPath, Definition(), cancellationToken: token);

    private static StreamingSubscriptionDefinition Definition()
        => StreamingSubscriptionDefinition.Create("bridge-target", "device-changes", batchSize: 1, capacity: 8);

    private static CdcStreamingBridgeBinding Binding() => new("devices-bridge", "source-db", "devices", "documents", 1, 7);

    private static DateTimeOffset Utc() => new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    private static CdcEvent Event(long offset)
        => new($"cdc-{offset}", "source-db", "devices", "device", offset, Utc(),
            new(1, "documents", 1, CdcOperation.Insert, new(7, offset)), null, "{\"value\":1}");

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory() => Directory.CreateDirectory(Root);

        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "sonnetdb-cdc-stream-bridge-" + Guid.NewGuid().ToString("N"));
        internal string SourcePath => Path.Combine(Root, "source.spool");
        internal string TargetPath => Path.Combine(Root, "target");
        internal string StatePath => Path.Combine(Root, "bridge.json");

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
