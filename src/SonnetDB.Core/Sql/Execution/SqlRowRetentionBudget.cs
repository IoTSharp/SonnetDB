using SonnetDB.Exceptions;
using SonnetDB.Routines;

namespace SonnetDB.Sql.Execution;

/// <summary>限制查询分支在内存中保留的行；嵌套分支同时计入外层预算。</summary>
internal sealed class SqlRowRetentionBudget : IDisposable
{
    private static readonly AsyncLocal<SqlRowRetentionBudget?> CurrentSlot = new();
    private readonly SqlRowRetentionBudget? _previous;
    private readonly SqlQueryResources.SqlOperatorMemoryReservation? _reservation;
    private readonly Lock _sync = new();
    private readonly long _limitRows;
    private readonly long _limitBytes;
    private readonly bool _insertSource;
    private readonly bool _execution;
    private long _rows;
    private long _bytes;

    private SqlRowRetentionBudget(long limitRows, long limitBytes, bool insertSource, bool execution = false)
    {
        _previous = CurrentSlot.Value;
        _reservation = SqlQueryResources.Current?.CreateReservation();
        _limitRows = limitRows;
        _limitBytes = limitBytes;
        _insertSource = insertSource;
        _execution = execution;
        CurrentSlot.Value = this;
    }

    internal static SqlRowRetentionBudget? Current => CurrentSlot.Value;

    internal bool IsInsertSource => _insertSource;

    internal static bool HasExecutionBudget => Current?.ExecutionBudget is not null;

    private SqlRowRetentionBudget? ExecutionBudget => _execution ? this : _previous?.ExecutionBudget;

    internal long OperatorLimitBytes => Math.Min(_limitBytes, _previous?.OperatorLimitBytes ?? long.MaxValue);

    internal static SqlRowRetentionBudget? EnterExecution(SqlExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.HasMaterializationLimits || HasExecutionBudget)
            return null;
        return new(options.MaxMaterializedRows ?? long.MaxValue,
            options.MaxMaterializedBytes ?? long.MaxValue, insertSource: false, execution: true);
    }

    internal static void RetainForExecution(IReadOnlyList<object?> row)
        => Current?.ExecutionBudget?.Retain(row);

    internal static IEnumerable<T> RetainExecutionRows<T>(IEnumerable<T> rows)
        where T : IReadOnlyList<object?>
        => Current?.ExecutionBudget is { } budget ? RetainRows(rows, budget) : rows;

    private static IEnumerable<T> RetainRows<T>(IEnumerable<T> rows, SqlRowRetentionBudget budget)
        where T : IReadOnlyList<object?>
    {
        foreach (T row in rows)
        {
            budget.Retain(row);
            yield return row;
        }
    }

    internal static void RetainValueForExecution(object? value)
    {
        if (Current?.ExecutionBudget is not { } budget)
            return;
        SqlExecutor.ThrowIfCancellationRequested();
        long bytes = EstimateValueBytes(value) - 1;
        if (bytes > 0)
            budget.RetainCore(rows: 0, bytes);
    }

    internal static SqlRowRetentionBudget EnterRecursive()
        => new(long.MaxValue, RecursiveCteExecutor.MaxBytes, insertSource: false);

    internal static SqlRowRetentionBudget EnterInsertSource(SqlExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(options.MaxTriggerTransitionRows, options.MaxTriggerTransitionBytes, insertSource: true);
    }

    internal void Retain(IReadOnlyList<object?> row)
    {
        SqlExecutor.ThrowIfCancellationRequested();
        long bytes = _execution ? EstimateExecutionBytes(row) : SqlSpillRowCodec.EstimateRowBytes(row);
        if (_insertSource)
            bytes = Math.Max(bytes, checked(TriggerTransitionBudget.Estimate(row) + 64));
        RetainCore(rows: 1, bytes);
        _previous?.Retain(row);
    }

    private void RetainCore(long rows, long bytes)
    {
        lock (_sync)
        {
            if (rows > _limitRows - _rows || bytes > _limitBytes - _bytes)
                throw LimitExceeded(memory: false);
            if (_reservation is not null && !_reservation.TryReserve(bytes))
                throw LimitExceeded(memory: true);
            _rows += rows;
            _bytes += bytes;
        }
    }

    private static long EstimateExecutionBytes(IReadOnlyList<object?> row)
    {
        // 额外覆盖结果列表倍增时的引用槽位和每行的小型辅助对象。
        long bytes = 64L + (row.Count * 8L);
        foreach (object? value in row)
            bytes = checked(bytes + EstimateValueBytes(value));
        return bytes;
    }

    private static long EstimateValueBytes(object? value) => value switch
    {
        null => 1,
        string text => 24L + (text.Length * 2L),
        byte[] data => 24L + data.Length,
        bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double
            or decimal or char or DateTime or DateTimeOffset or TimeOnly or Guid => 24,
        _ => throw new NotSupportedException("SQL 物化预算只支持标准 SQL 标量、字符串和二进制值。"),
    };

    private Exception LimitExceeded(bool memory)
        => _execution
            ? new InvalidOperationException(memory
                ? "SQL 执行器物化保留超出当前查询或数据库的内存预算。"
                : "SQL 执行器累计物化行数或估算字节数超过调用方预算。")
            : _insertSource
            ? new RoutineExecutionException(RoutineErrorCodes.TransitionLimit,
                "INSERT SELECT 源查询保留行超出行数或字节预算，语句已拒绝。")
            : new InvalidOperationException(memory
                ? "递归 CTE 单轮阻塞算子超过当前查询或数据库的内存预算。"
                : "递归 CTE 单轮阻塞算子保留字节数超过上限。");

    public void Dispose()
    {
        CurrentSlot.Value = _previous;
        _reservation?.Dispose();
    }
}
