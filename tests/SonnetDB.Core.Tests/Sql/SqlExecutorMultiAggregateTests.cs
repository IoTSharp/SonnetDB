using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// M33 #283 — 同字段多聚合单次扫描（count/sum/min/max/avg 共享字段合并为一次块扫描）的 SQL 端到端
/// 集成测试。核心手法：把多聚合查询逐列与「同聚合单独执行」对拍，直接证明分组路径与既有逐聚合路径
/// 结果按位一致；覆盖 MemTable 热路径、Flush 段冷路径、tombstone 逐点路径、GROUP BY 分桶、多 series、
/// 冷热混合，以及带 Geo 时不启用分组（回退逐点，保持 #282 语义）。
/// </summary>
public sealed class SqlExecutorMultiAggregateTests : IDisposable
{
    private readonly string _root;

    public SqlExecutorMultiAggregateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-multiagg-" + Guid.NewGuid().ToString("N"));
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
            "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT, position FIELD GEOPOINT)");
        return db;
    }

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));

    private static void Seed(Tsdb db, string host, params (long ts, double usage)[] points)
    {
        var lines = points.Select(p =>
            $"({p.ts}, '{host}', {p.usage.ToString(CultureInfo.InvariantCulture)})");
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, usage) VALUES " + string.Join(", ", lines));
    }

    private static double Cell(SelectExecutionResult r, int row, int col)
        => Convert.ToDouble(r.Rows[row][col]!, CultureInfo.InvariantCulture);

    /// <summary>对拍：多聚合一行 vs 各聚合单独查询，逐聚合断言按位一致。</summary>
    private static void AssertMultiMatchesSingles(Tsdb db, string whereAndGroup = "")
    {
        var multi = Select(db,
            $"SELECT count(usage), sum(usage), min(usage), max(usage), avg(usage) FROM cpu {whereAndGroup}");

        var count = Select(db, $"SELECT count(usage) FROM cpu {whereAndGroup}");
        var sum = Select(db, $"SELECT sum(usage) FROM cpu {whereAndGroup}");
        var min = Select(db, $"SELECT min(usage) FROM cpu {whereAndGroup}");
        var max = Select(db, $"SELECT max(usage) FROM cpu {whereAndGroup}");
        var avg = Select(db, $"SELECT avg(usage) FROM cpu {whereAndGroup}");

        Assert.Equal(count.Rows.Count, multi.Rows.Count);
        for (int i = 0; i < multi.Rows.Count; i++)
        {
            Assert.Equal(Cell(count, i, 0), Cell(multi, i, 0));
            Assert.Equal(Cell(sum, i, 0), Cell(multi, i, 1));
            Assert.Equal(Cell(min, i, 0), Cell(multi, i, 2));
            Assert.Equal(Cell(max, i, 0), Cell(multi, i, 3));
            Assert.Equal(Cell(avg, i, 0), Cell(multi, i, 4));
        }
    }

    [Fact]
    public void MultiAggregate_MemTableHot_MatchesSingleAggregates()
    {
        using var db = OpenWithSchema(Options());
        Seed(db, "h1", (1, 5), (2, 2), (3, 8), (4, 1), (5, 9), (6, 4));

        AssertMultiMatchesSingles(db);

        // 显式真值锚点，防止「两路都错但一致」。
        var r = Select(db, "SELECT count(usage), sum(usage), min(usage), max(usage), avg(usage) FROM cpu");
        Assert.Single(r.Rows);
        Assert.Equal(6.0, Cell(r, 0, 0));
        Assert.Equal(29.0, Cell(r, 0, 1));
        Assert.Equal(1.0, Cell(r, 0, 2));
        Assert.Equal(9.0, Cell(r, 0, 3));
        Assert.Equal(29.0 / 6.0, Cell(r, 0, 4), precision: 9);
    }

    [Fact]
    public void MultiAggregate_FlushedSegment_MatchesSingleAggregates()
    {
        using var db = OpenWithSchema(Options());
        Seed(db, "h1", (1, 5), (2, 2), (3, 8), (4, 1), (5, 9), (6, 4));
        db.FlushNow();

        AssertMultiMatchesSingles(db);
    }

    [Fact]
    public void MultiAggregate_MixedHotAndCold_MatchesSingleAggregates()
    {
        using var db = OpenWithSchema(Options());
        Seed(db, "h1", (1, 5), (2, 2), (3, 8));
        db.FlushNow();                       // 冷段
        Seed(db, "h1", (4, 1), (5, 9), (6, 4)); // 热 MemTable

        AssertMultiMatchesSingles(db);
    }

    [Fact]
    public void MultiAggregate_WithTombstone_UsesPointPath_MatchesSingleAggregates()
    {
        using var db = OpenWithSchema(Options());
        Seed(db, "h1", (1, 5), (2, 2), (3, 8), (4, 1), (5, 9), (6, 4));
        db.FlushNow();
        // 建立 tombstone → ShouldUsePointAggregatePath 为真，多聚合走逐点回退路径。
        SqlExecutor.Execute(db, "DELETE FROM cpu WHERE host = 'h1' AND time >= 4 AND time <= 5");

        AssertMultiMatchesSingles(db);

        var r = Select(db, "SELECT count(usage), sum(usage), min(usage), max(usage) FROM cpu");
        Assert.Equal(4.0, Cell(r, 0, 0));      // 6 - 2 删除
        Assert.Equal(19.0, Cell(r, 0, 1));     // 29 - (1+9)，删除的是 time 4(=1)、time 5(=9)
        Assert.Equal(2.0, Cell(r, 0, 2));      // 剩 {5,2,8,4} → min 2
        Assert.Equal(8.0, Cell(r, 0, 3));      // max 8
    }

    [Fact]
    public void MultiAggregate_GroupByTime_MatchesSingleAggregates()
    {
        using var db = OpenWithSchema(Options());
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, usage) VALUES " +
            "(0, 'h1', 1), (500, 'h1', 3), " +       // bucket [0,1000): {1,3}
            "(1000, 'h1', 10), (1500, 'h1', 30), " + // bucket [1000,2000): {10,30}
            "(2000, 'h1', 7)");                       // bucket [2000,3000): {7}

        AssertMultiMatchesSingles(db, "GROUP BY time(1000ms)");

        var r = Select(db,
            "SELECT count(usage), sum(usage), min(usage), max(usage), avg(usage) FROM cpu GROUP BY time(1000ms)");
        Assert.Equal(3, r.Rows.Count);
        // bucket 0: {1,3}
        Assert.Equal(2.0, Cell(r, 0, 0));
        Assert.Equal(4.0, Cell(r, 0, 1));
        Assert.Equal(1.0, Cell(r, 0, 2));
        Assert.Equal(3.0, Cell(r, 0, 3));
        // bucket 1: {10,30}
        Assert.Equal(40.0, Cell(r, 1, 1));
        Assert.Equal(20.0, Cell(r, 1, 4), precision: 9);
    }

    [Fact]
    public void MultiAggregate_GroupByTime_FlushedSegment_MatchesSingleAggregates()
    {
        using var db = OpenWithSchema(Options());
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, usage) VALUES " +
            "(0, 'h1', 1), (500, 'h1', 3), (1000, 'h1', 10), (1500, 'h1', 30), (2000, 'h1', 7)");
        db.FlushNow();

        AssertMultiMatchesSingles(db, "GROUP BY time(1000ms)");
    }

    [Fact]
    public void MultiAggregate_MultiSeries_MatchesSingleAggregates()
    {
        using var db = OpenWithSchema(Options());
        Seed(db, "h1", (1, 5), (2, 2), (3, 8));
        Seed(db, "h2", (1, 100), (2, 200), (3, 300));
        db.FlushNow();

        // 无 WHERE → 跨 series 全局聚合（各 series 独立扫描后合并到同一全局桶）。
        AssertMultiMatchesSingles(db);

        var r = Select(db, "SELECT count(usage), sum(usage), min(usage), max(usage) FROM cpu");
        Assert.Equal(6.0, Cell(r, 0, 0));
        Assert.Equal(615.0, Cell(r, 0, 1));   // 15 + 600
        Assert.Equal(2.0, Cell(r, 0, 2));
        Assert.Equal(300.0, Cell(r, 0, 3));
    }

    [Fact]
    public void MultiAggregate_WithTimeRange_MatchesSingleAggregates()
    {
        using var db = OpenWithSchema(Options());
        Seed(db, "h1", (1, 5), (2, 2), (3, 8), (4, 1), (5, 9), (6, 4));
        db.FlushNow();

        AssertMultiMatchesSingles(db, "WHERE time >= 2 AND time <= 5");
    }

    [Fact]
    public void MultiAggregate_PartialOverlap_MinMaxOnly_MatchesSingleAggregates()
    {
        // 只取 min/max（无 sum/count）也应等价——验证分组不依赖某一特定聚合在场。
        using var db = OpenWithSchema(Options());
        Seed(db, "h1", (1, 5), (2, 2), (3, 8), (4, 1), (5, 9));
        db.FlushNow();

        var multi = Select(db, "SELECT min(usage), max(usage) FROM cpu");
        var min = Select(db, "SELECT min(usage) FROM cpu");
        var max = Select(db, "SELECT max(usage) FROM cpu");
        Assert.Equal(Cell(min, 0, 0), Cell(multi, 0, 0));
        Assert.Equal(Cell(max, 0, 0), Cell(multi, 0, 1));
        Assert.Equal(1.0, Cell(multi, 0, 0));
        Assert.Equal(9.0, Cell(multi, 0, 1));
    }

    [Fact]
    public void MultiAggregate_DuplicateSameAggregate_MatchesSingle()
    {
        // 同字段同聚合出现两次：两列必须相等且等于单查询。
        using var db = OpenWithSchema(Options());
        Seed(db, "h1", (1, 5), (2, 2), (3, 8));

        var r = Select(db, "SELECT sum(usage), sum(usage), count(usage) FROM cpu");
        Assert.Equal(15.0, Cell(r, 0, 0));
        Assert.Equal(15.0, Cell(r, 0, 1));
        Assert.Equal(3.0, Cell(r, 0, 2));
    }

    [Fact]
    public void MultiAggregate_WithGeoPredicate_DisablesGrouping_StillCorrect()
    {
        // 带 Geo 谓词时 CanUseLegacyAggregateFastPath 恒 false → 不分组，回退逐点施加 #282 时间戳级 Geo 约束。
        using var db = OpenWithSchema(Options());
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, usage, position) VALUES " +
            "(1, 'h1', 5, POINT(31.10, 121.10)), " +   // box 内
            "(2, 'h1', 2, POINT(40.00, 116.00)), " +   // box 外（北京）
            "(3, 'h1', 8, POINT(31.20, 121.20)), " +   // box 内
            "(4, 'h1', 1, POINT(10.00, 100.00))");     // box 外
        db.FlushNow();

        var r = Select(db,
            "SELECT count(usage), sum(usage), min(usage), max(usage) FROM cpu " +
            "WHERE geo_bbox(position, 31.0, 121.0, 31.5, 121.5)");

        // 只命中 time 1(=5) 与 time 3(=8) 两点。
        Assert.Equal(2.0, Cell(r, 0, 0));
        Assert.Equal(13.0, Cell(r, 0, 1));
        Assert.Equal(5.0, Cell(r, 0, 2));
        Assert.Equal(8.0, Cell(r, 0, 3));
    }
}
