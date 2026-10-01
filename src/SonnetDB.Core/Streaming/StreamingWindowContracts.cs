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

/// <summary>以 Unix epoch 为边界的固定 UTC 滚动 COUNT 窗口定义。</summary>
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

    /// <summary>校验版本、订阅身份和窗口时间边界。</summary>
    public void Validate()
    {
        if (FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException($"不支持窗口定义格式版本 {FormatVersion}。");
        ArgumentException.ThrowIfNullOrWhiteSpace(SubscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(StreamName);
        if (SubscriptionId.Length > 256 || StreamName.Length > 4096)
            throw new ArgumentException("窗口订阅或流标识超过长度上限。");
        ArgumentOutOfRangeException.ThrowIfLessThan(WindowSizeMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(WindowSizeMilliseconds, 86_400_000);
        ArgumentOutOfRangeException.ThrowIfNegative(AllowedLatenessMilliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(AllowedLatenessMilliseconds, 2_592_000_000);
        if (!Enum.IsDefined(LateEventPolicy))
            throw new InvalidDataException("未知的窗口迟到策略。");
    }
}

/// <summary>本地窗口聚合器的容量、单批处理和操作超时边界。</summary>
public sealed record StreamingWindowOptions
{
    /// <summary>保留的最大窗口数量，包含尚未删除的已关闭窗口，最高为一万个。</summary>
    public int MaxWindows { get; init; } = 10_000;

    /// <summary>单次应用的最大事件数量，最高为一万个。</summary>
    public int MaxBatchEvents { get; init; } = 1_000;

    /// <summary>单批完整事件 JSON 的最大字节数，最高为十六 MiB。</summary>
    public int MaxBatchBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>状态文件的最大字节数，最高为十六 MiB。</summary>
    public int MaxStateBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>每次操作与闸门等待的总超时毫秒数，范围为五十毫秒至五分钟。</summary>
    public int OperationTimeoutMilliseconds { get; init; } = 10_000;

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
    [property: JsonRequired] Dictionary<long, long> Windows);

internal sealed record StreamingWindowEnvelope(
    [property: JsonRequired] StreamingWindowDocument State,
    [property: JsonRequired] string Sha256);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(StreamingWindowEnvelope))]
[JsonSerializable(typeof(StreamingWindowDocument))]
[JsonSerializable(typeof(StreamingWindowDefinition))]
[JsonSerializable(typeof(StreamingWindowOptions))]
internal sealed partial class StreamingWindowJsonContext : JsonSerializerContext;
