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
    private long _rows;
    private long _bytes;

    private SqlRowRetentionBudget(long limitRows, long limitBytes, bool insertSource)
    {
        _previous = CurrentSlot.Value;
        _reservation = SqlQueryResources.Current?.CreateReservation();
        _limitRows = limitRows;
        _limitBytes = limitBytes;
        _insertSource = insertSource;
        CurrentSlot.Value = this;
    }

    internal static SqlRowRetentionBudget? Current => CurrentSlot.Value;

    internal bool IsInsertSource => _insertSource;

    internal long OperatorLimitBytes => Math.Min(_limitBytes, _previous?.OperatorLimitBytes ?? long.MaxValue);

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
        long bytes = SqlSpillRowCodec.EstimateRowBytes(row);
        if (_insertSource)
            bytes = Math.Max(bytes, checked(TriggerTransitionBudget.Estimate(row) + 64));
        lock (_sync)
        {
            if (_rows >= _limitRows || bytes > _limitBytes - _bytes)
                throw LimitExceeded(memory: false);
            if (_reservation is not null && !_reservation.TryReserve(bytes))
                throw LimitExceeded(memory: true);
            _rows++;
            _bytes += bytes;
        }
        _previous?.Retain(row);
    }

    private Exception LimitExceeded(bool memory)
        => _insertSource
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
