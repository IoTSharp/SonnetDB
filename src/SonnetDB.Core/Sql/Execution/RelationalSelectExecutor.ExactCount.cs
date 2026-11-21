using SonnetDB.Engine;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

internal static partial class RelationalSelectExecutor
{
    private static bool TryExecuteExactCount(Tsdb tsdb, SelectStatement statement,
        SubqueryMemo memo, out SelectExecutionResult result)
    {
        result = null!;
        if (statement.Projections.Count != 1
            || statement.Projections[0].Expression is not FunctionCallExpression
            { IsStar: true, IsDistinct: false, Over: null } function
            || !string.Equals(function.Name, "COUNT", StringComparison.OrdinalIgnoreCase)
            || function.Arguments.Count != 0
            || statement.Distinct || statement.GroupBy.Count != 0 || statement.Having is not null
            || statement.JoinClauses.Count != 0 || statement.FromSubquery is not null
            || statement.UnionStatements.Count != 0 || statement.TableValuedFunction is not null
            || statement.OrderByList.Count != 0 || statement.Pagination is not null
            || statement.ForUpdate || statement.NoWait || statement.SkipLocked
            || statement.GraphTable is not null || statement.CommonTableExpressions.Count != 0
            || statement.IsRecursive || statement.CteOutputColumnNames is not null
            || string.IsNullOrEmpty(statement.Measurement)
            || RecursiveCteScope.Find(statement.Measurement) is not null
            || SonnetDB.Routines.TriggerTransitionTables.FindSchema(statement.Measurement) is not null)
            return false;

        TableSchema? schema = statement.SourceQualifiedName is { } qualified
            ? tsdb.Tables.Catalog.Resolve(qualified.SchemaName, qualified.SchemaIsQuoted, qualified.Name, qualified.NameIsQuoted)
            : tsdb.Tables.Catalog.TryGet(statement.Measurement);
        if (schema is null || SqlTransactionContext.Current is { } transaction
            && transaction.TryGetBufferedMutations(schema, out _))
            return false;

        object?[]? prefix = null;
        if (statement.Where is not null && !TryGetExactCountPrefix(schema, statement, statement.Where, out prefix))
            return false;
        SqlExecutor.ThrowIfCancellationRequested();
        TableStore store = tsdb.Tables.Open(schema.ObjectName);
        if (!store.TryCountCommittedRows(schema, prefix, out long count))
            return false;

        // Reuse the existing aggregate naming/declaration contract, with no input
        // rows to retain. Only the computed exact Int64 result is substituted.
        result = ExecuteAggregateProjection(tsdb, statement,
            new Relation([], [], new RelationalJoinInputEstimate(0, 0)), null, memo)
            with
        { Rows = [new object?[] { count }] };
        return true;
    }

    private static bool TryGetExactCountPrefix(TableSchema schema, SelectStatement statement,
        SqlExpression where, out object?[]? prefix)
    {
        prefix = null;
        if (schema.PrimaryKey.Count == 0) return false;
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var pending = new Stack<SqlExpression>();
        pending.Push(where);
        // An accepted predicate has at most one equality per primary-key column.
        int maximumNodes = checked(schema.PrimaryKey.Count * 2 + 1);
        for (int node = 0; node < maximumNodes && pending.Count != 0; node++)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            SqlExpression expression = pending.Pop();
            if (expression is BinaryExpression { Operator: SqlBinaryOperator.And } conjunction)
            {
                pending.Push(conjunction.Right);
                pending.Push(conjunction.Left);
                continue;
            }
            if (expression is not BinaryExpression { Operator: SqlBinaryOperator.Equal } equality)
                return false;
            IdentifierExpression? identifier = equality.Left as IdentifierExpression ?? equality.Right as IdentifierExpression;
            LiteralExpression? literal = equality.Left as LiteralExpression ?? equality.Right as LiteralExpression;
            if (identifier is null || literal is null) return false;
            if (identifier.Qualifier is not null && !string.Equals(identifier.Qualifier,
                statement.TableAlias ?? statement.Measurement, StringComparison.Ordinal)) return false;
            TableColumn? column = schema.TryGetColumn(identifier.Name);
            if (column is null || !schema.PrimaryKey.Contains(column.Name, StringComparer.Ordinal)) return false;
            object value;
            if (column.DataType == TableColumnType.String && literal.Kind == SqlLiteralKind.String)
                value = literal.StringValue!;
            else if (column.DataType == TableColumnType.Int64 && literal.Kind == SqlLiteralKind.Integer)
                value = literal.IntegerValue;
            else
                return false;
            // Duplicate or contradictory equalities stay on the original path.
            if (!values.TryAdd(column.Name, value)) return false;
        }
        if (pending.Count != 0 || values.Count == 0) return false;
        var ordered = new object?[values.Count];
        for (int index = 0; index < ordered.Length; index++)
            if (!values.TryGetValue(schema.PrimaryKey[index], out ordered[index])) return false;
        prefix = ordered;
        return true;
    }
}
