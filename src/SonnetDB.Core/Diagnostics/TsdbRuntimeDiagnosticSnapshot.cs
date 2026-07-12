namespace SonnetDB.Diagnostics;

/// <summary>
/// 单个 WAL 文件的运行时诊断元数据，不包含 WAL 记录内容。
/// </summary>
/// <param name="FileName">WAL 文件名，不包含数据库目录路径。</param>
/// <param name="FileLength">快照时文件长度（字节）。</param>
/// <param name="StartLsn">文件起始 LSN。</param>
/// <param name="LastLsn">已知时的文件末尾 LSN；未知时为 <c>null</c>。</param>
/// <param name="IsActive">是否为当前活跃 WAL 文件。</param>
public sealed record WalFileDiagnosticSnapshot(
    string FileName,
    long FileLength,
    long StartLsn,
    long? LastLsn,
    bool IsActive);

/// <summary>
/// 单个 <see cref="Engine.Tsdb"/> 实例的运行时诊断元数据。
/// </summary>
/// <param name="MemTableEstimatedBytes">活跃 MemTable 估算字节数。</param>
/// <param name="MemTablePointCount">活跃 MemTable 数据点计数。</param>
/// <param name="SegmentCount">当前活跃 Segment 数。</param>
/// <param name="PendingFlushTasks">排队或执行中的 Flush 请求数。</param>
/// <param name="PendingCompactionTasks">按当前稳定段快照可立即规划的 Compaction 任务数。</param>
/// <param name="CheckpointLsn">最近一次持久化 Checkpoint LSN。</param>
/// <param name="WalFiles">WAL 文件元数据列表，不包含记录内容。</param>
public sealed record TsdbRuntimeDiagnosticSnapshot(
    long MemTableEstimatedBytes,
    long MemTablePointCount,
    int SegmentCount,
    long PendingFlushTasks,
    int PendingCompactionTasks,
    long CheckpointLsn,
    IReadOnlyList<WalFileDiagnosticSnapshot> WalFiles);
