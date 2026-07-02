using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Query;

/// <summary>
/// <see cref="QueryEngine"/> 原始点查询（<see cref="PointQuery"/>）的单元测试。
/// </summary>
public sealed class QueryEnginePointQueryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TsdbOptions _opts;

    public QueryEnginePointQueryTests()
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

    private static Point MakePoint(string measurement, long ts, string field, FieldValue value,
        IReadOnlyDictionary<string, string>? tags = null)
        => Point.Create(measurement, ts,
            tags ?? new Dictionary<string, string> { ["host"] = "h1" },
            new Dictionary<string, FieldValue> { [field] = value });

    // ── 仅 MemTable ──────────────────────────────────────────────────────────

    [Fact]
    public void Execute_MemTableOnly_Returns100Points()
    {
        using var db = Tsdb.Open(_opts);

        for (int i = 0; i < 100; i++)
            db.Write(MakePoint("cpu", 1000L + i, "usage", FieldValue.FromDouble(i)));

        var entry = db.Catalog.Snapshot().First();
        var q = new PointQuery(entry.Id, "usage", TimeRange.All);
        var results = db.Query.Execute(q).ToList();

        Assert.Equal(100, results.Count);
        // 验证升序
        for (int i = 1; i < results.Count; i++)
            Assert.True(results[i].Timestamp >= results[i - 1].Timestamp);
    }

    // ── 仅段（Flush 后 MemTable 清空）────────────────────────────────────────

    [Fact]
    public void Execute_SegmentOnly_AfterFlush_ReturnsAllPoints()
    {
        using var db = Tsdb.Open(_opts);

        for (int i = 0; i < 50; i++)
            db.Write(MakePoint("sensor", 2000L + i, "temp", FieldValue.FromDouble(20.0 + i)));

        db.FlushNow();

        // MemTable 应已清空
        Assert.Equal(0L, db.MemTable.PointCount);

        var entry = db.Catalog.Snapshot().First();
        var q = new PointQuery(entry.Id, "temp", TimeRange.All);
        var results = db.Query.Execute(q).ToList();

        Assert.Equal(50, results.Count);
        for (int i = 1; i < results.Count; i++)
            Assert.True(results[i].Timestamp >= results[i - 1].Timestamp);
    }

    // ── 跨 MemTable + 段 ─────────────────────────────────────────────────────

    [Fact]
    public void Execute_CrossMemTableAndSegment_Returns100PointsInOrder()
    {
        using var db = Tsdb.Open(_opts);

        // 写 50 点，Flush
        for (int i = 0; i < 50; i++)
            db.Write(MakePoint("metric", 1000L + i * 2, "v", FieldValue.FromDouble(i)));

        db.FlushNow();

        // 再写 50 点（偶数时间戳 + 1，使它们插入段数据之间）
        for (int i = 0; i < 50; i++)
            db.Write(MakePoint("metric", 1001L + i * 2, "v", FieldValue.FromDouble(i + 100)));

        var entry = db.Catalog.Snapshot().First();
        var q = new PointQuery(entry.Id, "v", TimeRange.All);
        var results = db.Query.Execute(q).ToList();

        Assert.Equal(100, results.Count);
        for (int i = 1; i < results.Count; i++)
            Assert.True(results[i].Timestamp >= results[i - 1].Timestamp);
    }

    // ── 时间窗剪枝 ───────────────────────────────────────────────────────────

    [Fact]
    public void Execute_WithTimeRange_ReturnsOnlyPointsInRange()
    {
        using var db = Tsdb.Open(_opts);

        for (int i = 0; i < 100; i++)
            db.Write(MakePoint("m", 1000L + i, "f", FieldValue.FromDouble(i)));

        var entry = db.Catalog.Snapshot().First();
        var q = new PointQuery(entry.Id, "f", new TimeRange(1020L, 1049L));
        var results = db.Query.Execute(q).ToList();

        Assert.Equal(30, results.Count);
        Assert.Equal(1020L, results.First().Timestamp);
        Assert.Equal(1049L, results.Last().Timestamp);
    }

    // ── Limit ───────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_WithLimit_ReturnsFirstNPoints()
    {
        using var db = Tsdb.Open(_opts);

        for (int i = 0; i < 100; i++)
            db.Write(MakePoint("m", 1000L + i, "f", FieldValue.FromDouble(i)));

        var entry = db.Catalog.Snapshot().First();
        var q = new PointQuery(entry.Id, "f", TimeRange.All, Limit: 10);
        var results = db.Query.Execute(q).ToList();

        Assert.Equal(10, results.Count);
        Assert.Equal(1000L, results[0].Timestamp);
        Assert.Equal(1009L, results[9].Timestamp);
    }

    [Fact]
    public void TryGetLatestPoint_MemTableOnly_ReturnsLatestPoint()
    {
        using var db = Tsdb.Open(_opts);

        for (int i = 0; i < 100; i++)
            db.Write(MakePoint("m", 1000L + i, "f", FieldValue.FromDouble(i)));

        var entry = db.Catalog.Snapshot().First();
        var found = db.Query.TryGetLatestPoint(entry.Id, "f", TimeRange.All, out var latest);

        Assert.True(found);
        Assert.Equal(1099L, latest.Timestamp);
        Assert.Equal(99.0, latest.Value.AsDouble());
    }

    [Fact]
    public void TryGetLatestPoint_AfterFlush_ReturnsLatestSegmentPoint()
    {
        using var db = Tsdb.Open(_opts);

        for (int i = 0; i < 50; i++)
            db.Write(MakePoint("m", 2000L + i, "f", FieldValue.FromDouble(i)));

        db.FlushNow();

        var entry = db.Catalog.Snapshot().First();
        var found = db.Query.TryGetLatestPoint(entry.Id, "f", TimeRange.All, out var latest);

        Assert.True(found);
        Assert.Equal(2049L, latest.Timestamp);
        Assert.Equal(49.0, latest.Value.AsDouble());
    }

    [Fact]
    public void TryGetLatestPoint_CrossMemTableAndSegment_ReturnsNewestPoint()
    {
        using var db = Tsdb.Open(_opts);

        for (int i = 0; i < 50; i++)
            db.Write(MakePoint("m", 1000L + i, "f", FieldValue.FromDouble(i)));
        db.FlushNow();

        for (int i = 0; i < 50; i++)
            db.Write(MakePoint("m", 2000L + i, "f", FieldValue.FromDouble(i + 100)));

        var entry = db.Catalog.Snapshot().First();
        var found = db.Query.TryGetLatestPoint(entry.Id, "f", TimeRange.All, out var latest);

        Assert.True(found);
        Assert.Equal(2049L, latest.Timestamp);
        Assert.Equal(149.0, latest.Value.AsDouble());
    }

    [Fact]
    public void TryGetLatestPoint_WithTimeRange_ReturnsLatestPointInRange()
    {
        using var db = Tsdb.Open(_opts);

        for (int i = 0; i < 100; i++)
            db.Write(MakePoint("m", 1000L + i, "f", FieldValue.FromDouble(i)));

        var entry = db.Catalog.Snapshot().First();
        var found = db.Query.TryGetLatestPoint(entry.Id, "f", new TimeRange(1020L, 1049L), out var latest);

        Assert.True(found);
        Assert.Equal(1049L, latest.Timestamp);
        Assert.Equal(49.0, latest.Value.AsDouble());
    }

    [Fact]
    public void TryGetLatestPoint_SkipsTombstonedLatestPoint()
    {
        using var db = Tsdb.Open(_opts);

        for (int i = 0; i < 10; i++)
            db.Write(MakePoint("m", 1000L + i, "f", FieldValue.FromDouble(i)));

        var entry = db.Catalog.Snapshot().First();
        db.Delete(entry.Id, "f", 1009L, 1009L);

        var found = db.Query.TryGetLatestPoint(entry.Id, "f", TimeRange.All, out var latest);

        Assert.True(found);
        Assert.Equal(1008L, latest.Timestamp);
        Assert.Equal(8.0, latest.Value.AsDouble());
    }

    // ── 空数据集 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_NoDataForSeries_ReturnsEmpty()
    {
        using var db = Tsdb.Open(_opts);

        // 随机 SeriesId（不存在于 DB 中）
        var q = new PointQuery(0xDEAD_BEEF_CAFE_BABEuL, "f", TimeRange.All);
        var results = db.Query.Execute(q).ToList();

        Assert.Empty(results);
    }

    // ── 跨 Flush + 时间窗 ────────────────────────────────────────────────────

    [Fact]
    public void Execute_CrossFlushWithTimeRange_FiltersCorrectly()
    {
        using var db = Tsdb.Open(_opts);

        // 写 [0, 99]，flush
        for (int i = 0; i < 100; i++)
            db.Write(MakePoint("data", i * 10L, "v", FieldValue.FromDouble(i)));
        db.FlushNow();

        // 再写 [100, 199]
        for (int i = 100; i < 200; i++)
            db.Write(MakePoint("data", i * 10L, "v", FieldValue.FromDouble(i)));

        var entry = db.Catalog.Snapshot().First();
        // 查询 [500, 1500]（跨段和 MemTable）
        var q = new PointQuery(entry.Id, "v", new TimeRange(500L, 1500L));
        var results = db.Query.Execute(q).ToList();

        Assert.True(results.Count > 0);
        Assert.All(results, dp =>
        {
            Assert.True(dp.Timestamp >= 500L);
            Assert.True(dp.Timestamp <= 1500L);
        });
        for (int i = 1; i < results.Count; i++)
            Assert.True(results[i].Timestamp >= results[i - 1].Timestamp);
    }
}
