using SonnetDB.Engine;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

internal static partial class RelationalSelectExecutor
{
    private static bool TryExecutePrefixGroupCount(Tsdb tsdb, SelectStatement statement,
        SubqueryMemo memo, out SelectExecutionResult result)
    {
        result = null!;
        if (statement.Projections.Count != 2 || statement.GroupBy.Count != 1
            || statement.Projections[0].Expression is not IdentifierExpression selected
            || statement.GroupBy[0] is not IdentifierExpression grouped
            || !string.Equals(selected.Name, grouped.Name, StringComparison.Ordinal)
            || !IsPrefixGroupIdentifier(statement, selected) || !IsPrefixGroupIdentifier(statement, grouped)
            || statement.Projections[1].Expression is not FunctionCallExpression
            { IsStar: true, IsDistinct: false, Over: null } function
            || !string.Equals(function.Name, "COUNT", StringComparison.OrdinalIgnoreCase)
            || function.Arguments.Count != 0 || statement.Where is null
            || statement.Distinct || statement.Having is not null
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
            && transaction.TryGetBufferedMutations(schema, out _)
            || !TryGetExactCountPrefix(schema, statement, statement.Where, out object?[]? prefix)
            || prefix is null || prefix.Length >= schema.PrimaryKey.Count
            || !string.Equals(grouped.Name, schema.PrimaryKey[prefix.Length], StringComparison.Ordinal))
            return false;
        TableColumn groupColumn = schema.TryGetColumn(grouped.Name)!;
        if (groupColumn.DataType is not TableColumnType.String and not TableColumnType.Int64)
            return false;

        TableStore store = tsdb.Tables.Open(schema.ObjectName);
        if (!store.TryCountCommittedPrefixGroups(schema, prefix, out var rows))
            return false;

        // Bind output names/types through the original aggregate declaration path.
        // The snapshot visitor retained only the final two-column group outputs.
        string alias = statement.TableAlias ?? statement.Measurement;
        RelColumn[] columns = schema.Columns.Select(column =>
            new RelColumn(alias, column.Name, column.Name, column.DataType)
            { Info = SelectColumnInfo.FromColumn(column) }).ToArray();
        result = ExecuteAggregateProjection(tsdb, statement,
            new Relation(columns, [], new RelationalJoinInputEstimate(0, 0)), null, memo)
            with
        { Rows = rows };
        return true;
    }

    private static bool IsPrefixGroupIdentifier(SelectStatement statement, IdentifierExpression identifier)
        => identifier.Qualifier is null || string.Equals(identifier.Qualifier,
            statement.TableAlias ?? statement.Measurement, StringComparison.Ordinal);
}
