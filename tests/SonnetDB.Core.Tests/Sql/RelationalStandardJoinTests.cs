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
