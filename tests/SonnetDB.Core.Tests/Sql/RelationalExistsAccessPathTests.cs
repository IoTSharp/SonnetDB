using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// 验证 M41 #369 单表 EXISTS 的索引访问、早停、相关绑定和安全回退合同。
/// </summary>
public sealed class RelationalExistsAccessPathTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sndb-relational-exists-{Guid.NewGuid():N}");

    /// <summary>
    /// 唯一二级索引命中时不得扫描内表，并保留附加状态谓词的残余复检。
    /// </summary>
    [Fact]
    public void Exists_UniqueIndexWithResidual_UsesIndexAndStopsAfterMatch()
    {
        using var db = CreateAuditDatabase();
        var store = db.Tables.Open("exists_audits");
        long scansBefore = store.FullScanCount;

        var (result, metrics) = ExecuteWithMetrics(db, """
            SELECT EXISTS (
                SELECT 1 FROM exists_audits a
                WHERE a.idempotency_key = 'key-002' AND a.status = 'ready'
            )
            """);

        Assert.True(ReadExists(result));
        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Equal(1, metrics.SubqueryExecutionCount);
        Assert.Equal(1, metrics.ExistsFastPathExecutionCount);
        Assert.Equal(1, metrics.ExistsRowsExamined);
        Assert.Equal(1, metrics.ExistsEarlyExitCount);
        Assert.Equal("secondary_index", metrics.LastExistsAccessPath);
        Assert.Equal("ux_exists_audits_key", metrics.LastExistsIndexName);
        Assert.True(metrics.LastExistsHasResidualPredicate);
        Assert.Null(metrics.LastExistsFallbackReason);
    }

    /// <summary>
    /// 唯一索引未命中时应检查零行并返回 false，不得退回全表扫描。
    /// </summary>
    [Fact]
    public void Exists_UniqueIndexMiss_ExaminesNoRowsWithoutScan()
    {
        using var db = CreateAuditDatabase();
        var store = db.Tables.Open("exists_audits");
        long scansBefore = store.FullScanCount;

        var (result, metrics) = ExecuteWithMetrics(db, """
            SELECT EXISTS (SELECT 1 FROM exists_audits WHERE idempotency_key = 'missing')
            """);

        Assert.False(ReadExists(result));
        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Equal(0, metrics.ExistsRowsExamined);
        Assert.Equal(0, metrics.ExistsEarlyExitCount);
        Assert.Equal("secondary_index", metrics.LastExistsAccessPath);
    }

    /// <summary>
    /// 主键等值加残余谓词必须执行单次点查，并对命中行保留完整条件复检。
    /// </summary>
    [Fact]
    public void Exists_PrimaryKeyWithResidual_UsesSinglePointLookup()
    {
        using var db = CreateAuditDatabase();
        var store = db.Tables.Open("exists_audits");
        long scansBefore = store.FullScanCount;
        long lookupsBefore = store.PrimaryKeyLookupCount;

        var (result, metrics) = ExecuteWithMetrics(db, """
            SELECT EXISTS (
                SELECT 1 FROM exists_audits
                WHERE id = 2 AND occurred_at >= 1500 AND status = 'ready'
            )
            """);

        Assert.True(ReadExists(result));
        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Equal(lookupsBefore + 1, store.PrimaryKeyLookupCount);
        Assert.Equal(1, metrics.ExistsRowsExamined);
        Assert.Equal(1, metrics.ExistsEarlyExitCount);
        Assert.Equal("primary_key", metrics.LastExistsAccessPath);
        Assert.Equal("primary", metrics.LastExistsIndexName);
        Assert.True(metrics.LastExistsHasResidualPredicate);
    }

    /// <summary>
    /// 多个候选索引同时可用时，唯一完整等值探测必须优先于匹配列更多的普通索引。
    /// </summary>
    [Fact]
    public void Exists_CompetingIndexes_PrefersUniqueEqualityProbe()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, """
            CREATE TABLE exists_plan_choice (
                id INT,
                request_key STRING,
                tenant STRING,
                status STRING,
                PRIMARY KEY (id)
            )
            """);
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_exists_plan_key ON exists_plan_choice (request_key)");
        SqlExecutor.Execute(db, "CREATE INDEX ix_exists_plan_tenant_status ON exists_plan_choice (tenant, status)");
        SqlExecutor.Execute(db, """
            INSERT INTO exists_plan_choice (id, request_key, tenant, status) VALUES
                (1, 'key-001', 'north', 'ready'),
                (2, 'key-002', 'north', 'ready'),
                (3, 'key-003', 'north', 'ready')
            """);
        var store = db.Tables.Open("exists_plan_choice");
        long scansBefore = store.FullScanCount;

        var (result, metrics) = ExecuteWithMetrics(db, """
            SELECT EXISTS (
                SELECT 1 FROM exists_plan_choice
                WHERE request_key = 'key-003' AND tenant = 'north' AND status = 'ready'
            )
            """);

        Assert.True(ReadExists(result));
        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Equal(1, metrics.ExistsRowsExamined);
        Assert.Equal("secondary_index", metrics.LastExistsAccessPath);
        Assert.Equal("ux_exists_plan_key", metrics.LastExistsIndexName);
        Assert.True(metrics.LastExistsHasResidualPredicate);
    }

    /// <summary>
    /// 关系执行器接受的大小写不敏感列名必须规范化后再交给表索引规划和残余复检。
    /// </summary>
    [Fact]
    public void Exists_MixedCaseIdentifiers_UseCanonicalIndexedColumns()
    {
        using var db = CreateAuditDatabase();
        var store = db.Tables.Open("exists_audits");
        long scansBefore = store.FullScanCount;

        var (result, metrics) = ExecuteWithMetrics(db, """
            SELECT EXISTS (
                SELECT 1 FROM exists_audits a
                WHERE A.IDEMPOTENCY_KEY = 'key-002' AND A.STATUS = 'ready'
            )
            """);

        Assert.True(ReadExists(result));
        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Equal("secondary_index", metrics.LastExistsAccessPath);
        Assert.Equal("ux_exists_audits_key", metrics.LastExistsIndexName);
    }

    /// <summary>
    /// 内表存在仅大小写不同的同名列时必须回退并报告歧义，不能误绑定外层同名列。
    /// </summary>
    [Fact]
    public void Exists_AmbiguousCaseInsensitiveInnerColumn_DoesNotBindOuterColumn()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE exists_case_outer (id INT, foo STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE exists_case_inner (id INT, Foo STRING, FOO STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO exists_case_outer (id, foo) VALUES (1, 'outer')");
        SqlExecutor.Execute(db, "INSERT INTO exists_case_inner (id, Foo, FOO) VALUES (1, 'left', 'right')");
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse("""
            SELECT o.id
            FROM exists_case_outer o
            WHERE EXISTS (
                SELECT 1 FROM exists_case_inner i WHERE foo = o.foo
            )
            """));
        var metrics = new RelationalSelectExecutionMetrics();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RelationalSelectExecutor.Execute(db, statement, metrics));

        Assert.Contains("歧义", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, metrics.ExistsFallbackExecutionCount);
        Assert.Equal("outer_reference_not_safely_bindable", metrics.LastExistsFallbackReason);
    }

    /// <summary>
    /// 参数化 EXISTS 必须复用索引；NULL 等值保持 UNKNOWN，不得改写成 IS NULL。
    /// </summary>
    [Fact]
    public void Exists_ParameterizedEfShape_PreservesHitMissAndNullSemantics()
    {
        using var db = CreateAuditDatabase();
        var store = db.Tables.Open("exists_audits");
        long scansBefore = store.FullScanCount;
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM exists_audits AS a
                WHERE a.idempotency_key = @idempotencyKey AND a.status = @status
            )
            """;

        var hit = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: null,
            sql,
            new SqlParameters()
                .AddNamed("idempotencyKey", "key-002")
                .AddNamed("status", "ready")));
        var miss = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: null,
            sql,
            new SqlParameters()
                .AddNamed("idempotencyKey", "key-002")
                .AddNamed("status", "rejected")));
        var nullKey = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: null,
            sql,
            new SqlParameters()
                .AddNamed("idempotencyKey", null)
                .AddNamed("status", "ready")));

        Assert.True(ReadExists(hit));
        Assert.False(ReadExists(miss));
        Assert.False(ReadExists(nullKey));
        Assert.Equal(scansBefore, store.FullScanCount);
    }

    /// <summary>
    /// 首个外层键未命中时仍须标记相关，后续命中不得复用错误的 false 缓存。
    /// </summary>
    [Fact]
    public void Exists_CorrelatedFirstMissThenHit_ProbesPerOuterRowWithoutMemoPollution()
    {
        using var db = CreateAuditDatabase();
        SqlExecutor.Execute(db, "CREATE TABLE exists_requests (id INT, lookup_key STRING NULL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, """
            INSERT INTO exists_requests (id, lookup_key) VALUES
                (1, 'missing'), (2, 'key-002'), (3, NULL)
            """);
        var innerStore = db.Tables.Open("exists_audits");
        long scansBefore = innerStore.FullScanCount;

        var (result, metrics) = ExecuteWithMetrics(db, """
            SELECT r.id
            FROM exists_requests r
            WHERE EXISTS (
                SELECT 1 FROM exists_audits a
                WHERE a.idempotency_key = r.lookup_key AND a.status = 'ready'
            )
            ORDER BY r.id
            """);

        Assert.Equal([2L], result.Rows.Select(static row => (long)row[0]!));
        Assert.Equal(scansBefore, innerStore.FullScanCount);
        Assert.Equal(3, metrics.SubqueryExecutionCount);
        Assert.Equal(0, metrics.SubqueryCacheHitCount);
        Assert.Equal(3, metrics.ExistsFastPathExecutionCount);
        Assert.Equal(1, metrics.ExistsRowsExamined);
        Assert.Equal(1, metrics.ExistsEarlyExitCount);
    }

    /// <summary>
    /// 非唯一索引带残余条件时不得盲目 limit 1，第二个候选命中仍应返回 true。
    /// </summary>
    [Fact]
    public void Exists_NonUniqueIndexResidual_ExaminesUntilSecondCandidate()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE exists_events (id INT, tenant STRING, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_exists_events_tenant ON exists_events (tenant)");
        SqlExecutor.Execute(db, """
            INSERT INTO exists_events (id, tenant, status) VALUES
                (1, 'north', 'blocked'), (2, 'north', 'ready'), (3, 'north', 'blocked'),
                (4, 'south', 'ready')
            """);
        var store = db.Tables.Open("exists_events");
        long scansBefore = store.FullScanCount;

        var (hit, hitMetrics) = ExecuteWithMetrics(db, """
            SELECT EXISTS (
                SELECT 1 FROM exists_events
                WHERE tenant = 'north' AND status = 'ready'
            )
            """);
        var (miss, missMetrics) = ExecuteWithMetrics(db, """
            SELECT EXISTS (
                SELECT 1 FROM exists_events
                WHERE tenant = 'north' AND status = 'missing'
            )
            """);

        Assert.True(ReadExists(hit));
        Assert.False(ReadExists(miss));
        Assert.Equal(2, hitMetrics.ExistsRowsExamined);
        Assert.Equal(3, missMetrics.ExistsRowsExamined);
        Assert.Equal(1, hitMetrics.ExistsEarlyExitCount);
        Assert.Equal(0, missMetrics.ExistsEarlyExitCount);
        Assert.Equal(scansBefore, store.FullScanCount);
    }

    /// <summary>
    /// 无谓词 EXISTS 可把表扫描限制为一行；不可索引未命中则检查全部必要候选。
    /// </summary>
    [Fact]
    public void Exists_TableScan_UsesSafeLimitOnlyWhenPredicateCovered()
    {
        using var db = CreateAuditDatabase();
        var store = db.Tables.Open("exists_audits");
        long scansBefore = store.FullScanCount;

        var (any, anyMetrics) = ExecuteWithMetrics(db, "SELECT EXISTS (SELECT 1 FROM exists_audits)");
        var (none, noneMetrics) = ExecuteWithMetrics(db, """
            SELECT EXISTS (SELECT 1 FROM exists_audits WHERE status = 'missing')
            """);

        Assert.True(ReadExists(any));
        Assert.False(ReadExists(none));
        Assert.Equal(1, anyMetrics.ExistsRowsExamined);
        Assert.Equal(3, noneMetrics.ExistsRowsExamined);
        Assert.Equal(scansBefore + 2, store.FullScanCount);
        Assert.Equal("table_scan", anyMetrics.LastExistsAccessPath);
        Assert.Equal("no_sargable_predicate", noneMetrics.LastExistsFallbackReason);
    }

    /// <summary>
    /// 活动轻事务存在缓冲写时必须扫描并叠加 overlay，保持 insert/update/delete 的 read-your-writes。
    /// </summary>
    [Fact]
    public void Exists_TransactionOverlay_PreservesReadYourWrites()
    {
        using var db = CreateAuditDatabase();
        var store = db.Tables.Open("exists_audits");
        long scansBefore = store.FullScanCount;

        var insertScript = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO exists_audits (id, idempotency_key, status, occurred_at)
                VALUES (4, 'key-buffered', 'ready', 4000);
            SELECT EXISTS (SELECT 1 FROM exists_audits WHERE idempotency_key = 'key-buffered');
            ROLLBACK;
            """);
        var updateScript = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            UPDATE exists_audits SET idempotency_key = 'key-updated' WHERE id = 1;
            SELECT EXISTS (SELECT 1 FROM exists_audits WHERE idempotency_key = 'key-001');
            SELECT EXISTS (SELECT 1 FROM exists_audits WHERE idempotency_key = 'key-updated');
            ROLLBACK;
            """);
        var deleteScript = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            DELETE FROM exists_audits WHERE id = 2;
            SELECT EXISTS (SELECT 1 FROM exists_audits WHERE idempotency_key = 'key-002');
            ROLLBACK;
            """);

        Assert.True(ReadExists(Assert.IsType<SelectExecutionResult>(insertScript[2])));
        Assert.False(ReadExists(Assert.IsType<SelectExecutionResult>(updateScript[2])));
        Assert.True(ReadExists(Assert.IsType<SelectExecutionResult>(updateScript[3])));
        Assert.False(ReadExists(Assert.IsType<SelectExecutionResult>(deleteScript[2])));
        Assert.True(store.FullScanCount > scansBefore);
    }

    /// <summary>
    /// 活动事务仅修改其他表时，目标表 EXISTS 仍应保留二级索引探测，不能无条件退化为扫描。
    /// </summary>
    [Fact]
    public void Exists_TransactionWritesOnOtherTable_KeepsIndexedAccess()
    {
        using var db = CreateAuditDatabase();
        SqlExecutor.Execute(db, "CREATE TABLE exists_other_writes (id INT, note STRING, PRIMARY KEY (id))");
        var store = db.Tables.Open("exists_audits");
        long scansBefore = store.FullScanCount;

        var results = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO exists_other_writes (id, note) VALUES (1, 'buffered');
            EXPLAIN SELECT EXISTS (
                SELECT 1 FROM exists_audits WHERE idempotency_key = 'key-002'
            );
            SELECT EXISTS (
                SELECT 1 FROM exists_audits WHERE idempotency_key = 'key-002'
            );
            ROLLBACK;
            """);
        var explain = Assert.IsType<SelectExecutionResult>(results[2]);
        var explainValues = explain.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);

        Assert.Equal("secondary_index", explainValues["access_path"]);
        Assert.True(ReadExists(Assert.IsType<SelectExecutionResult>(results[3])));
        Assert.Equal(scansBefore, store.FullScanCount);
    }

    /// <summary>
    /// 聚合和 LIMIT 仍走原关系执行器，分别保持空表聚合存在性与 LIMIT 0 语义。
    /// </summary>
    [Fact]
    public void Exists_UnsafeShapes_FallBackWithoutChangingSemantics()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE exists_empty (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE exists_groups (id INT, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO exists_groups (id, status) VALUES (1, 'ready')");

        var (aggregate, aggregateMetrics) = ExecuteWithMetrics(
            db,
            "SELECT EXISTS (SELECT count(*) FROM exists_empty)");
        var (limited, limitedMetrics) = ExecuteWithMetrics(
            db,
            "SELECT EXISTS (SELECT 1 FROM exists_empty LIMIT 0)");
        var (grouped, groupedMetrics) = ExecuteWithMetrics(
            db,
            "SELECT EXISTS (SELECT status FROM exists_groups GROUP BY status HAVING count(*) > 99)");

        Assert.True(ReadExists(aggregate));
        Assert.False(ReadExists(limited));
        Assert.False(ReadExists(grouped));
        Assert.Equal(1, aggregateMetrics.ExistsFallbackExecutionCount);
        Assert.Equal("projection_requires_evaluation", aggregateMetrics.LastExistsFallbackReason);
        Assert.Equal(1, limitedMetrics.ExistsFallbackExecutionCount);
        Assert.Equal("ordering_or_pagination", limitedMetrics.LastExistsFallbackReason);
        Assert.Equal(1, groupedMetrics.ExistsFallbackExecutionCount);
        Assert.Equal("aggregate_or_distinct", groupedMetrics.LastExistsFallbackReason);
    }

    /// <summary>
    /// 星号投影携带别名时必须回退并保留原执行器的稳定错误，不能被 EXISTS 快路径忽略。
    /// </summary>
    [Fact]
    public void Exists_StarProjectionWithAlias_PreservesStableError()
    {
        using var db = CreateAuditDatabase();
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT EXISTS (SELECT * AS invalid_alias FROM exists_audits)"));
        var metrics = new RelationalSelectExecutionMetrics();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RelationalSelectExecutor.Execute(db, statement, metrics));

        Assert.Contains("'*' 不允许带 alias", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, metrics.ExistsFastPathExecutionCount);
        Assert.Equal(1, metrics.ExistsFallbackExecutionCount);
        Assert.Equal("projection_requires_evaluation", metrics.LastExistsFallbackReason);
    }

    /// <summary>删除每个测试使用的临时数据库目录。</summary>
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

    /// <summary>创建带唯一幂等键索引的固定审计数据集。</summary>
    private Tsdb CreateAuditDatabase()
    {
        var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, """
            CREATE TABLE exists_audits (
                id INT,
                idempotency_key STRING NULL,
                status STRING,
                occurred_at INT,
                PRIMARY KEY (id)
            )
            """);
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_exists_audits_key ON exists_audits (idempotency_key)");
        SqlExecutor.Execute(db, """
            INSERT INTO exists_audits (id, idempotency_key, status, occurred_at) VALUES
                (1, 'key-001', 'blocked', 1000),
                (2, 'key-002', 'ready', 2000),
                (3, NULL, 'ready', 3000)
            """);
        return db;
    }

    /// <summary>创建指向当前测试临时目录的数据库选项。</summary>
    private TsdbOptions Options() => new() { RootDirectory = _root };

    /// <summary>解析并通过可观测入口执行关系 SELECT。</summary>
    private static (SelectExecutionResult Result, RelationalSelectExecutionMetrics Metrics) ExecuteWithMetrics(
        Tsdb db,
        string sql)
    {
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(sql));
        var metrics = new RelationalSelectExecutionMetrics();
        return (RelationalSelectExecutor.Execute(db, statement, metrics), metrics);
    }

    /// <summary>读取独立 SELECT EXISTS 的单个布尔投影。</summary>
    private static bool ReadExists(SelectExecutionResult result)
    {
        Assert.Single(result.Rows);
        return Assert.IsType<bool>(result.Rows[0][0]);
    }
}
