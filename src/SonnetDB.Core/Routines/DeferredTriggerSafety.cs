using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Routines;

/// <summary>校验提交临界区中的关系触发器，防止跨模型访问或任意应用回调。</summary>
internal static class DeferredTriggerSafety
{
    // 明确列出没有外部回调的受支持函数；新增函数必须先审核其执行边界。
    private static readonly HashSet<string> PureFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "abs", "round", "sqrt", "log", "coalesce", "concat", "lower", "upper", "regexp_like",
        "json_value", "count", "sum", "min", "max", "avg", "first", "last",
        "stddev", "variance", "spread", "mode", "median", "percentile", "p50", "p90", "p95", "p99",
        "date_only", "date_part", "date_add", "date_add_datetime", "date_add_datetime_offset",
        "to_unix_milliseconds", "to_unix_seconds", "to_datetime", "to_utc_datetime", "to_local_datetime",
    };

    internal static void Validate(Tsdb database, TriggerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(definition);
        var validator = new Validator(database, definition);
        validator.Validate();
    }

    private sealed class Validator(Tsdb database, TriggerDefinition definition)
    {
        private const int MaxNodes = 16_384;
        private const int MaxDepth = 64;
        private readonly long _started = Stopwatch.GetTimestamp();
        private int _nodes;

        internal void Validate()
        {
            ValidateSource(definition.TableName, writable: true);
            VisitExpression(definition.When, 0);
            foreach (var statement in definition.Statements)
                VisitStatement(statement, 0);
        }

        private void CheckBudget(int depth)
        {
            RoutineExecutionContext.Current?.CheckCancellation();
            if (++_nodes > MaxNodes || depth > MaxDepth
                || Stopwatch.GetElapsedTime(_started) > TimeSpan.FromSeconds(5))
                throw Error("提交阶段触发器定义超过校验节点、嵌套深度或时间上限。");
        }

        private void ValidateSource(string name, bool writable)
        {
            CheckBudget(0);
            bool alias = string.Equals(name, definition.OldTableName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, definition.NewTableName, StringComparison.OrdinalIgnoreCase);
            if (alias)
            {
                if (writable)
                    throw Error("提交阶段不能写入 transition table。");
                return;
            }
            if (database.Tables.Catalog.TryGet(name) is null)
                throw Error($"提交阶段只允许基础关系表和当前 transition table，数据源 '{name}' 不在此范围。");
        }

        private void VisitStatement(SqlStatement statement, int depth)
        {
            CheckBudget(depth);
            switch (statement)
            {
                case InsertStatement insert:
                    ValidateSource(insert.Measurement, writable: true);
                    if (insert.Query is not null)
                        VisitSelect(insert.Query, depth + 1);
                    foreach (var row in insert.Rows)
                    {
                        CheckBudget(depth + 1);
                        foreach (var expression in row)
                            VisitExpression(expression, depth + 1);
                    }
                    break;
                case UpdateStatement update:
                    ValidateSource(update.TableName, writable: true);
                    foreach (var assignment in update.Assignments)
                        VisitExpression(assignment.Value, depth + 1);
                    VisitExpression(update.Where, depth + 1);
                    break;
                case DeleteStatement delete:
                    ValidateSource(delete.Measurement, writable: true);
                    VisitExpression(delete.Where, depth + 1);
                    break;
                case SetTriggerNewStatement assignment when definition.Timing == SqlTriggerTiming.Before:
                    VisitExpression(assignment.Value, depth + 1);
                    break;
                default:
                    throw Error($"提交阶段不支持语句 '{statement.GetType().Name}'。");
            }
        }

        private void VisitSelect(SelectStatement select, int depth)
        {
            CheckBudget(depth);
            if (select.TableValuedFunction is not null || select.GraphTable is not null)
                throw Error("提交阶段禁止表值函数、JSON_FILE 和 GRAPH_TABLE 数据源。");
            if (select.FromSubquery is not null)
                VisitSelect(select.FromSubquery, depth + 1);
            else
                ValidateSource(select.Measurement, writable: false);
            foreach (var projection in select.Projections)
                VisitExpression(projection.Expression, depth + 1);
            VisitExpression(select.Where, depth + 1);
            foreach (var expression in select.GroupBy)
                VisitExpression(expression, depth + 1);
            VisitExpression(select.Having, depth + 1);
            foreach (var order in select.OrderByList)
                VisitExpression(order.Expression, depth + 1);
            if (select.Pagination is { } pagination)
            {
                VisitExpression(pagination.OffsetExpression, depth + 1);
                VisitExpression(pagination.FetchExpression, depth + 1);
            }
            foreach (var join in select.JoinClauses)
            {
                CheckBudget(depth + 1);
                if (join.Subquery is not null)
                    VisitSelect(join.Subquery, depth + 1);
                else
                    ValidateSource(join.TableName, writable: false);
                VisitExpression(join.On, depth + 1);
            }
            foreach (var union in select.UnionStatements)
                VisitSelect(union, depth + 1);
        }

        private void VisitExpression(SqlExpression? expression, int depth)
        {
            CheckBudget(depth);
            switch (expression)
            {
                case null:
                case LiteralExpression:
                case DurationLiteralExpression:
                case IdentifierExpression:
                case StarExpression:
                case DefaultValueExpression:
                    break;
                case BinaryExpression binary:
                    VisitExpression(binary.Left, depth + 1);
                    VisitExpression(binary.Right, depth + 1);
                    break;
                case UnaryExpression unary:
                    VisitExpression(unary.Operand, depth + 1);
                    break;
                case IsNullExpression isNull:
                    VisitExpression(isNull.Operand, depth + 1);
                    break;
                case InExpression membership:
                    VisitExpression(membership.Value, depth + 1);
                    foreach (var value in membership.Values)
                        VisitExpression(value, depth + 1);
                    if (membership.Subquery is not null)
                        VisitSelect(membership.Subquery, depth + 1);
                    break;
                case CaseExpression conditional:
                    foreach (var branch in conditional.WhenClauses)
                    {
                        VisitExpression(branch.Condition, depth + 1);
                        VisitExpression(branch.Result, depth + 1);
                    }
                    VisitExpression(conditional.Else, depth + 1);
                    break;
                case NamedArgumentExpression named:
                    VisitExpression(named.Value, depth + 1);
                    break;
                case FunctionCallExpression function:
                    if (database.Functions.TryGetScalar(function.Name, out _)
                        || database.Functions.TryGetAggregate(function.Name, out _)
                        || database.Functions.TryGetWindow(function.Name, out _)
                        || database.Functions.TryGetTableValuedFunction(function.Name, out _)
                        || !PureFunctions.Contains(function.Name))
                        throw Error($"提交阶段不允许用户函数或未经准入的函数 '{function.Name}'。");
                    foreach (var argument in function.Arguments)
                        VisitExpression(argument, depth + 1);
                    break;
                case SubqueryExpression subquery:
                    VisitSelect(subquery.Select, depth + 1);
                    break;
                case ExistsExpression exists:
                    VisitSelect(exists.Select, depth + 1);
                    break;
                default:
                    throw Error($"提交阶段不支持表达式 '{expression.GetType().Name}'。");
            }
        }

        private static RoutineExecutionException Error(string message)
            => new(RoutineErrorCodes.Dependency, message);
    }
}
