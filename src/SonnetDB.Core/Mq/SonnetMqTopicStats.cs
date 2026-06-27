namespace SonnetMQ;

/// <summary>
/// Topic 统计快照。
/// </summary>
/// <param name="Topic">Topic 名称。</param>
/// <param name="MessageCount">已追加消息数量。</param>
/// <param name="NextOffset">下一条消息 offset。</param>
/// <param name="ConsumerOffsets">消费者组已确认 offset。值表示下一条待消费 offset。</param>
public sealed record SonnetMqTopicStats(
    string Topic,
    long MessageCount,
    long NextOffset,
    IReadOnlyDictionary<string, long> ConsumerOffsets);
