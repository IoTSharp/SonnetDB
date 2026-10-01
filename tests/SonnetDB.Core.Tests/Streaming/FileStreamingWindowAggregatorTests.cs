using System.Text;
using SonnetDB.Streaming;
using Xunit;

namespace SonnetDB.Core.Tests.Streaming;

public sealed class FileStreamingWindowAggregatorTests
{
    [Fact]
    public async Task PumpOnceAsync_WithOutOfOrderEventTimes_CountsFixedUtcWindowsAndAcknowledges()
    {
        using var directory = new TemporaryDirectory();
        var subscriptionDefinition = SubscriptionDefinition(batchSize: 4);
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, subscriptionDefinition);
        await subscription.PublishAsync(Event(1, 12));
        await subscription.PublishAsync(Event(2, 0));
        await subscription.PublishAsync(Event(3, 9.999));
        await subscription.PublishAsync(Event(4, 10));
        await subscription.CompleteAsync();
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition());

        Assert.True(await aggregator.PumpOnceAsync(subscription));
        Assert.False(await aggregator.PumpOnceAsync(subscription));
        StreamingWindowBatch batch = await aggregator.ReadWindowsAsync();
        Assert.Equal([2L, 2L], batch.Windows.Select(static window => window.Count));
        Assert.Equal(Utc(0), batch.Windows[0].StartUtc);
        Assert.Equal(Utc(10), batch.Windows[0].EndUtc);
        Assert.Equal(Utc(10), batch.Windows[1].StartUtc);
        Assert.False(batch.Windows[0].IsClosed);
        Assert.Equal(4, batch.State.AppliedSequence);
        Assert.Equal(1, batch.State.AppliedRevision);
        Assert.Equal(4, subscription.Checkpoint.CommittedSequence);
        Assert.Equal(0, subscription.PendingEventCount);
    }

    [Fact]
    public async Task PumpOnceAsync_AfterWindowCommitBeforeAckAndReopen_DeduplicatesStableDelivery()
    {
        using var directory = new TemporaryDirectory();
        var subscriptionDefinition = SubscriptionDefinition(batchSize: 2);
        StreamingDeliveryBatch delivery;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, subscriptionDefinition))
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition()))
        {
            await subscription.PublishAsync(Event(1, 1));
            await subscription.PublishAsync(Event(2, 2));
            delivery = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
            StreamingWindowState applied = await aggregator.ApplyBatchAsync(delivery);
            Assert.Equal(2, applied.AppliedSequence);
            Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
        }

        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, subscriptionDefinition))
        await using (var aggregator = await FileStreamingWindowAggregator.OpenAsync(directory.StatePath, Definition()))
        {
            FileStreamingSubscriptionStatus status = await subscription.GetStatusAsync();
            Assert.Equal(delivery.DeliveryId, status.InFlightDeliveryId);
            Assert.True(await aggregator.PumpOnceAsync(subscription));
            Assert.Equal(2, Assert.Single((await aggregator.ReadWindowsAsync()).Windows).Count);
            Assert.Equal(2, subscription.Checkpoint.CommittedSequence);
            await subscription.PublishAsync(Event(3, 3));
            Assert.True(await aggregator.PumpOnceAsync(subscription));
            Assert.Equal(3, Assert.Single((await aggregator.ReadWindowsAsync()).Windows).Count);
        }

        await using var reopened = await FileStreamingWindowAggregator.OpenAsync(directory.StatePath, Definition());
        StreamingWindowBatch final = await reopened.ReadWindowsAsync();
        Assert.Equal(3, Assert.Single(final.Windows).Count);
        Assert.Equal(3, final.State.AppliedSequence);
        Assert.Equal(2, final.State.AppliedRevision);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithSameDeliveryAndChangedContent_RejectsReplay()
    {
        using var directory = new TemporaryDirectory();
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition());
        StreamingDeliveryBatch original = Delivery(1, Event(1, 1));
        await aggregator.ApplyBatchAsync(original);
        await Assert.ThrowsAsync<InvalidDataException>(() => aggregator.ApplyBatchAsync(
            original with { Events = [Event(1, 11)] }).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => aggregator.ApplyBatchAsync(
            original with { DeliveryId = "different" }).AsTask());
        Assert.Equal(1, Assert.Single((await aggregator.ReadWindowsAsync()).Windows).Count);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithSameDeliveryAfterWatermarkAndResultRemoval_DoesNotRecountOrDrop()
    {
        using var directory = new TemporaryDirectory();
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition());
        StreamingDeliveryBatch original = Delivery(1, Event(1, 1));
        await aggregator.ApplyBatchAsync(original);
        await aggregator.AdvanceWatermarkAsync(Utc(10));
        await aggregator.ApplyBatchAsync(original with { Attempt = 2, Status = StreamingDeliveryStatus.Redelivered });
        StreamingWindowBatch closed = await aggregator.ReadWindowsAsync();
        Assert.Equal(1, Assert.Single(closed.Windows).Count);
        Assert.Equal(0, closed.State.DroppedLateEvents);
        Assert.Equal(1, await aggregator.RemoveClosedWindowsAsync(Utc(0)));
        await aggregator.ApplyBatchAsync(original with { Attempt = 3, Status = StreamingDeliveryStatus.Redelivered });
        StreamingWindowBatch removed = await aggregator.ReadWindowsAsync();
        Assert.Empty(removed.Windows);
        Assert.Equal(0, removed.State.DroppedLateEvents);
        await aggregator.ApplyBatchAsync(Delivery(2, Event(2, 2)));
        Assert.Empty((await aggregator.ReadWindowsAsync()).Windows);
        Assert.Equal(1, (await aggregator.GetStateAsync()).DroppedLateEvents);
    }

    [Fact]
    public async Task AdvanceWatermarkAsync_WithAllowedLateness_ClosesAtExactBoundaryAndPersistsDrop()
    {
        using var directory = new TemporaryDirectory();
        StreamingWindowDefinition definition = Definition(TimeSpan.FromSeconds(2));
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, definition))
        {
            await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1)));
            await aggregator.AdvanceWatermarkAsync(Utc(11.999));
            Assert.False(Assert.Single((await aggregator.ReadWindowsAsync()).Windows).IsClosed);
            await aggregator.ApplyBatchAsync(Delivery(2, Event(2, 2)));
            await aggregator.AdvanceWatermarkAsync(Utc(12));
            Assert.True(Assert.Single((await aggregator.ReadWindowsAsync()).Windows).IsClosed);
            await aggregator.ApplyBatchAsync(Delivery(3, Event(3, 3), Event(4, 11)));
            StreamingWindowBatch results = await aggregator.ReadWindowsAsync();
            Assert.Equal([2L, 1L], results.Windows.Select(static window => window.Count));
            Assert.Equal(1, results.State.DroppedLateEvents);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => aggregator.AdvanceWatermarkAsync(Utc(11)).AsTask());
        }

        await using var reopened = await FileStreamingWindowAggregator.OpenAsync(directory.StatePath, definition);
        StreamingWindowBatch closed = await reopened.ReadWindowsAsync(closedOnly: true);
        Assert.True(Assert.Single(closed.Windows).IsClosed);
        Assert.Equal(Utc(12), closed.State.WatermarkUtc);
        Assert.Equal(1, closed.State.DroppedLateEvents);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithRejectPolicy_KeepsEntireBatchUnapplied()
    {
        using var directory = new TemporaryDirectory();
        StreamingWindowDefinition definition = Definition(latePolicy: StreamingWindowLateEventPolicy.Reject);
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, definition);
        await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1)));
        await aggregator.AdvanceWatermarkAsync(Utc(10));
        await Assert.ThrowsAsync<StreamingLateEventException>(() => aggregator.ApplyBatchAsync(
            Delivery(2, Event(2, 11), Event(3, 2))).AsTask());
        StreamingWindowBatch results = await aggregator.ReadWindowsAsync();
        Assert.Equal(1, Assert.Single(results.Windows).Count);
        Assert.Equal(1, results.State.AppliedSequence);
        Assert.Equal(1, results.State.AppliedRevision);
        Assert.Equal(0, results.State.DroppedLateEvents);
    }

    [Fact]
    public async Task PumpOnceAsync_WhenWindowCapacityFull_PreservesBatchThenContinuesAfterRemovingClosedWindow()
    {
        using var directory = new TemporaryDirectory();
        var subscriptionDefinition = SubscriptionDefinition(batchSize: 1);
        var options = new StreamingWindowOptions { MaxWindows = 1 };
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, subscriptionDefinition);
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition(), options: options);
        await subscription.PublishAsync(Event(1, 1));
        Assert.True(await aggregator.PumpOnceAsync(subscription));
        await subscription.PublishAsync(Event(2, 11));
        await Assert.ThrowsAsync<InvalidOperationException>(() => aggregator.PumpOnceAsync(subscription).AsTask());
        Assert.Equal(1, subscription.Checkpoint.CommittedSequence);
        Assert.Equal(1, subscription.PendingEventCount);
        Assert.Equal(1, (await aggregator.GetStateAsync()).AppliedSequence);
        Assert.Equal(0, await aggregator.RemoveClosedWindowsAsync(Utc(0)));
        await aggregator.AdvanceWatermarkAsync(Utc(10));
        Assert.Equal(1, await aggregator.RemoveClosedWindowsAsync(Utc(0)));
        Assert.True(await aggregator.PumpOnceAsync(subscription));
        StreamingWindowCount result = Assert.Single((await aggregator.ReadWindowsAsync(maxWindows: 1)).Windows);
        Assert.Equal(Utc(10), result.StartUtc);
        Assert.Equal(1, result.Count);
        Assert.Equal(2, subscription.Checkpoint.CommittedSequence);
    }

    [Fact]
    public async Task ReadWindowsAsync_WithNegativeEpochTimeAndPagination_UsesFloorAndExclusiveCursor()
    {
        using var directory = new TemporaryDirectory();
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition());
        await aggregator.ApplyBatchAsync(Delivery(1, Event(1, -0.001), Event(2, 0), Event(3, 10)));
        StreamingWindowBatch first = await aggregator.ReadWindowsAsync(maxWindows: 1);
        Assert.Equal(Utc(-10), Assert.Single(first.Windows).StartUtc);
        Assert.True(first.HasMore);
        StreamingWindowBatch second = await aggregator.ReadWindowsAsync(maxWindows: 1, afterStartUtc: first.NextStartUtc);
        Assert.Equal(Utc(0), Assert.Single(second.Windows).StartUtc);
        Assert.True(second.HasMore);
        StreamingWindowBatch third = await aggregator.ReadWindowsAsync(maxWindows: 1, afterStartUtc: second.NextStartUtc);
        Assert.Equal(Utc(10), Assert.Single(third.Windows).StartUtc);
        Assert.False(third.HasMore);
    }

    [Fact]
    public async Task PumpOnceAsync_WithCancelledWait_LeavesStateUnchangedAndAllowsNextBatch()
    {
        using var directory = new TemporaryDirectory();
        var subscriptionDefinition = SubscriptionDefinition(batchSize: 1);
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, subscriptionDefinition);
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aggregator.PumpOnceAsync(subscription, cancellation.Token).AsTask());
        Assert.Equal(-1, (await aggregator.GetStateAsync()).AppliedSequence);
        await subscription.PublishAsync(Event(1, 1));
        Assert.True(await aggregator.PumpOnceAsync(subscription));
        Assert.Equal(1, Assert.Single((await aggregator.ReadWindowsAsync()).Windows).Count);
    }

    [Fact]
    public async Task PumpOnceAsync_WithEmptyStream_StopsAtOperationTimeout()
    {
        using var directory = new TemporaryDirectory();
        var subscriptionDefinition = SubscriptionDefinition(batchSize: 1);
        var options = new StreamingWindowOptions { OperationTimeoutMilliseconds = 250 };
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, subscriptionDefinition);
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition(), options: options);
        await Assert.ThrowsAsync<TimeoutException>(() => aggregator.PumpOnceAsync(subscription).AsTask());
        Assert.Equal(-1, (await aggregator.GetStateAsync()).AppliedSequence);
    }

    [Fact]
    public async Task OpenAsync_WithMissingCorruptOrMismatchedState_FailsClosedAndReleasesLease()
    {
        using var directory = new TemporaryDirectory();
        await Assert.ThrowsAsync<FileNotFoundException>(() => FileStreamingWindowAggregator.OpenAsync(
            directory.StatePath, Definition()).AsTask());
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition()))
        {
            await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1)));
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingWindowAggregator.OpenAsync(
            directory.StatePath, Definition(TimeSpan.FromSeconds(1))).AsTask());
        byte[] original = await File.ReadAllBytesAsync(directory.StatePath);
        string changed = Encoding.UTF8.GetString(original).Replace("\"appliedSequence\":1", "\"appliedSequence\":2", StringComparison.Ordinal);
        Assert.NotEqual(Encoding.UTF8.GetString(original), changed);
        await File.WriteAllTextAsync(directory.StatePath, changed);
        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingWindowAggregator.OpenAsync(
            directory.StatePath, Definition()).AsTask());
        await File.WriteAllBytesAsync(directory.StatePath, original);
        await using var reopened = await FileStreamingWindowAggregator.OpenAsync(directory.StatePath, Definition());
        Assert.Equal(1, (await reopened.GetStateAsync()).AppliedSequence);
    }

    [Fact]
    public async Task CreateAsync_WithExistingStateOrSecondWriter_RefusesOverwriteAndConcurrentLease()
    {
        using var directory = new TemporaryDirectory();
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition()))
        {
            await Assert.ThrowsAsync<IOException>(() => FileStreamingWindowAggregator.OpenAsync(
                directory.StatePath, Definition()).AsTask());
        }

        await Assert.ThrowsAsync<IOException>(() => FileStreamingWindowAggregator.CreateAsync(
            directory.StatePath, Definition()).AsTask());
        await using var reopened = await FileStreamingWindowAggregator.OpenAsync(directory.StatePath, Definition());
        Assert.Equal(-1, (await reopened.GetStateAsync()).AppliedSequence);
    }

    [Fact]
    public async Task OpenAsync_WithOwnedInterruptedTemporaryWrite_ReclaimsFixedSideFileAndContinues()
    {
        using var directory = new TemporaryDirectory();
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition()))
        {
            await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1)));
        }

        string pendingPath = directory.StatePath + ".pending";
        await File.WriteAllTextAsync(pendingPath, "{\"state\":");
        Assert.True(File.Exists(directory.StatePath + ".pending.owner"));
        await using var reopened = await FileStreamingWindowAggregator.OpenAsync(directory.StatePath, Definition());
        Assert.False(File.Exists(pendingPath));
        Assert.Equal(1, (await reopened.GetStateAsync()).AppliedSequence);
        await reopened.ApplyBatchAsync(Delivery(2, Event(2, 2)));
        Assert.False(File.Exists(pendingPath));
        Assert.Equal(2, Assert.Single((await reopened.ReadWindowsAsync()).Windows).Count);
    }

    [Fact]
    public async Task OpenAsync_WithUnownedSideFile_RejectsAndPreservesUnknownFile()
    {
        using var directory = new TemporaryDirectory();
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition()))
        {
        }

        File.Delete(directory.StatePath + ".pending.owner");
        string pendingPath = directory.StatePath + ".pending";
        await File.WriteAllTextAsync(pendingPath, "user-file");
        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingWindowAggregator.OpenAsync(
            directory.StatePath, Definition()).AsTask());
        Assert.Equal("user-file", await File.ReadAllTextAsync(pendingPath));
    }

    [Fact]
    public async Task CreateAsync_WithPendingInitialCommit_RejectsAndPreservesSideFile()
    {
        using var directory = new TemporaryDirectory();
        string pendingPath = directory.StatePath + ".pending";
        await File.WriteAllTextAsync(pendingPath, "unknown-initial-commit");
        await Assert.ThrowsAsync<IOException>(() => FileStreamingWindowAggregator.CreateAsync(
            directory.StatePath, Definition()).AsTask());
        Assert.Equal("unknown-initial-commit", await File.ReadAllTextAsync(pendingPath));
        Assert.False(File.Exists(directory.StatePath));
    }

    [Fact]
    public async Task ApplyBatchAsync_WithForeignSubscriptionSkippedRevisionOrOverBudget_RefusesMutation()
    {
        using var directory = new TemporaryDirectory();
        var options = new StreamingWindowOptions { MaxBatchEvents = 1, MaxBatchBytes = 1024 };
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition(), options: options);
        StreamingDeliveryBatch batch = Delivery(1, Event(1, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => aggregator.ApplyBatchAsync(batch with
        {
            CandidateCheckpoint = batch.CandidateCheckpoint with { SubscriptionId = "foreign" },
        }).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => aggregator.ApplyBatchAsync(batch with
        {
            CandidateCheckpoint = batch.CandidateCheckpoint with { Revision = 2 },
        }).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1), Event(2, 2))).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => aggregator.ApplyBatchAsync(
            Delivery(1, Event(1, 1) with { Payload = new byte[1024] })).AsTask());
        Assert.Equal(-1, (await aggregator.GetStateAsync()).AppliedSequence);
        await aggregator.ApplyBatchAsync(batch);
        Assert.Equal(1, (await aggregator.GetStateAsync()).AppliedSequence);
    }

    [Fact]
    public async Task ApplyBatchAsync_WhenStateByteBudgetExceeded_PreservesStateAndAllowsSmallerBatch()
    {
        using var directory = new TemporaryDirectory();
        var options = new StreamingWindowOptions { MaxStateBytes = 1024 };
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition(), options: options);
        StreamingEvent[] events = Enumerable.Range(1, 40).Select(static sequence => Event(sequence, sequence * 10)).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => aggregator.ApplyBatchAsync(Delivery(1, events)).AsTask());
        Assert.Equal(-1, (await aggregator.GetStateAsync()).AppliedSequence);
        Assert.Empty((await aggregator.ReadWindowsAsync()).Windows);
        await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1)));
        Assert.Equal(1, Assert.Single((await aggregator.ReadWindowsAsync()).Windows).Count);
        Assert.True(new FileInfo(directory.StatePath).Length <= options.MaxStateBytes);
    }

    [Fact]
    public async Task PumpOnceAsync_WhenSubscriptionAlreadyAcknowledgedMissingWindows_FailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var subscriptionDefinition = SubscriptionDefinition(batchSize: 1);
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, subscriptionDefinition);
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition());
        await subscription.PublishAsync(Event(1, 1));
        StreamingDeliveryBatch delivery = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
        await subscription.AcknowledgeAsync(delivery.DeliveryId);
        await Assert.ThrowsAsync<InvalidDataException>(() => aggregator.PumpOnceAsync(subscription).AsTask());
        Assert.Equal(-1, (await aggregator.GetStateAsync()).AppliedSequence);
    }

    [Fact]
    public async Task PumpOnceAsync_WithDifferentStreamIdentity_RejectsBeforeReading()
    {
        using var directory = new TemporaryDirectory();
        var subscriptionDefinition = StreamingSubscriptionDefinition.Create("consumer", "different-stream", 1, 10);
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, subscriptionDefinition);
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition());
        await subscription.PublishAsync(Event(1, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => aggregator.PumpOnceAsync(subscription).AsTask());
        Assert.Null((await subscription.GetStatusAsync()).InFlightDeliveryId);
        Assert.Equal(-1, (await aggregator.GetStateAsync()).AppliedSequence);
    }

    [Fact]
    public async Task CreateAsync_WithInitialCheckpoint_ContinuesExistingSubscriptionAtNextRevision()
    {
        using var directory = new TemporaryDirectory();
        var subscriptionDefinition = SubscriptionDefinition(batchSize: 1);
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, subscriptionDefinition);
        await subscription.PublishAsync(Event(1, 1));
        StreamingDeliveryBatch delivery = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
        await subscription.AcknowledgeAsync(delivery.DeliveryId);
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(
            directory.StatePath, Definition(), initialCheckpoint: subscription.Checkpoint);
        await subscription.PublishAsync(Event(2, 2));
        Assert.True(await aggregator.PumpOnceAsync(subscription));
        Assert.Equal(1, Assert.Single((await aggregator.ReadWindowsAsync()).Windows).Count);
        Assert.Equal(2, (await aggregator.GetStateAsync()).AppliedRevision);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithUnrepresentableWindow_RejectsUtcBoundaryWithoutMutation()
    {
        using var directory = new TemporaryDirectory();
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition());
        StreamingEvent value = Event(1, 1) with { EventTimeUtc = DateTimeOffset.MaxValue };
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => aggregator.ApplyBatchAsync(Delivery(1, value)).AsTask());
        Assert.Equal(-1, (await aggregator.GetStateAsync()).AppliedSequence);
        Assert.Throws<ArgumentOutOfRangeException>(() => StreamingWindowDefinition.Create("consumer", "orders", TimeSpan.FromTicks(1)));
    }

    private static StreamingSubscriptionDefinition SubscriptionDefinition(int batchSize)
    {
        return StreamingSubscriptionDefinition.Create("consumer", "orders", batchSize, capacity: 10);
    }

    private static StreamingWindowDefinition Definition(
        TimeSpan? lateness = null,
        StreamingWindowLateEventPolicy latePolicy = StreamingWindowLateEventPolicy.Drop)
    {
        return StreamingWindowDefinition.Create("consumer", "orders", TimeSpan.FromSeconds(10), lateness, latePolicy);
    }

    private static StreamingEvent Event(long sequence, double seconds)
    {
        return new StreamingEvent($"event-{sequence}", sequence, Utc(seconds), "payload"u8.ToArray());
    }

    private static DateTimeOffset Utc(double seconds)
    {
        return DateTimeOffset.UnixEpoch.AddSeconds(seconds);
    }

    private static StreamingDeliveryBatch Delivery(long revision, params StreamingEvent[] events)
    {
        return new StreamingDeliveryBatch($"delivery-{revision}", 1, StreamingDeliveryStatus.InFlight, events,
            new StreamingSubscriptionCheckpoint(1, "consumer", events[^1].Sequence, DateTimeOffset.MinValue, revision));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SonnetDB-WindowTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string SubscriptionPath => System.IO.Path.Combine(Path, "subscription");

        public string StatePath => System.IO.Path.Combine(Path, "windows.json");

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
