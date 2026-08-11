using SonnetDB.Sql.Ast;

namespace SonnetDB.Routines;

internal sealed record SqlRoutineAnalysis(
    IReadOnlyList<string> ObjectDependencies,
    IReadOnlyList<string> ProcedureDependencies,
    IReadOnlyList<string> RowColumns,
    bool RequiresWrite);

internal static class SqlRoutineAnalyzer
{
    public const int MaxDefinitionStatements = 64;
    private static readonly IReadOnlySet<string> EmptyNames = new HashSet<string>(StringComparer.Ordinal);

    public static SqlRoutineAnalysis AnalyzeProcedure(
        IReadOnlyList<SqlStatement> statements,
        IReadOnlyList<SqlProcedureParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(parameters);
        ValidateStatementCount(statements);

        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(parameter.Name);
            if (!parameterNames.Add(parameter.Name))
                throw new ArgumentException($"过程参数 '{parameter.Name}' 重复声明。", nameof(parameters));
        }

        return Analyze(statements, parameterNames, triggerEvent: null);
    }

    public static SqlRoutineAnalysis AnalyzeTrigger(
        IReadOnlyList<SqlStatement> statements,
        SqlExpression? when,
        SqlTriggerEvent triggerEvent)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ValidateStatementCount(statements);
        var analysis = Analyze(statements, EmptyNames, triggerEvent);
        if (when is null)
            return analysis;

        var objects = new HashSet<string>(analysis.ObjectDependencies, StringComparer.Ordinal);
        var procedures = new HashSet<string>(analysis.ProcedureDependencies, StringComparer.Ordinal);
        var rowColumns = new HashSet<string>(analysis.RowColumns, StringComparer.Ordinal);
        VisitExpression(when, EmptyNames, triggerEvent, objects, procedures, rowColumns);
        return analysis with
        {
            ObjectDependencies = objects.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            ProcedureDependencies = procedures.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            RowColumns = rowColumns.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
        };
    }

    private static SqlRoutineAnalysis Analyze(
        IReadOnlyList<SqlStatement> statements,
        IReadOnlySet<string> parameterNames,
        SqlTriggerEvent? triggerEvent)
    {
        var objects = new HashSet<string>(StringComparer.Ordinal);
        var procedures = new HashSet<string>(StringComparer.Ordinal);
        var rowColumns = new HashSet<string>(StringComparer.Ordinal);
        bool requiresWrite = false;

        foreach (var statement in statements)
        {
            if (triggerEvent is not null && statement is not InsertStatement and not UpdateStatement and not DeleteStatement)
            {
                throw new ArgumentException(
                    $"触发器 SQL body 只允许 INSERT / UPDATE / DELETE，实际为 {statement.GetType().Name}。",
                    nameof(statements));
            }

            switch (statement)
            {
                case SelectStatement select:
                    VisitSelect(select, parameterNames, triggerEvent, objects, procedures, rowColumns);
                    break;
                case InsertStatement insert:
                    requiresWrite = true;
                    objects.Add(insert.Measurement);
                    foreach (var row in insert.Rows)
                    foreach (var expression in row)
                        VisitExpression(expression, parameterNames, triggerEvent, objects, procedures, rowColumns);
                    break;
                case UpdateStatement update:
                    requiresWrite = true;
                    objects.Add(update.TableName);
                    foreach (var assignment in update.Assignments)
                        VisitExpression(assignment.Value, parameterNames, triggerEvent, objects, procedures, rowColumns);
                    VisitExpression(update.Where, parameterNames, triggerEvent, objects, procedures, rowColumns);
                    break;
                case DeleteStatement delete:
                    requiresWrite = true;
                    objects.Add(delete.Measurement);
                    VisitExpression(delete.Where, parameterNames, triggerEvent, objects, procedures, rowColumns);
                    break;
                case CallProcedureStatement call when triggerEvent is null:
                    procedures.Add(call.Name);
                    foreach (var argument in call.Arguments)
                        VisitExpression(argument, parameterNames, triggerEvent, objects, procedures, rowColumns);
                    break;
                default:
                    throw new ArgumentException(
                        $"SQL 过程 body 只允许 SELECT / INSERT / UPDATE / DELETE / CALL，实际为 {statement.GetType().Name}。",
                        nameof(statements));
            }
        }

        return new SqlRoutineAnalysis(
            objects.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            procedures.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            rowColumns.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            requiresWrite);
    }

    private static void VisitSelect(
        SelectStatement select,
        IReadOnlySet<string> parameterNames,
        SqlTriggerEvent? triggerEvent,
        HashSet<string> objects,
        HashSet<string> procedures,
        HashSet<string> rowColumns)
    {
        if (select.FromSubquery is not null)
        {
            VisitSelect(select.FromSubquery, parameterNames, triggerEvent, objects, procedures, rowColumns);
        }
        else if (select.GraphTable is { } graphTable)
        {
            objects.Add(graphTable.GraphName);
        }
        else if (!string.IsNullOrEmpty(select.Measurement)
                 && !string.Equals(select.Measurement, "__json_file__", StringComparison.Ordinal))
        {
            objects.Add(select.Measurement);
        }

        foreach (var projection in select.Projections)
            VisitExpression(projection.Expression, parameterNames, triggerEvent, objects, procedures, rowColumns);
        if (select.Where is not null)
            VisitExpression(select.Where, parameterNames, triggerEvent, objects, procedures, rowColumns);
        foreach (var expression in select.GroupBy)
            VisitExpression(expression, parameterNames, triggerEvent, objects, procedures, rowColumns);
        if (select.Having is not null)
            VisitExpression(select.Having, parameterNames, triggerEvent, objects, procedures, rowColumns);
        foreach (var orderBy in select.OrderByList)
            VisitExpression(orderBy.Expression, parameterNames, triggerEvent, objects, procedures, rowColumns);
        if (select.Pagination is { } pagination)
        {
            VisitExpression(pagination.OffsetExpression, parameterNames, triggerEvent, objects, procedures, rowColumns);
            if (pagination.FetchExpression is not null)
                VisitExpression(pagination.FetchExpression, parameterNames, triggerEvent, objects, procedures, rowColumns);
        }
        if (select.TableValuedFunction is not null)
            VisitExpression(select.TableValuedFunction, parameterNames, triggerEvent, objects, procedures, rowColumns);
        if (select.GraphTable is { } graphSource)
        {
            if (graphSource.Predicate is not null)
            {
                VisitExpression(
                    graphSource.Predicate,
                    parameterNames,
                    triggerEvent,
                    objects,
                    procedures,
                    rowColumns);
            }
            foreach (var column in graphSource.Columns)
            {
                VisitExpression(
                    column.Expression,
                    parameterNames,
                    triggerEvent,
                    objects,
                    procedures,
                    rowColumns);
            }
        }
        foreach (var join in select.JoinClauses)
        {
            if (join.Subquery is null)
                objects.Add(join.TableName);
            else
                VisitSelect(join.Subquery, parameterNames, triggerEvent, objects, procedures, rowColumns);
            VisitExpression(join.On, parameterNames, triggerEvent, objects, procedures, rowColumns);
        }
        foreach (var union in select.UnionStatements)
            VisitSelect(union, parameterNames, triggerEvent, objects, procedures, rowColumns);
    }

    private static void VisitExpression(
        SqlExpression expression,
        IReadOnlySet<string> parameterNames,
        SqlTriggerEvent? triggerEvent,
        HashSet<string> objects,
        HashSet<string> procedures,
        HashSet<string> rowColumns)
    {
        switch (expression)
        {
            case ParameterExpression parameter:
                if (parameter.Name is null)
                    throw new ArgumentException("持久化 SQL body 不允许位置参数 '?'，请使用已声明的命名 IN 参数。", nameof(expression));
                if (!parameterNames.Contains(parameter.Name))
                    throw new ArgumentException($"SQL body 引用了未声明参数 '@{parameter.Name}'。", nameof(expression));
                break;
            case IdentifierExpression { Qualifier: not null } identifier
                when string.Equals(identifier.Qualifier, "OLD", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(identifier.Qualifier, "NEW", StringComparison.OrdinalIgnoreCase):
                if (triggerEvent is null)
                    throw new ArgumentException("SQL 过程不能引用触发器 OLD/NEW 行上下文。", nameof(expression));
                if (triggerEvent == SqlTriggerEvent.Insert
                    && string.Equals(identifier.Qualifier, "OLD", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("AFTER INSERT 触发器不能引用 OLD。", nameof(expression));
                if (triggerEvent == SqlTriggerEvent.Delete
                    && string.Equals(identifier.Qualifier, "NEW", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("AFTER DELETE 触发器不能引用 NEW。", nameof(expression));
                rowColumns.Add(identifier.Name);
                break;
            case BinaryExpression binary:
                VisitExpression(binary.Left, parameterNames, triggerEvent, objects, procedures, rowColumns);
                VisitExpression(binary.Right, parameterNames, triggerEvent, objects, procedures, rowColumns);
                break;
            case UnaryExpression unary:
                VisitExpression(unary.Operand, parameterNames, triggerEvent, objects, procedures, rowColumns);
                break;
            case IsNullExpression isNull:
                VisitExpression(isNull.Operand, parameterNames, triggerEvent, objects, procedures, rowColumns);
                break;
            case InExpression @in:
                VisitExpression(@in.Value, parameterNames, triggerEvent, objects, procedures, rowColumns);
                foreach (var item in @in.Values)
                    VisitExpression(item, parameterNames, triggerEvent, objects, procedures, rowColumns);
                if (@in.Subquery is not null)
                    VisitSelect(@in.Subquery, parameterNames, triggerEvent, objects, procedures, rowColumns);
                break;
            case FunctionCallExpression function:
                foreach (var argument in function.Arguments)
                    VisitExpression(argument, parameterNames, triggerEvent, objects, procedures, rowColumns);
                break;
            case NamedArgumentExpression named:
                VisitExpression(named.Value, parameterNames, triggerEvent, objects, procedures, rowColumns);
                break;
            case CaseExpression @case:
                foreach (var clause in @case.WhenClauses)
                {
                    VisitExpression(clause.Condition, parameterNames, triggerEvent, objects, procedures, rowColumns);
                    VisitExpression(clause.Result, parameterNames, triggerEvent, objects, procedures, rowColumns);
                }
                if (@case.Else is not null)
                    VisitExpression(@case.Else, parameterNames, triggerEvent, objects, procedures, rowColumns);
                break;
            case SubqueryExpression subquery:
                VisitSelect(subquery.Select, parameterNames, triggerEvent, objects, procedures, rowColumns);
                break;
            case ExistsExpression exists:
                VisitSelect(exists.Select, parameterNames, triggerEvent, objects, procedures, rowColumns);
                break;
        }
    }

    private static void ValidateStatementCount(IReadOnlyList<SqlStatement> statements)
    {
        if (statements.Count is < 1 or > MaxDefinitionStatements)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statements),
                $"SQL body 语句数必须在 1 到 {MaxDefinitionStatements} 之间。");
        }
    }
}
