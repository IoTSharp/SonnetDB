namespace SonnetDB.Sql.Execution;

/// <summary>关系 DDL 事务 workspace 的容量、等待和清理预算。</summary>
internal sealed record SqlDdlTransactionOptions
{
    internal int MaximumTables { get; init; } = 256;
    internal int MaximumRows { get; init; } = 1_000_000;
    internal long MaximumBytes { get; init; } = 512L * 1024 * 1024;
    internal int CopyBatchRows { get; init; } = 512;
    internal int MaximumCleanupEntries { get; init; } = 65_536;
    internal int MaximumCleanupDepth { get; init; } = 24;
    internal TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan OperationTimeout { get; init; } = TimeSpan.FromMinutes(2);
    internal TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(15);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTables);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CopyBatchRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCleanupEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCleanupDepth);
        if (LockTimeout <= TimeSpan.Zero || LockTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(LockTimeout));
        if (OperationTimeout <= TimeSpan.Zero || OperationTimeout > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(OperationTimeout));
        if (CleanupTimeout <= TimeSpan.Zero || CleanupTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(CleanupTimeout));
    }
}
