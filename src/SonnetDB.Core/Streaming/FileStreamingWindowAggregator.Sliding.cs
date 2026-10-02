using System.Text.Json;

namespace SonnetDB.Streaming;

public sealed partial class FileStreamingWindowAggregator
{
    private List<long> OpenWindowStarts(StreamingEvent value, CancellationToken token)
    {
        long windowTicks = checked(Definition.WindowSizeMilliseconds * TimeSpan.TicksPerMillisecond);
        long slideTicks = checked((Definition.SlideMilliseconds ?? Definition.WindowSizeMilliseconds) * TimeSpan.TicksPerMillisecond);
        int maximumOverlap = checked((int)((windowTicks - 1) / slideTicks + 1));
        long latestStart = WindowStart(value.EventTimeUtc);
        var starts = new List<long>(maximumOverlap);
        // 定义保证最多 128 项；整次应用另有 MaxBatchEvents、操作 deadline 和调用方取消边界。
        for (int overlap = 0; overlap < maximumOverlap; overlap++)
        {
            token.ThrowIfCancellationRequested();
            long startTicks = checked(latestStart - overlap * slideTicks);
            long endTicks = checked(startTicks + windowTicks);
            if (value.EventTimeUtc.UtcTicks >= endTicks)
                break; // [start, end) 不包括终点，非整倍数的 size/slide 可能少一个成员。
            if (startTicks < DateTime.MinValue.Ticks || endTicks > DateTime.MaxValue.Ticks)
                throw new ArgumentOutOfRangeException(nameof(value), "事件所属完整窗口超出 UTC 可表示范围。");
            if (IsClosed(new DateTimeOffset(endTicks, TimeSpan.Zero), _document.WatermarkUtc))
            {
                if (Definition.LateEventPolicy == StreamingWindowLateEventPolicy.Reject)
                    throw new StreamingLateEventException(value.EventId);
                continue;
            }
            starts.Add(startTicks);
        }

        return starts;
    }

    private static void ValidateSlidingJsonRequiredFields(JsonElement state)
    {
        if (!state.TryGetProperty("definition", out JsonElement definition) || definition.ValueKind != JsonValueKind.Object
            || !definition.TryGetProperty("formatVersion", out _)
            || !definition.TryGetProperty("subscriptionId", out _)
            || !definition.TryGetProperty("streamName", out _)
            || !definition.TryGetProperty("windowSizeMilliseconds", out _)
            || !definition.TryGetProperty("allowedLatenessMilliseconds", out _)
            || !definition.TryGetProperty("lateEventPolicy", out _)
            || !definition.TryGetProperty("slideMilliseconds", out JsonElement slide)
            || slide.ValueKind != JsonValueKind.Number)
            throw new InvalidDataException("持久滑动窗口状态缺少必需定义字段或有效滑动步长，不能使用默认值恢复。");
        if (!state.TryGetProperty("options", out JsonElement options) || options.ValueKind != JsonValueKind.Object
            || !options.TryGetProperty("maxWindows", out _)
            || !options.TryGetProperty("maxBatchEvents", out _)
            || !options.TryGetProperty("maxBatchBytes", out _)
            || !options.TryGetProperty("maxStateBytes", out _)
            || !options.TryGetProperty("operationTimeoutMilliseconds", out _))
            throw new InvalidDataException("持久滑动窗口状态缺少必需容量字段，不能使用默认值恢复。");

        bool grouped = definition.TryGetProperty("groupField", out JsonElement groupField);
        if (definition.TryGetProperty("numericField", out JsonElement numericField) && numericField.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("持久滑动数值窗口选择必须为字符串，不能以空字段恢复。");
        if (grouped)
        {
            if (groupField.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("持久滑动分组窗口选择必须为字符串，不能以空字段恢复。");
            ValidateGroupedJsonRequiredFields(state);
        }
        else if (state.TryGetProperty("groupedWindows", out _) || options.TryGetProperty("maxGroups", out _))
        {
            throw new InvalidDataException("未分组滑动窗口状态不能包含分组字段，即使字段值为空。");
        }
    }
}
