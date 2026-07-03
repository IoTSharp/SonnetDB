using SonnetDB.Catalog;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Query.Functions;
using SonnetDB.Sql.Ast;
using SonnetDB.Storage.Format;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// <c>EXPLAIN</c> / MCP <c>explain_sql</c> 共用的扫描估算结果。
/// </summary>
public sealed record SqlExplainExecutionResult(
    string? Database,
    string StatementType,
    string? Measurement,
    int MatchedSeriesCount,
    int EstimatedSegmentCount,
    int EstimatedBlockCount,
    long EstimatedScannedRows,
    long EstimatedMemTableRows,
    long EstimatedSegmentRows,
    bool HasTimeFilter,
    int TagFilterCount,
    string? AccessPath = null,
    string? IndexName = null,
    DocumentQueryPlan? DocumentPlan = null);

/// <summary>
/// 为只读 SQL 估算查询将扫描的段数、block 数和行数。
/// </summary>
public static class SqlExplainPlanner
{
    private static readonly IReadOnlyList<string> _keyValueColumns =
        new List<string>(2) { "key", "value" }.AsReadOnly();

    private readonly record struct ExplainWhereClause(
        IReadOnlyDictionary<string, string> TagFilter,
        TimeRange TimeRange);

    /// <summary>
    /// 解释一条只读 SQL AST。
    /// </summary>
    /// <param name="databaseName">当前数据库名；嵌入式场景未知时可为 <c>null</c>。</param>
    /// <param name="tsdb">目标数据库实例。</param>
    /// <param name="statement">被解释的语句；可直接传入 <see cref="ExplainStatement"/>。</param>
    /// <returns>扫描估算摘要。</returns>
    public static SqlExplainExecutionResult Explain(string? databaseName, Tsdb tsdb, SqlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        if (statement is ExplainStatement explain)
            statement = explain.Statement;

        using var _ = UserFunctionRegistry.EnterScope(tsdb.Functions);
        return statement switch
        {
            ShowMeasurementsStatement => ExplainShowMeasurements(databaseName, tsdb),
            ShowTablesStatement => ExplainShowTables(databaseName, tsdb),
            ShowDocumentCollectionsStatement => ExplainShowDocumentCollections(databaseName, tsdb),
            ShowTableIndexesStatement showIndexes => ExplainShowIndexes(databaseName, tsdb, showIndexes.TableName),
            ShowDocumentIndexesStatement showDocumentIndexes => ExplainShowDocumentIndexes(databaseName, tsdb, showDocumentIndexes.CollectionName),
            ShowFullTextIndexesStatement showFullTextIndexes => ExplainShowFullTextIndexes(databaseName, tsdb, showFullTextIndexes.CollectionName),
            DescribeMeasurementStatement describe => ExplainDescribeMeasurement(databaseName, tsdb, describe.Name),
            DescribeTableStatement describeTable => ExplainDescribeTable(databaseName, tsdb, describeTable.Name),
            DescribeDocumentCollectionStatement describeDocumentCollection => ExplainDescribeDocumentCollection(databaseName, tsdb, describeDocumentCollection.Name),
            SelectStatement select => ExplainSelect(databaseName, tsdb, select),
            _ => throw new InvalidOperationException(
                "EXPLAIN 仅支持 SELECT、SHOW MEASUREMENTS / SHOW TABLES / SHOW DOCUMENT COLLECTIONS / SHOW INDEXES / SHOW JSON INDEXES / SHOW FULLTEXT INDEXES 与 DESCRIBE [MEASUREMENT|TABLE|DOCUMENT COLLECTION]。"),
        };
    }

    /// <summary>
    /// 把解释结果格式化为 SQL Console 可直接展示的 <see cref="SelectExecutionResult"/>。
    /// </summary>
    public static SelectExecutionResult ToSelectExecutionResult(SqlExplainExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var rows = new List<IReadOnlyList<object?>>(22)
        {
            new object?[] { "database", result.Database },
            new object?[] { "statement_type", result.StatementType },
            new object?[] { "measurement", result.Measurement },
            new object?[] { "matched_series_count", result.MatchedSeriesCount },
            new object?[] { "estimated_segment_count", result.EstimatedSegmentCount },
            new object?[] { "estimated_block_count", result.EstimatedBlockCount },
            new object?[] { "estimated_scanned_rows", result.EstimatedScannedRows },
            new object?[] { "estimated_memtable_rows", result.EstimatedMemTableRows },
            new object?[] { "estimated_segment_rows", result.EstimatedSegmentRows },
            new object?[] { "has_time_filter", result.HasTimeFilter },
            new object?[] { "tag_filter_count", result.TagFilterCount },
            new object?[] { "access_path", result.AccessPath },
            new object?[] { "index_name", result.IndexName },
        };

        if (result.DocumentPlan is { } documentPlan)
        {
            rows.Add(new object?[] { "estimated_candidate_rows", documentPlan.EstimatedCandidateRows });
            rows.Add(new object?[] { "estimated_output_rows", documentPlan.EstimatedOutputRows });
            rows.Add(new object?[] { "filter_pushdown", documentPlan.FilterPushdown });
            rows.Add(new object?[] { "filter_pushdown_fields", string.Join(",", documentPlan.FilterPushdownFields) });
            rows.Add(new object?[] { "residual_filter_fields", string.Join(",", documentPlan.ResidualFilterFields) });
            rows.Add(new object?[] { "sort_uses_index", documentPlan.SortUsesIndex });
            rows.Add(new object?[] { "projection_covered_by_index", documentPlan.ProjectionCoveredByIndex });
            rows.Add(new object?[] { "candidate_plans", FormatDocumentPlanCandidates(documentPlan.Candidates) });
            rows.Add(new object?[] { "gap_reason", documentPlan.GapReason });
        }

        return new SelectExecutionResult(_keyValueColumns, rows);
    }

    private static string FormatDocumentPlanCandidates(IReadOnlyList<DocumentQueryPlanCandidate> candidates)
        => string.Join(
            ";",
            candidates.Select(static candidate =>
                $"{(candidate.Selected ? "*" : string.Empty)}{candidate.AccessPath}"
                + $"{(candidate.IndexName is null ? string.Empty : ":" + candidate.IndexName)}"
                + $" rows={candidate.EstimatedCandidateRows} cost={candidate.Cost}"
                + $"{(candidate.FilterPushdownFields.Count == 0 ? string.Empty : " pushdown=" + string.Join("|", candidate.FilterPushdownFields))}"
                + $"{(candidate.RejectReason is null ? string.Empty : " reason=" + candidate.RejectReason)}"));

    private static SqlExplainExecutionResult ExplainShowMeasurements(string? databaseName, Tsdb tsdb)
    {
        var measurementCount = tsdb.Measurements.Snapshot().Count;
        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "show_measurements",
            Measurement: null,
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: measurementCount,
            EstimatedMemTableRows: 0,
            EstimatedSegmentRows: 0,
            HasTimeFilter: false,
            TagFilterCount: 0,
            AccessPath: "catalog",
            IndexName: null);
    }

    private static SqlExplainExecutionResult ExplainShowTables(string? databaseName, Tsdb tsdb)
    {
        var tableCount = tsdb.Tables.Catalog.Snapshot().Count;
        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "show_tables",
            Measurement: null,
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: tableCount,
            EstimatedMemTableRows: 0,
            EstimatedSegmentRows: 0,
            HasTimeFilter: false,
            TagFilterCount: 0,
            AccessPath: "catalog",
            IndexName: null);
    }

    private static SqlExplainExecutionResult ExplainShowDocumentCollections(string? databaseName, Tsdb tsdb)
    {
        var collectionCount = tsdb.Documents.Catalog.Snapshot().Count;
        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "show_document_collections",
            Measurement: null,
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: collectionCount,
            EstimatedMemTableRows: 0,
            EstimatedSegmentRows: 0,
            HasTimeFilter: false,
            TagFilterCount: 0,
            AccessPath: "catalog",
            IndexName: null);
    }

    private static SqlExplainExecutionResult ExplainDescribeMeasurement(
        string? databaseName,
        Tsdb tsdb,
        string measurementName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(measurementName);

        var schema = tsdb.Measurements.TryGet(measurementName)
            ?? throw new InvalidOperationException($"measurement '{measurementName}' 不存在。");

        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "describe_measurement",
            Measurement: schema.Name,
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: schema.Columns.Count,
            EstimatedMemTableRows: 0,
            EstimatedSegmentRows: 0,
            HasTimeFilter: false,
            TagFilterCount: 0,
            AccessPath: "catalog",
            IndexName: null);
    }

    private static SqlExplainExecutionResult ExplainShowIndexes(
        string? databaseName,
        Tsdb tsdb,
        string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var schema = tsdb.Tables.Catalog.TryGet(tableName)
            ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");

        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "show_indexes",
            Measurement: schema.Name,
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: schema.Indexes.Count,
            EstimatedMemTableRows: 0,
            EstimatedSegmentRows: 0,
            HasTimeFilter: false,
            TagFilterCount: 0,
            AccessPath: "catalog",
            IndexName: null);
    }

    private static SqlExplainExecutionResult ExplainShowDocumentIndexes(
        string? databaseName,
        Tsdb tsdb,
        string collectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        var schema = tsdb.Documents.Catalog.TryGet(collectionName)
            ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");

        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "show_json_indexes",
            Measurement: schema.Name,
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: schema.Indexes.Count,
            EstimatedMemTableRows: 0,
            EstimatedSegmentRows: 0,
            HasTimeFilter: false,
            TagFilterCount: 0,
            AccessPath: "catalog",
            IndexName: null);
    }

    private static SqlExplainExecutionResult ExplainShowFullTextIndexes(
        string? databaseName,
        Tsdb tsdb,
        string collectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        var schema = tsdb.Documents.Catalog.TryGet(collectionName)
            ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");

        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "show_fulltext_indexes",
            Measurement: schema.Name,
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: schema.FullTextIndexes.Count,
            EstimatedMemTableRows: 0,
            EstimatedSegmentRows: 0,
            HasTimeFilter: false,
            TagFilterCount: 0,
            AccessPath: "catalog",
            IndexName: null);
    }

    private static SqlExplainExecutionResult ExplainDescribeTable(
        string? databaseName,
        Tsdb tsdb,
        string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var schema = tsdb.Tables.Catalog.TryGet(tableName)
            ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");

        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "describe_table",
            Measurement: schema.Name,
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: schema.Columns.Count,
            EstimatedMemTableRows: 0,
            EstimatedSegmentRows: 0,
            HasTimeFilter: false,
            TagFilterCount: 0,
            AccessPath: "catalog",
            IndexName: null);
    }

    private static SqlExplainExecutionResult ExplainDescribeDocumentCollection(
        string? databaseName,
        Tsdb tsdb,
        string collectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        var schema = tsdb.Documents.Catalog.TryGet(collectionName)
            ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");

        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "describe_document_collection",
            Measurement: schema.Name,
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: schema.Indexes.Count + schema.FullTextIndexes.Count + 1,
            EstimatedMemTableRows: 0,
            EstimatedSegmentRows: 0,
            HasTimeFilter: false,
            TagFilterCount: 0,
            AccessPath: "catalog",
            IndexName: null);
    }

    private static SqlExplainExecutionResult ExplainSelect(
        string? databaseName,
        Tsdb tsdb,
        SelectStatement statement)
    {
        if (DocumentVectorSearchExecutor.IsVectorSearch(statement))
        {
            var vectorSearchSchema = tsdb.Documents.Catalog.TryGet(statement.Measurement)
                ?? throw new InvalidOperationException(
                    $"vector_search(...) 的 source '{statement.Measurement}' 必须是 document collection。");
            var (accessPath, indexName, estimatedRows) =
                DocumentVectorSearchExecutor.ExplainAccess(tsdb, statement, vectorSearchSchema);
            return new SqlExplainExecutionResult(
                Database: databaseName,
                StatementType: "vector_search",
                Measurement: statement.Measurement,
                MatchedSeriesCount: 0,
                EstimatedSegmentCount: 0,
                EstimatedBlockCount: 0,
                EstimatedScannedRows: estimatedRows,
                EstimatedMemTableRows: estimatedRows,
                EstimatedSegmentRows: 0,
                HasTimeFilter: statement.Where is not null,
                TagFilterCount: 0,
                AccessPath: accessPath,
                IndexName: indexName);
        }

        if (HybridSearchExecutor.IsHybridSearch(statement))
        {
            var hybridDocumentSchema = tsdb.Documents.Catalog.TryGet(statement.Measurement);
            if (hybridDocumentSchema is not null)
            {
                var (hybridAccessPath, hybridIndexName, hybridRowCount) =
                    HybridSearchExecutor.ExplainAccess(tsdb, statement, hybridDocumentSchema);
                return new SqlExplainExecutionResult(
                    Database: databaseName,
                    StatementType: "hybrid_search",
                    Measurement: statement.Measurement,
                    MatchedSeriesCount: 0,
                    EstimatedSegmentCount: 0,
                    EstimatedBlockCount: 0,
                    EstimatedScannedRows: hybridRowCount,
                    EstimatedMemTableRows: hybridRowCount,
                    EstimatedSegmentRows: 0,
                    HasTimeFilter: statement.Where is not null,
                    TagFilterCount: 0,
                    AccessPath: hybridAccessPath,
                    IndexName: hybridIndexName);
            }

            var hybridMeasurementSchema = tsdb.Measurements.TryGet(statement.Measurement)
                ?? throw new InvalidOperationException(
                    $"Measurement '{statement.Measurement}' 不存在；请先执行 CREATE MEASUREMENT。");
            var (measurementAccessPath, measurementIndexName, measurementRowCount) =
                HybridSearchExecutor.ExplainAccess(tsdb, statement, hybridMeasurementSchema);
            return new SqlExplainExecutionResult(
                Database: databaseName,
                StatementType: "hybrid_search",
                Measurement: statement.Measurement,
                MatchedSeriesCount: 0,
                EstimatedSegmentCount: 0,
                EstimatedBlockCount: 0,
                EstimatedScannedRows: measurementRowCount,
                EstimatedMemTableRows: measurementRowCount,
                EstimatedSegmentRows: 0,
                HasTimeFilter: statement.Where is not null,
                TagFilterCount: 0,
                AccessPath: measurementAccessPath,
                IndexName: measurementIndexName);
        }

        if (statement.Join is not null)
        {
            var joinPlan = JoinSqlExecutor.ExplainPlan(tsdb, statement);
            return new SqlExplainExecutionResult(
                Database: databaseName,
                StatementType: "select_join",
                Measurement: statement.Measurement,
                MatchedSeriesCount: joinPlan.MatchedSeriesCount,
                EstimatedSegmentCount: 0,
                EstimatedBlockCount: 0,
                EstimatedScannedRows: joinPlan.MatchedSeriesCount + joinPlan.TableCandidateRows,
                EstimatedMemTableRows: joinPlan.MatchedSeriesCount,
                EstimatedSegmentRows: 0,
                HasTimeFilter: joinPlan.FilterPlan.MeasurementWhere.TimeRange != TimeRange.All,
                TagFilterCount: joinPlan.FilterPlan.MeasurementWhere.TagFilter.Count,
                AccessPath: joinPlan.AccessPath,
                IndexName: joinPlan.IndexName);
        }

        var documentSchema = tsdb.Documents.Catalog.TryGet(statement.Measurement);
        if (documentSchema is not null)
        {
            var documentPlan = DocumentSqlExecutor.ExplainPlan(tsdb, documentSchema, statement);
            return new SqlExplainExecutionResult(
                Database: databaseName,
                StatementType: "select_document_collection",
                Measurement: statement.Measurement,
                MatchedSeriesCount: 0,
                EstimatedSegmentCount: 0,
                EstimatedBlockCount: 0,
                EstimatedScannedRows: documentPlan.EstimatedCandidateRows,
                EstimatedMemTableRows: documentPlan.EstimatedCandidateRows,
                EstimatedSegmentRows: 0,
                HasTimeFilter: statement.Where is not null,
                TagFilterCount: 0,
                AccessPath: documentPlan.AccessPath,
                IndexName: documentPlan.IndexName,
                DocumentPlan: documentPlan);
        }

        var tableSchema = tsdb.Tables.Catalog.TryGet(statement.Measurement);
        if (tableSchema is not null)
        {
            var store = tsdb.Tables.Open(tableSchema.Name);
            var (accessPath, indexName, rowCount) = ExplainTableAccess(store, tableSchema, statement.Where);
            return new SqlExplainExecutionResult(
                Database: databaseName,
                StatementType: "select_table",
                Measurement: statement.Measurement,
                MatchedSeriesCount: 0,
                EstimatedSegmentCount: 0,
                EstimatedBlockCount: 0,
                EstimatedScannedRows: rowCount,
                EstimatedMemTableRows: rowCount,
                EstimatedSegmentRows: 0,
                HasTimeFilter: statement.Where is not null,
                TagFilterCount: 0,
                AccessPath: accessPath,
                IndexName: indexName);
        }

        if (statement.TableValuedFunction is FunctionCallExpression { Name: var tvfName }
            && (string.Equals(tvfName, "json_each", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tvfName, "json_table", StringComparison.OrdinalIgnoreCase)))
        {
            var (accessPath, indexName, rowCount) = JsonFileSqlExecutor.ExplainAccess(statement);
            return new SqlExplainExecutionResult(
                Database: databaseName,
                StatementType: "json_file_virtual_table",
                Measurement: statement.Measurement,
                MatchedSeriesCount: 0,
                EstimatedSegmentCount: 0,
                EstimatedBlockCount: 0,
                EstimatedScannedRows: rowCount,
                EstimatedMemTableRows: rowCount,
                EstimatedSegmentRows: 0,
                HasTimeFilter: statement.Where is not null,
                TagFilterCount: 0,
                AccessPath: accessPath,
                IndexName: indexName);
        }

        if (statement.TableValuedFunction is FunctionCallExpression { Name: var otherTvfName }
            && !string.Equals(otherTvfName, "forecast", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(otherTvfName, "knn", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"EXPLAIN 暂不支持表值函数 '{otherTvfName}'；当前仅支持普通 SELECT、forecast(...) 与 knn(...)。");
        }

        var schema = tsdb.Measurements.TryGet(statement.Measurement)
            ?? throw new InvalidOperationException(
                $"Measurement '{statement.Measurement}' 不存在；请先执行 CREATE MEASUREMENT。");

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var where = DecomposeWhereClause(statement.Where, schema, nowMs);
        var matchedSeries = tsdb.Catalog.Find(statement.Measurement, where.TagFilter);
        var fields = ResolveScannedFields(statement, schema);

        var segmentIds = new HashSet<long>();
        var estimatedSegmentRows = 0L;
        var estimatedMemTableRows = 0L;
        var estimatedBlockCount = 0;

        // 单次租约拿到 {MemTable(active+sealing) + 段索引} 一致视图。
        using var readSnapshot = tsdb.AcquireReadSnapshot();
        var memTables = readSnapshot.AllMemTables();
        var index = readSnapshot.Snapshot.Index;

        foreach (var series in matchedSeries)
        {
            foreach (var fieldName in fields)
            {
                foreach (var memTable in memTables)
                    estimatedMemTableRows += CountMemTableRows(memTable, series.Id, fieldName, where.TimeRange);

                var candidates = index.LookupCandidates(
                    series.Id,
                    fieldName,
                    where.TimeRange.FromInclusive,
                    where.TimeRange.ToInclusive);

                foreach (var candidate in candidates)
                {
                    segmentIds.Add(candidate.SegmentId);
                    estimatedBlockCount++;
                    estimatedSegmentRows += EstimateBlockRows(candidate.Descriptor, where.TimeRange);
                }
            }
        }

        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "select",
            Measurement: statement.Measurement,
            MatchedSeriesCount: matchedSeries.Count,
            EstimatedSegmentCount: segmentIds.Count,
            EstimatedBlockCount: estimatedBlockCount,
            EstimatedScannedRows: checked(estimatedMemTableRows + estimatedSegmentRows),
            EstimatedMemTableRows: estimatedMemTableRows,
            EstimatedSegmentRows: estimatedSegmentRows,
            HasTimeFilter: where.TimeRange != TimeRange.All,
            TagFilterCount: where.TagFilter.Count,
            AccessPath: where.TagFilter.Count > 0 ? "tag_index" : "measurement_scan",
            IndexName: null);
    }

    private static (string AccessPath, string? IndexName, int EstimatedRows) ExplainTableAccess(
        TableStore store,
        TableSchema schema,
        SqlExpression? where)
    {
        if (TableSqlExecutor.ChooseBestIndexForWhere(schema, where, out var values) is { } index)
            return (string.IsNullOrWhiteSpace(index.JsonPath) ? "secondary_index" : "json_path_index",
                index.Name,
                store.GetByIndex(index, values).Count);

        if (TryHasPrimaryKeyFilter(schema, where))
            return ("primary_key", "primary", values.Count == 0 ? 0 : 1);

        return ("table_scan", null, store.Scan().Count);
    }

    private static bool TryHasPrimaryKeyFilter(TableSchema schema, SqlExpression? where)
    {
        if (where is null)
            return false;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leaf in FlattenAnd(where))
        {
            if (leaf is not BinaryExpression { Operator: SqlBinaryOperator.Equal } binary)
                continue;

            var identifier = binary.Left as IdentifierExpression ?? binary.Right as IdentifierExpression;
            if (identifier is not null)
                names.Add(identifier.Name);
        }

        return schema.PrimaryKey.All(names.Contains);
    }

    private static IReadOnlyList<string> ResolveScannedFields(SelectStatement statement, MeasurementSchema schema)
    {
        if (statement.TableValuedFunction is not null)
            return ResolveTvfFields(statement, schema);

        var fields = new HashSet<string>(StringComparer.Ordinal);
        var hasAggregate = false;
        var hasNonAggregate = false;

        foreach (var projection in statement.Projections)
            CollectProjectionFields(projection.Expression, schema, fields, ref hasAggregate, ref hasNonAggregate);

        ValidateGroupBy(statement.GroupBy, hasAggregate);

        if (hasAggregate && hasNonAggregate)
        {
            throw new InvalidOperationException(
                "SELECT 中不允许同时出现聚合函数与非聚合列（v1 不支持 GROUP BY 列）。");
        }

        if (!hasAggregate && fields.Count == 0)
        {
            var probeField = schema.FieldColumns.FirstOrDefault()
                ?? throw new InvalidOperationException("Measurement schema 至少需要一个 FIELD 列。");
            fields.Add(probeField.Name);
        }

        return fields.ToArray();
    }

    private static IReadOnlyList<string> ResolveTvfFields(SelectStatement statement, MeasurementSchema schema)
    {
        var tvf = statement.TableValuedFunction
            ?? throw new InvalidOperationException("内部错误：缺少表值函数调用。");

        if (string.Equals(tvf.Name, "forecast", StringComparison.OrdinalIgnoreCase))
        {
            if (tvf.Arguments.Count < 2 || tvf.Arguments[1] is not IdentifierExpression fieldId)
                throw new InvalidOperationException("forecast 第 2 个参数必须是字段列名。");

            var column = schema.TryGetColumn(fieldId.Name)
                ?? throw new InvalidOperationException($"forecast 引用了未知字段 '{fieldId.Name}'。");
            if (column.Role != MeasurementColumnRole.Field)
                throw new InvalidOperationException($"forecast 第 2 个参数 '{fieldId.Name}' 必须是 FIELD 列。");
            return [column.Name];
        }

        if (string.Equals(tvf.Name, "knn", StringComparison.OrdinalIgnoreCase))
        {
            if (tvf.Arguments.Count < 2 || tvf.Arguments[1] is not IdentifierExpression columnId)
                throw new InvalidOperationException("knn 第 2 个参数必须是向量列名标识符。");

            var column = schema.TryGetColumn(columnId.Name)
                ?? throw new InvalidOperationException($"knn 引用了未知列 '{columnId.Name}'。");
            if (column.Role != MeasurementColumnRole.Field)
                throw new InvalidOperationException($"knn 的列参数 '{columnId.Name}' 必须是 FIELD 列。");
            return [column.Name];
        }

        throw new InvalidOperationException(
            $"EXPLAIN 暂不支持表值函数 '{tvf.Name}'；当前仅支持 forecast(...) 与 knn(...)。");
    }

    private static void CollectProjectionFields(
        SqlExpression expression,
        MeasurementSchema schema,
        HashSet<string> fields,
        ref bool hasAggregate,
        ref bool hasNonAggregate)
    {
        switch (expression)
        {
            case StarExpression:
                hasNonAggregate = true;
                foreach (var field in schema.FieldColumns)
                    fields.Add(field.Name);
                return;

            case IdentifierExpression identifier:
                hasNonAggregate = true;
                if (string.Equals(identifier.Name, "time", StringComparison.OrdinalIgnoreCase))
                    return;

                var column = schema.TryGetColumn(identifier.Name)
                    ?? throw new InvalidOperationException($"SELECT 中引用了未知列 '{identifier.Name}'。");
                if (column.Role == MeasurementColumnRole.Field)
                    fields.Add(column.Name);
                return;

            case FunctionCallExpression function:
                var kind = FunctionRegistry.GetFunctionKind(function.Name);
                switch (kind)
                {
                    case FunctionKind.Aggregate:
                        hasAggregate = true;
                        CollectAggregateFields(function, schema, fields);
                        return;

                    case FunctionKind.Scalar:
                        hasNonAggregate = true;
                        foreach (var dependency in GetScalarFieldDependencies(function))
                        {
                            var scalarColumn = schema.TryGetColumn(dependency)
                                ?? throw new InvalidOperationException($"SELECT 中引用了未知列 '{dependency}'。");
                            if (scalarColumn.Role == MeasurementColumnRole.Field)
                                fields.Add(scalarColumn.Name);
                        }
                        return;

                    case FunctionKind.Window:
                        hasNonAggregate = true;
                        if (!FunctionRegistry.TryGetWindow(function.Name, out var windowFunction))
                            throw new InvalidOperationException($"未知窗口函数 '{function.Name}'。");
                        var evaluator = windowFunction.CreateEvaluator(function, schema);
                        fields.Add(evaluator.FieldName);
                        return;

                    case FunctionKind.Unknown:
                        throw new InvalidOperationException(
                            $"未知函数 '{function.Name}'；当前仅支持内置 aggregate/scalar/window 函数。");

                    default:
                        throw new InvalidOperationException($"当前 EXPLAIN 不支持投影函数 '{function.Name}'。");
                }

            default:
                throw new InvalidOperationException(
                    $"不支持的投影表达式类型 '{expression.GetType().Name}'。");
        }
    }

    private static void CollectAggregateFields(
        FunctionCallExpression function,
        MeasurementSchema schema,
        HashSet<string> fields)
    {
        if (!FunctionRegistry.TryGetAggregate(function.Name, out var aggregate))
            throw new InvalidOperationException($"未知聚合函数 '{function.Name}'。");

        var fieldName = aggregate.ResolveFieldName(function, schema);
        if (fieldName is not null)
        {
            fields.Add(fieldName);
            return;
        }

        foreach (var field in schema.FieldColumns)
        {
            if (field.DataType != FieldType.String)
                fields.Add(field.Name);
        }
    }

    private static IEnumerable<string> GetScalarFieldDependencies(SqlExpression expression)
    {
        switch (expression)
        {
            case IdentifierExpression identifier when !string.Equals(identifier.Name, "time", StringComparison.OrdinalIgnoreCase):
                yield return identifier.Name;
                yield break;

            case FunctionCallExpression function:
                foreach (var argument in function.Arguments)
                {
                    foreach (var dependency in GetScalarFieldDependencies(argument))
                        yield return dependency;
                }
                yield break;

            case UnaryExpression unary:
                foreach (var dependency in GetScalarFieldDependencies(unary.Operand))
                    yield return dependency;
                yield break;

            case BinaryExpression binary:
                foreach (var dependency in GetScalarFieldDependencies(binary.Left))
                    yield return dependency;
                foreach (var dependency in GetScalarFieldDependencies(binary.Right))
                    yield return dependency;
                yield break;

            default:
                yield break;
        }
    }

    private static void ValidateGroupBy(IReadOnlyList<SqlExpression> groupBy, bool hasAggregate)
    {
        if (groupBy.Count == 0)
            return;

        if (!hasAggregate)
            throw new InvalidOperationException("GROUP BY time(...) 仅在聚合查询中有效。");

        if (groupBy.Count != 1
            || groupBy[0] is not FunctionCallExpression
            {
                Name: var name,
                IsStar: false,
                Arguments.Count: 1,
                Arguments: [DurationLiteralExpression]
            }
            || !string.Equals(name, "time", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前仅支持 GROUP BY time(duration)。");
        }
    }

    private static long CountMemTableRows(MemTable memTable, ulong seriesId, string fieldName, TimeRange timeRange)
    {
        ArgumentNullException.ThrowIfNull(memTable);
        ArgumentException.ThrowIfNullOrEmpty(fieldName);

        var bucket = memTable.TryGet(new SeriesFieldKey(seriesId, fieldName));
        if (bucket is null || bucket.Count == 0)
            return 0;

        if (timeRange.FromInclusive <= bucket.MinTimestamp && timeRange.ToInclusive >= bucket.MaxTimestamp)
            return bucket.Count;

        return bucket.SnapshotRange(timeRange.FromInclusive, timeRange.ToInclusive).Length;
    }

    private static long EstimateBlockRows(
        in SonnetDB.Storage.Segments.BlockDescriptor descriptor,
        TimeRange timeRange)
    {
        if (descriptor.Count <= 0)
            return 0;

        if (descriptor.MinTimestamp >= timeRange.FromInclusive && descriptor.MaxTimestamp <= timeRange.ToInclusive)
            return descriptor.Count;

        var overlapStart = Math.Max(descriptor.MinTimestamp, timeRange.FromInclusive);
        var overlapEnd = Math.Min(descriptor.MaxTimestamp, timeRange.ToInclusive);
        if (overlapStart > overlapEnd)
            return 0;

        if (descriptor.MinTimestamp == descriptor.MaxTimestamp)
            return descriptor.Count;

        var overlapSpan = ((decimal)overlapEnd - overlapStart) + 1m;
        var totalSpan = ((decimal)descriptor.MaxTimestamp - descriptor.MinTimestamp) + 1m;
        var estimate = decimal.Ceiling(descriptor.Count * overlapSpan / totalSpan);
        return Math.Clamp((long)estimate, 1L, descriptor.Count);
    }

    private static ExplainWhereClause DecomposeWhereClause(
        SqlExpression? where,
        MeasurementSchema schema,
        long nowMs)
    {
        // 复用执行路径的分解器（#217：支持残差字段谓词 / OR），保证 EXPLAIN 与实际执行一致，
        // 不再在此重复一份会对字段谓词抛错的分解逻辑。
        var decomposed = WhereClauseDecomposer.Decompose(where, schema, nowMs);
        return new ExplainWhereClause(decomposed.TagFilter, decomposed.TimeRange);
    }

    private static IEnumerable<SqlExpression> FlattenAnd(SqlExpression expression)
    {
        if (expression is BinaryExpression { Operator: SqlBinaryOperator.And } andExpression)
        {
            foreach (var left in FlattenAnd(andExpression.Left))
                yield return left;
            foreach (var right in FlattenAnd(andExpression.Right))
                yield return right;
            yield break;
        }

        yield return expression;
    }
}
