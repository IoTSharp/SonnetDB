using System.Text.Json.Serialization;

namespace SonnetDB.Streaming;

/// <summary>文件订阅的事件、批次、磁盘容量与存储操作边界。</summary>
public sealed record FileStreamingSubscriptionOptions
{
    /// <summary>最大事件 JSON 字节数，包含 base64 载荷和事件头，最高为 256 KiB。</summary>
    public int MaxEventBytes { get; init; } = 256 * 1024;

    /// <summary>事件 spool 的最大文件字节数，包含 CDC 包装与帧头。</summary>
    public long MaxStoredBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>单个投递批次的最大 CDC 包装正文字节数。</summary>
    public long MaxBatchBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>单次存储操作及闸门等待的超时毫秒数，不包括等待事件或容量的时间。</summary>
    public int OperationTimeoutMilliseconds { get; init; } = 10_000;

    /// <summary>同一未确认批次允许的最大投递次数，达到上限后必须先确认或人工处理该批次。</summary>
    public int MaxDeliveryAttempts { get; init; } = 100;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEventBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxEventBytes, Cdc.CdcEventCodec.MaxPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxStoredBytes, Cdc.CdcEventSpool.FrameHeaderSize + 1L);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBatchBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBatchBytes, MaxStoredBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(OperationTimeoutMilliseconds, 50);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(OperationTimeoutMilliseconds, 300_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDeliveryAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxDeliveryAttempts, 1_000_000);
    }
}

/// <summary>
/// 文件持久订阅的本地运维状态快照。
/// </summary>
/// <remarks>
/// <para>快照只描述当前进程持有文件锁时观察到的本地状态，不代表远程实例或分布式租约状态。</para>
/// <para><see cref="OldestEventAge"/> 基于 <see cref="ObservedAtUtc"/> 计算，调用方应在长时间展示时重新查询。</para>
/// </remarks>
/// <param name="SubscriptionId">订阅标识。</param>
/// <param name="StreamName">事件流名称。</param>
/// <param name="PublishingCompleted">发布端是否已持久化完成标记。</param>
/// <param name="CommittedSequence">最近确认的事件序号。</param>
/// <param name="LastAcceptedSequence">最近接受的事件序号。</param>
/// <param name="PendingEventCount">尚未确认的事件数。</param>
/// <param name="StoredBytes">spool 当前占用的字节数。</param>
/// <param name="WatermarkUtc">当前持久化 watermark。</param>
/// <param name="InFlightDeliveryId">未确认批次标识；没有批次时为空。</param>
/// <param name="InFlightAttempt">未确认批次当前投递次数；没有批次时为 0。</param>
/// <param name="MaxDeliveryAttempts">同一未确认批次允许的最大投递次数。</param>
/// <param name="InFlightEventCount">未确认批次中的事件数；没有批次时为 0。</param>
/// <param name="OldestEventTimeUtc">当前 backlog 中最早事件的 UTC 时间；为空表示 backlog 为空。</param>
/// <param name="OldestEventAge">查询时最早事件的年龄；没有 backlog 时为空。</param>
/// <param name="ObservedAtUtc">生成快照时的 UTC 时间。</param>
public sealed record FileStreamingSubscriptionStatus(
    string SubscriptionId,
    string StreamName,
    bool PublishingCompleted,
    long CommittedSequence,
    long LastAcceptedSequence,
    int PendingEventCount,
    long StoredBytes,
    DateTimeOffset WatermarkUtc,
    string? InFlightDeliveryId,
    int InFlightAttempt,
    int MaxDeliveryAttempts,
    int InFlightEventCount,
    DateTimeOffset? OldestEventTimeUtc,
    TimeSpan? OldestEventAge,
    DateTimeOffset ObservedAtUtc)
{
    /// <summary>是否已持久暂停新批次投递和未确认批次重投；发布和确认仍可继续。</summary>
    public bool ConsumptionPaused { get; init; }

    /// <summary>状态文件的单调修订号，供运维命令进行条件更新；与确认检查点修订号不同。</summary>
    public long StateRevision { get; init; }

    /// <summary>当前未确认批次是否已经耗尽允许的投递次数。</summary>
    public bool DeliveryAttemptsExhausted => InFlightDeliveryId is not null && InFlightAttempt >= MaxDeliveryAttempts;
}

/// <summary>未确认批次达到文件订阅投递次数上限时抛出的异常。</summary>
public sealed class FileStreamingDeliveryAttemptLimitException : InvalidOperationException
{
    /// <summary>创建投递次数上限异常。</summary>
    /// <param name="deliveryId">未确认批次标识。</param>
    /// <param name="attempt">当前投递次数。</param>
    /// <param name="maxAttempts">允许的最大投递次数。</param>
    public FileStreamingDeliveryAttemptLimitException(string deliveryId, int attempt, int maxAttempts)
        : base($"持久订阅批次 '{deliveryId}' 已达到投递次数上限 {maxAttempts}。")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        DeliveryId = deliveryId;
        Attempt = attempt;
        MaxAttempts = maxAttempts;
    }

    /// <summary>达到上限的未确认批次标识。</summary>
    public string DeliveryId { get; }

    /// <summary>当前投递次数。</summary>
    public int Attempt { get; }

    /// <summary>允许的最大投递次数。</summary>
    public int MaxAttempts { get; }
}

internal sealed record FileStreamingDeliveryState(
    [property: JsonRequired] string DeliveryId,
    [property: JsonRequired] int Attempt,
    [property: JsonRequired] long FirstSequence,
    [property: JsonRequired] long LastSequence,
    [property: JsonRequired] int EventCount,
    [property: JsonRequired] StreamingSubscriptionCheckpoint CandidateCheckpoint);

internal sealed record FileStreamingSubscriptionState(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] StreamingSubscriptionDefinition Definition,
    [property: JsonRequired] FileStreamingSubscriptionOptions Options,
    [property: JsonRequired] StreamingSubscriptionCheckpoint Checkpoint,
    [property: JsonRequired] DateTimeOffset WatermarkUtc,
    [property: JsonRequired] long LastAcceptedSequence,
    [property: JsonRequired] int PendingEventCount,
    [property: JsonRequired] bool PublishingCompleted,
    [property: JsonRequired] FileStreamingDeliveryState? InFlight,
    [property: JsonRequired] bool ConsumptionPaused,
    [property: JsonRequired] long StateRevision);

internal sealed record FileStreamingStateEnvelope(
    [property: JsonRequired] FileStreamingSubscriptionState State,
    [property: JsonRequired] string Sha256);

// v1 状态文件没有 MaxDeliveryAttempts。保留一个只用于校验旧哈希的源生成模型，
// 打开时迁移到当前状态模型并使用默认投递上限；不再把旧字段暴露为运行时兼容 API。
internal sealed record LegacyFileStreamingSubscriptionOptions(
    [property: JsonRequired] int MaxEventBytes,
    [property: JsonRequired] long MaxStoredBytes,
    [property: JsonRequired] long MaxBatchBytes,
    [property: JsonRequired] int OperationTimeoutMilliseconds);

internal sealed record LegacyFileStreamingSubscriptionState(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] StreamingSubscriptionDefinition Definition,
    [property: JsonRequired] LegacyFileStreamingSubscriptionOptions Options,
    [property: JsonRequired] StreamingSubscriptionCheckpoint Checkpoint,
    [property: JsonRequired] DateTimeOffset WatermarkUtc,
    [property: JsonRequired] long LastAcceptedSequence,
    [property: JsonRequired] int PendingEventCount,
    [property: JsonRequired] bool PublishingCompleted,
    [property: JsonRequired] FileStreamingDeliveryState? InFlight);

internal sealed record LegacyFileStreamingStateEnvelope(
    [property: JsonRequired] LegacyFileStreamingSubscriptionState State,
    [property: JsonRequired] string Sha256);

// v2 已有投递上限，但没有暂停标记与状态修订号。此模型仅用于严格校验旧文件并迁移。
internal sealed record Version2FileStreamingSubscriptionOptions(
    [property: JsonRequired] int MaxEventBytes,
    [property: JsonRequired] long MaxStoredBytes,
    [property: JsonRequired] long MaxBatchBytes,
    [property: JsonRequired] int OperationTimeoutMilliseconds,
    [property: JsonRequired] int MaxDeliveryAttempts);

internal sealed record Version2FileStreamingSubscriptionState(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] StreamingSubscriptionDefinition Definition,
    [property: JsonRequired] Version2FileStreamingSubscriptionOptions Options,
    [property: JsonRequired] StreamingSubscriptionCheckpoint Checkpoint,
    [property: JsonRequired] DateTimeOffset WatermarkUtc,
    [property: JsonRequired] long LastAcceptedSequence,
    [property: JsonRequired] int PendingEventCount,
    [property: JsonRequired] bool PublishingCompleted,
    [property: JsonRequired] FileStreamingDeliveryState? InFlight);

internal sealed record Version2FileStreamingStateEnvelope(
    [property: JsonRequired] Version2FileStreamingSubscriptionState State,
    [property: JsonRequired] string Sha256);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(FileStreamingStateEnvelope))]
[JsonSerializable(typeof(FileStreamingSubscriptionState))]
[JsonSerializable(typeof(LegacyFileStreamingStateEnvelope))]
[JsonSerializable(typeof(LegacyFileStreamingSubscriptionState))]
[JsonSerializable(typeof(LegacyFileStreamingSubscriptionOptions))]
[JsonSerializable(typeof(Version2FileStreamingStateEnvelope))]
[JsonSerializable(typeof(Version2FileStreamingSubscriptionState))]
[JsonSerializable(typeof(Version2FileStreamingSubscriptionOptions))]
[JsonSerializable(typeof(StreamingEvent))]
internal sealed partial class FileStreamingJsonContext : JsonSerializerContext;
