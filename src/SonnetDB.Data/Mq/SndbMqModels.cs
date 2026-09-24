namespace SonnetDB.Data.Mq;

/// <summary>
/// SonnetDB MQ 消息。
/// </summary>
/// <param name="Topic">Topic 名称。</param>
/// <param name="Offset">消息 offset。</param>
/// <param name="TimestampUtc">服务端写入时间。</param>
/// <param name="Headers">消息头。</param>
/// <param name="Payload">消息体。</param>
public sealed record SndbMqMessage(
    string Topic,
    long Offset,
    DateTimeOffset TimestampUtc,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Payload);

/// <summary>
/// SonnetDB MQ Topic 统计。
/// </summary>
/// <param name="Topic">Topic 名称。</param>
/// <param name="MessageCount">消息数量。</param>
/// <param name="NextOffset">下一条消息 offset。</param>
/// <param name="ConsumerOffsets">消费者组 offset。</param>
public sealed record SndbMqStats(
    string Topic,
    long MessageCount,
    long NextOffset,
    IReadOnlyDictionary<string, long> ConsumerOffsets);

/// <summary>消费者拒绝消息后的结果。</summary>
public sealed record SndbMqNackResult(
    long NextOffset,
    int DeliveryAttempt,
    bool DeadLettered,
    long? DeadLetterOffset);

/// <summary>Topic 投递治理诊断。</summary>
public sealed record SndbMqDiagnostics(
    string Topic,
    long NextOffset,
    long EarliestOffset,
    IReadOnlyDictionary<string, long> ConsumerLag,
    long PendingRedeliveryCount,
    long DeadLetterCount,
    string? LastDiscardReason);

/// <summary>消费者组 offset 重置目标。</summary>
public enum SndbMqOffsetResetMode : byte
{
    /// <summary>当前 retention 最早 offset。</summary>
    Earliest = 0,
    /// <summary>Topic 当前末尾。</summary>
    Latest = 1,
    /// <summary>UTC ticks 时间定位。</summary>
    Time = 2,
    /// <summary>显式 offset。</summary>
    Explicit = 3,
}

/// <summary>
/// SonnetDB MQ 批量发布条目。
/// </summary>
/// <param name="Payload">消息体。</param>
/// <param name="Headers">可选消息头。</param>
public sealed record SndbMqPublishEntry(
    ReadOnlyMemory<byte> Payload,
    IReadOnlyDictionary<string, string>? Headers = null);

internal sealed record MqPublishRequest(byte[] Payload, IReadOnlyDictionary<string, string>? Headers = null);

internal sealed record MqPublishResponse(string Topic, long Offset);

internal sealed record MqPublishBatchEntry(byte[] Payload, IReadOnlyDictionary<string, string>? Headers = null);

internal sealed record MqPublishBatchRequest(IReadOnlyList<MqPublishBatchEntry> Messages);

internal sealed record MqPublishBatchResponse(string Topic, IReadOnlyList<long> Offsets);

internal sealed record MqPullRequest(string ConsumerGroup, int? MaxCount = null);

internal sealed record MqMessageResponse(
    string Topic,
    long Offset,
    DateTimeOffset TimestampUtc,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Payload);

internal sealed record MqPullResponse(List<MqMessageResponse> Messages);

internal sealed record MqAckRequest(string ConsumerGroup, long Offset);

internal sealed record MqAckResponse(string Topic, string ConsumerGroup, long NextOffset);

internal sealed record MqNackRequest(string ConsumerGroup, long Offset, string? Reason = null);

internal sealed record MqNackResponse(
    string Topic,
    string ConsumerGroup,
    long NextOffset,
    int DeliveryAttempt,
    bool DeadLettered,
    long? DeadLetterOffset);

internal sealed record MqOffsetResetRequest(string ConsumerGroup, byte Mode, long Value = 0);

internal sealed record MqOffsetResetResponse(string Topic, string ConsumerGroup, long NextOffset);

internal sealed record MqStatsResponse(
    string Topic,
    long MessageCount,
    long NextOffset,
    Dictionary<string, long> ConsumerOffsets);

internal sealed record MqDiagnosticsResponse(
    string Topic,
    long NextOffset,
    long EarliestOffset,
    Dictionary<string, long> ConsumerLag,
    long PendingRedeliveryCount,
    long DeadLetterCount,
    string? LastDiscardReason);
