using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlExplainTests : IDisposable
{
    private readonly string _root;

    public SqlExplainTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-explain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// 新增 EXPLAIN 字段时必须保留原 15 参数构造器和解构签名，避免破坏既有调用方。
    /// </summary>
    [Fact]
    public void SqlExplainExecutionResult_ExtensionPreservesOriginalPositionalContract()
    {
        var result = new SqlExplainExecutionResult(
            Database: "compat",
            StatementType: "select",
            Measurement: "events",
            MatchedSeriesCount: 1,
            EstimatedSegmentCount: 2,
            EstimatedBlockCount: 3,
            EstimatedScannedRows: 4,
            EstimatedMemTableRows: 5,
            EstimatedSegmentRows: 6,
            HasTimeFilter: true,
            TagFilterCount: 7,
            AccessPath: "table_scan",
            IndexName: null,
            DocumentPlan: null,
            ScanFilter: null)
        {
            EarlyExit = true,
        };

        var (
            database,
            statementType,
            measurement,
            matchedSeriesCount,
            estimatedSegmentCount,
            estimatedBlockCount,
            estimatedScannedRows,
            estimatedMemTableRows,
            estimatedSegmentRows,
            hasTimeFilter,
            tagFilterCount,
            accessPath,
            indexName,
            documentPlan,
            scanFilter) = result;

        Assert.Equal("compat", database);
        Assert.Equal("select", statementType);
        Assert.Equal("events", measurement);
        Assert.Equal(1, matchedSeriesCount);
        Assert.Equal(2, estimatedSegmentCount);
        Assert.Equal(3, estimatedBlockCount);
        Assert.Equal(4, estimatedScannedRows);
        Assert.Equal(5, estimatedMemTableRows);
        Assert.Equal(6, estimatedSegmentRows);
        Assert.True(hasTimeFilter);
        Assert.Equal(7, tagFilterCount);
        Assert.Equal("table_scan", accessPath);
        Assert.Null(indexName);
        Assert.Null(documentPlan);
        Assert.Null(scanFilter);
        Assert.True(result.EarlyExit);
        Assert.Contains(
            typeof(SqlExplainExecutionResult).GetConstructors(),
            static constructor => constructor.GetParameters().Length == 15);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private TsdbOptions Options() => new() { RootDirectory = _root };

    [Fact]
    public void Parse_ExplainSelect_WrapsInnerSelect()
    {
        var explain = Assert.IsType<ExplainStatement>(
            SqlParser.Parse("EXPLAIN SELECT usage FROM cpu WHERE host = 'h1'"));

        var select = Assert.IsType<SelectStatement>(explain.Statement);
        Assert.Equal("cpu", select.Measurement);
    }

    [Fact]
    public void Parse_ExplainShowTables_WrapsShowTables()
    {
        var explain = Assert.IsType<ExplainStatement>(
            SqlParser.Parse("EXPLAIN SHOW TABLES"));

        Assert.IsType<ShowTablesStatement>(explain.Statement);
    }

    [Fact]
    public void Parse_ExplainWriteOrControlPlaneStatement_Throws()
    {
        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("EXPLAIN INSERT INTO cpu (host, usage) VALUES ('h1', 1)"));

        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("EXPLAIN SHOW DATABASES"));
    }

    [Fact]
    public void Execute_ExplainSelect_ReturnsKeyValuePlanRows()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, usage) VALUES (1000, 'h1', 0.5), (2000, 'h1', 0.7), (3000, 'h2', 0.9)");
        db.FlushNow();

        var statement = SqlParser.Parse(
            "EXPLAIN SELECT usage FROM cpu WHERE host = 'h1' AND time >= 1000 AND time <= 2000");
        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.ExecuteStatement(db, "metrics", statement));

        Assert.Equal(new[] { "key", "value" }, result.Columns);

        var values = result.Rows.ToDictionary(
            row => (string)row[0]!,
            row => row[1],
            StringComparer.Ordinal);

        Assert.Equal("metrics", values["database"]);
        Assert.Equal("select", values["statement_type"]);
        Assert.Equal("cpu", values["measurement"]);
        Assert.Equal(1, Convert.ToInt32(values["matched_series_count"]));
        Assert.Equal(1, Convert.ToInt32(values["estimated_segment_count"]));
        Assert.Equal(1, Convert.ToInt32(values["estimated_block_count"]));
        Assert.Equal(2L, Convert.ToInt64(values["estimated_scanned_rows"]));
        Assert.Equal(0L, Convert.ToInt64(values["estimated_memtable_rows"]));
        Assert.Equal(2L, Convert.ToInt64(values["estimated_segment_rows"]));
        Assert.True((bool)values["has_time_filter"]!);
        Assert.Equal(1, Convert.ToInt32(values["tag_filter_count"]));
    }

    [Fact]
    public void Execute_ExplainJoinSelect_ShowsUnifiedPushdownPlan()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        SqlExecutor.Execute(db, "CREATE TABLE hosts (id STRING, site STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX idx_hosts_site ON hosts (site)");
        SqlExecutor.Execute(db, "INSERT INTO hosts (id, site) VALUES ('h1', 'north'), ('h2', 'south')");
        SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, usage) VALUES (1000, 'h1', 0.5), (2000, 'h2', 0.7)");

        var statement = SqlParser.Parse(
            "EXPLAIN SELECT c.time, h.site FROM cpu c JOIN hosts h ON c.host = h.id WHERE h.site = 'north' AND c.host = 'h1' AND c.time >= 1000");

        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.ExecuteStatement(db, "metrics", statement));
        var values = result.Rows.ToDictionary(
            row => (string)row[0]!,
            row => row[1],
            StringComparer.Ordinal);

        Assert.Equal("select_join", values["statement_type"]);
        Assert.True((bool)values["has_time_filter"]!);
        Assert.Equal(1, Convert.ToInt32(values["tag_filter_count"]));
        Assert.Contains("measurement:tag_index", (string)values["access_path"]!);
        Assert.Contains("table:secondary_index", (string)values["access_path"]!);
        Assert.Equal("hosts.idx_hosts_site", values["index_name"]);
    }

    /// <summary>
    /// 验证 JOIN EXPLAIN 与普通关系查询复用同一二级索引范围计划。
    /// </summary>
    [Fact]
    public void Execute_ExplainJoinSelect_ReportsSecondaryIndexRange()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu_range (host TAG, usage FIELD FLOAT)");
        SqlExecutor.Execute(db, "CREATE TABLE hosts_range (id STRING, rank INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX idx_hosts_range_rank ON hosts_range (rank)");
        SqlExecutor.Execute(db,
            "INSERT INTO hosts_range (id, rank) VALUES ('h1', -1), ('h2', 0), ('h3', 2)");
        SqlExecutor.Execute(db,
            "INSERT INTO cpu_range (time, host, usage) VALUES (1000, 'h1', 0.5), (2000, 'h2', 0.7)");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT c.time, h.rank
            FROM cpu_range c
            JOIN hosts_range h ON c.host = h.id
            WHERE h.rank >= -1 AND h.rank < 2 AND c.host = 'h1'
            """));
        var values = result.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);

        Assert.Contains("table:secondary_index_range", (string)values["access_path"]!);
        Assert.Equal("hosts_range.idx_hosts_range_rank", values["index_name"]);
        // EXPLAIN 只读取目录元数据；没有 ANALYZE 统计时表侧使用 3 行稳定上界，
        // 再加 1 个 measurement series，不再物化索引范围的实际 2 行。
        Assert.Equal(4L, Convert.ToInt64(values["estimated_scanned_rows"]));
    }

    /// <summary>
    /// 验证独立 EXISTS 的 EXPLAIN 与运行时共享索引、残余谓词和早停计划，且解释本身不读取业务行。
    /// </summary>
    [Fact]
    public void Execute_ExplainExists_ReportsActualFastPathWithoutScanningData()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE explain_audits (id INT, request_key STRING, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_explain_audits_key ON explain_audits (request_key)");
        SqlExecutor.Execute(db, """
            INSERT INTO explain_audits (id, request_key, status) VALUES
                (1, 'key-1', 'blocked'), (2, 'key-2', 'ready')
            """);
        var store = db.Tables.Open("explain_audits");
        long scansBefore = store.FullScanCount;
        const string existsSql = """
            SELECT EXISTS (
                SELECT 1 FROM explain_audits a
                WHERE A.REQUEST_KEY = 'key-2' AND A.STATUS = 'ready'
            )
            """;

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "EXPLAIN " + existsSql));
        var values = explain.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);

        Assert.Equal("select_exists", values["statement_type"]);
        Assert.Equal("explain_audits", values["measurement"]);
        Assert.Equal("secondary_index", values["access_path"]);
        Assert.Equal("ux_explain_audits_key", values["index_name"]);
        Assert.Equal(1L, Convert.ToInt64(values["estimated_scanned_rows"]));
        Assert.True((bool)values["early_exit"]!);
        Assert.True((bool)values["has_residual_predicate"]!);
        Assert.Null(values["fallback_reason"]);
        Assert.Equal("statistics_missing", values["estimate_source"]);
        Assert.NotNull(values["estimated_cost"]);
        Assert.Equal(scansBefore, store.FullScanCount);

        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(existsSql));
        var metrics = new RelationalSelectExecutionMetrics();
        var actual = RelationalSelectExecutor.Execute(db, statement, metrics);
        Assert.True(Assert.IsType<bool>(Assert.Single(actual.Rows)[0]));
        Assert.Equal(values["access_path"], metrics.LastExistsAccessPath);
        Assert.Equal(values["index_name"], metrics.LastExistsIndexName);
        Assert.Equal(values["has_residual_predicate"], metrics.LastExistsHasResidualPredicate);
    }

    /// <summary>
    /// 活动事务存在写集时，EXPLAIN 与实际 EXISTS 都必须报告 overlay 所需的全表扫描。
    /// </summary>
    [Fact]
    public void Execute_ExplainExistsWithTransactionOverlay_ReportsRuntimeScanFallback()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE explain_overlay (id INT, request_key STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_explain_overlay_key ON explain_overlay (request_key)");

        var results = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO explain_overlay (id, request_key) VALUES (1, 'buffered');
            EXPLAIN SELECT EXISTS (
                SELECT 1 FROM explain_overlay WHERE request_key = 'buffered'
            );
            SELECT EXISTS (
                SELECT 1 FROM explain_overlay WHERE request_key = 'buffered'
            );
            ROLLBACK;
            """);
        var explain = Assert.IsType<SelectExecutionResult>(results[2]);
        var values = explain.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);
        var exists = Assert.IsType<SelectExecutionResult>(results[3]);

        Assert.Equal("table_scan", values["access_path"]);
        Assert.Null(values["index_name"]);
        Assert.Equal(1L, Convert.ToInt64(values["estimated_scanned_rows"]));
        Assert.True((bool)values["has_residual_predicate"]!);
        Assert.Equal("transaction_overlay_requires_scan", values["fallback_reason"]);
        Assert.True(Assert.IsType<bool>(Assert.Single(exists.Rows)[0]));
    }

    /// <summary>
    /// 聚合 EXISTS 必须解释为完整关系执行器回退，不能宣称会使用单表早停路径。
    /// </summary>
    [Fact]
    public void Execute_ExplainAggregateExists_ReportsRelationalFallback()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE explain_groups (id INT, status STRING, PRIMARY KEY (id))");

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT EXISTS (
                SELECT status FROM explain_groups
                GROUP BY status
                HAVING count(*) > 99
            )
            """));
        var values = explain.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);

        Assert.Equal("relational_fallback", values["access_path"]);
        Assert.Null(values["index_name"]);
        Assert.False((bool)values["early_exit"]!);
        Assert.False((bool)values["has_residual_predicate"]!);
        Assert.Equal("aggregate_or_distinct", values["fallback_reason"]);
    }
}
