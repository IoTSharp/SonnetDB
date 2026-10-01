using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Cdc;
using SonnetDB.Streaming;
using Xunit;

namespace SonnetDB.Core.Tests.Streaming;

public sealed class FileStreamingSubscriptionTests
{
    [Fact]
    public async Task OpenAsync_AfterPublishingAndReopening_RestoresEventsAndWatermark()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 2);
        byte[] payload = [0, 1, 255];
        var headers = new Dictionary<string, string> { ["tenant"] = "alpha" };
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition))
        {
            await subscription.AdvanceWatermarkAsync(Utc(10));
            await subscription.PublishAsync(new StreamingEvent("first", 1, Utc(1), payload, headers));
            await subscription.PublishAsync(Event(2));
            payload[0] = 99;
            headers["tenant"] = "mutated";
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        Assert.Equal(Utc(10), reopened.WatermarkUtc);
        Assert.Equal(2, reopened.PendingEventCount);
        Assert.Equal(2, reopened.LastAcceptedSequence);
        StreamingDeliveryBatch batch = Assert.IsType<StreamingDeliveryBatch>(await reopened.ReadBatchAsync());
        Assert.Equal([1L, 2L], batch.Events.Select(static value => value.Sequence));
        Assert.Equal(new byte[] { 0, 1, 255 }, batch.Events[0].Payload);
        Assert.Equal("alpha", batch.Events[0].Headers!["tenant"]);
        Assert.True(batch.Events[0].IsLate);
    }

    [Fact]
    public async Task OpenAsync_MigratesLegacyStateWithoutDeliveryAttemptField()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 1);
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition))
        {
            await subscription.PublishAsync(Event(1));
        }

        byte[] currentBytes = await File.ReadAllBytesAsync(StatePath(directory));
        FileStreamingStateEnvelope current = JsonSerializer.Deserialize(
            currentBytes,
            FileStreamingJsonContext.Default.FileStreamingStateEnvelope)!;
        FileStreamingSubscriptionOptions currentOptions = current.State.Options;
        var legacyState = new LegacyFileStreamingSubscriptionState(
            1,
            current.State.Definition,
            new LegacyFileStreamingSubscriptionOptions(
                currentOptions.MaxEventBytes,
                currentOptions.MaxStoredBytes,
                currentOptions.MaxBatchBytes,
                currentOptions.OperationTimeoutMilliseconds),
            current.State.Checkpoint,
            current.State.WatermarkUtc,
            current.State.LastAcceptedSequence,
            current.State.PendingEventCount,
            current.State.PublishingCompleted,
            current.State.InFlight);
        byte[] legacyStateBytes = JsonSerializer.SerializeToUtf8Bytes(
            legacyState,
            FileStreamingJsonContext.Default.LegacyFileStreamingSubscriptionState);
        var legacyEnvelope = new LegacyFileStreamingStateEnvelope(
            legacyState,
            Convert.ToHexString(SHA256.HashData(legacyStateBytes)));
        await File.WriteAllBytesAsync(
            StatePath(directory),
            JsonSerializer.SerializeToUtf8Bytes(
                legacyEnvelope,
                FileStreamingJsonContext.Default.LegacyFileStreamingStateEnvelope));

        await using var migrated = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        Assert.Equal(100, migrated.Options.MaxDeliveryAttempts);
        Assert.Equal(1, migrated.PendingEventCount);

        FileStreamingStateEnvelope rewritten = JsonSerializer.Deserialize(
            await File.ReadAllBytesAsync(StatePath(directory)),
            FileStreamingJsonContext.Default.FileStreamingStateEnvelope)!;
        Assert.Equal(3, rewritten.State.FormatVersion);
        Assert.Equal(100, rewritten.State.Options.MaxDeliveryAttempts);
        Assert.NotEqual(legacyEnvelope.Sha256, rewritten.Sha256);
    }

    [Fact]
    public async Task ReadBatchAsync_AfterUnacknowledgedReopen_RedeliversSameBatchAndAttempt()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 2);
        StreamingDeliveryBatch first;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition))
        {
            await subscription.PublishAsync(Event(1));
            await subscription.PublishAsync(Event(2));
            first = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
            await subscription.PublishAsync(Event(3));
            first.Events[0].Payload[0] = 99;
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        StreamingDeliveryBatch second = Assert.IsType<StreamingDeliveryBatch>(await reopened.ReadBatchAsync());
        Assert.Equal(first.DeliveryId, second.DeliveryId);
        Assert.Equal(first.CandidateCheckpoint, second.CandidateCheckpoint);
        Assert.Equal(2, second.Attempt);
        Assert.Equal(StreamingDeliveryStatus.Redelivered, second.Status);
        Assert.Equal([1L, 2L], second.Events.Select(static value => value.Sequence));
        Assert.Equal("payload", Encoding.UTF8.GetString(second.Events[0].Payload));
        await reopened.AcknowledgeAsync(second.DeliveryId);
        StreamingDeliveryBatch third = Assert.IsType<StreamingDeliveryBatch>(await reopened.ReadBatchAsync());
        Assert.Equal(3, Assert.Single(third.Events).Sequence);
    }

    [Fact]
    public async Task GetStatusAsync_AfterReopening_ReportsBacklogOldestEventAndInFlightAttempt()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 1);
        var options = new FileStreamingSubscriptionOptions { MaxDeliveryAttempts = 2 };
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options))
        {
            await subscription.PublishAsync(Event(1));
            DateTimeOffset beforeStatus = DateTimeOffset.UtcNow;
            FileStreamingSubscriptionStatus status = await subscription.GetStatusAsync();
            DateTimeOffset afterStatus = DateTimeOffset.UtcNow;
            Assert.Equal(definition.SubscriptionId, status.SubscriptionId);
            Assert.Equal(definition.StreamName, status.StreamName);
            Assert.False(status.PublishingCompleted);
            Assert.Equal(-1, status.CommittedSequence);
            Assert.Equal(1, status.LastAcceptedSequence);
            Assert.Equal(1, status.PendingEventCount);
            Assert.True(status.StoredBytes > 0);
            Assert.Null(status.InFlightDeliveryId);
            Assert.Equal(0, status.InFlightAttempt);
            Assert.Equal(2, status.MaxDeliveryAttempts);
            Assert.Equal(Utc(1), status.OldestEventTimeUtc);
            Assert.True(status.OldestEventAge >= TimeSpan.Zero);
            Assert.InRange(status.ObservedAtUtc, beforeStatus, afterStatus);
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options);
        StreamingDeliveryBatch batch = Assert.IsType<StreamingDeliveryBatch>(await reopened.ReadBatchAsync());
        FileStreamingSubscriptionStatus inFlight = await reopened.GetStatusAsync();
        Assert.Equal(1, inFlight.PendingEventCount);
        Assert.Equal(batch.DeliveryId, inFlight.InFlightDeliveryId);
        Assert.Equal(1, inFlight.InFlightAttempt);
        Assert.Equal(1, inFlight.InFlightEventCount);
        Assert.Equal(Utc(1), inFlight.OldestEventTimeUtc);
        Assert.NotNull(inFlight.OldestEventAge);
    }

    [Fact]
    public async Task ReadBatchAsync_WhenAttemptLimitReached_ReportsStatusAndAllowsAcknowledge()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 1);
        var options = new FileStreamingSubscriptionOptions { MaxDeliveryAttempts = 2 };
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options);
        await subscription.PublishAsync(Event(1));
        StreamingDeliveryBatch first = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
        StreamingDeliveryBatch second = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
        Assert.Equal(first.DeliveryId, second.DeliveryId);
        Assert.Equal(2, second.Attempt);

        FileStreamingDeliveryAttemptLimitException exception =
            await Assert.ThrowsAsync<FileStreamingDeliveryAttemptLimitException>(
                () => subscription.ReadBatchAsync().AsTask());
        Assert.Equal(second.DeliveryId, exception.DeliveryId);
        Assert.Equal(2, exception.Attempt);
        Assert.Equal(2, exception.MaxAttempts);

        FileStreamingSubscriptionStatus blocked = await subscription.GetStatusAsync();
        Assert.True(blocked.InFlightAttempt >= blocked.MaxDeliveryAttempts);
        Assert.Equal(1, blocked.PendingEventCount);
        await subscription.AcknowledgeAsync(second.DeliveryId);
        FileStreamingSubscriptionStatus drained = await subscription.GetStatusAsync();
        Assert.Equal(0, drained.PendingEventCount);
        Assert.Null(drained.OldestEventTimeUtc);
        Assert.Null(drained.InFlightDeliveryId);
    }

    [Fact]
    public async Task AcknowledgeAsync_AfterReopening_RestoresCommittedCheckpointAndReclaimsSpace()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 1);
        StreamingSubscriptionCheckpoint committed;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition))
        {
            await subscription.PublishAsync(Event(1));
            await subscription.PublishAsync(Event(2));
            StreamingDeliveryBatch batch = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
            long originalBytes = subscription.StoredBytes;
            committed = (await subscription.AcknowledgeAsync(batch.DeliveryId)).Checkpoint;
            Assert.Equal(1, subscription.PendingEventCount);
            Assert.True(subscription.StoredBytes < originalBytes);
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        Assert.Equal(committed, reopened.Checkpoint);
        Assert.Equal(2, Assert.Single((await reopened.ReadBatchAsync())!.Events).Sequence);
    }

    [Fact]
    public async Task OpenAsync_AfterCheckpointCommitBeforeSpoolAck_CompletesInterruptedAck()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 1);
        StreamingSubscriptionCheckpoint committed;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition))
        {
            await subscription.PublishAsync(Event(1));
            await subscription.PublishAsync(Event(2));
            StreamingDeliveryBatch batch = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
            await subscription.AdvanceWatermarkAsync(Utc(20));
            committed = batch.CandidateCheckpoint with { WatermarkUtc = Utc(20) };
            await using var store = new FileStreamingSubscriptionCheckpointStore(Path.Combine(directory.Path, "checkpoints"));
            await store.SaveAsync(committed, subscription.Checkpoint.Revision);
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        Assert.Equal(committed, reopened.Checkpoint);
        Assert.Equal(1, reopened.PendingEventCount);
        Assert.Equal(2, Assert.Single((await reopened.ReadBatchAsync())!.Events).Sequence);
    }

    [Fact]
    public async Task OpenAsync_AfterSpoolAckBeforeStatePublication_CompletesInterruptedAck()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 1);
        byte[] oldState;
        StreamingSubscriptionCheckpoint committed;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition))
        {
            await subscription.PublishAsync(Event(1));
            await subscription.PublishAsync(Event(2));
            StreamingDeliveryBatch batch = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
            oldState = await File.ReadAllBytesAsync(StatePath(directory));
            committed = (await subscription.AcknowledgeAsync(batch.DeliveryId)).Checkpoint;
        }

        await File.WriteAllBytesAsync(StatePath(directory), oldState);
        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        Assert.Equal(committed, reopened.Checkpoint);
        Assert.Equal(1, reopened.PendingEventCount);
        Assert.Equal(2, Assert.Single((await reopened.ReadBatchAsync())!.Events).Sequence);
    }

    [Fact]
    public async Task OpenAsync_AfterAppendBeforeStatePublication_PreservesUncertainEvent()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 2);
        byte[] oldState;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition))
        {
            await subscription.PublishAsync(Event(1));
            oldState = await File.ReadAllBytesAsync(StatePath(directory));
            await subscription.PublishAsync(Event(2));
        }

        await File.WriteAllBytesAsync(StatePath(directory), oldState);
        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        Assert.Equal(2, reopened.PendingEventCount);
        Assert.Equal(2, reopened.LastAcceptedSequence);
        Assert.Equal([1L, 2L], (await reopened.ReadBatchAsync())!.Events.Select(static value => value.Sequence));
    }

    [Fact]
    public async Task PublishAsync_AtCapacity_WaitsForAckIncludingInFlightEvents()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 1, capacity: 1);
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        await subscription.PublishAsync(Event(1));
        StreamingDeliveryBatch first = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<StreamingPublishResult> pending = subscription.PublishAsync(Event(2), cancellation.Token).AsTask();
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, subscription.PendingEventCount);
        await subscription.AcknowledgeAsync(first.DeliveryId);
        Assert.Equal(StreamingPublishDisposition.Accepted, (await pending).Disposition);
        Assert.Equal(2, Assert.Single((await subscription.ReadBatchAsync())!.Events).Sequence);
    }

    [Fact]
    public async Task PublishAsync_AtCapacity_CancelledWaitDoesNotAcceptEvent()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 1, capacity: 1);
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition))
        {
            await subscription.PublishAsync(Event(1));
            using var cancellation = new CancellationTokenSource();
            Task<StreamingPublishResult> pending = subscription.PublishAsync(Event(2), cancellation.Token).AsTask();
            Assert.False(pending.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        Assert.Equal(1, reopened.PendingEventCount);
        Assert.Equal(1, Assert.Single((await reopened.ReadBatchAsync())!.Events).Sequence);
    }

    [Fact]
    public async Task PublishAsync_AtByteCapacity_WaitsForAckAndRecoversBound()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 1, capacity: 3);
        var options = new FileStreamingSubscriptionOptions { MaxEventBytes = 1024, MaxStoredBytes = 1600, MaxBatchBytes = 1500 };
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options))
        {
            await subscription.PublishAsync(Event(1) with { Payload = new byte[400] });
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task<StreamingPublishResult> pending = subscription.PublishAsync(Event(2) with { Payload = new byte[400] }, cancellation.Token).AsTask();
            Assert.False(pending.IsCompleted);
            StreamingDeliveryBatch first = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
            await subscription.AcknowledgeAsync(first.DeliveryId);
            await pending;
            Assert.True(subscription.StoredBytes <= options.MaxStoredBytes);
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options);
        Assert.Equal(1, reopened.PendingEventCount);
        Assert.Equal(2, Assert.Single((await reopened.ReadBatchAsync())!.Events).Sequence);
    }

    [Fact]
    public async Task ReadBatchAsync_AtByteBoundary_ReturnsPrefixAndPreservesRemainder()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 3, capacity: 3);
        var options = new FileStreamingSubscriptionOptions { MaxEventBytes = 1024, MaxStoredBytes = 4000, MaxBatchBytes = 1500 };
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options);
        await subscription.PublishAsync(Event(1) with { Payload = new byte[400] });
        await subscription.PublishAsync(Event(2) with { Payload = new byte[400] });
        StreamingDeliveryBatch first = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
        Assert.Equal(1, Assert.Single(first.Events).Sequence);
        await subscription.AcknowledgeAsync(first.DeliveryId);
        Assert.Equal(2, Assert.Single((await subscription.ReadBatchAsync())!.Events).Sequence);
    }

    [Fact]
    public async Task ReadBatchAsync_WhenEmptyAndCancelled_StopsWaitWithoutChangingState()
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition());
        using var cancellation = new CancellationTokenSource();
        Task<StreamingDeliveryBatch?> pending = subscription.ReadBatchAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, subscription.PendingEventCount);
        Assert.Equal(0, subscription.Checkpoint.Revision);
    }

    [Fact]
    public async Task DisposeAsync_WithWaitingPublishAndRead_WakesWaitersAndAllowsReopen()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 1, capacity: 1);
        var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        Task<StreamingDeliveryBatch?> read = subscription.ReadBatchAsync().AsTask();
        await subscription.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));

        var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        await reopened.PublishAsync(Event(1));
        Task<StreamingPublishResult> publish = reopened.PublishAsync(Event(2)).AsTask();
        await reopened.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => publish.WaitAsync(TimeSpan.FromSeconds(2)));
        await using var final = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        Assert.Equal(1, final.PendingEventCount);
    }

    [Fact]
    public async Task CompleteAsync_AfterReopening_DrainsAcceptedEventsAndReturnsNull()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 1);
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition))
        {
            await subscription.PublishAsync(Event(1));
            await subscription.CompleteAsync();
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.PublishAsync(Event(2)).AsTask());
        StreamingDeliveryBatch batch = Assert.IsType<StreamingDeliveryBatch>(await reopened.ReadBatchAsync());
        await reopened.AcknowledgeAsync(batch.DeliveryId);
        Assert.Null(await reopened.ReadBatchAsync());
    }

    [Theory]
    [InlineData(StreamingLateEventPolicy.Drop)]
    [InlineData(StreamingLateEventPolicy.Reject)]
    public async Task PublishAsync_AfterWatermarkReopen_RespectsPersistedLatePolicy(StreamingLateEventPolicy policy)
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition() with { LateEventPolicy = policy, AllowedLatenessMilliseconds = 1000 };
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition))
            await subscription.AdvanceWatermarkAsync(Utc(10));
        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        if (policy == StreamingLateEventPolicy.Drop)
            Assert.Equal(StreamingPublishDisposition.DroppedLate, (await reopened.PublishAsync(Event(1))).Disposition);
        else
            await Assert.ThrowsAsync<StreamingLateEventException>(() => reopened.PublishAsync(Event(1)).AsTask());
        Assert.Equal(0, reopened.PendingEventCount);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reopened.AdvanceWatermarkAsync(Utc(9)).AsTask());
    }

    [Fact]
    public async Task PublishAsync_WithDuplicateDescendingAndOversizedEvents_RejectsWithoutLosingAcceptedEvent()
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition());
        await subscription.PublishAsync(Event(5));
        await Assert.ThrowsAsync<ArgumentException>(() => subscription.PublishAsync(Event(5)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => subscription.PublishAsync(Event(4)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => subscription.PublishAsync(Event(6) with
        {
            Payload = new byte[new FileStreamingSubscriptionOptions().MaxEventBytes + 1],
        }).AsTask());
        Assert.Equal(1, subscription.PendingEventCount);
        Assert.Equal(5, Assert.Single((await subscription.ReadBatchAsync())!.Events).Sequence);
    }

    [Fact]
    public async Task OpenAsync_WithConcurrentOwner_RejectsSecondExecutor()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition();
        await using var first = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        await Assert.ThrowsAsync<IOException>(() => FileStreamingSubscription.OpenAsync(directory.Path, definition).AsTask());
        await first.PublishAsync(Event(1));
    }

    [Theory]
    [InlineData("definition")]
    [InlineData("options")]
    [InlineData("state")]
    [InlineData("checkpoint")]
    [InlineData("spool")]
    [InlineData("spool-metadata")]
    public async Task OpenAsync_WithMismatchedDefinitionOrMissingFiles_FailsClosed(string change)
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition();
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition))
            await subscription.PublishAsync(Event(1));
        switch (change)
        {
            case "definition":
                definition = definition with { StreamName = "other" };
                break;
            case "state":
                File.Delete(StatePath(directory));
                break;
            case "checkpoint":
                File.Delete(Directory.GetFiles(Path.Combine(directory.Path, "checkpoints"), "*.checkpoint.json").Single());
                break;
            case "spool":
                File.Delete(Path.Combine(directory.Path, "events.spool"));
                break;
            case "spool-metadata":
                File.Delete(Path.Combine(directory.Path, "events.spool"));
                File.Delete(Path.Combine(directory.Path, "events.spool.checkpoint"));
                break;
        }

        var options = change == "options" ? new FileStreamingSubscriptionOptions { MaxEventBytes = 1024 } : null;
        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingSubscription.OpenAsync(directory.Path, definition, options).AsTask());
    }

    [Theory]
    [InlineData("state")]
    [InlineData("state-integrity")]
    [InlineData("event-crc")]
    [InlineData("event-truncation")]
    [InlineData("checkpoint")]
    public async Task OpenAsync_WithCorruptFiles_FailsClosed(string change)
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition();
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition))
            await subscription.PublishAsync(Event(1));
        if (change == "state")
            await File.WriteAllTextAsync(StatePath(directory), "{\"state\":");
        else if (change == "state-integrity")
        {
            string json = await File.ReadAllTextAsync(StatePath(directory));
            await File.WriteAllTextAsync(StatePath(directory), json.Replace("\"lastAcceptedSequence\":1", "\"lastAcceptedSequence\":2", StringComparison.Ordinal));
        }
        else if (change == "checkpoint")
        {
            string path = Directory.GetFiles(Path.Combine(directory.Path, "checkpoints"), "*.checkpoint.json").Single();
            await File.WriteAllTextAsync(path, "{");
        }
        else
        {
            string path = Path.Combine(directory.Path, "events.spool");
            byte[] bytes = await File.ReadAllBytesAsync(path);
            if (change == "event-crc")
                bytes[^1] ^= 1;
            else
                bytes = bytes[..^1];
            await File.WriteAllBytesAsync(path, bytes);
        }

        Exception? exception = await Record.ExceptionAsync(() => FileStreamingSubscription.OpenAsync(directory.Path, definition).AsTask());
        Assert.NotNull(exception);
        Assert.True(exception is InvalidDataException or StreamingSubscriptionCheckpointCorruptException);
    }

    [Fact]
    public async Task AcknowledgeAsync_WithExternalCheckpointConflict_FaultsAndPreservesPendingEvents()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 1);
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition))
        {
            await subscription.PublishAsync(Event(1));
            StreamingDeliveryBatch batch = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
            await using var store = new FileStreamingSubscriptionCheckpointStore(Path.Combine(directory.Path, "checkpoints"));
            await store.SaveAsync(subscription.Checkpoint with { Revision = 1 }, expectedRevision: 0);
            await Assert.ThrowsAsync<StreamingSubscriptionCheckpointConflictException>(() => subscription.AcknowledgeAsync(batch.DeliveryId).AsTask());
            Assert.Equal(1, subscription.PendingEventCount);
            await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.ReadBatchAsync().AsTask());
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingSubscription.OpenAsync(directory.Path, definition).AsTask());
        using var spool = new CdcEventSpool(Path.Combine(directory.Path, "events.spool"));
        Assert.Single(await spool.ReplayAsync());
    }

    [Fact]
    public async Task AcknowledgeAsync_WithCancelledTokenOrWrongId_PreservesRecoverableBatch()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 1);
        string deliveryId;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition))
        {
            await subscription.PublishAsync(Event(1));
            deliveryId = (await subscription.ReadBatchAsync())!.DeliveryId;
            await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.AcknowledgeAsync("wrong").AsTask());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subscription.AcknowledgeAsync(deliveryId, cancellation.Token).AsTask());
            Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        Assert.Equal(deliveryId, (await reopened.ReadBatchAsync())!.DeliveryId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcknowledgeAsync_WithHeldCheckpointLock_StopsAtTimeoutOrCancellationAndRecovers(bool cancel)
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 1);
        var options = new FileStreamingSubscriptionOptions { OperationTimeoutMilliseconds = 200 };
        string deliveryId;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options))
        {
            await subscription.PublishAsync(Event(1));
            deliveryId = (await subscription.ReadBatchAsync())!.DeliveryId;
            string lockPath = Directory.GetFiles(Path.Combine(directory.Path, "checkpoints"), "*.lock").Single();
            using var heldLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var cancellation = new CancellationTokenSource();
            Task<StreamingDeliveryReceipt> pending = subscription.AcknowledgeAsync(deliveryId, cancellation.Token).AsTask();
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
            else
                await Assert.ThrowsAsync<TimeoutException>(() => pending);
            Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
            Assert.Equal(1, subscription.PendingEventCount);
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options);
        Assert.Equal(deliveryId, (await reopened.ReadBatchAsync())!.DeliveryId);
    }

    private static StreamingSubscriptionDefinition Definition(int batchSize = 2, int capacity = 4)
        => StreamingSubscriptionDefinition.Create("file-subscription", "orders", batchSize, capacity);

    private static StreamingEvent Event(long sequence)
        => new("event-" + sequence, sequence, Utc(sequence), "payload"u8.ToArray());

    private static DateTimeOffset Utc(long seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);

    private static string StatePath(TemporaryDirectory directory) => Path.Combine(directory.Path, "subscription.json");

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sonnetdb-file-streaming-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
