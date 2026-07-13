namespace SonnetDB.Kv;

/// <summary>Keyspace generation 切换结果。</summary>
/// <param name="RemovedKeys">切换前可见 key 数量。</param>
/// <param name="Generation">切换后的 generation。</param>
/// <param name="CleanupPendingFiles">等待后台回收的旧 state 文件数。</param>
public sealed record KvClearResult(int RemovedKeys, long Generation, int CleanupPendingFiles);

/// <summary>Keyspace 后台回收任务状态。</summary>
/// <param name="Generation">任务所属 generation。</param>
/// <param name="PendingFiles">尚未回收的旧 state 文件数。</param>
/// <param name="PendingBytes">尚未回收的旧 state 文件字节数。</param>
public sealed record KvCleanupStatus(long Generation, int PendingFiles, long PendingBytes);

/// <summary>数据库级 KV generation 后台维护状态。</summary>
/// <param name="CleanupRounds">已执行的实际回收轮数。</param>
/// <param name="RemovedFiles">进程启动后实际删除的旧 state 文件数。</param>
/// <param name="PendingFiles">当前 manifest 中仍待处理的文件数。</param>
/// <param name="PendingBytes">当前仍待回收的文件字节数。</param>
/// <param name="LastBytesPerSecond">最近一轮实际回收速率；尚未删除文件时为 0。</param>
/// <param name="ThrottledRounds">因资源压力暂停的轮数。</param>
/// <param name="LastThrottleReason">最近一次节流原因；当前未节流时为 null。</param>
/// <param name="LastErrorType">最近一次后台维护异常类型；无异常时为 null。</param>
public sealed record KvMaintenanceStatus(
    long CleanupRounds,
    long RemovedFiles,
    long PendingFiles,
    long PendingBytes,
    double LastBytesPerSecond,
    long ThrottledRounds,
    string? LastThrottleReason,
    string? LastErrorType);

internal readonly record struct KvCleanupRoundResult(
    int ProcessedEntries,
    int DeletedFiles,
    long RemovedBytes,
    int PendingFiles,
    long PendingBytes)
{
    public static KvCleanupRoundResult Empty { get; } = new(0, 0, 0, 0, 0);

    public static KvCleanupRoundResult operator +(KvCleanupRoundResult left, KvCleanupRoundResult right)
        => new(
            checked(left.ProcessedEntries + right.ProcessedEntries),
            checked(left.DeletedFiles + right.DeletedFiles),
            checked(left.RemovedBytes + right.RemovedBytes),
            checked(left.PendingFiles + right.PendingFiles),
            checked(left.PendingBytes + right.PendingBytes));
}

internal enum KvCleanupThrottleReason
{
    None,
    ActiveQueries,
    FlushPending,
    CpuPressure,
    MemoryPressure,
}
