using System.Diagnostics;
using SonnetDB.Exceptions;
using SonnetDB.Routines;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Tables;

/// <summary>在既有存储临界区内只尝试取锁，竞争时展开调用栈后等待并重新准备整个原子操作。</summary>
internal sealed class TableLockOperation : IDisposable
{
    private static readonly AsyncLocal<TableLockOperation?> CurrentScope = new();
    private readonly TableLockOperation? _previous;
    private readonly TableRowLockManager _manager;
    private readonly TableRowLockManager.Owner _owner;
    private readonly SqlTransactionContext? _transaction;
    private readonly List<(TableRowLockManager.Lease Lease, bool Retain)> _leases = [];
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly TimeSpan _timeout;
    private readonly CancellationToken _cancellation;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private bool _succeeded;
    private bool _disposed;

    private TableLockOperation(TableRowLockManager manager)
    {
        _previous = CurrentScope.Value;
        _manager = manager;
        _transaction = SqlTransactionContext.Current;
        _owner = _transaction?.GetRowLockOwner(manager) ?? manager.CreateOwner();
        var options = RoutineExecutionContext.Current?.Options ?? SqlExecutionOptions.Default;
        _timeout = TimeSpan.FromMilliseconds(options.TransactionCommitTimeoutMilliseconds);
        _cancellation = options.CancellationToken;
        CurrentScope.Value = this;
    }

    internal static T Run<T>(TableRowLockManager manager, Func<T> action)
    {
        if (CurrentScope.Value is { } active && !active._disposed
            && active._threadId == Environment.CurrentManagedThreadId && ReferenceEquals(active._manager, manager))
            return action();
        using var operation = new TableLockOperation(manager);
        while (true)
        {
            try
            {
                T result = action();
                operation._succeeded = true;
                return result;
            }
            catch (RetryRequired retry)
            {
                operation.ReleaseAttempt();
                var lease = manager.Acquire(operation._owner, retry.Resources, false,
                    operation.Remaining(), operation._cancellation);
                operation._leases.Add((lease, retry.Retain));
            }
        }
    }

    internal static void Run(TableRowLockManager manager, Action action)
        => Run(manager, () => { action(); return true; });

    internal static void Require(TableRowLockManager manager, IReadOnlyList<TableRowLockManager.Resource> resources,
        bool retain = true)
    {
        var operation = CurrentScope.Value;
        if (operation is null || operation._disposed || operation._threadId != Environment.CurrentManagedThreadId
            || !ReferenceEquals(operation._manager, manager))
            throw new InvalidOperationException("关系写入缺少外层行锁操作作用域。");
        SqlExecutor.ThrowIfCancellationRequested();
        try
        {
            operation._leases.Add((manager.Acquire(operation._owner, resources, true,
                operation.Remaining(), operation._cancellation), retain));
        }
        catch (SqlRowLockException error) when (error.Code == SqlRowLockException.NotAvailable)
        {
            throw new RetryRequired(resources.ToArray(), retain);
        }
    }

    internal static bool IsRetry(Exception exception) => exception is RetryRequired;

    private TimeSpan Remaining()
    {
        TimeSpan remaining = _timeout - Stopwatch.GetElapsedTime(_started);
        if (remaining <= TimeSpan.Zero)
            throw new SqlRowLockException(SqlRowLockException.WaitTimeout, "关系锁等待超过执行预算。");
        return remaining;
    }

    private void ReleaseAttempt()
    {
        foreach (var (lease, _) in _leases) lease.Dispose();
        _leases.Clear();
    }

    public void Dispose()
    {
        _disposed = true;
        CurrentScope.Value = _previous;
        if (_succeeded && _transaction is { IsCompleted: false })
            foreach (var (lease, retain) in _leases)
                if (retain) lease.Retain();
        ReleaseAttempt();
        if (_transaction is null) _owner.Dispose();
    }

    private sealed class RetryRequired(TableRowLockManager.Resource[] resources, bool retain) : Exception
    {
        internal TableRowLockManager.Resource[] Resources { get; } = resources;
        internal bool Retain { get; } = retain;
    }
}
