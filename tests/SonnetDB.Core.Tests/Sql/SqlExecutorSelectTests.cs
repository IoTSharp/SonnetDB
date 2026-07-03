using SonnetDB.Engine;
using SonnetDB.Query;
using SonnetDB.Query.Functions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public class SqlExecutorSelectTests : IDisposable
{
    private readonly string _root;

    public SqlExecutorSelectTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-select-" + Guid.NewGuid().ToString("N"));
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
            "CREATE MEASUREMENT cpu (host TAG, region TAG, usage FIELD FLOAT, count FIELD INT, ok FIELD BOOL, label FIELD STRING)");
        return db;
    }

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));

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

    // ── 原始模式 ───────────────────────────────────────────────────────────

    [Fact]
    public void Select_StarFromMeasurement_ReturnsTimeTagsAndFields()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT * FROM cpu WHERE host = 'h1'");

        // 列：time + host + region + usage + count + ok + label
        Assert.Equal(["time", "host", "region", "usage", "count", "ok", "label"], r.Columns);
        Assert.Equal(3, r.Rows.Count);
        Assert.All(r.Rows, row => Assert.Equal("h1", row[1]));
        Assert.All(r.Rows, row => Assert.Equal("cn", row[2]));
        Assert.Equal([1000L, 2000L, 3000L], r.Rows.Select(row => (long)row[0]!));
        Assert.Equal([1.0, 2.0, 3.0], r.Rows.Select(row => (double)row[3]!));
        Assert.Equal([10L, 20L, 30L], r.Rows.Select(row => (long)row[4]!));
    }

    [Fact]
    public void Select_ProjectedColumns_ReturnsRequestedColumnsOnly()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT time, host, usage FROM cpu WHERE host = 'h1'");

        Assert.Equal(["time", "host", "usage"], r.Columns);
        Assert.Equal(3, r.Rows.Count);
        Assert.Equal(1000L, r.Rows[0][0]);
        Assert.Equal("h1", r.Rows[0][1]);
        Assert.Equal(1.0, r.Rows[0][2]);
    }

    [Fact]
    public void Select_FieldOnly_NoTimeColumn()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT usage FROM cpu WHERE host = 'h2'");

        Assert.Equal(["usage"], r.Columns);
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal([5.0, 6.0], r.Rows.Select(row => (double)row[0]!));
    }

    [Fact]
    public void Select_NoWhere_ReturnsAllSeries()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT time, host, usage FROM cpu");

        Assert.Equal(5, r.Rows.Count);
    }

    [Fact]
    public void Select_WithLimit_ReturnsAtMostLimitRows()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT time, host FROM cpu LIMIT 3");

        Assert.Equal(3, r.Rows.Count);
    }

    [Fact]
    public void Select_LiteralProjectionWithLimit_ReturnsConstantRows()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT 1 AS ok FROM cpu LIMIT 1");

        Assert.Equal(["ok"], r.Columns);
        Assert.Single(r.Rows);
        Assert.Equal(1L, r.Rows[0][0]);
    }

    [Fact]
    public void Select_WithOffsetFetch_ReturnsPagedRows()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var full = Select(db, "SELECT time, host, usage FROM cpu");
        var paged = Select(db, "SELECT time, host, usage FROM cpu OFFSET 1 ROW FETCH NEXT 2 ROWS ONLY");

        Assert.Equal(2, paged.Rows.Count);
        Assert.Equal(full.Rows[1], paged.Rows[0]);
        Assert.Equal(full.Rows[2], paged.Rows[1]);
    }

    [Fact]
    public void Select_WithOffsetOnly_SkipsRowsAndReturnsRemaining()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var full = Select(db, "SELECT time, host, usage FROM cpu");
        var paged = Select(db, "SELECT time, host, usage FROM cpu OFFSET 2");

        Assert.Equal(full.Rows.Count - 2, paged.Rows.Count);
        Assert.Equal(full.Rows[2], paged.Rows[0]);
    }

    [Fact]
    public void Select_WithLimitOffset_Syntax_UsesMysqlStylePagination()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var full = Select(db, "SELECT time, host FROM cpu");
        var paged = Select(db, "SELECT time, host FROM cpu LIMIT 2 OFFSET 1");

        Assert.Equal(2, paged.Rows.Count);
        Assert.Equal(full.Rows[1], paged.Rows[0]);
        Assert.Equal(full.Rows[2], paged.Rows[1]);
    }

    [Fact]
    public void Select_OrderByTimeDesc_AppliesBeforeLimit()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT time, host FROM cpu ORDER BY time DESC LIMIT 2");

        Assert.Equal(2, r.Rows.Count);
        Assert.Equal([3000L, 2500L], r.Rows.Select(row => (long)row[0]!));
    }

    [Fact]
    public void Select_OrderByTimeDescLimitOne_SingleSeriesField_ReturnsLatestPoint()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT time, usage FROM cpu WHERE host = 'h1' ORDER BY time DESC LIMIT 1");

        Assert.Single(r.Rows);
        Assert.Equal(3000L, r.Rows[0][0]);
        Assert.Equal(3.0, r.Rows[0][1]);
    }

    [Fact]
    public void Select_OrderByTimeDescLimitOne_AfterFlush_ReturnsLatestPoint()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);
        db.FlushNow();

        var r = Select(db, "SELECT time, usage FROM cpu WHERE host = 'h1' ORDER BY time DESC LIMIT 1");

        Assert.Single(r.Rows);
        Assert.Equal(3000L, r.Rows[0][0]);
        Assert.Equal(3.0, r.Rows[0][1]);
    }

    [Fact]
    public void Select_OrderByTimeAsc_AppliesBeforeOffset()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT time, host FROM cpu ORDER BY time ASC OFFSET 1 ROW FETCH NEXT 2 ROWS ONLY");

        Assert.Equal(2, r.Rows.Count);
        Assert.Equal([1500L, 2000L], r.Rows.Select(row => (long)row[0]!));
    }

    [Fact]
    public void Select_OrderByTimeWithoutProjectedTime_Throws()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "SELECT usage FROM cpu ORDER BY time DESC LIMIT 2"));
    }

    [Fact]
    public void Select_QualifiedColumnsWithAlias_ReturnsRows()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db,
            "SELECT c.time, c.host, c.usage FROM cpu AS c WHERE c.host = 'h1' AND c.time >= 1000 ORDER BY c.time DESC LIMIT 2");

        Assert.Equal(["time", "host", "usage"], r.Columns);
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal([3000L, 2000L], r.Rows.Select(row => (long)row[0]!));
        Assert.All(r.Rows, row => Assert.Equal("h1", row[1]));
    }

    [Theory]
    [InlineData("SELECT c.time FROM cpu")]
    [InlineData("SELECT x.time FROM cpu AS c")]
    public void Select_QualifiedColumnWithMissingOrWrongAlias_Throws(string sql)
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, sql));
    }

    [Fact]
    public void Select_WithOffsetBeyondEnd_ReturnsEmpty()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT time, host FROM cpu OFFSET 100 ROWS");

        Assert.Empty(r.Rows);
    }

    [Fact]
    public void Select_TimeRange_FiltersByTime()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT time, usage FROM cpu WHERE host = 'h1' AND time >= 2000 AND time < 3000");

        Assert.Single(r.Rows);
        Assert.Equal(2000L, r.Rows[0][0]);
        Assert.Equal(2.0, r.Rows[0][1]);
    }

    [Fact]
    public void Select_TimeRange_WithNowAndDurationExpressions_FiltersByRelativeWindow()
    {
        using var db = OpenWithSchema(Options());

        const long hourMs = 3_600_000L;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SqlExecutor.Execute(db,
            $"INSERT INTO cpu (time, host, region, usage, count) VALUES " +
            $"({nowMs - 36 * hourMs}, 'h1', 'cn', 1.0, 10), " +
            $"({nowMs - 12 * hourMs}, 'h1', 'cn', 2.0, 20), " +
            $"({nowMs + 12 * hourMs}, 'h1', 'cn', 3.0, 30)");

        var r = Select(db,
            "SELECT time, usage FROM cpu WHERE host = 'h1' AND time >= now() - 1d AND time < now() + 1d ORDER BY time");

        Assert.Equal([nowMs - 12 * hourMs, nowMs + 12 * hourMs], r.Rows.Select(row => (long)row[0]!).ToArray());
        Assert.Equal([2.0, 3.0], r.Rows.Select(row => (double)row[1]!).ToArray());
    }

    [Fact]
    public void Select_OuterJoinAcrossFields_NullForMissingFields()
    {
        using var db = OpenWithSchema(Options());
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, usage) VALUES (1000, 'h1', 1.0), (2000, 'h1', 2.0)");
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, count) VALUES (2000, 'h1', 20), (3000, 'h1', 30)");

        var r = Select(db, "SELECT time, usage, count FROM cpu WHERE host = 'h1'");

        Assert.Equal(3, r.Rows.Count);
        Assert.Equal([1000L, 2000L, 3000L], r.Rows.Select(row => (long)row[0]!));
        Assert.Equal(1.0, r.Rows[0][1]); Assert.Null(r.Rows[0][2]);
        Assert.Equal(2.0, r.Rows[1][1]); Assert.Equal(20L, r.Rows[1][2]);
        Assert.Null(r.Rows[2][1]); Assert.Equal(30L, r.Rows[2][2]);
    }

    [Fact]
    public void Select_AliasOnIdentifier_RenamesColumn()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT usage AS u FROM cpu WHERE host = 'h1'");

        Assert.Equal(["u"], r.Columns);
    }

    [Fact]
    public void Select_ScalarFunctions_ReturnComputedValues()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT abs(-usage), round(usage / 3, 2), sqrt(count), log(count, 10), coalesce(label, 'n/a') FROM cpu WHERE host = 'h1'");

        Assert.Equal(["abs", "round", "sqrt(count)", "log", "coalesce"], r.Columns);
        Assert.Equal(3, r.Rows.Count);
        Assert.Equal(1.0, r.Rows[0][0]);
        Assert.Equal(Math.Round(1.0 / 3.0, 2), r.Rows[0][1]);
        Assert.Equal(Math.Sqrt(10.0), r.Rows[0][2]);
        Assert.Equal(Math.Log(10.0, 10.0), r.Rows[0][3]);
        Assert.Equal("n/a", r.Rows[0][4]);
    }

    [Fact]
    public void Select_Coalesce_UsesFirstNonNullFieldValue()
    {
        using var db = OpenWithSchema(Options());
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, usage) VALUES (1000, 'h1', 1.0), (2000, 'h1', 2.0)");
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, label) VALUES (2000, 'h1', 'ok'), (3000, 'h1', 'late')");

        var r = Select(db, "SELECT time, coalesce(label, 'missing') FROM cpu WHERE host = 'h1'");

        Assert.Equal([2000L, 3000L], r.Rows.Select(row => (long)row[0]!));
        Assert.Equal("ok", r.Rows[0][1]);
        Assert.Equal("late", r.Rows[1][1]);
    }

    [Fact]
    public void Select_ScalarFunctionAlias_RenamesColumn()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT round(usage, 1) AS rounded FROM cpu WHERE host = 'h1'");

        Assert.Equal(["rounded"], r.Columns);
        Assert.Equal(1.0, r.Rows[0][0]);
    }

    [Fact]
    public void Select_UnknownScalarFunction_Throws()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "SELECT mystery(usage) FROM cpu WHERE host = 'h1'"));
    }

    [Fact]
    public void Select_ScalarFunction_InvalidArgumentCount_Throws()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "SELECT abs() FROM cpu WHERE host = 'h1'"));
    }

    // ── 聚合模式 ───────────────────────────────────────────────────────────

    [Fact]
    public void Select_CountStar_CountsRowsNotFieldValues()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT count(*) FROM cpu WHERE host = 'h1'");

        Assert.Single(r.Rows);
        // h1 有 3 个时刻（行），尽管每行写了 usage 与 count 两个 field。
        // 旧实现按 field 值点累加会返回 6；count(*) 计行数应为 3。
        Assert.Equal(3L, r.Rows[0][0]);
    }

    [Fact]
    public void Select_CountOne_IsCompatibleWithCountStar()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT count(1) FROM cpu WHERE host = 'h1'");

        Assert.Equal(["count(1)"], r.Columns);
        Assert.Single(r.Rows);
        Assert.Equal(3L, r.Rows[0][0]);
    }

    [Fact]
    public void Select_CountStar_UnionsSparseFieldTimestamps()
    {
        using var db = OpenWithSchema(Options());
        // 稀疏写入：t=1000 只有 usage，t=2000 只有 count，t=3000 两者都有。
        // 行/时刻并集 = {1000, 2000, 3000} = 3；旧实现按 field 点累加会得 4。
        SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, region, usage) VALUES (1000, 'h1', 'cn', 1.0)");
        SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, region, count) VALUES (2000, 'h1', 'cn', 20)");
        SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, region, usage, count) VALUES (3000, 'h1', 'cn', 3.0, 30)");

        var r = Select(db, "SELECT count(*) FROM cpu WHERE host = 'h1'");

        Assert.Single(r.Rows);
        Assert.Equal(3L, r.Rows[0][0]);
    }

    [Fact]
    public void Select_CountStar_AcrossMultipleSeries_CountsAllRows()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        // h1 有 3 个时刻，h2 有 2 个时刻，跨 series 共 5 行。
        var r = Select(db, "SELECT count(*) FROM cpu");

        Assert.Single(r.Rows);
        Assert.Equal(5L, r.Rows[0][0]);
    }

    [Fact]
    public void Select_CountStar_GroupByTime_CountsRowsPerBucket()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        // 1s 桶：h1 的 1000/2000/3000 落 [1000,2000,3000) 三个桶各 1 行，加上 h2 的 1500→1000桶、2500→2000桶。
        var r = Select(db, "SELECT time, count(*) FROM cpu GROUP BY time(1000ms)");

        var byBucket = r.Rows.ToDictionary(row => (long)row[0]!, row => (long)row[1]!);
        Assert.Equal(2L, byBucket[1000]); // h1@1000 + h2@1500
        Assert.Equal(2L, byBucket[2000]); // h1@2000 + h2@2500
        Assert.Equal(1L, byBucket[3000]); // h1@3000
    }

    [Fact]
    public void Select_SumStar_Throws()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "SELECT sum(*) FROM cpu WHERE host = 'h1'"));
    }

    [Fact]
    public void Select_CountField_CountsOnlyThatField()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT count(usage) FROM cpu WHERE host = 'h1'");

        Assert.Equal(3L, r.Rows[0][0]);
    }

    [Fact]
    public void Select_SumAvgMinMax_SingleSeries()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db,
            "SELECT sum(usage), avg(usage), min(usage), max(usage) FROM cpu WHERE host = 'h1'");

        Assert.Equal(["sum(usage)", "avg(usage)", "min(usage)", "max(usage)"], r.Columns);
        Assert.Single(r.Rows);
        Assert.Equal(6.0, (double)r.Rows[0][0]!);
        Assert.Equal(2.0, (double)r.Rows[0][1]!);
        Assert.Equal(1.0, (double)r.Rows[0][2]!);
        Assert.Equal(3.0, (double)r.Rows[0][3]!);
    }

    [Fact]
    public void Select_FirstLast_SingleSeries()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT first(usage), last(usage) FROM cpu WHERE host = 'h1'");

        Assert.Equal(1.0, (double)r.Rows[0][0]!);
        Assert.Equal(3.0, (double)r.Rows[0][1]!);
    }

    [Fact]
    public void Select_AggregateMergesAcrossSeries()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT sum(usage), count(usage) FROM cpu");

        // h1: 1+2+3=6, h2: 5+6=11 → 17；count=5
        Assert.Equal(17.0, (double)r.Rows[0][0]!);
        Assert.Equal(5L, r.Rows[0][1]);
    }

    [Fact]
    public void Select_GroupByTime_AggregatesPerBucket()
    {
        using var db = OpenWithSchema(Options());
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, usage) VALUES " +
            "(0, 'h1', 1.0), " +
            "(500, 'h1', 2.0), " +
            "(1000, 'h1', 3.0), " +
            "(1500, 'h1', 4.0), " +
            "(2000, 'h1', 5.0)");

        var r = Select(db, "SELECT avg(usage), count(usage) FROM cpu GROUP BY time(1000ms)");

        // 桶 [0,1000): 1.0,2.0 avg=1.5 cnt=2
        // 桶 [1000,2000): 3.0,4.0 avg=3.5 cnt=2
        // 桶 [2000,3000): 5.0 avg=5.0 cnt=1
        Assert.Equal(3, r.Rows.Count);
        Assert.Equal(1.5, (double)r.Rows[0][0]!);
        Assert.Equal(2L, r.Rows[0][1]);
        Assert.Equal(3.5, (double)r.Rows[1][0]!);
        Assert.Equal(2L, r.Rows[1][1]);
        Assert.Equal(5.0, (double)r.Rows[2][0]!);
        Assert.Equal(1L, r.Rows[2][1]);
    }

    [Fact]
    public void Select_GroupByTime_WithTimeProjection_ReturnsBucketStart()
    {
        // ROADMAP #129.1：GROUP BY time(...) 下允许 SELECT time, agg(...)；time 是桶起始时间。
        using var db = OpenWithSchema(Options());
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, usage) VALUES " +
            "(0, 'h1', 1.0), " +
            "(500, 'h1', 2.0), " +
            "(1000, 'h1', 3.0), " +
            "(1500, 'h1', 4.0), " +
            "(2000, 'h1', 5.0)");

        var r = Select(db, "SELECT time, avg(usage) FROM cpu GROUP BY time(1000ms)");

        Assert.Equal(["time", "avg(usage)"], r.Columns);
        Assert.Equal(3, r.Rows.Count);
        Assert.Equal(0L, r.Rows[0][0]);
        Assert.Equal(1.5, (double)r.Rows[0][1]!);
        Assert.Equal(1000L, r.Rows[1][0]);
        Assert.Equal(3.5, (double)r.Rows[1][1]!);
        Assert.Equal(2000L, r.Rows[2][0]);
        Assert.Equal(5.0, (double)r.Rows[2][1]!);
    }

    [Fact]
    public void Select_GroupByTime_WithBucketAlias_ReturnsAliasedBucketStart()
    {
        using var db = OpenWithSchema(Options());
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, usage) VALUES " +
            "(0, 'h1', 1.0), " +
            "(1000, 'h1', 3.0)");

        var r = Select(db, "SELECT time AS bucket, avg(usage) FROM cpu GROUP BY time(1000ms)");

        Assert.Equal(["bucket", "avg(usage)"], r.Columns);
        Assert.Equal(0L, r.Rows[0][0]);
        Assert.Equal(1000L, r.Rows[1][0]);
    }

    [Fact]
    public void Select_AggregateLookup_IsCaseInsensitive()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT SuM(usage), CoUnT(usage) FROM cpu WHERE host = 'h1'");

        Assert.Equal(6.0, (double)r.Rows[0][0]!);
        Assert.Equal(3L, r.Rows[0][1]);
    }

    [Fact]
    public void Select_EmptyTimeWindow_ReturnsZeroRows()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);

        var r = Select(db, "SELECT sum(usage) FROM cpu WHERE time >= 999999 AND time < 1000000000");

        Assert.Empty(r.Rows);
    }

    // ── 错误场景 ───────────────────────────────────────────────────────────

    [Fact]
    public void Select_MissingMeasurement_Throws()
    {
        using var db = Tsdb.Open(Options());
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "SELECT * FROM ghost"));
    }

    [Fact]
    public void Select_UnknownColumn_Throws()
    {
        using var db = OpenWithSchema(Options());
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "SELECT bogus FROM cpu"));
    }

    [Fact]
    public void Select_OrInWhere_ReturnsUnionAcrossTags()
    {
        // #217：顶层 OR 走残差逐点求值——匹配 host='h1' 或 'h2' 的全部行。
        using var db = OpenWithSchema(Options());
        Seed(db);
        var r = Select(db, "SELECT time FROM cpu WHERE host = 'h1' OR host = 'h2'");
        // h1 有 3 行、h2 有 2 行，共 5 行。
        Assert.Equal(5, r.Rows.Count);
    }

    [Fact]
    public void Select_FieldInWhere_FiltersByFieldValue()
    {
        // #217：字段谓词 usage > 2 走残差逐点求值。
        using var db = OpenWithSchema(Options());
        Seed(db);
        var r = Select(db, "SELECT usage FROM cpu WHERE usage > 2.0");
        // usage 值：h1={1,2,3}, h2={5,6} → > 2 的有 {3,5,6} 三行。
        Assert.Equal([3.0, 5.0, 6.0], r.Rows.Select(row => (double)row[0]!).OrderBy(x => x));
    }

    [Fact]
    public void Select_MixedAggregateAndBareColumn_Throws()
    {
        using var db = OpenWithSchema(Options());
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "SELECT host, sum(usage) FROM cpu"));
    }

    [Fact]
    public void Select_GroupByTimeWithoutAggregate_Throws()
    {
        using var db = OpenWithSchema(Options());
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "SELECT usage FROM cpu GROUP BY time(1m)"));
    }

    [Fact]
    public void Select_FirstWithMultipleSeries_Throws()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "SELECT first(usage) FROM cpu"));
    }

    [Fact]
    public void Select_TagInequality_FiltersByResidual()
    {
        // #217：非等值 tag 比较（host != 'h1'）走残差逐点求值。
        using var db = OpenWithSchema(Options());
        Seed(db);
        var r = Select(db, "SELECT time FROM cpu WHERE host != 'h1'");
        // 仅 h2 的 2 行。
        Assert.Equal(2, r.Rows.Count);
    }

    [Fact]
    public void Select_MixedTagTimeAndFieldResidual()
    {
        // #217：tag（下推）+ time（下推）+ 字段谓词（残差）混合。
        using var db = OpenWithSchema(Options());
        Seed(db);
        var r = Select(db, "SELECT time, usage FROM cpu WHERE host = 'h1' AND time >= 2000 AND usage > 2.0");
        // h1 中 time>=2000 且 usage>2 → 只有 time=3000/usage=3。
        Assert.Single(r.Rows);
        Assert.Equal(3000L, r.Rows[0][0]);
        Assert.Equal(3.0, r.Rows[0][1]);
    }

    [Fact]
    public void Select_FieldAndField_Residual()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);
        // usage > 1 AND count >= 30：h1 {u,c}={(1,10),(2,20),(3,30)}，h2={(5,50),(6,60)}。
        // usage>1 且 count>=30 → h1@3000(3,30), h2@1500(5,50), h2@2500(6,60) 共 3 行。
        var r = Select(db, "SELECT time FROM cpu WHERE usage > 1.0 AND count >= 30");
        Assert.Equal([1500L, 2500L, 3000L], r.Rows.Select(row => (long)row[0]!).OrderBy(x => x));
    }

    [Fact]
    public void Select_FieldResidual_SkipsNullField()
    {
        // #217：残差字段谓词按三值逻辑跳过该字段缺失（NULL）的时刻。
        using var db = OpenWithSchema(Options());
        // ok 字段只在 t=1000 写入；其余时刻 ok 缺失（NULL）。
        SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, usage, ok) VALUES (1000, 'h1', 5.0, TRUE)");
        SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, usage) VALUES (2000, 'h1', 6.0)");

        var r = Select(db, "SELECT time FROM cpu WHERE ok = TRUE");
        // 只有 t=1000 的 ok 有值且为 TRUE；t=2000 的 ok 为 NULL → UNKNOWN → 排除。
        Assert.Single(r.Rows);
        Assert.Equal(1000L, r.Rows[0][0]);
    }

    [Fact]
    public void Select_Aggregate_WithFieldResidual()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);
        // avg(usage) WHERE usage > 2：h1 usage>2 = {3}, h2 = {5,6} → avg(3,5,6)=4.666...
        var r = Select(db, "SELECT avg(usage) FROM cpu WHERE usage > 2.0");
        Assert.Single(r.Rows);
        Assert.Equal((3.0 + 5.0 + 6.0) / 3.0, (double)r.Rows[0][0]!, 6);
    }

    [Fact]
    public void Select_CountStar_WithFieldResidual()
    {
        using var db = OpenWithSchema(Options());
        Seed(db);
        // count(*) WHERE usage > 2：usage>2 的行 = {h1@3000, h2@1500, h2@2500} = 3。
        var r = Select(db, "SELECT count(*) FROM cpu WHERE usage > 2.0");
        Assert.Equal(3L, r.Rows.Single()[0]);
    }

    [Fact]
    public void Select_LatestFastPathDisabled_WithResidual_ReturnsCorrectLatestMatch()
    {
        // #217：ORDER BY time DESC LIMIT 1 + 字段残差时，latest 快路径须禁用，返回正确的最新匹配点。
        using var db = OpenWithSchema(Options());
        Seed(db);  // h1 usage {1000:1, 2000:2, 3000:3}
        // 最新的点是 3000(usage=3)，但加 usage < 3 后最新匹配应为 2000(usage=2)。
        var r = Select(db, "SELECT time, usage FROM cpu WHERE host = 'h1' AND usage < 3.0 ORDER BY time DESC LIMIT 1");
        Assert.Single(r.Rows);
        Assert.Equal(2000L, r.Rows[0][0]);
        Assert.Equal(2.0, r.Rows[0][1]);
    }

    [Fact]
    public void Select_ConflictingTagFilters_Throws()
    {
        using var db = OpenWithSchema(Options());
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "SELECT * FROM cpu WHERE host = 'h1' AND host = 'h2'"));
    }

    [Fact]
    public void Select_AggregateOnStringField_ThrowsOnSum()
    {
        using var db = OpenWithSchema(Options());
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, label) VALUES (1000, 'h1', 'a'), (2000, 'h1', 'b')");
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "SELECT sum(label) FROM cpu WHERE host = 'h1'"));
    }
}
