using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Streaming;
using Xunit;

namespace SonnetDB.Core.Tests.Streaming;

public sealed class FileStreamingNumericWindowTests
{
    [Fact]
    public async Task ApplyBatchAsync_WhenHeaderEnumerationMutatesOriginalPayload_UsesOneOwnedSnapshotForHashAndNumericValue()
    {
        using var directory = new TemporaryDirectory();
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition());
        byte[] payload = "{\"Value\":1}"u8.ToArray();
        int mutations = 0;
        var headers = new MutatingHeaders(() =>
        {
            mutations++;
            if (mutations == 1)
                "{\"Value\":2}"u8.CopyTo(payload);
            else
                "{\"Value\":3}"u8.CopyTo(payload);
        });
        var value = new StreamingEvent("snapshot-event", 1, Utc(1), payload, headers);
        var delivery = new StreamingDeliveryBatch("snapshot-delivery", 1, StreamingDeliveryStatus.InFlight, [value],
            new StreamingSubscriptionCheckpoint(1, "consumer", 1, DateTimeOffset.MinValue, 1));
        await aggregator.ApplyBatchAsync(delivery);
        Assert.Equal("{\"Value\":2}"u8.ToArray(), payload);
        Assert.Equal(1, headers.Enumerations);
        StreamingWindowNumeric result = Assert.Single((await aggregator.ReadNumericWindowsAsync()).Windows);
        Assert.Equal(1m, result.Sum);
        Assert.Equal(1, result.Count);
        "{\"Value\":1}"u8.CopyTo(payload);
        await aggregator.ApplyBatchAsync(delivery with { Attempt = 2, Status = StreamingDeliveryStatus.Redelivered });
        Assert.Equal(2, headers.Enumerations);
        Assert.Equal("{\"Value\":3}"u8.ToArray(), payload);
        StreamingWindowNumeric replayed = Assert.Single((await aggregator.ReadNumericWindowsAsync()).Windows);
        Assert.Equal(1m, replayed.Sum);
        Assert.Equal(1, replayed.Count);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithPayloadSnapshotBeyondTotalByteLimit_RejectsBeforeHeaderEnumeration()
    {
        using var directory = new TemporaryDirectory();
        var options = new StreamingWindowOptions { MaxBatchBytes = 1024 };
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition(), options: options);
        var headers = new MutatingHeaders(() => throw new InvalidOperationException("超过原 payload 总字节上限时不应枚举第二个事件头。"));
        var first = new StreamingEvent("first", 1, Utc(1), new byte[512]);
        var second = new StreamingEvent("second", 2, Utc(2), new byte[513], headers);
        var delivery = new StreamingDeliveryBatch("bounded-copy", 1, StreamingDeliveryStatus.InFlight, [first, second],
            new StreamingSubscriptionCheckpoint(1, "consumer", 2, DateTimeOffset.MinValue, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => aggregator.ApplyBatchAsync(delivery).AsTask());
        Assert.Equal(0, headers.Enumerations);
        Assert.Equal(-1, (await aggregator.GetStateAsync()).AppliedSequence);
        Assert.Empty((await aggregator.ReadNumericWindowsAsync()).Windows);
    }

    [Fact]
    public async Task PumpOnceAsync_WithExactDecimalValues_PersistsNumericResultsAndAcknowledges()
    {
        using var directory = new TemporaryDirectory();
        StreamingWindowDefinition definition = Definition();
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, SubscriptionDefinition(4)))
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, definition))
        {
            await subscription.PublishAsync(Event(1, 12, "{\"Value\":5}"));
            await subscription.PublishAsync(Event(2, 0, "{\"Value\":0.1}"));
            await subscription.PublishAsync(Event(3, 9.999, "{\"Value\":2e-1}"));
            await subscription.PublishAsync(Event(4, 1, "{\"Value\":-0.3}"));
            await subscription.CompleteAsync();
            Assert.True(await aggregator.PumpOnceAsync(subscription));
            Assert.False(await aggregator.PumpOnceAsync(subscription));
            StreamingWindowNumericBatch batch = await aggregator.ReadNumericWindowsAsync();
            Assert.Equal(2, batch.Windows.Count);
            StreamingWindowNumeric first = batch.Windows[0];
            Assert.Equal(Utc(0), first.StartUtc);
            Assert.Equal(Utc(10), first.EndUtc);
            Assert.Equal(3, first.Count);
            Assert.Equal(0m, first.Sum);
            Assert.Equal(-0.3m, first.Min);
            Assert.Equal(0.2m, first.Max);
            Assert.Equal(0m, first.Average);
            Assert.Equal(5m, batch.Windows[1].Sum);
            Assert.Equal([3L, 1L], (await aggregator.ReadWindowsAsync()).Windows.Select(static value => value.Count));
            Assert.Equal(4, subscription.Checkpoint.CommittedSequence);
            Assert.Equal(0, subscription.PendingEventCount);
        }

        await using var reopened = await FileStreamingWindowAggregator.OpenAsync(directory.StatePath, definition);
        StreamingWindowNumericBatch restored = await reopened.ReadNumericWindowsAsync();
        Assert.Equal([0m, 5m], restored.Windows.Select(static value => value.Sum));
        Assert.Equal(4, restored.State.AppliedSequence);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllBytesAsync(directory.StatePath));
        Assert.Equal(2, document.RootElement.GetProperty("state").GetProperty("formatVersion").GetInt32());
    }

    [Fact]
    public async Task PumpOnceAsync_AfterNumericCommitBeforeAckAndReopen_DeduplicatesAndContinues()
    {
        using var directory = new TemporaryDirectory();
        StreamingWindowDefinition definition = Definition();
        StreamingDeliveryBatch delivery;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, SubscriptionDefinition(2)))
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, definition))
        {
            await subscription.PublishAsync(Event(1, 1, "{\"Value\":1.25}"));
            await subscription.PublishAsync(Event(2, 2, "{\"Value\":-0.25}"));
            delivery = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
            await aggregator.ApplyBatchAsync(delivery);
            Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
        }

        await using var restoredSubscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, SubscriptionDefinition(2));
        await using var restored = await FileStreamingWindowAggregator.OpenAsync(directory.StatePath, definition);
        Assert.Equal(delivery.DeliveryId, (await restoredSubscription.GetStatusAsync()).InFlightDeliveryId);
        Assert.True(await restored.PumpOnceAsync(restoredSubscription));
        StreamingWindowNumeric result = Assert.Single((await restored.ReadNumericWindowsAsync()).Windows);
        Assert.Equal(2, result.Count);
        Assert.Equal(1m, result.Sum);
        Assert.Equal(0.5m, result.Average);
        Assert.Equal(2, restoredSubscription.Checkpoint.CommittedSequence);
        await restoredSubscription.PublishAsync(Event(3, 3, "{\"Value\":2}"));
        Assert.True(await restored.PumpOnceAsync(restoredSubscription));
        Assert.Equal(3m, Assert.Single((await restored.ReadNumericWindowsAsync()).Windows).Sum);
        await Assert.ThrowsAsync<InvalidDataException>(() => restored.ApplyBatchAsync(
            delivery with { Events = [Event(1, 1, "{\"Value\":99}"), delivery.Events[1]] }).AsTask());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Value\":null}")]
    [InlineData("{\"Value\":\"1\"}")]
    [InlineData("{\"Value\":true}")]
    [InlineData("{\"Value\":[]}")]
    [InlineData("{\"value\":1}")]
    [InlineData("{\"nested\":{\"Value\":1}}")]
    [InlineData("{\"Value\":1,\"Value\":2}")]
    [InlineData("{\"Value\":1,\"\\u0056alue\":2}")]
    [InlineData("{\"Value\":1")]
    [InlineData("{\"Value\":1} {}")]
    [InlineData("{\"Value\":1e-29}")]
    [InlineData("{\"Value\":1e1001}")]
    [InlineData("{\"Value\":79228162514264337593543950336}")]
    [InlineData("{\"Value\":0.12345678901234567890123456789}")]
    public async Task PumpOnceAsync_WithInvalidNumericPayload_RejectsWholeBatchWithoutAckOrMutation(string payload)
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, SubscriptionDefinition(2));
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition());
        await subscription.PublishAsync(Event(1, 1, "{\"Value\":10}"));
        Assert.True(await aggregator.PumpOnceAsync(subscription));
        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        await subscription.PublishAsync(Event(2, 2, "{\"Value\":20}"));
        await subscription.PublishAsync(Event(3, 3, payload));
        await Assert.ThrowsAsync<InvalidDataException>(() => aggregator.PumpOnceAsync(subscription).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
        Assert.Equal(1, (await aggregator.GetStateAsync()).AppliedSequence);
        Assert.Equal(10m, Assert.Single((await aggregator.ReadNumericWindowsAsync()).Windows).Sum);
        Assert.Equal(1, subscription.Checkpoint.CommittedSequence);
        Assert.Equal(2, subscription.PendingEventCount);
        Assert.NotNull((await subscription.GetStatusAsync()).InFlightDeliveryId);
    }

    [Theory]
    [InlineData("79228162514264337593543950335", "1")]
    [InlineData("-79228162514264337593543950335", "-1")]
    [InlineData("79228162514264337593543950335", "0.1")]
    public async Task PumpOnceAsync_WithSumOverflowOrPrecisionLoss_RejectsAtomically(string first, string second)
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, SubscriptionDefinition(1));
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition());
        await subscription.PublishAsync(Event(1, 1, "{\"Value\":" + first + "}"));
        Assert.True(await aggregator.PumpOnceAsync(subscription));
        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        await subscription.PublishAsync(Event(2, 2, "{\"Value\":" + second + "}"));
        await Assert.ThrowsAsync<OverflowException>(() => aggregator.PumpOnceAsync(subscription).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
        Assert.Equal(decimal.Parse(first, CultureInfo.InvariantCulture), Assert.Single((await aggregator.ReadNumericWindowsAsync()).Windows).Sum);
        Assert.Equal(1, subscription.Checkpoint.CommittedSequence);
        Assert.Equal(1, subscription.PendingEventCount);
    }

    [Fact]
    public async Task PumpOnceAsync_WithRepresentableLongDecimalAndRepeatingAverage_PreservesExactSumAndRoundsAverage()
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, SubscriptionDefinition(3));
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition());
        await subscription.PublishAsync(Event(1, 1, "{\"Value\":0.9999999999999999999999999999}"));
        await subscription.PublishAsync(Event(2, 2, "{\"Value\":0.0000000000000000000000000001000}"));
        await subscription.PublishAsync(Event(3, 3, "{\"Value\":0}"));
        Assert.True(await aggregator.PumpOnceAsync(subscription));
        StreamingWindowNumeric result = Assert.Single((await aggregator.ReadNumericWindowsAsync()).Windows);
        Assert.Equal(1m, result.Sum);
        Assert.Equal(1m / 3m, result.Average);
        Assert.Equal(0m, result.Min);
        Assert.Equal(0.9999999999999999999999999999m, result.Max);
    }

    [Fact]
    public async Task ReadNumericWindowsAsync_WithPaginationCapacityAndLateDrop_ReclaimsBothCountAndNumericState()
    {
        using var directory = new TemporaryDirectory();
        var options = new StreamingWindowOptions { MaxWindows = 2 };
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, SubscriptionDefinition(2));
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition(), options: options);
        await subscription.PublishAsync(Event(1, -0.001, "{\"Value\":1}"));
        await subscription.PublishAsync(Event(2, 0, "{\"Value\":2}"));
        Assert.True(await aggregator.PumpOnceAsync(subscription));
        StreamingWindowNumericBatch first = await aggregator.ReadNumericWindowsAsync(maxWindows: 1);
        Assert.Equal(Utc(-10), Assert.Single(first.Windows).StartUtc);
        Assert.True(first.HasMore);
        StreamingWindowNumericBatch second = await aggregator.ReadNumericWindowsAsync(maxWindows: 1, afterStartUtc: first.NextStartUtc);
        Assert.Equal(Utc(0), Assert.Single(second.Windows).StartUtc);
        Assert.False(second.HasMore);
        await subscription.PublishAsync(Event(3, 10, "{\"Value\":3}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => aggregator.PumpOnceAsync(subscription).AsTask());
        Assert.Equal(2, subscription.Checkpoint.CommittedSequence);
        await aggregator.AdvanceWatermarkAsync(Utc(0));
        Assert.True(Assert.Single((await aggregator.ReadNumericWindowsAsync(maxWindows: 2, closedOnly: true)).Windows).IsClosed);
        Assert.Equal(1, await aggregator.RemoveClosedWindowsAsync(Utc(-10)));
        Assert.True(await aggregator.PumpOnceAsync(subscription));
        await subscription.PublishAsync(Event(4, -1, "not JSON"));
        Assert.True(await aggregator.PumpOnceAsync(subscription));
        Assert.Equal(1, (await aggregator.GetStateAsync()).DroppedLateEvents);
        Assert.Equal([2m, 3m], (await aggregator.ReadNumericWindowsAsync(maxWindows: 2)).Windows.Select(static value => value.Sum));
    }

    [Fact]
    public async Task PumpOnceAsync_WithRejectedLateNumericEvent_PreservesWholeBatch()
    {
        using var directory = new TemporaryDirectory();
        StreamingWindowDefinition definition = Definition(StreamingWindowLateEventPolicy.Reject);
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, SubscriptionDefinition(2));
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, definition);
        await aggregator.AdvanceWatermarkAsync(Utc(10));
        await subscription.PublishAsync(Event(1, 11, "{\"Value\":2}"));
        await subscription.PublishAsync(Event(2, 1, "{\"Value\":3}"));
        await Assert.ThrowsAsync<StreamingLateEventException>(() => aggregator.PumpOnceAsync(subscription).AsTask());
        Assert.Empty((await aggregator.ReadNumericWindowsAsync()).Windows);
        Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
        Assert.Equal(0, (await aggregator.GetStateAsync()).DroppedLateEvents);
    }

    [Fact]
    public async Task OpenAsync_WithOriginalCountFormatAndHash_RestoresWithoutChangingFormatAndRejectsTampering()
    {
        using var directory = new TemporaryDirectory();
        const string state = "{\"formatVersion\":1,\"definition\":{\"formatVersion\":1,\"subscriptionId\":\"consumer\",\"streamName\":\"orders\",\"windowSizeMilliseconds\":10000,\"allowedLatenessMilliseconds\":0,\"lateEventPolicy\":0},\"options\":{\"maxWindows\":10000,\"maxBatchEvents\":1000,\"maxBatchBytes\":4194304,\"maxStateBytes\":4194304,\"operationTimeoutMilliseconds\":10000},\"appliedSequence\":-1,\"appliedRevision\":0,\"watermarkUtc\":\"0001-01-01T00:00:00+00:00\",\"droppedLateEvents\":0,\"lastDeliveryId\":null,\"lastBatchHash\":null,\"windows\":{}}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
        string envelope = "{\"state\":" + state + ",\"sha256\":\"" + hash + "\"}";
        await File.WriteAllTextAsync(directory.StatePath, envelope);
        StreamingWindowDefinition countDefinition = StreamingWindowDefinition.Create("consumer", "orders", TimeSpan.FromSeconds(10));
        await using (var aggregator = await FileStreamingWindowAggregator.OpenAsync(directory.StatePath, countDefinition))
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, SubscriptionDefinition(1)))
        {
            await subscription.PublishAsync(Event(1, 1, "opaque COUNT payload"));
            Assert.True(await aggregator.PumpOnceAsync(subscription));
            Assert.Equal(1, Assert.Single((await aggregator.ReadWindowsAsync()).Windows).Count);
            await Assert.ThrowsAsync<InvalidOperationException>(() => aggregator.ReadNumericWindowsAsync().AsTask());
        }

        using (JsonDocument persisted = JsonDocument.Parse(await File.ReadAllBytesAsync(directory.StatePath)))
        {
            JsonElement restored = persisted.RootElement.GetProperty("state");
            Assert.Equal(1, restored.GetProperty("formatVersion").GetInt32());
            Assert.False(restored.TryGetProperty("numericWindows", out _));
            Assert.False(restored.GetProperty("definition").TryGetProperty("numericField", out _));
        }

        await File.WriteAllTextAsync(directory.StatePath, envelope.Replace("\"appliedRevision\":0", "\"appliedRevision\":1", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingWindowAggregator.OpenAsync(directory.StatePath, countDefinition).AsTask());
        await File.WriteAllTextAsync(directory.StatePath, envelope.Replace("\"windows\":{}", "\"windows\":{},\"numericWindows\":null", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingWindowAggregator.OpenAsync(directory.StatePath, countDefinition).AsTask());
        await File.WriteAllTextAsync(directory.StatePath, envelope);
        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingWindowAggregator.OpenAsync(directory.StatePath, Definition()).AsTask());
    }

    [Fact]
    public async Task PumpOnceAsync_WithCancellationThenValidNumericBatch_PreservesStateAndRemainsUsable()
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, SubscriptionDefinition(1));
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aggregator.PumpOnceAsync(subscription, cancellation.Token).AsTask());
        Assert.Equal(-1, (await aggregator.GetStateAsync()).AppliedSequence);
        await subscription.PublishAsync(Event(1, 1, "{\"Value\":42}"));
        Assert.True(await aggregator.PumpOnceAsync(subscription));
        Assert.Equal(42m, Assert.Single((await aggregator.ReadNumericWindowsAsync()).Windows).Average);
    }

    [Fact]
    public void CreateNumeric_WithInvalidFieldOrCountVersion_RejectsDefinition()
    {
        Assert.Throws<ArgumentException>(() => StreamingWindowDefinition.CreateNumeric("consumer", "orders", TimeSpan.FromSeconds(10), " "));
        Assert.Throws<ArgumentException>(() => StreamingWindowDefinition.CreateNumeric("consumer", "orders", TimeSpan.FromSeconds(10), new string('x', 257)));
        Assert.Throws<InvalidDataException>(() => (Definition() with { FormatVersion = 1 }).Validate());
    }

    private static StreamingWindowDefinition Definition(StreamingWindowLateEventPolicy latePolicy = StreamingWindowLateEventPolicy.Drop)
        => StreamingWindowDefinition.CreateNumeric("consumer", "orders", TimeSpan.FromSeconds(10), "Value", lateEventPolicy: latePolicy);

    private static StreamingSubscriptionDefinition SubscriptionDefinition(int batchSize)
        => StreamingSubscriptionDefinition.Create("consumer", "orders", batchSize, capacity: 10);

    private static DateTimeOffset Utc(double seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);

    private static StreamingEvent Event(long sequence, double seconds, string payload)
        => new($"event-{sequence}", sequence, Utc(seconds), Encoding.UTF8.GetBytes(payload));

    private sealed class MutatingHeaders(Action onEnumeration) : IReadOnlyDictionary<string, string>
    {
        public int Enumerations { get; private set; }

        public int Count => 1;

        public IEnumerable<string> Keys => ["source"];

        public IEnumerable<string> Values => ["meter"];

        public string this[string key] => key == "source" ? "meter" : throw new KeyNotFoundException();

        public bool ContainsKey(string key) => key == "source";

        public bool TryGetValue(string key, out string value)
        {
            value = key == "source" ? "meter" : string.Empty;
            return key == "source";
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            Enumerations++;
            onEnumeration();
            return ((IEnumerable<KeyValuePair<string, string>>)new[] { new KeyValuePair<string, string>("source", "meter") }).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _ownedPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SonnetDB-NumericWindowTests-" + Guid.NewGuid().ToString("N")));

        public TemporaryDirectory() => Directory.CreateDirectory(_ownedPath);

        public string SubscriptionPath => Path.Combine(_ownedPath, "subscription");

        public string StatePath => Path.Combine(_ownedPath, "windows.json");

        public void Dispose()
        {
            string resolved = Path.GetFullPath(_ownedPath);
            if (resolved != _ownedPath || !Path.GetFileName(resolved).StartsWith("SonnetDB-NumericWindowTests-", StringComparison.Ordinal)
                || Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())))
                throw new InvalidOperationException("测试目录归属验证失败，保留目录。");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
