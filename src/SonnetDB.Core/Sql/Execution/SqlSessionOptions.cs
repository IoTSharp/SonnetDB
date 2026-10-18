namespace SonnetDB.Sql.Execution;

/// <summary>连接级临时关系对象的容量、复制与回收预算。</summary>
public sealed record SqlSessionOptions
{
    /// <summary>临时目录的父目录；为空时使用操作系统临时目录。</summary>
    public string? ScratchRootDirectory { get; init; }

    /// <summary>同一可见临时目录最多容纳的表数。</summary>
    public int MaximumTemporaryTables { get; init; } = 64;

    /// <summary>同一临时目录最多容纳的总行数。</summary>
    public int MaximumTemporaryRows { get; init; } = 100_000;

    /// <summary>临时行的保守内存估算字节上限。</summary>
    public long MaximumTemporaryBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>一次事务或语句工作副本最多复制的行数。</summary>
    public int MaximumCloneRows { get; init; } = 100_000;

    /// <summary>一次工作副本最多复制的保守估算字节数。</summary>
    public long MaximumCloneBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>会话中同时保留的临时目录代际上限，包含事务保存点。</summary>
    public int MaximumWorkspaces { get; init; } = 128;

    /// <summary>会话所有保留代际的累计临时行估算字节上限。</summary>
    public long MaximumRetainedBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>会话独占 scratch 中所有代际文件的累计物理字节上限；发布临时变更前扫描核对。</summary>
    public long MaximumScratchBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>创建、复制或校验一个临时工作目录的协作超时。</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>会话关闭时扫描和回收独占目录的协作超时。</summary>
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>一次回收最多检查的文件与目录总数。</summary>
    public int MaximumCleanupEntries { get; init; } = 8_192;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTemporaryTables);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTemporaryRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTemporaryBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCloneRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCloneBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumWorkspaces);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRetainedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumScratchBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCleanupEntries);
        if (OperationTimeout <= TimeSpan.Zero || OperationTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(OperationTimeout));
        if (CleanupTimeout <= TimeSpan.Zero || CleanupTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(CleanupTimeout));
        if (ScratchRootDirectory is not null && !Path.IsPathFullyQualified(ScratchRootDirectory))
            throw new ArgumentException("临时目录父路径必须是绝对路径。", nameof(ScratchRootDirectory));
    }
}
