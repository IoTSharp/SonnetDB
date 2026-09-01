using System.Diagnostics;
using SonnetDB.Routines;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// 单条 SQL 的有界执行证据收集器。仅在调用方显式提供时启用，不保存参数值、SQL 文本或行内容。
/// </summary>
internal sealed class SqlExecutionMetrics
{
    // 完成冻结最多消耗固定迭代数和墙钟预算，避免异常 writer 阻塞查询收尾。
    private const int PhysicalReadSnapshotMaxAttempts = 64;
    private static readonly TimeSpan PhysicalReadSnapshotWaitBudget = TimeSpan.FromMilliseconds(5);
    private readonly object _sync = new();
    private readonly int _originThreadId = Environment.CurrentManagedThreadId;
    private readonly long _allocatedBytesStart = GC.GetAllocatedBytesForCurrentThread();
    private readonly int _gen0Start = GC.CollectionCount(0);
    private readonly int _gen1Start = GC.CollectionCount(1);
    private readonly int _gen2Start = GC.CollectionCount(2);
    private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
    private SqlExecutionMetricsSnapshot? _completed;
    private string? _accessPath;
    private string? _indexName;
    private string? _fallbackReason;
    private long _candidateRows;
    private long _examinedRows;
    private long _logicalReads;
    private long _physicalReads;
    private long _physicalReadBytes;
    private long _physicalReadStarted;
    private long _physicalReadCompleted;
    private long _physicalWrites;
    private long _physicalWriteBytes;
    private double _tableLockWaitMs;
    private double _kvLockWaitMs;
    private double _walFsyncMs;
    private int _walFsyncCount;
    private long _peakMemoryBytes;
    private long _spillCount;
    private long _spillBytes;
    private long _spillCleanupFailures;
    private string? _parallelOperator;
    private string? _parallelFallbackReason;
    private int _parallelWorkerCount = 1;
    private long _parallelCompletedItems;
    private long _estimatedRows;

    /// <summary>记录运行时实际选择的访问路径。</summary>
    internal void RecordAccessPath(string accessPath, string? indexName, string? fallbackReason)
    {
        lock (_sync)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(accessPath);
            _accessPath = Merge(_accessPath, accessPath);
            if (indexName is not null)
                _indexName = Merge(_indexName, indexName);
            if (fallbackReason is not null)
                _fallbackReason = Merge(_fallbackReason, fallbackReason);
        }
    }

    /// <summary>记录存储访问产生的候选行；逻辑读取按候选行解码次数计量。</summary>
    internal void RecordCandidateRows(long count)
    {
        lock (_sync)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            _candidateRows = checked(_candidateRows + count);
            _logicalReads = checked(_logicalReads + count);
        }
    }

    /// <summary>记录执行完整残余谓词的候选行。</summary>
    internal void RecordExaminedRows(long count)
    {
        lock (_sync)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            _examinedRows = checked(_examinedRows + count);
        }
    }

    /// <summary>在同一临界区批量累计候选、逻辑读取和残余谓词检查行数。</summary>
    internal void RecordCandidateAndExaminedRows(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (_sync)
        {
            _candidateRows = checked(_candidateRows + count);
            _logicalReads = checked(_logicalReads + count);
            _examinedRows = checked(_examinedRows + count);
        }
    }

    /// <summary>记录一次物理读取及其 payload 字节数。</summary>
    internal void RecordPhysicalRead(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        AddCheckedAtomic(ref _physicalReadStarted, 1);
        try
        {
            AddCheckedAtomic(ref _physicalReads, 1);
            try
            {
                AddCheckedAtomic(ref _physicalReadBytes, bytes);
            }
            catch (OverflowException)
            {
                // 字节累计失败时撤销本次读计数，保证失败样本不会污染最终快照。
                Interlocked.Decrement(ref _physicalReads);
                throw;
            }
        }
        finally
        {
            // 成功与失败 writer 都必须发布完成序号，避免 Complete 永久等待。
            AddCheckedAtomic(ref _physicalReadCompleted, 1);
        }
    }

    /// <summary>记录一次物理写入及其字节数。</summary>
    internal void RecordPhysicalWrite(long bytes)
    {
        lock (_sync)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            _physicalWrites++;
            _physicalWriteBytes = checked(_physicalWriteBytes + bytes);
        }
    }

    /// <summary>记录固定类别的关键存储锁等待。</summary>
    internal void RecordLockWait(bool tableManager, double elapsedMs)
    {
        lock (_sync)
        {
            if (tableManager)
                _tableLockWaitMs += elapsedMs;
            else
                _kvLockWaitMs += elapsedMs;
        }
    }

    /// <summary>记录一次 WAL fsync。</summary>
    internal void RecordWalFsync(double elapsedMs)
    {
        lock (_sync)
        {
            _walFsyncCount++;
            _walFsyncMs += elapsedMs;
        }
    }

    /// <summary>记录阻塞算子查询级内存峰值。</summary>
    internal void RecordPeakMemory(long bytes)
    {
        lock (_sync)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            if (bytes > _peakMemoryBytes)
                _peakMemoryBytes = bytes;
        }
    }

    /// <summary>记录一次 spill 及其写入字节数。</summary>
    internal void RecordSpill(long bytes)
    {
        lock (_sync)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            _spillCount++;
            _spillBytes = checked(_spillBytes + bytes);
        }
    }

    /// <summary>在不新增 spill 事件的情况下累计后续写入字节。</summary>
    internal void RecordSpillBytes(long bytes)
    {
        lock (_sync)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            _spillBytes = checked(_spillBytes + bytes);
        }
    }

    /// <summary>记录一次临时文件删除失败。</summary>
    internal void RecordSpillCleanupFailure()
    {
        lock (_sync)
            _spillCleanupFailures++;
    }

    /// <summary>记录计划估算的候选行数。</summary>
    internal void RecordEstimatedRows(long rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        lock (_sync)
            _estimatedRows = Math.Max(_estimatedRows, rows);
    }

    /// <summary>记录受控并行决策及 worker 上限。</summary>
    internal void RecordParallelDecision(string operatorName, bool enabled, int workerCount, string? fallbackReason)
    {
        lock (_sync)
        {
            _parallelOperator = Merge(_parallelOperator, operatorName);
            _parallelWorkerCount = Math.Max(_parallelWorkerCount, enabled ? workerCount : 1);
            if (fallbackReason is not null)
                _parallelFallbackReason = Merge(_parallelFallbackReason, fallbackReason);
        }
    }

    /// <summary>记录并行映射完成的输入项。</summary>
    internal void RecordParallelCompleted(string operatorName, int itemCount)
    {
        lock (_sync)
        {
            _parallelOperator = Merge(_parallelOperator, operatorName);
            _parallelCompletedItems = checked(_parallelCompletedItems + itemCount);
        }
    }

    /// <summary>冻结执行结果；重复调用返回同一快照。</summary>
    internal SqlExecutionMetricsSnapshot Complete()
    {
        lock (_sync)
        {
            if (_completed is not null)
                return _completed;

            long allocatedBytes = Environment.CurrentManagedThreadId == _originThreadId
                ? Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - _allocatedBytesStart)
                : -1;
            (long physicalReads, long physicalReadBytes, bool physicalReadSnapshotComplete) =
                CapturePhysicalReadSnapshot();
            _completed = new SqlExecutionMetricsSnapshot(
                _accessPath,
                _indexName,
                _fallbackReason,
                _candidateRows,
                _examinedRows,
                _logicalReads,
                physicalReads,
                physicalReadBytes,
                _physicalWrites,
                _physicalWriteBytes,
                _tableLockWaitMs,
                _kvLockWaitMs,
                _walFsyncMs,
                _walFsyncCount,
                _peakMemoryBytes,
                _spillCount,
                _spillBytes,
                _spillCleanupFailures,
                Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds,
                allocatedBytes,
                Math.Max(0, GC.CollectionCount(0) - _gen0Start),
                Math.Max(0, GC.CollectionCount(1) - _gen1Start),
                Math.Max(0, GC.CollectionCount(2) - _gen2Start),
                _estimatedRows,
                _parallelOperator,
                _parallelWorkerCount > 1,
                _parallelWorkerCount,
                _parallelCompletedItems,
                _parallelFallbackReason,
                physicalReadSnapshotComplete);
            return _completed;
        }
    }

    private static string Merge(string? current, string next)
        => current is null || string.Equals(current, next, StringComparison.Ordinal)
            ? next
            : "mixed";

    /// <summary>在有限等待预算内读取一致的物理读快照；无法稳定时返回零下界并标记降级。</summary>
    private (long Reads, long Bytes, bool Complete) CapturePhysicalReadSnapshot()
    {
        long startedTimestamp = Stopwatch.GetTimestamp();
        var spinner = new SpinWait();
        for (var attempt = 0; attempt < PhysicalReadSnapshotMaxAttempts; attempt++)
        {
            long completedBefore = Volatile.Read(ref _physicalReadCompleted);
            long startedBefore = Volatile.Read(ref _physicalReadStarted);
            if (startedBefore != completedBefore)
            {
                if (Stopwatch.GetElapsedTime(startedTimestamp) >= PhysicalReadSnapshotWaitBudget)
                    break;

                spinner.SpinOnce();
                continue;
            }

            long reads = Volatile.Read(ref _physicalReads);
            long bytes = Volatile.Read(ref _physicalReadBytes);
            long completedAfter = Volatile.Read(ref _physicalReadCompleted);
            long startedAfter = Volatile.Read(ref _physicalReadStarted);

            // 前后序号完全一致，才能排除读取期间启动或完成的 writer。
            if (startedBefore == startedAfter
                && completedBefore == completedAfter
                && startedAfter == completedAfter)
            {
                return (reads, bytes, true);
            }

            if (Stopwatch.GetElapsedTime(startedTimestamp) >= PhysicalReadSnapshotWaitBudget)
                break;

            spinner.SpinOnce();
        }

        // 在途 writer 可能只更新了次数或字节；零值是现有数值合同可表达的唯一安全下界。
        return (0, 0, false);
    }

    /// <summary>使用 CAS 原子累加非负计量值，并在提交前检查溢出。</summary>
    private static void AddCheckedAtomic(ref long target, long value)
    {
        while (true)
        {
            long current = Volatile.Read(ref target);
            long next = checked(current + value);

            // 竞争失败时基于最新值重算，避免 Interlocked.Add 的无检查回绕。
            if (Interlocked.CompareExchange(ref target, next, current) == current)
                return;
        }
    }
}

/// <summary>单条 SQL 冻结后的执行证据。</summary>
internal sealed record SqlExecutionMetricsSnapshot(
    string? AccessPath,
    string? IndexName,
    string? FallbackReason,
    long CandidateRows,
    long ExaminedRows,
    long LogicalReads,
    long PhysicalReads,
    long PhysicalReadBytes,
    long PhysicalWrites,
    long PhysicalWriteBytes,
    double TableLockWaitMs,
    double KvLockWaitMs,
    double WalFsyncMs,
    int WalFsyncCount,
    long PeakMemoryBytes,
    long SpillCount,
    long SpillBytes,
    long SpillCleanupFailures,
    double ExecutionElapsedMs,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long EstimatedRows = 0,
    string? ParallelOperator = null,
    bool ParallelismEnabled = false,
    int ParallelWorkerCount = 1,
    long ParallelCompletedItems = 0,
    string? ParallelFallbackReason = null,
    bool PhysicalReadSnapshotComplete = true)
{
    /// <summary>实际/估算候选行数比率；估算为零时返回 1。</summary>
    public double ActualToEstimatedRowsRatio => EstimatedRows <= 0 ? 1 : (double)CandidateRows / EstimatedRows;
}

/// <summary>把存储与执行器中的低开销计数转发到当前 SQL 根作用域。</summary>
internal static class SqlExecutionTelemetry
{
    internal static bool IsEnabled => Current is not null;

    private static readonly AsyncLocal<SqlExecutionMetrics?> OverrideSlot = new();

    private static SqlExecutionMetrics? Current
        => OverrideSlot.Value ?? RoutineExecutionContext.Current?.Options.Metrics;

    internal static Scope Enter(SqlExecutionMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        SqlExecutionMetrics? previous = OverrideSlot.Value;
        OverrideSlot.Value = metrics;
        return new Scope(previous);
    }

    internal static void RecordAccessPath(string accessPath, string? indexName = null, string? fallbackReason = null)
        => Current?.RecordAccessPath(accessPath, indexName, fallbackReason);

    internal static void RecordCandidateRows(long count)
    {
        Current?.RecordCandidateRows(count);
        SqlQueryResources.Current?.RecordActualRows(count);
    }

    internal static void RecordExaminedRows(long count) => Current?.RecordExaminedRows(count);

    /// <summary>批量记录同时作为候选和残余谓词输入的关系行，避免全扫路径逐行争用指标锁。</summary>
    internal static void RecordCandidateAndExaminedRows(long count)
    {
        Current?.RecordCandidateAndExaminedRows(count);
        SqlQueryResources.Current?.RecordActualRows(count);
    }

    internal static void RecordPhysicalRead(long bytes) => Current?.RecordPhysicalRead(bytes);

    internal static void RecordPhysicalWrite(long bytes) => Current?.RecordPhysicalWrite(bytes);

    internal static void RecordLockWait(bool tableManager, double elapsedMs)
        => Current?.RecordLockWait(tableManager, elapsedMs);

    internal static void RecordWalFsync(double elapsedMs) => Current?.RecordWalFsync(elapsedMs);

    internal static void RecordPeakMemory(long bytes) => Current?.RecordPeakMemory(bytes);

    internal static void RecordSpill(long bytes) => Current?.RecordSpill(bytes);

    internal static void RecordSpillBytes(long bytes) => Current?.RecordSpillBytes(bytes);

    internal static void RecordSpillCleanupFailure() => Current?.RecordSpillCleanupFailure();

    internal static void RecordEstimatedRows(long rows)
    {
        Current?.RecordEstimatedRows(rows);
        SqlQueryResources.Current?.RecordEstimatedRows(rows);
    }

    internal static void RecordParallelDecision(string operatorName, bool enabled, int workerCount, string? fallbackReason)
        => Current?.RecordParallelDecision(operatorName, enabled, workerCount, fallbackReason);

    internal static void RecordParallelCompleted(string operatorName, int itemCount)
        => Current?.RecordParallelCompleted(operatorName, itemCount);

    internal readonly struct Scope : IDisposable
    {
        private readonly SqlExecutionMetrics? _previous;

        internal Scope(SqlExecutionMetrics? previous) => _previous = previous;

        public void Dispose() => OverrideSlot.Value = _previous;
    }
}
