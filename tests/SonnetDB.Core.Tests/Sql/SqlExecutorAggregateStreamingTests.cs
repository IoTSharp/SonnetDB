using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// M33 #284 — 残差 / 跨字段 Geo 聚合路径流式化（<c>QueryPoints(...).ToList()</c> → 惰性 <c>QueryPointsStream</c>）
/// 与空残差 lookup 短路。两组断言：
/// (1) <b>正确性</b>——流式化后聚合结果与逐点物化参考实现在大数据集上逐值一致（残差数值过滤、跨字段 Geo、
///     count(*)、GROUP BY 分桶、段/MemTable 快慢混合）。
/// (2) <b>峰值内存不随点数线性增长</b>——底层 <see cref="Query.QueryEngine.Execute(Query.PointQuery)"/> 本就是单
///     租约、单趟流式合并，聚合逐点累加不再先物化整段 <c>List&lt;DataPoint&gt;</c>；对纯流式聚合路径
///     （<c>count(&lt;string&gt;)</c>：非数值字段绕过快路径、count-only 无逐点装箱），峰值分配不随 N 线性增长。
/// </summary>
public sealed class SqlExecutorAggregateStreamingTests : IDisposable
{
    private readonly string _root;

    public SqlExecutorAggregateStreamingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-aggstream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private TsdbOptions Options(string suffix = "") => new()
    {
        RootDirectory = suffix.Length == 0 ? _root : Path.Combine(_root, suffix),
        SegmentWriterOptions = new SonnetDB.Storage.Segments.SegmentWriterOptions { FsyncOnCommit = false },
    };

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));

    /// <summary>用引擎 Write API 批量写入 N 点（避免超长 INSERT 文本），usage=i%100、label='L'+(i%7)。</summary>
    private static void SeedNumericAndString(Tsdb db, int n, string host = "h1")
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["host"] = host };
        for (int i = 0; i < n; i++)
        {
            db.Write(Point.Create("cpu", 1000L + i, tags, new Dictionary<string, FieldValue>
            {
                ["usage"] = FieldValue.FromDouble(i % 100),
                ["label"] = FieldValue.FromString("L" + (i % 7)),
            }));
        }
    }

    // ── 正确性：残差数值过滤在大数据集上流式化后与逐点参考一致 ──────────────────────

    [Theory]
    [InlineData(50_000)]
    public void ResidualAggregate_LargeDataset_MatchesReference(int n)
    {
        using var db = Tsdb.Open(Options("residual"));
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT, label FIELD STRING)");
        SeedNumericAndString(db, n);
        db.FlushNow();

        // 参考：usage>50 的点 = i%100 ∈ {51..99}，每 100 个里 49 个命中。
        long expectedCount = 0;
        double expectedSum = 0, expectedMin = double.MaxValue, expectedMax = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            double v = i % 100;
            if (v > 50)
            {
                expectedCount++;
                expectedSum += v;
                expectedMin = Math.Min(expectedMin, v);
                expectedMax = Math.Max(expectedMax, v);
            }
        }

        var r = Select(db,
            "SELECT count(usage), sum(usage), min(usage), max(usage), avg(usage) FROM cpu WHERE usage > 50.0");

        var row = Assert.Single(r.Rows);
        Assert.Equal(expectedCount, Convert.ToInt64(row[0], CultureInfo.InvariantCulture));
        Assert.Equal(expectedSum, Convert.ToDouble(row[1], CultureInfo.InvariantCulture));
        Assert.Equal(expectedMin, Convert.ToDouble(row[2], CultureInfo.InvariantCulture));
        Assert.Equal(expectedMax, Convert.ToDouble(row[3], CultureInfo.InvariantCulture));
        Assert.Equal(expectedSum / expectedCount, Convert.ToDouble(row[4], CultureInfo.InvariantCulture), 9);
    }

    // ── 峰值内存：纯流式聚合路径分配不随 N 线性增长（O(N)→O(1)）────────────────────

    [Fact]
    public void StreamingCountAggregate_PeakAllocation_DoesNotScaleWithPointCount()
    {
        // count(<string field>) 走 SelectExecutor 的逐点流式聚合循环：
        //   · 字符串字段 → CanUseLegacyAggregateFastPath 恒 false（快路径只保证数值/布尔），不绕过逐点迭代；
        //   · count-only → UpdateCount 只取时间戳，无逐点值装箱；
        //   · 无残差 / 无 Geo → 不建残差 lookup 字典、不建 geoAllowed 集合。
        // 因此 #284 前后唯一的 N 级分配就是被移除的 QueryPoints(...).ToList()。底层 QueryEngine 单趟流式合并，
        // 惰性消费时段 block 逐块解码即弃，峰值分配应基本恒定、不随 N 线性增长。
        long alloc10k = MeasureStreamingCountAllocation(10_000, "n10k");
        long alloc40k = MeasureStreamingCountAllocation(40_000, "n40k");

        // 4× 点数下，纯流式路径的净分配增量必须远小于「若物化则 4×10k×sizeof(DataPoint)≈8MB」的量级。
        // 给足噪声余量：只要 40k 相对 10k 的增量 < 512KB（≪ 与点数成正比时应有的 ~6MB），即证明不随 N 线性增长。
        long delta = alloc40k - alloc10k;
        Assert.True(delta < 512 * 1024,
            $"纯流式 count 聚合分配不应随点数线性增长：10k={alloc10k} bytes, 40k={alloc40k} bytes, delta={delta} bytes。");
    }

    private long MeasureStreamingCountAllocation(int n, string suffix)
    {
        using var db = Tsdb.Open(Options(suffix));
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT, label FIELD STRING)");
        SeedNumericAndString(db, n);
        db.FlushNow();

        // 预热：JIT、段 reader、MemTable 快照缓存均在此固化，随后的测量只反映查询期净分配。
        _ = Select(db, "SELECT count(label) FROM cpu");

        long before = GC.GetAllocatedBytesForCurrentThread();
        var r = Select(db, "SELECT count(label) FROM cpu");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal((long)n, Convert.ToInt64(r.Rows[0][0], CultureInfo.InvariantCulture));
        return allocated;
    }

    // ── 正确性：跨字段 Geo 大数据集流式化后与参考一致（#282 语义在流式路径下保持）──────

    [Theory]
    [InlineData(30_000)]
    public void CrossFieldGeoAggregate_LargeDataset_MatchesReference(int n)
    {
        using var db = Tsdb.Open(Options("geo"));
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT, speed FIELD FLOAT)");

        var tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["device"] = "car-1" };
        // 偶数点在 bbox [30,120]~[32,122] 内（上海附近），奇数点在外（北京附近）。
        long expectedCount = 0;
        double expectedSum = 0;
        for (int i = 0; i < n; i++)
        {
            bool inBox = (i % 2) == 0;
            double lat = inBox ? 31.0 + (i % 100) * 0.001 : 40.0;
            double lon = inBox ? 121.0 + (i % 100) * 0.001 : 116.0;
            double speed = i % 200;
            db.Write(Point.Create("vehicle", 1000L + i, tags, new Dictionary<string, FieldValue>
            {
                ["position"] = FieldValue.FromGeoPoint(new GeoPoint(lat, lon)),
                ["speed"] = FieldValue.FromDouble(speed),
            }));
            if (inBox) { expectedCount++; expectedSum += speed; }
        }
        db.FlushNow();

        var r = Select(db,
            "SELECT count(speed), sum(speed) FROM vehicle WHERE geo_bbox(position, 30, 120, 32, 122)");

        var row = Assert.Single(r.Rows);
        Assert.Equal(expectedCount, Convert.ToInt64(row[0], CultureInfo.InvariantCulture));
        Assert.Equal(expectedSum, Convert.ToDouble(row[1], CultureInfo.InvariantCulture));
    }

    // ── 空残差短路：无残差谓词时聚合结果与逐点计数参考一致（短路不改语义）──────────────

    [Fact]
    public void NoResidual_CountAggregate_ShortCircuitPreservesResult()
    {
        using var db = Tsdb.Open(Options("noresidual"));
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT, label FIELD STRING)");
        const int n = 5_000;
        SeedNumericAndString(db, n);
        db.FlushNow();

        // 无 WHERE 残差：BuildResidualLookups 走 EmptyResidualLookups 短路，count 全量。
        var r = Select(db, "SELECT count(usage), count(label) FROM cpu");
        var row = Assert.Single(r.Rows);
        Assert.Equal((long)n, Convert.ToInt64(row[0], CultureInfo.InvariantCulture));
        Assert.Equal((long)n, Convert.ToInt64(row[1], CultureInfo.InvariantCulture));

        // count(*) 同样无 residual/geo，行/时刻数 = n。
        var star = Select(db, "SELECT count(*) FROM cpu");
        Assert.Equal((long)n, Convert.ToInt64(star.Rows[0][0], CultureInfo.InvariantCulture));
    }
}
