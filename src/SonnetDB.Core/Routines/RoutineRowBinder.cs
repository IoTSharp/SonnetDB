using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Routines;

internal sealed record RoutineRowContext(
    TableSchema Schema,
    IReadOnlyList<object?>? OldValues,
    IReadOnlyList<object?>? NewValues);

internal static class RoutineRowBinder
{
    public static SqlStatement Bind(SqlStatement statement, RoutineRowContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(context);
        return statement switch
        {
            InsertStatement insert => insert with
            {
                Rows = insert.Rows.Select(row => row
                    .Select(expression => BindInsertValue(expression, context))
                    .ToArray())
                    .ToArray(),
            },
            UpdateStatement update => update with
            {
                Assignments = update.Assignments
                    .Select(assignment => assignment with { Value = BindExpression(assignment.Value, context) })
                    .ToArray(),
                Where = BindExpression(update.Where, context),
            },
            DeleteStatement delete => delete with { Where = BindExpression(delete.Where, context) },
            _ => throw new InvalidOperationException(
                $"触发器 body 不支持语句 '{statement.GetType().Name}'。"),
        };
    }

    public static SqlExpression BindExpression(SqlExpression expression, RoutineRowContext context)
    {
        switch (expression)
        {
            case IdentifierExpression { Qualifier: not null } identifier
                when string.Equals(identifier.Qualifier, "OLD", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(identifier.Qualifier, "NEW", StringComparison.OrdinalIgnoreCase):
                var values = string.Equals(identifier.Qualifier, "OLD", StringComparison.OrdinalIgnoreCase)
                    ? context.OldValues
                    : context.NewValues;
                if (values is null)
                {
                    throw new RoutineExecutionException(
                        RoutineErrorCodes.TriggerContext,
                        $"当前触发事件没有 {identifier.Qualifier} 行上下文。");
                }
                var column = context.Schema.TryGetColumn(identifier.Name)
                    ?? throw new RoutineExecutionException(
                        RoutineErrorCodes.TriggerContext,
                        $"触发器引用了未知列 '{identifier.Name}'。");
                return SqlParameterBinder.ToLiteral(values[column.Ordinal]);
            case BinaryExpression binary:
                return binary with
                {
                    Left = BindExpression(binary.Left, context),
                    Right = BindExpression(binary.Right, context),
                };
            case UnaryExpression unary:
                return unary with { Operand = BindExpression(unary.Operand, context) };
            case IsNullExpression isNull:
                return isNull with { Operand = BindExpression(isNull.Operand, context) };
            case InExpression @in:
                return @in with
                {
                    Value = BindExpression(@in.Value, context),
                    Values = BindExpressions(@in.Values, context),
                    Subquery = @in.Subquery is null ? null : BindSelect(@in.Subquery, context),
                };
            case FunctionCallExpression function:
                return function with { Arguments = BindExpressions(function.Arguments, context) };
            case NamedArgumentExpression named:
                return named with { Value = BindExpression(named.Value, context) };
            case CaseExpression @case:
                return @case with
                {
                    WhenClauses = @case.WhenClauses.Select(clause => clause with
                    {
                        Condition = BindExpression(clause.Condition, context),
                        Result = BindExpression(clause.Result, context),
                    }).ToArray(),
                    Else = @case.Else is null ? null : BindExpression(@case.Else, context),
                };
            case SubqueryExpression subquery:
                return subquery with { Select = BindSelect(subquery.Select, context) };
            case ExistsExpression exists:
                return exists with { Select = BindSelect(exists.Select, context) };
            default:
                return expression;
        }
    }

    private static IReadOnlyList<SqlExpression> BindExpressions(
        IReadOnlyList<SqlExpression> expressions,
        RoutineRowContext context)
        => expressions.Select(expression => BindExpression(expression, context)).ToArray();

    private static SqlExpression BindInsertValue(SqlExpression expression, RoutineRowContext context)
    {
        SqlExpression bound = BindExpression(expression, context);
        try
        {
            return SqlParameterBinder.ToLiteral(RoutineExpressionEvaluator.Evaluate(bound));
        }
        catch (InvalidOperationException)
        {
            return bound;
        }
    }

    private static SelectStatement BindSelect(SelectStatement select, RoutineRowContext context)
        => select with
        {
            Projections = select.Projections.Select(item => item with
            {
                Expression = BindExpression(item.Expression, context),
            }).ToArray(),
            Where = select.Where is null ? null : BindExpression(select.Where, context),
            GroupBy = BindExpressions(select.GroupBy, context),
            Having = select.Having is null ? null : BindExpression(select.Having, context),
            TableValuedFunction = select.TableValuedFunction is null
                ? null
                : (FunctionCallExpression)BindExpression(select.TableValuedFunction, context),
            GraphTable = select.GraphTable is null
                ? null
                : select.GraphTable with
                {
                    Predicate = select.GraphTable.Predicate is null
                        ? null
                        : BindExpression(select.GraphTable.Predicate, context),
                    Columns = select.GraphTable.Columns.Select(item => item with
                    {
                        Expression = BindExpression(item.Expression, context),
                    }).ToArray(),
                },
            Pagination = select.Pagination is null ? null : new PaginationSpec(
                BindExpression(select.Pagination.OffsetExpression, context),
                select.Pagination.FetchExpression is null
                    ? null
                    : BindExpression(select.Pagination.FetchExpression, context)),
            OrderBy = null,
            OrderByItems = select.OrderByList.Select(item => item with
            {
                Expression = BindExpression(item.Expression, context),
            }).ToArray(),
            Join = null,
            Joins = select.JoinClauses.Select(join => join with
            {
                On = BindExpression(join.On, context),
                Subquery = join.Subquery is null ? null : BindSelect(join.Subquery, context),
            }).ToArray(),
            FromSubquery = select.FromSubquery is null ? null : BindSelect(select.FromSubquery, context),
            Unions = select.UnionStatements.Select(union => BindSelect(union, context)).ToArray(),
        };
}
