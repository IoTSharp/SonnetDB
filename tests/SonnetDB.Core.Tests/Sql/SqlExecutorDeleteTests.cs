using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public class SqlExecutorDeleteTests : IDisposable
{
    private readonly string _root;

    public SqlExecutorDeleteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private TsdbOptions Options() => new() { RootDirectory = _root };

    private static Tsdb OpenWithSchema(TsdbOptions options)
    {
        var db = Tsdb.Open(options);
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT cpu (host TAG, region TAG, usage FIELD FLOAT, count FIELD INT)");
        return db;
    }

    private static void Seed(Tsdb db)
    {
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, region, usage, count) VALUES " +
            "(1000, 'h1', 'cn', 1.0, 10), " +
            "(2000, 'h1', 'cn', 2.0, 20), " +
            "(3000, 'h1', 'cn', 3.0, 30), " +
            "(1500, 'h2', 'us', 5.0, 50), " +
            "(2500, 'h2', 'us', 6.0, 60)");
    }

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));

    private static DeleteExecutionResult Delete(Tsdb db, string sql)
        => Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(db, sql));

    [Fact]
    public void Delete_TimeRangeAndTagFilter_RemovesMatchingPoints()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Delete(db, "DELETE FROM cpu WHERE host = 'h1' AND time >= 2000 AND time <= 3000");

        Assert.Equal("cpu", r.Measurement);
        Assert.Equal(1, r.SeriesAffected);
        // schema 有 2 个 field 列（usage, count）
        Assert.Equal(2, r.TombstonesAdded);

        // h1 仅剩 ts=1000；h2 不受影响
        var remaining = Select(db, "SELECT time, host, usage FROM cpu");
        Assert.Equal(3, remaining.Rows.Count);
        Assert.Contains(remaining.Rows, row => (long)row[0]! == 1000 && (string?)row[1] == "h1");
        Assert.Contains(remaining.Rows, row => (long)row[0]! == 1500 && (string?)row[1] == "h2");
        Assert.Contains(remaining.Rows, row => (long)row[0]! == 2500 && (string?)row[1] == "h2");
    }

    [Fact]
    public void Delete_OnlyTimeRange_AppliesToAllMatchedSeries()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Delete(db, "DELETE FROM cpu WHERE time >= 2000 AND time <= 2500");

        // 命中 2 个 series（h1, h2），每个 series 2 个 field → 4 个墓碑
        Assert.Equal(2, r.SeriesAffected);
        Assert.Equal(4, r.TombstonesAdded);

        var remaining = Select(db, "SELECT time FROM cpu");
        // 删除窗口 [2000,2500]：剔除 h1@2000、h2@2500；剩 h1@1000、h1@3000、h2@1500
        Assert.Equal(3, remaining.Rows.Count);
        var times = remaining.Rows.Select(row => (long)row[0]!).OrderBy(t => t).ToList();
        Assert.Equal([1000L, 1500L, 3000L], times);
    }

    [Fact]
    public void Delete_TimeRange_WithNowAndDurationExpressions_RemovesExpectedPoints()
    {
        using var db = OpenWithSchema(Options());

        const long hourMs = 3_600_000L;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SqlExecutor.Execute(db,
            $"INSERT INTO cpu (time, host, region, usage, count) VALUES " +
            $"({nowMs - 36 * hourMs}, 'h1', 'cn', 1.0, 10), " +
            $"({nowMs - 12 * hourMs}, 'h1', 'cn', 2.0, 20), " +
            $"({nowMs + 12 * hourMs}, 'h1', 'cn', 3.0, 30)");

        var r = Delete(db,
            "DELETE FROM cpu WHERE host = 'h1' AND time >= now() - 1d AND time < now() + 1d");

        Assert.Equal(1, r.SeriesAffected);
        Assert.Equal(2, r.TombstonesAdded);

        var remaining = Select(db, "SELECT time, usage FROM cpu WHERE host = 'h1' ORDER BY time");
        Assert.Single(remaining.Rows);
        Assert.Equal(nowMs - 36 * hourMs, remaining.Rows[0][0]);
        Assert.Equal(1.0, remaining.Rows[0][1]);
    }

    [Fact]
    public void Delete_OnlyTagFilter_RemovesAllPointsOfSeries()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Delete(db, "DELETE FROM cpu WHERE host = 'h2'");

        Assert.Equal(1, r.SeriesAffected);
        Assert.Equal(2, r.TombstonesAdded);

        var remaining = Select(db, "SELECT time, host FROM cpu");
        Assert.Equal(3, remaining.Rows.Count);
        Assert.All(remaining.Rows, row => Assert.Equal("h1", row[1]));
    }

    [Fact]
    public void Delete_ExactTime_RemovesSinglePoint()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Delete(db, "DELETE FROM cpu WHERE host = 'h1' AND time = 2000");

        Assert.Equal(1, r.SeriesAffected);
        Assert.Equal(2, r.TombstonesAdded);

        var remaining = Select(db, "SELECT time FROM cpu WHERE host = 'h1'");
        Assert.Equal([1000L, 3000L], remaining.Rows.Select(row => (long)row[0]!));
    }

    [Fact]
    public void Delete_NoMatchingSeries_ReturnsZeroAffected()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Delete(db, "DELETE FROM cpu WHERE host = 'ghost' AND time >= 0");

        Assert.Equal(0, r.SeriesAffected);
        Assert.Equal(0, r.TombstonesAdded);

        // 数据未变
        var all = Select(db, "SELECT time FROM cpu");
        Assert.Equal(5, all.Rows.Count);
    }

    [Fact]
    public void Delete_PersistsAcrossReopen()
    {
        var options = Options();
        using (var db = OpenWithSchema(options))
        {
            Seed(db);
            Delete(db, "DELETE FROM cpu WHERE host = 'h1' AND time >= 2000");
        }

        using (var db = Tsdb.Open(options))
        {
            var remaining = Select(db, "SELECT time, host FROM cpu");
            // h1 剩下 ts=1000；h2 全部保留
            Assert.Equal(3, remaining.Rows.Count);
            Assert.Contains(remaining.Rows, row => (long)row[0]! == 1000 && (string?)row[1] == "h1");
            Assert.Contains(remaining.Rows, row => (long)row[0]! == 1500 && (string?)row[1] == "h2");
            Assert.Contains(remaining.Rows, row => (long)row[0]! == 2500 && (string?)row[1] == "h2");
        }
    }

    [Fact]
    public void Delete_AffectsAggregates()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var beforeSum = Select(db, "SELECT sum(usage) FROM cpu");
        Assert.Equal(17.0, (double)beforeSum.Rows[0][0]!); // 1+2+3+5+6

        Delete(db, "DELETE FROM cpu WHERE host = 'h1' AND time >= 2000");

        var afterSum = Select(db, "SELECT sum(usage) FROM cpu");
        Assert.Equal(12.0, (double)afterSum.Rows[0][0]!); // 1+5+6
    }

    // ── 错误场景 ───────────────────────────────────────────────────────────

    [Fact]
    public void Delete_MissingMeasurement_Throws()
    {
        using var db = Tsdb.Open(Options());
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "DELETE FROM ghost WHERE time = 0"));
    }

    [Fact]
    public void Delete_OrInWhere_Throws()
    {
        using var db = OpenWithSchema(Options());
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "DELETE FROM cpu WHERE host = 'h1' OR host = 'h2'"));
    }

    [Fact]
    public void Delete_FieldInWhere_Throws()
    {
        using var db = OpenWithSchema(Options());
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "DELETE FROM cpu WHERE usage > 0"));
    }

    [Fact]
    public void Delete_UnknownTagColumn_Throws()
    {
        using var db = OpenWithSchema(Options());
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "DELETE FROM cpu WHERE bogus = 'x'"));
    }

    [Fact]
    public void Delete_EmptyTimeWindow_Throws()
    {
        using var db = OpenWithSchema(Options());
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "DELETE FROM cpu WHERE time >= 5000 AND time <= 1000"));
    }

    [Fact]
    public void Delete_NullArguments_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SqlExecutor.ExecuteDelete(null!, new SonnetDB.Sql.Ast.DeleteStatement("cpu",
                new SonnetDB.Sql.Ast.LiteralExpression(SonnetDB.Sql.Ast.SqlLiteralKind.Boolean, BooleanValue: true))));

        using var db = Tsdb.Open(Options());
        Assert.Throws<ArgumentNullException>(() => SqlExecutor.ExecuteDelete(db, null!));
    }
}
