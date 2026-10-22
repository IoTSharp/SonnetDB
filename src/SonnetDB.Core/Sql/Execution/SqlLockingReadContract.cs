using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>保证未保留行身份的嵌套路径不会静默丢弃锁修饰词。</summary>
internal static class SqlLockingReadContract
{
    internal static void Validate(SqlStatement statement)
    {
        if (statement is SelectStatement { NoWait: true, ForUpdate: false })
            throw new SqlParseException("NOWAIT 必须跟随 FOR UPDATE。", 0,
                SqlErrorCodes.LockingReadUnsupported, "select_locking_read", "使用 FOR UPDATE NOWAIT。");
        var nested = statement is SelectStatement select
            ? select with { ForUpdate = false, NoWait = false } : statement;
        if (Contains(nested))
            throw new SqlParseException("锁定读只支持独立的关系基表 SELECT，不能嵌入其它语句或子查询。",
                0, SqlErrorCodes.LockingReadUnsupported, "select_locking_read", "在显式事务中单独执行 FOR UPDATE [NOWAIT]。");
    }
    internal static bool Contains(SqlExpression? expression) => expression switch
    {
        FunctionCallExpression function => function.Arguments.Any(Contains)
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
        SelectStatement select => select.ForUpdate || select.NoWait || select.Projections.Any(item => Contains(item.Expression)) || Contains(select.Where)
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
        CreateTableAsStatement table => Contains(table.Query),
        CreateTableStatement table => table.Columns.Any(column => Contains(column.DefaultExpression))
            || table.CheckConstraintClauses.Any(check => Contains(check.Expression)),
        AlterTableAddColumnStatement column => Contains(column.DefaultExpression),
        AlterTableAlterColumnStatement column => Contains(column.DefaultExpression),
        AlterTableAddCheckConstraintStatement check => Contains(check.Expression),
        CreateProcedureStatement procedure => procedure.Body.Any(Contains),
        CallProcedureStatement call => call.Arguments.Any(Contains),
        CreateTriggerStatement trigger => Contains(trigger.When) || trigger.Body.Any(Contains),
        SetTriggerNewStatement assignment => Contains(assignment.Value),
        CreateTableIndexStatement index => Contains(index.DocumentOptions?.PartialFilter),
        CreateDocumentIndexStatement index => Contains(index.PartialFilter),
        InsertGraphStatement insert => insert.Rows.Any(row => row.Any(Contains)),
        UpdateGraphStatement update => update.Assignments.Any(assignment => Contains(assignment.Value)) || Contains(update.Where),
        DeleteGraphStatement delete => Contains(delete.Where),
        WriteModbusStatement write => Contains(write.Value) || Contains(write.ConfirmationToken),
        _ => false,
    };

    private static bool Contains(JoinClause join) => Contains(join.On) || join.Subquery is { } query && Contains(query)
        || join.ValuesRows is { } rows && rows.Any(row => row.Any(Contains));

}
