using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Query;

/// <summary>
/// <see cref="QueryEngine"/> 聚合查询（<see cref="AggregateQuery"/>）的单元测试。
/// </summary>
public sealed class QueryEngineAggregateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TsdbOptions _opts;

    public QueryEngineAggregateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _opts = new TsdbOptions
        {
            RootDirectory = _tempDir,
            WalBufferSize = 64 * 1024,
            FlushPolicy = new MemTableFlushPolicy { MaxPoints = 1_000_000, MaxBytes = 64 * 1024 * 1024 },
            SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── 辅助方法 ──────────────────────────────────────────────────────────────

    private static Point MakePoint(string measurement, long ts, string field, FieldValue value)
        => Point.Create(measurement, ts,
            new Dictionary<string, string> { ["host"] = "h1" },
            new Dictionary<string, FieldValue> { [field] = value });

    // ── Double 全局聚合 ────────────────────────────────────────────────────

    [Fact]
    public void Execute_GlobalCount_Double_Correct()
    {
        using var db = Tsdb.Open(_opts);

        for (int i = 1; i <= 10; i++)
            db.Write(MakePoint("m", i * 100L, "v", FieldValue.FromDouble(i)));

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Count, 0);
        var result = db.Query.Execute(q).ToList();

        Assert.Single(result);
        Assert.Equal(10L, result[0].Count);
        Assert.Equal(10.0, result[0].Value, precision: 9);
    }

    [Fact]
    public void Execute_GlobalSum_Double_Correct()
    {
        using var db = Tsdb.Open(_opts);

        for (int i = 1; i <= 10; i++)
            db.Write(MakePoint("m", i * 100L, "v", FieldValue.FromDouble(i)));

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Sum, 0);
        var result = db.Query.Execute(q).ToList();

        Assert.Single(result);
        Assert.Equal(55.0, result[0].Value, precision: 9);  // 1+2+...+10 = 55
    }

    [Fact]
    public void Execute_GlobalMin_Double_Correct()
    {
        using var db = Tsdb.Open(_opts);

        foreach (var v in new double[] { 5.0, 2.0, 8.0, 1.0, 9.0 })
            db.Write(MakePoint("m", v.GetHashCode() + 1000L, "v", FieldValue.FromDouble(v)));

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Min, 0);
        var result = db.Query.Execute(q).ToList();

        Assert.Single(result);
        Assert.Equal(1.0, result[0].Value, precision: 9);
    }

    [Fact]
    public void Execute_GlobalMax_Double_Correct()
    {
        using var db = Tsdb.Open(_opts);

        foreach (var (ts, v) in new[] { (1L, 5.0), (2L, 2.0), (3L, 8.0), (4L, 1.0), (5L, 9.0) })
            db.Write(MakePoint("m", ts * 100L, "v", FieldValue.FromDouble(v)));

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Max, 0);
        var result = db.Query.Execute(q).ToList();

        Assert.Single(result);
        Assert.Equal(9.0, result[0].Value, precision: 9);
    }

    [Fact]
    public void Execute_GlobalAvg_Double_Correct()
    {
        using var db = Tsdb.Open(_opts);

        for (int i = 1; i <= 5; i++)
            db.Write(MakePoint("m", i * 100L, "v", FieldValue.FromDouble(i)));

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Avg, 0);
        var result = db.Query.Execute(q).ToList();

        Assert.Single(result);
        Assert.Equal(3.0, result[0].Value, precision: 9);  // (1+2+3+4+5)/5 = 3
    }

    [Fact]
    public void Execute_GlobalSum_AfterFlush_UsesPersistedBlockMetadataCorrectly()
    {
        using var db = Tsdb.Open(_opts);

        for (int i = 1; i <= 10; i++)
            db.Write(MakePoint("m", i * 100L, "v", FieldValue.FromDouble(i)));

        db.FlushNow();

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Sum, 0);
        var result = db.Query.Execute(q).ToList();

        Assert.Single(result);
        Assert.Equal(55.0, result[0].Value, precision: 9);
    }

    [Fact]
    public void Execute_GlobalAvg_AfterFlush_UsesPersistedBlockMetadataCorrectly()
    {
        using var db = Tsdb.Open(_opts);

        for (int i = 1; i <= 5; i++)
            db.Write(MakePoint("m", i * 100L, "v", FieldValue.FromDouble(i)));

        db.FlushNow();

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Avg, 0);
        var result = db.Query.Execute(q).ToList();

        Assert.Single(result);
        Assert.Equal(3.0, result[0].Value, precision: 9);
    }

    [Fact]
    public void Execute_GlobalSum_PartialRange_FallsBackAndRemainsCorrect()
    {
        using var db = Tsdb.Open(_opts);

        for (int i = 1; i <= 10; i++)
            db.Write(MakePoint("m", i * 100L, "v", FieldValue.FromDouble(i)));

        db.FlushNow();

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", new TimeRange(250L, 850L), Aggregator.Sum, 0);
        var result = db.Query.Execute(q).ToList();

        Assert.Single(result);
        Assert.Equal(33.0, result[0].Value, precision: 9);
    }

    // ── Long 全局聚合 ─────────────────────────────────────────────────────

    [Fact]
    public void Execute_GlobalCount_Long_Correct()
    {
        using var db = Tsdb.Open(_opts);

        for (int i = 1; i <= 5; i++)
            db.Write(MakePoint("m", i * 100L, "v", FieldValue.FromLong(i * 10L)));

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Count, 0);
        var result = db.Query.Execute(q).ToList();

        Assert.Single(result);
        Assert.Equal(5L, result[0].Count);
    }

    [Fact]
    public void Execute_GlobalSum_Long_Correct()
    {
        using var db = Tsdb.Open(_opts);

        for (int i = 1; i <= 4; i++)
            db.Write(MakePoint("m", i * 100L, "v", FieldValue.FromLong(i)));

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Sum, 0);
        var result = db.Query.Execute(q).ToList();

        Assert.Single(result);
        Assert.Equal(10.0, result[0].Value, precision: 9);  // 1+2+3+4=10
    }

    // ── Boolean 全局聚合 ──────────────────────────────────────────────────

    [Fact]
    public void Execute_GlobalSum_Bool_TrueIsOne()
    {
        using var db = Tsdb.Open(_opts);

        // 3 true, 2 false
        foreach (var (ts, v) in new[] { (1L, true), (2L, false), (3L, true), (4L, false), (5L, true) })
            db.Write(MakePoint("m", ts * 100L, "v", FieldValue.FromBool(v)));

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Sum, 0);
        var result = db.Query.Execute(q).ToList();

        Assert.Single(result);
        Assert.Equal(3.0, result[0].Value, precision: 9);  // 1+0+1+0+1=3
    }

    // ── First / Last ──────────────────────────────────────────────────────

    [Fact]
    public void Execute_GlobalFirst_ReturnsFirstPoint()
    {
        using var db = Tsdb.Open(_opts);

        // 写入乱序，应按时间戳排序后取第一个
        foreach (var (ts, v) in new[] { (300L, 30.0), (100L, 10.0), (200L, 20.0) })
            db.Write(MakePoint("m", ts, "v", FieldValue.FromDouble(v)));

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.First, 0);
        var result = db.Query.Execute(q).ToList();

        Assert.Single(result);
        Assert.Equal(10.0, result[0].Value, precision: 9);  // ts=100 的 value=10.0
    }

    [Fact]
    public void Execute_GlobalLast_ReturnsLastPoint()
    {
        using var db = Tsdb.Open(_opts);

        foreach (var (ts, v) in new[] { (300L, 30.0), (100L, 10.0), (200L, 20.0) })
            db.Write(MakePoint("m", ts, "v", FieldValue.FromDouble(v)));

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Last, 0);
        var result = db.Query.Execute(q).ToList();

        Assert.Single(result);
        Assert.Equal(30.0, result[0].Value, precision: 9);  // ts=300 的 value=30.0
    }

    // ── 桶聚合 (GROUP BY time) ────────────────────────────────────────────

    [Fact]
    public void Execute_BucketAgg_ThreeBuckets_CorrectCountAndValue()
    {
        using var db = Tsdb.Open(_opts);

        // 桶大小 1000ms，写 3 个桶各 10 点
        // 桶 0: [0, 999]，桶 1: [1000, 1999]，桶 2: [2000, 2999]
        for (int bucket = 0; bucket < 3; bucket++)
            for (int j = 0; j < 10; j++)
            {
                long ts = bucket * 1000L + j * 100L;
                db.Write(MakePoint("m", ts, "v", FieldValue.FromDouble(bucket * 100.0 + j)));
            }

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Count, 1000L);
        var result = db.Query.Execute(q).ToList();

        Assert.Equal(3, result.Count);
        Assert.All(result, b => Assert.Equal(10L, b.Count));

        // 验证 BucketStart 升序
        Assert.Equal(0L, result[0].BucketStart);
        Assert.Equal(1000L, result[1].BucketStart);
        Assert.Equal(2000L, result[2].BucketStart);
    }

    [Fact]
    public void Execute_BucketAgg_SumPerBucket_Correct()
    {
        using var db = Tsdb.Open(_opts);

        // 桶 0: [0, 999] 写 v=1,2,3
        // 桶 1: [1000, 1999] 写 v=10,20,30
        foreach (var (ts, v) in new[] { (100L, 1.0), (200L, 2.0), (300L, 3.0) })
            db.Write(MakePoint("m", ts, "v", FieldValue.FromDouble(v)));
        foreach (var (ts, v) in new[] { (1100L, 10.0), (1200L, 20.0), (1300L, 30.0) })
            db.Write(MakePoint("m", ts, "v", FieldValue.FromDouble(v)));

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Sum, 1000L);
        var result = db.Query.Execute(q).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal(6.0, result[0].Value, precision: 9);   // 1+2+3
        Assert.Equal(60.0, result[1].Value, precision: 9);  // 10+20+30
    }

    // ── 跨 MemTable + 多段聚合 ────────────────────────────────────────────

    [Fact]
    public void Execute_CrossFlushAndMemTable_AggregatesCorrectly()
    {
        using var db = Tsdb.Open(_opts);

        // 写 50 点，Flush
        for (int i = 0; i < 50; i++)
            db.Write(MakePoint("m", i * 10L, "v", FieldValue.FromDouble(i)));
        db.FlushNow();

        // 再写 50 点（不 Flush，在 MemTable 中）
        for (int i = 50; i < 100; i++)
            db.Write(MakePoint("m", i * 10L, "v", FieldValue.FromDouble(i)));

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Count, 0);
        var result = db.Query.Execute(q).ToList();

        Assert.Single(result);
        Assert.Equal(100L, result[0].Count);
    }

    // ── String 字段 count → count-only，不读取字段值 ─────────────────────

    [Fact]
    public void Execute_CountStringField_ReturnsPointCount()
    {
        using var db = Tsdb.Open(_opts);

        db.Write(MakePoint("m", 100L, "s", FieldValue.FromString("hello")));

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "s", TimeRange.All, Aggregator.Count, 0);
        var result = db.Query.Execute(q).ToList();

        Assert.Single(result);
        Assert.Equal(1L, result[0].Count);
        Assert.Equal(1.0, result[0].Value);
    }

    // ── 空数据集 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_NoData_ReturnsEmptySequence()
    {
        using var db = Tsdb.Open(_opts);

        var q = new AggregateQuery(0xDEAD_BEEF_CAFE_BABEuL, "v", TimeRange.All, Aggregator.Count, 0);
        var result = db.Query.Execute(q).ToList();

        Assert.Empty(result);
    }

    // ── BucketEndExclusive 验证 ───────────────────────────────────────────

    [Fact]
    public void Execute_BucketAgg_BucketEndExclusive_IsCorrect()
    {
        using var db = Tsdb.Open(_opts);

        // 桶大小 500ms，写两个桶
        db.Write(MakePoint("m", 100L, "v", FieldValue.FromDouble(1.0)));
        db.Write(MakePoint("m", 600L, "v", FieldValue.FromDouble(2.0)));

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Count, 500L);
        var result = db.Query.Execute(q).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal(0L, result[0].BucketStart);
        Assert.Equal(500L, result[0].BucketEndExclusive);
        Assert.Equal(500L, result[1].BucketStart);
        Assert.Equal(1000L, result[1].BucketEndExclusive);
    }

    // ── 元数据精度与快路径回归测试 ──────────────────────────────────────────

    [Fact]
    public void Execute_GlobalMin_Float64_NonRepresentable_ReturnsExactValue()
    {
        // 0.1 等小数无法被 float 精确表示；旧实现把 min/max 截断为 float 后写入元数据，
        // 导致 Min/Max 查询返回错误的 0.10000000149011612 之类的值。
        // 新实现应在不可无损降精度时跳过 min/max 元数据，回落到扫描，从而保持精度。
        using var db = Tsdb.Open(_opts);

        double[] values = { 0.1, 0.2, 0.3, 0.4, 0.5 };
        for (int i = 0; i < values.Length; i++)
            db.Write(MakePoint("m", (i + 1) * 100L, "v", FieldValue.FromDouble(values[i])));
        db.FlushNow();

        var entry = db.Catalog.Snapshot().First();

        var minQuery = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Min, 0);
        var minResult = db.Query.Execute(minQuery).Single();
        Assert.Equal(0.1, minResult.Value);  // 严格等于，不允许 float 截断误差

        var maxQuery = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Max, 0);
        var maxResult = db.Query.Execute(maxQuery).Single();
        Assert.Equal(0.5, maxResult.Value);
    }

    [Fact]
    public void Execute_GlobalSum_Float64_NonRepresentable_StillUsesPersistedSum()
    {
        // 即便 min/max 元数据被跳过，sum 元数据仍应写入，Sum/Avg/Count 走快路径。
        // 该用例只校验数值正确，性能由基准测试覆盖。
        using var db = Tsdb.Open(_opts);

        double[] values = { 0.1, 0.2, 0.3 };
        for (int i = 0; i < values.Length; i++)
            db.Write(MakePoint("m", (i + 1) * 100L, "v", FieldValue.FromDouble(values[i])));
        db.FlushNow();

        var entry = db.Catalog.Snapshot().First();
        var sumResult = db.Query.Execute(
            new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Sum, 0)).Single();
        Assert.Equal(0.6, sumResult.Value, precision: 9);

        var avgResult = db.Query.Execute(
            new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Avg, 0)).Single();
        Assert.Equal(0.2, avgResult.Value, precision: 9);

        var countResult = db.Query.Execute(
            new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Count, 0)).Single();
        Assert.Equal(3L, countResult.Count);
    }

    [Fact]
    public void Execute_BucketAgg_Sum_AfterFlush_UsesPerBlockMetadataFastPath()
    {
        // 桶大小 1000ms，每 100ms 一个 int64 点，每桶 10 个点；写入并 Flush 后单 block 整体落入单桶，
        // 应走桶聚合元数据快路径并返回正确结果。
        using var db = Tsdb.Open(_opts);

        for (int bucket = 0; bucket < 3; bucket++)
            for (int j = 0; j < 10; j++)
            {
                long ts = bucket * 1000L + j * 100L;
                db.Write(MakePoint("m", ts, "v", FieldValue.FromLong(bucket * 100L + j)));
            }
        db.FlushNow();

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Sum, 1000L);
        var result = db.Query.Execute(q).ToList();

        Assert.Equal(3, result.Count);
        // 桶 0: 0+1+...+9 = 45
        // 桶 1: 100+101+...+109 = 1045
        // 桶 2: 200+201+...+209 = 2045
        Assert.Equal(45.0, result[0].Value, precision: 9);
        Assert.Equal(1045.0, result[1].Value, precision: 9);
        Assert.Equal(2045.0, result[2].Value, precision: 9);
    }

    [Fact]
    public void Execute_BucketAgg_MinMax_AfterFlush_UsesPerBlockMetadataFastPath()
    {
        // Int64 在 int32 范围内时 min/max 元数据无损，桶快路径应使用并返回正确值。
        using var db = Tsdb.Open(_opts);

        for (int bucket = 0; bucket < 2; bucket++)
            for (int j = 0; j < 5; j++)
            {
                long ts = bucket * 1000L + j * 100L;
                db.Write(MakePoint("m", ts, "v", FieldValue.FromLong(bucket * 10L + j)));
            }
        db.FlushNow();

        var entry = db.Catalog.Snapshot().First();
        var minResult = db.Query.Execute(
            new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Min, 1000L)).ToList();
        Assert.Equal(2, minResult.Count);
        Assert.Equal(0.0, minResult[0].Value, precision: 9);    // 桶 0 min
        Assert.Equal(10.0, minResult[1].Value, precision: 9);   // 桶 1 min

        var maxResult = db.Query.Execute(
            new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Max, 1000L)).ToList();
        Assert.Equal(2, maxResult.Count);
        Assert.Equal(4.0, maxResult[0].Value, precision: 9);    // 桶 0 max
        Assert.Equal(14.0, maxResult[1].Value, precision: 9);   // 桶 1 max
    }

    [Fact]
    public void Execute_BucketAgg_BlockSpansMultipleBuckets_FallsBackToScan()
    {
        // 当 block 横跨多个桶时桶快路径不能用，必须回退到扫描；结果仍要正确。
        using var db = Tsdb.Open(_opts);

        // 5 个点跨 0..2000 的范围，桶大小 500 → 跨多个桶
        long[] timestamps = { 0L, 500L, 1000L, 1500L, 2000L };
        for (int i = 0; i < timestamps.Length; i++)
            db.Write(MakePoint("m", timestamps[i], "v", FieldValue.FromLong(i + 1)));
        db.FlushNow();

        var entry = db.Catalog.Snapshot().First();
        var q = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Sum, 500L);
        var result = db.Query.Execute(q).ToList();

        Assert.Equal(5, result.Count);
        for (int i = 0; i < 5; i++)
            Assert.Equal((double)(i + 1), result[i].Value, precision: 9);
    }
}
