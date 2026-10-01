using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Streaming;
using Xunit;

namespace SonnetDB.Core.Tests.Streaming;

public sealed class FileStreamingGroupedWindowTests
{
    [Fact]
    public async Task ApplyBatchAsync_WithOrdinalStringKeysAndOutOfOrderTimes_PagesStableWindowKeyPositions()
    {
        using var directory = new TemporaryDirectory();
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition());
        await aggregator.ApplyBatchAsync(Delivery(1,
            Event(1, 12, "{\"Group\":\"A\"}"), Event(2, 0, "{\"Group\":\"a\"}"),
            Event(3, 9.999, "{\"Group\":\"A\"}"), Event(4, -0.001, "{\"Group\":\"\"}"),
            Event(5, 1, "{\"Group\":\"A\"}"), Event(6, 2, "{\"\\u0047roup\":\"A\"}")));

        StreamingGroupedWindowBatch first = await aggregator.ReadGroupedWindowsAsync(maxWindows: 2);
        Assert.Equal([Utc(-10), Utc(0)], first.Windows.Select(static window => window.StartUtc));
        Assert.Equal(["", "A"], first.Windows.Select(static window => window.GroupKey));
        Assert.Equal([1L, 3L], first.Windows.Select(static window => window.Count));
        Assert.All(first.Windows, static window => Assert.Null(window.Sum));
        Assert.True(first.HasMore);
        StreamingGroupedWindowBatch second = await aggregator.ReadGroupedWindowsAsync(maxWindows: 2, after: first.NextCursor);
        Assert.Equal([Utc(0), Utc(10)], second.Windows.Select(static window => window.StartUtc));
        Assert.Equal(["a", "A"], second.Windows.Select(static window => window.GroupKey));
        Assert.False(second.HasMore);
        Assert.Equal(4, second.State.RetainedWindowCount);
        StreamingGroupedWindowBatch empty = await aggregator.ReadGroupedWindowsAsync(after: second.NextCursor);
        Assert.Empty(empty.Windows);
        Assert.Null(empty.NextCursor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => aggregator.ReadWindowsAsync().AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => aggregator.ReadNumericWindowsAsync().AsTask());
    }

    [Fact]
    public async Task PumpOnceAsync_AfterGroupedNumericCommitBeforeAckAndReopen_DeduplicatesExactResults()
    {
        using var directory = new TemporaryDirectory();
        StreamingWindowDefinition definition = NumericDefinition();
        StreamingDeliveryBatch original;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, SubscriptionDefinition(3)))
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, definition))
        {
            await subscription.PublishAsync(Event(1, 1, "{\"Group\":\"A\",\"Value\":0.1}"));
            await subscription.PublishAsync(Event(2, 2, "{\"Group\":\"A\",\"Value\":2e-1}"));
            await subscription.PublishAsync(Event(3, 3, "{\"Group\":\"a\",\"Value\":-0.3}"));
            original = Assert.IsType<StreamingDeliveryBatch>(await subscription.ReadBatchAsync());
            await aggregator.ApplyBatchAsync(original);
            Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
        }

        await using var restoredSubscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, SubscriptionDefinition(3));
        await using var restored = await FileStreamingWindowAggregator.OpenAsync(directory.StatePath, definition);
        Assert.True(await restored.PumpOnceAsync(restoredSubscription));
        StreamingGroupedWindowBatch results = await restored.ReadGroupedWindowsAsync();
        Assert.Equal(["A", "a"], results.Windows.Select(static window => window.GroupKey));
        Assert.Equal([2L, 1L], results.Windows.Select(static window => window.Count));
        Assert.Equal(0.3m, results.Windows[0].Sum);
        Assert.Equal(0.1m, results.Windows[0].Min);
        Assert.Equal(0.2m, results.Windows[0].Max);
        Assert.Equal(0.15m, results.Windows[0].Average);
        Assert.Equal(-0.3m, results.Windows[1].Sum);
        Assert.Equal(3, restoredSubscription.Checkpoint.CommittedSequence);
        await restored.ApplyBatchAsync(original with { Attempt = 3, Status = StreamingDeliveryStatus.Redelivered });
        Assert.Equal(2, (await restored.ReadGroupedWindowsAsync()).Windows[0].Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => restored.ApplyBatchAsync(original with
        {
            Events = [Event(1, 1, "{\"Group\":\"B\",\"Value\":0.1}"), original.Events[1], original.Events[2]],
        }).AsTask());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Group\":null}")]
    [InlineData("{\"Group\":1}")]
    [InlineData("{\"Group\":true}")]
    [InlineData("{\"Group\":[]}")]
    [InlineData("{\"Group\":{}}")]
    [InlineData("{\"group\":\"A\"}")]
    [InlineData("{\"nested\":{\"Group\":\"A\"}}")]
    [InlineData("{\"Group\":\"A\",\"Group\":\"B\"}")]
    [InlineData("{\"Group\":\"\",\"\\u0047roup\":\"B\"}")]
    [InlineData("{\"Group\":\"A\"")]
    [InlineData("{\"Group\":\"A\"} {}")]
    [InlineData("[]")]
    public async Task PumpOnceAsync_WithInvalidGroupKey_RejectsWholeBatchWithoutAckOrMutation(string payload)
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.SubscriptionPath, SubscriptionDefinition(2));
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition());
        await subscription.PublishAsync(Event(1, 1, "{\"Group\":\"A\"}"));
        Assert.True(await aggregator.PumpOnceAsync(subscription));
        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        await subscription.PublishAsync(Event(2, 2, "{\"Group\":\"B\"}"));
        await subscription.PublishAsync(Event(3, 3, payload));
        await Assert.ThrowsAsync<InvalidDataException>(() => aggregator.PumpOnceAsync(subscription).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
        Assert.Equal(1, subscription.Checkpoint.CommittedSequence);
        Assert.Equal(2, subscription.PendingEventCount);
        Assert.Equal("A", Assert.Single((await aggregator.ReadGroupedWindowsAsync()).Windows).GroupKey);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithMaximumKeyLength_PreservesUnicodeAndRejectsLongerKey()
    {
        using var directory = new TemporaryDirectory();
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition());
        string key = new('界', 256);
        await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1, "{\"Group\":\"" + key + "\"}")));
        Assert.Equal(key, Assert.Single((await aggregator.ReadGroupedWindowsAsync()).Windows).GroupKey);
        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => aggregator.ApplyBatchAsync(
            Delivery(2, Event(2, 2, "{\"Group\":\"" + key + "界\"}"))).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
    }

    [Fact]
    public async Task ApplyBatchAsync_WithDistinctGroupLimit_RejectsAtomicallyAndReclaimsOnlyAfterLastWindowRemoved()
    {
        using var directory = new TemporaryDirectory();
        var options = new StreamingWindowOptions { MaxGroups = 1, MaxWindows = 3 };
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition(), options: options);
        await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1, "{\"Group\":\"A\"}"), Event(2, 11, "{\"Group\":\"A\"}")));
        StreamingDeliveryBatch next = Delivery(2, Event(3, 12, "{\"Group\":\"A\"}"), Event(4, 13, "{\"Group\":\"B\"}"));
        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => aggregator.ApplyBatchAsync(next).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
        await aggregator.AdvanceWatermarkAsync(Utc(10));
        Assert.Equal(1, await aggregator.RemoveClosedWindowsAsync(Utc(0)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => aggregator.ApplyBatchAsync(next).AsTask());
        Assert.Equal(1, Assert.Single((await aggregator.ReadGroupedWindowsAsync(maxWindows: 3)).Windows).Count);
        await aggregator.AdvanceWatermarkAsync(Utc(20));
        Assert.Equal(1, await aggregator.RemoveClosedWindowsAsync(Utc(10)));
        await aggregator.ApplyBatchAsync(Delivery(2, Event(3, 21, "{\"Group\":\"B\"}")));
        Assert.Equal("B", Assert.Single((await aggregator.ReadGroupedWindowsAsync(maxWindows: 3)).Windows).GroupKey);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithWindowPairLimit_ReclaimsClosedResultsAndDeduplicatesAfterCleanup()
    {
        using var directory = new TemporaryDirectory();
        var options = new StreamingWindowOptions { MaxGroups = 2, MaxWindows = 2 };
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition(), options: options);
        StreamingDeliveryBatch original = Delivery(1, Event(1, 1, "{\"Group\":\"A\"}"), Event(2, 1, "{\"Group\":\"B\"}"));
        await aggregator.ApplyBatchAsync(original);
        await Assert.ThrowsAsync<InvalidOperationException>(() => aggregator.ApplyBatchAsync(
            Delivery(2, Event(3, 11, "{\"Group\":\"A\"}"))).AsTask());
        await aggregator.AdvanceWatermarkAsync(Utc(10));
        StreamingGroupedWindowBatch closed = await aggregator.ReadGroupedWindowsAsync(maxWindows: 1, closedOnly: true);
        Assert.Equal("A", Assert.Single(closed.Windows).GroupKey);
        Assert.True(closed.HasMore);
        Assert.Equal("B", Assert.Single((await aggregator.ReadGroupedWindowsAsync(maxWindows: 1, after: closed.NextCursor, closedOnly: true)).Windows).GroupKey);
        Assert.Equal(2, await aggregator.RemoveClosedWindowsAsync(Utc(0)));
        await aggregator.ApplyBatchAsync(original with { Attempt = 2, Status = StreamingDeliveryStatus.Redelivered });
        Assert.Equal(0, (await aggregator.GetStateAsync()).DroppedLateEvents);
        Assert.Empty((await aggregator.ReadGroupedWindowsAsync(maxWindows: 2)).Windows);
        await aggregator.ApplyBatchAsync(Delivery(2, Event(3, 11, "{\"Group\":\"A\"}")));
        Assert.Equal(1, Assert.Single((await aggregator.ReadGroupedWindowsAsync(maxWindows: 2)).Windows).Count);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithStateByteLimit_RejectsBeforeCommitAndKeepsInstanceUsable()
    {
        using var directory = new TemporaryDirectory();
        var options = new StreamingWindowOptions { MaxGroups = 2, MaxStateBytes = 1024 };
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition(), options: options);
        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => aggregator.ApplyBatchAsync(
            Delivery(1, Event(1, 1, "{\"Group\":\"" + new string('界', 256) + "\"}"))).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
        Assert.Equal(-1, (await aggregator.GetStateAsync()).AppliedSequence);
        await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1, "{\"Group\":\"A\"}")));
        Assert.Equal("A", Assert.Single((await aggregator.ReadGroupedWindowsAsync()).Windows).GroupKey);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithAllowedLatenessAndLateDrop_DoesNotParseDiscardedKeysOrRecreateRemovedGroups()
    {
        using var directory = new TemporaryDirectory();
        StreamingWindowDefinition definition = StreamingWindowDefinition.CreateGrouped("consumer", "orders", TimeSpan.FromSeconds(10),
            "Group", TimeSpan.FromSeconds(2));
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, definition))
        {
            await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1, "{\"Group\":\"A\"}")));
            await aggregator.AdvanceWatermarkAsync(Utc(11.999));
            Assert.Empty((await aggregator.ReadGroupedWindowsAsync(closedOnly: true)).Windows);
            Assert.Equal(0, await aggregator.RemoveClosedWindowsAsync(Utc(0)));
            await aggregator.AdvanceWatermarkAsync(Utc(12));
            Assert.True(Assert.Single((await aggregator.ReadGroupedWindowsAsync()).Windows).IsClosed);
            Assert.Equal(1, await aggregator.RemoveClosedWindowsAsync(Utc(0)));
            await aggregator.ApplyBatchAsync(Delivery(2, Event(2, 1, "not JSON"), Event(3, 11, "{\"Group\":\"B\"}")));
            Assert.Equal("B", Assert.Single((await aggregator.ReadGroupedWindowsAsync()).Windows).GroupKey);
            Assert.Equal(1, (await aggregator.GetStateAsync()).DroppedLateEvents);
        }

        await using var restored = await FileStreamingWindowAggregator.OpenAsync(directory.StatePath, definition);
        Assert.Equal(1, (await restored.GetStateAsync()).DroppedLateEvents);
        Assert.Equal("B", Assert.Single((await restored.ReadGroupedWindowsAsync()).Windows).GroupKey);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithRejectedLateEvent_RejectsOtherGroupsInSameBatch()
    {
        using var directory = new TemporaryDirectory();
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath,
            Definition(StreamingWindowLateEventPolicy.Reject));
        await aggregator.AdvanceWatermarkAsync(Utc(10));
        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        await Assert.ThrowsAsync<StreamingLateEventException>(() => aggregator.ApplyBatchAsync(Delivery(1,
            Event(1, 11, "{\"Group\":\"B\"}"), Event(2, 1, "{\"Group\":\"A\"}"))).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
        Assert.Empty((await aggregator.ReadGroupedWindowsAsync()).Windows);
        Assert.Equal(0, (await aggregator.GetStateAsync()).DroppedLateEvents);
    }

    [Theory]
    [InlineData("79228162514264337593543950335", "1", true)]
    [InlineData("79228162514264337593543950335", "0.1", true)]
    [InlineData("0.1", "1e-29", false)]
    public async Task ApplyBatchAsync_WithUnrepresentableGroupedNumericSum_RejectsWholeBatch(string first, string second, bool overflow)
    {
        using var directory = new TemporaryDirectory();
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, NumericDefinition());
        StreamingDeliveryBatch batch = Delivery(1, Event(1, 1, "{\"Group\":\"A\",\"Value\":" + first + "}"),
            Event(2, 2, "{\"Group\":\"A\",\"Value\":" + second + "}"));
        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        if (overflow)
            await Assert.ThrowsAsync<OverflowException>(() => aggregator.ApplyBatchAsync(batch).AsTask());
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => aggregator.ApplyBatchAsync(batch).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
        Assert.Empty((await aggregator.ReadGroupedWindowsAsync()).Windows);
    }

    [Theory]
    [InlineData("{\"Group\":\"B\"}")]
    [InlineData("{\"Group\":\"B\",\"Value\":null}")]
    [InlineData("{\"Group\":\"B\",\"Value\":1,\"\\u0056alue\":2}")]
    public async Task ApplyBatchAsync_WithInvalidGroupedNumericProperty_PreservesOtherGroups(string payload)
    {
        using var directory = new TemporaryDirectory();
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, NumericDefinition());
        await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1, "{\"Group\":\"A\",\"Value\":0.1}")));
        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => aggregator.ApplyBatchAsync(Delivery(2,
            Event(2, 2, "{\"Group\":\"A\",\"Value\":0.2}"), Event(3, 3, payload))).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
        StreamingGroupedWindow result = Assert.Single((await aggregator.ReadGroupedWindowsAsync()).Windows);
        Assert.Equal(1, result.Count);
        Assert.Equal(0.1m, result.Sum);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithCancellation_RemainsUsableWithoutStateMutation()
    {
        using var directory = new TemporaryDirectory();
        await using var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition());
        StreamingDeliveryBatch batch = Delivery(1, Event(1, 1, "{\"Group\":\"A\"}"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        byte[] before = await File.ReadAllBytesAsync(directory.StatePath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aggregator.ApplyBatchAsync(batch, cancellation.Token).AsTask());
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.StatePath));
        await aggregator.ApplyBatchAsync(batch);
        Assert.Equal(1, Assert.Single((await aggregator.ReadGroupedWindowsAsync()).Windows).Count);
    }

    [Fact]
    public async Task ApplyBatchAsync_WithRepeatingAverageAndIndependentGroupSums_PreservesExactDecimalState()
    {
        using var directory = new TemporaryDirectory();
        StreamingWindowDefinition definition = NumericDefinition();
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, definition))
        {
            await aggregator.ApplyBatchAsync(Delivery(1,
                Event(1, 1, "{\"Group\":\"A\",\"Value\":0.9999999999999999999999999999}"),
                Event(2, 2, "{\"Group\":\"A\",\"Value\":0.0000000000000000000000000001000}"),
                Event(3, 3, "{\"Group\":\"A\",\"Value\":0}"),
                Event(4, 3, "{\"Group\":\"B\",\"Value\":79228162514264337593543950335}")));
        }

        await using var restored = await FileStreamingWindowAggregator.OpenAsync(directory.StatePath, definition);
        StreamingGroupedWindowBatch results = await restored.ReadGroupedWindowsAsync();
        Assert.Equal(1m, results.Windows[0].Sum);
        Assert.Equal(1m / 3m, results.Windows[0].Average);
        Assert.Equal(decimal.MaxValue, results.Windows[1].Sum);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OpenAsync_WithOriginalVersionAndHash_PreservesOldShapeAndRefusesGroupedUpgrade(int version)
    {
        using var directory = new TemporaryDirectory();
        string state = OriginalState(version);
        string envelope = Envelope(state);
        await File.WriteAllTextAsync(directory.StatePath, envelope);
        StreamingWindowDefinition definition = version == 1
            ? StreamingWindowDefinition.Create("consumer", "orders", TimeSpan.FromSeconds(10))
            : StreamingWindowDefinition.CreateNumeric("consumer", "orders", TimeSpan.FromSeconds(10), "Value");
        await using (var aggregator = await FileStreamingWindowAggregator.OpenAsync(directory.StatePath, definition))
        {
            Assert.Equal(envelope, await File.ReadAllTextAsync(directory.StatePath));
            await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1, version == 1 ? "opaque" : "{\"Value\":0.1}")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => aggregator.ReadGroupedWindowsAsync().AsTask());
        }

        using (JsonDocument persisted = JsonDocument.Parse(await File.ReadAllBytesAsync(directory.StatePath)))
        {
            JsonElement raw = persisted.RootElement.GetProperty("state");
            Assert.Equal(version, raw.GetProperty("formatVersion").GetInt32());
            Assert.False(raw.TryGetProperty("groupedWindows", out _));
            Assert.False(raw.GetProperty("definition").TryGetProperty("groupField", out _));
            Assert.False(raw.GetProperty("options").TryGetProperty("maxGroups", out _));
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingWindowAggregator.OpenAsync(directory.StatePath, Definition()).AsTask());
    }

    [Theory]
    [InlineData(1, "options")]
    [InlineData(2, "options")]
    [InlineData(1, "definition")]
    [InlineData(2, "definition")]
    [InlineData(1, "state")]
    [InlineData(2, "state")]
    public async Task OpenAsync_WithGroupedNullFieldInOldState_RejectsEvenWithOriginalHash(int version, string location)
    {
        using var directory = new TemporaryDirectory();
        string state = OriginalState(version);
        string modified = location switch
        {
            "options" => state.Replace("\"operationTimeoutMilliseconds\":10000", "\"operationTimeoutMilliseconds\":10000,\"maxGroups\":null", StringComparison.Ordinal),
            "definition" => state.Replace("\"lateEventPolicy\":0", "\"lateEventPolicy\":0,\"groupField\":null", StringComparison.Ordinal),
            _ => state[..^1] + ",\"groupedWindows\":null}",
        };
        // 旧哈希排除新增空字段；不能因共享 DTO 忽略空值而接受此变体。
        await File.WriteAllTextAsync(directory.StatePath,
            "{\"state\":" + modified + ",\"sha256\":\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state))) + "\"}");
        StreamingWindowDefinition definition = version == 1
            ? StreamingWindowDefinition.Create("consumer", "orders", TimeSpan.FromSeconds(10))
            : StreamingWindowDefinition.CreateNumeric("consumer", "orders", TimeSpan.FromSeconds(10), "Value");
        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingWindowAggregator.OpenAsync(directory.StatePath, definition).AsTask());
    }

    [Fact]
    public async Task OpenAsync_WithDuplicateGroupedPositionAndValidHash_RejectsStructuralCorruption()
    {
        using var directory = new TemporaryDirectory();
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition()))
        {
            await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1, "{\"Group\":\"A\"}"), Event(2, 1, "{\"Group\":\"B\"}")));
        }

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllBytesAsync(directory.StatePath));
        string state = document.RootElement.GetProperty("state").GetRawText().Replace("\"groupKey\":\"B\"", "\"groupKey\":\"A\"", StringComparison.Ordinal);
        await File.WriteAllTextAsync(directory.StatePath, Envelope(state));
        await Assert.ThrowsAsync<InvalidDataException>(() => FileStreamingWindowAggregator.OpenAsync(directory.StatePath, Definition()).AsTask());
    }

    [Theory]
    [InlineData("root")]
    [InlineData("state")]
    [InlineData("groupKey")]
    [InlineData("count")]
    [InlineData("escapedGroupKey")]
    public async Task OpenAsync_WithDuplicateGroupedJsonPropertyAndValidEffectiveHash_RejectsBeforeLastWins(string location)
    {
        using var directory = new TemporaryDirectory();
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition()))
        {
            await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1, "{\"Group\":\"A\"}")));
        }

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllBytesAsync(directory.StatePath));
        string state = document.RootElement.GetProperty("state").GetRawText();
        // 对最后属性生效后的合法状态重新签名；单纯的重序列化哈希校验会接受以下变体。
        string signed = Envelope(state);
        string modified = location switch
        {
            "root" => signed.Replace("\"state\":", "\"state\":{\"formatVersion\":3},\"state\":", StringComparison.Ordinal),
            "state" => signed.Replace("\"appliedSequence\":1", "\"appliedSequence\":99,\"appliedSequence\":1", StringComparison.Ordinal),
            "groupKey" => signed.Replace("\"groupKey\":\"A\"", "\"groupKey\":\"shadow\",\"groupKey\":\"A\"", StringComparison.Ordinal),
            "count" => signed.Replace("\"count\":1", "\"count\":99,\"count\":1", StringComparison.Ordinal),
            _ => signed.Replace("\"groupKey\":\"A\"", "\"\\u0067roupKey\":\"shadow\",\"groupKey\":\"A\"", StringComparison.Ordinal),
        };
        Assert.NotEqual(signed, modified);
        await File.WriteAllTextAsync(directory.StatePath, modified);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            FileStreamingWindowAggregator.OpenAsync(directory.StatePath, Definition()).AsTask());
        Assert.Contains("重复 JSON 属性", error.Message, StringComparison.Ordinal);
        Assert.Equal(modified, await File.ReadAllTextAsync(directory.StatePath));
    }

    [Theory]
    [InlineData("allowedLatenessMilliseconds", "0", "定义")]
    [InlineData("lateEventPolicy", "0", "定义")]
    [InlineData("maxWindows", "10000", "容量")]
    [InlineData("operationTimeoutMilliseconds", "10000", "容量")]
    public async Task OpenAsync_WithOmittedDefaultGroupedFieldAndOriginalHash_RejectsInsteadOfRestoringDefault(
        string field, string value, string boundary)
    {
        using var directory = new TemporaryDirectory();
        await using (var aggregator = await FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition()))
        {
            await aggregator.ApplyBatchAsync(Delivery(1, Event(1, 1, "{\"Group\":\"A\"}")));
        }

        string original = await File.ReadAllTextAsync(directory.StatePath);
        // 四个字段后均仍有属性；删除它们并保留原 SHA-256，默认值回填会恢复原有效状态及哈希。
        string modified = original.Replace($"\"{field}\":{value},", "", StringComparison.Ordinal);
        Assert.NotEqual(original, modified);
        await File.WriteAllTextAsync(directory.StatePath, modified);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            FileStreamingWindowAggregator.OpenAsync(directory.StatePath, Definition()).AsTask());
        Assert.Contains("缺少必需" + boundary + "字段", error.Message, StringComparison.Ordinal);
        Assert.Equal(modified, await File.ReadAllTextAsync(directory.StatePath));
    }

    [Fact]
    public async Task CreateAsync_WithInvalidGroupingDefinitionOrCapacity_RejectsExplicitContract()
    {
        using var directory = new TemporaryDirectory();
        Assert.Throws<ArgumentException>(() => StreamingWindowDefinition.CreateGrouped("consumer", "orders", TimeSpan.FromSeconds(10), " "));
        Assert.Throws<ArgumentException>(() => StreamingWindowDefinition.CreateGrouped("consumer", "orders", TimeSpan.FromSeconds(10), new string('x', 257)));
        Assert.Throws<ArgumentException>(() => StreamingWindowDefinition.CreateGroupedNumeric("consumer", "orders", TimeSpan.FromSeconds(10), "Group", "Group"));
        Assert.Throws<InvalidDataException>(() => (Definition() with { FormatVersion = 1 }).Validate());
        await Assert.ThrowsAsync<ArgumentException>(() => FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition(), options: new StreamingWindowOptions()).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => FileStreamingWindowAggregator.CreateAsync(directory.StatePath, Definition(), options: new StreamingWindowOptions { MaxGroups = 0 }).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => FileStreamingWindowAggregator.CreateAsync(directory.StatePath,
            StreamingWindowDefinition.Create("consumer", "orders", TimeSpan.FromSeconds(10)), options: new StreamingWindowOptions { MaxGroups = 1 }).AsTask());
    }

    private static StreamingWindowDefinition Definition(StreamingWindowLateEventPolicy latePolicy = StreamingWindowLateEventPolicy.Drop)
        => StreamingWindowDefinition.CreateGrouped("consumer", "orders", TimeSpan.FromSeconds(10), "Group", lateEventPolicy: latePolicy);

    private static StreamingWindowDefinition NumericDefinition()
        => StreamingWindowDefinition.CreateGroupedNumeric("consumer", "orders", TimeSpan.FromSeconds(10), "Group", "Value");

    private static StreamingSubscriptionDefinition SubscriptionDefinition(int batchSize)
        => StreamingSubscriptionDefinition.Create("consumer", "orders", batchSize, capacity: 10);

    private static DateTimeOffset Utc(double seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);

    private static StreamingEvent Event(long sequence, double seconds, string payload)
        => new($"event-{sequence}", sequence, Utc(seconds), Encoding.UTF8.GetBytes(payload));

    private static StreamingDeliveryBatch Delivery(long revision, params StreamingEvent[] events)
        => new($"delivery-{revision}", 1, StreamingDeliveryStatus.InFlight, events,
            new StreamingSubscriptionCheckpoint(1, "consumer", events[^1].Sequence, DateTimeOffset.MinValue, revision));

    private static string OriginalState(int version)
    {
        string numericField = version == 2 ? ",\"numericField\":\"Value\"" : "";
        string numericWindows = version == 2 ? ",\"numericWindows\":{}" : "";
        return "{\"formatVersion\":" + version + ",\"definition\":{\"formatVersion\":" + version
            + ",\"subscriptionId\":\"consumer\",\"streamName\":\"orders\",\"windowSizeMilliseconds\":10000,\"allowedLatenessMilliseconds\":0,\"lateEventPolicy\":0"
            + numericField + "},\"options\":{\"maxWindows\":10000,\"maxBatchEvents\":1000,\"maxBatchBytes\":4194304,\"maxStateBytes\":4194304,\"operationTimeoutMilliseconds\":10000},\"appliedSequence\":-1,\"appliedRevision\":0,\"watermarkUtc\":\"0001-01-01T00:00:00+00:00\",\"droppedLateEvents\":0,\"lastDeliveryId\":null,\"lastBatchHash\":null,\"windows\":{}"
            + numericWindows + "}";
    }

    private static string Envelope(string state)
        => "{\"state\":" + state + ",\"sha256\":\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state))) + "\"}";

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _ownedPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SdbGw-" + Guid.NewGuid().ToString("N")));

        public TemporaryDirectory() => Directory.CreateDirectory(_ownedPath);

        public string SubscriptionPath => Path.Combine(_ownedPath, "subscription");

        public string StatePath => Path.Combine(_ownedPath, "windows.json");

        public void Dispose()
        {
            string resolved = Path.GetFullPath(_ownedPath);
            if (resolved != _ownedPath || !Path.GetFileName(resolved).StartsWith("SdbGw-", StringComparison.Ordinal)
                || Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())))
                throw new InvalidOperationException("测试目录归属验证失败，保留目录。");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
