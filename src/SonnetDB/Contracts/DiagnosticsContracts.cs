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
    string Severity);

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
/// <param name="Count">缓冲窗口内的慢查询样本数。</param>
/// <param name="FailedCount">失败样本数。</param>
/// <param name="P50Ms">P50 耗时（毫秒）。</param>
/// <param name="P95Ms">P95 耗时（毫秒）。</param>
/// <param name="MaxMs">最大耗时（毫秒）。</param>
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
    long LastSeenTimestampMs);

/// <summary>
/// Top-N 查询统计响应。
/// </summary>
/// <param name="Enabled">服务端是否启用慢查询采集。</param>
/// <param name="Capacity">进程内环形缓冲容量。</param>
/// <param name="SampleCount">当前调用方可见的慢查询样本数。</param>
/// <param name="Items">按 P95、最大耗时与出现次数倒序排列的聚合项。</param>
public sealed record TopQueryListResponse(
    bool Enabled,
    int Capacity,
    int SampleCount,
    IReadOnlyList<TopQueryDiagnosticEntry> Items);

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
