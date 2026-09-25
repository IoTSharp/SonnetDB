using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Data;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlRecursiveCteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-recursive-cte-" + Guid.NewGuid().ToString("N"));

    public SqlRecursiveCteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Parse_RecursiveCte_PreservesDefinitionAndColumnNames()
    {
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(TreeSql));
        Assert.True(statement.IsRecursive);
        Assert.Equal(["id", "depth"], statement.CommonTableExpressions[0].ColumnNames);
        Assert.Equal(SqlSetOperationKind.UnionAll, statement.CommonTableExpressions[0].Query.SetOperationList[0].Kind);
    }

    [Fact]
    public void Execute_BranchingTree_ReturnsBreadthFirstRowsAndFinalPagination()
    {
        using var db = OpenTree();
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, TreeSql));
        Assert.Equal(["id", "depth"], result.Columns);
        Assert.Equal([(1L, 0L), (2L, 1L), (3L, 1L), (4L, 2L)],
            result.Rows.Select(row => (Assert.IsType<long>(row[0]), Assert.IsType<long>(row[1]))));

        var paged = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            TreeSql.Replace("ORDER BY id", "ORDER BY id DESC LIMIT 2 OFFSET 1", StringComparison.Ordinal)));
        Assert.Equal([3L, 2L], paged.Rows.Select(row => Assert.IsType<long>(row[0])));
    }

    [Fact]
    public void Execute_EmptyAnchorAndNamedParameter_ReturnsNoRows()
    {
        using var db = OpenTree();
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null,
            TreeSql.Replace("WHERE id = 1", "WHERE id = @root", StringComparison.Ordinal),
            new SqlParameters().AddNamed("root", 999L)));
        Assert.Equal(["id", "depth"], result.Columns);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Execute_EmptyAnchorWithWrongMemberWidth_ReportsColumnError()
    {
        using var db = OpenTree();
        var exception = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, """
            WITH RECURSIVE r (id) AS (
                SELECT id FROM devices WHERE id = 999
                UNION ALL
                SELECT id, id FROM r
            )
            SELECT id FROM r
            """));
        Assert.Contains("分支列数不一致", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_OrdinaryCtesBeforeAndAfterRecursiveDefinition_ResolveInOrder()
    {
        using var db = OpenTree();
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            WITH RECURSIVE roots AS (
                SELECT id FROM devices WHERE id = 1
            ), device_tree (id, depth) AS (
                SELECT id, 0 AS depth FROM roots
                UNION ALL
                SELECT child.id, device_tree.depth + 1 AS depth
                FROM devices AS child JOIN device_tree ON child.parent_id = device_tree.id
            ), leaves AS (
                SELECT id FROM device_tree WHERE depth >= 1
            )
            SELECT id FROM leaves ORDER BY id
            """));
        Assert.Equal([2L, 3L, 4L], result.Rows.Select(row => Assert.IsType<long>(row[0])));
    }

    [Fact]
    public void Execute_RecursiveAnchorForwardReferencesLaterCte_ReportsBoundary()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var exception = Assert.Throws<NotSupportedException>(() => SqlExecutor.Execute(db, """
            WITH RECURSIVE r (id) AS (
                SELECT id FROM later
                UNION ALL
                SELECT id FROM r
            ), later AS (SELECT 1 AS id)
            SELECT id FROM r
            """));
        Assert.Contains("不得前向引用后续普通 CTE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedAdo_RecursiveCte_ReadsTypedRows()
    {
        using var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE devices (id INT, parent_id INT, PRIMARY KEY (id))";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO devices (id, parent_id) VALUES (1, NULL), (2, 1), (3, 1), (4, 2)";
        command.ExecuteNonQuery();
        command.CommandText = TreeSql.Replace("WHERE id = 1", "WHERE id = @root", StringComparison.Ordinal);
        command.Parameters.AddWithValue("@root", 1L);
        using var reader = command.ExecuteReader();
        Assert.Equal("id", reader.GetName(0));
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
    }

    [Fact]
    public void Execute_CycleWithUnionDistinct_TerminatesAfterUniqueRows()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE edges (source INT, target INT, PRIMARY KEY (source, target))");
        SqlExecutor.Execute(db, "INSERT INTO edges (source, target) VALUES (1, 2), (2, 1)");
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            WITH RECURSIVE reach (id) AS (
                SELECT 1 AS id
                UNION
                SELECT edges.target FROM edges JOIN reach ON edges.source = reach.id
            )
            SELECT id FROM reach ORDER BY id
            """));
        Assert.Equal([1L, 2L], result.Rows.Select(row => Assert.IsType<long>(row[0])));
    }

    [Fact]
    public void Execute_CycleWithUnionAll_FailsAtDepthLimit()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var exception = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, """
            WITH RECURSIVE loop (id) AS (
                SELECT 1 AS id
                UNION ALL
                SELECT id FROM loop
            )
            SELECT id FROM loop
            """));
        Assert.Contains("最大层数", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ExponentialBranch_FailsAtCandidateProbeBeforeNextLevel()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE emit (id INT, parent_id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO emit (id, parent_id) VALUES (1, 1), (2, 1)");

        var exception = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, ExponentialBranchSql));
        Assert.Contains("单轮候选行数超过上限", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ExponentialBranch_CancellationStopsCandidateProbe()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE emit (id INT, parent_id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO emit (id, parent_id) VALUES (1, 1), (2, 1)");
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(10));

        var exception = Assert.Throws<RoutineExecutionException>(() => SqlExecutor.Execute(
            db, null, ExponentialBranchSql, null, null,
            new SqlExecutionOptions { CancellationToken = cancellation.Token }));
        Assert.IsType<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public void Execute_RecursiveNestedLoopJoinWideBuild_RejectsBeforeMaterialization()
    {
        using var db = OpenWideTable();
        const string sql = """
                WITH RECURSIVE r (payload) AS (
                    SELECT 'root' AS payload
                    UNION ALL
                    SELECT w.payload FROM r JOIN wide AS w ON w.id > 0 WHERE r.payload = 'root'
                )
                SELECT payload FROM r
                """;
        var exception = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db, null, sql, null, null,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 16 * 1024 }));
        Assert.Contains("单轮阻塞算子超过当前查询或数据库的内存预算", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db, null, sql, null, null,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 256 * 1024 }));
        Assert.Equal(4, result.Rows.Count);
        Assert.Equal("root", result.Rows[0][0]);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    [Fact]
    public void Execute_RecursiveOrdinaryCteWideSubquery_RejectsBeforeClone()
    {
        using var db = OpenWideTable();
        var exception = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db, null, """
                WITH RECURSIVE base AS (SELECT id, payload FROM wide), r (payload) AS (
                    SELECT 'root' AS payload
                    UNION ALL
                    SELECT b.payload FROM r JOIN base AS b ON b.id > 0 WHERE r.payload = 'root'
                )
                SELECT payload FROM r
                """, null, null,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 16 * 1024 }));
        Assert.Contains("单轮阻塞算子超过当前查询或数据库的内存预算", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    [Fact]
    public void Retain_WideRowAndCancellation_RejectBeforeKeepingMoreRows()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        using var cancellation = new CancellationTokenSource();
        using var resources = SqlQueryResources.EnterRoot(db,
            new SqlExecutionOptions { CancellationToken = cancellation.Token });
        using var budget = RecursiveCteBranchBudget.Enter();

        var wide = new object?[] { new string('x', 17 * 1024 * 1024) };
        var exception = Assert.Throws<InvalidOperationException>(() => budget.Retain(wide));
        Assert.Contains("单轮阻塞算子保留字节数超过上限", exception.Message, StringComparison.Ordinal);

        budget.Retain(["first"]);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => budget.Retain(["second"]));
    }

    [Fact]
    public void Retain_ConcurrentRows_EnforcesSingleQueryBudget()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        int accepted = 0;
        using (SqlQueryResources.EnterRoot(db,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 16 * 1024 }))
        using (var budget = RecursiveCteBranchBudget.Enter())
        {
            Parallel.For(0, 32, _ =>
            {
                try
                {
                    budget.Retain([new string('x', 1024)]);
                    Interlocked.Increment(ref accepted);
                }
                catch (InvalidOperationException)
                {
                    // 预算拒绝是此并发测试的预期结果。
                }
            });
            Assert.Equal(7, accepted);
        }
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    [Theory]
    [InlineData("WITH RECURSIVE r (a, b) AS (SELECT 1 AS a UNION ALL SELECT a FROM r) SELECT a FROM r", "输出列数")]
    [InlineData("WITH RECURSIVE r (a) AS (SELECT 1 AS a UNION ALL SELECT 'x' AS a FROM r) SELECT a FROM r", "类型不一致")]
    [InlineData("WITH RECURSIVE r (a) AS (SELECT 1 AS a UNION ALL SELECT a FROM r JOIN r AS other ON r.a = other.a) SELECT a FROM r", "一次直接自引用")]
    public void Execute_InvalidRecursiveShape_ReportsStableBoundary(string sql, string expected)
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        Exception exception = Record.Exception(() => SqlExecutor.Execute(db, sql))!;
        Assert.NotNull(exception);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explain_RecursiveCte_ReportsLimitsWithoutExecuting()
    {
        using var db = OpenTree();
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "EXPLAIN " + TreeSql));
        var plan = result.Rows.ToDictionary(row => Assert.IsType<string>(row[0]), row => row[1]);
        Assert.Equal("recursive_cte_worktable", plan["plan_node"]);
        Assert.Contains("最大层数 64", Assert.IsType<string>(plan["memory_behavior"]), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_PreCancelledRecursiveQuery_ThrowsCancellation()
    {
        using var db = OpenTree();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exception = Assert.Throws<RoutineExecutionException>(() => SqlExecutor.Execute(
            db, null, TreeSql, null, null, new SqlExecutionOptions { CancellationToken = cancellation.Token }));
        Assert.IsType<OperationCanceledException>(exception.InnerException);
    }

    private Tsdb OpenTree()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, parent_id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, parent_id) VALUES (1, NULL), (2, 1), (3, 1), (4, 2)");
        return db;
    }

    private Tsdb OpenWideTable()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE wide (id INT, payload STRING, PRIMARY KEY (id))");
        string payload = new('x', 4096);
        for (int id = 1; id <= 3; id++)
        {
            SqlExecutor.Execute(db, null,
                "INSERT INTO wide (id, payload) VALUES (@id, @payload)",
                new SqlParameters().AddNamed("id", id).AddNamed("payload", payload));
        }
        return db;
    }

    private const string TreeSql = """
        WITH RECURSIVE device_tree (id, depth) AS (
            SELECT id, 0 AS depth FROM devices WHERE id = 1
            UNION ALL
            SELECT child.id, device_tree.depth + 1 AS depth
            FROM devices AS child JOIN device_tree ON child.parent_id = device_tree.id
        )
        SELECT id, depth FROM device_tree ORDER BY id
        """;

    private const string ExponentialBranchSql = """
        WITH RECURSIVE r (id) AS (
            SELECT 1 AS id
            UNION ALL
            SELECT emit.parent_id FROM emit JOIN r ON emit.parent_id = r.id
        )
        SELECT id FROM r
        """;
}
