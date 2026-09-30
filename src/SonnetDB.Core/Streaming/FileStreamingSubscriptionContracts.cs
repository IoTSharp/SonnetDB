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

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEventBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxEventBytes, Cdc.CdcEventCodec.MaxPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxStoredBytes, Cdc.CdcEventSpool.FrameHeaderSize + 1L);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBatchBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBatchBytes, MaxStoredBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(OperationTimeoutMilliseconds, 50);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(OperationTimeoutMilliseconds, 300_000);
    }
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
    [property: JsonRequired] FileStreamingDeliveryState? InFlight);

internal sealed record FileStreamingStateEnvelope(
    [property: JsonRequired] FileStreamingSubscriptionState State,
    [property: JsonRequired] string Sha256);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(FileStreamingStateEnvelope))]
[JsonSerializable(typeof(FileStreamingSubscriptionState))]
[JsonSerializable(typeof(StreamingEvent))]
internal sealed partial class FileStreamingJsonContext : JsonSerializerContext;
