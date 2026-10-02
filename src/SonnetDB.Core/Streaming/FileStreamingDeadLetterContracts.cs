using System.Text.Json.Serialization;

namespace SonnetDB.Streaming;

/// <summary>文件订阅独立死信目录的持久容量边界；容量耗尽时拒绝隔离并保留原批次。</summary>
public sealed record FileStreamingDeadLetterOptions
{
    /// <summary>最多保留的死信批次数，包含尚待恢复完成的隔离意图。</summary>
    [JsonRequired]
    public int MaxBatches { get; init; } = 1000;

    /// <summary>死信状态文件的最大字节数，包含原事件、原因、检查点和 SHA-256 校验。</summary>
    [JsonRequired]
    public long MaxStoredBytes { get; init; } = 64L * 1024 * 1024;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBatches, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBatches, 10_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxStoredBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxStoredBytes, 128L * 1024 * 1024);
    }
}

/// <summary>一条持久死信批次的只读运维摘要，不包含事件载荷。</summary>
/// <param name="Sequence">死信目录内从 1 开始的单调批次序号，供有界分页使用。</param>
/// <param name="DeliveryId">原未确认批次的稳定投递标识。</param>
/// <param name="SubscriptionId">原订阅标识。</param>
/// <param name="StreamName">原事件流名称。</param>
/// <param name="Attempt">隔离时已耗尽的原投递次数。</param>
/// <param name="Reason">管理员提供的隔离原因。</param>
/// <param name="CreatedAtUtc">持久隔离意图创建的 UTC 时间。</param>
/// <param name="EventCount">保留的原事件数。</param>
/// <param name="FirstEventSequence">首个原事件序号。</param>
/// <param name="LastEventSequence">末尾原事件序号。</param>
/// <param name="Checkpoint">隔离完成后推进的订阅检查点。</param>
public sealed record FileStreamingDeadLetterSummary(
    long Sequence,
    string DeliveryId,
    string SubscriptionId,
    string StreamName,
    int Attempt,
    string Reason,
    DateTimeOffset CreatedAtUtc,
    int EventCount,
    long FirstEventSequence,
    long LastEventSequence,
    StreamingSubscriptionCheckpoint Checkpoint)
{
    /// <summary>生成摘要时独立死信目录的修订号，供重放和删除进行条件更新。</summary>
    public long DeadLetterRevision { get; init; }

    /// <summary>已领取重放的稳定身份；空值表示尚未领取。</summary>
    public string? ReplayId { get; init; }

    /// <summary>已持久领取重放的次数；尚未领取时为 0。</summary>
    public int ReplayAttempt { get; init; }

    /// <summary>最近一次持久领取重放的 UTC 时间；尚未领取时为空。</summary>
    public DateTimeOffset? LastReplayClaimedAtUtc { get; init; }
}

/// <summary>原事件及其完整载荷、头、时间和迟到标记的持久死信批次副本。</summary>
/// <param name="Summary">批次身份、原因和检查点摘要。</param>
/// <param name="Events">按原顺序保留的事件副本；修改副本不影响持久数据。</param>
public sealed record FileStreamingDeadLetterBatch(
    FileStreamingDeadLetterSummary Summary,
    IReadOnlyList<StreamingEvent> Events);

/// <summary>管理员条件隔离耗尽批次后返回的持久结果；与业务确认回执不同。</summary>
/// <param name="DeliveryId">原未确认批次的稳定投递标识。</param>
/// <param name="DeadLetterSequence">持久死信目录中的批次序号。</param>
/// <param name="Attempt">隔离时已耗尽的投递次数。</param>
/// <param name="Checkpoint">已持久推进的订阅检查点。</param>
/// <param name="StateRevision">隔离成功后的订阅状态修订号。</param>
public sealed record FileStreamingDeadLetterReceipt(
    string DeliveryId,
    long DeadLetterSequence,
    int Attempt,
    StreamingSubscriptionCheckpoint Checkpoint,
    long StateRevision);

/// <summary>已持久领取的本地死信重放批次；业务成功后须使用回执条件显式删除，失败时保留并再次领取。</summary>
/// <param name="Summary">原死信批次摘要，包含本次领取后的目录修订号和重放身份。</param>
/// <param name="Events">保留原序号、载荷、头和迟到标记的事件副本，供业务幂等处理。</param>
public sealed record FileStreamingDeadLetterReplayBatch(
    FileStreamingDeadLetterSummary Summary,
    IReadOnlyList<StreamingEvent> Events);

/// <summary>死信批次经条件删除后返回的持久结果；同一序号不会重新分配。</summary>
/// <param name="Sequence">已删除的死信批次序号。</param>
/// <param name="DeliveryId">已删除批次的原投递身份。</param>
/// <param name="ReplayId">完成的重放身份；直接丢弃未领取批次时为空。</param>
/// <param name="ReplayAttempt">完成的重放领取次数；未领取时为 0。</param>
/// <param name="DeadLetterRevision">条件删除后的独立死信目录修订号。</param>
public sealed record FileStreamingDeadLetterDeletionReceipt(
    long Sequence,
    string DeliveryId,
    string? ReplayId,
    int ReplayAttempt,
    long DeadLetterRevision);

internal sealed record FileStreamingDeadLetterReplayState(
    [property: JsonRequired] string ReplayId,
    [property: JsonRequired] int Attempt,
    [property: JsonRequired] DateTimeOffset LastClaimedAtUtc);

internal sealed record FileStreamingDeadLetterEvent(
    [property: JsonRequired] string EventId,
    [property: JsonRequired] long Sequence,
    [property: JsonRequired] DateTimeOffset EventTimeUtc,
    [property: JsonRequired] byte[] Payload,
    [property: JsonRequired] Dictionary<string, string>? Headers,
    [property: JsonRequired] bool IsLate)
{
    internal static FileStreamingDeadLetterEvent FromEvent(StreamingEvent value)
        => new(value.EventId, value.Sequence, value.EventTimeUtc, value.Payload.ToArray(),
            value.Headers is null ? null : new Dictionary<string, string>(value.Headers, StringComparer.Ordinal), value.IsLate);

    internal StreamingEvent ToEvent()
        => new(EventId, Sequence, EventTimeUtc, Payload.ToArray(),
            Headers is null ? null : new Dictionary<string, string>(Headers, StringComparer.Ordinal), IsLate);
}

internal sealed record FileStreamingDeadLetterRecord(
    [property: JsonRequired] long Sequence,
    [property: JsonRequired] string DeliveryId,
    [property: JsonRequired] int Attempt,
    [property: JsonRequired] string Reason,
    [property: JsonRequired] DateTimeOffset CreatedAtUtc,
    [property: JsonRequired] long ExpectedStateRevision,
    [property: JsonRequired] StreamingSubscriptionCheckpoint PreviousCheckpoint,
    [property: JsonRequired] StreamingSubscriptionCheckpoint Checkpoint,
    [property: JsonRequired] FileStreamingDeadLetterEvent[] Events)
{
    [JsonRequired]
    public FileStreamingDeadLetterReplayState? Replay { get; init; }
}

internal sealed record FileStreamingDeadLetterState(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] string SubscriptionId,
    [property: JsonRequired] string StreamName,
    [property: JsonRequired] FileStreamingDeadLetterOptions Options,
    [property: JsonRequired] FileStreamingDeadLetterRecord[] Records,
    [property: JsonRequired] string? PendingDeliveryId)
{
    [JsonRequired]
    public long NextSequence { get; init; } = 1;

    [JsonRequired]
    public long Revision { get; init; }
}

internal sealed record FileStreamingDeadLetterEnvelope(
    [property: JsonRequired] FileStreamingDeadLetterState State,
    [property: JsonRequired] string Sha256);

// v1 的规范化哈希必须使用原字段集合校验，不能用 v2 的默认字段重新签名。
internal sealed record LegacyFileStreamingDeadLetterRecord(
    [property: JsonRequired] long Sequence,
    [property: JsonRequired] string DeliveryId,
    [property: JsonRequired] int Attempt,
    [property: JsonRequired] string Reason,
    [property: JsonRequired] DateTimeOffset CreatedAtUtc,
    [property: JsonRequired] long ExpectedStateRevision,
    [property: JsonRequired] StreamingSubscriptionCheckpoint PreviousCheckpoint,
    [property: JsonRequired] StreamingSubscriptionCheckpoint Checkpoint,
    [property: JsonRequired] FileStreamingDeadLetterEvent[] Events);

internal sealed record LegacyFileStreamingDeadLetterState(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] string SubscriptionId,
    [property: JsonRequired] string StreamName,
    [property: JsonRequired] FileStreamingDeadLetterOptions Options,
    [property: JsonRequired] LegacyFileStreamingDeadLetterRecord[] Records,
    [property: JsonRequired] string? PendingDeliveryId);

internal sealed record LegacyFileStreamingDeadLetterEnvelope(
    [property: JsonRequired] LegacyFileStreamingDeadLetterState State,
    [property: JsonRequired] string Sha256);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(FileStreamingDeadLetterEnvelope))]
[JsonSerializable(typeof(FileStreamingDeadLetterState))]
[JsonSerializable(typeof(LegacyFileStreamingDeadLetterEnvelope))]
[JsonSerializable(typeof(LegacyFileStreamingDeadLetterState))]
internal sealed partial class FileStreamingDeadLetterJsonContext : JsonSerializerContext;
