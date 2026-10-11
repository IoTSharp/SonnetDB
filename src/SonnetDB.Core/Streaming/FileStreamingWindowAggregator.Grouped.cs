using System.Text.Json;

namespace SonnetDB.Streaming;

public sealed partial class FileStreamingWindowAggregator
{
    /// <summary>按 UTC 窗口起点及 Ordinal 字符串键有界读取分组 COUNT 或数值结果。</summary>
    /// <param name="maxWindows">返回的最大分组结果数量，不能超过配置的 MaxWindows。</param>
    /// <param name="after">排他位置；为空时从第一个结果开始。</param>
    /// <param name="closedOnly">是否只返回已关闭窗口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>有界结果及读取时对应的一致恢复状态。</returns>
    /// <remarks>每页读取当前提交状态；跨页一致读取时调用方必须停止应用、推进 watermark 或清理结果。</remarks>
    public async ValueTask<StreamingGroupedWindowBatch> ReadGroupedWindowsAsync(
        int maxWindows = 100,
        StreamingGroupedWindowCursor? after = null,
        bool closedOnly = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWindows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxWindows, Options.MaxWindows);
        if (after is not null)
        {
            ValidateUtc(after.StartUtc, nameof(after));
            ArgumentNullException.ThrowIfNull(after.GroupKey);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(after.GroupKey.Length, 256);
        }

        return await ExecuteAsync(token =>
        {
            if (_document.GroupedWindows is not { } windows)
                throw new InvalidOperationException("必须显式创建分组窗口才能读取分组结果。");
            var results = new List<StreamingGroupedWindow>(maxWindows);
            bool hasMore = false;
            // 持久列表始终按相同合同排序；最多扫描 MaxWindows 项并观察操作 deadline。
            foreach (StreamingGroupedWindowAccumulator window in windows)
            {
                token.ThrowIfCancellationRequested();
                DateTimeOffset end = WindowEnd(window.StartTicks);
                bool closed = IsClosed(end, _document.WatermarkUtc);
                if ((after is not null && CompareGroupPosition(window.StartTicks, window.GroupKey, after.StartUtc.UtcTicks, after.GroupKey) <= 0)
                    || (closedOnly && !closed))
                    continue;
                if (results.Count == maxWindows)
                {
                    hasMore = true;
                    break;
                }

                StreamingWindowNumericAccumulator? numeric = window.Numeric;
                results.Add(new StreamingGroupedWindow(new DateTimeOffset(window.StartTicks, TimeSpan.Zero),
                    end, window.GroupKey, window.Count, numeric?.Sum, numeric?.Min, numeric?.Max,
                    numeric is null ? null : numeric.Sum / window.Count, closed));
            }

            StreamingGroupedWindowCursor? cursor = results.Count == 0 ? null
                : new StreamingGroupedWindowCursor(results[^1].StartUtc, results[^1].GroupKey);
            return ValueTask.FromResult(new StreamingGroupedWindowBatch(results.AsReadOnly(), cursor, hasMore, State(_document)));
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ApplyGroupedCoreAsync(
        StreamingDeliveryBatch batch,
        StreamingSubscriptionCheckpoint checkpoint,
        StreamingEvent[] events,
        string batchHash,
        CancellationToken token)
    {
        var windows = new Dictionary<(long StartTicks, string Key), StreamingGroupedWindowAccumulator>();
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (StreamingGroupedWindowAccumulator window in _document.GroupedWindows!)
        {
            token.ThrowIfCancellationRequested();
            windows.Add((window.StartTicks, window.GroupKey), window);
            groups.Add(window.GroupKey);
        }

        long dropped = _document.DroppedLateEvents;
        foreach (StreamingEvent value in events)
        {
            token.ThrowIfCancellationRequested();
            List<long> starts = OpenWindowStarts(value, token);
            if (starts.Count == 0)
            {
                dropped = checked(dropped + 1);
                continue;
            }

            string key = ReadGroupKey(value.Payload, Definition.GroupField!, token);
            decimal numericValue = Definition.NumericField is { } field ? ReadNumericValue(value.Payload, field, token) : 0;
            foreach (long startTicks in starts)
            {
                token.ThrowIfCancellationRequested();
                if (windows.TryGetValue((startTicks, key), out StreamingGroupedWindowAccumulator? existing))
                {
                    StreamingWindowNumericAccumulator? numeric = existing.Numeric;
                    windows[(startTicks, key)] = existing with
                    {
                        Count = checked(existing.Count + 1),
                        Numeric = numeric is null ? null : new StreamingWindowNumericAccumulator(
                            AddExact(numeric.Sum, numericValue), Math.Min(numeric.Min, numericValue), Math.Max(numeric.Max, numericValue)),
                    };
                }
                else
                {
                    if (windows.Count >= Options.MaxWindows)
                        throw new InvalidOperationException("持久分组窗口结果容量已满；移除已关闭结果后重试批次。");
                    if (!groups.Contains(key) && groups.Count >= Options.MaxGroups!.Value)
                        throw new InvalidOperationException("持久分组键容量已满；移除该键的全部已关闭结果后重试批次。");
                    groups.Add(key);
                    windows.Add((startTicks, key), new StreamingGroupedWindowAccumulator(startTicks, key, 1,
                        Definition.NumericField is null ? null : new StreamingWindowNumericAccumulator(numericValue, numericValue, numericValue)));
                }
            }
        }

        token.ThrowIfCancellationRequested();
        List<StreamingGroupedWindowAccumulator> sorted = windows.Values.OrderBy(static window => window.StartTicks)
            .ThenBy(static window => window.GroupKey, StringComparer.Ordinal).ToList();
        token.ThrowIfCancellationRequested();
        await CommitAsync(_document with
        {
            AppliedSequence = checkpoint.CommittedSequence,
            AppliedRevision = checkpoint.Revision,
            DroppedLateEvents = dropped,
            LastDeliveryId = batch.DeliveryId,
            LastBatchHash = batchHash,
            GroupedWindows = sorted,
        }, token).ConfigureAwait(false);
    }

    private async ValueTask<int> RemoveClosedGroupedWindowsAsync(DateTimeOffset throughStartUtc, CancellationToken token)
    {
        List<StreamingGroupedWindowAccumulator> retained = new(_document.GroupedWindows!.Count);
        int removed = 0;
        foreach (StreamingGroupedWindowAccumulator window in _document.GroupedWindows)
        {
            token.ThrowIfCancellationRequested();
            if (window.StartTicks <= throughStartUtc.UtcTicks && IsClosed(WindowEnd(window.StartTicks), _document.WatermarkUtc))
                removed++;
            else
                retained.Add(window);
        }

        if (removed > 0)
            await CommitAsync(_document with { GroupedWindows = retained }, token).ConfigureAwait(false);
        return removed;
    }

    private static string ReadGroupKey(byte[] payload, string field, CancellationToken token)
    {
        try
        {
            var reader = new Utf8JsonReader(payload);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("分组窗口 payload 必须为 JSON 对象。");
            string? key = null;
            // 每个 JSON token 至少消耗一个 payload 字节；载荷总量和 deadline 都由外层限制。
            for (int tokenCount = 0; tokenCount < payload.Length && reader.Read(); tokenCount++)
            {
                token.ThrowIfCancellationRequested();
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    if (reader.Read())
                        throw new InvalidDataException("分组窗口 payload 不能包含多个 JSON 根值。");
                    return key ?? throw new InvalidDataException("分组窗口选择的顶层字符串属性缺失。");
                }

                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals(field))
                    continue;
                if (key is not null)
                    throw new InvalidDataException("分组窗口选择的顶层字符串属性重复。");
                if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    throw new InvalidDataException("分组窗口属性必须为 JSON 字符串。");
                key = reader.GetString()!;
                if (key.Length > 256)
                    throw new InvalidDataException("分组窗口字符串键超过二百五十六个 UTF-16 字符。");
            }

            throw new InvalidDataException("分组窗口 payload 不完整。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("分组窗口 payload JSON 无效。", exception);
        }
    }

    private static void ValidateGroupedDocument(StreamingWindowDocument document, CancellationToken token)
    {
        if (document.GroupedWindows!.Count > document.Options.MaxWindows)
            throw new InvalidDataException("持久分组窗口结果超过数量容量。");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        StreamingGroupedWindowAccumulator? previous = null;
        long windowTicks = checked(document.Definition.WindowSizeMilliseconds * TimeSpan.TicksPerMillisecond);
        long alignmentTicks = checked((document.Definition.SlideMilliseconds ?? document.Definition.WindowSizeMilliseconds) * TimeSpan.TicksPerMillisecond);
        foreach (StreamingGroupedWindowAccumulator window in document.GroupedWindows)
        {
            token.ThrowIfCancellationRequested();
            if (window is null || window.GroupKey is null || window.GroupKey.Length > 256 || window.Count < 1
                || window.StartTicks < DateTime.MinValue.Ticks || window.StartTicks > DateTime.MaxValue.Ticks - windowTicks
                || (window.StartTicks - DateTime.UnixEpoch.Ticks) % alignmentTicks != 0
                || (document.Definition.NumericField is null) != (window.Numeric is null)
                || (previous is not null && CompareGroupPosition(previous.StartTicks, previous.GroupKey, window.StartTicks, window.GroupKey) >= 0))
                throw new InvalidDataException("持久分组窗口边界、键、排序或 COUNT 无效。");
            if (window.Numeric is { } numeric && (numeric.Min > numeric.Max
                || numeric.Sum / window.Count < numeric.Min || numeric.Sum / window.Count > numeric.Max
                || (window.Count == 1 && (numeric.Sum != numeric.Min || numeric.Sum != numeric.Max))))
                throw new InvalidDataException("持久分组数值状态无效。");
            keys.Add(window.GroupKey);
            if (keys.Count > document.Options.MaxGroups!.Value)
                throw new InvalidDataException("持久分组窗口键超过数量容量。");
            previous = window;
        }
    }

    private static void ValidateGroupedJsonRequiredFields(JsonElement state)
    {
        // 仅 v3 要求持久定义与容量字段实际存在，不能将缺失字段按构造器默认值补回后核对哈希。
        if (!state.TryGetProperty("definition", out JsonElement definition) || definition.ValueKind != JsonValueKind.Object
            || !definition.TryGetProperty("formatVersion", out _)
            || !definition.TryGetProperty("subscriptionId", out _)
            || !definition.TryGetProperty("streamName", out _)
            || !definition.TryGetProperty("windowSizeMilliseconds", out _)
            || !definition.TryGetProperty("allowedLatenessMilliseconds", out _)
            || !definition.TryGetProperty("lateEventPolicy", out _)
            || !definition.TryGetProperty("groupField", out _))
            throw new InvalidDataException("持久分组窗口状态缺少必需定义字段，不能使用默认值恢复。");
        if (!state.TryGetProperty("options", out JsonElement options) || options.ValueKind != JsonValueKind.Object
            || !options.TryGetProperty("maxWindows", out _)
            || !options.TryGetProperty("maxBatchEvents", out _)
            || !options.TryGetProperty("maxBatchBytes", out _)
            || !options.TryGetProperty("maxStateBytes", out _)
            || !options.TryGetProperty("operationTimeoutMilliseconds", out _)
            || !options.TryGetProperty("maxGroups", out _))
            throw new InvalidDataException("持久分组窗口状态缺少必需容量字段，不能使用默认值恢复。");
    }

    private static void RejectDuplicateGroupedJsonProperties(byte[] bytes, CancellationToken token)
    {
        var reader = new Utf8JsonReader(bytes);
        var objects = new Stack<HashSet<string>>();
        // 文件已受 MaxStateBytes 限制；每个 token 至少占一个字节，且恢复操作 deadline 每步可取消。
        for (int inspected = 0; inspected < bytes.Length && reader.Read(); inspected++)
        {
            token.ThrowIfCancellationRequested();
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objects.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    objects.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (!objects.Peek().Add(reader.GetString()!))
                        throw new InvalidDataException("持久分组窗口状态包含重复 JSON 属性，不能按最后一个属性覆盖恢复。");
                    break;
            }
        }

        token.ThrowIfCancellationRequested();
    }

    private static int CompareGroupPosition(long leftTicks, string leftKey, long rightTicks, string rightKey)
    {
        int time = leftTicks.CompareTo(rightTicks);
        return time == 0 ? StringComparer.Ordinal.Compare(leftKey, rightKey) : time;
    }
}
