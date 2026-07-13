using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlRegexContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-regex-" + Guid.NewGuid().ToString("N"));

    public SqlRegexContractTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        RegexPatternMatcher.ClearCache();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("REGEX")]
    [InlineData("REGEXP")]
    [InlineData("RLIKE")]
    public void Parse_RegexOperatorAliases_UseSingleAstOperator(string alias)
    {
        var select = Assert.IsType<SelectStatement>(
            SqlParser.Parse($"SELECT id FROM devices WHERE name {alias} '^pump'"));

        var predicate = Assert.IsType<BinaryExpression>(select.Where);
        Assert.Equal(SqlBinaryOperator.Regex, predicate.Operator);
    }

    [Fact]
    public void RegexpLike_TableWhereAndProjection_SupportFlagsAndAliases()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO devices (id, name) VALUES (1, 'Pump-001'), (2, 'pump-A'), (3, 'fan-001')");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id, regexp_like(name, '^pump-[0-9]+$', 'i') AS numeric_name
            FROM devices
            WHERE name RLIKE '^[Pp]ump' AND regexp_like(name, '[0-9]+$')
            ORDER BY id
            """));

        var row = Assert.Single(result.Rows);
        Assert.Equal(1L, row[0]);
        Assert.Equal(true, row[1]);
    }

    [Fact]
    public void RegexpLike_MeasurementWhereAndProjection_UseUnifiedMatcher()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE MEASUREMENT status_log (host TAG, state FIELD STRING)");
        SqlExecutor.Execute(db, """
            INSERT INTO status_log (time, host, state) VALUES
              (1000, 'a', 'OK-ready'),
              (2000, 'a', 'failed')
            """);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT time, regexp_like(state, '^ok', 'i') AS healthy
            FROM status_log
            WHERE regexp_like(state, '^ok', 'i')
            ORDER BY time
            """));

        var row = Assert.Single(result.Rows);
        Assert.Equal(1000L, row[0]);
        Assert.Equal(true, row[1]);
    }

    [Fact]
    public void RegexpLike_DocumentWhereAndProjection_UseUnifiedMatcher()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");
        SqlExecutor.Execute(db, """
            INSERT INTO device_docs (id, document) VALUES
              ('dev-1', '{"type":"pump"}'),
              ('dev-2', '{"type":"fan"}')
            """);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id, regexp_like(id, '^DEV-', 'i') AS valid_id
            FROM device_docs
            WHERE regexp_like(json_value(document, '$.type'), '^pump$')
            ORDER BY id
            """));

        var row = Assert.Single(result.Rows);
        Assert.Equal("dev-1", row[0]);
        Assert.Equal(true, row[1]);
    }

    [Fact]
    public void RegexBudgetsAndCache_AreExplicitAndBounded()
    {
        Assert.Throws<InvalidOperationException>(() => RegexPatternMatcher.IsMatch(
            "value", new string('a', RegexPatternMatcher.MaxPatternLength + 1)));
        Assert.Throws<InvalidOperationException>(() => RegexPatternMatcher.IsMatch(
            new string('a', RegexPatternMatcher.MaxInputLength + 1), "a"));
        Assert.Throws<InvalidOperationException>(() => RegexPatternMatcher.IsMatch("value", "value", "q"));

        for (int i = 0; i < RegexPatternMatcher.CacheCapacity + 32; i++)
            Assert.True(RegexPatternMatcher.IsMatch($"value-{i}", $"^value-{i}$"));

        Assert.Equal(RegexPatternMatcher.CacheCapacity, RegexPatternMatcher.CachedPatternCount);
    }

    [Fact]
    public void Explain_RegexPredicate_ReportsResidualScanContract()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, name) VALUES (1, 'pump-001')");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "EXPLAIN SELECT id FROM devices WHERE regexp_like(name, '^pump')"));
        var values = result.Rows.ToDictionary(
            static row => (string)row[0]!, static row => row[1], StringComparer.Ordinal);

        Assert.Contains("regex_residual", Assert.IsType<string>(values["scan_filter"]), StringComparison.Ordinal);
        Assert.Equal("table_scan", values["access_path"]);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });
}
