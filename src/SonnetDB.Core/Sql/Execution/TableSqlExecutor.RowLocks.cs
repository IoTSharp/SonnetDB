using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Routines;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

internal static partial class TableSqlExecutor
{
    internal static SelectExecutionResult ExecuteLockingSelect(Tsdb tsdb, SelectStatement statement)
    {
        var transaction = SqlTransactionContext.Current
            ?? throw new InvalidOperationException("FOR UPDATE 必须在显式事务中执行。");
        transaction.ThrowIfCompleted();
        var schema = tsdb.Tables.Catalog.TryGet(statement.Measurement);
        if (schema is null || statement.Distinct || statement.IsRecursive
            || SqlSequenceRuntime.Contains(statement)
            || statement.CommonTableExpressions.Count != 0 || statement.SetOperationList.Count != 0
            || statement.TableValuedFunction is not null || statement.GraphTable is not null
            || RelationalSelectExecutor.NeedsRelationalPath(statement)
            || statement.Where is not null && ContainsUserScalarFunction(tsdb.Functions, statement.Where)
            || statement.Projections.Any(item => ContainsUserScalarFunction(tsdb.Functions, item.Expression))
            || statement.OrderByList.Any(item => ContainsUserScalarFunction(tsdb.Functions, item.Expression)))
            throw new SqlParseException("锁定读仅支持单个关系基表的非聚合查询；不支持子查询、集合、窗口或用户函数。",
                0, SqlErrorCodes.LockingReadUnsupported, "select_locking_read", "使用单个关系基表查询锁定目标行。");
        ValidateTableAliasReferences(statement);
        var store = tsdb.Tables.Open(schema.Name);
        var owner = transaction.GetRowLockOwner(tsdb.Tables.RowLocks);
        var options = RoutineExecutionContext.Current?.Options ?? SqlExecutionOptions.Default;
        if (!options.CanWrite)
            throw new SqlRowLockException("forbidden", "锁定读需要数据库写权限。");
        long started = Stopwatch.GetTimestamp();
        TimeSpan timeout = TimeSpan.FromMilliseconds(options.TransactionCommitTimeoutMilliseconds);
        while (true)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            LockingSelection selected;
            long sequence;
            lock (store.SynchronizationRoot)
            {
                if (!ReferenceEquals(schema, store.Schema)
                    || !ReferenceEquals(schema, tsdb.Tables.Catalog.TryGet(schema.Name)))
                    throw new InvalidOperationException("锁定读期间关系表定义发生变化，请重试事务。");
                sequence = store.RowLockSequence;
                selected = SelectRowsForLock(store, schema, statement);
            }
            tsdb.Tables.LockCandidatesSelectedTestHook?.Invoke();
            TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                throw new SqlRowLockException(SqlRowLockException.WaitTimeout, "锁定读取复验超过执行预算。");

            if (statement.SkipLocked)
            {
                var acquired = new List<(TableRowLockManager.Lease Lease, int RowIndex)>(selected.Identities.Length);
                var identities = selected.Identities;
                int visibleRows = selected.Result.Rows.Count;
                try
                {
                    for (int i = 0; i < identities.Length; i++)
                    {
                        SqlExecutor.ThrowIfCancellationRequested();
                        TimeSpan rowRemaining = timeout - Stopwatch.GetElapsedTime(started);
                        if (rowRemaining <= TimeSpan.Zero)
                            throw new SqlRowLockException(SqlRowLockException.WaitTimeout, "锁定读取复验超过执行预算。");
                        var resource = TableRowLockManager.Resource.Row(store.RowLockIdentity, identities[i]);
                        try
                        {
                            var rowLease = tsdb.Tables.RowLocks.Acquire(owner, [resource], noWait: true,
                                rowRemaining, options.CancellationToken);
                            acquired.Add((rowLease, i));
                        }
                        catch (SqlRowLockException error) when (error.Code == SqlRowLockException.NotAvailable)
                        {
                            // SKIP LOCKED 只跳过竞争行；其它锁错误仍须向调用方报告。
                        }
                    }

                    lock (store.SynchronizationRoot)
                    {
                        if (!ReferenceEquals(schema, store.Schema)
                            || !ReferenceEquals(schema, tsdb.Tables.Catalog.TryGet(schema.Name)))
                            throw new InvalidOperationException("锁定读期间关系表定义发生变化，请重试事务。");
                        if (sequence != store.RowLockSequence) continue;
                        SqlExecutor.ThrowIfCancellationRequested();
                        foreach (var item in acquired)
                            item.Lease.Retain();
                        var rows = acquired
                            .Where(item => item.RowIndex < visibleRows)
                            .Select(item => selected.Result.Rows[item.RowIndex])
                            .ToArray();
                        return selected.Result with { Rows = rows };
                    }
                }
                finally
                {
                    foreach (var item in acquired)
                        item.Lease.Dispose();
                }
            }

            var resources = selected.Identities.Select(key =>
                TableRowLockManager.Resource.Row(store.RowLockIdentity, key)).Distinct().ToArray();
            using var lease = tsdb.Tables.RowLocks.Acquire(owner, resources, statement.NoWait,
                remaining, options.CancellationToken);
            lock (store.SynchronizationRoot)
            {
                // 全表序列同时覆盖候选行 ABA、谓词、排序/LIMIT 候选和身份迁移。
                // 短锁内复验后即成为本次读取的线性化点；之后仅被选中行保持排他锁。
                if (!ReferenceEquals(schema, store.Schema)
                    || !ReferenceEquals(schema, tsdb.Tables.Catalog.TryGet(schema.Name)))
                    throw new InvalidOperationException("锁定读期间关系表定义发生变化，请重试事务。");
                if (sequence != store.RowLockSequence) continue;
                SqlExecutor.ThrowIfCancellationRequested();
                lease.Retain();
                return selected.Result;
            }
        }
    }

    private static LockingSelection SelectRowsForLock(TableStore store, TableSchema schema, SelectStatement statement)
    {
        var projections = BuildProjections(statement.Projections, schema);
        var hidden = ResolveHiddenOrderColumns(projections, statement.OrderByList, schema);
        if (statement.Pagination is { Fetch: 0 })
            return new(new SelectExecutionResult(projections.Select(projection => projection.ColumnName).ToArray(), [])
            {
                ColumnInfo = projections.Select(projection => ResolveProjectionColumnInfo(projection, schema)).ToArray(),
            }, []);
        var columnNames = projections.Select(projection => projection.ColumnName)
            .Concat(hidden.Select(column => column.Name)).Append("__sndb_lock_row_ordinal").ToArray();
        List<TableRow> matched = [];
        List<IReadOnlyList<object?>> output = [];
        using var reservation = SqlQueryResources.Current?.CreateReservation();
        long bytes = 0;
        foreach (var row in EnumerateSelectCandidateRows(store, schema, statement.Where))
        {
            SqlExecutor.ThrowIfCancellationRequested();
            if (!EvaluateWhere(statement.Where, schema, row.Values)) continue;
            SqlRowRetentionBudget.RetainForExecution(row.Values);
            var values = new object?[columnNames.Length];
            for (int i = 0; i < projections.Length; i++) values[i] = EvaluateProjection(projections[i], schema, row.Values);
            for (int i = 0; i < hidden.Length; i++) values[projections.Length + i] = row.Values[hidden[i].Ordinal];
            values[^1] = (long)matched.Count;
            long retained = SqlSpillRowCodec.EstimateRowBytes(row.Values)
                + SqlSpillRowCodec.EstimateRowBytes(values) + row.PrimaryKey.Length * 5L + 256;
            if (matched.Count >= 100_000 || retained > 64 * 1024 * 1024 - bytes
                || reservation is not null && !reservation.TryReserve(retained))
                throw new SqlRowLockException(SqlRowLockException.LimitExceeded, "锁定读取候选超过行数或内存预算，请缩小查询范围。");
            bytes += retained;
            matched.Add(row);
            output.Add(values);
        }
        var selected = statement.OrderByList.Count == 0
            ? ApplyPagination(columnNames, output, statement.Pagination)
            : ApplyOrderByAndPagination(columnNames, output, statement.OrderByList, statement.Pagination);
        var identities = selected.Rows.Select(row => matched[checked((int)(long)row[^1]!)].PrimaryKey.ToArray()).ToList();
        if (SqlTransactionContext.Current is { } transaction
            && transaction.TryGetBufferedMutations(schema, out var mutations))
        {
            var visibleKeys = identities.Select(Convert.ToHexString).ToHashSet(StringComparer.Ordinal);
            foreach (var mutation in mutations)
                if (mutation.NewValues is not null
                    && visibleKeys.Contains(Convert.ToHexString(TableRowIdentity.CurrentKey(schema, mutation))))
                    identities.Add(TableRowIdentity.TargetKey(schema, mutation));
        }
        var result = new SelectExecutionResult(projections.Select(projection => projection.ColumnName).ToArray(),
            selected.Rows.Select(row => (IReadOnlyList<object?>)row.Take(projections.Length).ToArray()).ToArray())
        {
            ColumnInfo = projections.Select(projection => ResolveProjectionColumnInfo(projection, schema)).ToArray(),
        };
        return new(result, identities.ToArray());
    }

    private sealed record LockingSelection(SelectExecutionResult Result, byte[][] Identities);
}
