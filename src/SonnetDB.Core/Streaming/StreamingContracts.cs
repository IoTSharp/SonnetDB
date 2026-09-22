using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonnetDB.Streaming;

/// <summary>
/// 事件落后于 watermark 时采用的处理策略。
/// </summary>
public enum StreamingLateEventPolicy
{
    /// <summary>仍然投递事件，并在事件上标记 <see cref="StreamingEvent.IsLate"/>。</summary>
    Deliver = 0,

    /// <summary>丢弃迟到事件，并向发布方返回丢弃结果。</summary>
    Drop = 1,

    /// <summary>拒绝迟到事件，并向发布方抛出 <see cref="StreamingLateEventException"/>。</summary>
    Reject = 2,
}

/// <summary>
/// 订阅批次在至少一次投递过程中的状态。
/// </summary>
public enum StreamingDeliveryStatus
{
    /// <summary>批次已读取但尚未确认。</summary>
    InFlight = 0,

    /// <summary>批次在上一次读取后仍未确认，本次读取是重投。</summary>
    Redelivered = 1,

    /// <summary>批次已由订阅者确认。</summary>
    Acknowledged = 2,
}

/// <summary>
/// 发布事件的处理结果。
/// </summary>
public enum StreamingPublishDisposition
{
    /// <summary>事件已经进入有界订阅缓冲区。</summary>
    Accepted = 0,

    /// <summary>事件因为迟到策略被丢弃。</summary>
    DroppedLate = 1,
}

/// <summary>
/// 可持久化的订阅定义。该 DTO 只描述单个本地订阅的边界，不代表分布式协调或 exactly-once。
/// </summary>
/// <param name="FormatVersion">订阅定义格式版本。</param>
/// <param name="SubscriptionId">订阅的稳定标识。</param>
/// <param name="StreamName">事件流名称。</param>
/// <param name="BatchSize">一次读取的最大事件数。</param>
/// <param name="Capacity">缓冲区最多保留的事件数。</param>
/// <param name="AllowedLatenessMilliseconds">允许迟到窗口，单位为毫秒。</param>
/// <param name="LateEventPolicy">迟到事件处理策略。</param>
public sealed record StreamingSubscriptionDefinition(
    int FormatVersion,
    string SubscriptionId,
    string StreamName,
    int BatchSize,
    int Capacity,
    long AllowedLatenessMilliseconds,
    StreamingLateEventPolicy LateEventPolicy)
{
    /// <summary>当前订阅定义格式版本。</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>允许迟到时间窗口。</summary>
    [JsonIgnore]
    public TimeSpan AllowedLateness => TimeSpan.FromMilliseconds(AllowedLatenessMilliseconds);

    /// <summary>
    /// 创建并校验订阅定义。
    /// </summary>
    /// <param name="subscriptionId">订阅的稳定标识。</param>
    /// <param name="streamName">事件流名称。</param>
    /// <param name="batchSize">一次读取的最大事件数。</param>
    /// <param name="capacity">缓冲区最多保留的事件数。</param>
    /// <param name="allowedLateness">允许迟到窗口。</param>
    /// <param name="lateEventPolicy">迟到事件处理策略。</param>
    /// <returns>已校验的订阅定义。</returns>
    public static StreamingSubscriptionDefinition Create(
        string subscriptionId,
        string streamName,
        int batchSize = 100,
        int capacity = 1000,
        TimeSpan? allowedLateness = null,
        StreamingLateEventPolicy lateEventPolicy = StreamingLateEventPolicy.Deliver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, batchSize);
        TimeSpan lateness = allowedLateness ?? TimeSpan.Zero;
        if (lateness < TimeSpan.Zero || lateness.TotalMilliseconds > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(allowedLateness), "允许迟到窗口必须为非负且可持久化为毫秒。");
        if (lateness.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(allowedLateness),
                "允许迟到窗口必须能精确表示为整数毫秒。");
        }

        long latenessMilliseconds = checked(lateness.Ticks / TimeSpan.TicksPerMillisecond);

        var definition = new StreamingSubscriptionDefinition(
            CurrentFormatVersion,
            subscriptionId,
            streamName,
            batchSize,
            capacity,
            latenessMilliseconds,
            lateEventPolicy);
        definition.Validate();
        return definition;
    }

    /// <summary>校验版本和边界，拒绝无法安全恢复的 DTO。</summary>
    public void Validate()
    {
        if (FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException($"不支持 StreamingSubscriptionDefinition 格式版本 {FormatVersion}。");
        ArgumentException.ThrowIfNullOrWhiteSpace(SubscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(StreamName);
        ArgumentOutOfRangeException.ThrowIfLessThan(BatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(Capacity, BatchSize);
        ArgumentOutOfRangeException.ThrowIfNegative(AllowedLatenessMilliseconds);
        if (AllowedLatenessMilliseconds > (long)TimeSpan.MaxValue.TotalMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(AllowedLatenessMilliseconds), "允许迟到窗口超过 TimeSpan 可表示范围。");
        if (!Enum.IsDefined(LateEventPolicy))
            throw new InvalidDataException($"未知迟到事件策略值 {(int)LateEventPolicy}。");
    }
}

/// <summary>
/// 可持久化的订阅检查点。只有确认批次后才应将候选检查点替换为当前检查点。
/// </summary>
/// <param name="FormatVersion">检查点格式版本。</param>
/// <param name="SubscriptionId">所属订阅标识。</param>
/// <param name="CommittedSequence">最近一次确认的事件序号；初始值为 -1。</param>
/// <param name="WatermarkUtc">最近一次确认时的 UTC watermark。</param>
/// <param name="Revision">检查点单调递增版本，可供外部持久层做条件更新。</param>
public sealed record StreamingSubscriptionCheckpoint(
    int FormatVersion,
    string SubscriptionId,
    long CommittedSequence,
    DateTimeOffset WatermarkUtc,
    long Revision)
{
    /// <summary>当前检查点格式版本。</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>创建空检查点。</summary>
    /// <param name="subscriptionId">订阅标识。</param>
    /// <returns>序号为 -1、revision 为 0 的检查点。</returns>
    public static StreamingSubscriptionCheckpoint Create(string subscriptionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        return new StreamingSubscriptionCheckpoint(
            CurrentFormatVersion,
            subscriptionId,
            -1,
            DateTimeOffset.MinValue,
            0);
    }

    /// <summary>校验检查点版本、序号和 revision。</summary>
    public void Validate()
    {
        if (FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException($"不支持 StreamingSubscriptionCheckpoint 格式版本 {FormatVersion}。");
        ArgumentException.ThrowIfNullOrWhiteSpace(SubscriptionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(CommittedSequence, -1);
        ArgumentOutOfRangeException.ThrowIfNegative(Revision);
        if (WatermarkUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("检查点 watermark 必须为 UTC。");
    }

    internal StreamingSubscriptionCheckpoint Advance(long sequence, DateTimeOffset watermarkUtc)
    {
        if (sequence < CommittedSequence)
            sequence = CommittedSequence;
        if (watermarkUtc < WatermarkUtc)
            watermarkUtc = WatermarkUtc;
        return this with
        {
            CommittedSequence = sequence,
            WatermarkUtc = watermarkUtc.ToUniversalTime(),
            Revision = checked(Revision + 1),
        };
    }
}

/// <summary>
/// 订阅输入的一条事件。事件序号必须由上游流保持稳定且非负。
/// </summary>
/// <param name="EventId">事件的稳定标识。</param>
/// <param name="Sequence">事件在流中的单调序号。</param>
/// <param name="EventTimeUtc">业务事件时间，必须为 UTC。</param>
/// <param name="Payload">事件载荷。</param>
/// <param name="Headers">可选的字符串头。</param>
/// <param name="IsLate">是否被订阅器判定为迟到事件。</param>
public sealed record StreamingEvent(
    string EventId,
    long Sequence,
    DateTimeOffset EventTimeUtc,
    byte[] Payload,
    IReadOnlyDictionary<string, string>? Headers = null,
    bool IsLate = false)
{
    /// <summary>校验事件字段和时间类型。</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(EventId);
        ArgumentOutOfRangeException.ThrowIfNegative(Sequence);
        if (EventTimeUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("事件时间必须为 UTC。", nameof(EventTimeUtc));
        ArgumentNullException.ThrowIfNull(Payload);
    }
}

/// <summary>
/// 一次发布的结果，供调用方记录被丢弃的迟到事件。
/// </summary>
/// <param name="Disposition">事件处理结果。</param>
/// <param name="EventId">输入事件标识。</param>
/// <param name="Sequence">输入事件序号。</param>
/// <param name="WatermarkUtc">判定时使用的 watermark。</param>
public sealed record StreamingPublishResult(
    StreamingPublishDisposition Disposition,
    string EventId,
    long Sequence,
    DateTimeOffset WatermarkUtc);

/// <summary>
/// 一次至少一次投递批次。未确认前再次读取会返回相同 delivery id 的批次。
/// </summary>
/// <param name="DeliveryId">批次投递标识。</param>
/// <param name="Attempt">从 1 开始的投递次数。</param>
/// <param name="Status">当前投递状态。</param>
/// <param name="Events">批次事件。</param>
/// <param name="CandidateCheckpoint">确认该批次后可持久化的候选检查点。</param>
public sealed record StreamingDeliveryBatch(
    string DeliveryId,
    int Attempt,
    StreamingDeliveryStatus Status,
    IReadOnlyList<StreamingEvent> Events,
    StreamingSubscriptionCheckpoint CandidateCheckpoint);

/// <summary>
/// 批次确认结果。
/// </summary>
/// <param name="DeliveryId">批次投递标识。</param>
/// <param name="Attempt">确认前的投递次数。</param>
/// <param name="Status">确认后的状态。</param>
/// <param name="Checkpoint">已提交到内存订阅的检查点。</param>
public sealed record StreamingDeliveryReceipt(
    string DeliveryId,
    int Attempt,
    StreamingDeliveryStatus Status,
    StreamingSubscriptionCheckpoint Checkpoint);

/// <summary>迟到事件被拒绝时抛出的异常。</summary>
public sealed class StreamingLateEventException : InvalidOperationException
{
    /// <summary>创建迟到事件异常。</summary>
    /// <param name="eventId">被拒绝的事件标识。</param>
    public StreamingLateEventException(string eventId)
        : base($"事件 '{eventId}' 已超过订阅允许的迟到窗口。")
    {
        EventId = eventId;
    }

    /// <summary>被拒绝的事件标识。</summary>
    public string EventId { get; }
}

/// <summary>订阅专用的 Native AOT JSON 元数据。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StreamingSubscriptionDefinition))]
[JsonSerializable(typeof(StreamingSubscriptionCheckpoint))]
[JsonSerializable(typeof(StreamingEvent))]
[JsonSerializable(typeof(StreamingPublishResult))]
[JsonSerializable(typeof(StreamingDeliveryBatch))]
[JsonSerializable(typeof(StreamingDeliveryReceipt))]
[JsonSerializable(typeof(IReadOnlyList<StreamingEvent>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class StreamingJsonContext : JsonSerializerContext;

/// <summary>
/// 使用 source-generated 元数据序列化 Streaming 合同 DTO。
/// </summary>
public static class StreamingSubscriptionJson
{
    /// <summary>序列化订阅定义。</summary>
    /// <param name="definition">订阅定义。</param>
    /// <returns>UTF-8 JSON。</returns>
    public static byte[] Serialize(StreamingSubscriptionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        return JsonSerializer.SerializeToUtf8Bytes(definition, StreamingJsonContext.Default.StreamingSubscriptionDefinition);
    }

    /// <summary>反序列化并校验订阅定义。</summary>
    /// <param name="utf8Json">UTF-8 JSON。</param>
    /// <returns>订阅定义。</returns>
    public static StreamingSubscriptionDefinition DeserializeDefinition(ReadOnlySpan<byte> utf8Json)
    {
        var value = JsonSerializer.Deserialize(utf8Json, StreamingJsonContext.Default.StreamingSubscriptionDefinition)
            ?? throw new InvalidDataException("Streaming 订阅定义 JSON 为空。");
        value.Validate();
        return value;
    }

    /// <summary>序列化订阅检查点。</summary>
    /// <param name="checkpoint">检查点。</param>
    /// <returns>UTF-8 JSON。</returns>
    public static byte[] Serialize(StreamingSubscriptionCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        checkpoint.Validate();
        return JsonSerializer.SerializeToUtf8Bytes(checkpoint, StreamingJsonContext.Default.StreamingSubscriptionCheckpoint);
    }

    /// <summary>反序列化并校验订阅检查点。</summary>
    /// <param name="utf8Json">UTF-8 JSON。</param>
    /// <returns>订阅检查点。</returns>
    public static StreamingSubscriptionCheckpoint DeserializeCheckpoint(ReadOnlySpan<byte> utf8Json)
    {
        var value = JsonSerializer.Deserialize(utf8Json, StreamingJsonContext.Default.StreamingSubscriptionCheckpoint)
            ?? throw new InvalidDataException("Streaming 订阅检查点 JSON 为空。");
        value.Validate();
        return value;
    }
}
