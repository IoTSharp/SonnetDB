using System.Text.Json.Serialization;

namespace SonnetDB.Streaming;

/// <summary>已经关闭的事件时间窗口收到新事件时的处理策略。</summary>
public enum StreamingWindowLateEventPolicy
{
    /// <summary>丢弃事件并持久记录丢弃数量。</summary>
    Drop = 0,

    /// <summary>拒绝整个批次，保留订阅中的未确认事件。</summary>
    Reject = 1,
}

/// <summary>以 Unix epoch 为边界的固定 UTC 滚动或显式滑动窗口定义，默认只计数。</summary>
/// <param name="FormatVersion">定义格式版本。</param>
/// <param name="SubscriptionId">所属订阅标识。</param>
/// <param name="StreamName">所属事件流名称。</param>
/// <param name="WindowSizeMilliseconds">固定窗口长度，单位为整数毫秒。</param>
/// <param name="AllowedLatenessMilliseconds">关闭窗口前允许的迟到时间，单位为整数毫秒。</param>
/// <param name="LateEventPolicy">已关闭窗口的新事件处理策略。</param>
public sealed record StreamingWindowDefinition(
    int FormatVersion,
    string SubscriptionId,
    string StreamName,
    long WindowSizeMilliseconds,
    long AllowedLatenessMilliseconds,
    StreamingWindowLateEventPolicy LateEventPolicy)
{
    /// <summary>当前窗口定义格式版本。</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>显式选择数值 JSON 属性的窗口定义格式版本。</summary>
    public const int NumericFormatVersion = 2;

    /// <summary>显式选择字符串分组键的窗口定义格式版本。</summary>
    public const int GroupedFormatVersion = 3;

    /// <summary>显式指定滑动步长的窗口定义格式版本。</summary>
    public const int SlidingFormatVersion = 4;

    /// <summary>单个事件最多所属的滑动窗口数量。</summary>
    public const int MaximumSlidingOverlap = 128;

    /// <summary>显式选择的顶层 JSON 数值属性，按 Ordinal 匹配；为空时保持原 COUNT 合同。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NumericField { get; init; }

    /// <summary>显式选择的顶层 JSON 字符串分组属性，按 Ordinal 匹配并保留键原值。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupField { get; init; }

    /// <summary>显式滑动步长，单位为整数毫秒；为空时保持原滚动窗口合同。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SlideMilliseconds { get; init; }

    /// <summary>创建并校验固定 UTC 滚动 COUNT 窗口定义。</summary>
    /// <param name="subscriptionId">所属订阅标识。</param>
    /// <param name="streamName">所属事件流名称。</param>
    /// <param name="windowSize">正整数毫秒的窗口长度，最大为一天。</param>
    /// <param name="allowedLateness">非负整数毫秒的允许迟到时间，最大为三十天。</param>
    /// <param name="lateEventPolicy">已关闭窗口的新事件处理策略。</param>
    /// <returns>已校验的窗口定义。</returns>
    public static StreamingWindowDefinition Create(
        string subscriptionId,
        string streamName,
        TimeSpan windowSize,
        TimeSpan? allowedLateness = null,
        StreamingWindowLateEventPolicy lateEventPolicy = StreamingWindowLateEventPolicy.Drop)
    {
        TimeSpan lateness = allowedLateness ?? TimeSpan.Zero;
        if (windowSize.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new ArgumentOutOfRangeException(nameof(windowSize), "窗口长度必须为整数毫秒。");
        if (lateness.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new ArgumentOutOfRangeException(nameof(allowedLateness), "允许迟到时间必须为整数毫秒。");
        var definition = new StreamingWindowDefinition(
            CurrentFormatVersion, subscriptionId, streamName,
            windowSize.Ticks / TimeSpan.TicksPerMillisecond,
            lateness.Ticks / TimeSpan.TicksPerMillisecond, lateEventPolicy);
        definition.Validate();
        return definition;
    }

    /// <summary>创建同时提供 COUNT、精确 decimal SUM/MIN/MAX 和 decimal AVG 的固定 UTC 窗口定义。</summary>
    /// <param name="subscriptionId">所属订阅标识。</param>
    /// <param name="streamName">所属事件流名称。</param>
    /// <param name="windowSize">正整数毫秒的窗口长度，最大为一天。</param>
    /// <param name="numericField">顶层 JSON 数值属性原名，最长为二百五十六个字符。</param>
    /// <param name="allowedLateness">非负整数毫秒的允许迟到时间，最大为三十天。</param>
    /// <param name="lateEventPolicy">已关闭窗口的新事件处理策略。</param>
    /// <returns>已校验的数值窗口定义。</returns>
    /// <remarks>缺失、null、非数值、重复属性、无法精确表示的 decimal 或 SUM 会拒绝整个批次；AVG 使用 decimal 除法舍入。</remarks>
    public static StreamingWindowDefinition CreateNumeric(
        string subscriptionId,
        string streamName,
        TimeSpan windowSize,
        string numericField,
        TimeSpan? allowedLateness = null,
        StreamingWindowLateEventPolicy lateEventPolicy = StreamingWindowLateEventPolicy.Drop)
    {
        StreamingWindowDefinition definition = Create(subscriptionId, streamName, windowSize, allowedLateness, lateEventPolicy) with
        {
            FormatVersion = NumericFormatVersion,
            NumericField = numericField,
        };
        definition.Validate();
        return definition;
    }

    /// <summary>创建按顶层 JSON 字符串键分组的固定 UTC 滚动 COUNT 窗口定义。</summary>
    /// <param name="subscriptionId">所属订阅标识。</param>
    /// <param name="streamName">所属事件流名称。</param>
    /// <param name="windowSize">正整数毫秒的窗口长度，最大为一天。</param>
    /// <param name="groupField">顶层 JSON 字符串属性原名，最长为二百五十六个字符。</param>
    /// <param name="allowedLateness">非负整数毫秒的允许迟到时间，最大为三十天。</param>
    /// <param name="lateEventPolicy">已关闭窗口的新事件处理策略。</param>
    /// <returns>已校验的分组窗口定义。</returns>
    /// <remarks>分组键保留原值并按 Ordinal 比较，允许空字符串；键最多二百五十六个 UTF-16 字符。</remarks>
    public static StreamingWindowDefinition CreateGrouped(
        string subscriptionId,
        string streamName,
        TimeSpan windowSize,
        string groupField,
        TimeSpan? allowedLateness = null,
        StreamingWindowLateEventPolicy lateEventPolicy = StreamingWindowLateEventPolicy.Drop)
    {
        StreamingWindowDefinition definition = Create(subscriptionId, streamName, windowSize, allowedLateness, lateEventPolicy) with
        {
            FormatVersion = GroupedFormatVersion,
            GroupField = groupField,
        };
        definition.Validate();
        return definition;
    }

    /// <summary>创建按字符串键分组并提供精确 decimal SUM/MIN/MAX 与 decimal AVG 的窗口定义。</summary>
    /// <param name="subscriptionId">所属订阅标识。</param>
    /// <param name="streamName">所属事件流名称。</param>
    /// <param name="windowSize">正整数毫秒的窗口长度，最大为一天。</param>
    /// <param name="groupField">顶层 JSON 字符串分组属性原名。</param>
    /// <param name="numericField">与分组属性不同的顶层 JSON 数值属性原名。</param>
    /// <param name="allowedLateness">非负整数毫秒的允许迟到时间，最大为三十天。</param>
    /// <param name="lateEventPolicy">已关闭窗口的新事件处理策略。</param>
    /// <returns>已校验的分组数值窗口定义。</returns>
    public static StreamingWindowDefinition CreateGroupedNumeric(
        string subscriptionId,
        string streamName,
        TimeSpan windowSize,
        string groupField,
        string numericField,
        TimeSpan? allowedLateness = null,
        StreamingWindowLateEventPolicy lateEventPolicy = StreamingWindowLateEventPolicy.Drop)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(numericField);
        StreamingWindowDefinition definition = CreateGrouped(subscriptionId, streamName, windowSize, groupField, allowedLateness, lateEventPolicy) with
        {
            NumericField = numericField,
        };
        definition.Validate();
        return definition;
    }

    /// <summary>创建以 Unix epoch 对齐且有界重叠的固定 UTC 滑动 COUNT 窗口定义。</summary>
    /// <param name="subscriptionId">所属订阅标识。</param>
    /// <param name="streamName">所属事件流名称。</param>
    /// <param name="windowSize">正整数毫秒的窗口长度，最大为一天。</param>
    /// <param name="slide">正整数毫秒的滑动步长，不得超过窗口长度；每事件重叠最多一百二十八项。</param>
    /// <param name="allowedLateness">非负整数毫秒的允许迟到时间，最大为三十天。</param>
    /// <param name="lateEventPolicy">已关闭窗口的新事件处理策略。</param>
    /// <returns>已校验的滑动窗口定义。</returns>
    /// <remarks>Drop 仅跳过已经关闭的目标窗口，全部目标关闭时才累计一个丢弃事件；Reject 在任何目标关闭时拒绝整批。</remarks>
    public static StreamingWindowDefinition CreateSliding(
        string subscriptionId,
        string streamName,
        TimeSpan windowSize,
        TimeSpan slide,
        TimeSpan? allowedLateness = null,
        StreamingWindowLateEventPolicy lateEventPolicy = StreamingWindowLateEventPolicy.Drop)
    {
        if (slide.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new ArgumentOutOfRangeException(nameof(slide), "滑动步长必须为整数毫秒。");
        StreamingWindowDefinition definition = Create(subscriptionId, streamName, windowSize, allowedLateness, lateEventPolicy) with
        {
            FormatVersion = SlidingFormatVersion,
            SlideMilliseconds = slide.Ticks / TimeSpan.TicksPerMillisecond,
        };
        definition.Validate();
        return definition;
    }

    /// <summary>创建提供精确 decimal SUM/MIN/MAX 与 decimal AVG 的固定 UTC 滑动窗口定义。</summary>
    /// <param name="subscriptionId">所属订阅标识。</param>
    /// <param name="streamName">所属事件流名称。</param>
    /// <param name="windowSize">正整数毫秒的窗口长度，最大为一天。</param>
    /// <param name="slide">正整数毫秒的滑动步长；每事件重叠最多一百二十八项。</param>
    /// <param name="numericField">顶层 JSON 数值属性原名，最长为二百五十六个字符。</param>
    /// <param name="allowedLateness">非负整数毫秒的允许迟到时间，最大为三十天。</param>
    /// <param name="lateEventPolicy">已关闭窗口的新事件处理策略。</param>
    /// <returns>已校验的滑动数值窗口定义。</returns>
    public static StreamingWindowDefinition CreateSlidingNumeric(
        string subscriptionId,
        string streamName,
        TimeSpan windowSize,
        TimeSpan slide,
        string numericField,
        TimeSpan? allowedLateness = null,
        StreamingWindowLateEventPolicy lateEventPolicy = StreamingWindowLateEventPolicy.Drop)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(numericField);
        StreamingWindowDefinition definition = CreateSliding(subscriptionId, streamName, windowSize, slide, allowedLateness, lateEventPolicy) with
        {
            NumericField = numericField,
        };
        definition.Validate();
        return definition;
    }

    /// <summary>创建按顶层 JSON 字符串键分组的固定 UTC 滑动 COUNT 窗口定义。</summary>
    /// <param name="subscriptionId">所属订阅标识。</param>
    /// <param name="streamName">所属事件流名称。</param>
    /// <param name="windowSize">正整数毫秒的窗口长度，最大为一天。</param>
    /// <param name="slide">正整数毫秒的滑动步长；每事件重叠最多一百二十八项。</param>
    /// <param name="groupField">顶层 JSON 字符串分组属性原名。</param>
    /// <param name="allowedLateness">非负整数毫秒的允许迟到时间，最大为三十天。</param>
    /// <param name="lateEventPolicy">已关闭窗口的新事件处理策略。</param>
    /// <returns>已校验的分组滑动窗口定义。</returns>
    public static StreamingWindowDefinition CreateSlidingGrouped(
        string subscriptionId,
        string streamName,
        TimeSpan windowSize,
        TimeSpan slide,
        string groupField,
        TimeSpan? allowedLateness = null,
        StreamingWindowLateEventPolicy lateEventPolicy = StreamingWindowLateEventPolicy.Drop)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupField);
        StreamingWindowDefinition definition = CreateSliding(subscriptionId, streamName, windowSize, slide, allowedLateness, lateEventPolicy) with
        {
            GroupField = groupField,
        };
        definition.Validate();
        return definition;
    }

    /// <summary>创建按字符串键分组并提供精确 decimal 数值聚合的固定 UTC 滑动窗口定义。</summary>
    /// <param name="subscriptionId">所属订阅标识。</param>
    /// <param name="streamName">所属事件流名称。</param>
    /// <param name="windowSize">正整数毫秒的窗口长度，最大为一天。</param>
    /// <param name="slide">正整数毫秒的滑动步长；每事件重叠最多一百二十八项。</param>
    /// <param name="groupField">顶层 JSON 字符串分组属性原名。</param>
    /// <param name="numericField">与分组属性不同的顶层 JSON 数值属性原名。</param>
    /// <param name="allowedLateness">非负整数毫秒的允许迟到时间，最大为三十天。</param>
    /// <param name="lateEventPolicy">已关闭窗口的新事件处理策略。</param>
    /// <returns>已校验的分组滑动数值窗口定义。</returns>
    public static StreamingWindowDefinition CreateSlidingGroupedNumeric(
        string subscriptionId,
        string streamName,
        TimeSpan windowSize,
        TimeSpan slide,
        string groupField,
        string numericField,
        TimeSpan? allowedLateness = null,
        StreamingWindowLateEventPolicy lateEventPolicy = StreamingWindowLateEventPolicy.Drop)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(numericField);
        StreamingWindowDefinition definition = CreateSlidingGrouped(subscriptionId, streamName, windowSize, slide, groupField, allowedLateness, lateEventPolicy) with
        {
            NumericField = numericField,
        };
        definition.Validate();
        return definition;
    }

    /// <summary>校验版本、订阅身份和窗口时间边界。</summary>
    public void Validate()
    {
        if (FormatVersion is not (CurrentFormatVersion or NumericFormatVersion or GroupedFormatVersion or SlidingFormatVersion))
            throw new InvalidDataException($"不支持窗口定义格式版本 {FormatVersion}。");
        if (FormatVersion == CurrentFormatVersion && NumericField is not null)
            throw new InvalidDataException("COUNT 版本不能包含数值属性选择。");
        if (FormatVersion is not (GroupedFormatVersion or SlidingFormatVersion) && GroupField is not null)
            throw new InvalidDataException("未分组窗口版本不能包含分组属性选择。");
        if (FormatVersion == GroupedFormatVersion || GroupField is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(GroupField);
            if (GroupField.Length > 256)
                throw new ArgumentException("窗口分组属性超过长度上限。", nameof(GroupField));
            if (GroupField == NumericField)
                throw new ArgumentException("分组与数值属性必须不同。", nameof(NumericField));
        }
        if (FormatVersion == NumericFormatVersion || NumericField is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(NumericField);
            if (NumericField.Length > 256)
                throw new ArgumentException("窗口数值属性超过长度上限。", nameof(NumericField));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(SubscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(StreamName);
        if (SubscriptionId.Length > 256 || StreamName.Length > 4096)
            throw new ArgumentException("窗口订阅或流标识超过长度上限。");
        ArgumentOutOfRangeException.ThrowIfLessThan(WindowSizeMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(WindowSizeMilliseconds, 86_400_000);
        if (FormatVersion == SlidingFormatVersion)
        {
            if (SlideMilliseconds is not { } slide)
                throw new InvalidDataException("滑动窗口定义必须显式包含滑动步长。");
            ArgumentOutOfRangeException.ThrowIfLessThan(slide, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(slide, WindowSizeMilliseconds);
            if ((WindowSizeMilliseconds - 1) / slide + 1 > MaximumSlidingOverlap)
                throw new ArgumentOutOfRangeException(nameof(SlideMilliseconds), "每个事件的滑动窗口重叠数量超过一百二十八项。");
        }
        else if (SlideMilliseconds is not null)
        {
            throw new InvalidDataException("滚动窗口版本不能包含滑动步长。");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(AllowedLatenessMilliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(AllowedLatenessMilliseconds, 2_592_000_000);
        if (!Enum.IsDefined(LateEventPolicy))
            throw new InvalidDataException("未知的窗口迟到策略。");
    }
}

/// <summary>本地窗口聚合器的容量、单批处理和操作超时边界。</summary>
public sealed record StreamingWindowOptions
{
    /// <summary>保留的最大窗口结果数量，分组时每个起点与键的组合计一项，最高为一万个。</summary>
    public int MaxWindows { get; init; } = 10_000;

    /// <summary>单次应用的最大事件数量，最高为一万个。</summary>
    public int MaxBatchEvents { get; init; } = 1_000;

    /// <summary>单批完整事件 JSON 的最大字节数，最高为十六 MiB。</summary>
    public int MaxBatchBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>状态文件的最大字节数，最高为十六 MiB。</summary>
    public int MaxStateBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>每次操作与闸门等待的总超时毫秒数，范围为五十毫秒至五分钟。</summary>
    public int OperationTimeoutMilliseconds { get; init; } = 10_000;

    /// <summary>显式分组窗口保留的不同键数量上限，最高为一万个；未分组版本必须为空。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxGroups { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxWindows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxWindows, 10_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBatchEvents, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBatchEvents, 10_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBatchBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBatchBytes, 16 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxStateBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxStateBytes, 16 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(OperationTimeoutMilliseconds, 50);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(OperationTimeoutMilliseconds, 300_000);
        if (MaxGroups is { } groups)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(groups, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(groups, 10_000);
        }
    }
}

/// <summary>一个固定 UTC 窗口的持久 COUNT 结果。</summary>
/// <param name="StartUtc">包含的窗口起点。</param>
/// <param name="EndUtc">不包含的窗口终点。</param>
/// <param name="Count">已应用且未被迟到策略丢弃的事件数量。</param>
/// <param name="IsClosed">watermark 是否已经超过窗口终点加允许迟到时间。</param>
public sealed record StreamingWindowCount(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    long Count,
    bool IsClosed);

/// <summary>窗口结果和恢复位点的持久状态快照。</summary>
/// <param name="AppliedSequence">最近已应用的订阅事件序号。</param>
/// <param name="AppliedRevision">最近已应用的订阅批次检查点版本。</param>
/// <param name="WatermarkUtc">单调递增的窗口关闭 watermark。</param>
/// <param name="DroppedLateEvents">已持久记录的关闭窗口迟到丢弃数量。</param>
/// <param name="RetainedWindowCount">尚未移除的窗口数量。</param>
public sealed record StreamingWindowState(
    long AppliedSequence,
    long AppliedRevision,
    DateTimeOffset WatermarkUtc,
    long DroppedLateEvents,
    int RetainedWindowCount);

/// <summary>按窗口起点排序的一次有界结果读取。</summary>
/// <param name="Windows">本次返回的窗口结果。</param>
/// <param name="NextStartUtc">继续读取时传入的排他窗口起点。</param>
/// <param name="HasMore">同一筛选条件下是否仍有后续窗口。</param>
/// <param name="State">读取时对应的一致恢复状态。</param>
public sealed record StreamingWindowBatch(
    IReadOnlyList<StreamingWindowCount> Windows,
    DateTimeOffset? NextStartUtc,
    bool HasMore,
    StreamingWindowState State);

/// <summary>一个固定 UTC 窗口的 decimal 数值聚合结果。</summary>
/// <param name="StartUtc">包含的窗口起点。</param>
/// <param name="EndUtc">不包含的窗口终点。</param>
/// <param name="Count">已应用且未被迟到策略丢弃的事件数量。</param>
/// <param name="Sum">没有舍入的精确 decimal 总和。</param>
/// <param name="Min">窗口数值最小值。</param>
/// <param name="Max">窗口数值最大值。</param>
/// <param name="Average">总和除以数量的 decimal 结果，除不尽时按 decimal 除法舍入。</param>
/// <param name="IsClosed">watermark 是否已经超过窗口终点加允许迟到时间。</param>
public sealed record StreamingWindowNumeric(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    long Count,
    decimal Sum,
    decimal Min,
    decimal Max,
    decimal Average,
    bool IsClosed);

/// <summary>按窗口起点排序的一次有界数值结果读取。</summary>
/// <param name="Windows">本次返回的窗口结果。</param>
/// <param name="NextStartUtc">继续读取时传入的排他窗口起点。</param>
/// <param name="HasMore">同一筛选条件下是否仍有后续窗口。</param>
/// <param name="State">读取时对应的一致恢复状态。</param>
public sealed record StreamingWindowNumericBatch(
    IReadOnlyList<StreamingWindowNumeric> Windows,
    DateTimeOffset? NextStartUtc,
    bool HasMore,
    StreamingWindowState State);

/// <summary>分组窗口分页使用的排他位置，按 UTC 起点再按 Ordinal 键原值排序。</summary>
/// <param name="StartUtc">上页最后一个窗口的 UTC 起点。</param>
/// <param name="GroupKey">上页最后一个窗口的原始字符串键。</param>
public sealed record StreamingGroupedWindowCursor(DateTimeOffset StartUtc, string GroupKey);

/// <summary>一个字符串键与固定 UTC 窗口的持久聚合结果。</summary>
/// <param name="StartUtc">包含的窗口起点。</param>
/// <param name="EndUtc">不包含的窗口终点。</param>
/// <param name="GroupKey">保留原值的字符串分组键。</param>
/// <param name="Count">已应用且未被迟到策略丢弃的事件数量。</param>
/// <param name="Sum">显式数值模式的精确总和，否则为空。</param>
/// <param name="Min">显式数值模式的最小值，否则为空。</param>
/// <param name="Max">显式数值模式的最大值，否则为空。</param>
/// <param name="Average">显式数值模式的 decimal 平均值，否则为空。</param>
/// <param name="IsClosed">窗口是否已由 watermark 关闭。</param>
public sealed record StreamingGroupedWindow(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string GroupKey,
    long Count,
    decimal? Sum,
    decimal? Min,
    decimal? Max,
    decimal? Average,
    bool IsClosed);

/// <summary>按 UTC 起点与 Ordinal 字符串键排序的有界分组结果页。</summary>
/// <param name="Windows">本次返回的分组窗口。</param>
/// <param name="NextCursor">继续读取的排他位置，空页时为空。</param>
/// <param name="HasMore">同一筛选条件下是否还有结果。</param>
/// <param name="State">读取时的一致恢复状态，其中窗口数量为保留的分组结果数量。</param>
public sealed record StreamingGroupedWindowBatch(
    IReadOnlyList<StreamingGroupedWindow> Windows,
    StreamingGroupedWindowCursor? NextCursor,
    bool HasMore,
    StreamingWindowState State);

internal sealed record StreamingWindowNumericAccumulator(
    [property: JsonRequired] decimal Sum,
    [property: JsonRequired] decimal Min,
    [property: JsonRequired] decimal Max);

internal sealed record StreamingGroupedWindowAccumulator(
    [property: JsonRequired] long StartTicks,
    [property: JsonRequired] string GroupKey,
    [property: JsonRequired] long Count,
    [property: JsonRequired] StreamingWindowNumericAccumulator? Numeric);

internal sealed record StreamingWindowDocument(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] StreamingWindowDefinition Definition,
    [property: JsonRequired] StreamingWindowOptions Options,
    [property: JsonRequired] long AppliedSequence,
    [property: JsonRequired] long AppliedRevision,
    [property: JsonRequired] DateTimeOffset WatermarkUtc,
    [property: JsonRequired] long DroppedLateEvents,
    [property: JsonRequired] string? LastDeliveryId,
    [property: JsonRequired] string? LastBatchHash,
    [property: JsonRequired] Dictionary<long, long> Windows)
{
    // 空值必须从旧版本 JSON 中省略，使 COUNT 的既有字节形状及 SHA-256 保持不变。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<long, StreamingWindowNumericAccumulator>? NumericWindows { get; init; }

    // 仅显式版本 3 保存此字段；旧版本序列化和原始哈希不增加字段。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StreamingGroupedWindowAccumulator>? GroupedWindows { get; init; }
}

internal sealed record StreamingWindowEnvelope(
    [property: JsonRequired] StreamingWindowDocument State,
    [property: JsonRequired] string Sha256);

// 版本 1 DTO 固定原 COUNT 字段顺序和形状，必须先核对其原哈希，不能补新字段后再校验。
internal sealed record LegacyStreamingWindowDefinition(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] string SubscriptionId,
    [property: JsonRequired] string StreamName,
    [property: JsonRequired] long WindowSizeMilliseconds,
    [property: JsonRequired] long AllowedLatenessMilliseconds,
    [property: JsonRequired] StreamingWindowLateEventPolicy LateEventPolicy);

internal sealed record LegacyStreamingWindowDocument(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] LegacyStreamingWindowDefinition Definition,
    [property: JsonRequired] StreamingWindowOptions Options,
    [property: JsonRequired] long AppliedSequence,
    [property: JsonRequired] long AppliedRevision,
    [property: JsonRequired] DateTimeOffset WatermarkUtc,
    [property: JsonRequired] long DroppedLateEvents,
    [property: JsonRequired] string? LastDeliveryId,
    [property: JsonRequired] string? LastBatchHash,
    [property: JsonRequired] Dictionary<long, long> Windows);

internal sealed record LegacyStreamingWindowEnvelope(
    [property: JsonRequired] LegacyStreamingWindowDocument State,
    [property: JsonRequired] string Sha256);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(StreamingWindowEnvelope))]
[JsonSerializable(typeof(StreamingWindowDocument))]
[JsonSerializable(typeof(StreamingWindowDefinition))]
[JsonSerializable(typeof(StreamingWindowOptions))]
[JsonSerializable(typeof(LegacyStreamingWindowEnvelope))]
[JsonSerializable(typeof(LegacyStreamingWindowDocument))]
internal sealed partial class StreamingWindowJsonContext : JsonSerializerContext;
