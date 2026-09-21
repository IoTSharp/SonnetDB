using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// 验证 BETWEEN 范围谓词与 ILIKE 大小写不敏感模式匹配的 SQL 合同。
/// </summary>
public sealed class SqlBetweenIlikeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-sql-between-ilike-" + Guid.NewGuid().ToString("N"));

    public SqlBetweenIlikeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* 测试清理失败不覆盖原始断言结果。 */ }
    }

    [Fact]
    public void BetweenAndNotBetween_FilterInclusiveRangeAndExcludeNull()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database,
            "CREATE TABLE readings (id INT, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO readings (id, value) VALUES (1, 1), (2, 5), (3, 10), (4, NULL)");

        var inside = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
            "SELECT id FROM readings WHERE value BETWEEN 5 AND 10 ORDER BY id"));
        Assert.Equal(new object?[] { 2L }, inside.Rows[0]);
        Assert.Equal(new object?[] { 3L }, inside.Rows[1]);

        var outside = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
            "SELECT id FROM readings WHERE value NOT BETWEEN 5 AND 10 ORDER BY id"));
        Assert.Equal(new object?[] { 1L }, outside.Rows.Single());
    }

    [Fact]
    public void IlikeAndNotIlike_MatchOrdinalInvariantCase()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database,
            "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO devices (id, name) VALUES "
            + "(1, 'Pump-East'), (2, 'pump-west'), (3, 'Valve-East'), (4, NULL)");

        var matching = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
            "SELECT id FROM devices WHERE name ILIKE 'PUMP-%' ORDER BY id"));
        Assert.Equal(new object?[] { 1L }, matching.Rows[0]);
        Assert.Equal(new object?[] { 2L }, matching.Rows[1]);

        var notMatching = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
            "SELECT id FROM devices WHERE name NOT ILIKE '%EAST%' ORDER BY id"));
        Assert.Equal(new object?[] { 2L }, notMatching.Rows.Single());
    }

    [Fact]
    public void Parse_BetweenAndIlike_ProducesSupportedExpressions()
    {
        var between = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT id FROM readings WHERE value NOT BETWEEN 1 AND 2"));
        Assert.NotNull(between.Where);

        var ilike = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT id FROM devices WHERE name ILIKE 'pump%'"));
        Assert.NotNull(ilike.Where);
    }
}
