using System.Globalization;
using SonnetDB.Catalog;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Modbus;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Query.Functions;
using SonnetDB.Sql.Ast;
using SonnetDB.Storage.Format;
using SonnetDB.Tables;
using SonnetDB.Views;

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
    DocumentQueryPlan? DocumentPlan = null,
    string? ScanFilter = null)
{
    /// <summary>候选复检是否允许在首个匹配行处停止；不表示存储层已使用流式 cursor。</summary>
    public bool? EarlyExit { get; init; }

    /// <summary>候选读取后是否仍需执行残余谓词。</summary>
    public bool? HasResidualPredicate { get; init; }

    /// <summary>未使用期望访问路径时的稳定回退原因。</summary>
    public string? FallbackReason { get; init; }

    /// <summary>多路候选源的有界规模与融合输出上限。</summary>
    public string? CandidateContract { get; init; }

    /// <summary>轻量成本模型估算的返回行数。</summary>
    public long? EstimatedOutputRows { get; init; }

    /// <summary>估算的平均行宽。</summary>
    public double? EstimatedRowWidth { get; init; }

    /// <summary>估算的逻辑读取量。</summary>
    public long? EstimatedLogicalReads { get; init; }

    /// <summary>有限成本模型估算的相对成本。</summary>
    public double? EstimatedCost { get; init; }

    /// <summary>估算来源：refreshed、statistics_stale 或 statistics_missing。</summary>
    public string? EstimateSource { get; init; }

    /// <summary>估算使用的统计 sequence。</summary>
    public long? StatisticsSequence { get; init; }

    /// <summary>统计刷新距当前的毫秒数。</summary>
    public long? StatisticsFreshnessMilliseconds { get; init; }

    /// <summary>有界候选计划摘要；不包含参数值和行内容。</summary>
    public string? CandidatePlans { get; init; }

    /// <summary>计划节点的稳定名称。</summary>
    public string? PlanNode { get; init; }

    /// <summary>EXPLAIN ANALYZE 实际访问路径。</summary>
    public string? ActualAccessPath { get; init; }

    /// <summary>EXPLAIN ANALYZE 实际使用的索引。</summary>
    public string? ActualIndexName { get; init; }

    /// <summary>EXPLAIN ANALYZE 实际回退原因。</summary>
    public string? ActualFallbackReason { get; init; }

    /// <summary>EXPLAIN ANALYZE 实际返回行数。</summary>
    public long? ActualRows { get; init; }

    /// <summary>EXPLAIN ANALYZE 实际候选行数。</summary>
    public long? ActualCandidateRows { get; init; }

    /// <summary>EXPLAIN ANALYZE 实际谓词检查行数。</summary>
    public long? ActualExaminedRows { get; init; }

    /// <summary>EXPLAIN ANALYZE 被残余谓词移除的行数估算。</summary>
    public long? ActualRowsRemoved { get; init; }

    /// <summary>EXPLAIN ANALYZE 执行循环数。</summary>
    public long? ActualLoops { get; init; }

    /// <summary>EXPLAIN ANALYZE 核心执行耗时（毫秒）。</summary>
    public double? ActualExecutionMilliseconds { get; init; }

    /// <summary>EXPLAIN ANALYZE 当前线程托管分配字节数。</summary>
    public long? ActualAllocatedBytes { get; init; }

    /// <summary>EXPLAIN ANALYZE 观测到的表/KV 锁等待毫秒数。</summary>
    public double? ActualLockWaitMilliseconds { get; init; }

    /// <summary>EXPLAIN ANALYZE WAL fsync 次数。</summary>
    public long? ActualWalFsyncCount { get; init; }

    /// <summary>EXPLAIN ANALYZE spill 次数；当前受限执行器固定为 0。</summary>
    public long? ActualSpillCount { get; init; }
}

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

    private sealed record ComposedSourceExplain(
        string Alias,
        string AccessPath,
        string? IndexName,
        long EstimatedRows,
        string? CandidateContract,
        string? FallbackReason);

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
            ShowViewsStatement => ExplainShowViews(databaseName, tsdb),
            ShowMaterializedViewsStatement => ExplainShowMaterializedViews(databaseName, tsdb),
            ShowDocumentCollectionsStatement => ExplainShowDocumentCollections(databaseName, tsdb),
            ShowTableIndexesStatement showIndexes => ExplainShowIndexes(databaseName, tsdb, showIndexes.TableName),
            ShowDocumentIndexesStatement showDocumentIndexes => ExplainShowDocumentIndexes(databaseName, tsdb, showDocumentIndexes.CollectionName),
            ShowFullTextIndexesStatement showFullTextIndexes => ExplainShowFullTextIndexes(databaseName, tsdb, showFullTextIndexes.CollectionName),
            ShowModbusSourcesStatement => ExplainShowModbusSources(databaseName, tsdb.Modbus.Catalog),
            ShowModbusEndpointsStatement => ExplainShowModbusEndpoints(databaseName, tsdb.Modbus.Catalog),
            DescribeMeasurementStatement describe => ExplainDescribeMeasurement(databaseName, tsdb, describe.Name),
            DescribeTableStatement describeTable => ExplainDescribeTable(databaseName, tsdb, describeTable.Name),
            DescribeViewStatement describeView => ExplainDescribeView(databaseName, tsdb, describeView.Name),
            DescribeMaterializedViewStatement describeMaterializedView => ExplainDescribeMaterializedView(
                databaseName,
                tsdb,
                describeMaterializedView.Name),
            DescribeDocumentCollectionStatement describeDocumentCollection => ExplainDescribeDocumentCollection(databaseName, tsdb, describeDocumentCollection.Name),
            DescribeModbusSourceStatement describeModbusSource => ExplainDescribeModbusSource(
                databaseName,
                tsdb,
                describeModbusSource.Name),
            DescribeModbusEndpointStatement describeModbusEndpoint => ExplainDescribeModbusEndpoint(
                databaseName,
                tsdb,
                describeModbusEndpoint.Name),
            DescribeModbusTableStatement describeModbusTable => ExplainDescribeModbusTable(
                databaseName,
                tsdb,
                describeModbusTable.Name),
            SelectStatement select => ExplainSelect(databaseName, tsdb, select),
            _ => throw new InvalidOperationException(
                "EXPLAIN 仅支持 SELECT、SHOW MEASUREMENTS / SHOW TABLES / SHOW VIEWS / SHOW DOCUMENT COLLECTIONS / SHOW INDEXES / SHOW JSON INDEXES / SHOW FULLTEXT INDEXES 与 DESCRIBE [MEASUREMENT|TABLE|VIEW|DOCUMENT COLLECTION]。"),
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
            new object?[] { "scan_filter", result.ScanFilter },
            new object?[] { "early_exit", result.EarlyExit },
            new object?[] { "has_residual_predicate", result.HasResidualPredicate },
            new object?[] { "fallback_reason", result.FallbackReason },
            new object?[] { "candidate_contract", result.CandidateContract },
            new object?[] { "plan_node", result.PlanNode },
            new object?[] { "actual_access_path", result.ActualAccessPath },
            new object?[] { "actual_index_name", result.ActualIndexName },
            new object?[] { "actual_fallback_reason", result.ActualFallbackReason },
            new object?[] { "estimated_row_width", result.EstimatedRowWidth },
            new object?[] { "estimated_logical_reads", result.EstimatedLogicalReads },
            new object?[] { "estimated_cost", result.EstimatedCost },
            new object?[] { "estimate_source", result.EstimateSource },
            new object?[] { "statistics_sequence", result.StatisticsSequence },
            new object?[] { "statistics_freshness_ms", result.StatisticsFreshnessMilliseconds },
            new object?[] { "actual_rows", result.ActualRows },
            new object?[] { "actual_candidate_rows", result.ActualCandidateRows },
            new object?[] { "actual_examined_rows", result.ActualExaminedRows },
            new object?[] { "actual_rows_removed", result.ActualRowsRemoved },
            new object?[] { "actual_loops", result.ActualLoops },
            new object?[] { "actual_execution_ms", result.ActualExecutionMilliseconds },
            new object?[] { "actual_allocated_bytes", result.ActualAllocatedBytes },
            new object?[] { "actual_lock_wait_ms", result.ActualLockWaitMilliseconds },
            new object?[] { "actual_wal_fsync_count", result.ActualWalFsyncCount },
            new object?[] { "actual_spill_count", result.ActualSpillCount },
        };

        if (result.DocumentPlan is null && result.EstimatedOutputRows is not null)
            rows.Add(new object?[] { "estimated_output_rows", result.EstimatedOutputRows });

        if (result.DocumentPlan is null && result.CandidatePlans is not null)
            rows.Add(new object?[] { "candidate_plans", result.CandidatePlans });

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

    /// <summary>从单次目录快照估算 SHOW MODBUS SOURCES 返回行数。</summary>
    private static SqlExplainExecutionResult ExplainShowModbusSources(
        string? databaseName,
        ModbusCatalog catalog)
    {
        ModbusCatalogSnapshot snapshot = catalog.CaptureSnapshot();
        return ExplainCatalogMetadata(
            databaseName,
            "show_modbus_sources",
            measurement: null,
            snapshot.Sources.Count);
    }

    /// <summary>从单次目录快照估算 SHOW MODBUS ENDPOINTS 返回行数。</summary>
    private static SqlExplainExecutionResult ExplainShowModbusEndpoints(
        string? databaseName,
        ModbusCatalog catalog)
    {
        ModbusCatalogSnapshot snapshot = catalog.CaptureSnapshot();
        return ExplainCatalogMetadata(
            databaseName,
            "show_modbus_endpoints",
            measurement: null,
            snapshot.Endpoints.Count);
    }

    /// <summary>解释一个只读取本地 Modbus source 定义的元数据查询。</summary>
    private static SqlExplainExecutionResult ExplainDescribeModbusSource(
        string? databaseName,
        Tsdb tsdb,
        string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ModbusCatalogSnapshot snapshot = tsdb.Modbus.Catalog.CaptureSnapshot();
        if (!snapshot.Sources.ContainsKey(sourceName))
            throw new InvalidOperationException($"MODBUS SOURCE '{sourceName}' 不存在。");
        return ExplainCatalogMetadata(databaseName, "describe_modbus_source", sourceName, 1);
    }

    /// <summary>解释一个只读取本地 Modbus endpoint 定义的元数据查询。</summary>
    private static SqlExplainExecutionResult ExplainDescribeModbusEndpoint(
        string? databaseName,
        Tsdb tsdb,
        string endpointName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        ModbusCatalogSnapshot snapshot = tsdb.Modbus.Catalog.CaptureSnapshot();
        if (!snapshot.Endpoints.ContainsKey(endpointName))
            throw new InvalidOperationException($"MODBUS ENDPOINT '{endpointName}' 不存在。");
        return ExplainCatalogMetadata(databaseName, "describe_modbus_endpoint", endpointName, 1);
    }

    /// <summary>解释一个只读取本地 Modbus 表映射的元数据查询。</summary>
    private static SqlExplainExecutionResult ExplainDescribeModbusTable(
        string? databaseName,
        Tsdb tsdb,
        string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ModbusCatalogSnapshot snapshot = tsdb.Modbus.Catalog.CaptureSnapshot();
        ModbusTableBinding binding = snapshot.Bindings.GetValueOrDefault(tableName)
            ?? throw new InvalidOperationException($"table '{tableName}' 不存在 MODBUS 绑定。");
        return ExplainCatalogMetadata(
            databaseName,
            "describe_modbus_table",
            tableName,
            binding.Columns.Count);
    }

    /// <summary>构造不扫描关系数据、只访问本地 catalog 的解释结果。</summary>
    private static SqlExplainExecutionResult ExplainCatalogMetadata(
        string? databaseName,
        string statementType,
        string? measurement,
        int rowCount)
        => new(
            Database: databaseName,
            StatementType: statementType,
            Measurement: measurement,
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: rowCount,
            EstimatedMemTableRows: 0,
            EstimatedSegmentRows: 0,
            HasTimeFilter: false,
            TagFilterCount: 0,
            AccessPath: "catalog",
            IndexName: null);

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

    private static SqlExplainExecutionResult ExplainShowViews(string? databaseName, Tsdb tsdb)
    {
        int viewCount = tsdb.Views.Catalog.Count;
        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "show_views",
            Measurement: null,
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: viewCount,
            EstimatedMemTableRows: 0,
            EstimatedSegmentRows: 0,
            HasTimeFilter: false,
            TagFilterCount: 0,
            AccessPath: "catalog",
            IndexName: null);
    }

    private static SqlExplainExecutionResult ExplainShowMaterializedViews(string? databaseName, Tsdb tsdb)
    {
        int viewCount = tsdb.MaterializedViews.Catalog.Count;
        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "show_materialized_views",
            Measurement: null,
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: viewCount,
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

    private static SqlExplainExecutionResult ExplainDescribeView(
        string? databaseName,
        Tsdb tsdb,
        string viewName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);

        var definition = tsdb.Views.Catalog.TryGet(viewName)
            ?? throw new InvalidOperationException($"view '{viewName}' 不存在。");
        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "describe_view",
            Measurement: definition.Name,
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: 1,
            EstimatedMemTableRows: 0,
            EstimatedSegmentRows: 0,
            HasTimeFilter: false,
            TagFilterCount: 0,
            AccessPath: "catalog",
            IndexName: null);
    }

    private static SqlExplainExecutionResult ExplainDescribeMaterializedView(
        string? databaseName,
        Tsdb tsdb,
        string viewName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);
        var definition = tsdb.MaterializedViews.Catalog.TryGet(viewName)
            ?? throw new InvalidOperationException($"materialized view '{viewName}' 不存在。");
        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "describe_materialized_view",
            Measurement: definition.Name,
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: 1,
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
        if (TryGetStandaloneExists(statement, out var existsSubquery))
            return ExplainStandaloneExists(databaseName, tsdb, existsSubquery);

        if (RelationalSelectExecutor.TryRewriteNonCorrelatedInSemijoin(
            tsdb,
            statement,
            out var semijoinStatement,
            out _))
        {
            statement = semijoinStatement;
        }

        if (statement.FromSubquery is not null)
            return ExplainRelationalComposition(databaseName, tsdb, statement);

        if (statement.FromSubquery is null
            && statement.TableValuedFunction is null
            && tsdb.Views.Catalog.TryGet(statement.Measurement) is { } view)
        {
            return new SqlExplainExecutionResult(
                Database: databaseName,
                StatementType: "select_view",
                Measurement: view.Name,
                MatchedSeriesCount: 0,
                EstimatedSegmentCount: 0,
                EstimatedBlockCount: 0,
                EstimatedScannedRows: 0,
                EstimatedMemTableRows: 0,
                EstimatedSegmentRows: 0,
                HasTimeFilter: statement.Where is not null,
                TagFilterCount: 0,
                AccessPath: "view_expansion",
                IndexName: null,
                ScanFilter: DescribeScanFilter(statement.Where));
        }

        if (statement.FromSubquery is null
            && statement.TableValuedFunction is null
            && tsdb.MaterializedViews.Catalog.TryGet(statement.Measurement) is { } materializedView)
        {
            return new SqlExplainExecutionResult(
                Database: databaseName,
                StatementType: "select_materialized_view",
                Measurement: materializedView.Name,
                MatchedSeriesCount: 0,
                EstimatedSegmentCount: materializedView.ActiveGeneration == 0 ? 0 : 1,
                EstimatedBlockCount: 0,
                EstimatedScannedRows: materializedView.RowCount,
                EstimatedMemTableRows: 0,
                EstimatedSegmentRows: materializedView.RowCount,
                HasTimeFilter: statement.Where is not null,
                TagFilterCount: 0,
                AccessPath: materializedView.ActiveGeneration == 0
                    ? "materialized_view_uninitialized"
                    : "materialized_view_snapshot",
                IndexName: null,
                ScanFilter: DescribeScanFilter(statement.Where));
        }

        string? scanFilter = DescribeScanFilter(statement.Where);
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
                IndexName: indexName,
                ScanFilter: scanFilter);
        }

        if (HybridSearchExecutor.IsHybridSearch(statement))
        {
            var hybridDocumentSchema = tsdb.Documents.Catalog.TryGet(statement.Measurement);
            if (hybridDocumentSchema is not null)
            {
                HybridSearchExplainPlan hybridPlan =
                    HybridSearchExecutor.ExplainAccess(tsdb, statement, hybridDocumentSchema);
                return new SqlExplainExecutionResult(
                    Database: databaseName,
                    StatementType: "hybrid_search",
                    Measurement: statement.Measurement,
                    MatchedSeriesCount: 0,
                    EstimatedSegmentCount: 0,
                    EstimatedBlockCount: 0,
                    EstimatedScannedRows: hybridPlan.EstimatedRows,
                    EstimatedMemTableRows: hybridPlan.EstimatedRows,
                    EstimatedSegmentRows: 0,
                    HasTimeFilter: statement.Where is not null,
                    TagFilterCount: 0,
                    AccessPath: hybridPlan.AccessPath,
                    IndexName: hybridPlan.IndexName,
                    ScanFilter: scanFilter)
                {
                    CandidateContract = hybridPlan.CandidateContract,
                    FallbackReason = hybridPlan.FallbackReason,
                };
            }

            var hybridMeasurementSchema = tsdb.Measurements.TryGet(statement.Measurement)
                ?? throw new InvalidOperationException(
                    $"Measurement '{statement.Measurement}' 不存在；请先执行 CREATE MEASUREMENT。");
            HybridSearchExplainPlan measurementPlan =
                HybridSearchExecutor.ExplainAccess(tsdb, statement, hybridMeasurementSchema);
            return new SqlExplainExecutionResult(
                Database: databaseName,
                StatementType: "hybrid_search",
                Measurement: statement.Measurement,
                MatchedSeriesCount: 0,
                EstimatedSegmentCount: 0,
                EstimatedBlockCount: 0,
                EstimatedScannedRows: measurementPlan.EstimatedRows,
                EstimatedMemTableRows: measurementPlan.EstimatedRows,
                EstimatedSegmentRows: 0,
                HasTimeFilter: statement.Where is not null,
                TagFilterCount: 0,
                AccessPath: measurementPlan.AccessPath,
                IndexName: measurementPlan.IndexName,
                ScanFilter: scanFilter)
            {
                CandidateContract = measurementPlan.CandidateContract,
                FallbackReason = measurementPlan.FallbackReason,
            };
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
                IndexName: joinPlan.IndexName,
                ScanFilter: scanFilter)
            {
                FallbackReason = joinPlan.TableEstimate.FallbackReason,
                EstimatedOutputRows = joinPlan.TableEstimate.EstimatedRows,
                EstimatedRowWidth = joinPlan.TableEstimate.EstimatedRowWidth,
                EstimatedLogicalReads = joinPlan.TableEstimate.EstimatedLogicalReads,
                EstimatedCost = joinPlan.TableEstimate.EstimatedCost,
                EstimateSource = joinPlan.TableEstimate.EstimateSource,
                StatisticsSequence = joinPlan.TableEstimate.StatisticsSequence,
                StatisticsFreshnessMilliseconds = joinPlan.TableEstimate.StatisticsFreshnessMilliseconds,
                CandidatePlans = joinPlan.TableEstimate.CandidatePlans,
                PlanNode = "hash_join",
            };
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
                DocumentPlan: documentPlan,
                ScanFilter: scanFilter);
        }

        var tableSchema = tsdb.Tables.Catalog.TryGet(statement.Measurement);
        if (tableSchema is not null)
        {
            var store = tsdb.Tables.Open(tableSchema.Name);
            TableAccessCostEstimate tablePlan = ExplainTableAccess(
                store,
                tableSchema,
                statement.Where,
                statement);
            return new SqlExplainExecutionResult(
                Database: databaseName,
                StatementType: "select_table",
                Measurement: statement.Measurement,
                MatchedSeriesCount: 0,
                EstimatedSegmentCount: 0,
                EstimatedBlockCount: 0,
                EstimatedScannedRows: tablePlan.EstimatedRows,
                EstimatedMemTableRows: tablePlan.EstimatedRows,
                EstimatedSegmentRows: 0,
                HasTimeFilter: statement.Where is not null,
                TagFilterCount: 0,
                AccessPath: tablePlan.AccessPath,
                IndexName: tablePlan.IndexName,
                ScanFilter: scanFilter)
            {
                FallbackReason = tablePlan.FallbackReason,
                EstimatedOutputRows = tablePlan.EstimatedRows,
                EstimatedRowWidth = tablePlan.EstimatedRowWidth,
                EstimatedLogicalReads = tablePlan.EstimatedLogicalReads,
                EstimatedCost = tablePlan.EstimatedCost,
                EstimateSource = tablePlan.EstimateSource,
                StatisticsSequence = tablePlan.StatisticsSequence,
                StatisticsFreshnessMilliseconds = tablePlan.StatisticsFreshnessMilliseconds,
                CandidatePlans = tablePlan.CandidatePlans,
                PlanNode = tablePlan.AccessPath,
            };
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
                IndexName: indexName,
                ScanFilter: scanFilter);
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
            IndexName: null,
            ScanFilter: scanFilter);
    }

    private static SqlExplainExecutionResult ExplainRelationalComposition(
        string? databaseName,
        Tsdb tsdb,
        SelectStatement statement)
    {
        var sources = new List<ComposedSourceExplain>(statement.JoinClauses.Count + 1)
        {
            ExplainComposedSubquery(
                tsdb,
                statement.FromSubquery!,
                statement.TableAlias ?? "source"),
        };
        foreach (JoinClause join in statement.JoinClauses)
        {
            sources.Add(join.Subquery is not null
                ? ExplainComposedSubquery(tsdb, join.Subquery, join.Alias)
                : ExplainComposedRelation(tsdb, join.TableName, join.Alias));
        }

        long estimatedRows = 0;
        foreach (ComposedSourceExplain source in sources)
            estimatedRows = SaturatingAdd(estimatedRows, source.EstimatedRows);
        string accessPath = string.Join(
            ";",
            sources.Select((source, position) =>
                $"{(position == 0 ? "source" : "join")}:{source.Alias}[{source.AccessPath}]"));
        if (sources.Count > 1)
            accessPath += ";join_operator=hash";
        string? indexName = JoinNonEmpty(sources.Select(static source => source.IndexName));
        string? candidateContract = JoinNonEmpty(sources
            .Where(static source => source.CandidateContract is not null)
            .Select(static source => $"{source.Alias}:{source.CandidateContract}"));
        string? fallbackReason = JoinNonEmpty(sources
            .Where(static source => source.FallbackReason is not null)
            .Select(static source => $"{source.Alias}:{source.FallbackReason}"));

        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "cross_model_select",
            Measurement: string.Join(",", sources.Select(static source => source.Alias)),
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: estimatedRows,
            EstimatedMemTableRows: estimatedRows,
            EstimatedSegmentRows: 0,
            HasTimeFilter: false,
            TagFilterCount: 0,
            AccessPath: accessPath,
            IndexName: indexName,
            ScanFilter: DescribeScanFilter(statement.Where))
        {
            CandidateContract = candidateContract,
            FallbackReason = fallbackReason,
        };
    }

    private static ComposedSourceExplain ExplainComposedSubquery(
        Tsdb tsdb,
        SelectStatement subquery,
        string alias)
    {
        if (subquery.GraphTable is not null)
        {
            SelectExecutionResult graphExplain = GraphTableSqlExecutor.Explain(tsdb, subquery);
            string graphKind = Convert.ToString(
                FindGraphExplainValue(graphExplain, static key => key == "graph_kind"),
                CultureInfo.InvariantCulture) ?? "unknown";
            string anchorPath = Convert.ToString(
                FindGraphExplainValue(graphExplain, static key =>
                    key == "anchor_access_path" || key.StartsWith("anchor.", StringComparison.Ordinal)),
                CultureInfo.InvariantCulture) ?? "unknown";
            string edgePath = Convert.ToString(
                FindGraphExplainValue(graphExplain, static key =>
                    key == "edge_access_path"
                    || (key.StartsWith("edge.", StringComparison.Ordinal)
                        && key.EndsWith(".access_path", StringComparison.Ordinal))),
                CultureInfo.InvariantCulture) ?? "unknown";
            long anchorRows = Convert.ToInt64(
                FindGraphExplainValue(graphExplain, static key => key == "estimated_anchor_rows") ?? 0L,
                CultureInfo.InvariantCulture);
            long expansionRows = Convert.ToInt64(
                FindGraphExplainValue(graphExplain, static key => key == "estimated_expansions") ?? 0L,
                CultureInfo.InvariantCulture);
            bool fallback = graphExplain.Rows.Any(static row =>
                row.Count > 1 && string.Equals(
                    Convert.ToString(row[1], CultureInfo.InvariantCulture),
                    "relation_scan_fallback",
                    StringComparison.Ordinal));
            return new ComposedSourceExplain(
                alias,
                $"graph_table:{graphKind};anchor={anchorPath};edge={edgePath}",
                FindGraphIndexes(graphExplain),
                expansionRows,
                $"anchor<={anchorRows};expansions<={expansionRows}",
                fallback ? "relation_scan_fallback" : null);
        }

        SqlExplainExecutionResult nested = ExplainSelect(databaseName: null, tsdb, subquery);
        return new ComposedSourceExplain(
            alias,
            nested.AccessPath ?? nested.StatementType,
            nested.IndexName,
            nested.EstimatedScannedRows,
            nested.CandidateContract,
            nested.FallbackReason);
    }

    private static ComposedSourceExplain ExplainComposedRelation(
        Tsdb tsdb,
        string relationName,
        string alias)
    {
        TableSchema? table = tsdb.Tables.Catalog.TryGet(relationName);
        if (table is not null)
        {
            TableAccessCostEstimate tablePlan = ExplainTableAccess(
                tsdb.Tables.Open(table.Name),
                table,
                where: null);
            return new ComposedSourceExplain(
                alias,
                tablePlan.AccessPath,
                tablePlan.IndexName,
                tablePlan.EstimatedRows,
                $"rows<={tablePlan.EstimatedRows}",
                tablePlan.FallbackReason);
        }

        MaterializedViewDefinition? materialized = tsdb.MaterializedViews.Catalog.TryGet(relationName);
        if (materialized is not null)
        {
            return new ComposedSourceExplain(
                alias,
                materialized.ActiveGeneration == 0
                    ? "materialized_view_uninitialized"
                    : "materialized_view_snapshot",
                null,
                materialized.RowCount,
                $"rows<={materialized.RowCount}",
                null);
        }

        throw new InvalidOperationException($"table/materialized view '{relationName}' 不存在。");
    }

    private static object? FindGraphExplainValue(
        SelectExecutionResult explain,
        Func<string, bool> matches)
    {
        foreach (IReadOnlyList<object?> row in explain.Rows)
        {
            if (row.Count > 1 && row[0] is string key && matches(key))
                return row[1];
        }
        return null;
    }

    private static string? FindGraphIndexes(SelectExecutionResult explain)
        => JoinNonEmpty(explain.Rows
            .Where(static row => row.Count > 1
                && row[0] is string key
                && key.EndsWith(".index", StringComparison.Ordinal))
            .Select(static row => Convert.ToString(row[1], CultureInfo.InvariantCulture)));

    private static string? JoinNonEmpty(IEnumerable<string?> values)
    {
        string[] materialized = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToArray();
        return materialized.Length == 0 ? null : string.Join(";", materialized);
    }

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    /// <summary>
    /// 识别无 FROM 的独立 <c>SELECT EXISTS (...)</c>，避免把空 measurement 误交给普通查询解释器。
    /// </summary>
    private static bool TryGetStandaloneExists(SelectStatement statement, out SelectStatement subquery)
    {
        if (string.IsNullOrEmpty(statement.Measurement)
            && statement.FromSubquery is null
            && statement.TableValuedFunction is null
            && statement.JoinClauses.Count == 0
            && statement.UnionStatements.Count == 0
            && statement.GroupBy.Count == 0
            && statement.Having is null
            && statement.Where is null
            && !statement.Distinct
            && statement.OrderByList.Count == 0
            && statement.Pagination is null
            && statement.Projections.Count == 1
            && statement.Projections[0].Expression is ExistsExpression exists)
        {
            subquery = exists.Select;
            return true;
        }

        subquery = null!;
        return false;
    }

    /// <summary>
    /// 使用关系执行器的共享 EXISTS 计划生成 EXPLAIN，不读取候选业务行。
    /// </summary>
    private static SqlExplainExecutionResult ExplainStandaloneExists(
        string? databaseName,
        Tsdb tsdb,
        SelectStatement subquery)
    {
        var plan = RelationalSelectExecutor.ExplainExists(tsdb, subquery);
        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "select_exists",
            Measurement: plan.Measurement,
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: plan.EstimatedCandidateRows,
            EstimatedMemTableRows: plan.EstimatedCandidateRows,
            EstimatedSegmentRows: 0,
            HasTimeFilter: subquery.Where is not null,
            TagFilterCount: 0,
            AccessPath: plan.AccessPath,
            IndexName: plan.IndexName,
            ScanFilter: DescribeScanFilter(subquery.Where))
        {
            EarlyExit = plan.EarlyExit,
            HasResidualPredicate = plan.HasResidualPredicate,
            FallbackReason = plan.FallbackReason,
            EstimatedOutputRows = plan.EstimatedCandidateRows,
            EstimatedRowWidth = plan.EstimatedRowWidth,
            EstimatedLogicalReads = plan.EstimatedLogicalReads,
            EstimatedCost = plan.EstimatedCost,
            EstimateSource = plan.EstimateSource,
            StatisticsSequence = plan.StatisticsSequence,
            StatisticsFreshnessMilliseconds = plan.StatisticsFreshnessMilliseconds,
            CandidatePlans = plan.CandidatePlans,
            PlanNode = plan.AccessPath,
        };
    }

    private static string? DescribeScanFilter(SqlExpression? expression)
        => ContainsRegex(expression)
            ? $"regex_residual(timeout_ms={(long)RegexPatternMatcher.MatchTimeout.TotalMilliseconds},pattern_chars<={RegexPatternMatcher.MaxPatternLength},input_chars<={RegexPatternMatcher.MaxInputLength})"
            : null;

    private static bool ContainsRegex(SqlExpression? expression)
    {
        return expression switch
        {
            null => false,
            BinaryExpression { Operator: SqlBinaryOperator.Regex or SqlBinaryOperator.NotRegex } => true,
            BinaryExpression binary => ContainsRegex(binary.Left) || ContainsRegex(binary.Right),
            UnaryExpression unary => ContainsRegex(unary.Operand),
            IsNullExpression isNull => ContainsRegex(isNull.Operand),
            InExpression inExpression => ContainsRegex(inExpression.Value)
                || inExpression.Values.Any(ContainsRegex),
            CaseExpression caseExpression => caseExpression.WhenClauses.Any(static when =>
                    ContainsRegex(when.Condition) || ContainsRegex(when.Result))
                || ContainsRegex(caseExpression.Else),
            FunctionCallExpression function => string.Equals(
                    function.Name, "regexp_like", StringComparison.OrdinalIgnoreCase)
                || function.Arguments.Any(ContainsRegex),
            NamedArgumentExpression named => ContainsRegex(named.Value),
            _ => false,
        };
    }

    private static TableAccessCostEstimate ExplainTableAccess(
        TableStore store,
        TableSchema schema,
        SqlExpression? where,
        SelectStatement? statement = null)
    {
        TableAccessCostEstimate estimate = TableCostPlanner.Estimate(
            store,
            schema,
            where,
            allowAutomaticRefresh: false);
        if (string.Equals(estimate.EstimateSource, "transaction_overlay", StringComparison.Ordinal))
            return estimate;

        if (TableSqlExecutor.CanUsePrimaryKeyLookup(schema, where))
        {
            return new TableAccessCostEstimate(
                "primary_key", "primary", 1, 1, 0, 1,
                "catalog", null, null, "primary_key rows<=1", null, null);
        }

        if (TableSqlExecutor.TryChooseInAccessPlan(schema, where, out var inPlan))
        {
            long rows = inPlan.Values.Count;
            return new TableAccessCostEstimate(
                inPlan.UsesPrimaryKey ? "primary_key_in" : "secondary_index_in",
                inPlan.UsesPrimaryKey ? "primary" : inPlan.Index!.Name,
                rows, rows, 0, rows, "catalog", null, null,
                $"{(inPlan.UsesPrimaryKey ? "primary_key_in" : "secondary_index_in")} rows<={rows}",
                null, null);
        }

        if ((estimate.IndexPlan is not null || where is null)
            && statement is not null
            && TableSqlExecutor.TryChooseOrderedRangeAccessPlan(
                schema,
                statement,
                out var orderedPlan,
                out int orderedCandidateLimit,
                out _))
        {
            TableStatisticsState state = store.GetStatisticsState();
            long rows = Math.Min(orderedCandidateLimit, store.RowCount);
            return new TableAccessCostEstimate(
                "secondary_index_range",
                orderedPlan.Index.Name,
                rows,
                rows,
                state.Statistics?.AverageRowWidth ?? 0,
                rows,
                state.EstimateSource,
                state.Statistics?.SourceSequence,
                state.FreshnessMilliseconds,
                $"secondary_index_range:{orderedPlan.Index.Name} rows<={rows};ordered_window<={orderedCandidateLimit}",
                null,
                orderedPlan);
        }

        if (estimate.IndexPlan is not null)
            return estimate;

        if (TableSqlExecutor.TryChooseIndexUnionPlan(
            schema,
            where,
            out var unionPlan,
            out string? unionFallback))
        {
            TableStatisticsState state = store.GetStatisticsState();
            long estimatedRows = 0;
            foreach (TableIndexUnionBranch branch in unionPlan.Branches)
            {
                long branchRows = branch.AccessPlan switch
                {
                    { UsesPrimaryKey: true } => 1,
                    { InPlan: { } branchIn } => branchIn.Values.Count,
                    { IndexPlan: { Index.IsUnique: true, IsFullEquality: true } } => 1,
                    { IndexPlan: { } branchIndex } when state.Statistics is not null && !state.IsStale
                        => TableCostPlanner.EstimateIndexRows(
                            store.RowCount,
                            schema,
                            branchIndex,
                            state.Statistics),
                    _ => store.RowCount,
                };
                estimatedRows = Math.Min(store.RowCount, SaturatingAdd(estimatedRows, branchRows));
            }

            return new TableAccessCostEstimate(
                "index_union",
                null,
                estimatedRows,
                estimatedRows,
                state.Statistics?.AverageRowWidth ?? 0,
                estimatedRows,
                state.EstimateSource,
                state.Statistics?.SourceSequence,
                state.FreshnessMilliseconds,
                $"index_union branches={unionPlan.Branches.Count} rows<={estimatedRows};table_scan rows<={store.RowCount}",
                unionFallback,
                null);
        }

        return estimate;
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
