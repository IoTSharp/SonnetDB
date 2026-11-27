using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Query;
using SonnetDB.Sql.Ast;
using SonnetDB.Storage.Format;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

internal static partial class SelectExecutor
{
    private const int MaxOrderedPointCursors = 32;

    internal static OrderedSelectStream? TryExecuteOrderedMeasurementStream(
        Tsdb tsdb, SelectStatement statement, SqlQueryResources resources)
    {
        if (!HasOrderedMeasurementStreamShape(statement) || SqlRowRetentionBudget.HasExecutionBudget)
            return null;

        ValidateTableAliasReferences(statement);
        if (tsdb.Measurements.TryGet(statement.Measurement) is not { } schema)
            return null;
        var projections = ClassifyProjections(statement.Projections, schema);
        var where = WhereClauseDecomposer.Decompose(statement.Where, schema);
        var result = TryCreateOrderedMeasurementStream(tsdb, schema, statement, projections, where, resources);
        if (result is not null)
        {
            try
            {
                RecordEstimatedRows(tsdb, statement);
            }
            catch
            {
                result.ReadView?.Dispose();
                throw;
            }
        }
        return result;
    }

    private static bool HasOrderedMeasurementStreamShape(SelectStatement statement)
        => statement.OrderByList.Count == 1
            && statement.OrderByList[0].Expression is IdentifierExpression identifier
            && IsTimePseudoColumn(identifier)
            && !statement.Distinct
            && statement.GroupBy.Count == 0
            && statement.Having is null
            && statement.JoinClauses.Count == 0
            && statement.FromSubquery is null
            && statement.CommonTableExpressions.Count == 0
            && statement.SetOperationList.Count == 0
            && statement.TableValuedFunction is null
            && statement.GraphTable is null;

    private static OrderedSelectStream? TryCreateOrderedMeasurementStream(
        Tsdb tsdb,
        MeasurementSchema schema,
        SelectStatement statement,
        IReadOnlyList<Projection> projections,
        WhereClause where,
        SqlQueryResources resources)
    {
        if (!HasOrderedMeasurementStreamShape(statement)
            || where.Residual is not null
            || where.GeoFilters.Count != 0
            || projections.Any(static p => p.Kind is not (
                ProjectionKind.Time or ProjectionKind.Tag or ProjectionKind.Field or ProjectionKind.Constant)))
        {
            return null;
        }

        // The materialized sorter binds the first output named time. An alias can shadow that name.
        var columns = projections.Select(static p => p.ColumnName).ToArray();
        int timeColumn = Array.IndexOf(columns, "time");
        if (timeColumn < 0 || projections[timeColumn].Kind != ProjectionKind.Time)
            return null;

        var metadata = projections.Select(DescribeRawProjection).ToArray();
        if (statement.Pagination?.Fetch == 0)
            return new OrderedSelectStream(columns, metadata, []);

        var fields = GetRawFields(schema, projections);
        var fieldSet = fields.ToHashSet(StringComparer.Ordinal);
        var (series, readView) = tsdb.CaptureMeasurementReadView(() =>
        {
            // Classification and physical capture must refer to the same schema generation.
            if (!ReferenceEquals(tsdb.Measurements.TryGet(statement.Measurement), schema))
                throw new InvalidOperationException("Measurement schema changed while opening the query.");

            var matched = tsdb.Catalog.Find(statement.Measurement, where.TagFilter);
            var seriesIds = new HashSet<ulong>();
            foreach (var entry in matched)
            {
                resources.ThrowIfCancellationRequested();
                seriesIds.Add(entry.Id);
            }

            var view = tsdb.Query.CaptureMeasurementReadView(
                seriesIds, fieldSet, where.TimeRange, resources.CancellationToken,
                resources.ThrowIfCancellationRequested);
            return (matched, view);
        }, resources.CancellationToken);

        bool descending = statement.OrderByList[0].Direction == SortDirection.Descending;
        return new OrderedSelectStream(columns, metadata, ReadRows(), readView);

        IEnumerable<IReadOnlyList<object?>> ReadRows()
        {
            try
            {
                foreach (var row in ReadOrderedRows())
                    yield return row;
            }
            finally
            {
                SqlExecutionTelemetry.RecordSortReaderPeak(readView.PeakPointReaders);
            }
        }

        IEnumerable<IReadOnlyList<object?>> ReadOrderedRows()
        {
            long offset = statement.Pagination?.Offset ?? 0;
            long? remaining = statement.Pagination?.Fetch;
            bool needsSorter = (long)series.Count * fields.Length > MaxOrderedPointCursors
                || (remaining is null && resources.Options.BlockingOperatorMemoryLimitBytes is not null);
            SqlExecutionTelemetry.RecordAccessPath(needsSorter ? "measurement_time_sort" : "measurement_time_merge");
            IEnumerable<IReadOnlyList<object?>> rows;
            if (needsSorter)
            {
                var candidates = EnumerateNaturalRawRows(tsdb, schema, projections, series, where.TimeRange, readView, fields)
                    .Select(static row => row.Values);
                var comparer = new TimeColumnComparer(timeColumn, descending);
                if (remaining is long fetch && SqlPagination.TryGetInt32Prefix(offset, fetch, out _))
                {
                    foreach (var row in TopN.OrderByThenPaginate(
                        candidates, comparer, offset, fetch, SqlSpillCodecs.ReadOnlyRows))
                    {
                        resources.ThrowIfCancellationRequested();
                        yield return row;
                    }
                    yield break;
                }
                rows = SqlSpillSorter.Order(candidates, comparer, SqlSpillCodecs.ReadOnlyRows);
            }
            else
            {
                rows = EnumerateOrderedRawRows(
                    tsdb, schema, projections, series, where.TimeRange,
                    descending ? QueryDirection.Descending : QueryDirection.Ascending, readView, fields)
                    .Select(static row => row.Values);
            }

            foreach (var row in rows)
            {
                resources.ThrowIfCancellationRequested();
                if (offset > 0)
                {
                    offset--;
                    continue;
                }
                yield return row;
                if (remaining is not null && --remaining == 0)
                    yield break;
            }
        }
    }

    private static string[] GetRawFields(MeasurementSchema schema, IReadOnlyList<Projection> projections)
    {
        var fields = projections
            .Where(static projection => projection.Kind == ProjectionKind.Field)
            .Select(static projection => projection.Column!.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return fields.Length > 0 ? fields : [schema.FieldColumns.First().Name];
    }

    private static SelectColumnInfo DescribeRawProjection(Projection projection)
        => projection.Kind switch
        {
            ProjectionKind.Time => new SelectColumnInfo(TableColumnType.Int64, false),
            ProjectionKind.Tag => new SelectColumnInfo(TableColumnType.String, true),
            ProjectionKind.Field => new SelectColumnInfo(projection.Column!.DataType switch
            {
                FieldType.Int64 => TableColumnType.Int64,
                FieldType.Float64 => TableColumnType.Float64,
                FieldType.Boolean => TableColumnType.Boolean,
                FieldType.String => TableColumnType.String,
                _ => null,
            }, true),
            _ => new SelectColumnInfo(projection.ConstantValue switch
            {
                long => TableColumnType.Int64,
                double => TableColumnType.Float64,
                bool => TableColumnType.Boolean,
                string => TableColumnType.String,
                _ => null,
            }, projection.ConstantValue is null),
        };
}
