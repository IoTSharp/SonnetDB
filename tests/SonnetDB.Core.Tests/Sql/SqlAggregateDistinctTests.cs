using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// 验证标准聚合函数的 DISTINCT 输入去重、NULL 处理和错误边界。
/// </summary>
public sealed class SqlAggregateDistinctTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-sql-aggregate-distinct-" + Guid.NewGuid().ToString("N"));

    public SqlAggregateDistinctTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* 测试清理失败不覆盖原始断言结果。 */ }
    }

    [Fact]
    public void RelationalAggregates_DistinctValues_IgnoreDuplicatesAndNulls()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database,
            "CREATE TABLE readings (id INT, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO readings (id, value) VALUES (1, 1), (2, 1), (3, 2), (4, NULL)");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, """
            SELECT count(DISTINCT value),
                   sum(DISTINCT value),
                   avg(DISTINCT value),
                   min(DISTINCT value),
                   max(DISTINCT value)
            FROM readings
            """));

        Assert.Equal(new object?[] { 2L, 3L, 1.5d, 1L, 2L }, Assert.Single(result.Rows));
    }

    [Fact]
    public void MeasurementAggregates_DistinctValues_RespectSeriesFilter()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database,
            "CREATE MEASUREMENT readings (device TAG, value FIELD INT)");
        SqlExecutor.Execute(database, """
            INSERT INTO readings (time, device, value)
            VALUES (1000, 'a', 1), (2000, 'a', 1), (3000, 'a', 2),
                   (1000, 'b', 2), (2000, 'b', 3)
            """);

        var parsed = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT count(DISTINCT value), sum(DISTINCT value) FROM readings WHERE device = 'a'"));
        Assert.Empty(parsed.GroupBy);

        var first = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
            "SELECT count(DISTINCT value), sum(DISTINCT value) FROM readings WHERE device = 'a'"));
        Assert.Equal(new object?[] { 2L, 3d }, Assert.Single(first.Rows));

        var second = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
            "SELECT count(DISTINCT value), sum(DISTINCT value) FROM readings WHERE device = 'b'"));
        Assert.Equal(new object?[] { 2L, 5d }, Assert.Single(second.Rows));
    }

    [Fact]
    public void AggregateDistinct_StarArgument_IsRejected()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database,
            "CREATE TABLE readings (id INT, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "INSERT INTO readings (id, value) VALUES (1, 1)");

        var exception = Assert.Throws<SqlParseException>(() =>
            SqlExecutor.Execute(database, "SELECT count(DISTINCT *) FROM readings"));
        Assert.Contains("DISTINCT", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregateDistinct_ParserStoresFunctionModifier()
    {
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT count(DISTINCT value) FROM readings"));
        var function = Assert.IsType<FunctionCallExpression>(
            statement.Projections.Single().Expression);

        Assert.True(function.IsDistinct);
        Assert.False(function.IsStar);
        Assert.Equal(new IdentifierExpression("value"), Assert.Single(function.Arguments));
    }

}
