namespace SonnetMQ;

/// <summary>
/// SonnetMQ 本地队列选项。
/// </summary>
public sealed record SonnetMqOptions
{
    /// <summary>
    /// 默认段大小：64 MiB。
    /// </summary>
    public const long DefaultSegmentMaxBytes = 64L * 1024L * 1024L;

    /// <summary>
    /// 队列目录或单文件路径。
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// 存储打开模式。默认使用单目录模式。
    /// </summary>
    public SonnetMqOpenMode OpenMode { get; init; } = SonnetMqOpenMode.Directory;

    /// <summary>
    /// 发布消息后是否立即 flush 到操作系统页缓存。
    /// </summary>
    public bool FlushOnPublish { get; init; } = true;

    /// <summary>
    /// 发布消息后是否调用 durable flush。吞吐优先场景建议关闭，由宿主批量刷盘。
    /// </summary>
    public bool SyncOnPublish { get; init; }

    /// <summary>
    /// 是否启用发布组提交（leader-flush 合并刷盘）。默认启用。
    /// <para>
    /// 启用后，并发发布到同一 topic 的多个 publish 会把各自的落盘/ fsync 合并到一次刷盘：
    /// 一个「leader」执行一次 <c>Flush</c> 覆盖此刻已追加的全部记录，其字节已被覆盖的并发发布者
    /// 直接跳过自己的刷盘系统调用。合并窗口 = 该次刷盘（<see cref="FlushOnPublish"/> 的 OS flush 或
    /// <see cref="SyncOnPublish"/> 的 fsync）本身的在途时长，<b>不引入任何定时等待</b>——单发布者无争用
    /// 时立即刷盘，延迟与逐条刷盘一致；仅在并发争用下减少刷盘次数。
    /// </para>
    /// <para>
    /// 持久性语义不变：每个 publish 仍在其数据被刷盘到所配置的持久层（OS 页缓存或磁盘）后才返回。
    /// 关闭时回退为每次 publish 各自刷盘（严格逐条隔离）。单文件模式（所有 topic 共享一个流）始终逐条刷盘。
    /// </para>
    /// </summary>
    public bool GroupCommitPublish { get; init; } = true;

    /// <summary>
    /// Topic 内 offset 稀疏索引步长。值越小 pull 定位越快，但内存占用越高。
    /// </summary>
    public int OffsetIndexStride { get; init; } = 1024;

    /// <summary>
    /// 单个 Topic 段文件最大字节数。目录模式下达到该大小后滚动新段。
    /// </summary>
    public long SegmentMaxBytes { get; init; } = DefaultSegmentMaxBytes;

    /// <summary>
    /// Retention 按时间保留的最长消息年龄；为空表示不按时间裁剪。
    /// </summary>
    public TimeSpan? RetentionMaxAge { get; init; }

    /// <summary>
    /// Retention 按 Topic 保留的最大段文件字节数；为空表示不按大小裁剪。
    /// </summary>
    public long? RetentionMaxBytes { get; init; }

    /// <summary>
    /// 后台 RetentionWorker 检查间隔；小于等于零表示禁用后台 worker，仅保留手动 <c>TrimRetention()</c>。
    /// </summary>
    public TimeSpan RetentionInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 是否基于所有消费者组已确认的最小 offset 裁剪已消费消息。
    /// </summary>
    public bool TrimAcknowledgedMessages { get; init; } = true;

    /// <summary>
    /// Ack retention 每次推进 tombstone 的最小 offset 间隔，用于避免逐条 ack 产生 tombstone 写放大。
    /// </summary>
    public long AckRetentionMinOffsetDelta { get; init; } = 1024;

    /// <summary>
    /// 冷读时每个 Topic 最多保持打开的历史段只读句柄数量（LRU 上限）。
    /// <para>
    /// 目录模式下，当被驱逐的冷 offset 触发按需读盘时，其所在段以只读 <c>SafeFileHandle</c> 打开并缓存，
    /// 超过该上限按最近最少使用关闭最久未用句柄。活跃写入段不进入该只读缓存（避免与写句柄冲突）。
    /// 单文件模式不使用该缓存（全量常驻内存）。
    /// </para>
    /// </summary>
    public int SegmentCacheSize { get; init; } = 8;

    /// <summary>
    /// 单个 Topic 常驻内存「热尾部」的 payload 累计字节上限（仅目录模式生效），默认 64 MiB。
    /// <para>
    /// 追加消息使热尾 payload 超过该上限时，从内存头部驱逐最老消息，仅保留有界的近期热尾 + offset
    /// 稀疏位置索引；被驱逐的冷 offset 在 <c>Pull</c> 时经位置索引定位、通过 <see cref="SegmentCacheSize"/>
    /// 有界只读句柄 LRU 从段文件按需读盘。这把长期高吞吐（消费者跟不上或无消费者）topic 的内存占用
    /// 从「随消息数无界增长」改为「有界」，修复 OOM。
    /// </para>
    /// <para>
    /// 默认值较大，因此常规小规模负载全部保持全量常驻、行为不变，仅在真实积压超过该阈值时才触发冷读。
    /// 冷读不改变对外 offset / retention / replay / durability 语义。<b>单文件模式不生效</b>（共享单流、
    /// 无 per-topic 段边界，延续全量常驻惯例）。
    /// </para>
    /// </summary>
    public long HotTailMaxBytes { get; init; } = 64L * 1024L * 1024L;
}
