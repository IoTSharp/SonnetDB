using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlExplainTests : IDisposable
{
    private readonly string _root;

    public SqlExplainTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-explain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private TsdbOptions Options() => new() { RootDirectory = _root };

    [Fact]
    public void Parse_ExplainSelect_WrapsInnerSelect()
    {
        var explain = Assert.IsType<ExplainStatement>(
            SqlParser.Parse("EXPLAIN SELECT usage FROM cpu WHERE host = 'h1'"));

        var select = Assert.IsType<SelectStatement>(explain.Statement);
        Assert.Equal("cpu", select.Measurement);
    }

    [Fact]
    public void Parse_ExplainShowTables_MapsToShowMeasurements()
    {
        var explain = Assert.IsType<ExplainStatement>(
            SqlParser.Parse("EXPLAIN SHOW TABLES"));

        Assert.IsType<ShowMeasurementsStatement>(explain.Statement);
    }

    [Fact]
    public void Parse_ExplainWriteOrControlPlaneStatement_Throws()
    {
        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("EXPLAIN INSERT INTO cpu (host, usage) VALUES ('h1', 1)"));

        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("EXPLAIN SHOW DATABASES"));
    }

    [Fact]
    public void Execute_ExplainSelect_ReturnsKeyValuePlanRows()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, usage) VALUES (1000, 'h1', 0.5), (2000, 'h1', 0.7), (3000, 'h2', 0.9)");
        db.FlushNow();

        var statement = SqlParser.Parse(
            "EXPLAIN SELECT usage FROM cpu WHERE host = 'h1' AND time >= 1000 AND time <= 2000");
        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.ExecuteStatement(db, "metrics", statement));

        Assert.Equal(new[] { "key", "value" }, result.Columns);

        var values = result.Rows.ToDictionary(
            row => (string)row[0]!,
            row => row[1],
            StringComparer.Ordinal);

        Assert.Equal("metrics", values["database"]);
        Assert.Equal("select", values["statement_type"]);
        Assert.Equal("cpu", values["measurement"]);
        Assert.Equal(1, Convert.ToInt32(values["matched_series_count"]));
        Assert.Equal(1, Convert.ToInt32(values["estimated_segment_count"]));
        Assert.Equal(1, Convert.ToInt32(values["estimated_block_count"]));
        Assert.Equal(2L, Convert.ToInt64(values["estimated_scanned_rows"]));
        Assert.Equal(0L, Convert.ToInt64(values["estimated_memtable_rows"]));
        Assert.Equal(2L, Convert.ToInt64(values["estimated_segment_rows"]));
        Assert.True((bool)values["has_time_filter"]!);
        Assert.Equal(1, Convert.ToInt32(values["tag_filter_count"]));
    }
}
