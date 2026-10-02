using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Graphs;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证直接原生图 SELECT 的累计物化、分页、恢复及拒绝边界。</summary>
public sealed class GraphSelectMaterializationBudgetTests : IDisposable
{
    private const string NativeMatch = """
        FROM GRAPH_TABLE (
            topology
            MATCH (a IS 1)-[e IS 2]->(b IS 3)
            WHERE a.id = 1
            COLUMNS (b.id AS TargetID, b.property_7 AS Title, b.property_8 AS Payload)
        )
        """;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sdb-graphb-{Guid.NewGuid():N}");

    /// <summary>顶点和边查询在失败前沿停止投影，释放根保留而不返回成功前缀。</summary>
    [Theory]
    [InlineData("graph_nodes('topology', 3)")]
    [InlineData("graph_edges('topology', 2)")]
    public void Execute_ManyGraphRowsOverBudget_StopsAtFailureFrontier(string source)
    {
        using Tsdb db = OpenGraph(1024);
        var metrics = new SqlExecutionMetrics();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Select(db,
            $"SELECT id FROM {source}", new SqlExecutionOptions { MaxMaterializedRows = 2, Metrics = metrics }));

        Assert.Contains("累计物化", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, metrics.Complete().CandidateRows);
        AssertReleased(db);
        Assert.Equal(1024, Select(db, $"SELECT id FROM {source}").Rows.Count);
    }

    /// <summary>WHERE 和 OFFSET 后保留的完整 SQL 页计费，支持两种分页语法。</summary>
    [Theory]
    [InlineData("SELECT id FROM graph_nodes('topology', 3) WHERE id >= 4 LIMIT 2 OFFSET 3")]
    [InlineData("SELECT id FROM graph_nodes('topology', 3) WHERE id >= 4 OFFSET 3 ROWS FETCH NEXT 2 ROWS ONLY")]
    [InlineData("SELECT id FROM graph_edges('topology', 2) WHERE id >= 1002 LIMIT 2 OFFSET 3")]
    [InlineData("SELECT TargetID " + NativeMatch + " WHERE targetid >= 4 LIMIT 2 OFFSET 3")]
    public void Execute_FilterAndPagination_ChargesOnlyRetainedPage(string sql)
    {
        using Tsdb db = OpenGraph(10);

        SelectExecutionResult result = Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 2, MaxMaterializedBytes = 192 });

        Assert.Equal(2, result.Rows.Count);
        AssertEquivalent(Select(db, sql), result);
        AssertReleased(db);
    }

    /// <summary>单行 UTF-16 字符串按既有估算精确收费，等号允许且少一字节拒绝。</summary>
    [Theory]
    [InlineData("SELECT id, '雪😀' AS Title FROM graph_nodes('topology', 3) LIMIT 1", 134)]
    [InlineData("SELECT title " + NativeMatch + " LIMIT 1", 102)]
    [InlineData("SELECT payload " + NativeMatch + " LIMIT 1", 99)]
    public void Execute_StringAndBlobProjection_EnforcesExactEstimatedBytes(string sql, long bytes)
    {
        using Tsdb db = OpenGraph(1);

        Assert.Single(Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 1, MaxMaterializedBytes = bytes }).Rows);
        Assert.Throws<InvalidOperationException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 1, MaxMaterializedBytes = bytes - 1 }));
        AssertReleased(db);
    }

    /// <summary>原生一跳 GRAPH_TABLE 逐行向根预算收费，拒绝后仍能执行完整默认查询。</summary>
    [Fact]
    public void Execute_NativeOneHopOverRowBudget_ReleasesSnapshotAndBudget()
    {
        using Tsdb db = OpenGraph(100);
        var metrics = new SqlExecutionMetrics();
        string sql = "SELECT TargetID " + NativeMatch;

        Assert.Throws<InvalidOperationException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 2, Metrics = metrics }));

        Assert.Equal(3, metrics.Complete().CandidateRows);
        AssertReleased(db);
        Assert.Equal(100, Select(db, sql).Rows.Count);
    }

    /// <summary>LIMIT 0 不拉取候选，极小预算返回完整空结果。</summary>
    [Theory]
    [InlineData("SELECT * FROM graph_nodes('topology') LIMIT 0")]
    [InlineData("SELECT * FROM graph_edges('topology') LIMIT 0")]
    [InlineData("SELECT * " + NativeMatch + " LIMIT 0")]
    public void Execute_ZeroLimit_DoesNotPullCandidates(string sql)
    {
        using Tsdb db = OpenGraph(10);
        var metrics = new SqlExecutionMetrics();

        SelectExecutionResult result = Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedBytes = 1, Metrics = metrics });

        Assert.Empty(result.Rows);
        Assert.False(result.Truncated);
        Assert.Equal(0, metrics.Complete().CandidateRows);
        AssertReleased(db);
    }

    /// <summary>保留 GRAPH_TABLE 创建的原始结果名称，并按引号精确校验它们。</summary>
    [Fact]
    public void Execute_GraphTableMixedCaseAliases_PreservesNamesAndQuotedLookup()
    {
        using Tsdb db = OpenGraph(1);
        var options = new SqlExecutionOptions { MaxMaterializedRows = 1 };

        SelectExecutionResult result = Select(db, "SELECT * " + NativeMatch, options);

        Assert.Equal(["TargetID", "Title", "Payload"], result.Columns);
        Assert.Single(Select(db, "SELECT \"TargetID\" " + NativeMatch, options).Rows);
        Assert.Single(Select(db, "SELECT targetid " + NativeMatch, options).Rows);
        Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT \"targetid\" " + NativeMatch, options));
        AssertReleased(db);
    }

    /// <summary>原生合成列和来源限定符按未引号忽略大小写、引号精确匹配解析。</summary>
    [Theory]
    [InlineData("SELECT \"id\" FROM graph_nodes('topology') LIMIT 1", true)]
    [InlineData("SELECT \"ID\" FROM graph_nodes('topology') LIMIT 1", false)]
    [InlineData("SELECT graphrows.TargetID " + NativeMatch + " AS GraphRows LIMIT 1", true)]
    [InlineData("SELECT \"GraphRows\".\"TargetID\" " + NativeMatch + " AS GraphRows LIMIT 1", true)]
    [InlineData("SELECT \"graphrows\".TargetID " + NativeMatch + " AS GraphRows LIMIT 1", false)]
    [InlineData("SELECT id FROM GRAPH_TABLE (topology MATCH (a IS 1)-[e IS 2]->(b IS 3) COLUMNS (b.\"id\" AS id)) LIMIT 1", true)]
    [InlineData("SELECT id FROM GRAPH_TABLE (topology MATCH (a IS 1)-[e IS 2]->(b IS 3) COLUMNS (b.\"ID\" AS id)) LIMIT 1", false)]
    [InlineData("SELECT id FROM GRAPH_TABLE (topology MATCH (a IS 1)-[e IS 2]->(b IS 3) COLUMNS (\"B\".id AS id)) LIMIT 1", false)]
    public void Execute_QuotedSourceIdentifiers_RequiresExactSpelling(string sql, bool succeeds)
    {
        using Tsdb db = OpenGraph(1);
        var metrics = new SqlExecutionMetrics();
        var options = new SqlExecutionOptions { MaxMaterializedRows = 1, Metrics = metrics };

        if (succeeds)
            Assert.Single(Select(db, sql, options).Rows);
        else
        {
            Assert.Throws<InvalidOperationException>(() => Select(db, sql, options));
            Assert.Equal(0, metrics.Complete().CandidateRows);
        }
        AssertReleased(db);
    }

    /// <summary>排序、聚合、窗口、嵌套及非标准值在候选读取前拒绝。</summary>
    [Theory]
    [InlineData("SELECT id FROM graph_nodes('topology') ORDER BY id LIMIT 1")]
    [InlineData("SELECT DISTINCT id FROM graph_nodes('topology')")]
    [InlineData("SELECT COUNT(*) FROM graph_nodes('topology')")]
    [InlineData("SELECT ROW_NUMBER() OVER (ORDER BY id) FROM graph_nodes('topology')")]
    [InlineData("SELECT id FROM graph_nodes('topology') WHERE EXISTS (SELECT 1)")]
    [InlineData("SELECT id FROM graph_nodes('topology') WHERE id IN (SELECT 1)")]
    [InlineData("SELECT id FROM (SELECT id FROM graph_nodes('topology')) AS source")]
    [InlineData("WITH source AS (SELECT id FROM graph_nodes('topology')) SELECT id FROM source")]
    [InlineData("SELECT id FROM graph_nodes('topology') UNION ALL SELECT 1")]
    [InlineData("SELECT 1 UNION ALL SELECT id FROM graph_nodes('topology')")]
    [InlineData("SELECT [1, 2] FROM graph_nodes('topology')")]
    [InlineData("SELECT COUNT(*) " + NativeMatch)]
    [InlineData("SELECT targetid " + NativeMatch + " ORDER BY targetid")]
    [InlineData("SELECT targetid " + NativeMatch + " WHERE EXISTS (SELECT 1)")]
    [InlineData("SELECT id FROM GRAPH_TABLE (topology MATCH (a IS 1)-[e IS 2]->(b IS 3) COLUMNS ((SELECT 1) AS id))")]
    [InlineData("SELECT id FROM GRAPH_TABLE (topology MATCH (a IS 1)-[e IS 2]->{1,2}(b IS 3) COLUMNS (b.id AS id))")]
    public void Execute_UnsupportedGraphShape_RejectsBeforeCandidateRead(string sql)
    {
        using Tsdb db = OpenGraph(4);
        var metrics = new SqlExecutionMetrics();

        Assert.Throws<NotSupportedException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 100, Metrics = metrics }));

        Assert.Equal(0, metrics.Complete().CandidateRows);
        AssertReleased(db);
    }

    /// <summary>含图源的视图仍拒绝，不能通过展开获得直接图查询准入。</summary>
    [Fact]
    public void Execute_GraphView_RejectsBeforeCandidateRead()
    {
        using Tsdb db = OpenGraph(2);
        SqlExecutor.Execute(db, "CREATE VIEW graph_view AS SELECT TargetID " + NativeMatch);
        var metrics = new SqlExecutionMetrics();

        Assert.Throws<NotSupportedException>(() => Select(db, "SELECT TargetID FROM graph_view",
            new SqlExecutionOptions { MaxMaterializedRows = 100, Metrics = metrics }));

        Assert.Equal(0, metrics.Complete().CandidateRows);
        AssertReleased(db);
    }

    /// <summary>用户回调在外层及 MATCH COLUMNS 中均拒绝，默认调用仍保留原行为。</summary>
    [Fact]
    public void Execute_UserFunction_RejectsWithoutCallingCallback()
    {
        using Tsdb db = OpenGraph(2);
        int called = 0;
        db.Functions.RegisterScalar("graph_callback", _ => { called++; return 7L; }, 0);
        var options = new SqlExecutionOptions { MaxMaterializedRows = 100 };

        Assert.Throws<NotSupportedException>(() => Select(db,
            "SELECT graph_callback() FROM graph_nodes('topology')", options));
        Assert.Throws<NotSupportedException>(() => Select(db,
            "SELECT id FROM GRAPH_TABLE (topology MATCH (a IS 1)-[e IS 2]->(b IS 3) COLUMNS (graph_callback() AS id))", options));

        Assert.Equal(0, called);
        AssertReleased(db);
    }

    /// <summary>EXPLAIN 的元数据以及 ANALYZE 的真实结果共用累计根预算。</summary>
    [Theory]
    [InlineData("EXPLAIN SELECT id FROM graph_nodes('topology')", 0)]
    [InlineData("EXPLAIN SELECT TargetID " + NativeMatch, 0)]
    [InlineData("EXPLAIN ANALYZE SELECT TargetID " + NativeMatch, 2)]
    public void Execute_ExplainResults_ChargeSharedRootBudget(string sql, int actualRows)
    {
        using Tsdb db = OpenGraph(2);
        int explainRows = Select(db, sql).Rows.Count;

        Assert.Equal(explainRows, Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = explainRows + actualRows }).Rows.Count);
        Assert.Throws<InvalidOperationException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = explainRows + actualRows - 1 }));
        AssertReleased(db);
    }

    /// <summary>取消和已过期截止时间不拉取结果，后续调用恢复独立预算。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_CancelledRoot_ReleasesBudgetAndAllowsNextQuery(bool expiredDeadline)
    {
        using Tsdb db = OpenGraph(10);
        using var cancellation = new CancellationTokenSource();
        if (!expiredDeadline)
            cancellation.Cancel();
        var options = new SqlExecutionOptions
        {
            MaxMaterializedRows = 1,
            CancellationToken = cancellation.Token,
            DeadlineUtc = expiredDeadline ? DateTimeOffset.UtcNow.AddSeconds(-1) : null,
        };

        RoutineExecutionException error = Assert.Throws<RoutineExecutionException>(() => Select(db,
            "SELECT TargetID " + NativeMatch, options));

        Assert.Equal("routine_cancelled", error.Code);
        AssertReleased(db);
        Assert.Single(Select(db, "SELECT TargetID " + NativeMatch + " LIMIT 1",
            new SqlExecutionOptions { MaxMaterializedRows = 1 }).Rows);
    }

    /// <summary>预算失败不改变图持久数据，重开后默认排序和原生读取仍完整可用。</summary>
    [Fact]
    public void Execute_BudgetFailure_PreservesGraphAcrossReopen()
    {
        using (Tsdb db = OpenGraph(10))
        {
            Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT TargetID " + NativeMatch,
                new SqlExecutionOptions { MaxMaterializedRows = 2 }));
            AssertReleased(db);
        }
        using Tsdb reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });

        SelectExecutionResult result = Select(reopened, "SELECT id FROM graph_edges('topology') ORDER BY id DESC");
        Assert.Equal(10, result.Rows.Count);
        Assert.Equal(1009L, result.Rows[0][0]);
        using GraphReadSession read = reopened.Graphs.Open("topology").BeginRead();
        Assert.NotNull(read.GetVertex(new GraphElementId(11)));
        Assert.NotNull(read.GetEdge(new GraphElementId(1009)));
        AssertReleased(reopened);
    }

    /// <summary>清理本测试创建且已核实路径的独占数据库目录。</summary>
    public void Dispose()
    {
        string target = Path.GetFullPath(_root);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetDirectoryName(target));
        Assert.StartsWith("sdb-graphb-", Path.GetFileName(target), StringComparison.Ordinal);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }

    private Tsdb OpenGraph(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 1024);
        long startedAt = Stopwatch.GetTimestamp();
        Tsdb db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        GraphStore store = db.Graphs.Create("topology");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertVertex(new GraphElementId(1), 0, [new LabelId(1)], []);
        for (int index = 0; index < count; index++)
        {
            Assert.True(Stopwatch.GetElapsedTime(startedAt) < TimeSpan.FromSeconds(30));
            transaction.UpsertVertex(new GraphElementId(index + 2), 0, [new LabelId(3)],
                [new GraphProperty(7, GraphPropertyValue.FromString("雪😀")),
                    new GraphProperty(8, GraphPropertyValue.FromBlob([1, 2, 3]))]);
            transaction.UpsertEdge(new GraphElementId(index + 1000), 0, new GraphElementId(1),
                new GraphElementId(index + 2), new LabelId(2), []);
        }
        transaction.Commit();
        return db;
    }

    private static SelectExecutionResult Select(Tsdb db, string sql, SqlExecutionOptions? options = null)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, sql, null, null,
            options ?? SqlExecutionOptions.Default));

    private static void AssertReleased(Tsdb db)
    {
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        Assert.False(SqlRowRetentionBudget.HasExecutionBudget);
    }

    private static void AssertEquivalent(SelectExecutionResult expected, SelectExecutionResult actual)
    {
        Assert.Equal(expected.Columns, actual.Columns);
        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        for (int index = 0; index < expected.Rows.Count; index++)
            Assert.Equal(expected.Rows[index], actual.Rows[index]);
        Assert.False(actual.Truncated);
    }
}
