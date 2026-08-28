using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 M41 #378 Hash Join build side 的成本选择和外连接语义。</summary>
public sealed class RelationalJoinBuildSideTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sndb-relational-join-side-{Guid.NewGuid():N}");

    /// <summary>左侧输入更小时应在左侧建 Hash，并流式探测较大的右侧输入。</summary>
    [Fact]
    public void Execute_InnerJoinWithSmallerLeftInput_BuildsLeftHash()
    {
        using Tsdb database = CreateDatabase();

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT l.id, r.id
            FROM join_small l
            JOIN join_large r ON l.id = r.small_id
            ORDER BY r.id
            """);

        Assert.Equal(6, result.Rows.Count);
        Assert.Equal("left", metrics.LastHashJoinBuildSide);
        Assert.Equal(1, metrics.HashJoinLeftBuildCount);
        Assert.Equal(0, metrics.HashJoinRightBuildCount);
        Assert.Equal(2, metrics.LastHashJoinEstimatedBuildRows);
        Assert.Equal(2, metrics.LastHashJoinActualBuildRows);
        Assert.Equal(6, metrics.LastHashJoinActualProbeRows);
    }

    /// <summary>行数相同时应把投影后的估算行宽纳入 build side 成本。</summary>
    [Fact]
    public void Execute_InnerJoinWithEqualRowsAndNarrowerLeftInput_BuildsLeftHash()
    {
        using Tsdb database = CreateDatabase();

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT l.id, w.payload
            FROM join_small l
            JOIN join_wide w ON l.id = w.small_id
            ORDER BY l.id
            """);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("left", metrics.LastHashJoinBuildSide);
        Assert.Equal(2, metrics.LastHashJoinActualBuildRows);
        Assert.True(metrics.LastHashJoinEstimatedBuildBytes < 64);
    }

    /// <summary>右侧输入更小时应保留右侧 build 和左侧流式 probe。</summary>
    [Fact]
    public void Execute_InnerJoinWithSmallerRightInput_BuildsRightHash()
    {
        using Tsdb database = CreateDatabase();

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT r.id, l.id
            FROM join_large l
            JOIN join_small r ON l.small_id = r.id
            ORDER BY l.id
            """);

        Assert.Equal(6, result.Rows.Count);
        Assert.Equal("right", metrics.LastHashJoinBuildSide);
        Assert.Equal(0, metrics.HashJoinLeftBuildCount);
        Assert.Equal(1, metrics.HashJoinRightBuildCount);
        Assert.Equal(2, metrics.LastHashJoinActualBuildRows);
        Assert.Equal(6, metrics.LastHashJoinActualProbeRows);
    }

    /// <summary>LEFT JOIN 不得为了更小的 build 输入而破坏未匹配左行。</summary>
    [Fact]
    public void Execute_LeftJoinWithSmallerLeftInput_BuildsRightAndPreservesUnmatchedRows()
    {
        using Tsdb database = CreateDatabase();
        SqlExecutor.Execute(database, "INSERT INTO join_small (id) VALUES (99)");

        (SelectExecutionResult result, RelationalSelectExecutionMetrics metrics) = Execute(database, """
            SELECT l.id, r.id
            FROM join_small l
            LEFT JOIN join_large r ON l.id = r.small_id
            ORDER BY l.id, r.id
            """);

        Assert.Equal(7, result.Rows.Count);
        Assert.Equal(new object?[] { 99L, null }, result.Rows[^1]);
        Assert.Equal("right", metrics.LastHashJoinBuildSide);
        Assert.Equal(6, metrics.LastHashJoinActualBuildRows);
        Assert.Equal(3, metrics.LastHashJoinActualProbeRows);
    }

    /// <summary>EXPLAIN 应报告与运行时一致的 build side、估算规模和阻塞边界。</summary>
    [Fact]
    public void Explain_InnerJoinWithSmallerLeftInput_ReportsLeftHashBuild()
    {
        using Tsdb database = CreateDatabase();

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, """
            EXPLAIN SELECT l.id, r.id
            FROM join_small l
            JOIN join_large r ON l.id = r.small_id
            """));
        var plan = result.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);

        Assert.Equal("hash_join", plan["plan_node"]);
        Assert.Contains("join_1:hash_join(build=left,rows=2,bytes=16)", (string)plan["access_path"]!);
        Assert.Contains(
            "join_1=left_input_blocking(hash_build),right_input=streaming_probe",
            (string)plan["memory_behavior"]!);
        Assert.Equal(6L, Convert.ToInt64(plan["estimated_output_rows"]));
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

    private Tsdb CreateDatabase()
    {
        Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE TABLE join_small (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, """
            CREATE TABLE join_large (
                id INT,
                small_id INT,
                PRIMARY KEY (id))
            """);
        SqlExecutor.Execute(database, """
            CREATE TABLE join_wide (
                id INT,
                small_id INT,
                payload STRING,
                PRIMARY KEY (id))
            """);
        SqlExecutor.Execute(database, "INSERT INTO join_small (id) VALUES (1), (2)");
        SqlExecutor.Execute(database, """
            INSERT INTO join_large (id, small_id) VALUES
                (10, 1), (11, 1), (12, 1), (20, 2), (21, 2), (22, 2)
            """);
        SqlExecutor.Execute(database, """
            INSERT INTO join_wide (id, small_id, payload) VALUES
                (10, 1, 'a-wide-payload'), (20, 2, 'another-wide-payload')
            """);
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
}
