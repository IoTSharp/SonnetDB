using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlExecutorMeasurementGroupByTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-measurement-group-" + Guid.NewGuid().ToString("N"));

    public SqlExecutorMeasurementGroupByTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Select_GroupByTag_ReturnsStableTagGroups()
    {
        using var db = OpenAndSeed();

        var result = Select(db,
            "SELECT host, count(usage), sum(usage) FROM cpu GROUP BY host");

        Assert.Equal(["host", "count(usage)", "sum(usage)"], result.Columns);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(["h1", "h2"], result.Rows.Select(static row => (string)row[0]!));
        Assert.Equal([3L, 2L], result.Rows.Select(static row => (long)row[1]!));
        Assert.Equal([6.0, 11.0], result.Rows.Select(static row => (double)row[2]!));
    }

    [Fact]
    public void Select_GroupByTag_AfterFlushAndReopen_ReturnsTheSameResults()
    {
        IReadOnlyList<IReadOnlyList<object?>> expected;
        using (var db = OpenAndSeed())
        {
            expected = Select(db, "SELECT host, count(*), sum(usage) FROM cpu GROUP BY host").Rows;
            db.FlushNow();
            Assert.Equal(expected, Select(db,
                "SELECT host, count(*), sum(usage) FROM cpu GROUP BY host").Rows);
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        Assert.Equal(expected, Select(reopened,
            "SELECT host, count(*), sum(usage) FROM cpu GROUP BY host").Rows);
    }

    [Fact]
    public void Select_GroupByTagAndTime_ReturnsCompositeGroupsInOrder()
    {
        using var db = OpenAndSeed();

        var result = Select(db,
            "SELECT host, time, count(usage) FROM cpu GROUP BY host, time(1s)");

        Assert.Equal(5, result.Rows.Count);
        Assert.Equal(
            [("h1", 1000L), ("h2", 1000L), ("h1", 2000L), ("h2", 2000L), ("h1", 3000L)],
            result.Rows.Select(static row => ((string)row[0]!, (long)row[1]!)));
        Assert.All(result.Rows, static row => Assert.Equal(1L, row[2]));
    }

    [Fact]
    public void Select_GroupByMultipleTags_MergesOnlyMatchingCompositeKeys()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE MEASUREMENT sites (host TAG, region TAG, usage FIELD FLOAT)");
        SqlExecutor.Execute(db,
            "INSERT INTO sites (time, host, region, usage) VALUES " +
            "(1000, 'h1', 'north', 1.0), (2000, 'h1', 'north', 2.0), " +
            "(1000, 'h1', 'south', 4.0), (1000, 'H1', 'north', 8.0)");

        var result = Select(db,
            "SELECT host, region, count(*), sum(usage) FROM sites GROUP BY host, region");

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(new object?[] { "H1", "north", 1L, 8.0 }, result.Rows[0]);
        Assert.Equal(new object?[] { "h1", "north", 2L, 3.0 }, result.Rows[1]);
        Assert.Equal(new object?[] { "h1", "south", 1L, 4.0 }, result.Rows[2]);
        var merged = Select(db, "SELECT host, sum(usage) FROM sites GROUP BY host");
        Assert.Equal(new object?[] { "h1", 7.0 }, merged.Rows[1]);
    }

    [Fact]
    public void Select_GroupByFieldOrUnknownColumn_RejectsBeforeExecution()
    {
        using var db = OpenAndSeed();

        var field = Assert.Throws<InvalidOperationException>(() =>
            Select(db, "SELECT usage, count(*) FROM cpu GROUP BY usage"));
        Assert.Contains("必须是 TAG", field.Message);

        var unknown = Assert.Throws<InvalidOperationException>(() =>
            Select(db, "SELECT missing, count(*) FROM cpu GROUP BY missing"));
        Assert.Contains("未知列", unknown.Message);
    }

    [Fact]
    public void Select_GroupByTag_OnEmptyMeasurement_ReturnsNoGroups()
    {
        using var db = OpenEmpty();

        var result = Select(db, "SELECT host, count(usage) FROM cpu GROUP BY host");

        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Explain_GroupByTag_UsesTheSameSupportContract()
    {
        using var db = OpenAndSeed();

        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "EXPLAIN SELECT host, count(usage) FROM cpu GROUP BY host"));
        var values = result.Rows.ToDictionary(
            static row => (string)row[0]!, static row => row[1], StringComparer.Ordinal);

        Assert.Equal("cpu", values["measurement"]);
        Assert.Equal("select", values["statement_type"]);
        Assert.Contains("aggregate=tag_group_blocking", Assert.IsType<string>(values["memory_behavior"]));
    }

    [Fact]
    public void Select_GroupByTag_WithResidualOrHaving_RejectsExplicitly()
    {
        using var db = OpenAndSeed();

        var residual = Assert.Throws<NotSupportedException>(() =>
            Select(db, "SELECT host, count(usage) FROM cpu WHERE usage > 1 GROUP BY host"));
        Assert.Contains("残差", residual.Message);

        var having = Assert.Throws<NotSupportedException>(() =>
            Select(db, "SELECT host, count(usage) FROM cpu GROUP BY host HAVING count(usage) > 1"));
        Assert.Contains("HAVING", having.Message);
    }

    [Fact]
    public void Select_GroupByTag_WithSparseFieldAndMissingTag_PreservesNullAndEmptyAggregates()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE MEASUREMENT sparse_metrics (host TAG, usage FIELD FLOAT, marker FIELD INT)");
        SqlExecutor.Execute(db,
            "INSERT INTO sparse_metrics (time, marker) VALUES (1000, 1)");
        SqlExecutor.Execute(db,
            "INSERT INTO sparse_metrics (time, host, marker) VALUES (2000, 'h0', 2)");
        SqlExecutor.Execute(db,
            "INSERT INTO sparse_metrics (time, host, usage, marker) VALUES (3000, 'h1', 2.0, 3)");

        var result = Select(db,
            "SELECT host, count(*), count(usage), sum(usage), min(usage), avg(usage) FROM sparse_metrics GROUP BY host");

        Assert.Equal(3, result.Rows.Count);
        Assert.Null(result.Rows[0][0]);
        Assert.Equal("h0", result.Rows[1][0]);
        Assert.Equal("h1", result.Rows[2][0]);
        foreach (var row in result.Rows.Take(2))
        {
            Assert.Equal(1L, row[1]);
            Assert.Equal(0L, row[2]);
            Assert.Null(row[3]);
            Assert.Null(row[4]);
            Assert.Null(row[5]);
        }
        Assert.Equal(1L, result.Rows[2][2]);
        Assert.Equal(2.0, result.Rows[2][3]);
    }

    [Fact]
    public void Select_GroupByTag_WithInt64Sum_PreservesPrecisionAndRejectsOverflow()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE MEASUREMENT integers (host TAG, value FIELD INT)");
        SqlExecutor.Execute(db,
            "INSERT INTO integers (time, host, value) VALUES " +
            "(1000, 'precise', 9007199254740993), (2000, 'precise', 2), " +
            "(1000, 'overflow', 9223372036854775807), (2000, 'overflow', 1)");

        var result = Select(db,
            "SELECT host, sum(value) FROM integers WHERE host = 'precise' GROUP BY host");
        Assert.Equal(9007199254740995L, Assert.IsType<long>(Assert.Single(result.Rows)[1]));
        Assert.Throws<OverflowException>(() => Select(db,
            "SELECT host, sum(value) FROM integers WHERE host = 'overflow' GROUP BY host"));
        db.FlushNow();
        Assert.Equal(9007199254740995L, Assert.IsType<long>(Assert.Single(Select(db,
            "SELECT host, sum(value) FROM integers WHERE host = 'precise' GROUP BY host").Rows)[1]));
        Assert.Throws<OverflowException>(() => Select(db,
            "SELECT host, sum(value) FROM integers WHERE host = 'overflow' GROUP BY host"));
    }

    [Theory]
    [InlineData("GROUP BY host HAVING count(usage) > 1")]
    [InlineData("GROUP BY time(1s) HAVING count(usage) > 1")]
    [InlineData("HAVING count(usage) > 1")]
    public void SelectAndExplain_WithMeasurementHaving_RejectsBeforeScanning(string suffix)
    {
        using var db = OpenEmpty();
        string sql = "SELECT count(usage) FROM cpu " + suffix;

        Assert.Throws<NotSupportedException>(() => Select(db, sql));
        Assert.Throws<NotSupportedException>(() => SqlExecutor.Execute(db, "EXPLAIN " + sql));
    }

    [Fact]
    public void SelectAndExplain_GroupByMixedCaseTag_PreserveOriginalNamesAndQuotedResolution()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE MEASUREMENT Metrics (Host TAG, Usage FIELD FLOAT)");
        SqlExecutor.Execute(db,
            "INSERT INTO Metrics (time, Host, Usage) VALUES (1000, 'h1', 1.0)");
        const string sql = "SELECT host, count(usage) FROM metrics GROUP BY HOST";

        var result = Select(db, sql);
        Assert.Equal(["Host", "count(Usage)"], result.Columns);
        Assert.Equal("h1", Assert.Single(result.Rows)[0]);
        var plan = SqlExplainPlanner.Explain(null, db, SqlParser.Parse(sql));
        Assert.Equal("Metrics", plan.Measurement);
        Assert.Single(Select(db,
            "SELECT \"Host\", count(Usage) FROM Metrics GROUP BY \"Host\"").Rows);

        const string mismatched = "SELECT count(Usage) FROM Metrics GROUP BY \"host\"";
        Assert.Throws<InvalidOperationException>(() => Select(db, mismatched));
        Assert.Throws<InvalidOperationException>(() =>
            SqlExplainPlanner.Explain(null, db, SqlParser.Parse(mismatched)));
    }

    [Theory]
    [InlineData("ORDER BY host")]
    [InlineData("ORDER BY time, host DESC")]
    public void SelectAndExplain_GroupByTag_WithUnsupportedOrder_RejectsBeforeScanning(string order)
    {
        using var db = OpenEmpty();
        string sql = "SELECT host, time, count(*) FROM cpu GROUP BY host, time(1s) " + order;

        Assert.Throws<NotSupportedException>(() => Select(db, sql));
        Assert.Throws<NotSupportedException>(() => SqlExecutor.Execute(db, "EXPLAIN " + sql));
    }

    [Fact]
    public void SelectAndExplain_GroupByTag_WithCompoundAggregateProjection_RejectsBeforeScanning()
    {
        using var db = OpenEmpty();
        const string sql = "SELECT host, sum(usage) + 1 FROM cpu GROUP BY host";

        Assert.Throws<NotSupportedException>(() => Select(db, sql));
        Assert.Throws<NotSupportedException>(() => SqlExecutor.Execute(db, "EXPLAIN " + sql));
    }

    private Tsdb OpenAndSeed()
    {
        var db = OpenEmpty();
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, usage) VALUES " +
            "(1000, 'h1', 1.0), (2000, 'h1', 2.0), (3000, 'h1', 3.0), " +
            "(1000, 'h2', 5.0), (2000, 'h2', 6.0)");
        return db;
    }

    private Tsdb OpenEmpty()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        return db;
    }

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
}
