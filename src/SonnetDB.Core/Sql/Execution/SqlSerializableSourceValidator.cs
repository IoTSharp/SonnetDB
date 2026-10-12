using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Routines;
using SonnetDB.Sql.Ast;
using SonnetDB.Views;

namespace SonnetDB.Sql.Execution;

internal static class SqlSerializableSourceValidator
{
    internal static void Validate(Tsdb tsdb, SqlStatement statement, SqlTransactionContext? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction?.IsolationLevel != SqlTransactionIsolationLevel.Serializable || transaction.IsCompleted)
            return;
        SelectStatement? query = statement switch
        {
            SelectStatement select => select,
            InsertStatement insert => new SelectStatement(
                insert.Rows.SelectMany(static row => row).Select(static value => new SelectItem(value, null)).ToArray(),
                insert.Measurement, null, [], FromSubquery: insert.Query),
            UpdateStatement update => new SelectStatement(
                update.Assignments.Select(static assignment => new SelectItem(assignment.Value, null)).ToArray(),
                update.TableName, update.Where, [], Joins: update.FromClauses),
            DeleteStatement delete => new SelectStatement([], delete.Measurement, delete.Where, []),
            CommitTransactionStatement or RollbackTransactionStatement or CallProcedureStatement => null,
            _ => throw Unsupported("该语句的 catalog 或外部状态"),
        };
        if (query is null)
            return;
        if (statement is InsertStatement insertStatement)
            RequireTable(tsdb, insertStatement.Measurement, true);
        ValidateQueryShape(query, cancellationToken);
        _ = ViewDependencyCollector.Analyze(query, (name, quoted) =>
        {
            RequireTable(tsdb, name, quoted);
            return name;
        });
    }

    private static void RequireTable(Tsdb tsdb, string name, bool quoted)
    {
        if (RecursiveCteScope.Find(name) is not null || TriggerTransitionTables.FindSchema(name) is not null)
            return;
        if (SqlSourceNameResolver.Resolve(tsdb, name, quoted) is not { Kind: SqlSourceKind.Table })
            throw Unsupported(name);
    }

    private static NotSupportedException Unsupported(string source)
        => new($"Serializable 当前仅保护关系基表；不支持数据源 '{source}'（包括逻辑/物化视图、其它模型与外部数据）。");

    private static void ValidateQueryShape(SelectStatement query, CancellationToken cancellationToken)
    {
        var pending = new Stack<(object Node, int Depth)>();
        pending.Push((query, 0));
        long started = Stopwatch.GetTimestamp();
        for (int visited = 0; pending.Count != 0 && visited < 16_384; visited++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started).TotalSeconds >= 3)
                throw new NotSupportedException("Serializable 数据源校验超出三秒预算。");
            var (node, depth) = pending.Pop();
            if (depth > 64)
                throw new NotSupportedException("Serializable 数据源嵌套超过 64 层。");
            void Add(object? value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pending.Count >= 16_384 || Stopwatch.GetElapsedTime(started).TotalSeconds >= 3)
                    throw new NotSupportedException("Serializable 数据源校验超出节点或时间预算。");
                if (value is not null) pending.Push((value, depth + 1));
            }
            switch (node)
            {
                case SelectStatement select:
                    if (select.Measurement == "__json_file__" || select.GraphTable is not null)
                        throw Unsupported(select.Measurement);
                    if (select.TableValuedFunction is { Name: var functionName }
                        && !string.Equals(functionName, "json_each", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(functionName, "json_table", StringComparison.OrdinalIgnoreCase))
                        throw Unsupported(functionName);
                    Add(select.FromSubquery);
                    Add(select.Where);
                    Add(select.Having);
                    Add(select.TableValuedFunction);
                    foreach (var item in select.Projections) Add(item.Expression);
                    foreach (var expression in select.GroupBy) Add(expression);
                    foreach (var order in select.OrderByList) Add(order.Expression);
                    foreach (var cte in select.CommonTableExpressions) Add(cte.Query);
                    foreach (var operation in select.SetOperationList) Add(operation.Query);
                    foreach (var join in select.JoinClauses) { Add(join.Subquery); Add(join.On); }
                    if (select.Pagination is { } page) { Add(page.OffsetExpression); Add(page.FetchExpression); }
                    break;
                case BinaryExpression binary: Add(binary.Left); Add(binary.Right); break;
                case UnaryExpression unary: Add(unary.Operand); break;
                case CastExpression cast: Add(cast.Operand); break;
                case IsNullExpression isNull: Add(isNull.Operand); break;
                case InExpression @in:
                    Add(@in.Value); Add(@in.Subquery);
                    foreach (var value in @in.Values) Add(value);
                    break;
                case SubqueryExpression subquery: Add(subquery.Select); break;
                case ExistsExpression exists: Add(exists.Select); break;
                case NamedArgumentExpression named: Add(named.Value); break;
                case FunctionCallExpression function:
                    foreach (var argument in function.Arguments) Add(argument);
                    if (function.Over is { } over)
                    {
                        foreach (var partition in over.PartitionBy) Add(partition);
                        foreach (var order in over.OrderBy) Add(order.Expression);
                    }
                    break;
                case CaseExpression @case:
                    foreach (var when in @case.WhenClauses) { Add(when.Condition); Add(when.Result); }
                    Add(@case.Else);
                    break;
            }
        }
        if (pending.Count != 0)
            throw new NotSupportedException("Serializable 数据源校验超过 16384 个节点。");
    }
}
