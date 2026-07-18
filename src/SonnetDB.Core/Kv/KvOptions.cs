namespace SonnetDB.Kv;

/// <summary>
/// 内置 KV Keyspace 的存储选项。
/// </summary>
public sealed record KvOptions
{
    /// <summary>KV WAL 写缓冲区大小（字节），默认 64 KB。</summary>
    public int WalBufferSize { get; init; } = 64 * 1024;

    /// <summary>
    /// 是否在每次 <c>Put</c> / <c>Delete</c> 后强制 fsync KV WAL。
    /// 默认开启，优先保证小对象和内部元数据写入的崩溃安全性。
    /// </summary>
    public bool SyncWalOnEveryWrite { get; init; } = true;

    /// <summary>是否在 WAL 或内存覆盖层达到预算后自动创建后台检查点。</summary>
    public bool AutoCheckpointEnabled { get; init; } = true;

    /// <summary>
    /// 自动检查点允许的活跃 WAL 最大字节数，默认 256 MB；小于等于 0 时关闭字节预算。
    /// 单条记录可能使文件短暂超过该值，检查点期间的新写入记录在新的活跃 WAL 中。
    /// </summary>
    public long MaxWalBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// 自动检查点允许的可变内存覆盖层最大条目数，默认 100,000；小于等于 0 时关闭条目预算。
    /// </summary>
    public int MaxOverlayEntries { get; init; } = 100_000;

    /// <summary>
    /// 写入因检查点预算耗尽而等待的最长时间，默认 30 秒。超时或后台检查点失败时，
    /// 写入会在追加 WAL 之前失败，避免 WAL 与内存覆盖层无界增长。
    /// </summary>
    public TimeSpan CheckpointWriteBackpressureTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 关闭 keyspace 时等待在途检查点的最长时间，默认 5 秒。超时后关闭立即返回，
    /// 在途检查点结束时再释放它持有的只读 state；WAL 会在等待前同步关闭。
    /// </summary>
    public TimeSpan CheckpointShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>单个 key 的最大字节数，默认 64 KB。</summary>
    public int MaxKeyBytes { get; init; } = 64 * 1024;

    /// <summary>单个 value 的最大字节数，默认 16 MB。</summary>
    public int MaxValueBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>单次前缀扫描的默认最大返回行数。</summary>
    public int DefaultScanLimit { get; init; } = 1024;

    /// <summary>单条 batch-delete WAL record 最多包含的 key 数，默认 4096。</summary>
    public int BatchDeleteMaxKeys { get; init; } = 4096;

    /// <summary>单条 batch-delete WAL record 的 key payload 最大字节数，默认 4 MB。</summary>
    public int BatchDeleteMaxBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>是否启用 KV 后台过期清理线程。</summary>
    public bool ExpirerEnabled { get; init; } = true;

    /// <summary>KV 后台过期清理线程的轮询间隔。</summary>
    public TimeSpan ExpirerPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>KV 后台过期清理每轮每个已打开 keyspace 最多清理的 key 数。</summary>
    public int ExpirerBatchSize { get; init; } = 1024;

    /// <summary>KV 后台过期清理线程关闭等待超时。</summary>
    public TimeSpan ExpirerShutdownTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>是否启用 generation 旧文件后台回收。</summary>
    public bool CleanupEnabled { get; init; } = true;

    /// <summary>generation 旧文件后台回收轮询间隔。</summary>
    public TimeSpan CleanupPollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>每轮每个 keyspace 最多删除的旧 state 文件数，用于限制维护 I/O 峰值。</summary>
    public int CleanupMaxFilesPerRound { get; init; } = 2;

    /// <summary>是否在活跃查询期间暂停 generation 文件回收。</summary>
    public bool CleanupPauseWhenQueriesActive { get; init; } = true;

    /// <summary>是否在后台 flush 尚有排队或在途任务时暂停 generation 文件回收。</summary>
    public bool CleanupPauseWhenFlushPending { get; init; } = true;

    /// <summary>
    /// generation 文件回收允许的进程 CPU 使用率上限；默认 90，设为 0 可关闭 CPU 压力检查。
    /// 使用相邻维护轮次间的进程 CPU 时间采样，不创建常驻采样线程。
    /// </summary>
    public double CleanupMaxCpuPercent { get; init; } = 90;

    /// <summary>
    /// generation 文件回收允许的 GC 内存负载百分比上限；默认 90，设为 0 可关闭内存压力检查。
    /// </summary>
    public double CleanupMaxMemoryLoadPercent { get; init; } = 90;

    /// <summary>默认 KV 选项实例。</summary>
    public static KvOptions Default { get; } = new();
}
