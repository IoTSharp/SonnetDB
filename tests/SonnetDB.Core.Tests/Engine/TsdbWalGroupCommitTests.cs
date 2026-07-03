using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Engine.Retention;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;
using SonnetDB.Wal;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>
/// <see cref="Tsdb"/> 的 WAL group-commit 行为测试。
/// </summary>
public sealed class TsdbWalGroupCommitTests : IDisposable
{
    private readonly string _tempDir;

    public TsdbWalGroupCommitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TsdbOptions MakeOptions(TimeSpan flushWindow) =>
        new()
        {
            RootDirectory = _tempDir,
            WalBufferSize = 64 * 1024,
            SyncWalOnEveryWrite = true,
            WalGroupCommit = new WalGroupCommitOptions
            {
                Enabled = true,
                FlushWindow = flushWindow,
            },
            FlushPolicy = new MemTableFlushPolicy
            {
                MaxPoints = 10_000_000,
                MaxBytes = 1024L * 1024 * 1024,
                MaxAge = TimeSpan.FromHours(24),
            },
            SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
            Retention = new RetentionPolicy { Enabled = false },
        };

    [Fact]
    public void WriteMany_WithGroupCommit_PerformsSingleWalSync()
    {
        using var db = Tsdb.Open(MakeOptions(TimeSpan.FromMilliseconds(50)));
        db.CreateMeasurement(CreateMetricSchema());

        Point[] points = Enumerable.Range(0, 128)
            .Select(i => CreateMetricPoint(i, "h1"))
            .ToArray();

        int written = db.WriteMany(points);

        Assert.Equal(points.Length, written);
        Assert.Equal(points.Length, (int)db.MemTable.PointCount);
        Assert.Equal(1L, db.WalSyncCount);
    }

    [Fact]
    public async Task ConcurrentWrites_WithGroupCommit_CoalescesWalSync()
    {
        const int writeCount = 32;
        using var db = Tsdb.Open(MakeOptions(TimeSpan.FromMilliseconds(500)));
        db.CreateMeasurement(CreateMetricSchema());

        using var ready = new CountdownEvent(writeCount);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, writeCount)
            .Select(async i =>
            {
                ready.Signal();
                await start.Task.ConfigureAwait(false);
                db.Write(CreateMetricPoint(i, "h1"));
            })
            .ToArray();

        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        start.SetResult();

        await Task.WhenAll(tasks);

        Assert.Equal(writeCount, (int)db.MemTable.PointCount);
        // On a loaded CI runner tasks may not all coalesce into a single WAL sync.
        // Verify coalescing did occur (syncs << writeCount), not that exactly 1 happened.
        Assert.True(db.WalSyncCount <= 4,
            $"Expected at most 4 WAL syncs (group-commit coalescing), but got {db.WalSyncCount}");
    }

    [Fact]
    public void CrashRecovery_AfterGroupCommittedWriteMany_ReplaysAllWalRecords()
    {
        const int pointCount = 64;
        var options = MakeOptions(TimeSpan.FromMilliseconds(50));

        var db = Tsdb.Open(options);
        db.CreateMeasurement(CreateMetricSchema());
        var points = Enumerable.Range(0, pointCount)
            .Select(i => CreateMetricPoint(i, "h1"))
            .ToArray();

        db.WriteMany(points);

        var writePointsOnDisk = ReplayWritePoints(TsdbPaths.WalDir(_tempDir)).Count;
        Assert.Equal(pointCount, writePointsOnDisk);

        db.CrashSimulationCloseWal();

        using var reopened = Tsdb.Open(options);
        Assert.Equal(1, reopened.Catalog.Count);
        Assert.Equal(pointCount, (int)reopened.MemTable.PointCount);
    }

    [Fact]
    public void DirectSync_WindowZero_SyncsEveryWrite()
    {
        // window=0 走直写路径：fsync 被推迟到 _writeSync 之外执行（S10/C5），但每写仍必须同步一次。
        using var db = Tsdb.Open(MakeOptions(TimeSpan.Zero));
        db.CreateMeasurement(CreateMetricSchema());

        db.Write(CreateMetricPoint(0, "h1"));
        db.Write(CreateMetricPoint(1, "h1"));
        db.Write(CreateMetricPoint(2, "h1"));

        // 三次独立写各一次 fsync（无 group-commit 合并）。
        Assert.Equal(3L, db.WalSyncCount);
        Assert.Equal(3, (int)db.MemTable.PointCount);
    }

    [Fact]
    public void DirectSync_WindowZero_CrashRecovery_ReplaysAllWalRecords()
    {
        var options = MakeOptions(TimeSpan.Zero);

        var db = Tsdb.Open(options);
        db.CreateMeasurement(CreateMetricSchema());
        for (int i = 0; i < 16; i++)
            db.Write(CreateMetricPoint(i, "h1"));

        db.CrashSimulationCloseWal();

        using var reopened = Tsdb.Open(options);
        Assert.Equal(16, (int)reopened.MemTable.PointCount);
    }

    [Fact]
    public void DirectSync_WindowZero_DisposeWithPendingWrites_DoesNotThrow()
    {
        // 直写 ticket 的推迟 fsync 若与 Dispose 竞争，应静默跳过 ODE 而非抛给调用方（S11）。
        var db = Tsdb.Open(MakeOptions(TimeSpan.Zero));
        db.CreateMeasurement(CreateMetricSchema());
        db.Write(CreateMetricPoint(0, "h1"));

        // 正常 Dispose 不应抛异常。
        db.Dispose();
    }

    private static MeasurementSchema CreateMetricSchema()
        => MeasurementSchema.Create(
            "metric",
            new[]
            {
                new MeasurementColumn("host", MeasurementColumnRole.Tag, FieldType.String),
                new MeasurementColumn("value", MeasurementColumnRole.Field, FieldType.Float64),
            });

    private static Point CreateMetricPoint(int index, string host)
        => Point.Create(
            "metric",
            1_700_000_000_000L + index,
            new Dictionary<string, string> { ["host"] = host },
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromDouble(index) });

    private static List<WritePointRecord> ReplayWritePoints(string walDir)
    {
        var records = new List<WritePointRecord>();
        foreach (var segment in WalSegmentLayout.Enumerate(walDir))
        {
            using var reader = WalReader.Open(segment.Path);
            records.AddRange(reader.Replay().OfType<WritePointRecord>());
        }

        return records;
    }
}
