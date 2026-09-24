using System.Text.Json;
using System.Text;
using SonnetDB.Streaming;
using Xunit;

namespace SonnetDB.Core.Tests.Streaming;

public sealed class StreamingSubscriptionTests
{
    [Fact]
    public async Task ReadBatch_WithoutAcknowledge_RedeliversSameBatch()
    {
        var definition = StreamingSubscriptionDefinition.Create("sub-1", "orders", batchSize: 2, capacity: 4);
        await using var subscription = new InMemoryStreamingSubscription(definition);
        await subscription.PublishAsync(Event("a", 0));
        await subscription.PublishAsync(Event("b", 1));

        StreamingDeliveryBatch? first = await subscription.ReadBatchAsync();
        StreamingDeliveryBatch? second = await subscription.ReadBatchAsync();
        Assert.NotNull(first);
        Assert.NotNull(second);

        Assert.Equal(first.DeliveryId, second.DeliveryId);
        Assert.Equal(2, second.Attempt);
        Assert.Equal(StreamingDeliveryStatus.Redelivered, second.Status);
        Assert.Equal(first.Events.Select(static item => item.EventId), second.Events.Select(static item => item.EventId));
    }

    [Fact]
    public async Task Acknowledge_AdvancesVersionedCheckpoint_AndAllowsNextBatch()
    {
        var definition = StreamingSubscriptionDefinition.Create("sub-2", "orders", batchSize: 1, capacity: 2);
        await using var subscription = new InMemoryStreamingSubscription(definition);
        await subscription.PublishAsync(Event("a", 4));

        StreamingDeliveryBatch? batch = await subscription.ReadBatchAsync();
        Assert.NotNull(batch);
        StreamingDeliveryReceipt receipt = await subscription.AcknowledgeAsync(batch.DeliveryId);

        Assert.Equal(StreamingDeliveryStatus.Acknowledged, receipt.Status);
        Assert.Equal(4, receipt.Checkpoint.CommittedSequence);
        Assert.Equal(1, receipt.Checkpoint.Revision);
        Assert.Equal(receipt.Checkpoint, subscription.Checkpoint);
        await subscription.DisposeAsync();
        Assert.Null(await subscription.ReadBatchAsync());
    }

    [Fact]
    public async Task LatePolicy_DropAndRejectRespectWatermark()
    {
        var drop = StreamingSubscriptionDefinition.Create(
            "sub-drop", "events", allowedLateness: TimeSpan.FromSeconds(1), lateEventPolicy: StreamingLateEventPolicy.Drop);
        await using var dropped = new InMemoryStreamingSubscription(drop);
        await dropped.AdvanceWatermarkAsync(Utc(10));
        StreamingPublishResult result = await dropped.PublishAsync(Event("late", 0, Utc(8)));
        Assert.Equal(StreamingPublishDisposition.DroppedLate, result.Disposition);
        Assert.Equal(0, dropped.BufferedCount);

        var reject = StreamingSubscriptionDefinition.Create(
            "sub-reject", "events", allowedLateness: TimeSpan.FromSeconds(1), lateEventPolicy: StreamingLateEventPolicy.Reject);
        await using var rejected = new InMemoryStreamingSubscription(reject);
        await rejected.AdvanceWatermarkAsync(Utc(10));
        await Assert.ThrowsAsync<StreamingLateEventException>(async () => await rejected.PublishAsync(Event("late", 0, Utc(8))));
    }

    [Fact]
    public async Task LatePolicy_DeliverMarksEvent_AndWatermarkCannotMoveBack()
    {
        var definition = StreamingSubscriptionDefinition.Create(
            "sub-deliver", "events", allowedLateness: TimeSpan.FromSeconds(1));
        await using var subscription = new InMemoryStreamingSubscription(definition);
        await subscription.AdvanceWatermarkAsync(Utc(10));
        await subscription.PublishAsync(Event("late", 0, Utc(8)));

        StreamingDeliveryBatch? batch = await subscription.ReadBatchAsync();
        Assert.NotNull(batch);
        Assert.True(Assert.Single(batch.Events).IsLate);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await subscription.AdvanceWatermarkAsync(Utc(9)));
    }

    [Fact]
    public void Definition_RejectsSubMillisecondLatenessInsteadOfTruncating()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StreamingSubscriptionDefinition.Create(
            "sub-precision",
            "events",
            allowedLateness: TimeSpan.FromTicks(1)));
    }

    [Fact]
    public async Task ReadBatch_WhenClosedBeforeRead_ReturnsNull()
    {
        var definition = StreamingSubscriptionDefinition.Create("sub-closed", "events");
        await using var subscription = new InMemoryStreamingSubscription(definition);

        await subscription.DisposeAsync();

        Assert.Null(await subscription.ReadBatchAsync());
    }

    [Fact]
    public async Task ReadBatch_WhenOpenAndEmpty_CancellationStopsWait()
    {
        var definition = StreamingSubscriptionDefinition.Create("sub-read-cancel", "events");
        await using var subscription = new InMemoryStreamingSubscription(definition);
        using var cancellation = new CancellationTokenSource();

        Task<StreamingDeliveryBatch?> pending = subscription.ReadBatchAsync(cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Publish_WhenBoundedBufferIsFull_CancellationStopsBackpressureWait()
    {
        var definition = StreamingSubscriptionDefinition.Create("sub-bounded", "events", batchSize: 1, capacity: 1);
        await using var subscription = new InMemoryStreamingSubscription(definition);
        await subscription.PublishAsync(Event("first", 0));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await subscription.PublishAsync(Event("second", 1), cancellation.Token).AsTask());
        Assert.Equal(1, subscription.BufferedCount);
    }

    [Fact]
    public void VersionedDtos_RoundTripWithSourceGeneratedJson()
    {
        var definition = StreamingSubscriptionDefinition.Create(
            "sub-json", "events", allowedLateness: TimeSpan.FromSeconds(3), lateEventPolicy: StreamingLateEventPolicy.Drop);
        var checkpoint = StreamingSubscriptionCheckpoint.Create(definition.SubscriptionId) with
        {
            CommittedSequence = 7,
            WatermarkUtc = Utc(20),
            Revision = 3,
        };

        byte[] definitionJson = StreamingSubscriptionJson.Serialize(definition);
        byte[] checkpointJson = StreamingSubscriptionJson.Serialize(checkpoint);
        StreamingSubscriptionDefinition restoredDefinition = StreamingSubscriptionJson.DeserializeDefinition(definitionJson);
        StreamingSubscriptionCheckpoint restoredCheckpoint = StreamingSubscriptionJson.DeserializeCheckpoint(checkpointJson);

        Assert.Equal(definition, restoredDefinition);
        Assert.Equal(checkpoint, restoredCheckpoint);
        Assert.DoesNotContain("System.", Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(definition)));
    }

    private static StreamingEvent Event(string id, long sequence, DateTimeOffset? eventTimeUtc = null)
        => new(id, sequence, eventTimeUtc ?? Utc(sequence), "payload"u8.ToArray());

    private static DateTimeOffset Utc(long seconds)
        => DateTimeOffset.UnixEpoch.AddSeconds(seconds);
}
