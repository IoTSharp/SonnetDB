using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>GH-Issue #196：参数重写与 measurement JOIN 使用同一 AST 合同。</summary>
public sealed class MeasurementJoinContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-measurement-join-core-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(30));

    /// <summary>回收当前测试的取消令牌与唯一数据目录。</summary>
    public void Dispose()
    {
        _deadline.Dispose();
        string root = Path.GetFullPath(_root);
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.StartsWith(temporaryRoot, root, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("sndb-measurement-join-core-", Path.GetFileName(root), StringComparison.Ordinal);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    /// <summary>WHERE、ON、投影与分页参数重写均保留规范化后的 JOIN 列表。</summary>
    [Fact]
    public void Bind_ParametersAcrossJoinStatement_PreservesNormalizedJoinAndOriginalAst()
    {
        const string sql = """
            SELECT a.time, @label AS label FROM sonnet_metric a
            INNER JOIN sonnet_device b ON a.Host = b.Name AND b.Id = @device_id
            WHERE a.time >= @begin ORDER BY a.time OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
            """;
        var original = Assert.IsType<SelectStatement>(SqlParser.Parse(sql));
        var parameters = new SqlParameters().AddNamed("label", "bound").AddNamed("device_id", 1L)
            .AddNamed("begin", 1500L).AddNamed("skip", 1L).AddNamed("take", 2L);
        var bound = Assert.IsType<SelectStatement>(SqlParameterBinder.Bind(original, parameters));
        Assert.Null(bound.Join);
        JoinClause join = Assert.Single(bound.JoinClauses);
        Assert.Equal(JoinKind.Inner, join.Kind);
        var conjunction = Assert.IsType<BinaryExpression>(join.On);
        var originalConjunction = Assert.IsType<BinaryExpression>(Assert.Single(original.JoinClauses).On);
        Assert.IsType<ParameterExpression>(Assert.IsType<BinaryExpression>(originalConjunction.Right).Right);
        Assert.Equal(1L, Assert.IsType<LiteralExpression>(Assert.IsType<BinaryExpression>(conjunction.Right).Right).IntegerValue);
        Assert.Equal(1500L, Assert.IsType<LiteralExpression>(Assert.IsType<BinaryExpression>(bound.Where).Right).IntegerValue);
        Assert.Equal("bound", Assert.IsType<LiteralExpression>(bound.Projections[1].Expression).StringValue);
        Assert.Equal(1, bound.Pagination!.Offset);
        Assert.Equal(2, bound.Pagination.Fetch);
    }

    /// <summary>原 issue 的带引号 SQL 经参数绑定返回全部匹配的时序行及字段。</summary>
    [Fact]
    public void Execute_QuotedMeasurementJoinWithWhereParameters_ReturnsMultipleRowsAndExplain()
    {
        using var database = CreateDatabase();
        const string sql = """
            SELECT a.time, a."Host", b."Id", a."Value", @label AS marker
            FROM "sonnet_metric" AS a INNER JOIN "sonnet_device" AS b ON a."Host" = b."Name"
            WHERE a.time >= @begin AND b."Id" = @device_id ORDER BY a.time
            """;
        var parameters = new SqlParameters().AddNamed("begin", 1500L).AddNamed("device_id", 1L).AddNamed("label", "bound");
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, null, sql, parameters, null));
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new object?[] { 2000L, "alpha", 1L, 2.5, "bound" }, result.Rows[0]);
        Assert.Equal(new object?[] { 3000L, "alpha", 1L, 3.5, "bound" }, result.Rows[1]);
        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, null, "EXPLAIN " + sql, parameters, null));
        Assert.Contains(explain.Rows, row => Equals(row[0], "statement_type") && Equals(row[1], "select_join"));
    }

    /// <summary>规范化 AST 保留未投影 FIELD 的升降序，分页在跨 series 排序后执行。</summary>
    [Theory]
    [InlineData("ASC", 1500L, 1000L)]
    [InlineData("DESC", 1000L, 1500L)]
    public void Execute_ParameterizedHiddenFieldOrdering_PaginatesAfterGlobalSort(string direction, long first, long second)
    {
        using var database = CreateDatabase();
        string sql = $"SELECT a.time FROM sonnet_metric a INNER JOIN sonnet_device b ON a.Host = b.Name WHERE a.time >= @begin ORDER BY a.Rank {direction} OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY";
        var parameters = new SqlParameters().AddNamed("begin", 0L).AddNamed("skip", 1L).AddNamed("take", 2L);
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, null, sql, parameters, null));
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new object?[] { first }, result.Rows[0]);
        Assert.Equal(new object?[] { second }, result.Rows[1]);
    }

    /// <summary>多键混合方向包含关系键和隐藏 FIELD，不丢弃后续排序项。</summary>
    [Theory]
    [InlineData("b.Id ASC, a.Rank DESC", 1000L, 2000L)]
    [InlineData("b.Id DESC, a.Rank ASC", 2000L, 1000L)]
    public void Execute_ParameterizedMultipleSortKeys_PreservesDirectionsBeforePagination(string ordering, long first, long second)
    {
        using var database = CreateDatabase();
        string sql = $"SELECT a.time FROM sonnet_metric a INNER JOIN sonnet_device b ON a.Host = b.Name WHERE a.time >= @begin ORDER BY {ordering} OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY";
        var parameters = new SqlParameters().AddNamed("begin", 0L).AddNamed("skip", 1L).AddNamed("take", 2L);
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, null, sql, parameters, null));
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new object?[] { first }, result.Rows[0]);
        Assert.Equal(new object?[] { second }, result.Rows[1]);
    }

    /// <summary>不支持的组合经参数绑定后返回模型诊断，不能回到丢失 JOIN 的内部错误。</summary>
    [Theory]
    [InlineData("LEFT JOIN sonnet_device b ON a.Host = b.Name", "当前仅支持关系表 FROM")]
    [InlineData("INNER JOIN sonnet_device b ON a.Host = b.Name INNER JOIN sonnet_device c ON a.Host = c.Name", "仅支持一个关系维表")]
    [InlineData("INNER JOIN sonnet_device b ON a.Host = b.Name AND b.Id = @device_id", "仅支持 ON 中一个等值条件")]
    [InlineData("INNER JOIN sonnet_device b ON a.Host = @name", "等值两侧必须都是列引用")]
    [InlineData("INNER JOIN sonnet_docs b ON a.Host = b.id", "JOIN 右侧必须是关系表")]
    public void ExecuteAndExplain_UnsupportedJoinShape_ReturnsStableModelDiagnostic(string clause, string diagnostic)
    {
        using var database = CreateDatabase();
        var parameters = new SqlParameters().AddNamed("device_id", 1L).AddNamed("name", "alpha");
        string sql = "SELECT a.time FROM sonnet_metric a " + clause;
        var execute = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database, null, sql, parameters, null));
        var explain = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database, null, "EXPLAIN " + sql, parameters, null));
        Assert.Contains(diagnostic, execute.Message, StringComparison.Ordinal);
        Assert.Equal(execute.Message, explain.Message);
        Assert.DoesNotContain("要求 SELECT 包含 JOIN", execute.Message, StringComparison.Ordinal);
    }

    /// <summary>聚合与分组的拒绝不依赖输入行数，EXPLAIN 使用同一校验。</summary>
    [Theory]
    [InlineData("COUNT(*)", "", "不支持聚合函数")]
    [InlineData("SUM(a.Value) + 1", "", "不支持聚合函数")]
    [InlineData("CAST(SUM(a.Value) AS FLOAT)", "", "不支持聚合函数")]
    [InlineData("CASE WHEN a.Value > 0 THEN SUM(a.Value) ELSE 0 END", "", "不支持聚合函数")]
    [InlineData("SUM(a.Value) IS NULL", "", "不支持聚合函数")]
    [InlineData("b.Id", " GROUP BY b.Id", "不支持 GROUP BY")]
    [InlineData("a.time", " HAVING COUNT(*) > 0", "不支持 HAVING")]
    public void ExecuteAndExplain_AggregateWithEmptyOrPopulatedInput_RejectsBeforeScanning(string projection, string suffix, string diagnostic)
    {
        using var database = CreateDatabase();
        string? firstMessage = null;
        foreach (long begin in new[] { 0L, 9999L })
        {
            _deadline.Token.ThrowIfCancellationRequested();
            string sql = $"SELECT {projection} FROM sonnet_metric a INNER JOIN sonnet_device b ON a.Host = b.Name WHERE a.time >= @begin{suffix}";
            var parameters = new SqlParameters().AddNamed("begin", begin);
            foreach (string prefix in new[] { string.Empty, "EXPLAIN " })
            {
                var error = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database, null, prefix + sql, parameters, null));
                Assert.Contains(diagnostic, error.Message, StringComparison.Ordinal);
                if (firstMessage is not null) Assert.Equal(firstMessage, error.Message);
                firstMessage = error.Message;
            }
        }
    }

    private Tsdb CreateDatabase()
    {
        _deadline.Token.ThrowIfCancellationRequested();
        var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        try
        {
            string[] statements =
            [
                "CREATE MEASUREMENT sonnet_metric (Host TAG, Value FIELD FLOAT, Rank FIELD INT)",
                "CREATE TABLE sonnet_device (Id INT, Name STRING, PRIMARY KEY (Id))",
                "CREATE DOCUMENT COLLECTION sonnet_docs",
                "INSERT INTO sonnet_device (Id, Name) VALUES (1, 'alpha'), (2, 'beta')",
                "INSERT INTO sonnet_metric (time, Host, Value, Rank) VALUES (1000, 'alpha', 1.5, 30), (2000, 'alpha', 2.5, 10), (3000, 'alpha', 3.5, 40), (1500, 'beta', 8.5, 20), (4000, 'orphan', 9.5, 50)",
            ];
            foreach (string statement in statements)
            {
                _deadline.Token.ThrowIfCancellationRequested();
                SqlExecutor.Execute(database, statement);
            }
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }
}
