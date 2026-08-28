namespace SonnetDB.Engine;

/// <summary>
/// SQL 阻塞算子的内存与临时落盘配置。
/// </summary>
public sealed record SqlMemoryOptions
{
    /// <summary>默认配置：单查询 64 MiB、单数据库实例全局 256 MiB。</summary>
    public static SqlMemoryOptions Default { get; } = new();

    /// <summary>单条查询的默认阻塞算子内存上限（字节）。</summary>
    public long QueryLimitBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>同一数据库实例上所有并发查询共享的阻塞算子内存上限（字节）。</summary>
    public long GlobalLimitBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>单个数据库实例允许的 SQL 并行 worker 上限；并行算子不会超过此值。</summary>
    public int MaxParallelWorkers { get; init; } = Math.Clamp(Environment.ProcessorCount, 1, 8);

    /// <summary>估算输入行数达到此值后才评估并行收益。</summary>
    public long ParallelismMinRows { get; init; } = 2_048;

    /// <summary>每个并行 worker 预留的查询阻塞算子预算（字节）。</summary>
    public long ParallelWorkerMemoryBytes { get; init; } = 64L * 1024;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(QueryLimitBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(GlobalLimitBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxParallelWorkers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ParallelismMinRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ParallelWorkerMemoryBytes);
    }
}
