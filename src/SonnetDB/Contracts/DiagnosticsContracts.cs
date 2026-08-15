namespace SonnetDB.Contracts;

/// <summary>
/// 单条慢查询诊断记录。
/// </summary>
/// <param name="TimestampMs">记录时间（Unix 毫秒，UTC）。</param>
/// <param name="Database">数据库名；控制面 SQL 为 <c>__control</c>。</param>
/// <param name="Sql">截断后的原始 SQL。</param>
/// <param name="NormalizedSql">移除注释并将字面量替换为占位符后的 SQL。</param>
/// <param name="Fingerprint">归一化 SQL 的稳定 SHA-256 短指纹。</param>
/// <param name="ElapsedMs">执行耗时（毫秒）。</param>
/// <param name="RowCount">返回行数。</param>
/// <param name="RecordsAffected">受影响行数。</param>
/// <param name="Failed">是否执行失败。</param>
/// <param name="Severity">慢查询等级。</param>
public sealed record SlowQueryDiagnosticEntry(
    long TimestampMs,
    string Database,
    string Sql,
    string NormalizedSql,
    string Fingerprint,
    double ElapsedMs,
    long RowCount,
    int RecordsAffected,
    bool Failed,
    string Severity)
{
    /// <summary>运行时实际使用的访问路径；多个输入不同时为 <c>mixed</c>。</summary>
    public string? AccessPath { get; init; }

    /// <summary>实际使用的索引名；多个索引不同时为 <c>mixed</c>。</summary>
    public string? IndexName { get; init; }

    /// <summary>未使用快速路径时的稳定回退原因。</summary>
    public string? FallbackReason { get; init; }

    /// <summary>存储访问产生的候选行数。</summary>
    public long CandidateRows { get; init; }

    /// <summary>执行完整残余谓词的候选行数。</summary>
    public long ExaminedRows { get; init; }

    /// <summary>按候选行解码口径记录的逻辑读取次数。</summary>
    public long LogicalReads { get; init; }

    /// <summary>按受影响行口径记录的逻辑写入次数。</summary>
    public long LogicalWrites { get; init; }

    /// <summary>Segment block 等实际物理读取次数。</summary>
    public long PhysicalReads { get; init; }

    /// <summary>实际物理读取的 payload 字节数。</summary>
    public long PhysicalReadBytes { get; init; }

    /// <summary>WAL record 等实际物理写入次数。</summary>
    public long PhysicalWrites { get; init; }

    /// <summary>实际物理写入的 record 字节数。</summary>
    public long PhysicalWriteBytes { get; init; }

    /// <summary>数据库级 SQL permit 队列等待毫秒数。</summary>
    public double QueueWaitMs { get; init; }

    /// <summary>关系事务管理器锁累计等待毫秒数。</summary>
    public double TableLockWaitMs { get; init; }

    /// <summary>KV keyspace 锁累计等待毫秒数。</summary>
    public double KvLockWaitMs { get; init; }

    /// <summary>WAL fsync 累计耗时（毫秒）。</summary>
    public double WalFsyncMs { get; init; }

    /// <summary>WAL fsync 次数。</summary>
    public int WalFsyncCount { get; init; }

    /// <summary>不含网络响应编码的 Core 执行耗时（毫秒）。</summary>
    public double ExecutionElapsedMs { get; init; }

    /// <summary>同步执行线程的分配字节数；跨线程执行时为 -1。</summary>
    public long AllocatedBytes { get; init; } = -1;

    /// <summary>执行窗口内观测到的 Gen0 GC 次数。</summary>
    public int Gen0Collections { get; init; }

    /// <summary>执行窗口内观测到的 Gen1 GC 次数。</summary>
    public int Gen1Collections { get; init; }

    /// <summary>执行窗口内观测到的 Gen2 GC 次数。</summary>
    public int Gen2Collections { get; init; }
}

/// <summary>
/// 慢查询列表响应。
/// </summary>
/// <param name="Enabled">服务端是否启用慢查询采集。</param>
/// <param name="ThresholdMs">基础阈值（毫秒）。</param>
/// <param name="WarningThresholdMs">警告阈值（毫秒）。</param>
/// <param name="CriticalThresholdMs">严重阈值（毫秒）。</param>
/// <param name="Capacity">进程内环形缓冲容量。</param>
/// <param name="Count">当前调用方可见的缓冲记录数。</param>
/// <param name="Items">按时间倒序返回的记录。</param>
public sealed record SlowQueryListResponse(
    bool Enabled,
    int ThresholdMs,
    int WarningThresholdMs,
    int CriticalThresholdMs,
    int Capacity,
    int Count,
    IReadOnlyList<SlowQueryDiagnosticEntry> Items);

/// <summary>
/// 按数据库与归一化 SQL 指纹聚合的查询统计。
/// </summary>
/// <param name="Database">数据库名。</param>
/// <param name="NormalizedSql">归一化 SQL。</param>
/// <param name="Fingerprint">稳定 SQL 指纹。</param>
/// <param name="Count">自进程启动以来调用次数的 Int32 饱和值。</param>
/// <param name="FailedCount">自进程启动以来失败次数的 Int32 饱和值。</param>
/// <param name="P50Ms">最近最多 128 次执行的 P50 耗时（毫秒）。</param>
/// <param name="P95Ms">最近最多 128 次执行的 P95 耗时（毫秒）。</param>
/// <param name="MaxMs">自进程启动以来的最大耗时（毫秒）。</param>
/// <param name="LastSeenTimestampMs">最近一次出现时间（Unix 毫秒，UTC）。</param>
public sealed record TopQueryDiagnosticEntry(
    string Database,
    string NormalizedSql,
    string Fingerprint,
    int Count,
    int FailedCount,
    double P50Ms,
    double P95Ms,
    double MaxMs,
    long LastSeenTimestampMs)
{
    /// <summary>独立聚合器自进程启动以来记录的精确调用次数。</summary>
    public long LifetimeCount { get; init; }

    /// <summary>独立聚合器自进程启动以来记录的失败次数。</summary>
    public long LifetimeFailedCount { get; init; }

    /// <summary>聚合生命周期内的实际访问路径；不一致时为 <c>mixed</c>。</summary>
    public string? AccessPath { get; init; }

    /// <summary>聚合生命周期内的索引名；不一致时为 <c>mixed</c>。</summary>
    public string? IndexName { get; init; }

    /// <summary>聚合生命周期内的稳定回退原因；不一致时为 <c>mixed</c>。</summary>
    public string? FallbackReason { get; init; }

    /// <summary>累计候选行数。</summary>
    public long CandidateRows { get; init; }

    /// <summary>累计检查行数。</summary>
    public long ExaminedRows { get; init; }

    /// <summary>累计返回行数。</summary>
    public long ReturnedRows { get; init; }

    /// <summary>累计逻辑读取次数。</summary>
    public long LogicalReads { get; init; }

    /// <summary>累计逻辑写入次数。</summary>
    public long LogicalWrites { get; init; }

    /// <summary>累计物理读取次数。</summary>
    public long PhysicalReads { get; init; }

    /// <summary>累计物理读取 payload 字节数。</summary>
    public long PhysicalReadBytes { get; init; }

    /// <summary>累计物理写入次数。</summary>
    public long PhysicalWrites { get; init; }

    /// <summary>累计物理写入 record 字节数。</summary>
    public long PhysicalWriteBytes { get; init; }

    /// <summary>累计 SQL permit 队列等待毫秒数。</summary>
    public double QueueWaitMs { get; init; }

    /// <summary>累计关系表与 KV 锁等待毫秒数。</summary>
    public double LockWaitMs { get; init; }

    /// <summary>累计关系事务管理器锁等待毫秒数。</summary>
    public double TableLockWaitMs { get; init; }

    /// <summary>累计 KV keyspace 锁等待毫秒数。</summary>
    public double KvLockWaitMs { get; init; }

    /// <summary>累计 WAL fsync 耗时（毫秒）。</summary>
    public double WalFsyncMs { get; init; }

    /// <summary>累计 WAL fsync 次数。</summary>
    public long WalFsyncCount { get; init; }

    /// <summary>累计不含网络响应编码的 Core 执行耗时（毫秒）。</summary>
    public double ExecutionElapsedMs { get; init; }

    /// <summary>累计同步执行线程分配字节数；跨线程未知样本不计入。</summary>
    public long AllocatedBytes { get; init; }

    /// <summary>累计 Gen0 GC 次数。</summary>
    public long Gen0Collections { get; init; }

    /// <summary>累计 Gen1 GC 次数。</summary>
    public long Gen1Collections { get; init; }

    /// <summary>累计 Gen2 GC 次数。</summary>
    public long Gen2Collections { get; init; }
}

/// <summary>
/// Top-N 查询统计响应。
/// </summary>
/// <param name="Enabled">服务端是否启用慢查询采集。</param>
/// <param name="Capacity">进程内环形缓冲容量。</param>
/// <param name="SampleCount">当前调用方可归属累计查询数的 Int32 饱和值。</param>
/// <param name="Items">按 P95、最大耗时与出现次数倒序排列的聚合项。</param>
public sealed record TopQueryListResponse(
    bool Enabled,
    int Capacity,
    int SampleCount,
    IReadOnlyList<TopQueryDiagnosticEntry> Items)
{
    /// <summary>有界查询指纹聚合器的分组容量。</summary>
    public int AggregateCapacity { get; init; }

    /// <summary>有界指纹聚合器可归属到当前调用方的累计查询数。</summary>
    public long LifetimeSampleCount { get; init; }

    /// <summary>聚合容量耗尽后无法归属到具体指纹的查询数；仅管理员视图返回全局值。</summary>
    public long UnattributedSampleCount { get; init; }
}

/// <summary>
/// Diagnostic Dump 顶层响应。
/// </summary>
/// <param name="TimestampUtcMs">采集时间（Unix 毫秒，UTC）。</param>
/// <param name="Process">进程级摘要。</param>
/// <param name="Gc">GC 内存摘要。</param>
/// <param name="ThreadPool">ThreadPool 摘要。</param>
/// <param name="Copilot">Copilot 运行时摘要。</param>
/// <param name="Databases">逐数据库运行时 metadata。</param>
public sealed record DiagnosticDumpResponse(
    long TimestampUtcMs,
    ProcessDiagnosticSnapshot Process,
    GcDiagnosticSnapshot Gc,
    ThreadPoolDiagnosticSnapshot ThreadPool,
    CopilotRuntimeDiagnosticSnapshot Copilot,
    IReadOnlyList<DatabaseDiagnosticSnapshot> Databases);

/// <summary>
/// 进程级诊断摘要。
/// </summary>
/// <param name="ProcessId">当前进程 ID。</param>
/// <param name="UptimeMs">当前进程运行时长近似值（毫秒）。</param>
/// <param name="WorkingSetBytes">当前进程工作集字节数。</param>
public sealed record ProcessDiagnosticSnapshot(int ProcessId, long UptimeMs, long WorkingSetBytes);

/// <summary>
/// GC 内存诊断摘要。
/// </summary>
public sealed record GcDiagnosticSnapshot(
    long TotalMemoryBytes,
    long HeapSizeBytes,
    long FragmentedBytes,
    long TotalCommittedBytes,
    long MemoryLoadBytes,
    long TotalAvailableMemoryBytes,
    long HighMemoryLoadThresholdBytes,
    long PinnedObjectsCount,
    long FinalizationPendingCount,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

/// <summary>
/// ThreadPool 诊断摘要。
/// </summary>
public sealed record ThreadPoolDiagnosticSnapshot(
    int ThreadCount,
    long PendingWorkItemCount,
    long CompletedWorkItemCount,
    int AvailableWorkerThreads,
    int AvailableCompletionPortThreads,
    int MinWorkerThreads,
    int MinCompletionPortThreads,
    int MaxWorkerThreads,
    int MaxCompletionPortThreads);

/// <summary>
/// Copilot 运行时诊断摘要。
/// </summary>
/// <param name="InFlightSessions">当前正在执行的 Copilot 会话请求数。</param>
public sealed record CopilotRuntimeDiagnosticSnapshot(long InFlightSessions);

/// <summary>
/// 单数据库运行时诊断 metadata。
/// </summary>
public sealed record DatabaseDiagnosticSnapshot(
    string Name,
    long MemTableEstimatedBytes,
    long MemTablePointCount,
    int SegmentCount,
    long PendingFlushTasks,
    int PendingCompactionTasks,
    long CheckpointLsn,
    IReadOnlyList<WalFileDiagnosticEntry> WalFiles);

/// <summary>
/// WAL 文件诊断 metadata，不包含文件路径或记录内容。
/// </summary>
public sealed record WalFileDiagnosticEntry(
    string FileName,
    long FileLength,
    long StartLsn,
    long? LastLsn,
    bool IsActive);
