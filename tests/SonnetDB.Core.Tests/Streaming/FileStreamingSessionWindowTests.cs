using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Streaming;
using Xunit;

namespace SonnetDB.Core.Tests.Streaming;

public sealed class FileStreamingSessionWindowTests
{
    [Fact]
    public async Task ApplyBatchAsync_WithEqualGapAndOutOfOrderBridge_MergesSessions()
    {
        using var directory = new TemporaryDirectory();
        StreamingSessionWindowDefinition definition = Definition(TimeSpan.FromSeconds(10));
        await using var aggregator = await FileStreamingSessionWindowAggregator.CreateAsync(directory.StatePath, definition);

        await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 0), Event(2, 20)));
        await aggregator.ApplyBatchAsync(Delivery(2, Event(3, 10)));

        StreamingSessionWindow result = Assert.Single((await aggregator.ReadSessionsAsync()).Windows);
        Assert.Equal(Utc(0), result.StartUtc);
        Assert.Equal(Utc(20), result.EndUtc);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithClosedSessionAndRejectPolicy_IsAtomic()
    {
        using var directory = new TemporaryDirectory();
        StreamingSessionWindowDefinition definition = Definition(TimeSpan.FromSeconds(10), StreamingSessionLateEventPolicy.Reject);
        await using var aggregator = await FileStreamingSessionWindowAggregator.CreateAsync(directory.StatePath, definition);
        await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1)));
        await aggregator.AdvanceWatermarkAsync(Utc(11));

        await Assert.ThrowsAsync<StreamingLateEventException>(() => aggregator.ApplyBatchAsync(
            Delivery(2, Event(2, 30), Event(3, 1))).AsTask());
        StreamingSessionWindowState state = await aggregator.GetStateAsync();
        Assert.Equal(1, state.AppliedSequence);
        Assert.Equal(1, state.RetainedSessionCount);
    }

    [Fact]
    public async Task RemoveClosedSessionsAsync_RecordsRetiredBoundaryAndDoesNotRecreate()
    {
        using var directory = new TemporaryDirectory();
        StreamingSessionWindowDefinition definition = Definition(TimeSpan.FromSeconds(5));
        await using var aggregator = await FileStreamingSessionWindowAggregator.CreateAsync(directory.StatePath, definition);
        await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1)));
        await aggregator.AdvanceWatermarkAsync(Utc(6));
        Assert.Equal(1, await aggregator.RemoveClosedSessionsAsync(Utc(1)));
        await aggregator.ApplyBatchAsync(Delivery(2, Event(2, 1)));
        Assert.Empty((await aggregator.ReadSessionsAsync()).Windows);
        Assert.Equal(1, (await aggregator.GetStateAsync()).DroppedLateEvents);
    }

    [Fact]
    public async Task PumpOnceAsync_AfterCommitBeforeAck_ReopensAndDeduplicatesRealSubscription()
    {
        using var directory = new TemporaryDirectory();
        StreamingSubscriptionDefinition subscriptionDefinition = StreamingSubscriptionDefinition.Create("consumer", "orders", batchSize: 2, capacity: 8);
        StreamingSessionWindowDefinition definition = Definition(TimeSpan.FromSeconds(10));
        StreamingDeliveryBatch delivery;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, subscriptionDefinition))
        await using (var aggregator = await FileStreamingSessionWindowAggregator.CreateAsync(directory.StatePath, definition))
        {
            await subscription.PublishAsync(Event(1, 1));
            delivery = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
            await aggregator.ApplyBatchAsync(delivery);
            Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
        }

        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, subscriptionDefinition))
        await using (var aggregator = await FileStreamingSessionWindowAggregator.OpenAsync(directory.StatePath, definition))
        {
            Assert.True(await aggregator.PumpOnceAsync(subscription));
            Assert.Equal(1, Assert.Single((await aggregator.ReadSessionsAsync()).Windows).Count);
            Assert.Equal(1, subscription.Checkpoint.CommittedSequence);
        }
    }

    [Fact]
    public async Task OpenAsync_WithValidShaButInvalidSessionStructure_RejectsFailClosed()
    {
        using var directory = new TemporaryDirectory();
        StreamingSessionWindowDefinition definition = Definition(TimeSpan.FromSeconds(10));
        await using (var aggregator = await FileStreamingSessionWindowAggregator.CreateAsync(directory.StatePath, definition))
            await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1)));

        string original = await File.ReadAllTextAsync(directory.StatePath);
        using JsonDocument document = JsonDocument.Parse(original);
        string state = document.RootElement.GetProperty("state").GetRawText();
        string malformedState = state.Replace("\"count\":1", "\"count\":0", StringComparison.Ordinal);
        Assert.NotEqual(state, malformedState);
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(malformedState)));
        await File.WriteAllTextAsync(directory.StatePath, "{\"state\":" + malformedState + ",\"sha256\":\"" + digest + "\"}");
        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingSessionWindowAggregator.OpenAsync(directory.StatePath, definition).AsTask());
    }

    private static StreamingSessionWindowDefinition Definition(
        TimeSpan gap,
        StreamingSessionLateEventPolicy policy = StreamingSessionLateEventPolicy.Drop)
        => StreamingSessionWindowDefinition.Create("consumer", "orders", gap, lateEventPolicy: policy);

    private static DateTimeOffset Utc(double seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);

    private static StreamingEvent Event(long sequence, double seconds)
        => new($"event-{sequence}", sequence, Utc(seconds), Encoding.UTF8.GetBytes("{}"));

    private static StreamingDeliveryBatch Delivery(long revision, params StreamingEvent[] events)
        => new($"delivery-{revision}", 1, StreamingDeliveryStatus.InFlight, events,
            new StreamingSubscriptionCheckpoint(1, "consumer", events[^1].Sequence, DateTimeOffset.MinValue, revision));

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SdbSession-" + Guid.NewGuid().ToString("N")));

        public TemporaryDirectory() => Directory.CreateDirectory(_path);

        public string SubscriptionPath => Path.Combine(_path, "subscription");

        public string StatePath => Path.Combine(_path, "session.json");

        public void Dispose()
        {
            string resolved = Path.GetFullPath(_path);
            if (resolved != _path || !Path.GetFileName(resolved).StartsWith("SdbSession-", StringComparison.Ordinal)
                || Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())))
                throw new InvalidOperationException("测试目录归属验证失败，保留目录。");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
