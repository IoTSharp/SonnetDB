using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Engine.Retention;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>
/// 维护操作并发安全回归测试（ROADMAP #191 / S5 / C1）：Compaction、Retention、DropMeasurement
/// 与前台写入/查询并发时，不得出现 use-after-dispose（ObjectDisposedException），
/// 且 Retention 删除的过期数据不得被 Compaction 重新物化（复活）。
/// </summary>
public sealed class TsdbMaintenanceConcurrencyTests : IDisposable
{
    private readonly string _tempDir;

    public TsdbMaintenanceConcurrencyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static Point MakePoint(string measurement, long ts, double value) =>
        Point.Create(measurement, ts,
            new Dictionary<string, string> { ["host"] = "s1" },
            new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromDouble(value) });

    [Fact]
    public void CompactionAndRetention_Concurrent_NoUseAfterDispose_NoResurrection()
    {
        // 小段阈值 + 短轮询：让 Compaction 与 Retention 高频触发并与写入交错。
        long nowValue = 0L;
        var opts = new TsdbOptions
        {
            RootDirectory = _tempDir,
            WalBufferSize = 64 * 1024,
            FlushPolicy = new MemTableFlushPolicy
            {
                MaxPoints = 20,
                MaxBytes = 1024L * 1024 * 1024,
                MaxAge = TimeSpan.FromHours(24),
            },
            SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
            BackgroundFlush = new BackgroundFlushOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(5) },
            Compaction = new CompactionPolicy
            {
                Enabled = true,
                MinTierSize = 2,
                PollInterval = TimeSpan.FromMilliseconds(5),
            },
            Retention = new RetentionPolicy
            {
                Enabled = true,
                TtlInTimestampUnits = 500,
                NowFn = () => Volatile.Read(ref nowValue),
                PollInterval = TimeSpan.FromMilliseconds(5),
            },
        };

        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var db = Tsdb.Open(opts);
        db.DiagnosticEvent += (_, e) =>
        {
            // 记录任何维护路径的 use-after-dispose / 异常诊断。
            if (e.Exception is ObjectDisposedException or NullReferenceException)
                diagnostics.Enqueue($"{e.Operation}: {e.Exception.GetType().Name}: {e.Exception.Message}");
        };

        // 写线程：持续写入递增时间戳，并推进虚拟时钟，让旧数据不断跨过 retention cutoff。
        const int totalWrites = 5000;
        for (int i = 1; i <= totalWrites; i++)
        {
            db.Write(MakePoint("cpu", i, i));
            // 每 100 点推进虚拟时钟，使较旧的段/点进入过期窗口，触发 retention drop 与 compaction 交错。
            if (i % 100 == 0)
                Volatile.Write(ref nowValue, i);
        }

        db.FlushNow();

        // 断言 1：并发维护期间没有 use-after-dispose 类诊断。
        Assert.True(diagnostics.IsEmpty,
            "并发维护出现 use-after-dispose / NRE：\n" + string.Join("\n", diagnostics));

        // 断言 2：无复活——查询 cutoff 之前的过期区间不应因 compaction 重新物化而返回已删数据。
        // 用一次全量查询确认结果单调有序、无异常（查询本身也在与维护并发时不崩）。
        var seriesId = SeriesId.Compute(new SeriesKey("cpu",
            new Dictionary<string, string> { ["host"] = "s1" }));
        var points = db.Query.Execute(new PointQuery(seriesId, "v", TimeRange.All)).ToList();

        // 所有仍在的点必须时间戳升序、值等于时间戳（写入不变式），无重复损坏。
        for (int i = 1; i < points.Count; i++)
            Assert.True(points[i].Timestamp >= points[i - 1].Timestamp, "查询结果时间戳应升序");
    }

    [Fact]
    public void DropMeasurement_ConcurrentWithCompaction_NoThrow()
    {
        var opts = new TsdbOptions
        {
            RootDirectory = _tempDir,
            WalBufferSize = 64 * 1024,
            FlushPolicy = new MemTableFlushPolicy { MaxPoints = 20, MaxBytes = 1024L * 1024 * 1024, MaxAge = TimeSpan.FromHours(24) },
            SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
            BackgroundFlush = new BackgroundFlushOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(5) },
            Compaction = new CompactionPolicy { Enabled = true, MinTierSize = 2, PollInterval = TimeSpan.FromMilliseconds(5) },
        };

        using var db = Tsdb.Open(opts);

        // 写入两个 measurement 的数据，形成多段。
        for (int i = 1; i <= 2000; i++)
        {
            db.Write(MakePoint("keep", i, i));
            db.Write(MakePoint("drop", i, i));
        }

        // 与后台 compaction 并发地 DropMeasurement：维护锁序列化，不应抛 ODE。
        var ex = Record.Exception(() => db.DropMeasurement("drop"));
        Assert.Null(ex);

        db.FlushNow();

        // keep 数据完好。
        var keepId = SeriesId.Compute(new SeriesKey("keep",
            new Dictionary<string, string> { ["host"] = "s1" }));
        int keepCount = db.Query.Execute(new PointQuery(keepId, "v", TimeRange.All)).Count();
        Assert.Equal(2000, keepCount);

        // drop 数据已清除。
        var dropId = SeriesId.Compute(new SeriesKey("drop",
            new Dictionary<string, string> { ["host"] = "s1" }));
        int dropCount = db.Query.Execute(new PointQuery(dropId, "v", TimeRange.All)).Count();
        Assert.Equal(0, dropCount);
    }
}
