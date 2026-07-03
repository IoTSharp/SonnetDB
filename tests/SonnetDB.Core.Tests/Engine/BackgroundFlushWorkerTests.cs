using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>
/// <see cref="BackgroundFlushWorker"/> 单元测试。
/// </summary>
public sealed class BackgroundFlushWorkerTests : IDisposable
{
    private readonly string _tempDir;

    public BackgroundFlushWorkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TsdbOptions MakeOptions(
        MemTableFlushPolicy? flushPolicy = null,
        BackgroundFlushOptions? bgFlush = null) =>
        new TsdbOptions
        {
            RootDirectory = _tempDir,
            WalBufferSize = 64 * 1024,
            FlushPolicy = flushPolicy ?? new MemTableFlushPolicy
            {
                MaxPoints = 500,
                MaxBytes = 1024L * 1024 * 1024,
                MaxAge = TimeSpan.FromHours(24),
            },
            SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
            BackgroundFlush = bgFlush ?? new BackgroundFlushOptions
            {
                Enabled = true,
                PollInterval = TimeSpan.FromMilliseconds(50),
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            },
        };

    private static Point MakePoint(long timestamp, double value) =>
        Point.Create("cpu", timestamp,
            new Dictionary<string, string> { ["host"] = "srv1" },
            new Dictionary<string, FieldValue> { ["val"] = FieldValue.FromDouble(value) });

    /// <summary>
    /// BackgroundFlush.Enabled=false 时，不启动后台线程；写入大量数据不会自动 Flush。
    /// </summary>
    [Fact]
    public void Disabled_NoAutoFlush()
    {
        var opts = MakeOptions(
            flushPolicy: new MemTableFlushPolicy { MaxPoints = 10, MaxBytes = long.MaxValue, MaxAge = TimeSpan.MaxValue },
            bgFlush: new BackgroundFlushOptions { Enabled = false });

        using var db = Tsdb.Open(opts);

        // 写入 50 点（超过 MaxPoints=10）
        for (int i = 0; i < 50; i++)
            db.Write(MakePoint(1000L + i, i));

        // 等待一段时间，后台线程不应触发 Flush（因为 Enabled=false）
        Thread.Sleep(200);

        // MemTable 应仍有数据（未 Flush）
        Assert.True(db.MemTable.PointCount > 0);
        Assert.Equal(0, db.Segments.SegmentCount);
    }

    /// <summary>
    /// Signal 多次调用幂等：SemaphoreSlim 容量为 1，不会阻塞。
    /// </summary>
    [Fact]
    public void Signal_MultipleCallsAreIdempotent()
    {
        var opts = MakeOptions(bgFlush: new BackgroundFlushOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromSeconds(60), // 极长轮询，防止自动触发
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        });

        using var db = Tsdb.Open(opts);

        // 多次发信号，不应阻塞或抛异常
        for (int i = 0; i < 100; i++)
            db.Write(MakePoint(1000L + i, i));

        // 正常 Dispose 不应挂起
    }

    /// <summary>
    /// Dispose 在 worker 正在执行 Flush 时调用，不应抛异常。
    /// </summary>
    [Fact]
    public void Dispose_WhileWorkerRunning_DoesNotThrow()
    {
        var opts = MakeOptions(
            flushPolicy: new MemTableFlushPolicy { MaxPoints = 5, MaxBytes = long.MaxValue, MaxAge = TimeSpan.MaxValue },
            bgFlush: new BackgroundFlushOptions
            {
                Enabled = true,
                PollInterval = TimeSpan.FromMilliseconds(20),
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            });

        var db = Tsdb.Open(opts);

        // 写入数据触发 Flush
        for (int i = 0; i < 20; i++)
            db.Write(MakePoint(1000L + i, i));

        // 等待一个轮询周期
        Thread.Sleep(100);

        // Dispose 不应抛异常
        var ex = Record.Exception(() => db.Dispose());
        Assert.Null(ex);
    }

    /// <summary>
    /// BackgroundFlush.Enabled=true 且数据超过阈值时，后台线程在 PollInterval 内触发 Flush。
    /// </summary>
    [Fact]
    public void Enabled_TriggersFlushWhenThresholdExceeded()
    {
        var opts = MakeOptions(
            flushPolicy: new MemTableFlushPolicy { MaxPoints = 100, MaxBytes = long.MaxValue, MaxAge = TimeSpan.MaxValue },
            bgFlush: new BackgroundFlushOptions
            {
                Enabled = true,
                PollInterval = TimeSpan.FromMilliseconds(50),
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            });

        using var db = Tsdb.Open(opts);

        // 写入 200 点（超过阈值 100）
        for (int i = 0; i < 200; i++)
            db.Write(MakePoint(1000L + i, i));

        // 等待后台线程触发 Flush（最长等 3s）
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && db.Segments.SegmentCount == 0)
            Thread.Sleep(50);

        Assert.True(db.Segments.SegmentCount >= 1, "后台线程应在 3s 内触发至少一次 Flush");
    }

    [Fact]
    public void WorkerFlushFailure_RaisesDiagnosticEvent()
    {
        var expected = new IOException("background flush boom");
        var opts = new TsdbOptions
        {
            RootDirectory = _tempDir,
            WalBufferSize = 64 * 1024,
            FlushPolicy = new MemTableFlushPolicy
            {
                MaxPoints = 1,
                MaxBytes = long.MaxValue,
                MaxAge = TimeSpan.MaxValue,
            },
            SegmentWriterOptions = new SegmentWriterOptions
            {
                FsyncOnCommit = false,
                FailAt = _ => throw expected,
            },
            BackgroundFlush = new BackgroundFlushOptions
            {
                Enabled = true,
                PollInterval = TimeSpan.FromMilliseconds(20),
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            },
        };

        using var db = Tsdb.Open(opts);
        TsdbDiagnosticEvent? diagnostic = null;
        using var signaled = new ManualResetEventSlim();
        db.DiagnosticEvent += (_, e) =>
        {
            diagnostic = e;
            signaled.Set();
        };

        db.Write(MakePoint(1000L, 1.0));

        Assert.True(signaled.Wait(TimeSpan.FromSeconds(3)), "后台 Flush 失败应触发诊断事件。");
        Assert.NotNull(diagnostic);
        // Phase 2：flush 的编码落盘在 flush 泵线程执行，失败诊断来源为 FlushPump.Flush。
        Assert.Equal("FlushPump.Flush", diagnostic!.Operation);
        Assert.Equal(TsdbDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Same(expected, diagnostic.Exception);
        Assert.Same(expected, db.LastError);
    }
}
