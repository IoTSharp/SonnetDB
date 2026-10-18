using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Query.Functions;
using SonnetDB.Routines;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>独立序列的有副作用标量求值与语句边界。</summary>
internal static class SqlSequenceRuntime
{
    private static readonly AsyncLocal<Execution?> Current = new();

    internal static IDisposable Enter(Tsdb tsdb, SqlExecutionOptions options)
    {
        var previous = Current.Value;
        Current.Value = new Execution(tsdb, options.CanWrite && (previous?.CanWrite ?? true), options.CancellationToken);
        return new Scope(previous);
    }

    internal static object Next(IReadOnlyList<object?> arguments)
    {
        if (RoutineExecutionContext.Current?.TriggerAncestry.Length > 0)
            throw Invalid("触发器首版不支持 nextval，包括通过 DEFAULT 引用序列默认值。");
        var execution = Current.Value ?? throw Invalid("nextval 只能在数据库 SQL 执行作用域内调用。");
        if (!execution.CanWrite)
            throw new SequenceException("forbidden", "nextval 是事务外持久写操作，需要数据库写权限。");
        if (arguments.Count != 1 || arguments[0] is not string reference)
            throw Invalid("nextval 需要一个序列名称字符串参数。");
        SqlExecutor.ThrowIfCancellationRequested();
        return execution.Database.Sequences.Next(reference, execution.CancellationToken);
    }

    internal static bool IsNext(string name) => name.Equals("nextval", StringComparison.OrdinalIgnoreCase);

    internal static bool Contains(SqlExpression? expression) => expression switch
    {
        FunctionCallExpression function => IsNext(function.Name) || function.Arguments.Any(Contains)
            || function.Over is { } over && (over.PartitionBy.Any(Contains) || over.OrderBy.Any(item => Contains(item.Expression))),
        BinaryExpression binary => Contains(binary.Left) || Contains(binary.Right),
        UnaryExpression unary => Contains(unary.Operand),
        CastExpression cast => Contains(cast.Operand),
        IsNullExpression isNull => Contains(isNull.Operand),
        InExpression value => Contains(value.Value) || value.Values.Any(Contains) || value.Subquery is { } query && Contains(query),
        CaseExpression conditional => conditional.WhenClauses.Any(clause => Contains(clause.Condition) || Contains(clause.Result)) || Contains(conditional.Else),
        NamedArgumentExpression named => Contains(named.Value),
        SubqueryExpression subquery => Contains(subquery.Select),
        ExistsExpression exists => Contains(exists.Select),
        _ => false,
    };

    internal static bool Contains(SqlStatement statement) => statement switch
    {
        SelectStatement select => select.Projections.Any(item => Contains(item.Expression)) || Contains(select.Where)
            || select.GroupBy.Any(Contains) || Contains(select.Having) || select.OrderByList.Any(item => Contains(item.Expression))
            || Contains(select.TableValuedFunction) || select.FromSubquery is { } source && Contains(source)
            || select.CommonTableExpressions.Any(cte => Contains(cte.Query)) || select.SetOperationList.Any(operation => Contains(operation.Query))
            || select.JoinClauses.Any(Contains)
            || select.GraphTable is { } graph && (Contains(graph.Predicate) || graph.Columns.Any(column => Contains(column.Expression)))
            || select.Pagination is { } page && (Contains(page.OffsetExpression) || Contains(page.FetchExpression)),
        InsertStatement insert => insert.Rows.Any(row => row.Any(Contains)) || insert.Query is { } query && Contains(query)
            || insert.OnConflict is { } conflict && (conflict.UpdateAssignments.Any(assignment => Contains(assignment.Value)) || Contains(conflict.UpdateWhere)),
        UpdateStatement update => update.Assignments.Any(assignment => Contains(assignment.Value)) || Contains(update.Where)
            || update.FromClauses.Any(Contains),
        DeleteStatement delete => Contains(delete.Where) || Contains(delete.RowLimit),
        ExplainStatement explain => Contains(explain.Statement),
        CreateViewStatement view => Contains(view.Query),
        CreateMaterializedViewStatement view => Contains(view.Query),
        CreateTableStatement table => table.Columns.Any(column => Contains(column.DefaultExpression))
            || table.CheckConstraintClauses.Any(check => Contains(check.Expression)),
        AlterTableAddColumnStatement column => Contains(column.DefaultExpression),
        AlterTableAlterColumnStatement column => Contains(column.DefaultExpression),
        AlterTableAddCheckConstraintStatement check => Contains(check.Expression),
        CreateProcedureStatement procedure => procedure.Body.Any(Contains),
        CallProcedureStatement call => call.Arguments.Any(Contains),
        CreateTriggerStatement trigger => Contains(trigger.When) || trigger.Body.Any(Contains),
        SetTriggerNewStatement assignment => Contains(assignment.Value),
        SetProcedureParameterStatement assignment => Contains(assignment.Value),
        ReturnProcedureStatement exit => Contains(exit.Value),
        CreateTableIndexStatement index => Contains(index.DocumentOptions?.PartialFilter),
        CreateDocumentIndexStatement index => Contains(index.PartialFilter),
        InsertGraphStatement insert => insert.Rows.Any(row => row.Any(Contains)),
        UpdateGraphStatement update => update.Assignments.Any(assignment => Contains(assignment.Value)) || Contains(update.Where),
        DeleteGraphStatement delete => Contains(delete.Where),
        WriteModbusStatement write => Contains(write.Value) || Contains(write.ConfirmationToken),
        CreateSequenceStatement or DropSequenceStatement => true,
        _ => false,
    };

    private static bool Contains(JoinClause join) => Contains(join.On) || join.Subquery is { } query && Contains(query)
        || join.ValuesRows is { } rows && rows.Any(row => row.Any(Contains));

    internal static void Validate(Tsdb tsdb, SqlStatement statement, SqlExecutionOptions options)
    {
        if (!options.CanWrite && (statement is CreateSequenceStatement or DropSequenceStatement || Contains(statement)))
            throw new SequenceException("forbidden", "独立序列操作需要数据库写权限。");
        switch (statement)
        {
            case SelectStatement select:
                ValidateSelect(tsdb, select);
                break;
            case InsertStatement insert:
                if (insert.Query is { } query) ValidateSelect(tsdb, query);
                var schema = tsdb.Tables.Catalog.Resolve(insert.Measurement, insert.MeasurementIsQuoted);
                bool usesSequence = Contains(insert) || schema is not null && UsesSequenceDefault(insert, schema);
                if (usesSequence && RoutineExecutionContext.Current?.TriggerAncestry.Length > 0)
                    throw Invalid("触发器首版不支持 nextval，包括通过 DEFAULT 引用序列默认值。");
                if (usesSequence && (schema is null || insert.OnConflict is not null))
                    throw Invalid("nextval 插入首版仅支持无 ON CONFLICT 的关系表 INSERT。");
                foreach (var expression in insert.Rows.SelectMany(static row => row))
                    if (Contains(expression)) ValidateProjection(expression);
                break;
            case UpdateStatement update when Contains(update)
                || tsdb.Tables.Catalog.Resolve(update.TableName, update.TableNameIsQuoted) is { } table
                    && UsesSequenceDefault(update.Assignments, table):
                throw Invalid("UPDATE 首版不支持 nextval，包括通过 DEFAULT 引用序列默认值。");
            case CreateViewStatement view when Contains(view.Query):
            case CreateMaterializedViewStatement materialized when Contains(materialized.Query):
                throw Invalid("持久化视图定义不支持有副作用的 nextval。");
            case CreateTableStatement table when table.CheckConstraintClauses.Any(check => Contains(check.Expression)):
            case AlterTableAddCheckConstraintStatement check when Contains(check.Expression):
                throw Invalid("CHECK 约束不支持有副作用的 nextval。");
            case DeleteStatement or InsertGraphStatement or UpdateGraphStatement or DeleteGraphStatement
                or CallProcedureStatement or CreateTriggerStatement or SetTriggerNewStatement
                or SetProcedureParameterStatement or ReturnProcedureStatement
                or CreateTableIndexStatement or CreateDocumentIndexStatement or WriteModbusStatement when Contains(statement):
                throw Invalid("此语句位置尚不支持有副作用的 nextval；请使用受支持的 SELECT、INSERT 或关系表 DEFAULT。");
            case CreateProcedureStatement procedure:
                foreach (var body in procedure.Body) Validate(tsdb, body, options);
                break;
        }
    }

    private static bool UsesSequenceDefault(InsertStatement statement, TableSchema schema)
    {
        foreach (var column in schema.Columns)
        {
            if (!IsSequenceDefault(column)) continue;
            int ordinal = -1;
            for (int i = 0; i < statement.Columns.Count; i++)
                if (string.Equals(statement.Columns[i], column.Name, i < statement.ColumnIsQuoted.Count && statement.ColumnIsQuoted[i]
                    ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) { ordinal = i; break; }
            if (ordinal < 0 || statement.Rows.Any(row => ordinal < row.Count && row[ordinal] is DefaultValueExpression)) return true;
        }
        return statement.OnConflict is { } conflict && UsesSequenceDefault(conflict.UpdateAssignments, schema);
    }

    private static bool UsesSequenceDefault(IReadOnlyList<UpdateAssignment> assignments, TableSchema schema)
        => assignments.Any(assignment => assignment.Value is DefaultValueExpression
            && schema.Columns.Any(column => string.Equals(column.Name, assignment.ColumnName,
                assignment.ColumnNameIsQuoted ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) && IsSequenceDefault(column)));

    private static bool IsSequenceDefault(TableColumn column)
        => column.DefaultExpressionSql is { } sql && Contains(SqlParser.ParsePredicate(sql));

    private static void ValidateSelect(Tsdb tsdb, SelectStatement select)
    {
        if (!Contains(select)) return;
        if (Contains(select.Where) || select.GroupBy.Any(Contains) || Contains(select.Having)
            || select.OrderByList.Any(item => Contains(item.Expression)) || Contains(select.TableValuedFunction)
            || select.Pagination is { } page && (Contains(page.OffsetExpression) || Contains(page.FetchExpression))
            || select.Distinct || select.GroupBy.Count != 0 || select.Having is not null
            || select.JoinClauses.Count != 0 || select.FromSubquery is not null || select.CommonTableExpressions.Count != 0
            || select.SetOperationList.Count != 0 || select.GraphTable is not null || select.IsRecursive || select.TableValuedFunction is not null
            || !string.IsNullOrEmpty(select.Measurement) && tsdb.Tables.Catalog.Resolve(select.Measurement, select.MeasurementIsQuoted) is null)
            throw Invalid("nextval 查询首版仅支持无 FROM 或单个关系基表的标量投影；不支持谓词、排序表达式、聚合、DISTINCT、JOIN、CTE 或子查询中的取号。");
        foreach (var item in select.Projections)
            ValidateProjection(item.Expression);
    }

    private static void ValidateProjection(SqlExpression expression)
    {
        if (expression is FunctionCallExpression function)
        {
            if (function.Over is not null || FunctionRegistry.TryGetAggregate(function.Name, out _))
                throw Invalid("nextval 查询不支持聚合或窗口投影。");
            if (IsNext(function.Name) && (function.IsStar || function.IsDistinct || function.Arguments.Count != 1))
                throw Invalid("nextval 需要一个序列名称字符串参数。");
            foreach (var argument in function.Arguments) ValidateProjection(argument);
        }
        else if (expression is BinaryExpression binary) { ValidateProjection(binary.Left); ValidateProjection(binary.Right); }
        else if (expression is UnaryExpression unary) ValidateProjection(unary.Operand);
        else if (expression is CastExpression cast) ValidateProjection(cast.Operand);
        else if (expression is IsNullExpression isNull) ValidateProjection(isNull.Operand);
        else if (expression is NamedArgumentExpression named) ValidateProjection(named.Value);
        else if (expression is InExpression value)
        {
            if (value.Subquery is not null) throw Invalid("nextval 查询不支持子查询。");
            ValidateProjection(value.Value);
            foreach (var item in value.Values) ValidateProjection(item);
        }
        else if (expression is CaseExpression conditional)
        {
            foreach (var clause in conditional.WhenClauses) { ValidateProjection(clause.Condition); ValidateProjection(clause.Result); }
            if (conditional.Else is { } otherwise) ValidateProjection(otherwise);
        }
        else if (expression is SubqueryExpression or ExistsExpression)
            throw Invalid("nextval 查询不支持子查询。");
    }

    internal static bool ValidateDefault(SqlExpression expression, TableColumn column)
    {
        if (!Contains(expression)) return false;
        if (expression is not FunctionCallExpression { Arguments: [LiteralExpression { Kind: SqlLiteralKind.String, StringValue: { } reference }] } function
            || !IsNext(function.Name) || function.IsStar || function.IsDistinct || function.Over is not null
            || column.DataType != TableColumnType.Int64)
            throw Invalid("序列 DEFAULT 仅支持 INT 列的 nextval('sequence') 直接调用。");
        var execution = Current.Value ?? throw Invalid("序列 DEFAULT 校验需要数据库执行作用域。");
        execution.Database.Sequences.ValidateReference(reference);
        return true;
    }

    internal static SequenceException Invalid(string message) => new(SequenceException.InvalidDefinition, message);

    private sealed record Execution(Tsdb Database, bool CanWrite, CancellationToken CancellationToken);
    private sealed class Scope(Execution? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
