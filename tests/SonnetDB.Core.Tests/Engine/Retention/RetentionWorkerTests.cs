using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Engine.Retention;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Engine.Retention;

/// <summary>
/// <see cref="RetentionWorker"/> 单元测试：验证启用/禁用、虚拟时钟驱动、幂等性及容错。
/// </summary>
public sealed class RetentionWorkerTests : IDisposable
{
    private readonly string _tempDir;

    public RetentionWorkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TsdbOptions MakeOptions(RetentionPolicy? retention = null, int maxPoints = 5) =>
        new TsdbOptions
        {
            RootDirectory = _tempDir,
            WalBufferSize = 64 * 1024,
            FlushPolicy = new MemTableFlushPolicy
            {
                MaxPoints = maxPoints,
                MaxBytes = 1024L * 1024 * 1024,
                MaxAge = TimeSpan.FromHours(24),
            },
            SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
            SyncWalOnEveryWrite = false,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
            Retention = retention ?? RetentionPolicy.Default,
        };

    private static Point MakePoint(long ts, double value, string field = "usage") =>
        Point.Create("cpu", ts,
            new Dictionary<string, string> { ["host"] = "srv1" },
            new Dictionary<string, FieldValue> { [field] = FieldValue.FromDouble(value) });

    // ── Enabled=false → Tsdb.Open 后 Retention == null ─────────────────────

    [Fact]
    public void Disabled_TsdbRetentionIsNull()
    {
        using var db = Tsdb.Open(MakeOptions());
        Assert.Null(db.Retention);
    }

    // ── Enabled=true → Tsdb.Retention 非 null ────────────────────────────────

    [Fact]
    public void Enabled_TsdbRetentionNotNull()
    {
        var policy = new RetentionPolicy
        {
            Enabled = true,
            TtlInTimestampUnits = 1000,
            NowFn = () => 10000,
            PollInterval = TimeSpan.FromHours(24),
        };
        using var db = Tsdb.Open(MakeOptions(policy));
        Assert.NotNull(db.Retention);
    }

    // ── 虚拟时钟：写入数据 → 推进时钟 → RunOnce → 段被 drop ─────────────────

    [Fact]
    public void RunOnce_WithVirtualClock_DropsExpiredSegments()
    {
        // NowFn 初始返回 1000，cutoff = 0（TTL=1000），数据时间戳 100..400
        long nowValue = 1000L;
        var policy = new RetentionPolicy
        {
            Enabled = true,
            TtlInTimestampUnits = 1000,
            NowFn = () => Volatile.Read(ref nowValue),
            PollInterval = TimeSpan.FromHours(24),
        };

        using var db = Tsdb.Open(MakeOptions(policy, maxPoints: 4));

        // 写入 4 个点（时间戳 100..400），触发 Flush（maxPoints=4）
        db.Write(MakePoint(100L, 1.0));
        db.Write(MakePoint(200L, 2.0));
        db.Write(MakePoint(300L, 3.0));
        db.Write(MakePoint(400L, 4.0));
        db.FlushNow();

        int segBefore = db.Segments.SegmentCount;
        Assert.True(segBefore >= 1);

        // 推进时钟使 cutoff = 10000 - 1000 = 9000，所有段(Max=400) < 9000
        Volatile.Write(ref nowValue, 10000L);

        var stats = db.Retention!.RunOnce();

        Assert.True(stats.DroppedSegments > 0);
        Assert.True(db.Segments.SegmentCount < segBefore);
    }

    [Fact]
    public void RunOnce_DropCommittedButFilesRemain_RestartDoesNotReloadDroppedSegments()
    {
        long nowValue = 1000L;
        var policy = new RetentionPolicy
        {
            Enabled = true,
            TtlInTimestampUnits = 1000,
            NowFn = () => Volatile.Read(ref nowValue),
            PollInterval = TimeSpan.FromHours(24),
        };

        using (var db = Tsdb.Open(MakeOptions(policy, maxPoints: 4)))
        {
            db.Write(MakePoint(100L, 1.0));
            db.Write(MakePoint(200L, 2.0));
            db.Write(MakePoint(300L, 3.0));
            db.Write(MakePoint(400L, 4.0));
            db.FlushNow();

            var droppedIds = db.Segments.Readers.Select(static reader => reader.Header.SegmentId).ToArray();
            Assert.NotEmpty(droppedIds);

            SegmentReplacementManifest.CommitDroppedSegments(_tempDir, droppedIds);
        }

        using (var reopened = Tsdb.Open(MakeOptions(policy, maxPoints: 4)))
        {
            Assert.Equal(0, reopened.Segments.SegmentCount);
        }
    }

    // ── 虚拟时钟：写入数据 → 推进时钟 → RunOnce → 部分过期段注入墓碑 ─────────

    [Fact]
    public void RunOnce_PartiallyExpiredSegment_InjectsTombstone()
    {
        // cutoff = 9000；段包含 [8000, 9500]（部分过期）
        long nowValue = 1000L; // 初始不过期
        var policy = new RetentionPolicy
        {
            Enabled = true,
            TtlInTimestampUnits = 1000,
            NowFn = () => Volatile.Read(ref nowValue),
            PollInterval = TimeSpan.FromHours(24),
        };

        using var db = Tsdb.Open(MakeOptions(policy, maxPoints: 4));

        db.Write(MakePoint(8000L, 1.0));
        db.Write(MakePoint(8500L, 2.0));
        db.Write(MakePoint(9000L, 3.0)); // 等于 cutoff → 不过期
        db.Write(MakePoint(9500L, 4.0));
        db.FlushNow();

        // 推进时钟：cutoff = 10000 - 1000 = 9000，段 [8000..9500] 部分过期
        Volatile.Write(ref nowValue, 10000L);

        var tombsBefore = db.Tombstones.Count;
        var stats = db.Retention!.RunOnce();

        Assert.Equal(1, stats.InjectedTombstones);
        Assert.True(db.Tombstones.Count > tombsBefore);
    }

    // ── 幂等：连续 RunOnce 三次 → 第二、三次返回 0 drop / 0 inject ──────────

    [Fact]
    public void RunOnce_Idempotent_SecondAndThirdRoundReturnZero()
    {
        long nowValue = 1000L;
        var policy = new RetentionPolicy
        {
            Enabled = true,
            TtlInTimestampUnits = 1000,
            NowFn = () => Volatile.Read(ref nowValue),
            PollInterval = TimeSpan.FromHours(24),
        };

        using var db = Tsdb.Open(MakeOptions(policy, maxPoints: 4));

        db.Write(MakePoint(100L, 1.0));
        db.Write(MakePoint(200L, 2.0));
        db.Write(MakePoint(300L, 3.0));
        db.Write(MakePoint(400L, 4.0));
        db.FlushNow();

        // 推进时钟
        Volatile.Write(ref nowValue, 10000L);

        var stats1 = db.Retention!.RunOnce();
        var stats2 = db.Retention!.RunOnce();
        var stats3 = db.Retention!.RunOnce();

        Assert.True(stats1.DroppedSegments > 0 || stats1.InjectedTombstones > 0);
        // 第二、三次：无新操作
        Assert.Equal(0, stats2.DroppedSegments);
        Assert.Equal(0, stats2.InjectedTombstones);
        Assert.Equal(0, stats3.DroppedSegments);
        Assert.Equal(0, stats3.InjectedTombstones);
    }

    // ── Dispose 在 worker 正在等待时调用 → 不死锁 ───────────────────────────

    [Fact]
    public void Dispose_WhileWorkerIsWaiting_DoesNotDeadlock()
    {
        var policy = new RetentionPolicy
        {
            Enabled = true,
            TtlInTimestampUnits = 1000,
            NowFn = () => 1000L,
            PollInterval = TimeSpan.FromSeconds(60), // 很长，worker 会阻塞在 Sleep
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        };

        var db = Tsdb.Open(MakeOptions(policy));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        db.Dispose(); // 应在 ShutdownTimeout 内完成
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"Dispose 耗时过长：{sw.Elapsed}");
    }

    // ── 并发读写场景：写入 + RunOnce + 查询 → 无异常 ─────────────────────────

    [Fact]
    public async Task ConcurrentWriteAndRetention_NoException()
    {
        long nowValue = 1000L;
        var policy = new RetentionPolicy
        {
            Enabled = true,
            TtlInTimestampUnits = 1000,
            NowFn = () => Volatile.Read(ref nowValue),
            PollInterval = TimeSpan.FromHours(24), // 不自动触发，手动 RunOnce
        };

        using var db = Tsdb.Open(MakeOptions(policy, maxPoints: 100_000));

        // 预先写入足量数据并落盘
        for (int i = 0; i < 20; i++)
            db.Write(MakePoint(100L + i * 10, i));
        db.FlushNow();

        var writeExceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var readExceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var retentionExceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        using var cts = new CancellationTokenSource();

        // 后台持续写入新鲜数据
        var writeTask = Task.Run(() =>
        {
            int seq = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    db.Write(MakePoint(9000L + seq++, seq));
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { writeExceptions.Add(ex); }
            }
        });

        // 后台持续读取（通过 SegmentManager.Readers 快照模拟查询侧压力）
        var readTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    _ = db.Segments.Readers.Count;
                    _ = db.Tombstones.Count;
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { readExceptions.Add(ex); }
            }
        });

        // 推进时钟，多次执行 Retention
        await Task.Delay(50);
        Volatile.Write(ref nowValue, 10000L); // cutoff=9000，使预写数据（ts<=400）全部过期

        for (int round = 0; round < 5; round++)
        {
            try
            {
                db.Retention!.RunOnce();
            }
            catch (Exception ex)
            {
                retentionExceptions.Add(ex);
            }
            await Task.Delay(10);
        }

        await cts.CancelAsync();
        await Task.WhenAll(writeTask, readTask);

        Assert.Empty(writeExceptions);
        Assert.Empty(readExceptions);
        Assert.Empty(retentionExceptions);
    }
}
