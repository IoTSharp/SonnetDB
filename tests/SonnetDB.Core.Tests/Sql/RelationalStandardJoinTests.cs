using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证关系表 RIGHT/FULL/CROSS JOIN 的解析与有界执行语义。</summary>
public sealed class RelationalStandardJoinTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sndb-standard-join-{Guid.NewGuid():N}");

    public RelationalStandardJoinTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 测试临时目录清理失败不应掩盖断言结果。
        }
    }

    [Fact]
    public void Parse_StandardJoinKinds_PreservesKindAndCrossPredicate()
    {
        var right = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT l.id FROM left_rows l RIGHT OUTER JOIN right_rows r ON l.id = r.id"));
        var full = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT l.id FROM left_rows l FULL JOIN right_rows r ON l.id = r.id"));
        var cross = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT l.id FROM left_rows l CROSS JOIN right_rows r"));

        Assert.Equal(JoinKind.Right, Assert.Single(right.JoinClauses).Kind);
        Assert.Equal(JoinKind.Full, Assert.Single(full.JoinClauses).Kind);
        var crossJoin = Assert.Single(cross.JoinClauses);
        Assert.Equal(JoinKind.Cross, crossJoin.Kind);
        var predicate = Assert.IsType<LiteralExpression>(crossJoin.On);
        Assert.Equal(SqlLiteralKind.Boolean, predicate.Kind);
        Assert.True(predicate.BooleanValue);
    }

    [Fact]
    public void Execute_RightJoin_PreservesUnmatchedRightRows()
    {
        using var database = CreateDatabase();

        var result = Execute(database, """
            SELECT l.id, r.id
            FROM left_rows l
            RIGHT JOIN right_rows r ON l.id = r.id
            """);

        Assert.Equal(2, result.Rows.Count);
        Assert.Contains(result.Rows, static row => Equals(row[0], 2L) && Equals(row[1], 2L));
        Assert.Contains(result.Rows, static row => row[0] is null && Equals(row[1], 3L));
    }

    [Fact]
    public void Execute_FullJoin_PreservesUnmatchedRowsOnBothSides()
    {
        using var database = CreateDatabase();

        var result = Execute(database, """
            SELECT l.id, r.id
            FROM left_rows l
            FULL OUTER JOIN right_rows r ON l.id = r.id
            """);

        Assert.Equal(3, result.Rows.Count);
        Assert.Contains(result.Rows, static row => Equals(row[0], 1L) && row[1] is null);
        Assert.Contains(result.Rows, static row => Equals(row[0], 2L) && Equals(row[1], 2L));
        Assert.Contains(result.Rows, static row => row[0] is null && Equals(row[1], 3L));
    }

    [Fact]
    public void Execute_CrossJoin_ReturnsCartesianProduct()
    {
        using var database = CreateDatabase();

        var result = Execute(database, """
            SELECT l.id, r.id
            FROM left_rows l
            CROSS JOIN right_rows r
            """);

        Assert.Equal(4, result.Rows.Count);
        Assert.Equal(
            4,
            result.Rows.Select(static row => $"{row[0]}:{row[1]}").Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>规范化 AST 后执行和 EXPLAIN 都保留 measurement 的标准外连接拒绝边界。</summary>
    [Theory]
    [InlineData("LEFT")]
    [InlineData("RIGHT")]
    [InlineData("FULL")]
    [InlineData("CROSS")]
    public void ExecuteAndExplain_MeasurementStandardJoin_RejectWithModelDiagnostic(string kind)
    {
        using var database = CreateDatabase();
        SqlExecutor.Execute(database, "CREATE MEASUREMENT samples (source TAG, value FIELD FLOAT)");
        string predicate = kind == "CROSS" ? string.Empty : " ON s.source = r.id";
        string sql = $"SELECT r.id FROM samples s {kind} JOIN right_rows r{predicate}";
        var execute = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database, sql));
        var explain = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database, "EXPLAIN " + sql));
        Assert.Contains("当前仅支持关系表 FROM", execute.Message, StringComparison.Ordinal);
        Assert.Equal(execute.Message, explain.Message);
    }

    /// <summary>读取规范化 JOIN 列表仍能执行原有 INNER measurement 维表的匹配行路径。</summary>
    [Fact]
    public void Execute_MeasurementInnerJoinWithParameter_PreservesSupportedPath()
    {
        using var database = CreateDatabase();
        SqlExecutor.Execute(database, "CREATE MEASUREMENT samples (source TAG, value FIELD FLOAT)");
        SqlExecutor.Execute(database, "CREATE TABLE sources (id STRING, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "INSERT INTO sources (id, name) VALUES ('a', 'source-a')");
        SqlExecutor.Execute(database, "INSERT INTO samples (time, source, value) VALUES (1000, 'a', 1.5)");
        const string sql = "SELECT s.time, r.name FROM samples s INNER JOIN sources r ON s.source = r.id WHERE s.source = @source";
        var parameters = new SqlParameters().AddNamed("source", "a");
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database, databaseName: null, sql, parameters, controlPlane: null));
        Assert.Equal(new object?[] { 1000L, "source-a" }, Assert.Single(result.Rows));
        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database, databaseName: null, "EXPLAIN " + sql, parameters, controlPlane: null));
        Assert.Contains(explain.Rows, static row => Equals(row[0], "statement_type")
            && Equals(row[1], "select_join"));
    }

    private Tsdb CreateDatabase()
    {
        var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE TABLE left_rows (id INT NOT NULL, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "CREATE TABLE right_rows (id INT NOT NULL, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "INSERT INTO left_rows (id) VALUES (1), (2)");
        SqlExecutor.Execute(database, "INSERT INTO right_rows (id) VALUES (2), (3)");
        return database;
    }

    private static SelectExecutionResult Execute(Tsdb database, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql));
}
