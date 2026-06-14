namespace SonnetMQ;

/// <summary>
/// SonnetMQ 批量发布条目。
/// </summary>
/// <param name="Payload">消息体。</param>
/// <param name="Headers">可选消息头。</param>
public sealed record SonnetMqPublishEntry(
    ReadOnlyMemory<byte> Payload,
    IReadOnlyDictionary<string, string>? Headers = null);
