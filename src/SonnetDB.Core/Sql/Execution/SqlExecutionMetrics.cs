using System.Diagnostics;
using SonnetDB.Routines;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// 单条 SQL 的有界执行证据收集器。仅在调用方显式提供时启用，不保存参数值、SQL 文本或行内容。
/// </summary>
internal sealed class SqlExecutionMetrics
{
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
    private long _physicalWrites;
    private long _physicalWriteBytes;
    private double _tableLockWaitMs;
    private double _kvLockWaitMs;
    private double _walFsyncMs;
    private int _walFsyncCount;

    /// <summary>记录运行时实际选择的访问路径。</summary>
    internal void RecordAccessPath(string accessPath, string? indexName, string? fallbackReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessPath);
        _accessPath = Merge(_accessPath, accessPath);
        if (indexName is not null)
            _indexName = Merge(_indexName, indexName);
        if (fallbackReason is not null)
            _fallbackReason = Merge(_fallbackReason, fallbackReason);
    }

    /// <summary>记录存储访问产生的候选行；逻辑读取按候选行解码次数计量。</summary>
    internal void RecordCandidateRows(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _candidateRows = checked(_candidateRows + count);
        _logicalReads = checked(_logicalReads + count);
    }

    /// <summary>记录执行完整残余谓词的候选行。</summary>
    internal void RecordExaminedRows(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _examinedRows = checked(_examinedRows + count);
    }

    /// <summary>记录一次物理读取及其 payload 字节数。</summary>
    internal void RecordPhysicalRead(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        _physicalReads++;
        _physicalReadBytes = checked(_physicalReadBytes + bytes);
    }

    /// <summary>记录一次物理写入及其字节数。</summary>
    internal void RecordPhysicalWrite(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        _physicalWrites++;
        _physicalWriteBytes = checked(_physicalWriteBytes + bytes);
    }

    /// <summary>记录固定类别的关键存储锁等待。</summary>
    internal void RecordLockWait(bool tableManager, double elapsedMs)
    {
        if (tableManager)
            _tableLockWaitMs += elapsedMs;
        else
            _kvLockWaitMs += elapsedMs;
    }

    /// <summary>记录一次 WAL fsync。</summary>
    internal void RecordWalFsync(double elapsedMs)
    {
        _walFsyncCount++;
        _walFsyncMs += elapsedMs;
    }

    /// <summary>冻结执行结果；重复调用返回同一快照。</summary>
    internal SqlExecutionMetricsSnapshot Complete()
    {
        if (_completed is not null)
            return _completed;

        long allocatedBytes = Environment.CurrentManagedThreadId == _originThreadId
            ? Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - _allocatedBytesStart)
            : -1;
        _completed = new SqlExecutionMetricsSnapshot(
            _accessPath,
            _indexName,
            _fallbackReason,
            _candidateRows,
            _examinedRows,
            _logicalReads,
            _physicalReads,
            _physicalReadBytes,
            _physicalWrites,
            _physicalWriteBytes,
            _tableLockWaitMs,
            _kvLockWaitMs,
            _walFsyncMs,
            _walFsyncCount,
            Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds,
            allocatedBytes,
            Math.Max(0, GC.CollectionCount(0) - _gen0Start),
            Math.Max(0, GC.CollectionCount(1) - _gen1Start),
            Math.Max(0, GC.CollectionCount(2) - _gen2Start));
        return _completed;
    }

    private static string Merge(string? current, string next)
        => current is null || string.Equals(current, next, StringComparison.Ordinal)
            ? next
            : "mixed";
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
    double ExecutionElapsedMs,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

/// <summary>把存储与执行器中的低开销计数转发到当前 SQL 根作用域。</summary>
internal static class SqlExecutionTelemetry
{
    internal static bool IsEnabled => Current is not null;

    private static SqlExecutionMetrics? Current => RoutineExecutionContext.Current?.Options.Metrics;

    internal static void RecordAccessPath(string accessPath, string? indexName = null, string? fallbackReason = null)
        => Current?.RecordAccessPath(accessPath, indexName, fallbackReason);

    internal static void RecordCandidateRows(long count) => Current?.RecordCandidateRows(count);

    internal static void RecordExaminedRows(long count) => Current?.RecordExaminedRows(count);

    internal static void RecordPhysicalRead(long bytes) => Current?.RecordPhysicalRead(bytes);

    internal static void RecordPhysicalWrite(long bytes) => Current?.RecordPhysicalWrite(bytes);

    internal static void RecordLockWait(bool tableManager, double elapsedMs)
        => Current?.RecordLockWait(tableManager, elapsedMs);

    internal static void RecordWalFsync(double elapsedMs) => Current?.RecordWalFsync(elapsedMs);
}
