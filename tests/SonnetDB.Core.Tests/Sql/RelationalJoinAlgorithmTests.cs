using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 M41 #378 index nested-loop 与 merge join 的成本准入和语义。</summary>
public sealed class RelationalJoinAlgorithmTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sndb-relational-join-algorithm-{Guid.NewGuid():N}");

    /// <summary>小 probe 连接大主键表时应逐键点读，而不是扫描或构建大表。</summary>
    [Fact]
    public void Execute_SmallProbeToLargePrimaryKey_UsesIndexNestedLoop()
    {
        using Tsdb database = CreateIndexNestedLoopDatabase();

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT p.id, d.name
            FROM join_probe p
            JOIN join_dimension d ON p.dimension_id = d.id
            ORDER BY p.id
            """);

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(["dimension-7", "dimension-999", "dimension-1999"],
            result.Rows.Select(static row => (string)row[1]!));
        Assert.Equal("index_nested_loop", metrics.LastJoinOperator);
        Assert.Equal("primary", metrics.LastJoinIndexName);
        Assert.Equal(3, metrics.LastJoinProbeRows);
        Assert.Equal(3, metrics.LastJoinLookupCount);
        Assert.Equal(3, metrics.LastJoinCandidateRows);
    }

    /// <summary>索引嵌套循环执行 LEFT JOIN 时必须保留 NULL 键和未命中的左行。</summary>
    [Fact]
    public void Execute_IndexNestedLoopLeftJoin_PreservesNullAndMissingProbeRows()
    {
        using Tsdb database = CreateIndexNestedLoopDatabase(includeMissingRows: true);

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT p.id, d.name
            FROM join_probe p
            LEFT JOIN join_dimension d ON p.dimension_id = d.id
            ORDER BY p.id
            """);

        Assert.Equal(5, result.Rows.Count);
        Assert.Null(result.Rows[3][1]);
        Assert.Null(result.Rows[4][1]);
        Assert.Equal("index_nested_loop", metrics.LastJoinOperator);
        Assert.Equal(4, metrics.LastJoinLookupCount);
    }

    /// <summary>EXPLAIN 应与运行时使用同一 index nested-loop 成本选择。</summary>
    [Fact]
    public void Explain_SmallProbeToLargePrimaryKey_ReportsIndexNestedLoop()
    {
        using Tsdb database = CreateIndexNestedLoopDatabase();

        IReadOnlyDictionary<string, object?> plan = Explain(database, """
            SELECT p.id, d.name
            FROM join_probe p
            JOIN join_dimension d ON p.dimension_id = d.id
            """);

        Assert.Equal("index_nested_loop", plan["plan_node"]);
        Assert.Contains(
            "join_1:index_nested_loop(index=primary,probes<=3)",
            (string)plan["access_path"]!);
        Assert.Contains("primary", (string)plan["index_name"]!);
        Assert.Contains("index_lookup=bounded_per_probe", (string)plan["memory_behavior"]!);
    }

    /// <summary>两个大且已按连接键建索引的输入应走流式 merge join，并保留重复键乘积。</summary>
    [Fact]
    public void Execute_LargeOrderedInputs_UsesMergeJoinAndPreservesDuplicateGroups()
    {
        using Tsdb database = CreateMergeJoinDatabase();

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT l.id, r.payload
            FROM merge_left l
            JOIN merge_right r ON l.join_key = r.join_key
            ORDER BY l.id
            """);

        Assert.Equal(4_999, result.Rows.Count);
        Assert.Equal("payload--1250", result.Rows[0][1]);
        Assert.Equal("payload-1249", result.Rows[^1][1]);
        Assert.Equal("merge_join", metrics.LastJoinOperator);
        Assert.Equal("ix_merge_left_key,primary", metrics.LastJoinIndexName);
        Assert.Equal(0, metrics.LastJoinLookupCount);
    }

    /// <summary>Merge Join 必须按 Table V1 的补码索引顺序推进，不能在稀疏集合中跳过共同负键。</summary>
    [Fact]
    public void Execute_SparseSignedOrderedInputs_UsesPhysicalIndexOrderAndPreservesNegativeMatch()
    {
        using Tsdb database = CreateSparseSignedMergeJoinDatabase();

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT l.id, r.payload
            FROM merge_sparse_left l
            JOIN merge_sparse_right r ON l.join_key = r.join_key
            """);

        IReadOnlyList<object?> row = Assert.Single(result.Rows);
        Assert.Equal(0L, row[0]);
        Assert.Equal(4_999L, row[1]);
        Assert.Equal("merge_join", metrics.LastJoinOperator);
    }

    /// <summary>Merge Join EXPLAIN 应报告双侧有序索引和仅按重复键分组的内存边界。</summary>
    [Fact]
    public void Explain_LargeOrderedInputs_ReportsMergeJoin()
    {
        using Tsdb database = CreateMergeJoinDatabase();

        IReadOnlyDictionary<string, object?> plan = Explain(database, """
            SELECT l.id, r.payload
            FROM merge_left l
            JOIN merge_right r ON l.join_key = r.join_key
            """);

        Assert.Equal("merge_join", plan["plan_node"]);
        Assert.Contains(
            "join_1:merge_join(indexes=ix_merge_left_key,primary,rows<=7500)",
            (string)plan["access_path"]!);
        Assert.Contains("duplicate_group=bounded_by_key", (string)plan["memory_behavior"]!);
    }

    /// <summary>字符串索引的长度前缀物理顺序不满足 ordinal 比较时必须回退 Hash Join。</summary>
    [Fact]
    public void Execute_StringIndexedInputs_FallsBackFromMergeJoinAndPreservesMatches()
    {
        using Tsdb database = CreateStringJoinDatabase();

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT l.id, r.payload
            FROM merge_string_left l
            JOIN merge_string_right r ON l.join_key = r.join_key
            """);

        IReadOnlyList<object?> row = Assert.Single(result.Rows);
        Assert.Equal(1L, row[0]);
        Assert.Equal(1L, row[1]);
        Assert.Equal("hash_join", metrics.LastJoinOperator);
    }

    /// <summary>静态类型不一致的连接键不得直接编码为右表索引键。</summary>
    [Fact]
    public void Execute_CrossTypeJoinKey_FallsBackFromIndexNestedLoopWithoutConversionFailure()
    {
        using Tsdb database = CreateCrossTypeJoinDatabase();

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT p.id, d.name
            FROM cross_type_probe p
            JOIN cross_type_dimension d ON p.lookup_key = d.id
            ORDER BY p.id
            """);

        IReadOnlyList<object?> row = Assert.Single(result.Rows);
        Assert.Equal(1L, row[0]);
        Assert.Equal("dimension-7", row[1]);
        Assert.Equal("hash_join", metrics.LastJoinOperator);
    }

    /// <summary>DATETIME 与等值 Unix 毫秒 INT64 必须落入同一 Hash Join 桶。</summary>
    [Fact]
    public void Execute_DateTimeAndUnixMillisecondsHashJoin_PreservesSqlEquality()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE TABLE hash_datetime_left (id INT, join_key DATETIME, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "CREATE TABLE hash_datetime_right (id INT, join_key INT, PRIMARY KEY (id))");
        var timestamp = new DateTime(2026, 8, 28, 9, 30, 0, DateTimeKind.Utc);
        long unixMilliseconds = new DateTimeOffset(timestamp).ToUnixTimeMilliseconds();
        database.Tables.Open("hash_datetime_left").InsertMany([[1L, timestamp]]);
        database.Tables.Open("hash_datetime_right").InsertMany([[2L, unixMilliseconds]]);

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT l.id, r.id
            FROM hash_datetime_left l
            JOIN hash_datetime_right r ON l.join_key = r.join_key
            """);

        Assert.Equal(new object?[] { 1L, 2L }, Assert.Single(result.Rows));
        Assert.Equal("hash_join", metrics.LastJoinOperator);
    }

    /// <summary>BLOB 连接键必须按内容而不是数组引用生成 Hash Join 哈希码。</summary>
    [Fact]
    public void Execute_EqualBlobHashJoinKeysWithDifferentArrays_ReturnsMatch()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE TABLE hash_blob_left (id INT, join_key BLOB, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "CREATE TABLE hash_blob_right (id INT, join_key BLOB, PRIMARY KEY (id))");
        database.Tables.Open("hash_blob_left").InsertMany([[1L, new byte[] { 1, 2, 3 }]]);
        database.Tables.Open("hash_blob_right").InsertMany([[2L, new byte[] { 1, 2, 3 }]]);

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT l.id, r.id
            FROM hash_blob_left l
            JOIN hash_blob_right r ON l.join_key = r.join_key
            """);

        Assert.Equal(new object?[] { 1L, 2L }, Assert.Single(result.Rows));
        Assert.Equal("hash_join", metrics.LastJoinOperator);
    }

    /// <summary>非索引外键的 IN 子查询应构建一次 membership Hash 并按外表行数探测。</summary>
    [Fact]
    public void Execute_NonCorrelatedInSubquery_UsesHashSemijoinMembership()
    {
        using Tsdb database = CreateMembershipDatabase(includeInnerNull: false);

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT id
            FROM membership_outer
            WHERE value IN (SELECT value FROM membership_inner)
            ORDER BY id
            """);

        Assert.Equal(1_000, result.Rows.Count);
        Assert.Equal(2_000, metrics.SemiJoinProbeCount);
        Assert.Equal(0, metrics.AntiJoinProbeCount);
        Assert.Equal(1_000, metrics.LastMembershipBuildRows);
        Assert.Equal(1, metrics.SubqueryExecutionCount);

        IReadOnlyDictionary<string, object?> plan = Explain(database, """
            SELECT id
            FROM membership_outer
            WHERE value IN (SELECT value FROM membership_inner)
            """);
        Assert.Equal("hash_semijoin", plan["plan_node"]);
        Assert.Contains("join:hash_semijoin(build=inner,rows=1000)", (string)plan["access_path"]!);
    }

    /// <summary>NOT IN 应复用同一 membership Hash，并保持内表 NULL 导致 UNKNOWN 的三值逻辑。</summary>
    [Fact]
    public void Execute_NonCorrelatedNotInSubquery_UsesHashAntijoinAndPreservesNullSemantics()
    {
        using Tsdb database = CreateMembershipDatabase(includeInnerNull: true);

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT id
            FROM membership_outer
            WHERE value NOT IN (SELECT value FROM membership_inner)
            ORDER BY id
            """);

        Assert.Empty(result.Rows);
        Assert.Equal(0, metrics.SemiJoinProbeCount);
        Assert.Equal(2_000, metrics.AntiJoinProbeCount);
        Assert.Equal(1_001, metrics.LastMembershipBuildRows);
        Assert.Equal(1, metrics.SubqueryExecutionCount);

        IReadOnlyDictionary<string, object?> plan = Explain(database, """
            SELECT id
            FROM membership_outer
            WHERE value NOT IN (SELECT value FROM membership_inner)
            """);
        Assert.Equal("hash_antijoin", plan["plan_node"]);
        Assert.Equal("null_aware_not_in_membership", plan["candidate_contract"]);
    }

    /// <summary>空内表上的 NOT IN 对 NULL 外值仍为 TRUE，membership 快速路径不得引入 UNKNOWN。</summary>
    [Fact]
    public void Execute_NotInEmptySubquery_WithNullOuterValue_ReturnsOuterRow()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE TABLE empty_membership_outer (id INT, value INT NULL, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "CREATE TABLE empty_membership_inner (id INT, value INT NULL, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "INSERT INTO empty_membership_outer (id, value) VALUES (1, NULL)");

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT id
            FROM empty_membership_outer
            WHERE value NOT IN (SELECT value FROM empty_membership_inner)
            """);

        IReadOnlyList<object?> row = Assert.Single(result.Rows);
        Assert.Equal(1L, row[0]);
        Assert.Equal(1, metrics.AntiJoinProbeCount);
        Assert.Equal(0, metrics.LastMembershipBuildRows);
    }

    /// <summary>三个基础表的有限枚举应从最小连通输入开始，并恢复声明列顺序。</summary>
    [Fact]
    public void Execute_ThreeWayInnerJoin_EnumeratesAndReordersConnectedGraph()
    {
        using Tsdb database = CreateJoinOrderDatabase(includeMissingMedium: false);

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT *
            FROM order_large l
            JOIN order_medium m ON l.medium_id = m.id
            JOIN order_tiny t ON m.tiny_id = t.id
            ORDER BY l.id
            """);

        Assert.Equal(1_000, result.Rows.Count);
        Assert.Equal(["l.id", "medium_id", "m.id", "tiny_id", "t.id"], result.Columns);
        Assert.Equal("t,m,l", metrics.LastJoinOrder);
        Assert.Equal(4, metrics.LastJoinOrderCandidateCount);
        Assert.True(metrics.LastJoinOrderReordered);
        Assert.Null(metrics.LastJoinOrderFallbackReason);

        IReadOnlyDictionary<string, object?> plan = Explain(database, """
            SELECT *
            FROM order_large l
            JOIN order_medium m ON l.medium_id = m.id
            JOIN order_tiny t ON m.tiny_id = t.id
            """);
        Assert.Contains("join_order=t,m,l", (string)plan["access_path"]!);
        Assert.Contains("join_order_candidates=4", (string)plan["access_path"]!);
    }

    /// <summary>同一张表的三个别名参与重排时，别名绑定和 SELECT * 声明列顺序必须保持稳定。</summary>
    [Fact]
    public void Execute_ThreeAliasSelfJoin_ReordersByAliasAndRestoresDeclaredColumns()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE TABLE self_join_nodes (id INT, parent_id INT, PRIMARY KEY (id))");
        database.Tables.Open("self_join_nodes").InsertMany(Enumerable.Range(0, 1_000)
            .Select(static id => (IReadOnlyList<object?>)new object?[]
            {
                (long)id,
                (long)(id < 10 ? 1 : id / 10),
            })
            .ToArray());

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT *
            FROM self_join_nodes a
            JOIN self_join_nodes b ON a.parent_id = b.id
            JOIN self_join_nodes c ON b.parent_id = c.id
            WHERE c.id = 1
            ORDER BY a.id
            """);

        Assert.Equal(200, result.Rows.Count);
        Assert.Equal(
            ["a.id", "a.parent_id", "b.id", "b.parent_id", "c.id", "c.parent_id"],
            result.Columns);
        Assert.All(result.Rows, static row => Assert.Equal(1L, row[4]));
        Assert.Equal("c,b,a", metrics.LastJoinOrder);
        Assert.True(metrics.LastJoinOrderReordered);
    }

    /// <summary>外连接必须保持声明顺序，并保留跨两级 JOIN 的 NULL 扩展行。</summary>
    [Fact]
    public void Execute_ThreeWayLeftJoin_PreservesDeclaredOrderAndNullExtension()
    {
        using Tsdb database = CreateJoinOrderDatabase(includeMissingMedium: true);

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT l.id, m.id, t.id
            FROM order_large l
            LEFT JOIN order_medium m ON l.medium_id = m.id
            LEFT JOIN order_tiny t ON m.tiny_id = t.id
            ORDER BY l.id
            """);

        Assert.Equal(1_000, result.Rows.Count);
        Assert.Equal(new object?[] { 999L, null, null }, result.Rows[^1]);
        Assert.Equal("l,m,t", metrics.LastJoinOrder);
        Assert.False(metrics.LastJoinOrderReordered);
        Assert.Equal("outer_join_preserves_declared_order", metrics.LastJoinOrderFallbackReason);

        IReadOnlyDictionary<string, object?> plan = Explain(database, """
            SELECT l.id, m.id, t.id
            FROM order_large l
            LEFT JOIN order_medium m ON l.medium_id = m.id
            LEFT JOIN order_tiny t ON m.tiny_id = t.id
            """);
        Assert.Contains(
            "join_order:outer_join_preserves_declared_order",
            (string)plan["fallback_reason"]!);
    }

    /// <summary>超过六个输入的连接图必须走稳定声明顺序回退。</summary>
    [Fact]
    public void Execute_SevenInputJoin_FallsBackWithoutUnboundedEnumeration()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        for (int index = 0; index < 7; index++)
        {
            SqlExecutor.Execute(database, $"CREATE TABLE order_{index} (id INT, PRIMARY KEY (id))");
            SqlExecutor.Execute(database, $"INSERT INTO order_{index} (id) VALUES (1)");
        }

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT a0.id
            FROM order_0 a0
            JOIN order_1 a1 ON a0.id = a1.id
            JOIN order_2 a2 ON a1.id = a2.id
            JOIN order_3 a3 ON a2.id = a3.id
            JOIN order_4 a4 ON a3.id = a4.id
            JOIN order_5 a5 ON a4.id = a5.id
            JOIN order_6 a6 ON a5.id = a6.id
            """);

        Assert.Single(result.Rows);
        Assert.Equal("a0,a1,a2,a3,a4,a5,a6", metrics.LastJoinOrder);
        Assert.Equal(0, metrics.LastJoinOrderCandidateCount);
        Assert.Equal("join_graph_exceeds_enumeration_limit", metrics.LastJoinOrderFallbackReason);
    }

    /// <inheritdoc />
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

    private Tsdb CreateIndexNestedLoopDatabase(bool includeMissingRows = false)
    {
        Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE TABLE join_dimension (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "CREATE TABLE join_probe (id INT, dimension_id INT NULL, PRIMARY KEY (id))");

        database.Tables.Open("join_dimension").InsertMany(Enumerable.Range(0, 2_000)
            .Select(static id => (IReadOnlyList<object?>)new object?[] { (long)id, $"dimension-{id}" })
            .ToArray());
        string extra = includeMissingRows ? ", (4, 9000), (5, NULL)" : string.Empty;
        SqlExecutor.Execute(database, $"""
            INSERT INTO join_probe (id, dimension_id) VALUES
                (1, 7), (2, 999), (3, 1999){extra}
            """);
        return database;
    }

    private Tsdb CreateMergeJoinDatabase()
    {
        Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, """
            CREATE TABLE merge_left (
                id INT,
                join_key INT NULL,
                PRIMARY KEY (id))
            """);
        SqlExecutor.Execute(database, "CREATE INDEX ix_merge_left_key ON merge_left (join_key)");
        SqlExecutor.Execute(database, """
            CREATE TABLE merge_right (
                join_key INT,
                payload STRING,
                PRIMARY KEY (join_key))
            """);

        database.Tables.Open("merge_left").InsertMany(Enumerable.Range(0, 5_000)
            .Select(static id => (IReadOnlyList<object?>)new object?[]
            {
                (long)id,
                id == 4_999 ? null : -1_250L + id / 2,
            })
            .ToArray());
        database.Tables.Open("merge_right").InsertMany(Enumerable.Range(0, 2_500)
            .Select(static id => (IReadOnlyList<object?>)new object?[]
            {
                -1_250L + id,
                $"payload-{-1_250 + id}",
            })
            .ToArray());
        return database;
    }

    private Tsdb CreateSparseSignedMergeJoinDatabase()
    {
        Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE TABLE merge_sparse_left (id INT, join_key INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "CREATE INDEX ix_merge_sparse_left_key ON merge_sparse_left (join_key)");
        SqlExecutor.Execute(database, "CREATE TABLE merge_sparse_right (join_key INT, payload INT, PRIMARY KEY (join_key))");

        database.Tables.Open("merge_sparse_left").InsertMany(Enumerable.Range(0, 5_000)
            .Select(static id => (IReadOnlyList<object?>)new object?[]
            {
                (long)id,
                id == 0 ? -1L : (long)id,
            })
            .ToArray());
        database.Tables.Open("merge_sparse_right").InsertMany(Enumerable.Range(0, 5_000)
            .Select(static id => (IReadOnlyList<object?>)new object?[]
            {
                (long)(id - 5_000),
                (long)id,
            })
            .ToArray());
        return database;
    }

    private Tsdb CreateStringJoinDatabase()
    {
        Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE TABLE merge_string_left (id INT, join_key STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "CREATE INDEX ix_merge_string_left_key ON merge_string_left (join_key)");
        SqlExecutor.Execute(database, "CREATE TABLE merge_string_right (join_key STRING, payload INT, PRIMARY KEY (join_key))");

        database.Tables.Open("merge_string_left").InsertMany(Enumerable.Range(0, 3_000)
            .Select(static id => (IReadOnlyList<object?>)new object?[]
            {
                (long)id,
                id switch
                {
                    0 => "b",
                    1 => "aa",
                    _ => $"left-{id:D4}",
                },
            })
            .ToArray());
        database.Tables.Open("merge_string_right").InsertMany(Enumerable.Range(0, 3_000)
            .Select(static id => (IReadOnlyList<object?>)new object?[]
            {
                id == 0 ? "aa" : $"right-{id:D4}",
                (long)(id + 1),
            })
            .ToArray());
        return database;
    }

    private Tsdb CreateCrossTypeJoinDatabase()
    {
        Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE TABLE cross_type_dimension (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "CREATE TABLE cross_type_probe (id INT, lookup_key FLOAT, PRIMARY KEY (id))");
        database.Tables.Open("cross_type_dimension").InsertMany(Enumerable.Range(0, 2_000)
            .Select(static id => (IReadOnlyList<object?>)new object?[] { (long)id, $"dimension-{id}" })
            .ToArray());
        database.Tables.Open("cross_type_probe").InsertMany(
        [
            [1L, 7d],
            [2L, 7.5d],
            [3L, 1e20d],
        ]);
        return database;
    }

    private Tsdb CreateMembershipDatabase(bool includeInnerNull)
    {
        Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE TABLE membership_outer (id INT, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "CREATE TABLE membership_inner (id INT, value INT NULL, PRIMARY KEY (id))");
        database.Tables.Open("membership_outer").InsertMany(Enumerable.Range(0, 2_000)
            .Select(static id => (IReadOnlyList<object?>)new object?[] { (long)id, (long)id })
            .ToArray());
        var innerRows = Enumerable.Range(0, 1_000)
            .Select(static id => (IReadOnlyList<object?>)new object?[] { (long)id, (long)(id * 2) })
            .ToList();
        if (includeInnerNull)
            innerRows.Add([1_001L, null]);
        database.Tables.Open("membership_inner").InsertMany(innerRows);
        return database;
    }

    private Tsdb CreateJoinOrderDatabase(bool includeMissingMedium)
    {
        Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE TABLE order_tiny (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "CREATE TABLE order_medium (id INT, tiny_id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "CREATE TABLE order_large (id INT, medium_id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "INSERT INTO order_tiny (id) VALUES (0), (1)");
        database.Tables.Open("order_medium").InsertMany(Enumerable.Range(0, 100)
            .Select(static id => (IReadOnlyList<object?>)new object?[] { (long)id, (long)(id % 2) })
            .ToArray());
        database.Tables.Open("order_large").InsertMany(Enumerable.Range(0, 1_000)
            .Select(id => (IReadOnlyList<object?>)new object?[]
            {
                (long)id,
                includeMissingMedium && id == 999 ? 999L : (long)(id % 100),
            })
            .ToArray());
        return database;
    }

    private static (SelectExecutionResult Result, RelationalSelectExecutionMetrics Metrics) Execute(
        Tsdb database,
        string sql)
    {
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(sql));
        var metrics = new RelationalSelectExecutionMetrics();
        return (RelationalSelectExecutor.Execute(database, statement, metrics), metrics);
    }

    private static IReadOnlyDictionary<string, object?> Explain(Tsdb database, string sql)
    {
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, "EXPLAIN " + sql));
        return result.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);
    }
}
