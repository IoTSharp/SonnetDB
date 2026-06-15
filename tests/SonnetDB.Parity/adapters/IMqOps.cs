namespace SonnetDB.Parity.Adapters;

/// <summary>
/// MQ / 追加日志支柱的语义操作集合。
/// </summary>
public interface IMqOps
{
    /// <summary>清空当前场景使用的 topic 或 subject。</summary>
    Task ResetTopicAsync(string topic, CancellationToken ct);

    /// <summary>发布消息并返回后端分配的单调 offset / sequence。</summary>
    Task<long> PublishAsync(string topic, byte[] payload, IReadOnlyDictionary<string, string>? headers, CancellationToken ct);

    /// <summary>批量发布消息。</summary>
    Task<IReadOnlyList<long>> PublishManyAsync(string topic, IReadOnlyList<MqPublishRecord> records, CancellationToken ct);

    /// <summary>按 consumer group 拉取未确认消息。</summary>
    Task<IReadOnlyList<MqMessageRecord>> PullAsync(string topic, string consumerGroup, int maxCount, CancellationToken ct);

    /// <summary>按 offset 重放，不推进 consumer group。</summary>
    Task<IReadOnlyList<MqMessageRecord>> ReplayAsync(string topic, long offset, int maxCount, CancellationToken ct);

    /// <summary>确认 consumer group 已处理到指定 offset。</summary>
    Task<long> AckAsync(string topic, string consumerGroup, long offset, CancellationToken ct);

    /// <summary>模拟后端重启或重连。</summary>
    Task RestartAsync(CancellationToken ct);
}

/// <summary>
/// 规范化 MQ 发布记录。
/// </summary>
public sealed record MqPublishRecord(byte[] Payload, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// 规范化 MQ 消息。
/// </summary>
public sealed record MqMessageRecord(
    string Topic,
    long Offset,
    DateTimeOffset TimestampUtc,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Payload);

/// <summary>
/// 不支持 MQ 能力的空操作对象。
/// </summary>
public sealed class UnsupportedMqOps : IMqOps
{
    /// <summary>共享实例。</summary>
    public static UnsupportedMqOps Instance { get; } = new();

    private UnsupportedMqOps() { }

    /// <inheritdoc />
    public Task ResetTopicAsync(string topic, CancellationToken ct) => Unsupported();

    /// <inheritdoc />
    public Task<long> PublishAsync(string topic, byte[] payload, IReadOnlyDictionary<string, string>? headers, CancellationToken ct) => Unsupported<long>();

    /// <inheritdoc />
    public Task<IReadOnlyList<long>> PublishManyAsync(string topic, IReadOnlyList<MqPublishRecord> records, CancellationToken ct) => Unsupported<IReadOnlyList<long>>();

    /// <inheritdoc />
    public Task<IReadOnlyList<MqMessageRecord>> PullAsync(string topic, string consumerGroup, int maxCount, CancellationToken ct) => Unsupported<IReadOnlyList<MqMessageRecord>>();

    /// <inheritdoc />
    public Task<IReadOnlyList<MqMessageRecord>> ReplayAsync(string topic, long offset, int maxCount, CancellationToken ct) => Unsupported<IReadOnlyList<MqMessageRecord>>();

    /// <inheritdoc />
    public Task<long> AckAsync(string topic, string consumerGroup, long offset, CancellationToken ct) => Unsupported<long>();

    /// <inheritdoc />
    public Task RestartAsync(CancellationToken ct) => Unsupported();

    private static Task Unsupported()
        => throw new NotSupportedException("当前后端不支持 MQ 操作。");

    private static Task<T> Unsupported<T>()
        => throw new NotSupportedException("当前后端不支持 MQ 操作。");
}
