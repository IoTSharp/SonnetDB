namespace SonnetMQ;

/// <summary>消费者拒绝一次投递后的结果。</summary>
/// <param name="NextOffset">消费者组下一条待消费 offset。</param>
/// <param name="DeliveryAttempt">该消息在本次拒绝后的累计投递次数。</param>
/// <param name="DeadLettered">是否已转入死信 Topic。</param>
/// <param name="DeadLetterOffset">死信 Topic 中新消息的 offset；未转入死信时为 null。</param>
public sealed record SonnetMqNackResult(
    long NextOffset,
    int DeliveryAttempt,
    bool DeadLettered,
    long? DeadLetterOffset);

/// <summary>Topic 投递治理诊断快照。</summary>
/// <param name="Topic">Topic 名称。</param>
/// <param name="NextOffset">下一条消息 offset。</param>
/// <param name="EarliestOffset">当前仍可读取的最早 offset。</param>
/// <param name="ConsumerLag">各消费者组积压量。</param>
/// <param name="PendingRedeliveryCount">尚未转入死信的拒绝消息数。</param>
/// <param name="DeadLetterCount">已转入死信的消息数。</param>
/// <param name="LastDiscardReason">最近一次转入死信的原因。</param>
public sealed record SonnetMqTopicDiagnostics(
    string Topic,
    long NextOffset,
    long EarliestOffset,
    IReadOnlyDictionary<string, long> ConsumerLag,
    long PendingRedeliveryCount,
    long DeadLetterCount,
    string? LastDiscardReason);
