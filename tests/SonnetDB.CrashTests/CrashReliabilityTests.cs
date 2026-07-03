using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Segments;
using SonnetDB.Wal;
using Xunit;

namespace SonnetDB.CrashTests;

/// <summary>
/// 可靠性套件：使用真子进程与 <see cref="Process.Kill(bool)"/> 注入崩溃。
/// </summary>
public sealed class CrashReliabilityTests : IDisposable
{
    private readonly string _tempDir;

    public CrashReliabilityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sndb-crash-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void crash_kill9_during_fsync_ReopenReplaysOnlyValidWalRecords()
    {
        string root = RunKillScenario("crash_kill9_during_fsync", TimeSpan.FromMilliseconds(500));

        using var db = Tsdb.Open(MakeOptions(root));

        Assert.True(db.Catalog.Count >= 1);
        Assert.True(db.MemTable.PointCount > 0);
        Assert.True(QueryPointCount(db, "fsync", "kill9") > 0);
    }

    [Fact]
    public void crash_kill9_mid_compaction_ReopenDoesNotDuplicateCommittedSegments()
    {
        string root = RunKillScenario("crash_kill9_mid_compaction", TimeSpan.FromMilliseconds(300));

        using var db = Tsdb.Open(MakeOptions(root, compactionEnabled: false));

        long totalPoints = CountAllSegmentPoints(db) + db.MemTable.PointCount;
        Assert.Equal(128L, totalPoints);
    }

    [Fact]
    public void crash_kill9_after_delete_DeletedRangeStaysDeleted()
    {
        // #194：写入不同步，落段后删除 [50,100]，随即 kill -9。Delete 已强制 fsync WAL Delete 记录，
        // 且单条删除未触发 manifest checkpoint，故恢复只能靠 WAL Delete 记录——删除必须存活，数据不复活。
        string root = RunKillScenario("crash_kill9_after_delete", TimeSpan.FromMilliseconds(300));

        using var db = Tsdb.Open(MakeOptions(root, compactionEnabled: false));

        var entry = Assert.Single(db.Catalog.Find("del", new Dictionary<string, string> { ["host"] = "h" }));

        // 被删区间 [50,100] 恢复后仍为空（未复活）。
        int deletedRange = db.Query.Execute(
            new PointQuery(entry.Id, "v", new TimeRange(50L, 100L))).Count();
        Assert.Equal(0, deletedRange);

        // 未删数据仍在：总数 = 200 - 51 = 149。
        int total = db.Query.Execute(new PointQuery(entry.Id, "v", TimeRange.All)).Count();
        Assert.Equal(149, total);
    }

    [Fact]
    public void crash_kill9_os_flushed_writes_AckedWritesSurviveProcessCrash()
    {
        // #196：默认 FlushWalToOsOnWrite=true，写入不 fsync 也不落段，但每写 flush 到 OS。
        // 进程被 kill -9（非掉电）→ OS page cache 存活 → 重开 WAL replay 应恢复全部 300 个已确认写。
        string root = RunKillScenario("crash_kill9_os_flushed_writes", TimeSpan.FromMilliseconds(300));

        using var db = Tsdb.Open(MakeOptions(root, compactionEnabled: false));

        int total = QueryPointCount(db, "osflush", "h");
        Assert.Equal(300, total);
    }

    [Fact]
    public void disk_full_during_wal_append_PreservesPreviouslySyncedRecords()
    {        string root = NewScenarioRoot();
        using (var db = Tsdb.Open(MakeOptions(root)))
        {
            db.Write(MakePoint("disk_full", 1_000L, "h", 1.0));
            Assert.Throws<IOException>(() =>
            {
                string walPath = Assert.Single(WalSegmentLayout.Enumerate(TsdbPaths.WalDir(root))).Path;
                using var blocker = new FileStream(walPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                db.Write(MakePoint("disk_full", 1_001L, "h", 2.0));
            });
        }

        using var reopened = Tsdb.Open(MakeOptions(root));
        Assert.Equal(1, QueryPointCount(reopened, "disk_full", "h"));
    }

    [Fact]
    public void oom_protection_memtable_backpressure_HardCapFlushesBeforeUnboundedGrowth()
    {
        string root = NewScenarioRoot();
        using var db = Tsdb.Open(MakeOptions(root) with
        {
            FlushPolicy = new MemTableFlushPolicy
            {
                MaxBytes = long.MaxValue,
                HardCapBytes = 1,
                MaxPoints = long.MaxValue,
                MaxAge = TimeSpan.MaxValue,
            },
        });

        for (int i = 0; i < 20; i++)
            db.Write(MakePoint("oom", 1_000L + i, "h", i));

        Assert.Equal(0L, db.MemTable.PointCount);
        Assert.True(db.Segments.SegmentCount > 0);
    }

    [Fact]
    public void power_loss_torn_record_ReopenIgnoresTornWalTail()
    {
        string root = NewScenarioRoot();
        using (var db = Tsdb.Open(MakeOptions(root)))
            db.Write(MakePoint("torn", 1_000L, "h", 1.0));

        string walPath = Assert.Single(WalSegmentLayout.Enumerate(TsdbPaths.WalDir(root))).Path;
        using (var stream = new FileStream(walPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            stream.Write([0x42, 0x13, 0x37, 0x00, 0x7F]);

        using var reopened = Tsdb.Open(MakeOptions(root));
        Assert.Equal(1, QueryPointCount(reopened, "torn", "h"));
    }

    [Fact]
    public void power_loss_half_renamed_segment_PendingReplacementIsIgnoredOnReopen()
    {
        string root = NewScenarioRoot();
        using (var db = Tsdb.Open(MakeOptions(root)))
        {
            for (int i = 0; i < 10; i++)
                db.Write(MakePoint("half_rename", 1_000L + i, "old", i));

            db.FlushNow();
        }

        SegmentReplacementManifest.RecordPendingReplacement(root, 100, [1L]);
        File.Copy(TsdbPaths.SegmentPath(root, 1), TsdbPaths.SegmentPath(root, 100), overwrite: true);

        using var reopened = Tsdb.Open(MakeOptions(root));
        var segmentIds = reopened.Segments.Readers.Select(static reader => reader.Header.SegmentId).Order().ToArray();

        Assert.Equal([1L], segmentIds);
        Assert.Equal(10L, CountAllSegmentPoints(reopened));
    }

    private string RunKillScenario(string scenario, TimeSpan liveAfterReady)
    {
        string root = NewScenarioRoot();
        string readyFile = Path.Combine(root, $"{scenario}.ready");
        string childDll = ResolveChildDll();

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{childDll}\" {scenario} \"{root}\" \"{readyFile}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("无法启动 CrashTests 子进程。");

        try
        {
            WaitForReady(process, readyFile, TimeSpan.FromSeconds(10));
            Thread.Sleep(liveAfterReady);
            process.Kill(entireProcessTree: true);
            Assert.True(process.WaitForExit((int)TimeSpan.FromSeconds(10).TotalMilliseconds));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }

        return root;
    }

    private string NewScenarioRoot()
    {
        string root = Path.Combine(_tempDir, Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        return root;
    }

    private static TsdbOptions MakeOptions(string? root = null, bool compactionEnabled = false)
        => new()
        {
            RootDirectory = root ?? Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
            SyncWalOnEveryWrite = true,
            FlushPolicy = new MemTableFlushPolicy
            {
                MaxPoints = long.MaxValue,
                MaxBytes = long.MaxValue,
                HardCapBytes = 0,
                MaxAge = TimeSpan.MaxValue,
            },
            SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = compactionEnabled },
        };

    private static Point MakePoint(string measurement, long timestamp, string host, double value)
        => Point.Create(
            measurement,
            timestamp,
            new Dictionary<string, string> { ["host"] = host },
            new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromDouble(value) });

    private static int QueryPointCount(Tsdb db, string measurement, string host)
    {
        var entry = Assert.Single(db.Catalog.Find(measurement, new Dictionary<string, string> { ["host"] = host }));
        return db.Query.Execute(new PointQuery(entry.Id, "v", TimeRange.All)).Count();
    }

    private static long CountAllSegmentPoints(Tsdb db)
    {
        long total = 0;
        foreach (var reader in db.Segments.Readers)
        {
            foreach (var block in reader.Blocks)
                total += block.Count;
        }

        return total;
    }

    private static void WaitForReady(Process process, string readyFile, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (File.Exists(readyFile))
                return;

            if (process.HasExited)
            {
                string stderr = process.StandardError.ReadToEnd();
                throw new InvalidOperationException($"子进程过早退出，ExitCode={process.ExitCode}，stderr={stderr}");
            }

            Thread.Sleep(25);
        }

        throw new TimeoutException($"等待子进程 ready 超时：{readyFile}");
    }

    private static string ResolveChildDll()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string path = Path.Combine(baseDirectory, "SonnetDB.CrashTests.Child.dll");
        if (File.Exists(path))
            return path;

        throw new FileNotFoundException("未找到 CrashTests 子进程程序集。", path);
    }
}
