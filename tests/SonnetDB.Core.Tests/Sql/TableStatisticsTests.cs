using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>M41 #375/#376/#377 统计、成本规划和执行证据回归。</summary>
public sealed class TableStatisticsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-table-statistics-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AnalyzeTable_PersistsStatisticsAcrossRestart_WithoutRawValues()
    {
        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            SqlExecutor.Execute(db, "CREATE TABLE events (id INT, tenant STRING, value INT, PRIMARY KEY (id))");
            SqlExecutor.Execute(db, "CREATE INDEX ix_events_tenant ON events (tenant)");
            SqlExecutor.Execute(db, "INSERT INTO events (id, tenant, value) VALUES "
                + "(1, 'north', 10), (2, 'north', 20), (3, 'south', 30), (4, NULL, 40)");

            var result = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(db, "ANALYZE TABLE events"));
            Assert.Equal(4L, result.Rows[0][1]);
            Assert.Equal(4, result.Rows[0][4]);

            TableStatistics statistics = Assert.IsType<TableStatistics>(db.Tables.Open("events").Statistics);
            Assert.Equal(4, statistics.RowCount);
            Assert.Equal(0.25, statistics.TryGetColumn("tenant")!.NullFraction, precision: 6);
            Assert.NotEmpty(statistics.TryGetColumn("tenant")!.MostCommonValues);
            Assert.NotEmpty(statistics.TryGetColumn("value")!.Histogram);
            Assert.Equal(4, statistics.TryGetIndex("ix_events_tenant")!.RowCount);
            Assert.DoesNotContain(
                statistics.TryGetColumn("tenant")!.MostCommonValues,
                value => value.ToString()!.Contains("north", StringComparison.OrdinalIgnoreCase));
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        TableStore store = reopened.Tables.Open("events");
        Assert.NotNull(store.Statistics);
        Assert.False(store.AreStatisticsStale);
    }

    [Fact]
    public void Explain_ReadsStatisticsMetadata_WithoutScanningBusinessRows()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE audits (id INT, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_audits_status ON audits (status)");
        InsertSelectiveRows(db, "audits");
        TableStore store = db.Tables.Open("audits");
        _ = store.RefreshStatistics();
        long scansBefore = store.FullScanCount;

        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "EXPLAIN SELECT id FROM audits WHERE status = 'ready'"));
        var values = result.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);

        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Equal("refreshed", values["estimate_source"]);
        Assert.Equal("secondary_index", values["access_path"]);
        Assert.NotNull(values["estimated_cost"]);
        Assert.NotNull(values["candidate_plans"]);
    }

    [Fact]
    public void ExplainAnalyze_Select_ReportsEstimatedAndActualEvidence()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE readings (id INT, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_readings_status ON readings (status)");
        InsertSelectiveRows(db, "readings");
        _ = SqlExecutor.Execute(db, "ANALYZE TABLE readings");

        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "EXPLAIN ANALYZE SELECT id FROM readings WHERE status = 'ready'"));
        var values = result.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);

        Assert.Equal("refreshed", values["estimate_source"]);
        Assert.Equal(1L, Convert.ToInt64(values["actual_rows"]));
        Assert.Equal(1L, Convert.ToInt64(values["actual_candidate_rows"]));
        Assert.Equal(1L, Convert.ToInt64(values["actual_examined_rows"]));
        Assert.Equal(1L, Convert.ToInt64(values["actual_loops"]));
        Assert.NotNull(values["actual_execution_ms"]);
        Assert.Equal(0L, Convert.ToInt64(values["actual_spill_count"]));
        Assert.Equal("secondary_index", values["actual_access_path"]);
    }

    [Fact]
    public void CostPlanner_FreshStatistics_SelectsSelectiveIndexAndRejectsNonSelectiveIndex()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE cost_events (id INT, tenant STRING, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_cost_tenant ON cost_events (tenant)");
        SqlExecutor.Execute(db, "CREATE INDEX ix_cost_status ON cost_events (status)");
        TableStore store = db.Tables.Open("cost_events");
        store.InsertMany(Enumerable.Range(1, 2_000)
            .Select(id => (IReadOnlyList<object?>)new object?[]
            {
                (long)id,
                "common",
                id == 1 ? "rare" : "common",
            })
            .ToArray());
        _ = store.RefreshStatistics();

        var selective = Explain(db, "SELECT id FROM cost_events WHERE tenant = 'common' AND status = 'rare'");
        Assert.Equal("ix_cost_status", selective["index_name"]);
        Assert.Equal("secondary_index", selective["access_path"]);
        Assert.Contains("ix_cost_tenant", (string)selective["candidate_plans"]!);
        Assert.Contains("ix_cost_status", (string)selective["candidate_plans"]!);

        var nonSelective = Explain(db, "SELECT id FROM cost_events WHERE status = 'common'");
        Assert.Equal("table_scan", nonSelective["access_path"]);
        Assert.Equal("cost_model_table_scan", nonSelective["fallback_reason"]);
    }

    [Fact]
    public void ExplainExists_FreshStatisticsMatchesRuntimeCostPlan_WithoutScanningBusinessRows()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE exists_cost (id INT, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_exists_cost_status ON exists_cost (status)");
        TableStore store = db.Tables.Open("exists_cost");
        InsertSkewedRows(store);
        _ = store.RefreshStatistics();
        long scansBefore = store.FullScanCount;

        var explain = Explain(db, "SELECT EXISTS (SELECT 1 FROM exists_cost WHERE status = 'common')");
        Assert.Equal("table_scan", explain["access_path"]);
        Assert.Equal("cost_model_table_scan", explain["fallback_reason"]);
        Assert.Equal("refreshed", explain["estimate_source"]);
        Assert.NotNull(explain["estimated_cost"]);
        Assert.Equal(scansBefore, store.FullScanCount);

        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT EXISTS (SELECT 1 FROM exists_cost WHERE status = 'common')"));
        var metrics = new RelationalSelectExecutionMetrics();
        _ = RelationalSelectExecutor.Execute(db, statement, metrics);
        Assert.Equal(explain["access_path"], metrics.LastExistsAccessPath);
        Assert.Equal(explain["fallback_reason"], metrics.LastExistsFallbackReason);
    }

    [Fact]
    public void ExplainJoin_FreshStatisticsReportsTableCostEvidence_WithoutScanningBusinessRows()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE MEASUREMENT join_cpu (host TAG, usage FIELD FLOAT)");
        SqlExecutor.Execute(db, "CREATE TABLE join_hosts (id INT, host STRING, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_join_hosts_status ON join_hosts (status)");
        TableStore store = db.Tables.Open("join_hosts");
        InsertSkewedRows(store, includeHost: true);
        _ = store.RefreshStatistics();
        long scansBefore = store.FullScanCount;

        var explain = Explain(db, """
            SELECT c.time, h.host
            FROM join_cpu c
            JOIN join_hosts h ON c.host = h.host
            WHERE h.status = 'rare'
            """);

        Assert.Contains("table:secondary_index", (string)explain["access_path"]!);
        Assert.Equal("join_hosts.ix_join_hosts_status", explain["index_name"]);
        Assert.Equal("refreshed", explain["estimate_source"]);
        Assert.NotNull(explain["estimated_cost"]);
        Assert.Contains("ix_join_hosts_status", (string)explain["candidate_plans"]!);
        Assert.Equal(scansBefore, store.FullScanCount);
    }

    [Fact]
    public void Parse_AnalyzeTable_AcceptsOptionalTableKeyword()
    {
        Assert.Equal("events", Assert.IsType<AnalyzeTableStatement>(SqlParser.Parse("ANALYZE TABLE events")).TableName);
        Assert.Equal("events", Assert.IsType<AnalyzeTableStatement>(SqlParser.Parse("ANALYZE events")).TableName);
    }

    [Fact]
    public void AnalyzeTable_Int64Extremes_PreservesHistogramBounds()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE extremes (id INT, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(
            db,
            databaseName: null,
            "INSERT INTO extremes (id, value) VALUES (1, @minimum), (2, @maximum)",
            new SqlParameters().AddNamed("minimum", long.MinValue).AddNamed("maximum", long.MaxValue),
            controlPlane: null);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "ANALYZE extremes"));
        Assert.Equal(2L, result.Rows[0][1]);
        var histogram = db.Tables.Open("extremes").Statistics!.TryGetColumn("value")!.Histogram;
        Assert.Contains(histogram, bucket => bucket.Int64UpperBound == long.MinValue);
        Assert.Contains(histogram, bucket => bucket.Int64UpperBound == long.MaxValue);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void InsertSelectiveRows(Tsdb db, string tableName)
    {
        string values = string.Join(", ", Enumerable.Range(1, 100)
            .Select(id => $"({id}, '{(id == 1 ? "ready" : "blocked")}')"));
        SqlExecutor.Execute(db, $"INSERT INTO {tableName} (id, status) VALUES {values}");
    }

    private static void InsertSkewedRows(TableStore store, bool includeHost = false)
    {
        store.InsertMany(Enumerable.Range(1, 2_000)
            .Select(id => (IReadOnlyList<object?>)(includeHost
                ? new object?[] { (long)id, $"host-{id}", id == 1 ? "rare" : "common" }
                : new object?[] { (long)id, id == 1 ? "rare" : "common" }))
            .ToArray());
    }

    private static IReadOnlyDictionary<string, object?> Explain(Tsdb db, string sql)
    {
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "EXPLAIN " + sql));
        return result.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);
    }
}
