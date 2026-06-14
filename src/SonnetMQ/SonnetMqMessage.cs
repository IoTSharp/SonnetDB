namespace SonnetMQ;

/// <summary>
/// SonnetMQ 消息读取结果。
/// </summary>
/// <param name="Topic">Topic 名称。</param>
/// <param name="Offset">Topic 内单调递增 offset。</param>
/// <param name="TimestampUtc">服务端写入时间。</param>
/// <param name="Headers">消息头。</param>
/// <param name="Payload">消息体。</param>
public sealed record SonnetMqMessage(
    string Topic,
    long Offset,
    DateTimeOffset TimestampUtc,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Payload);
