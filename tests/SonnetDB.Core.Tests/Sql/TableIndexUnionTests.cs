using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 M41 #370 OR 多索引候选并集、去重、nullable 分支和有界回退。</summary>
public sealed class TableIndexUnionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sndb-index-union-{Guid.NewGuid():N}");

    /// <summary>主键与两个二级索引分支应合并去重，并保留完整 OR/残余谓词复检。</summary>
    [Fact]
    public void Select_IndexableOrBranches_UsesDeduplicatedIndexUnion()
    {
        using var db = CreateDatabase();
        var store = db.Tables.Open("union_events");
        long scansBefore = store.FullScanCount;
        int snapshotsAcquired = 0;
        store.ReadSnapshotAcquiredTestHook = () => snapshotsAcquired++;
        var metrics = new SqlExecutionMetrics();

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: null,
            """
            SELECT id FROM union_events
            WHERE id = 1
               OR external_key = 'key-003'
               OR (tenant = 'south' AND status = 'ready')
            ORDER BY id
            """,
            parameters: null,
            controlPlane: null,
            new SqlExecutionOptions { Metrics = metrics }));
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();

        Assert.Equal([1L, 3L, 4L], result.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Equal(1, snapshotsAcquired);
        Assert.Equal("index_union", snapshot.AccessPath);
        Assert.Null(snapshot.FallbackReason);
        Assert.Equal(3, snapshot.CandidateRows);
        Assert.Equal(3, snapshot.ExaminedRows);

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT id FROM union_events
            WHERE id = 1 OR external_key = 'key-003' OR tenant = 'south'
            """));
        var explainValues = explain.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);
        Assert.Equal("index_union", explainValues["access_path"]);
        Assert.Null(explainValues["fallback_reason"]);
    }

    /// <summary>IS NULL 与同列范围分支应复用 nullable 二级索引并只返回真实匹配行。</summary>
    [Fact]
    public void Select_NullableIsNullOrRange_UsesIndexUnion()
    {
        using var db = CreateDatabase();
        var store = db.Tables.Open("union_events");
        long scansBefore = store.FullScanCount;
        var metrics = new SqlExecutionMetrics();

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: null,
            "SELECT id FROM union_events WHERE occurred_at IS NULL OR occurred_at >= 3000 ORDER BY id",
            parameters: null,
            controlPlane: null,
            new SqlExecutionOptions { Metrics = metrics }));

        Assert.Equal([2L, 3L, 4L], result.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Equal("index_union", metrics.Complete().AccessPath);
    }

    /// <summary>nullable 参数为非 NULL 时消去恒假分支；为 NULL 时显式回退匹配全表。</summary>
    [Fact]
    public void Select_NullableParameterOr_UsesIndexWhenSelectiveAndFallsBackWhenMatchAll()
    {
        using var db = CreateDatabase();
        const string sql = """
            SELECT id FROM union_events
            WHERE (occurred_at >= @from OR @from IS NULL) AND status = 'ready'
            ORDER BY id
            """;
        var selectiveMetrics = new SqlExecutionMetrics();
        var selective = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: null,
            sql,
            new SqlParameters().AddNamed("from", 3000L),
            controlPlane: null,
            new SqlExecutionOptions { Metrics = selectiveMetrics }));
        var fallbackMetrics = new SqlExecutionMetrics();
        var matchAll = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: null,
            sql,
            new SqlParameters().AddNamed("from", null),
            controlPlane: null,
            new SqlExecutionOptions { Metrics = fallbackMetrics }));

        Assert.Equal([3L, 4L], selective.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.Equal("index_union", selectiveMetrics.Complete().AccessPath);
        Assert.Equal([1L, 3L, 4L], matchAll.Rows.Select(static row => (long)row[0]!).ToArray());
        SqlExecutionMetricsSnapshot fallback = fallbackMetrics.Complete();
        Assert.Equal("table_scan", fallback.AccessPath);
        Assert.Equal("index_union_branch_matches_all", fallback.FallbackReason);
    }

    /// <summary>任一 OR 分支不可索引时必须完整回退，不能漏掉该分支命中的行。</summary>
    [Fact]
    public void Select_UnindexedOrBranch_FallsBackWithReason()
    {
        using var db = CreateDatabase();
        var store = db.Tables.Open("union_events");
        long scansBefore = store.FullScanCount;
        var metrics = new SqlExecutionMetrics();

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: null,
            "SELECT id FROM union_events WHERE external_key = 'key-001' OR status = 'blocked' ORDER BY id",
            parameters: null,
            controlPlane: null,
            new SqlExecutionOptions { Metrics = metrics }));

        Assert.Equal([1L, 2L], result.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.Equal(scansBefore + 1, store.FullScanCount);
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();
        Assert.Equal("table_scan", snapshot.AccessPath);
        Assert.Equal("index_union_unindexed_branch", snapshot.FallbackReason);
    }

    /// <summary>顶层 AND 已有可用索引时应保持既有访问路径，不能被更宽的 OR 并集抢占。</summary>
    [Fact]
    public void Select_IndexedConjunctWithOr_PrefersExistingSingleIndexPath()
    {
        using var db = CreateDatabase();
        var store = db.Tables.Open("union_events");
        long scansBefore = store.FullScanCount;
        var metrics = new SqlExecutionMetrics();

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: null,
            "SELECT id FROM union_events WHERE tenant = 'north' AND (external_key = 'key-003' OR status = 'ready')",
            parameters: null,
            controlPlane: null,
            new SqlExecutionOptions { Metrics = metrics }));

        Assert.Equal([1L], result.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.Equal(scansBefore, store.FullScanCount);
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();
        Assert.Equal("secondary_index", snapshot.AccessPath);
        Assert.Equal("ix_union_events_tenant", snapshot.IndexName);
        Assert.Null(snapshot.FallbackReason);
    }

    /// <summary>单表 EXISTS 的纯 OR 谓词应复用有界索引并集并在首个真值候选停止。</summary>
    [Fact]
    public void Exists_IndexableOrBranches_UsesIndexUnionWithoutFullScan()
    {
        using var db = CreateDatabase();
        var store = db.Tables.Open("union_events");
        long scansBefore = store.FullScanCount;
        var metrics = new SqlExecutionMetrics();

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: null,
            "SELECT EXISTS (SELECT 1 FROM union_events WHERE external_key = 'missing' OR tenant = 'south')",
            parameters: null,
            controlPlane: null,
            new SqlExecutionOptions { Metrics = metrics }));

        Assert.True(Assert.IsType<bool>(Assert.Single(Assert.Single(result.Rows))));
        Assert.Equal(scansBefore, store.FullScanCount);
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();
        Assert.Equal("index_union", snapshot.AccessPath);
        Assert.Null(snapshot.FallbackReason);
    }

    /// <summary>目标表存在事务写集时 OR 并集必须回退扫描并叠加写集，保持 read-your-writes。</summary>
    [Fact]
    public void Select_IndexUnionWithTransactionOverlay_FallsBackAndSeesBufferedRow()
    {
        using var db = CreateDatabase();

        var results = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO union_events (id, external_key, tenant, status, occurred_at)
                VALUES (5, 'key-buffered', 'buffered', 'ready', 5000);
            EXPLAIN SELECT id FROM union_events
                WHERE external_key = 'key-buffered' OR tenant = 'missing';
            SELECT id FROM union_events
                WHERE external_key = 'key-buffered' OR tenant = 'missing';
            ROLLBACK;
            """);
        var explain = Assert.IsType<SelectExecutionResult>(results[2]);
        var explainValues = explain.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);
        var selected = Assert.IsType<SelectExecutionResult>(results[3]);

        Assert.Equal("table_scan", explainValues["access_path"]);
        Assert.Equal("transaction_overlay_requires_scan", explainValues["fallback_reason"]);
        Assert.Equal(5L, Assert.Single(Assert.Single(selected.Rows)));
    }

    /// <summary>候选集合超过预算时应丢弃部分并集并要求调用方回退，不返回截断结果。</summary>
    [Fact]
    public void IndexUnion_CandidateLimitExceeded_RequestsFallbackWithoutPartialRows()
    {
        using var db = CreateDatabase();
        var schema = db.Tables.Catalog.TryGet("union_events")!;
        var store = db.Tables.Open(schema.Name);
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT id FROM union_events WHERE tenant = 'north' OR tenant = 'south'"));

        Assert.True(TableSqlExecutor.TryChooseIndexUnionPlan(
            schema,
            statement.Where,
            out var plan,
            out var planningFallback));
        Assert.Null(planningFallback);

        Assert.False(TableSqlExecutor.TryLoadIndexUnionRows(
            store,
            schema,
            plan,
            out var rows,
            out var fallbackReason,
            candidateLimit: 2));
        Assert.Empty(rows);
        Assert.Equal("index_union_candidate_limit_exceeded", fallbackReason);
    }

    /// <summary>32 个 OR 分支可规划，第 33 个必须在有界遍历中返回稳定 fallback。</summary>
    [Fact]
    public void IndexUnion_BranchLimit_AcceptsBoundaryAndRejectsNextBranch()
    {
        using var db = CreateDatabase();
        var schema = db.Tables.Catalog.TryGet("union_events")!;
        string acceptedWhere = string.Join(
            " OR ",
            Enumerable.Range(0, 32).Select(static value => $"id = {value}"));
        string rejectedWhere = string.Join(
            " OR ",
            Enumerable.Range(0, 33).Select(static value => $"id = {value}"));
        var accepted = Assert.IsType<SelectStatement>(SqlParser.Parse(
            $"SELECT id FROM union_events WHERE {acceptedWhere}"));
        var rejected = Assert.IsType<SelectStatement>(SqlParser.Parse(
            $"SELECT id FROM union_events WHERE {rejectedWhere}"));

        Assert.True(TableSqlExecutor.TryChooseIndexUnionPlan(
            schema,
            accepted.Where,
            out var acceptedPlan,
            out var acceptedFallback));
        Assert.Equal(32, acceptedPlan.Branches.Count);
        Assert.Null(acceptedFallback);

        Assert.False(TableSqlExecutor.TryChooseIndexUnionPlan(
            schema,
            rejected.Where,
            out _,
            out var rejectedFallback));
        Assert.Equal("index_union_branch_limit_exceeded", rejectedFallback);
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

    /// <summary>创建带固定 PK、唯一键、普通键和 nullable 时间索引的数据集。</summary>
    private Tsdb CreateDatabase()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, """
            CREATE TABLE union_events (
                id INT,
                external_key STRING,
                tenant STRING,
                status STRING,
                occurred_at INT NULL,
                PRIMARY KEY (id)
            )
            """);
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_union_events_key ON union_events (external_key)");
        SqlExecutor.Execute(db, "CREATE INDEX ix_union_events_tenant ON union_events (tenant)");
        SqlExecutor.Execute(db, "CREATE INDEX ix_union_events_occurred ON union_events (occurred_at)");
        SqlExecutor.Execute(db, """
            INSERT INTO union_events (id, external_key, tenant, status, occurred_at) VALUES
                (1, 'key-001', 'north', 'ready', 1000),
                (2, 'key-002', 'north', 'blocked', NULL),
                (3, 'key-003', 'south', 'ready', 3000),
                (4, 'key-004', 'south', 'ready', 4000)
            """);
        return db;
    }
}
