using SonnetDB.Cdc;
using SonnetDB.Streaming;
using Xunit;

namespace SonnetDB.CrashTests;

public sealed partial class CrashReliabilityTests
{
    [Fact]
    public async Task M43_CdcPartialSnapshot_HardKill_ReopensAndResumesAtCommittedOrdinal()
    {
        const string scenario = "crash_kill9_cdc_partial_snapshot";
        string root = RunKillScenario(scenario, TimeSpan.Zero);
        Assert.Equal("cdc-snapshot-page-committed", File.ReadAllText(Path.Combine(root, scenario + ".ready")));
        string path = Path.Combine(root, "replica.bin");
        CdcSnapshotDescriptor descriptor = M43SnapshotDescriptor();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var replica = CdcSnapshotReplica.Open(path, descriptor))
        {
            CdcSnapshotReplicaState state = replica.GetState();
            Assert.Equal(CdcSnapshotPhase.Snapshot, state.Phase);
            Assert.Equal(1, state.SnapshotRowsCopied);
            Assert.Equal("a", state.LastSnapshotKey);
            Assert.Equal(descriptor.Checkpoint, state.AppliedCheckpoint);
            await replica.WriteSnapshotPageAsync(state.SnapshotRowsCopied, [new CdcSnapshotRow("b", "{\"value\":1}")], deadline.Token);
            await replica.CompleteSnapshotAsync(deadline.Token);
            Assert.Equal(["a", "b"], (await replica.ReadRowsAsync(cancellationToken: deadline.Token)).Rows.Select(static row => row.Key));
        }
        await using var reopened = CdcSnapshotReplica.Open(path, descriptor);
        Assert.Equal(CdcSnapshotPhase.Incremental, reopened.GetState().Phase);
        Assert.Equal(2, reopened.GetState().SnapshotRowsCopied);
        Assert.Equal(["a", "b"], (await reopened.ReadRowsAsync(cancellationToken: deadline.Token)).Rows.Select(static row => row.Key));
    }

    [Fact]
    public async Task M43_CdcMaterializationBeforeAck_HardKill_ReopensFiltersAndConfirmsCommittedEvents()
    {
        const string scenario = "crash_kill9_cdc_materialization_before_spool_ack";
        string root = RunKillScenario(scenario, TimeSpan.Zero);
        Assert.Equal("cdc-materialized-before-spool-ack", File.ReadAllText(Path.Combine(root, scenario + ".ready")));
        string path = Path.Combine(root, "replica.bin");
        string spoolPath = Path.Combine(root, "events.log");
        CdcSnapshotDescriptor descriptor = M43SnapshotDescriptor();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var replica = CdcSnapshotReplica.Open(path, descriptor))
        await using (var spool = new CdcEventSpool(spoolPath))
        {
            Assert.Equal(new CdcCheckpoint(7, 12), replica.GetState().AppliedCheckpoint);
            CdcSnapshotRow row = Assert.Single((await replica.ReadRowsAsync(cancellationToken: deadline.Token)).Rows);
            Assert.Equal(new CdcSnapshotRow("a", "{\"value\":2}"), row);
            Assert.Empty(spool.AcknowledgedCheckpoints);
            Assert.Equal([11L, 12L, 13L], (await spool.ReplayAsync(cancellationToken: deadline.Token)).Select(static value => value.Metadata.Checkpoint.Offset));

            // 位点已经随物化行持久化；只回放它之后的事件，随后确认已提交前缀。
            CdcEventSpoolBatch pending = await spool.ReplayBatchAsync(replica.GetState().AppliedCheckpoint, cancellationToken: deadline.Token);
            Assert.Equal(13, Assert.Single(pending.Events).Metadata.Checkpoint.Offset);
            await spool.AcknowledgeAsync(replica.GetState().AppliedCheckpoint, deadline.Token);
            CdcSnapshotReplicaState applied = await replica.ApplyIncrementalAsync(pending.Events, deadline.Token);
            await spool.AcknowledgeAsync(applied.AppliedCheckpoint, deadline.Token);
            Assert.Equal(new CdcCheckpoint(7, 13), applied.AppliedCheckpoint);
        }

        await using var reopenedReplica = CdcSnapshotReplica.Open(path, descriptor);
        await using var reopenedSpool = new CdcEventSpool(spoolPath);
        Assert.Equal([new CdcSnapshotRow("a", "{\"value\":2}"), new CdcSnapshotRow("c", "{\"value\":3}")],
            (await reopenedReplica.ReadRowsAsync(cancellationToken: deadline.Token)).Rows);
        Assert.Equal(new CdcCheckpoint(7, 13), reopenedReplica.GetState().AppliedCheckpoint);
        Assert.Equal(new CdcCheckpoint(7, 13), reopenedSpool.AcknowledgedCheckpoint);
        Assert.Empty(await reopenedSpool.ReplayAsync(cancellationToken: deadline.Token));
    }

    [Fact]
    public async Task M43_CdcSingleTailBeforeAck_HardKill_ReplayEmptyStillConfirmsAndReusesCapacity()
    {
        const string scenario = "crash_kill9_cdc_single_tail_before_ack";
        string root = RunKillScenario(scenario, TimeSpan.Zero);
        Assert.Equal("cdc-single-tail-materialized-before-ack", File.ReadAllText(Path.Combine(root, scenario + ".ready")));
        string path = Path.Combine(root, "replica.bin");
        string spoolPath = Path.Combine(root, "events.log");
        CdcSnapshotDescriptor descriptor = M43SnapshotDescriptor() with { RowCount = 0 };
        var limits = new CdcEventSpoolOptions { MaxEvents = 1, MaxReplayEvents = 1 };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var replica = CdcSnapshotReplica.Open(path, descriptor))
        await using (var spool = new CdcEventSpool(spoolPath, limits))
        {
            CdcCheckpoint applied = replica.GetState().AppliedCheckpoint;
            Assert.Equal(new CdcCheckpoint(7, 11), applied);
            Assert.Equal(1, spool.EventCount);
            CdcEventSpoolBatch replay = await spool.ReplayBatchAsync(applied, cancellationToken: deadline.Token);
            Assert.Empty(replay.Events);
            Assert.Empty(spool.AcknowledgedCheckpoints);
            await Assert.ThrowsAsync<InvalidOperationException>(() => spool.AppendAsync(
                M43CdcRecoveryEvent(12, "b"), deadline.Token).AsTask());

            await spool.AcknowledgeAsync(applied, deadline.Token);
            Assert.Equal(0, spool.EventCount);
            await spool.AppendAsync(M43CdcRecoveryEvent(12, "b"), deadline.Token);
            CdcEventSpoolBatch next = await spool.ReplayBatchAsync(applied, cancellationToken: deadline.Token);
            CdcSnapshotReplicaState committed = await replica.ApplyIncrementalAsync(next.Events, deadline.Token);
            await spool.AcknowledgeAsync(committed.AppliedCheckpoint, deadline.Token);
            Assert.Equal(new CdcCheckpoint(7, 12), committed.AppliedCheckpoint);
        }

        await using var reopenedReplica = CdcSnapshotReplica.Open(path, descriptor);
        await using var reopenedSpool = new CdcEventSpool(spoolPath, limits);
        Assert.Equal(["a", "b"], (await reopenedReplica.ReadRowsAsync(cancellationToken: deadline.Token)).Rows.Select(static row => row.Key));
        Assert.Equal(new CdcCheckpoint(7, 12), reopenedSpool.AcknowledgedCheckpoint);
        Assert.Equal(0, reopenedSpool.EventCount);
    }

    [Fact]
    public async Task M43_StreamingInFlight_HardKill_RedeliversStableDeliveryIdAndPersistsAck()
    {
        const string scenario = "crash_kill9_streaming_inflight";
        string root = RunKillScenario(scenario, TimeSpan.Zero);
        Assert.Equal("streaming-inflight-before-ack", File.ReadAllText(Path.Combine(root, scenario + ".ready")));
        string deliveryId = File.ReadAllText(Path.Combine(root, "streaming-delivery-id.txt"));
        Assert.NotEmpty(deliveryId);
        string directory = Path.Combine(root, "subscription");
        StreamingSubscriptionDefinition definition = M43StreamingDefinition();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        StreamingSubscriptionCheckpoint committed;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory, definition, cancellationToken: deadline.Token))
        {
            Assert.Equal(2, subscription.PendingEventCount);
            Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
            StreamingDeliveryBatch retry = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync(deadline.Token));
            Assert.Equal(deliveryId, retry.DeliveryId);
            Assert.Equal(2, retry.Attempt);
            Assert.Equal(StreamingDeliveryStatus.Redelivered, retry.Status);
            Assert.Equal(["streaming-crash-1", "streaming-crash-2"], retry.Events.Select(static value => value.EventId));
            Assert.Equal([1L, 2L], retry.Events.Select(static value => value.Sequence));
            StreamingDeliveryReceipt receipt = await subscription.AcknowledgeAsync(retry.DeliveryId, deadline.Token);
            Assert.Equal(StreamingDeliveryStatus.Acknowledged, receipt.Status);
            committed = receipt.Checkpoint;
            Assert.Equal(2, committed.CommittedSequence);
            Assert.Equal(0, subscription.PendingEventCount);
            Assert.Null(await subscription.ReadBatchAsync(deadline.Token));
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory, definition, cancellationToken: deadline.Token);
        Assert.Equal(committed, reopened.Checkpoint);
        Assert.Equal(0, reopened.PendingEventCount);
        Assert.Equal(0, reopened.StoredBytes);
        Assert.Null(await reopened.ReadBatchAsync(deadline.Token));
    }

    private static CdcSnapshotDescriptor M43SnapshotDescriptor()
        => new("crash-fixed-view", "crash-source", "documents", "documents", 1, new CdcCheckpoint(7, 10), 2);

    private static StreamingSubscriptionDefinition M43StreamingDefinition()
        => StreamingSubscriptionDefinition.Create("crash-subscription", "crash-events", batchSize: 2, capacity: 4);

    private static CdcEvent M43CdcRecoveryEvent(long offset, string key)
        => new($"crash-event-{offset}", "crash-source", "documents", key, offset,
            new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero),
            new CdcEventMetadata(1, "documents", 1, CdcOperation.Insert, new CdcCheckpoint(7, offset)),
            null, "{\"value\":1}");
}
