namespace SonnetDB.Cdc;

/// <summary>
/// CDC append-only spool 的容量和回放边界。
/// </summary>
public sealed record CdcEventSpoolOptions
{
    /// <summary>默认允许保存的最大事件数。</summary>
    public const int DefaultMaxEvents = 100_000;

    /// <summary>默认允许使用的最大文件字节数。</summary>
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    /// <summary>默认允许记录的最大分区数。</summary>
    public const int DefaultMaxPartitions = 128;

    /// <summary>实现允许配置的绝对最大分区数。</summary>
    public const int AbsoluteMaxPartitions = 100_000;

    /// <summary>默认单次回放的最大事件数。</summary>
    public const int DefaultMaxReplayEvents = 10_000;

    /// <summary>默认单次回放的最大事件正文字节数。</summary>
    public const long DefaultMaxReplayBytes = 16L * 1024 * 1024;

    /// <summary>允许保存的最大事件数。</summary>
    public int MaxEvents { get; init; } = DefaultMaxEvents;

    /// <summary>允许使用的最大 spool 文件字节数（包含帧头）。</summary>
    public long MaxBytes { get; init; } = DefaultMaxBytes;

    /// <summary>允许在 checkpoint 元数据中出现的最大分区数。</summary>
    public int MaxPartitions { get; init; } = DefaultMaxPartitions;

    /// <summary>单次回放默认返回的最大事件数。</summary>
    public int MaxReplayEvents { get; init; } = DefaultMaxReplayEvents;

    /// <summary>单次回放默认返回的最大事件正文字节数。</summary>
    public long MaxReplayBytes { get; init; } = DefaultMaxReplayBytes;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEvents);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPartitions);
        if (MaxPartitions > AbsoluteMaxPartitions)
            throw new ArgumentOutOfRangeException(nameof(MaxPartitions), $"MaxPartitions 不能超过 {AbsoluteMaxPartitions}。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxReplayEvents);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxReplayBytes, 1);

        long minimumBytes = CdcEventSpool.FrameHeaderSize + 1L;
        if (MaxBytes < minimumBytes)
            throw new ArgumentOutOfRangeException(nameof(MaxBytes), $"MaxBytes 至少需要 {minimumBytes} 字节。");
    }
}

/// <summary>
/// 一次 append 成功后分配的 spool 帧位置。
/// </summary>
/// <param name="FrameOffset">帧在文件中的起始字节偏移。</param>
/// <param name="FrameLength">帧的总字节长度，包含固定帧头。</param>
/// <param name="Checkpoint">事件携带的分区位点。</param>
public readonly record struct CdcEventSpoolAppendResult(
    long FrameOffset,
    int FrameLength,
    CdcCheckpoint Checkpoint);

/// <summary>
/// 一次有界 CDC 回放的结果。
/// </summary>
/// <param name="Events">本批次返回的事件。</param>
/// <param name="EncodedBytes">本批次事件正文的 UTF-8 字节数。</param>
/// <param name="HasMore">达到本次边界后是否仍有可回放事件。</param>
public sealed record CdcEventSpoolBatch(
    IReadOnlyList<CdcEvent> Events,
    long EncodedBytes,
    bool HasMore);

/// <summary>
/// CDC spool 当前的只读状态快照。
/// </summary>
/// <param name="EventCount">文件中当前保留的帧数。</param>
/// <param name="StoredBytes">文件中当前保留的总字节数。</param>
/// <param name="AcknowledgedCheckpoints">每个分区已确认的最大位点。</param>
public sealed record CdcEventSpoolState(
    long EventCount,
    long StoredBytes,
    IReadOnlyDictionary<long, long> AcknowledgedCheckpoints);
