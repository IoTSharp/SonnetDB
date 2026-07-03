using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>
/// Flush 原子性回归测试（ROADMAP #190 / S3）：验证在写入 + flush 与并发查询交错时，
/// 查询看到的点数既不会"瞬时跌落"（flush 期间数据丢失窗口），也不会"超过已写入总数"（重复）。
/// 这是 MemTable 双缓冲统一快照（SuperVersion）改造的核心不变式。
/// </summary>
public sealed class TsdbFlushAtomicityTests : IDisposable
{
    private readonly string _tempDir;

    public TsdbFlushAtomicityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TsdbOptions MakeOptions() =>
        new TsdbOptions
        {
            RootDirectory = _tempDir,
            WalBufferSize = 64 * 1024,
            // 关闭后台 flush，测试自行显式 FlushNow，让读写与 flush 的交错完全可控。
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new global::SonnetDB.Engine.Compaction.CompactionPolicy { Enabled = false },
            FlushPolicy = new MemTableFlushPolicy { MaxPoints = 1_000_000, MaxBytes = 64 * 1024 * 1024 },
            SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
        };

    private static Point MakePoint(long ts, double value) =>
        Point.Create("m", ts,
            new Dictionary<string, string> { ["host"] = "s1" },
            new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromDouble(value) });

    private static ulong SeriesIdFor() =>
        SeriesId.Compute(new SeriesKey("m", new Dictionary<string, string> { ["host"] = "s1" }));

    [Fact]
    public void Flush_ConcurrentQuery_NoGapNoDuplicate()
    {
        using var db = Tsdb.Open(MakeOptions());
        var seriesId = SeriesIdFor();

        // 预写一个点，确保 series/schema 已注册。
        db.Write(MakePoint(0, 0.0));

        const int totalWrites = 4000;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        // upperBound 在每次 Write *之前* 自增，因此"已可见点数 <= upperBound"恒成立
        // （数据只会在 Write 期间/之后变可见，此时计数器已提前 +1）。这样无需容差即可
        // 严格断言"无重复/无凭空多出"。
        long upperBound = 1; // 含预写点
        var stop = false;

        // 读线程：持续查询点数。不变式：
        //   1) 观测值 <= upperBound（Write 前已自增）——绝不"凭空多出"（无重复）。
        //   2) 观测值单调非递减——flush 不会让已可见的数据瞬时消失（无丢失窗口，修 #190）。
        var reader = new Thread(() =>
        {
            long lastSeen = 0;
            while (!Volatile.Read(ref stop))
            {
                int observed;
                try
                {
                    observed = db.Query.Execute(
                        new PointQuery(seriesId, "v", TimeRange.All)).Count();
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"query threw: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                long bound = Volatile.Read(ref upperBound);
                if (observed > bound)
                    failures.Enqueue($"duplicate/over-count: observed={observed} > upperBound={bound}");
                if (observed < lastSeen)
                    failures.Enqueue($"gap/regression: observed={observed} < lastSeen={lastSeen}");

                lastSeen = observed;
            }
        });
        reader.Start();

        // 写线程（主线程）：持续写入并周期性 flush，制造 flush 与查询的交错。
        for (int i = 1; i <= totalWrites; i++)
        {
            Volatile.Write(ref upperBound, upperBound + 1);
            db.Write(MakePoint(i, i));

            if (i % 200 == 0)
                db.FlushNow(); // flush 期间读线程仍在并发查询
        }

        Volatile.Write(ref stop, true);
        reader.Join();

        Assert.True(failures.IsEmpty,
            "flush 原子性被破坏：\n" + string.Join("\n", failures));

        // 终态一致性：flush 全部数据后，查询总数应等于写入总数。
        db.FlushNow();
        int finalCount = db.Query.Execute(new PointQuery(seriesId, "v", TimeRange.All)).Count();
        Assert.Equal(totalWrites + 1, finalCount);
    }

    [Fact]
    public void Flush_SwapsActiveMemTable_OldDataMovesToSegmentAtomically()
    {
        using var db = Tsdb.Open(MakeOptions());
        var seriesId = SeriesIdFor();

        for (int i = 0; i < 10; i++)
            db.Write(MakePoint(1000 + i, i));

        var activeBefore = db.MemTable;
        Assert.Equal(10, (int)activeBefore.PointCount);

        db.FlushNow();

        // 新契约：flush 后活跃 MemTable 是一个全新的空实例（而非原地 Reset）。
        var activeAfter = db.MemTable;
        Assert.NotSame(activeBefore, activeAfter);
        Assert.Equal(0, (int)activeAfter.PointCount);

        // 数据仍可查（已原子转移到 segment），总数不变。
        int count = db.Query.Execute(new PointQuery(seriesId, "v", TimeRange.All)).Count();
        Assert.Equal(10, count);
        Assert.Equal(1, db.Segments.SegmentCount);
    }

    // ── Phase 2：flush 移出写锁（#204 / C2）────────────────────────────────────

    [Fact]
    public void ConcurrentFlushNow_SingleFlight_NoCorruption()
    {
        using var db = Tsdb.Open(MakeOptions());
        var seriesId = SeriesIdFor();

        // 多线程并发写 + 并发 FlushNow：single-flight 泵串行处理，段 ID 顺序发布、无损坏。
        long ts = 0;
        var writers = new List<Thread>();
        for (int t = 0; t < 4; t++)
        {
            var writer = new Thread(() =>
            {
                for (int i = 0; i < 500; i++)
                    db.Write(MakePoint(Interlocked.Increment(ref ts), i));
            });
            writers.Add(writer);
            writer.Start();
        }

        var flushers = new List<Thread>();
        for (int t = 0; t < 3; t++)
        {
            var flusher = new Thread(() =>
            {
                for (int i = 0; i < 20; i++)
                    db.FlushNow();
            });
            flushers.Add(flusher);
            flusher.Start();
        }

        foreach (var w in writers) w.Join();
        foreach (var f in flushers) f.Join();

        // 最终 flush 后总数守恒：2000 点全部可查，无重复无丢失。
        db.FlushNow();
        int count = db.Query.Execute(new PointQuery(seriesId, "v", TimeRange.All)).Count();
        Assert.Equal(2000, count);
    }

    [Fact]
    public void Dispose_DrainsInFlightFlush_NoDataLoss()
    {
        var opts = MakeOptions();

        using (var db = Tsdb.Open(opts))
        {
            for (int i = 0; i < 500; i++)
                db.Write(MakePoint(i, i));

            // 触发后台异步 flush（不等待），随即 Dispose——Dispose 必须排空在飞 flush + 落盘剩余数据。
            db.SignalFlush();
            // 立即 Dispose（可能 flush 尚在泵中）
        }

        // 重开：全部 500 点应可恢复（要么在段、要么 WAL replay），不丢。
        using var db2 = Tsdb.Open(opts);
        var seriesId = SeriesIdFor();
        int count = db2.Query.Execute(new PointQuery(seriesId, "v", TimeRange.All)).Count();
        Assert.Equal(500, count);
    }

    [Fact]
    public void IntToFloatPromotion_UnderFlush_NoTypeMismatch_NoLoss()
    {
        using var db = Tsdb.Open(MakeOptions());

        // 先写 Int64 值。
        for (int i = 0; i < 50; i++)
            db.Write(Point.Create("mixed", i,
                new Dictionary<string, string> { ["host"] = "s1" },
                new Dictionary<string, FieldValue> { ["val"] = FieldValue.FromLong(i) }));

        // 写一个 Float64 值触发 int→float 提升：应密封旧 Int64 表（异步落段），新表接 Float64，
        // 不抛 FieldType mismatch。
        db.Write(Point.Create("mixed", 100,
            new Dictionary<string, string> { ["host"] = "s1" },
            new Dictionary<string, FieldValue> { ["val"] = FieldValue.FromDouble(1.5) }));

        // 继续写 Float64。
        for (int i = 101; i < 150; i++)
            db.Write(Point.Create("mixed", i,
                new Dictionary<string, string> { ["host"] = "s1" },
                new Dictionary<string, FieldValue> { ["val"] = FieldValue.FromDouble(i) }));

        db.FlushNow();

        var seriesId = SeriesId.Compute(new SeriesKey("mixed",
            new Dictionary<string, string> { ["host"] = "s1" }));
        var points = db.Query.Execute(new PointQuery(seriesId, "val", TimeRange.All)).ToList();

        // 50 个 Int64 + 1 + 49 个 Float64 = 100 点，全部可查（跨源 int/float 兼容）。
        Assert.Equal(100, points.Count);
    }
}
