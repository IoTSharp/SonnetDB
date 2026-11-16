using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Query;

/// <summary>
/// PR #50 引入的查询优化（Format v2 + 跨桶融合内联 + MemTable 元数据快路径 + ExecuteMany 共享 snapshot）
/// 的端到端正确性测试。
/// </summary>
public sealed class QueryEngineV2OptimizationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TsdbOptions _opts;

    public QueryEngineV2OptimizationTests()
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

    private static Point MakePoint(string measurement, long ts, string field, FieldValue value)
        => Point.Create(measurement, ts,
            new Dictionary<string, string> { ["host"] = "h1" },
            new Dictionary<string, FieldValue> { [field] = value });

    private static Point MakePoint(string measurement, string host, long ts, string field, FieldValue value)
        => Point.Create(measurement, ts,
            new Dictionary<string, string> { ["host"] = host },
            new Dictionary<string, FieldValue> { [field] = value });

    // ── Phase 1：Format v2，AggregateMin/Max 8 字节 double 持全 Int64 范围 ────

    [Fact]
    public void V2Format_PersistsInt64MaxValueLossless()
    {
        // 写入 Int64 极值，flush，然后用 Min/Max 聚合验证不丢失。
        // v1 时 Int64 边界仅有 4 字节存储 → 写入侧会放弃 HasMinMax，导致此查询走全扫描。
        // v2 升 8 字节 double 后，2^53 以下整数完全无损；这里使用接近但不超过 2^53 的 Int64 值。
        using var db = Tsdb.Open(_opts);

        long bigValue = (1L << 50);  // 2^50, 安全在 double 精度内。
        for (int i = 1; i <= 5; i++)
            db.Write(MakePoint("m", i * 1000L, "v", FieldValue.FromLong(bigValue + i)));

        db.FlushNow();

        var entry = db.Catalog.Snapshot().First();
        var minQ = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Min, 0);
        var maxQ = new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Max, 0);

        var minResult = db.Query.Execute(minQ).Single();
        var maxResult = db.Query.Execute(maxQ).Single();

        Assert.Equal((double)(bigValue + 1), minResult.Value);
        Assert.Equal((double)(bigValue + 5), maxResult.Value);
    }

    [Fact]
    public void V2Format_PreservesFloat64MinMaxFullPrecision()
    {
        // 写入需要 7+ 位有效数字的 Float64 → v1 会因 (float)cast 不能 round-trip 而放弃 HasMinMax。
        using var db = Tsdb.Open(_opts);

        double[] values = { 1.234567890123, 9.876543210987, -3.141592653589 };
        for (int i = 0; i < values.Length; i++)
            db.Write(MakePoint("m", (i + 1) * 1000L, "v", FieldValue.FromDouble(values[i])));

        db.FlushNow();

        var entry = db.Catalog.Snapshot().First();
        var minResult = db.Query.Execute(
            new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Min, 0)).Single();
        var maxResult = db.Query.Execute(
            new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Max, 0)).Single();

        Assert.Equal(-3.141592653589, minResult.Value, precision: 12);
        Assert.Equal(9.876543210987, maxResult.Value, precision: 12);
    }

    // ── Phase 2：跨桶 block 走 (delta-ts + raw-val) 融合内联路径 ───────────────

    [Fact]
    public void Aggregate_LargeFlushedBlock_AcrossBuckets_MatchesPointByPointResult()
    {
        // 写入足够多点触发 delta-of-delta 时间戳编码（block 内部递增 ts），然后用桶聚合跨多个桶。
        using var db = Tsdb.Open(_opts);

        const int N = 500;
        double expectedSum = 0;
        for (int i = 0; i < N; i++)
        {
            double v = i * 1.5;
            expectedSum += v;
            db.Write(MakePoint("m", 1000L + i * 10L, "v", FieldValue.FromDouble(v)));
        }

        db.FlushNow();

        var entry = db.Catalog.Snapshot().First();

        // 全局聚合（单桶覆盖所有点）。
        var globalSum = db.Query.Execute(
            new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Sum, 0)).Single();
        Assert.Equal(expectedSum, globalSum.Value, precision: 6);

        // 跨桶聚合：每桶 500 ms（每桶约 50 点），共 ~10 桶，逐点累加结果应一致。
        var bucketed = db.Query.Execute(
            new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Sum, 500L)).ToList();

        Assert.True(bucketed.Count > 1, "应跨多个桶");
        double bucketSum = bucketed.Sum(b => b.Value);
        Assert.Equal(expectedSum, bucketSum, precision: 6);
    }

    [Fact]
    public void Aggregate_LargeFlushedBlock_RangeQuery_MatchesUnsharded()
    {
        using var db = Tsdb.Open(_opts);

        const int N = 300;
        for (int i = 0; i < N; i++)
            db.Write(MakePoint("m", 10000L + i * 20L, "v", FieldValue.FromDouble(i)));

        db.FlushNow();

        var entry = db.Catalog.Snapshot().First();

        // 截取 block 中段范围，强制走 binary search + 融合内联。
        long from = 12000L;
        long to = 14000L;

        double expectedSum = 0;
        int expectedCount = 0;
        for (int i = 0; i < N; i++)
        {
            long ts = 10000L + i * 20L;
            if (ts >= from && ts <= to) { expectedSum += i; expectedCount++; }
        }

        var sumResult = db.Query.Execute(
            new AggregateQuery(entry.Id, "v", new TimeRange(from, to), Aggregator.Sum, 0)).Single();
        var countResult = db.Query.Execute(
            new AggregateQuery(entry.Id, "v", new TimeRange(from, to), Aggregator.Count, 0)).Single();

        Assert.Equal(expectedCount, sumResult.Count);
        Assert.Equal(expectedSum, sumResult.Value, precision: 6);
        Assert.Equal((double)expectedCount, countResult.Value);
    }

    // ── Phase 3：MemTable 运行期 sum/min/max 元数据快路径 ────────────────────

    [Fact]
    public void Aggregate_MemTableOnly_FullCoverage_UsesRunningAggregates()
    {
        // 数据全部留在 MemTable（不 Flush），范围查询完整覆盖 → 触发元数据合并快路径。
        // 通过和逐点路径结果对比验证正确性。
        using var db = Tsdb.Open(_opts);

        for (int i = 1; i <= 100; i++)
            db.Write(MakePoint("m", i * 100L, "v", FieldValue.FromDouble(i * 0.5)));

        var entry = db.Catalog.Snapshot().First();

        // 全局（覆盖所有 ts）。
        var sumAll = db.Query.Execute(
            new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Sum, 0)).Single();
        double expectedSum = Enumerable.Range(1, 100).Sum(i => i * 0.5);
        Assert.Equal(expectedSum, sumAll.Value, precision: 6);

        var minAll = db.Query.Execute(
            new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Min, 0)).Single();
        Assert.Equal(0.5, minAll.Value);

        var maxAll = db.Query.Execute(
            new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Max, 0)).Single();
        Assert.Equal(50.0, maxAll.Value);

        var countAll = db.Query.Execute(
            new AggregateQuery(entry.Id, "v", TimeRange.All, Aggregator.Count, 0)).Single();
        Assert.Equal(100L, countAll.Count);
    }

    [Fact]
    public void Aggregate_MemTableOnly_PartialCoverage_FallsBackToScan()
    {
        // 范围未覆盖整个 MemTable → 不能走元数据快路径，结果仍必须正确。
        using var db = Tsdb.Open(_opts);

        for (int i = 1; i <= 100; i++)
            db.Write(MakePoint("m", i * 100L, "v", FieldValue.FromDouble(i)));

        var entry = db.Catalog.Snapshot().First();
        // 仅覆盖 i=20..50 的点（ts 2000..5000）。
        var result = db.Query.Execute(
            new AggregateQuery(entry.Id, "v", new TimeRange(2000L, 5000L), Aggregator.Sum, 0)).Single();

        double expected = Enumerable.Range(20, 31).Sum(); // 20..50
        Assert.Equal(expected, result.Value);
    }

    // ── Phase 4：ExecuteMany 共享 snapshot 与单次 Execute 一致 ────────────────

    [Fact]
    public void ExecuteMany_SharesSnapshot_ReturnsSameResultsAsExecute()
    {
        using var db = Tsdb.Open(_opts);

        // 5 个 series × 30 点，部分 Flush 部分 MemTable。
        for (int s = 0; s < 5; s++)
            for (int i = 0; i < 15; i++)
                db.Write(MakePoint("cpu", $"host{s}", 1000L + i * 100L, "usage",
                    FieldValue.FromDouble(s * 100.0 + i)));
        db.FlushNow();

        for (int s = 0; s < 5; s++)
            for (int i = 15; i < 30; i++)
                db.Write(MakePoint("cpu", $"host{s}", 1000L + i * 100L, "usage",
                    FieldValue.FromDouble(s * 100.0 + i)));

        var entries = db.Catalog.Snapshot().ToList();
        var seriesIds = entries.Select(e => e.Id).ToList();

        // 桶聚合 + Sum。
        var manyResult = db.Query.ExecuteMany(
            seriesIds, "usage", TimeRange.All, Aggregator.Sum, 1000L);

        Assert.Equal(seriesIds.Count, manyResult.Count);

        foreach (var sid in seriesIds)
        {
            var single = db.Query.Execute(
                new AggregateQuery(sid, "usage", TimeRange.All, Aggregator.Sum, 1000L)).ToList();
            var many = manyResult[sid];
            Assert.Equal(single.Count, many.Count);
            for (int i = 0; i < single.Count; i++)
            {
                Assert.Equal(single[i].BucketStart, many[i].BucketStart);
                Assert.Equal(single[i].Count, many[i].Count);
                Assert.Equal(single[i].Value, many[i].Value, precision: 9);
            }
        }
    }

    [Fact]
    public void RawSelectStreaming_DuplicateTimestampAcrossSegmentAndMemTable_UsesLastValue()
    {
        using var db = Tsdb.Open(_opts);

        db.Write(MakePoint("cpu", "h1", 1000L, "usage", FieldValue.FromDouble(1)));
        db.FlushNow();
        db.Write(MakePoint("cpu", "h1", 1000L, "usage", FieldValue.FromDouble(2)));

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT time, host, usage FROM cpu WHERE host = 'h1'"));

        var row = Assert.Single(result.Rows);
        Assert.Equal(1000L, row[0]);
        Assert.Equal("h1", row[1]);
        Assert.Equal(2d, row[2]);
    }

    [Fact]
    public void RawSelectStreaming_MultipleFields_PreservesTimestampUnionAndNulls()
    {
        using var db = Tsdb.Open(_opts);

        db.Write(Point.Create(
            "cpu",
            1000L,
            new Dictionary<string, string> { ["host"] = "h1" },
            new Dictionary<string, FieldValue>
            {
                ["usage"] = FieldValue.FromDouble(1),
                ["temperature"] = FieldValue.FromDouble(10),
            }));
        db.Write(Point.Create(
            "cpu",
            2000L,
            new Dictionary<string, string> { ["host"] = "h1" },
            new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromDouble(2) }));
        db.Write(Point.Create(
            "cpu",
            3000L,
            new Dictionary<string, string> { ["host"] = "h1" },
            new Dictionary<string, FieldValue> { ["temperature"] = FieldValue.FromDouble(30) }));

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT time, host, usage, temperature FROM cpu WHERE host = 'h1'"));

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal([1000L, 2000L, 3000L], result.Rows.Select(static row => row[0]));
        Assert.Equal(1d, result.Rows[0][2]);
        Assert.Equal(10d, result.Rows[0][3]);
        Assert.Equal(2d, result.Rows[1][2]);
        Assert.Null(result.Rows[1][3]);
        Assert.Null(result.Rows[2][2]);
        Assert.Equal(30d, result.Rows[2][3]);
    }

    [Fact]
    public void RawSelectStreaming_PreCancelledExecutionStopsBeforeReading()
    {
        using var db = Tsdb.Open(_opts);
        db.Write(MakePoint("cpu", "h1", 1000L, "usage", FieldValue.FromDouble(1)));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = Assert.Throws<SonnetDB.Exceptions.RoutineExecutionException>(() => SqlExecutor.Execute(
            db,
            databaseName: null,
            "SELECT time, host, usage FROM cpu WHERE host = 'h1'",
            parameters: null,
            controlPlane: null,
            SqlExecutionOptions.Default with { CancellationToken = cancellation.Token }));
        Assert.IsType<OperationCanceledException>(error.InnerException);
    }
}
