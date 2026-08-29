using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Engine.Retention;
using SonnetDB.Kv;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>
/// 验证后台维护线程在长轮询期间仍能安全、快速地释放。
/// </summary>
public sealed class BackgroundMaintenanceShutdownTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-background-shutdown-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Dispose_WithLongCompactionPoll_StopsPromptlyAndIsIdempotent()
    {
        Tsdb database = Open(
            CompactionPolicy.Default with
            {
                Enabled = true,
                PollInterval = TimeSpan.FromHours(1),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
            },
            RetentionPolicy.Default,
            DisabledKv());

        AssertPromptIdempotentDispose(database);
    }

    [Fact]
    public void Dispose_WithLongRetentionPoll_StopsPromptlyAndIsIdempotent()
    {
        Tsdb database = Open(
            CompactionPolicy.Default with { Enabled = false },
            RetentionPolicy.Default with
            {
                Enabled = true,
                PollInterval = TimeSpan.FromHours(1),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
            },
            DisabledKv());

        AssertPromptIdempotentDispose(database);
    }

    [Fact]
    public void Dispose_WithLongKvMaintenancePoll_StopsPromptlyAndIsIdempotent()
    {
        Tsdb database = Open(
            CompactionPolicy.Default with { Enabled = false },
            RetentionPolicy.Default,
            KvOptions.Default with
            {
                ExpirerEnabled = true,
                ExpirerPollInterval = TimeSpan.FromHours(1),
                ExpirerShutdownTimeout = TimeSpan.FromSeconds(5),
                CleanupEnabled = true,
                CleanupPollInterval = TimeSpan.FromHours(1),
            });

        AssertPromptIdempotentDispose(database);
    }

    [Theory]
    [InlineData(WorkerKind.Compaction)]
    [InlineData(WorkerKind.Retention)]
    [InlineData(WorkerKind.KvMaintenance)]
    public async Task Dispose_WhileRoundIsInFlight_ConcurrentCallersWaitForWorkerExit(WorkerKind kind)
    {
        using Tsdb database = Open(
            CompactionPolicy.Default with { Enabled = false },
            RetentionPolicy.Default with { Enabled = false },
            DisabledKv());
        WorkerHarness harness = CreateHarness(kind, database);
        using var enteredRound = new ManualResetEventSlim(initialState: false);
        using var releaseRound = new ManualResetEventSlim(initialState: false);
        using var callersReady = new CountdownEvent(4);
        using var beginDispose = new ManualResetEventSlim(initialState: false);
        Task[] disposeTasks = [];

        harness.SetBeforeMaintenanceRoundTestHook(() =>
        {
            enteredRound.Set();
            releaseRound.Wait();
        });
        harness.Start();

        try
        {
            Assert.True(
                enteredRound.Wait(TimeSpan.FromSeconds(5)),
                $"{kind} 未进入测试维护轮次。");

            disposeTasks = Enumerable.Range(0, callersReady.InitialCount)
                .Select(_ => Task.Factory.StartNew(
                    () =>
                    {
                        callersReady.Signal();
                        beginDispose.Wait();
                        harness.Dispose();
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .ToArray();

            Assert.True(callersReady.Wait(TimeSpan.FromSeconds(5)));
            beginDispose.Set();
            await Task.Delay(TimeSpan.FromMilliseconds(200));

            Assert.All(disposeTasks, task => Assert.False(task.IsCompleted));
            Assert.IsType<TimeoutException>(harness.LastError());

            releaseRound.Set();
            await Task.WhenAll(disposeTasks).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(harness.HasExited());
        }
        finally
        {
            releaseRound.Set();
            if (disposeTasks.Length != 0)
                await Task.WhenAll(disposeTasks).WaitAsync(TimeSpan.FromSeconds(5));
            harness.Dispose();
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理不得覆盖测试结果。
        }
        catch (UnauthorizedAccessException)
        {
            // 清理不得覆盖测试结果。
        }
    }

    private Tsdb Open(
        CompactionPolicy compaction,
        RetentionPolicy retention,
        KvOptions kv)
    {
        string databaseRoot = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        return Tsdb.Open(new TsdbOptions
        {
            RootDirectory = databaseRoot,
            BackgroundFlush = BackgroundFlushOptions.Default with { Enabled = false },
            Compaction = compaction,
            Retention = retention,
            Kv = kv,
        });
    }

    private static KvOptions DisabledKv() => KvOptions.Default with
    {
        ExpirerEnabled = false,
        CleanupEnabled = false,
    };

    private static void AssertPromptIdempotentDispose(Tsdb database)
    {
        Thread.Sleep(TimeSpan.FromMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();

        Exception? first = Record.Exception(database.Dispose);
        Exception? second = Record.Exception(database.Dispose);
        stopwatch.Stop();

        Assert.Null(first);
        Assert.Null(second);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Dispose 未及时唤醒长轮询 worker：{stopwatch.Elapsed}。");
    }

    private static WorkerHarness CreateHarness(WorkerKind kind, Tsdb database)
    {
        switch (kind)
        {
            case WorkerKind.Compaction:
                {
                    var worker = new CompactionWorker(
                        database,
                        CompactionPolicy.Default with
                        {
                            Enabled = true,
                            PollInterval = TimeSpan.FromMilliseconds(1),
                            ShutdownTimeout = TimeSpan.FromMilliseconds(25),
                        });
                    return new WorkerHarness(
                        worker.Start,
                        worker.Dispose,
                        hook => worker.BeforeMaintenanceRoundTestHook = hook,
                        () => worker.LastError,
                        () => worker.HasExited);
                }
            case WorkerKind.Retention:
                {
                    var worker = new RetentionWorker(
                        database,
                        RetentionPolicy.Default with
                        {
                            Enabled = true,
                            PollInterval = TimeSpan.FromMilliseconds(1),
                            ShutdownTimeout = TimeSpan.FromMilliseconds(25),
                        });
                    return new WorkerHarness(
                        worker.Start,
                        worker.Dispose,
                        hook => worker.BeforeMaintenanceRoundTestHook = hook,
                        () => worker.LastError,
                        () => worker.HasExited);
                }
            case WorkerKind.KvMaintenance:
                {
                    var worker = new KvExpirerWorker(
                        database,
                        KvOptions.Default with
                        {
                            ExpirerEnabled = true,
                            ExpirerPollInterval = TimeSpan.FromMilliseconds(1),
                            ExpirerShutdownTimeout = TimeSpan.FromMilliseconds(25),
                            CleanupEnabled = false,
                        });
                    return new WorkerHarness(
                        worker.Start,
                        worker.Dispose,
                        hook => worker.BeforeMaintenanceRoundTestHook = hook,
                        () => worker.LastError,
                        () => worker.HasExited);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    public enum WorkerKind
    {
        Compaction,
        Retention,
        KvMaintenance,
    }

    private sealed record WorkerHarness(
        Action Start,
        Action Dispose,
        Action<Action> SetBeforeMaintenanceRoundTestHook,
        Func<Exception?> LastError,
        Func<bool> HasExited);
}
