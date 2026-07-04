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
    /// 每个 Topic 最多保持打开的历史段读取句柄数量。当前实现仅主动保持写入段打开，此值保留给 LRU 读缓存策略。
    /// </summary>
    public int SegmentCacheSize { get; init; } = 8;
}
