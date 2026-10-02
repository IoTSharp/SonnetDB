using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonnetDB.Streaming;

/// <summary>会话窗口收到已关闭区域事件时采用的策略。</summary>
public enum StreamingSessionLateEventPolicy
{
    /// <summary>丢弃事件并累计丢弃数量。</summary>
    Drop = 0,

    /// <summary>拒绝整个批次并保留未确认事件。</summary>
    Reject = 1,
}

/// <summary>持久会话 COUNT 窗口定义。</summary>
/// <param name="FormatVersion">定义格式版本。</param>
/// <param name="SubscriptionId">所属订阅标识。</param>
/// <param name="StreamName">所属事件流名称。</param>
/// <param name="InactivityGapMilliseconds">相邻事件之间允许的最大不活跃间隔（毫秒）。等于该值仍属于同一会话。</param>
/// <param name="AllowedLatenessMilliseconds">关闭会话前允许的迟到时间（毫秒）。</param>
/// <param name="LateEventPolicy">关闭会话收到事件时的处理策略。</param>
public sealed record StreamingSessionWindowDefinition(
    int FormatVersion,
    string SubscriptionId,
    string StreamName,
    long InactivityGapMilliseconds,
    long AllowedLatenessMilliseconds,
    StreamingSessionLateEventPolicy LateEventPolicy)
{
    /// <summary>当前定义格式版本。</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>不活跃间隔。</summary>
    [JsonIgnore]
    public TimeSpan InactivityGap => TimeSpan.FromMilliseconds(InactivityGapMilliseconds);

    /// <summary>允许迟到时间。</summary>
    [JsonIgnore]
    public TimeSpan AllowedLateness => TimeSpan.FromMilliseconds(AllowedLatenessMilliseconds);

    /// <summary>创建并校验会话窗口定义。</summary>
    /// <param name="subscriptionId">订阅标识。</param>
    /// <param name="streamName">事件流名称。</param>
    /// <param name="inactivityGap">相邻事件最大间隔；等于间隔仍桥接到同一会话。</param>
    /// <param name="allowedLateness">关闭会话前允许的迟到时间。</param>
    /// <param name="lateEventPolicy">迟到事件策略。</param>
    /// <returns>已校验定义。</returns>
    public static StreamingSessionWindowDefinition Create(
        string subscriptionId,
        string streamName,
        TimeSpan inactivityGap,
        TimeSpan? allowedLateness = null,
        StreamingSessionLateEventPolicy lateEventPolicy = StreamingSessionLateEventPolicy.Drop)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        TimeSpan lateness = allowedLateness ?? TimeSpan.Zero;
        if (inactivityGap.Ticks % TimeSpan.TicksPerMillisecond != 0 || inactivityGap < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(inactivityGap), "不活跃间隔必须为正整数毫秒。");
        if (lateness.Ticks % TimeSpan.TicksPerMillisecond != 0 || lateness < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(allowedLateness), "允许迟到必须为非负整数毫秒。");
        var definition = new StreamingSessionWindowDefinition(
            CurrentFormatVersion,
            subscriptionId,
            streamName,
            checked(inactivityGap.Ticks / TimeSpan.TicksPerMillisecond),
            checked(lateness.Ticks / TimeSpan.TicksPerMillisecond),
            lateEventPolicy);
        definition.Validate();
        return definition;
    }

    /// <summary>校验版本、标识及时间边界。</summary>
    public void Validate()
    {
        if (FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException($"不支持会话窗口定义格式版本 {FormatVersion}。");
        ArgumentException.ThrowIfNullOrWhiteSpace(SubscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(StreamName);
        ArgumentOutOfRangeException.ThrowIfLessThan(InactivityGapMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(InactivityGapMilliseconds, 2_592_000_000);
        ArgumentOutOfRangeException.ThrowIfNegative(AllowedLatenessMilliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(AllowedLatenessMilliseconds, 2_592_000_000);
        if (!Enum.IsDefined(LateEventPolicy))
            throw new InvalidDataException("未知的会话窗口迟到策略。");
    }
}

/// <summary>会话窗口的数量、状态字节和操作超时边界。</summary>
public sealed record StreamingSessionWindowOptions
{
    /// <summary>最多保留的会话数量。</summary>
    public int MaxSessions { get; init; } = 10_000;

    /// <summary>单批最多事件数量。</summary>
    public int MaxBatchEvents { get; init; } = 1_000;

    /// <summary>单批事件 JSON 最大字节数。</summary>
    public int MaxBatchBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>状态文件最大字节数。</summary>
    public int MaxStateBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>单次操作与等待的超时毫秒数。</summary>
    public int OperationTimeoutMilliseconds { get; init; } = 10_000;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSessions, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxSessions, 10_000);
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

/// <summary>一个持久会话 COUNT 结果。</summary>
/// <param name="StartUtc">会话首事件时间。</param>
/// <param name="EndUtc">会话末事件时间。</param>
/// <param name="Count">会话事件数量。</param>
/// <param name="IsClosed">按当前 watermark 判断是否关闭。</param>
public sealed record StreamingSessionWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc, long Count, bool IsClosed);

/// <summary>会话窗口读取结果及分页状态。</summary>
/// <param name="Windows">按起始时间排序的会话。</param>
/// <param name="NextStartUtc">下一页排他起点。</param>
/// <param name="HasMore">是否仍有下一页。</param>
/// <param name="State">读取时的状态快照。</param>
public sealed record StreamingSessionWindowBatch(
    IReadOnlyList<StreamingSessionWindow> Windows,
    DateTimeOffset? NextStartUtc,
    bool HasMore,
    StreamingSessionWindowState State);

/// <summary>会话聚合器的持久状态快照。</summary>
/// <param name="AppliedSequence">最近应用的事件序号。</param>
/// <param name="AppliedRevision">最近应用批次的检查点 revision。</param>
/// <param name="WatermarkUtc">单调 UTC watermark。</param>
/// <param name="DroppedLateEvents">累计丢弃的关闭会话事件数量。</param>
/// <param name="RetainedSessionCount">当前保留会话数量。</param>
public sealed record StreamingSessionWindowState(
    long AppliedSequence,
    long AppliedRevision,
    DateTimeOffset WatermarkUtc,
    long DroppedLateEvents,
    int RetainedSessionCount);

internal sealed record StreamingSessionMember(long StartTicks, long EndTicks, long Count);

internal sealed record StreamingSessionWindowDocument(
    int FormatVersion,
    StreamingSessionWindowDefinition Definition,
    StreamingSessionWindowOptions Options,
    long AppliedSequence,
    long AppliedRevision,
    DateTimeOffset WatermarkUtc,
    long DroppedLateEvents,
    DateTimeOffset? RetiredUntilUtc,
    string? LastDeliveryId,
    string? LastBatchHash,
    IReadOnlyList<StreamingSessionMember> Sessions);

internal sealed record StreamingSessionWindowEnvelope(StreamingSessionWindowDocument State, string Sha256);

/// <summary>会话窗口专用 Native AOT JSON 元数据。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StreamingSessionWindowDefinition))]
[JsonSerializable(typeof(StreamingSessionWindowOptions))]
[JsonSerializable(typeof(StreamingSessionWindowDocument))]
[JsonSerializable(typeof(StreamingSessionWindowEnvelope))]
[JsonSerializable(typeof(StreamingSessionMember))]
[JsonSerializable(typeof(IReadOnlyList<StreamingSessionMember>))]
internal sealed partial class StreamingSessionWindowJsonContext : JsonSerializerContext;

/// <summary>使用 source-generated 元数据处理会话窗口定义。</summary>
public static class StreamingSessionWindowJson
{
    /// <summary>序列化会话窗口定义。</summary>
    /// <param name="definition">会话窗口定义。</param>
    /// <returns>UTF-8 JSON。</returns>
    public static byte[] Serialize(StreamingSessionWindowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        return JsonSerializer.SerializeToUtf8Bytes(definition, StreamingSessionWindowJsonContext.Default.StreamingSessionWindowDefinition);
    }

    /// <summary>反序列化并校验会话窗口定义。</summary>
    /// <param name="utf8Json">UTF-8 JSON。</param>
    /// <returns>会话窗口定义。</returns>
    public static StreamingSessionWindowDefinition Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        StreamingSessionWindowDefinition definition = JsonSerializer.Deserialize(utf8Json, StreamingSessionWindowJsonContext.Default.StreamingSessionWindowDefinition)
            ?? throw new InvalidDataException("会话窗口定义 JSON 为空。");
        definition.Validate();
        return definition;
    }
}
