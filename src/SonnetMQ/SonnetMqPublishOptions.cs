namespace SonnetMQ;

/// <summary>
/// 发布消息时的附加选项。
/// </summary>
/// <param name="Headers">可选消息头。</param>
public sealed record SonnetMqPublishOptions(IReadOnlyDictionary<string, string>? Headers = null);
