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

internal sealed record MqPublishRequest(byte[] Payload, IReadOnlyDictionary<string, string>? Headers = null);

internal sealed record MqPublishResponse(string Topic, long Offset);

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

internal sealed record MqStatsResponse(
    string Topic,
    long MessageCount,
    long NextOffset,
    Dictionary<string, long> ConsumerOffsets);
