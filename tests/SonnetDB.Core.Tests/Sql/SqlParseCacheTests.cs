using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// <see cref="SqlParser"/> 解析缓存（#212）行为测试。
/// </summary>
public sealed class SqlParseCacheTests
{
    [Fact]
    public void Parse_SameSqlTwice_ReturnsSameCachedInstance()
    {
        SqlParser.ClearParseCache();
        const string sql = "SELECT * FROM cpu WHERE host = 'h1'";

        var first = SqlParser.Parse(sql);
        var second = SqlParser.Parse(sql);

        // 命中缓存：同一不可变 AST 实例被复用（引用相等）。
        Assert.Same(first, second);
    }

    [Fact]
    public void Parse_DifferentSql_ReturnsDistinctInstances()
    {
        SqlParser.ClearParseCache();

        var a = SqlParser.Parse("SELECT * FROM cpu");
        var b = SqlParser.Parse("SELECT * FROM mem");

        Assert.NotSame(a, b);
        Assert.IsType<SelectStatement>(a);
        Assert.IsType<SelectStatement>(b);
    }

    [Fact]
    public void Parse_InvalidSql_IsNotCached_AndKeepsThrowing()
    {
        SqlParser.ClearParseCache();
        const string bad = "SELECT FROM WHERE ???";

        Assert.Throws<SqlParseException>(() => SqlParser.Parse(bad));
        // 第二次仍抛（未把失败结果缓存成功值）。
        Assert.Throws<SqlParseException>(() => SqlParser.Parse(bad));
    }

    [Fact]
    public void Parse_CachedAst_IsEquivalentToFreshParse()
    {
        SqlParser.ClearParseCache();
        const string sql = "SELECT host, avg(value) FROM cpu WHERE time >= 1000 GROUP BY time(1m)";

        var cached = SqlParser.Parse(sql);  // 入缓存
        var again = SqlParser.Parse(sql);   // 命中缓存

        // 结构等价（record 值相等）且引用相同。
        Assert.Equal(cached, again);
        Assert.Same(cached, again);
    }
}
