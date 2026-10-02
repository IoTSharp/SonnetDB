using System.Security.Cryptography;
using System.Text;
using SonnetDB.Streaming;
using Xunit;

namespace SonnetDB.Core.Tests.Streaming;

public sealed class FileStreamingSlidingWindowTests
{
    [Fact]
    public async Task ApplyBatchAsync_WithNonIntegralSizeSlideRatioAndNegativeEpoch_PreservesEveryHalfOpenMember()
    {
        using var directory = new TemporaryDirectory();
        StreamingWindowDefinition definition = StreamingWindowDefinition.CreateSliding(
            "consumer", "orders", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3));
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, definition);
        await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 9), Event(2, 10), Event(3, -0.001), Event(4, 12)));

        StreamingWindowBatch page = await aggregator.ReadWindowsAsync();
        Assert.Equal([Utc(-9), Utc(-6), Utc(-3), Utc(0), Utc(3), Utc(6), Utc(9), Utc(12)],
            page.Windows.Select(static window => window.StartUtc));
        Assert.Equal([1L, 1L, 1L, 1L, 3L, 3L, 3L, 1L], page.Windows.Select(static window => window.Count));
        Assert.All(page.Windows, static window => Assert.Equal(TimeSpan.FromSeconds(10), window.EndUtc - window.StartUtc));
        Assert.Equal(4, page.State.AppliedSequence);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithSubMillisecondEventAtExclusiveEnd_ExcludesOnlyTheEndedMember()
    {
        using var directory = new TemporaryDirectory();
        StreamingWindowDefinition definition = StreamingWindowDefinition.CreateSliding(
            "consumer", "orders", TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(1));
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, definition);
        await aggregator.ApplyBatchAsync(Delivery(1,
            new StreamingEvent("before-end", 1, DateTimeOffset.UnixEpoch.AddTicks(19_999), []),
            new StreamingEvent("at-end", 2, DateTimeOffset.UnixEpoch.AddTicks(20_000), [])));

        StreamingWindowBatch results = await aggregator.ReadWindowsAsync();
        Assert.Equal([DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMilliseconds(1), DateTimeOffset.UnixEpoch.AddMilliseconds(2)],
            results.Windows.Select(static window => window.StartUtc));
        Assert.Equal([1L, 2L, 1L], results.Windows.Select(static window => window.Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PumpOnceAsync_AfterSlidingNumericCommitBeforeAckAndReopen_DeduplicatesAllMembers(bool grouped)
    {
        using var directory = new TemporaryDirectory();
        StreamingWindowDefinition definition = grouped
            ? StreamingWindowDefinition.CreateSlidingGroupedNumeric("consumer", "orders", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), "Group", "Value")
            : NumericDefinition();
        StreamingDeliveryBatch original;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, SubscriptionDefinition(3)))
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, definition))
        {
            await subscription.PublishAsync(Event(1, 1, "{\"Group\":\"A\",\"Value\":0.1}"));
            await subscription.PublishAsync(Event(2, 6, "{\"Group\":\"A\",\"Value\":2e-1}"));
            await subscription.PublishAsync(Event(3, 2, "{\"Group\":\"A\",\"Value\":-0.3}"));
            original = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
            await aggregator.ApplyBatchAsync(original);
            Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
        }

        await using var restoredSubscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, SubscriptionDefinition(3));
        await using var restored = await FileStreamingWindowAggregator.OpenAsync(directory.StatePath, definition);
        Assert.True(await restored.PumpOnceAsync(restoredSubscription));
        if (grouped)
        {
            StreamingGroupedWindowBatch results = await restored.ReadGroupedWindowsAsync();
            Assert.Equal([Utc(-5), Utc(0), Utc(5)], results.Windows.Select(static window => window.StartUtc));
            Assert.Equal([2L, 3L, 1L], results.Windows.Select(static window => window.Count));
            Assert.Equal(new decimal?[] { -0.2m, 0m, 0.2m }, results.Windows.Select(static window => window.Sum));
            Assert.All(results.Windows, static window => Assert.Equal("A", window.GroupKey));
            Assert.Equal(-0.3m, results.Windows[1].Min);
            Assert.Equal(0.2m, results.Windows[1].Max);
            Assert.Equal(0m, results.Windows[1].Average);
        }
        else
        {
            StreamingWindowNumericBatch results = await restored.ReadNumericWindowsAsync();
            Assert.Equal([Utc(-5), Utc(0), Utc(5)], results.Windows.Select(static window => window.StartUtc));
            Assert.Equal([2L, 3L, 1L], results.Windows.Select(static window => window.Count));
            Assert.Equal([-0.2m, 0m, 0.2m], results.Windows.Select(static window => window.Sum));
            Assert.Equal(-0.3m, results.Windows[1].Min);
            Assert.Equal(0.2m, results.Windows[1].Max);
            Assert.Equal(0m, results.Windows[1].Average);
            Assert.Equal([2L, 3L, 1L], (await restored.ReadWindowsAsync()).Windows.Select(static window => window.Count));
        }

        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        Assert.Equal(3, restoredSubscription.Checkpoint.CommittedSequence);
        await restored.ApplyBatchAsync(original with { Attempt = 3, Status = StreamingDeliveryStatus.Redelivered });
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
        await Assert.ThrowsAsync<InvalidDataException>(() => restored.ApplyBatchAsync(original with
        {
            Events = [Event(1, 1, "{\"Group\":\"A\",\"Value\":0.2}"), original.Events[1], original.Events[2]],
        }).AsTask());
        await restoredSubscription.PublishAsync(Event(4, 11, "{\"Group\":\"A\",\"Value\":0.4}"));
        Assert.True(await restored.PumpOnceAsync(restoredSubscription));
        Assert.Equal(4, (await restored.GetStateAsync()).AppliedSequence);
        Assert.Equal(4, (await restored.GetStateAsync()).RetainedWindowCount);
        await restoredSubscription.CompleteAsync();
        Assert.False(await restored.PumpOnceAsync(restoredSubscription));
    }

    [Fact]
    public async Task ApplyBatchAsync_WithPartialLateDrop_PreservesOpenMembersAndCountsOnlyEntirelyDroppedEvents()
    {
        using var directory = new TemporaryDirectory();
        StreamingWindowDefinition definition = StreamingWindowDefinition.CreateSlidingNumeric(
            "consumer", "orders", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), "Value", TimeSpan.FromSeconds(2));
        StreamingDeliveryBatch dropped;
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, definition))
        {
            await aggregator.AdvanceWatermarkAsync(Utc(6.999));
            await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1, "{\"Value\":0.1}")));
            Assert.Equal(2, (await aggregator.GetStateAsync()).RetainedWindowCount);
            await aggregator.AdvanceWatermarkAsync(Utc(7));
            await aggregator.ApplyBatchAsync(Delivery(2, Event(2, 1, "{\"Value\":0.2}")));
            StreamingWindowNumericBatch partial = await aggregator.ReadNumericWindowsAsync();
            Assert.Equal([1L, 2L], partial.Windows.Select(static window => window.Count));
            Assert.Equal([0.1m, 0.3m], partial.Windows.Select(static window => window.Sum));
            Assert.True(partial.Windows[0].IsClosed);
            Assert.False(partial.Windows[1].IsClosed);
            Assert.Equal(0, partial.State.DroppedLateEvents);
            Assert.Equal(1, await aggregator.RemoveClosedWindowsAsync(Utc(-5)));
            await aggregator.AdvanceWatermarkAsync(Utc(12));
            Assert.Equal(1, await aggregator.RemoveClosedWindowsAsync(Utc(0)));
            dropped = Delivery(3, Event(3, 1, "invalid JSON"));
            await aggregator.ApplyBatchAsync(dropped);
            Assert.Empty((await aggregator.ReadNumericWindowsAsync()).Windows);
            Assert.Equal(1, (await aggregator.GetStateAsync()).DroppedLateEvents);
        }

        await using var restored = await FileStreamingWindowAggregator.OpenAsync(directory.StatePath, definition);
        await restored.ApplyBatchAsync(dropped with { Attempt = 2, Status = StreamingDeliveryStatus.Redelivered });
        Assert.Equal(1, (await restored.GetStateAsync()).DroppedLateEvents);
        Assert.Empty((await restored.ReadNumericWindowsAsync()).Windows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PumpOnceAsync_WithAnyClosedMemberAndReject_RejectsWholeBatchWithoutAck(bool grouped)
    {
        using var directory = new TemporaryDirectory();
        StreamingWindowDefinition definition = grouped
            ? StreamingWindowDefinition.CreateSlidingGrouped("consumer", "orders", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), "Group",
                lateEventPolicy: StreamingWindowLateEventPolicy.Reject)
            : Definition(StreamingWindowLateEventPolicy.Reject);
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, SubscriptionDefinition(2));
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, definition);
        await aggregator.AdvanceWatermarkAsync(Utc(5));
        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        await subscription.PublishAsync(Event(1, 11, "{\"Group\":\"A\"}"));
        await subscription.PublishAsync(Event(2, 1, "{\"Group\":\"B\"}"));
        await Assert.ThrowsAsync<StreamingLateEventException>(() => aggregator.PumpOnceAsync(subscription).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
        Assert.Equal(-1, (await aggregator.GetStateAsync()).AppliedSequence);
        Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
        Assert.Equal(2, subscription.PendingEventCount);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithOverlappingCapacityLimit_ReclaimsClosedMembersAndRetriesOriginalBatch()
    {
        using var directory = new TemporaryDirectory();
        var options = new StreamingWindowOptions { MaxWindows = 2 };
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition(), options: options);
        await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1)));
        StreamingDeliveryBatch next = Delivery(2, Event(2, 11));
        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => aggregator.ApplyBatchAsync(next).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
        await aggregator.AdvanceWatermarkAsync(Utc(10));
        Assert.Equal(2, await aggregator.RemoveClosedWindowsAsync(Utc(0)));
        await aggregator.ApplyBatchAsync(next);
        StreamingWindowBatch first = await aggregator.ReadWindowsAsync(maxWindows: 1);
        Assert.Equal(Utc(5), Assert.Single(first.Windows).StartUtc);
        Assert.True(first.HasMore);
        StreamingWindowBatch second = await aggregator.ReadWindowsAsync(maxWindows: 1, afterStartUtc: first.NextStartUtc);
        Assert.Equal(Utc(10), Assert.Single(second.Windows).StartUtc);
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithSlidingStringGroups_PagesEveryOrdinalKeyAndReclaimsDistinctKeyCapacity()
    {
        using var directory = new TemporaryDirectory();
        StreamingWindowDefinition definition = StreamingWindowDefinition.CreateSlidingGrouped(
            "consumer", "orders", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), "Group");
        var options = new StreamingWindowOptions { MaxGroups = 2, MaxWindows = 4 };
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, definition, options: options);
        await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1, "{\"Group\":\"A\"}"), Event(2, 1, "{\"Group\":\"a\"}")));
        StreamingGroupedWindowBatch first = await aggregator.ReadGroupedWindowsAsync(maxWindows: 3);
        Assert.Equal([Utc(-5), Utc(-5), Utc(0)], first.Windows.Select(static window => window.StartUtc));
        Assert.Equal(["A", "a", "A"], first.Windows.Select(static window => window.GroupKey));
        Assert.True(first.HasMore);
        StreamingGroupedWindowBatch last = await aggregator.ReadGroupedWindowsAsync(maxWindows: 3, after: first.NextCursor);
        Assert.Equal("a", Assert.Single(last.Windows).GroupKey);
        Assert.Equal(Utc(0), last.Windows[0].StartUtc);
        Assert.False(last.HasMore);
        await aggregator.AdvanceWatermarkAsync(Utc(5));
        Assert.Equal(2, await aggregator.RemoveClosedWindowsAsync(Utc(-5)));
        StreamingDeliveryBatch next = Delivery(2, Event(3, 6, "{\"Group\":\"B\"}"));
        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => aggregator.ApplyBatchAsync(next).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
        await aggregator.AdvanceWatermarkAsync(Utc(10));
        Assert.Equal(2, await aggregator.RemoveClosedWindowsAsync(Utc(0)));
        await aggregator.ApplyBatchAsync(next);
        StreamingGroupedWindow only = Assert.Single((await aggregator.ReadGroupedWindowsAsync(maxWindows: 4)).Windows);
        Assert.Equal("B", only.GroupKey);
        Assert.Equal(Utc(5), only.StartUtc);
        Assert.Equal(0, (await aggregator.GetStateAsync()).DroppedLateEvents);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithDecimalOverflowInOneMember_RejectsAllOverlappingUpdates()
    {
        using var directory = new TemporaryDirectory();
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, NumericDefinition());
        await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1, "{\"Value\":0.1}")));
        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        await Assert.ThrowsAsync<OverflowException>(() => aggregator.ApplyBatchAsync(
            Delivery(2, Event(2, 6, "{\"Value\":79228162514264337593543950335}"))).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
        Assert.Equal([0.1m, 0.1m], (await aggregator.ReadNumericWindowsAsync()).Windows.Select(static window => window.Sum));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyBatchAsync_WithUnrepresentableFullMember_RejectsBeforePersistence(bool maximum)
    {
        using var directory = new TemporaryDirectory();
        StreamingWindowDefinition definition = StreamingWindowDefinition.CreateSliding(
            "consumer", "orders", TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(1));
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, definition);
        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        DateTimeOffset time = maximum ? DateTimeOffset.MaxValue : DateTimeOffset.MinValue;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => aggregator.ApplyBatchAsync(
            Delivery(1, new StreamingEvent("extreme", 1, time, []))).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
        await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 0)));
        Assert.Equal(2, (await aggregator.GetStateAsync()).RetainedWindowCount);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithMaximumAllowedOverlap_RetainsExactly128MembersAndDeduplicates()
    {
        using var directory = new TemporaryDirectory();
        StreamingWindowDefinition definition = StreamingWindowDefinition.CreateSliding(
            "consumer", "orders", TimeSpan.FromMilliseconds(128), TimeSpan.FromMilliseconds(1));
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, definition);
        StreamingDeliveryBatch delivery = Delivery(1, Event(1, 0));
        await aggregator.ApplyBatchAsync(delivery);
        StreamingWindowBatch results = await aggregator.ReadWindowsAsync(maxWindows: 128);
        Assert.Equal(128, results.Windows.Count);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMilliseconds(-127), results.Windows[0].StartUtc);
        Assert.Equal(DateTimeOffset.UnixEpoch, results.Windows[^1].StartUtc);
        Assert.All(results.Windows, static window => Assert.Equal(1, window.Count));
        await aggregator.ApplyBatchAsync(delivery with { Attempt = 2, Status = StreamingDeliveryStatus.Redelivered });
        Assert.All((await aggregator.ReadWindowsAsync(maxWindows: 128)).Windows, static window => Assert.Equal(1, window.Count));
    }

    [Fact]
    public async Task ApplyBatchAsync_WithCancelledSlidingBatch_LeavesAllMembersAndPositionUnchanged()
    {
        using var directory = new TemporaryDirectory();
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aggregator.ApplyBatchAsync(
            Delivery(1, Event(1, 1)), cancellation.Token).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
        Assert.Equal(-1, (await aggregator.GetStateAsync()).AppliedSequence);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithStateBytesInsufficientForOverlap_RejectsAtomicallyAndRetainsUsableInstance()
    {
        using var directory = new TemporaryDirectory();
        StreamingWindowDefinition definition = StreamingWindowDefinition.CreateSliding(
            "consumer", "orders", TimeSpan.FromMilliseconds(128), TimeSpan.FromMilliseconds(1));
        var options = new StreamingWindowOptions { MaxWindows = 128, MaxStateBytes = 1024 };
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, definition, options: options);
        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        StreamingDeliveryBatch delivery = Delivery(1, Event(1, 0));
        await Assert.ThrowsAsync<InvalidDataException>(() => aggregator.ApplyBatchAsync(delivery).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
        await aggregator.AdvanceWatermarkAsync(DateTimeOffset.UnixEpoch.AddMilliseconds(127));
        await aggregator.ApplyBatchAsync(delivery);
        Assert.Equal(DateTimeOffset.UnixEpoch, Assert.Single((await aggregator.ReadWindowsAsync()).Windows).StartUtc);
        Assert.Equal(0, (await aggregator.GetStateAsync()).DroppedLateEvents);
    }

    [Fact]
    public async Task OpenAsync_WithDifferentSlideDefinition_RejectsInsteadOfMigratingExistingWindows()
    {
        using var directory = new TemporaryDirectory();
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition()))
        {
            await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1)));
        }

        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        StreamingWindowDefinition different = StreamingWindowDefinition.CreateSliding(
            "consumer", "orders", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingWindowAggregator.OpenAsync(directory.StatePath, different).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingWindowAggregator.OpenAsync(directory.StatePath,
            StreamingWindowDefinition.Create("consumer", "orders", TimeSpan.FromSeconds(10))).AsTask());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 2)]
    [InlineData(129, 1)]
    [InlineData(257, 2)]
    [InlineData(86_400_001, 86_400_001)]
    public void CreateSliding_WithInvalidSizeSlideOrOverlap_RejectsExplicitBounds(int size, int slide)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StreamingWindowDefinition.CreateSliding(
            "consumer", "orders", TimeSpan.FromMilliseconds(size), TimeSpan.FromMilliseconds(slide)));
    }

    [Fact]
    public void CreateSliding_WithFractionalMillisecondsOrWrongVersion_RejectsInvalidDefinition()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StreamingWindowDefinition.CreateSliding(
            "consumer", "orders", TimeSpan.FromTicks(10_001), TimeSpan.FromMilliseconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => StreamingWindowDefinition.CreateSliding(
            "consumer", "orders", TimeSpan.FromMilliseconds(2), TimeSpan.FromTicks(10_001)));
        Assert.Throws<InvalidDataException>(() => (Definition() with { SlideMilliseconds = null }).Validate());
        Assert.Throws<InvalidDataException>(() => (Definition() with { FormatVersion = 1 }).Validate());
        Assert.Throws<ArgumentException>(() => StreamingWindowDefinition.CreateSlidingGroupedNumeric(
            "consumer", "orders", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), "Value", "Value"));
    }

    [Theory]
    [InlineData("slideMilliseconds", "5000")]
    [InlineData("allowedLatenessMilliseconds", "0")]
    [InlineData("lateEventPolicy", "0")]
    [InlineData("maxWindows", "10000")]
    [InlineData("operationTimeoutMilliseconds", "10000")]
    public async Task OpenAsync_WithOmittedRequiredFieldAndOriginalHash_RejectsDefaultReconstruction(string field, string value)
    {
        using var directory = new TemporaryDirectory();
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition()))
        {
            await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1)));
        }

        string original = await File.ReadAllTextAsync(directory.StatePath);
        string marker = "\"" + field + "\":" + value;
        string modified = original.Contains(marker + ",", StringComparison.Ordinal)
            ? original.Replace(marker + ",", "", StringComparison.Ordinal)
            : original.Replace("," + marker, "", StringComparison.Ordinal);
        Assert.NotEqual(original, modified);
        await File.WriteAllTextAsync(directory.StatePath, modified);
        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingWindowAggregator.OpenAsync(directory.StatePath, Definition()).AsTask());
        Assert.Equal(modified, await File.ReadAllTextAsync(directory.StatePath));
    }

    [Theory]
    [InlineData("\"slideMilliseconds\":5000", "\"slideMilliseconds\":5000,\"slideMilliseconds\":5000")]
    [InlineData("\"slideMilliseconds\":5000", "\"slideMilliseconds\":5000,\"slide\\u004dilliseconds\":5000")]
    [InlineData("\"slideMilliseconds\":5000", "\"slideMilliseconds\":null")]
    [InlineData("\"slideMilliseconds\":5000", "\"slideMilliseconds\":1000")]
    [InlineData("\"windowSizeMilliseconds\":10000", "\"windowSizeMilliseconds\":10000,\"groupField\":null")]
    public async Task OpenAsync_WithDuplicateInvalidOrMismatchedSlide_RejectsInsteadOfResetting(string original, string modified)
    {
        using var directory = new TemporaryDirectory();
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition()))
        {
            await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1)));
        }

        string state = await File.ReadAllTextAsync(directory.StatePath);
        string corrupt = state.Replace(original, modified, StringComparison.Ordinal);
        Assert.NotEqual(state, corrupt);
        await File.WriteAllTextAsync(directory.StatePath, corrupt);
        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingWindowAggregator.OpenAsync(directory.StatePath, Definition()).AsTask());
        Assert.Equal(corrupt, await File.ReadAllTextAsync(directory.StatePath));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OpenAsync_WithOriginalLegacyHash_PreservesTumblingStateAndRejectsInjectedSlide(int version)
    {
        using var directory = new TemporaryDirectory();
        string state = OriginalState(version);
        await File.WriteAllTextAsync(directory.StatePath, Envelope(state));
        StreamingWindowDefinition definition = version switch
        {
            1 => StreamingWindowDefinition.Create("consumer", "orders", TimeSpan.FromSeconds(10)),
            2 => StreamingWindowDefinition.CreateNumeric("consumer", "orders", TimeSpan.FromSeconds(10), "Value"),
            _ => StreamingWindowDefinition.CreateGrouped("consumer", "orders", TimeSpan.FromSeconds(10), "Group"),
        };
        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        await using (var aggregator = await FileStreamingWindowAggregator.OpenAsync(directory.StatePath, definition))
        {
            Assert.Null(aggregator.Definition.SlideMilliseconds);
            Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
            await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1, "{\"Group\":\"A\",\"Value\":0.1}")));
            Assert.Equal(1, (await aggregator.GetStateAsync()).RetainedWindowCount);
        }

        string persisted = await File.ReadAllTextAsync(directory.StatePath);
        Assert.DoesNotContain("slideMilliseconds", persisted, StringComparison.Ordinal);
        string injected = persisted.Replace("\"windowSizeMilliseconds\":10000", "\"windowSizeMilliseconds\":10000,\"slideMilliseconds\":null", StringComparison.Ordinal);
        await File.WriteAllTextAsync(directory.StatePath, injected);
        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingWindowAggregator.OpenAsync(directory.StatePath, definition).AsTask());
    }

    private static StreamingWindowDefinition Definition(StreamingWindowLateEventPolicy policy = StreamingWindowLateEventPolicy.Drop)
        => StreamingWindowDefinition.CreateSliding("consumer", "orders", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), lateEventPolicy: policy);

    private static StreamingWindowDefinition NumericDefinition()
        => StreamingWindowDefinition.CreateSlidingNumeric("consumer", "orders", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), "Value");

    private static StreamingSubscriptionDefinition SubscriptionDefinition(int batchSize)
        => StreamingSubscriptionDefinition.Create("consumer", "orders", batchSize, capacity: 10);

    private static DateTimeOffset Utc(double seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);

    private static StreamingEvent Event(long sequence, double seconds, string payload = "{}")
        => new($"event-{sequence}", sequence, Utc(seconds), Encoding.UTF8.GetBytes(payload));

    private static StreamingDeliveryBatch Delivery(long revision, params StreamingEvent[] events)
        => new($"delivery-{revision}", 1, StreamingDeliveryStatus.InFlight, events,
            new StreamingSubscriptionCheckpoint(1, "consumer", events[^1].Sequence, DateTimeOffset.MinValue, revision));

    private static string OriginalState(int version)
    {
        string selectedField = version switch { 2 => ",\"numericField\":\"Value\"", 3 => ",\"groupField\":\"Group\"", _ => "" };
        string groups = version == 3 ? ",\"maxGroups\":1000" : "";
        string results = version switch { 2 => ",\"numericWindows\":{}", 3 => ",\"groupedWindows\":[]", _ => "" };
        return "{\"formatVersion\":" + version + ",\"definition\":{\"formatVersion\":" + version
            + ",\"subscriptionId\":\"consumer\",\"streamName\":\"orders\",\"windowSizeMilliseconds\":10000,\"allowedLatenessMilliseconds\":0,\"lateEventPolicy\":0"
            + selectedField + "},\"options\":{\"maxWindows\":10000,\"maxBatchEvents\":1000,\"maxBatchBytes\":4194304,\"maxStateBytes\":4194304,\"operationTimeoutMilliseconds\":10000"
            + groups + "},\"appliedSequence\":-1,\"appliedRevision\":0,\"watermarkUtc\":\"0001-01-01T00:00:00+00:00\",\"droppedLateEvents\":0,\"lastDeliveryId\":null,\"lastBatchHash\":null,\"windows\":{}"
            + results + "}";
    }

    private static string Envelope(string state)
        => "{\"state\":" + state + ",\"sha256\":\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state))) + "\"}";

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _ownedPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SdbSw-" + Guid.NewGuid().ToString("N")));

        public TemporaryDirectory() => Directory.CreateDirectory(_ownedPath);

        public string SubscriptionPath => Path.Combine(_ownedPath, "subscription");

        public string StatePath => Path.Combine(_ownedPath, "windows.json");

        public void Dispose()
        {
            string resolved = Path.GetFullPath(_ownedPath);
            if (resolved != _ownedPath || !Path.GetFileName(resolved).StartsWith("SdbSw-", StringComparison.Ordinal)
                || Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())))
                throw new InvalidOperationException("测试目录归属验证失败，保留目录。");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
