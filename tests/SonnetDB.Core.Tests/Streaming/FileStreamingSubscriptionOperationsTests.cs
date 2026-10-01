using System.Security.Cryptography;
using System.Text.Json;
using SonnetDB.Streaming;
using Xunit;

namespace SonnetDB.Core.Tests.Streaming;

public sealed class FileStreamingSubscriptionOperationsTests
{
    [Fact]
    public async Task PauseConsumptionAsync_AfterReopening_PreservesPauseAndAllowsPublishingUntilResume()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition();
        long pausedRevision;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition))
        {
            await subscription.PublishAsync(Event(1));
            pausedRevision = await subscription.PauseConsumptionAsync(subscription.StateRevision);
            Assert.True(subscription.ConsumptionPaused);
            Assert.Equal(pausedRevision, await subscription.PauseConsumptionAsync(pausedRevision));
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        Assert.True(reopened.ConsumptionPaused);
        Assert.Equal(pausedRevision, reopened.StateRevision);
        Assert.Equal(StreamingPublishDisposition.Accepted, (await reopened.PublishAsync(Event(2))).Disposition);
        await reopened.AdvanceWatermarkAsync(DateTimeOffset.UnixEpoch.AddSeconds(10));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<StreamingDeliveryBatch?> pending = reopened.ReadBatchAsync(cancellation.Token).AsTask();
        Assert.False(pending.IsCompleted);
        FileStreamingSubscriptionStatus paused = await reopened.GetStatusAsync();
        Assert.True(paused.ConsumptionPaused);
        Assert.Equal(2, paused.PendingEventCount);
        Assert.Equal(0, paused.InFlightAttempt);
        await reopened.ResumeConsumptionAsync(paused.StateRevision);
        StreamingDeliveryBatch delivery = Assert.IsType<StreamingDeliveryBatch>(await pending);
        Assert.Equal([1L, 2L], delivery.Events.Select(static value => value.Sequence));
        Assert.Equal(1, delivery.Attempt);
        Assert.False(reopened.ConsumptionPaused);
    }

    [Fact]
    public async Task AcknowledgeAsync_WhilePausedAndCompleted_DrainsInFlightAndWakesTerminalRead()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition();
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition))
        {
            await subscription.PublishAsync(Event(1));
            StreamingDeliveryBatch delivery = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
            await subscription.PauseConsumptionAsync(subscription.StateRevision);
            await subscription.CompleteAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task<StreamingDeliveryBatch?> pending = subscription.ReadBatchAsync(cancellation.Token).AsTask();
            Assert.False(pending.IsCompleted);
            Assert.Equal(1, (await subscription.GetStatusAsync()).InFlightAttempt);
            StreamingDeliveryReceipt receipt = await subscription.AcknowledgeAsync(delivery.DeliveryId);
            Assert.Equal(StreamingDeliveryStatus.Acknowledged, receipt.Status);
            Assert.Null(await pending);
            Assert.True(subscription.ConsumptionPaused);
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        Assert.True(reopened.ConsumptionPaused);
        Assert.Equal(0, reopened.PendingEventCount);
        Assert.Equal(1, reopened.Checkpoint.CommittedSequence);
        Assert.Null(await reopened.ReadBatchAsync());
    }

    [Fact]
    public async Task ReadBatchAsync_WhilePausedWithNoEvents_WaitsUntilPublishingCompletes()
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition());
        await subscription.PauseConsumptionAsync(subscription.StateRevision);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<StreamingDeliveryBatch?> pending = subscription.ReadBatchAsync(cancellation.Token).AsTask();
        Assert.False(pending.IsCompleted);
        await subscription.CompleteAsync();
        Assert.Null(await pending);
        Assert.True(subscription.ConsumptionPaused);
    }

    [Fact]
    public async Task ResetDeliveryAttemptsAsync_AfterExhaustionAndReopening_RedeliversStableBatchWithoutResuming()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 1);
        var options = new FileStreamingSubscriptionOptions { MaxDeliveryAttempts = 1 };
        StreamingDeliveryBatch original;
        long resetRevision;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options))
        {
            await subscription.PublishAsync(Event(1));
            await subscription.PublishAsync(Event(2));
            original = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
            await Assert.ThrowsAsync<FileStreamingDeliveryAttemptLimitException>(
                () => subscription.ReadBatchAsync().AsTask());
            await subscription.PauseConsumptionAsync(subscription.StateRevision);
            FileStreamingSubscriptionStatus status = await subscription.GetStatusAsync();
            Assert.True(status.DeliveryAttemptsExhausted);
            resetRevision = await subscription.ResetDeliveryAttemptsAsync(
                status.InFlightDeliveryId!, status.StateRevision, status.InFlightAttempt);
            Assert.True(subscription.ConsumptionPaused);
            Assert.Equal(2, subscription.PendingEventCount);
            Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options);
        FileStreamingSubscriptionStatus reset = await reopened.GetStatusAsync();
        Assert.Equal(resetRevision, reset.StateRevision);
        Assert.True(reset.ConsumptionPaused);
        Assert.Equal(original.DeliveryId, reset.InFlightDeliveryId);
        Assert.Equal(0, reset.InFlightAttempt);
        Assert.False(reset.DeliveryAttemptsExhausted);
        await reopened.ResumeConsumptionAsync(reset.StateRevision);
        StreamingDeliveryBatch retry = Assert.IsType<StreamingDeliveryBatch>(await reopened.ReadBatchAsync());
        Assert.Equal(original.DeliveryId, retry.DeliveryId);
        Assert.Equal(original.CandidateCheckpoint, retry.CandidateCheckpoint);
        Assert.Equal(original.Events[0].EventId, Assert.Single(retry.Events).EventId);
        Assert.Equal(original.Events[0].Payload, retry.Events[0].Payload);
        Assert.Equal(1, retry.Attempt);
        Assert.Equal(StreamingDeliveryStatus.Redelivered, retry.Status);
        await reopened.AcknowledgeAsync(retry.DeliveryId);
        StreamingDeliveryBatch next = Assert.IsType<StreamingDeliveryBatch>(await reopened.ReadBatchAsync());
        Assert.NotEqual(original.DeliveryId, next.DeliveryId);
        Assert.Equal(2, Assert.Single(next.Events).Sequence);
    }

    [Theory]
    [InlineData("delivery")]
    [InlineData("revision")]
    [InlineData("attempt")]
    public async Task ResetDeliveryAttemptsAsync_WithMismatchedCondition_LeavesDurableStateUnchanged(string mismatch)
    {
        using var directory = new TemporaryDirectory();
        var options = new FileStreamingSubscriptionOptions { MaxDeliveryAttempts = 2 };
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition(), options);
        await subscription.PublishAsync(Event(1));
        await subscription.ReadBatchAsync();
        StreamingDeliveryBatch delivery = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
        FileStreamingSubscriptionStatus before = await subscription.GetStatusAsync();
        byte[] bytesBefore = await File.ReadAllBytesAsync(StatePath(directory));
        await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.ResetDeliveryAttemptsAsync(
            mismatch == "delivery" ? Guid.NewGuid().ToString("N") : delivery.DeliveryId,
            mismatch == "revision" ? before.StateRevision - 1 : before.StateRevision,
            mismatch == "attempt" ? 1 : before.InFlightAttempt).AsTask());
        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(StatePath(directory)));
        FileStreamingSubscriptionStatus after = await subscription.GetStatusAsync();
        Assert.Equal(before.StateRevision, after.StateRevision);
        Assert.Equal(before.InFlightAttempt, after.InFlightAttempt);
        Assert.Equal(before.InFlightDeliveryId, after.InFlightDeliveryId);
        Assert.Equal(before.PendingEventCount, after.PendingEventCount);
        Assert.True(after.DeliveryAttemptsExhausted);
    }

    [Fact]
    public async Task ResetDeliveryAttemptsAsync_BeforeExhaustion_RejectsWithoutChangingState()
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition());
        await subscription.PublishAsync(Event(1));
        StreamingDeliveryBatch delivery = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
        long revision = subscription.StateRevision;
        await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.ResetDeliveryAttemptsAsync(
            delivery.DeliveryId, revision, delivery.Attempt).AsTask());
        Assert.Equal(revision, subscription.StateRevision);
        Assert.Equal(1, (await subscription.GetStatusAsync()).InFlightAttempt);
    }

    [Fact]
    public async Task ResetDeliveryAttemptsAsync_AfterAnotherResetCycle_RejectsOriginalCommandEvenWhenAttemptMatches()
    {
        using var directory = new TemporaryDirectory();
        var options = new FileStreamingSubscriptionOptions { MaxDeliveryAttempts = 1 };
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition(), options);
        await subscription.PublishAsync(Event(1));
        StreamingDeliveryBatch first = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
        long originalRevision = subscription.StateRevision;
        await subscription.ResetDeliveryAttemptsAsync(first.DeliveryId, originalRevision, first.Attempt);
        StreamingDeliveryBatch retry = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
        Assert.Equal(first.Attempt, retry.Attempt);
        Assert.Equal(first.DeliveryId, retry.DeliveryId);
        long currentRevision = subscription.StateRevision;
        await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.ResetDeliveryAttemptsAsync(
            first.DeliveryId, originalRevision, first.Attempt).AsTask());
        Assert.Equal(currentRevision, subscription.StateRevision);
        await subscription.ResetDeliveryAttemptsAsync(retry.DeliveryId, currentRevision, retry.Attempt);
    }

    [Fact]
    public async Task ResumeConsumptionAsync_WithStaleRevision_LeavesSubscriptionPaused()
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition());
        long revision = await subscription.PauseConsumptionAsync(subscription.StateRevision);
        await subscription.PublishAsync(Event(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.ResumeConsumptionAsync(revision).AsTask());
        Assert.True(subscription.ConsumptionPaused);
        await subscription.ResumeConsumptionAsync(subscription.StateRevision);
        Assert.False(subscription.ConsumptionPaused);
    }

    [Fact]
    public async Task ReadBatchAsync_WhilePaused_CancellationPreservesEventsAndAttempts()
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition());
        await subscription.PublishAsync(Event(1));
        StreamingDeliveryBatch delivery = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
        await subscription.PauseConsumptionAsync(subscription.StateRevision);
        long revision = subscription.StateRevision;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<StreamingDeliveryBatch?> pending = subscription.ReadBatchAsync(cancellation.Token).AsTask();
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(revision, subscription.StateRevision);
        Assert.Equal(1, subscription.PendingEventCount);
        Assert.Equal(1, (await subscription.GetStatusAsync()).InFlightAttempt);
        await subscription.ResumeConsumptionAsync(revision);
        Assert.Equal(delivery.DeliveryId, (await subscription.ReadBatchAsync())!.DeliveryId);
    }

    [Fact]
    public async Task DisposeAsync_WhilePaused_WakesReadAndCapacityWaitAndPreservesBacklog()
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 1, capacity: 1);
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition))
        {
            await subscription.PublishAsync(Event(1));
            await subscription.PauseConsumptionAsync(subscription.StateRevision);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task<StreamingDeliveryBatch?> read = subscription.ReadBatchAsync(cancellation.Token).AsTask();
            Task<StreamingPublishResult> publish = subscription.PublishAsync(Event(2), cancellation.Token).AsTask();
            Assert.False(read.IsCompleted);
            Assert.False(publish.IsCompleted);
            await subscription.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => read);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => publish);
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, definition);
        Assert.True(reopened.ConsumptionPaused);
        Assert.Equal(1, reopened.PendingEventCount);
        Assert.Equal(1, reopened.LastAcceptedSequence);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OpenAsync_WithLegacyInFlightState_MigratesVerifiedHashAndPreservesStableBatch(int version)
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition(batchSize: 1);
        var options = new FileStreamingSubscriptionOptions { MaxDeliveryAttempts = version == 1 ? 100 : 2 };
        StreamingDeliveryBatch original;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options))
        {
            await subscription.PublishAsync(Event(1));
            original = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
        }

        await WriteLegacyStateAsync(directory, version);
        await using var migrated = await FileStreamingSubscription.OpenAsync(directory.Path, definition, options);
        Assert.False(migrated.ConsumptionPaused);
        Assert.Equal(0, migrated.StateRevision);
        Assert.Equal(options.MaxDeliveryAttempts, migrated.Options.MaxDeliveryAttempts);
        FileStreamingStateEnvelope rewritten = JsonSerializer.Deserialize(
            await File.ReadAllBytesAsync(StatePath(directory)), FileStreamingJsonContext.Default.FileStreamingStateEnvelope)!;
        Assert.Equal(3, rewritten.State.FormatVersion);
        StreamingDeliveryBatch delivery = Assert.IsType<StreamingDeliveryBatch>(await migrated.ReadBatchAsync());
        Assert.Equal(original.DeliveryId, delivery.DeliveryId);
        Assert.Equal(original.CandidateCheckpoint, delivery.CandidateCheckpoint);
        Assert.Equal(2, delivery.Attempt);
        Assert.Equal(StreamingDeliveryStatus.Redelivered, delivery.Status);
    }

    [Theory]
    [InlineData(1, "hash")]
    [InlineData(2, "hash")]
    [InlineData(1, "field")]
    [InlineData(2, "field")]
    public async Task OpenAsync_WithCorruptLegacyState_FailsClosedWithoutRewriting(int version, string corruption)
    {
        using var directory = new TemporaryDirectory();
        var definition = Definition();
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, definition))
            await subscription.PublishAsync(Event(1));
        await WriteLegacyStateAsync(directory, version);
        string valid = await File.ReadAllTextAsync(StatePath(directory));
        string corrupt = corruption == "hash"
            ? valid.Replace("\"pendingEventCount\":1", "\"pendingEventCount\":2", StringComparison.Ordinal)
            : valid.Replace("\"inFlight\":null", "\"unrecognizedField\":null", StringComparison.Ordinal);
        Assert.NotEqual(valid, corrupt);
        await File.WriteAllTextAsync(StatePath(directory), corrupt);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => FileStreamingSubscription.OpenAsync(directory.Path, definition).AsTask());
        Assert.Equal(corrupt, await File.ReadAllTextAsync(StatePath(directory)));
    }

    private static async Task WriteLegacyStateAsync(TemporaryDirectory directory, int version)
    {
        FileStreamingStateEnvelope current = JsonSerializer.Deserialize(
            await File.ReadAllBytesAsync(StatePath(directory)), FileStreamingJsonContext.Default.FileStreamingStateEnvelope)!;
        FileStreamingSubscriptionState state = current.State;
        FileStreamingSubscriptionOptions options = state.Options;
        byte[] bytes;
        if (version == 1)
        {
            var legacy = new LegacyFileStreamingSubscriptionState(
                1, state.Definition,
                new LegacyFileStreamingSubscriptionOptions(options.MaxEventBytes, options.MaxStoredBytes,
                    options.MaxBatchBytes, options.OperationTimeoutMilliseconds),
                state.Checkpoint, state.WatermarkUtc, state.LastAcceptedSequence,
                state.PendingEventCount, state.PublishingCompleted, state.InFlight);
            byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(legacy,
                FileStreamingJsonContext.Default.LegacyFileStreamingSubscriptionState);
            bytes = JsonSerializer.SerializeToUtf8Bytes(
                new LegacyFileStreamingStateEnvelope(legacy, Convert.ToHexString(SHA256.HashData(stateBytes))),
                FileStreamingJsonContext.Default.LegacyFileStreamingStateEnvelope);
        }
        else
        {
            var legacy = new Version2FileStreamingSubscriptionState(
                2, state.Definition,
                new Version2FileStreamingSubscriptionOptions(options.MaxEventBytes, options.MaxStoredBytes,
                    options.MaxBatchBytes, options.OperationTimeoutMilliseconds, options.MaxDeliveryAttempts),
                state.Checkpoint, state.WatermarkUtc, state.LastAcceptedSequence,
                state.PendingEventCount, state.PublishingCompleted, state.InFlight);
            byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(legacy,
                FileStreamingJsonContext.Default.Version2FileStreamingSubscriptionState);
            bytes = JsonSerializer.SerializeToUtf8Bytes(
                new Version2FileStreamingStateEnvelope(legacy, Convert.ToHexString(SHA256.HashData(stateBytes))),
                FileStreamingJsonContext.Default.Version2FileStreamingStateEnvelope);
        }

        await File.WriteAllBytesAsync(StatePath(directory), bytes);
    }

    private static StreamingSubscriptionDefinition Definition(int batchSize = 2, int capacity = 4)
        => StreamingSubscriptionDefinition.Create("operations-subscription", "orders", batchSize, capacity);

    private static StreamingEvent Event(long sequence)
        => new("event-" + sequence, sequence, DateTimeOffset.UnixEpoch.AddSeconds(sequence), "payload"u8.ToArray());

    private static string StatePath(TemporaryDirectory directory) => Path.Combine(directory.Path, "subscription.json");

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sonnetdb-subscription-operations-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            string absolutePath = System.IO.Path.GetFullPath(Path);
            string temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (!absolutePath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !System.IO.Path.GetFileName(absolutePath).StartsWith("sonnetdb-subscription-operations-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("测试临时目录不属于订阅运维测试。");
            }

            if (Directory.Exists(absolutePath))
                Directory.Delete(absolutePath, recursive: true);
        }
    }
}
