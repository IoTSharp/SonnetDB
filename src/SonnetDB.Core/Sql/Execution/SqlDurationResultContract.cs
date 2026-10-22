using SonnetDB.Engine;
using SonnetDB.Routines;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>在旧传输合同下拒绝不支持的原生时间类型，防止持久写入后才发现结果不可表达。</summary>
internal static class SqlDurationResultContract
{
    internal static bool IsLegacy => !AllowsDuration || !AllowsDateTimeOffset || !AllowsDateTime2;
    private static bool AllowsDuration => RoutineExecutionContext.Current?.Options.AllowDurationResults != false;
    private static bool AllowsDateTimeOffset => RoutineExecutionContext.Current?.Options.AllowDateTimeOffsetResults != false;
    private static bool AllowsDateTime2 => RoutineExecutionContext.Current?.Options.AllowDateTime2Results != false;
    private static bool UnsupportedType(TableColumnType? type)
        => type == TableColumnType.Duration && !AllowsDuration
            || type == TableColumnType.DateTimeOffset && !AllowsDateTimeOffset
            || type == TableColumnType.DateTime2 && !AllowsDateTime2;

    internal static void ValidateBeforeSequence(Tsdb database, SelectStatement statement)
    {
        if (!IsLegacy || !SqlSequenceRuntime.Contains(statement))
            return;
        ValidateSelectDeclaration(database, statement);
    }

    internal static void ValidateProcedure(Tsdb database, ProcedureDefinition definition)
    {
        var definitions = new Dictionary<string, ProcedureDefinition>(StringComparer.Ordinal);
        var pending = new Stack<ProcedureDefinition>();
        pending.Push(definition);
        while (pending.TryPop(out var current))
        {
            if (!definitions.TryAdd(current.Name, current))
                continue;
            if (definitions.Count > 1024)
                throw Unsupported();
            foreach (string dependency in current.ProcedureDependencies)
                pending.Push(database.Routines.TryGetProcedure(dependency) ?? throw Unsupported());
        }
        // 序列是事务外持久写；必须在整个调用树的第一条语句之前检查所有输出。
        // DEFAULT 可能间接调用 nextval，因此同样纳入预检。
        bool usesSequence = definitions.Values.Any(procedure => procedure.Statements.Any(statement =>
            SqlSequenceRuntime.Contains(statement) || statement is InsertStatement insert
            && database.Tables.Catalog.Resolve(insert.Measurement, insert.MeasurementIsQuoted) is { } schema
            && schema.Columns.Any(column => column.DefaultExpressionSql is { } sql
                && SqlSequenceRuntime.Contains(SqlParser.ParsePredicate(sql)))));
        if (!usesSequence)
            return;
        foreach (var procedure in definitions.Values)
        {
            var parameters = new SqlParameters();
            foreach (var parameter in procedure.Parameters)
                parameters.AddNamed(parameter.Name, parameter.DataType switch
                {
                    SqlProcedureParameterType.Int64 => (object)0L,
                    SqlProcedureParameterType.Float64 => 0d,
                    SqlProcedureParameterType.Boolean => false,
                    SqlProcedureParameterType.String => string.Empty,
                    _ => throw Unsupported(),
                });
            foreach (var statement in procedure.Statements)
            {
                SqlStatement bound = SqlParameterBinder.Bind(statement, parameters);
                SqlDateTimeOffsetContract.Validate(database, SqlNameBinder.Bind(database, bound));
                if (!IsLegacy)
                    continue;
                if (bound is SelectStatement select)
                    ValidateSelectDeclaration(database, select);
                else if (bound is InsertStatement insert)
                    ValidateReturningDeclaration(database, insert.Measurement, insert.MeasurementIsQuoted, insert.ReturningColumns);
                else if (bound is UpdateStatement update)
                    ValidateReturningDeclaration(database, update.TableName, update.TableNameIsQuoted, update.ReturningColumns);
                else if (bound is DeleteStatement delete)
                    ValidateReturningDeclaration(database, delete.Measurement, delete.MeasurementIsQuoted, delete.ReturningColumns);
            }
        }
    }

    private static void ValidateReturningDeclaration(Tsdb database, string tableName, bool quoted, IReadOnlyList<string> requested)
    {
        if (requested.Count == 0)
            return;
        var schema = database.Tables.Catalog.Resolve(tableName, quoted) ?? throw Unsupported();
        ValidateReturning(requested.Count == 1 && requested[0] == "*" ? schema.Columns
            : requested.Select(name => schema.TryGetColumn(name) ?? throw Unsupported()).ToArray());
    }

    private static void ValidateSelectDeclaration(Tsdb database, SelectStatement statement)
    {
        // 复杂来源或不透明函数无法证明结果类型时，在副作用之前拒绝；不提前执行表达式。
        if (statement.CommonTableExpressions.Count != 0 || statement.JoinClauses.Count != 0
            || statement.FromSubquery is not null || statement.TableValuedFunction is not null
            || statement.GraphTable is not null || statement.SetOperationList.Count != 0)
            throw Unsupported();
        // nextval 自身已把来源限制为单个基表或无 FROM；只使用声明，不求值或消耗序列号。
        IReadOnlyList<TableColumn> columns = string.IsNullOrEmpty(statement.Measurement) ? []
            : database.Tables.Catalog.Resolve(statement.Measurement, statement.MeasurementIsQuoted)?.Columns
                ?? throw Unsupported();
        foreach (SelectItem projection in statement.Projections)
        {
            if (projection.Expression is StarExpression)
            {
                ValidateReturning(columns);
                continue;
            }
            SelectColumnInfo info = RelationalSelectExecutor.InferColumnInfo(projection.Expression, columns);
            if (info.DataType is null || UnsupportedType(info.DataType))
                throw Unsupported();
        }
    }

    internal static void ValidateReturning(IReadOnlyList<TableColumn> columns)
    {
        if (IsLegacy && columns.Any(static column => UnsupportedType(column.DataType)))
            throw Unsupported();
    }

    internal static void Validate(object? result, bool requireKnownTypes = false)
    {
        if (!IsLegacy)
            return;
        SelectExecutionResult? select = result switch
        {
            SelectExecutionResult query => query,
            InsertExecutionResult insert => insert.Returning,
            RowsAffectedExecutionResult affected => affected.Returning,
            DeleteExecutionResult deleted => deleted.Returning,
            _ => null,
        };
        if (select is null)
            return;
        for (int column = 0; column < select.Columns.Count; column++)
        {
            TableColumnType? type = select.ColumnSchema?[column].DataType ?? select.ColumnInfo?[column].DataType;
            if (UnsupportedType(type) || requireKnownTypes && type is null)
                throw Unsupported();
        }
        if (select.Rows.Any(static row => row.Any(static value => value is TimeSpan && !AllowsDuration || value is DateTimeOffset && !AllowsDateTimeOffset)))
            throw Unsupported();
    }

    private static InvalidOperationException Unsupported()
        => new("DURATION 需要 SQL 结果格式 v5；DATETIMEOFFSET 需要 v6；DATETIME2 需要 v7；不能在副作用前确认类型的结果必须升级协议。");
}
