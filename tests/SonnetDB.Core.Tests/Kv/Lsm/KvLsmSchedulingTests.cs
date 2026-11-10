using System.Text;
using SonnetDB.Kv;
using SonnetDB.Kv.Lsm;
using Xunit;

namespace SonnetDB.Core.Tests.Kv.Lsm;

/// <summary>K5c：进程级后台调度器与跨引擎写缓冲预算。</summary>
public sealed class KvLsmSchedulingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-kv-sched-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// 超出预算时只请求活跃 memtable 最大的一个引擎；待处理请求与正在刷盘的不可变 memtable 计为“正在释放”，
    /// 不连带请求其他引擎；请求状态以引擎上报为准。
    /// </summary>
    [Fact]
    public void WriteBufferManager_RequestsLargestMutable_OncePerOverflow_AndAccountsRelease()
    {
        var manager = new KvWriteBufferManager(1_000, minVictimBytes: 0);
        var small = new RecordingClient();
        var large = new RecordingClient();
        manager.Register(small);
        manager.Register(large);

        manager.Report(small, 300, 300, flushPending: false);
        manager.Report(large, 600, 600, flushPending: false);
        Assert.Equal(0, manager.FlushRequests);

        manager.Report(small, 500, 500, flushPending: false); // 总量 1100 > 1000
        Assert.Equal(1, large.Requests);
        Assert.Equal(0, small.Requests);

        // large 的 600 字节正在释放：1120 - 600 <= 1000，不再请求其他引擎。
        manager.Report(small, 520, 520, flushPending: false);
        Assert.Equal(1, large.Requests);
        Assert.Equal(0, small.Requests);

        // large 轮转：活跃为 0，600 字节进入刷盘队列（仍计入总量，但视为正在释放）。
        manager.Report(large, 600, 0, flushPending: false);
        manager.Report(small, 700, 700, flushPending: false);
        Assert.Equal(0, small.Requests);
        manager.Report(small, 1_100, 1_100, flushPending: false); // 扣除 600 后仍超限
        Assert.Equal(1, small.Requests);
        Assert.Equal(2, manager.FlushRequests);

        // 引擎上报请求已作废（未轮转）后，下一次超限可以再次选中它。
        manager.Report(small, 1_100, 1_100, flushPending: false);
        Assert.Equal(2, small.Requests);

        manager.Unregister(large);
        Assert.Equal(1_100, manager.UsageBytes);
        Assert.Equal(1, manager.ClientCount);
        manager.Report(large, 5_000, 5_000, flushPending: false); // 已注销：忽略
        Assert.Equal(1_100, manager.UsageBytes);
    }

    /// <summary>活跃 memtable 小于最小刷盘对象时不请求刷盘（新 memtable 本身就有固定占用，刷了也不会下降）。</summary>
    [Fact]
    public void WriteBufferManager_IgnoresVictimsBelowMinimumSize()
    {
        var manager = new KvWriteBufferManager(500, minVictimBytes: 800);
        var client = new RecordingClient();
        manager.Register(client);
        manager.Report(client, 1_500, 700, flushPending: false); // 扣除 800 后仍超限，但活跃部分不足 800
        Assert.Equal(0, client.Requests);
        manager.Report(client, 1_700, 900, flushPending: false);
        Assert.Equal(1, client.Requests);
    }

    /// <summary>大量引擎共享固定数量的工作线程完成 flush 与 compaction，数据不丢失。</summary>
    [Fact]
    public void ManyEngines_ShareFixedWorkerThreads_AndAllFlushAndCompact()
    {
        using var scheduler = new KvLsmBackgroundScheduler(flushWorkers: 1, compactionWorkers: 1, "Test");
        var engines = new List<KvLsmEngine>();
        try
        {
            for (int e = 0; e < 50; e++)
            {
                KvLsmEngine engine = KvLsmEngine.Open(Path.Combine(_root, "e" + e), Options(scheduler) with
                {
                    WriteBufferBytes = 64 * 1024,
                });
                engines.Add(engine);
                for (int i = 0; i < 300; i++)
                    engine.Apply([Put($"k{i:D4}", $"e{e}-v{i}-" + new string('x', 600))]);
            }

            foreach (KvLsmEngine engine in engines)
                engine.Flush();
            Assert.Equal(1, scheduler.FlushWorkerCount);
            Assert.Equal(1, scheduler.CompactionWorkerCount);
            Assert.True(SpinWait.SpinUntil(
                () => engines.All(static engine => engine.GetStats().FileCountByLevel[0] < 4),
                TimeSpan.FromSeconds(60)));
            for (int e = 0; e < engines.Count; e++)
            {
                KvLsmEngineStats stats = engines[e].GetStats();
                Assert.True(stats.FlushCount > 1, $"engine {e} flush count {stats.FlushCount}");
                Assert.True(stats.CompactionCount > 0, $"engine {e} compaction count {stats.CompactionCount}");
                Assert.Equal($"e{e}-v299-" + new string('x', 600), Text(engines[e].Get(Bytes("k0299"))!.Value));
                Assert.Null(engines[e].Fault);
            }
        }
        finally
        {
            foreach (KvLsmEngine engine in engines)
                engine.Dispose();
        }
    }

    /// <summary>引擎自身写缓冲远未达到上限时，全局预算超限会请求活跃 memtable 最大的引擎刷盘。</summary>
    [Fact]
    public void WriteBufferManager_ForcesLargestEngineToFlush_BeforeItsOwnLimit()
    {
        using var scheduler = new KvLsmBackgroundScheduler(1, 1, "Test");
        var manager = new KvWriteBufferManager(256 * 1024);
        KvLsmEngineOptions options = Options(scheduler) with
        {
            WriteBufferBytes = 64L * 1024 * 1024,
            WriteBufferManager = manager,
        };
        using KvLsmEngine hot = KvLsmEngine.Open(Path.Combine(_root, "hot"), options);
        using KvLsmEngine cold = KvLsmEngine.Open(Path.Combine(_root, "cold"), options);
        string value = new('x', 1024);
        cold.Apply([Put("cold", "1")]);
        for (int i = 0; i < 400; i++)
            hot.Apply([Put($"k{i:D4}", value)]);

        Assert.True(SpinWait.SpinUntil(() => hot.GetStats().FlushCount > 0, TimeSpan.FromSeconds(30)));
        Assert.Equal(0, cold.GetStats().FlushCount);
        Assert.True(manager.FlushRequests > 0);
        Assert.True(SpinWait.SpinUntil(() => manager.UsageBytes <= manager.LimitBytes, TimeSpan.FromSeconds(30)),
            $"usage={manager.UsageBytes} hot={hot.GetStats()} cold={cold.GetStats()}");
        Assert.Equal(value, Text(hot.Get(Bytes("k0399"))!.Value));
        Assert.Equal(2, manager.ClientCount);
        hot.Dispose();
        cold.Dispose();
        Assert.Equal(0, manager.ClientCount);
        Assert.Equal(0, manager.UsageBytes);
    }

    /// <summary>同步模式（无调度器）下，全局预算请求由下一次写入在写锁内兑现。</summary>
    [Fact]
    public void WriteBufferManager_SyncMode_RotatesOnNextWrite()
    {
        var manager = new KvWriteBufferManager(64 * 1024);
        using KvLsmEngine engine = KvLsmEngine.Open(_root, Options(scheduler: null) with
        {
            BackgroundFlush = false,
            WriteBufferBytes = 64L * 1024 * 1024,
            WriteBufferManager = manager,
        });
        string value = new('y', 1024);
        for (int i = 0; i < 200; i++)
            engine.Apply([Put($"k{i:D4}", value)]);

        Assert.True(manager.FlushRequests > 0);
        Assert.True(engine.GetStats().ImmutableMemTables > 0 || engine.GetStats().FlushCount > 0);
        engine.Flush();
        Assert.Equal(value, Text(engine.Get(Bytes("k0000"))!.Value));
    }

    /// <summary>关闭等待本引擎的在途后台作业结束；关闭后排队的作业不再执行，目录可以立即删除。</summary>
    [Fact]
    public async Task Dispose_WaitsForInFlightJob_AndSkipsQueuedJobs()
    {
        using var scheduler = new KvLsmBackgroundScheduler(1, 1, "Test");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        KvLsmEngine engine = KvLsmEngine.Open(_root, Options(scheduler) with
        {
            WriteBufferBytes = 64 * 1024,
            TestHook = phase =>
            {
                if (phase == KvLsmEnginePhase.FlushAfterSstWritten && !entered.IsSet)
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                }
            },
        });
        for (int i = 0; i < 64; i++)
            engine.Apply([Put($"k{i:D4}", new string('z', 2048))]);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

        Task disposing = Task.Run(engine.Dispose);
        await Task.Delay(200);
        Assert.False(disposing.IsCompleted);
        release.Set();
        await disposing.WaitAsync(TimeSpan.FromSeconds(30));

        Directory.Delete(_root, recursive: true);
        Assert.False(Directory.Exists(_root));
    }

    private static KvLsmEngineOptions Options(KvLsmBackgroundScheduler? scheduler) => new()
    {
        Scheduler = scheduler,
        SyncWalOnEveryWrite = false,
        MaxImmutableMemTables = 4,
        SstWriterOptions = new KvSstWriterOptions { BlockSize = 1024, SyncDirectory = false },
        Compaction = new KvCompactionOptions
        {
            Level0CompactionTrigger = 4,
            MaxBytesForLevelBase = 256 * 1024,
            LevelSizeMultiplier = 4,
            TargetFileSizeBytes = 64 * 1024,
        },
    };

    private static KvBatchMutation Put(string key, string value) => new(Bytes(key), Bytes(value), null);

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static string Text(ReadOnlyMemory<byte> value) => Encoding.UTF8.GetString(value.Span);

    private sealed class RecordingClient : IKvWriteBufferClient
    {
        internal int Requests { get; private set; }

        public void RequestFlushForWriteBuffer() => Requests++;
    }
}
