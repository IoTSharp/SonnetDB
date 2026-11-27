using SonnetDB.Engine;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

public static partial class SqlExecutor
{
    private static SqlStreamingSelectResult? TryExecuteOrderedSelectStream(
        Tsdb database,
        SelectStatement statement,
        SqlExecutionOptions options,
        CancellationToken callerCancellation)
    {
        if (SqlRowRetentionBudget.HasExecutionBudget || options.PreviewMaxRows is not null
            || statement.OrderByList.Count == 0 || statement.ForUpdate || statement.Distinct
            || statement.IsRecursive || statement.CommonTableExpressions.Count != 0
            || statement.SetOperationList.Count != 0 || statement.FromSubquery is not null
            || statement.JoinClauses.Count != 0 || statement.GraphTable is not null
            || statement.TableValuedFunction is not null || statement.GroupBy.Count != 0
            || statement.Having is not null || string.IsNullOrEmpty(statement.Measurement)
            || RelationalSelectExecutor.ContainsSubquery(statement) || SqlSequenceRuntime.Contains(statement))
        {
            return null;
        }

        options = options with
        {
            CancellationToken = callerCancellation,
            SortSpillMaxBytes = options.SortSpillMaxBytes ?? 8L * 1024 * 1024 * 1024,
        };
        CancellationTokenSource? deadline = options.CreateDeadlineSource();
        if (deadline is not null)
            options = options with { CancellationToken = deadline.Token };

        var resources = new SqlQueryResources(database, options);
        OrderedSelectStream? stream = null;
        bool transferred = false;
        try
        {
            using var activation = resources.Activate();
            resources.ThrowIfCancellationRequested();
            if (ResolveBoundTable(database, statement) is { } schema)
            {
                if (!RelationalSelectExecutor.NeedsRelationalPath(statement))
                    stream = TableSqlExecutor.TryExecuteOrderedTableStream(database, statement, schema, resources);
            }
            else if (database.Measurements.TryGet(statement.Measurement) is not null
                && database.Documents.Catalog.TryGet(statement.Measurement) is null
                && database.MaterializedViews.Catalog.TryGet(statement.Measurement) is null)
            {
                stream = SelectExecutor.TryExecuteOrderedMeasurementStream(database, statement, resources);
            }

            if (stream?.ColumnInfo is not { } columnInfo || !CanStreamDeclaredColumns(columnInfo, options))
                return null;

            resources.ThrowIfCancellationRequested();
            var result = new SqlStreamingSelectResult(stream, resources, deadline);
            transferred = true;
            return result;
        }
        finally
        {
            if (!transferred)
            {
                using var ownedDeadline = deadline;
                using var ownedResources = resources;
                stream?.ReadView?.Dispose();
            }
        }
    }

    private static bool CanStreamDeclaredColumns(IReadOnlyList<SelectColumnInfo> columns, SqlExecutionOptions options)
        => columns.All(column => column.DataType switch
        {
            null => false,
            TableColumnType.Duration => options.AllowDurationResults,
            TableColumnType.DateTimeOffset => options.AllowDateTimeOffsetResults,
            TableColumnType.DateTime2 => options.AllowDateTime2Results,
            _ => true,
        });
}
