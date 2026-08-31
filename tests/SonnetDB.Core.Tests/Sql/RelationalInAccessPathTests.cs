using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 M41 #369 标量 IN/semijoin 的单快照 MultiGet 与安全回退合同。</summary>
public sealed class RelationalInAccessPathTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sndb-relational-in-{Guid.NewGuid():N}");

    /// <summary>非相关主键 IN 子查询应去重后执行一次 MultiGet，且不扫描外表。</summary>
    [Fact]
    public void InSubquery_PrimaryKey_UsesDeduplicatedSingleSnapshotMultiGet()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE in_targets (id INT, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE in_keys (seq INT, target_id INT NULL, PRIMARY KEY (seq))");
        SqlExecutor.Execute(db, "INSERT INTO in_targets (id, status) VALUES (1, 'ready'), (2, 'blocked'), (3, 'ready'), (4, 'ready')");
        SqlExecutor.Execute(db, "INSERT INTO in_keys (seq, target_id) VALUES (1, 3), (2, 1), (3, 3), (4, NULL), (5, 99)");
        var store = db.Tables.Open("in_targets");
        long scansBefore = store.FullScanCount;
        long batchesBefore = store.MultiGetCount;
        long lookupsBefore = store.PrimaryKeyLookupCount;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id FROM in_targets
            WHERE id IN (SELECT target_id FROM in_keys) AND status = 'ready'
            ORDER BY id
            """));

        Assert.Equal([1L, 3L], result.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Equal(batchesBefore + 1, store.MultiGetCount);
        Assert.Equal(lookupsBefore + 3, store.PrimaryKeyLookupCount);

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT id FROM in_targets
            WHERE id IN (SELECT target_id FROM in_keys) AND status = 'ready'
            """));
        var explainValues = explain.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);
        Assert.Equal("primary_key_in", explainValues["access_path"]);
        Assert.Equal("primary", explainValues["index_name"]);
    }

    /// <summary>非相关二级索引 IN 子查询应批量探测索引并保留外层残余过滤。</summary>
    [Fact]
    public void InSubquery_SecondaryIndex_UsesMultiGetAndResidualPredicate()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE in_events (id INT, tenant STRING, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_in_events_tenant ON in_events (tenant)");
        SqlExecutor.Execute(db, "CREATE TABLE in_tenants (id INT, tenant STRING NULL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO in_events (id, tenant, status) VALUES (1, 'north', 'ready'), (2, 'north', 'blocked'), (3, 'south', 'ready')");
        SqlExecutor.Execute(db, "INSERT INTO in_tenants (id, tenant) VALUES (1, 'north'), (2, 'north'), (3, NULL)");
        var store = db.Tables.Open("in_events");
        long scansBefore = store.FullScanCount;
        long batchesBefore = store.MultiGetCount;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id FROM in_events
            WHERE tenant IN (SELECT tenant FROM in_tenants) AND status = 'ready'
            ORDER BY id
            """));

        Assert.Equal([1L], result.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Equal(batchesBefore + 1, store.MultiGetCount);

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT id FROM in_events
            WHERE tenant IN (SELECT tenant FROM in_tenants) AND status = 'ready'
            """));
        var explainValues = explain.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);
        Assert.Equal("secondary_index_in", explainValues["access_path"]);
        Assert.Equal("ix_in_events_tenant", explainValues["index_name"]);
    }

    /// <summary>单列非主键 IN 应跳过等值字典；存在可用复合前缀时才收集并选择更完整索引。</summary>
    [Fact]
    public void TryChooseInAccessPlan_SingleColumnIndex_SkipsEqualityCollection()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE in_fast_path (id INT, tenant STRING, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_in_fast_path_tenant ON in_fast_path (tenant)");
        var store = db.Tables.Open("in_fast_path");
        var statement = Assert.IsType<SonnetDB.Sql.Ast.SelectStatement>(SqlParser.Parse(
            "SELECT id FROM in_fast_path WHERE tenant IN ('north', 'south') AND status = 'ready'"));
        int equalityCollections = 0;

        bool selected = TableSqlExecutor.TryChooseInAccessPlan(
            store.Schema,
            statement.Where,
            () => equalityCollections++,
            out var singleColumnPlan);

        Assert.True(selected);
        Assert.Equal(0, equalityCollections);
        Assert.Equal("ix_in_fast_path_tenant", singleColumnPlan.Index!.Name);
        Assert.Empty(singleColumnPlan.EqualityPrefixValues);
        Assert.Equal(2, singleColumnPlan.LookupKeys.Count);

        TableSchema compositeSchema = store.Schema.WithIndex(new TableIndexDefinition(
            "ix_in_fast_path_status_tenant",
            ["status", "tenant"],
            IsUnique: false));
        selected = TableSqlExecutor.TryChooseInAccessPlan(
            compositeSchema,
            statement.Where,
            () => equalityCollections++,
            out var compositePlan);

        Assert.True(selected);
        Assert.Equal(1, equalityCollections);
        Assert.Equal("ix_in_fast_path_status_tenant", compositePlan.Index!.Name);
        Assert.Equal(["ready"], compositePlan.EqualityPrefixValues);
        Assert.Equal(2, compositePlan.LookupKeys.Count);
    }

    /// <summary>复合唯一索引的等值首列加次列 IN 应在单快照内批量点读，并保留额外过滤条件。</summary>
    [Fact]
    public void CompositeIndex_EqualityPrefixAndIn_UsesMultiGetWithResidualPredicate()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE in_evidence (id INT, capture_role STRING, capture_id STRING, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_in_evidence_role_capture ON in_evidence (capture_role, capture_id)");
        SqlExecutor.Execute(db, "INSERT INTO in_evidence (id, capture_role, capture_id, status) VALUES (1, 'front', 'c-1', 'ready'), (2, 'front', 'c-2', 'blocked'), (3, 'rear', 'c-2', 'ready'), (4, 'front', 'c-4', 'ready')");
        var store = db.Tables.Open("in_evidence");
        long scansBefore = store.FullScanCount;
        long batchesBefore = store.MultiGetCount;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id FROM in_evidence
            WHERE capture_role = 'front'
                AND capture_id IN ('c-4', 'c-2', 'missing')
                AND status = 'ready'
            ORDER BY id
            """));

        Assert.Equal([4L], result.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Equal(batchesBefore + 1, store.MultiGetCount);

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT id FROM in_evidence
            WHERE capture_role = 'front'
                AND capture_id IN ('c-4', 'c-2', 'missing')
                AND status = 'ready'
            """));
        var explainValues = explain.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);
        Assert.Equal("secondary_index_prefix_in", explainValues["access_path"]);
        Assert.Equal("ux_in_evidence_role_capture", explainValues["index_name"]);
    }

    /// <summary>缺少复合索引首列等值时，次列 IN 必须回退扫描，不能漏掉其他前缀下的命中行。</summary>
    [Fact]
    public void CompositeIndex_InWithoutLeadingPrefix_FallsBackToScan()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE in_prefix_fallback (id INT, capture_role STRING, capture_id STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_in_prefix_fallback ON in_prefix_fallback (capture_role, capture_id)");
        SqlExecutor.Execute(db, "INSERT INTO in_prefix_fallback (id, capture_role, capture_id) VALUES (1, 'front', 'same'), (2, 'rear', 'same'), (3, 'front', 'other')");
        var store = db.Tables.Open("in_prefix_fallback");
        long scansBefore = store.FullScanCount;
        long batchesBefore = store.MultiGetCount;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM in_prefix_fallback WHERE capture_id IN ('same') ORDER BY id"));

        Assert.Equal([1L, 2L], result.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.Equal(scansBefore + 1, store.FullScanCount);
        Assert.Equal(batchesBefore, store.MultiGetCount);
    }

    /// <summary>复合索引前缀与 IN 完全覆盖 EXISTS 时，应在首个有效索引键命中后停止探测。</summary>
    [Fact]
    public void Exists_CompositeIndexPrefixIn_StopsAfterFirstHit()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE in_composite_exists (id INT, capture_role STRING, capture_id STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_in_composite_exists ON in_composite_exists (capture_role, capture_id)");
        SqlExecutor.Execute(db, "INSERT INTO in_composite_exists (id, capture_role, capture_id) VALUES (1, 'front', 'c-1'), (2, 'front', 'c-2'), (3, 'rear', 'c-2')");
        var store = db.Tables.Open("in_composite_exists");
        long scansBefore = store.FullScanCount;
        long batchesBefore = store.MultiGetCount;
        var metrics = new RelationalSelectExecutionMetrics();
        var statement = Assert.IsType<SonnetDB.Sql.Ast.SelectStatement>(SqlParser.Parse("""
            SELECT EXISTS (
                SELECT 1 FROM in_composite_exists
                WHERE capture_role = 'front'
                    AND capture_id IN ('missing', 'c-2', 'c-1'))
            """));

        var result = RelationalSelectExecutor.Execute(db, statement, metrics);

        Assert.True(Assert.IsType<bool>(Assert.Single(Assert.Single(result.Rows))));
        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Equal(batchesBefore + 1, store.MultiGetCount);
        Assert.Equal(1, metrics.ExistsRowsExamined);
        Assert.Equal(1, metrics.ExistsEarlyExitCount);
        Assert.Equal("secondary_index_prefix_in", metrics.LastExistsAccessPath);
        Assert.Equal("ux_in_composite_exists", metrics.LastExistsIndexName);
    }

    /// <summary>相关 IN 子查询不得提前物化，继续按外层行执行原关系语义。</summary>
    [Fact]
    public void InSubquery_Correlated_FallsBackWithoutMultiGetRewrite()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE in_outer (id INT, expected INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE in_inner (id INT, outer_id INT, candidate INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO in_outer (id, expected) VALUES (1, 10), (2, 20)");
        SqlExecutor.Execute(db, "INSERT INTO in_inner (id, outer_id, candidate) VALUES (1, 1, 10), (2, 2, 99)");
        var store = db.Tables.Open("in_outer");
        long batchesBefore = store.MultiGetCount;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT o.id FROM in_outer o
            WHERE o.expected IN (
                SELECT i.candidate FROM in_inner i WHERE i.outer_id = o.id)
            ORDER BY o.id
            """));

        Assert.Equal([1L], result.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.Equal(batchesBefore, store.MultiGetCount);
    }

    /// <summary>谓词完全由主键 IN 覆盖的 EXISTS 应在首个命中键后停止批量探测。</summary>
    [Fact]
    public void Exists_PrimaryKeyIn_StopsMultiGetAfterFirstHit()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE in_exists (id INT, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO in_exists (id, status) VALUES (1, 'ready'), (2, 'ready'), (3, 'ready')");
        var store = db.Tables.Open("in_exists");
        long batchesBefore = store.MultiGetCount;
        long lookupsBefore = store.PrimaryKeyLookupCount;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT EXISTS (SELECT 1 FROM in_exists WHERE id IN (3, 2, 1))"));

        Assert.True(Assert.IsType<bool>(Assert.Single(Assert.Single(result.Rows))));
        Assert.Equal(batchesBefore + 1, store.MultiGetCount);
        Assert.Equal(lookupsBefore + 1, store.PrimaryKeyLookupCount);
    }

    /// <summary>目标表有事务写集时 semijoin 可物化内表键，但外表必须扫描叠加 overlay。</summary>
    [Fact]
    public void InSubquery_TransactionOverlay_PreservesReadYourWritesWithoutMultiGet()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE in_tx_targets (id INT, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE in_tx_keys (seq INT, target_id INT, PRIMARY KEY (seq))");
        SqlExecutor.Execute(db, "INSERT INTO in_tx_keys (seq, target_id) VALUES (1, 5)");
        var store = db.Tables.Open("in_tx_targets");
        long batchesBefore = store.MultiGetCount;

        var results = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO in_tx_targets (id, status) VALUES (5, 'ready');
            SELECT id FROM in_tx_targets WHERE id IN (SELECT target_id FROM in_tx_keys);
            ROLLBACK;
            """);

        var selected = Assert.IsType<SelectExecutionResult>(results[2]);
        Assert.Equal(5L, Assert.Single(Assert.Single(selected.Rows)));
        Assert.Equal(batchesBefore, store.MultiGetCount);
    }

    /// <summary>删除测试使用的临时数据库目录。</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>创建指向当前测试临时目录的数据库选项。</summary>
    private TsdbOptions Options() => new() { RootDirectory = _root };
}
