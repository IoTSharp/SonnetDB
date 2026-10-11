using System.Diagnostics;
using SonnetDB.Kv;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>验证 ONLINE 有序覆盖层准入能够推进检查点，且排队、冻结和失败退避不会重复调度。</summary>
public sealed class TableOnlineCheckpointAdmissionTests : IDisposable
{
    private const int LargeRowCount = 100_001;
    private const int SeedBatchSize = 1_000;
    private const int MaximumSeedBatches = 101;
    private const int MaximumOnlineBatches = 160;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-online-checkpoint-" + Guid.NewGuid().ToString("N"));

    /// <summary>仅回收本测试创建的独立目录，所有 worker 在各测试 finally 中先完成收束。</summary>
    public void Dispose()
    {
        string path = Path.GetFullPath(_root);
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        if (!path.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith("sndb-online-checkpoint-", StringComparison.Ordinal))
            throw new InvalidOperationException("测试清理路径超出独立临时目录。");
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    /// <summary>先以三行验证同一数据准备和续建助手，小覆盖层无需触发检查点。</summary>
    [Fact]
    public async Task SmallOverlay_OnlineCompletesWithoutSchedulingCheckpoint()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var manager = CreateManager("small", autoCheckpointEnabled: true);
        TableStore store = CreateStore(manager);
        SeedRows(store, 3, timeout.Token);
        long schedules = store.AutoCheckpointScheduleCount;

        await CompleteOnlineAsync(manager, store, timeout.Token);

        Assert.Equal(schedules, store.AutoCheckpointScheduleCount);
        AssertIndexContains(manager, store, 3L, 3L);
    }

    /// <summary>真实十万行以上覆盖层仅调度一次；排队和冻结重试不增加 worker，写入仍可推进。</summary>
    [Fact]
    public async Task LargeOverlay_QueuedAndFrozenRetriesDoNotReschedule_ThenIndexPublishes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var manager = CreateManager("automatic", autoCheckpointEnabled: true);
        TableStore store = CreateStore(manager);
        SeedRows(store, LargeRowCount, timeout.Token);
        Assert.Equal(0, store.AutoCheckpointScheduleCount);
        using var beforeFreeze = new ManualResetEventSlim();
        using var releaseBeforeFreeze = new ManualResetEventSlim();
        using var afterFreeze = new ManualResetEventSlim();
        using var releaseAfterFreeze = new ManualResetEventSlim();
        int beforeFreezeCalls = 0;
        int afterFreezeCalls = 0;
        int lateWrite = 0;
        store.CheckpointTestHook = phase =>
        {
            if (phase == KvCheckpointPhase.BeforeFreeze
                && Interlocked.Increment(ref beforeFreezeCalls) == 1)
            {
                beforeFreeze.Set();
                if (!releaseBeforeFreeze.Wait(TimeSpan.FromSeconds(15), timeout.Token))
                    throw new TimeoutException("测试未及时释放排队检查点。");
            }
            else if (phase == KvCheckpointPhase.AfterFreeze
                && Interlocked.Increment(ref afterFreezeCalls) == 1)
            {
                afterFreeze.Set();
                if (!releaseAfterFreeze.Wait(TimeSpan.FromSeconds(15), timeout.Token))
                    throw new TimeoutException("测试未及时释放冻结检查点。");
            }
        };

        try
        {
            TableOnlineIndexBuildResult first = AdvanceOnline(manager, timeout.Token, maximumPages: 1);
            Assert.Equal("waiting_checkpoint", first.Status);
            Assert.False(first.Completed);
            Assert.Equal(0, first.RowsProcessed);
            Assert.Equal(1, store.AutoCheckpointScheduleCount);
            Assert.True(beforeFreeze.Wait(TimeSpan.FromSeconds(10), timeout.Token));
            Assert.True(store.AutoCheckpointQueued);
            Assert.Equal(0, Volatile.Read(ref afterFreezeCalls));
            await AssertWaitingRetriesAsync(manager, store, expectedSchedules: 1, timeout.Token);

            releaseBeforeFreeze.Set();
            Assert.True(afterFreeze.Wait(TimeSpan.FromSeconds(10), timeout.Token));
            Assert.True(store.AutoCheckpointQueued);
            await AssertWaitingRetriesAsync(manager, store, expectedSchedules: 1, timeout.Token);
            store.Insert([LargeRowCount + 1L, -1L]);
            Assert.Equal(LargeRowCount + 1, store.RowCount);
            Assert.Equal(-1L, store.GetByPrimaryKey([LargeRowCount + 1L])!.Values[1]);
            Assert.Equal(1, store.AutoCheckpointScheduleCount);
            Assert.Equal(1, Volatile.Read(ref afterFreezeCalls));

            releaseAfterFreeze.Set();
            await WaitForConditionAsync(() => !store.AutoCheckpointQueued,
                TimeSpan.FromSeconds(15), timeout.Token);
            Assert.Null(store.LastCheckpointException);

            // 在首个已提交页之后插入游标之前的主键，必须由待建索引的 DML 同批维护补齐。
            manager.OnlineIndexPageCompletedTestHook = _ =>
            {
                if (Interlocked.CompareExchange(ref lateWrite, 1, 0) == 0)
                    store.Insert([0L, -2L]);
            };
            await CompleteOnlineAsync(manager, store, timeout.Token);

            Assert.Equal(1, Volatile.Read(ref lateWrite));
            Assert.Equal(LargeRowCount + 2, store.RowCount);
            AssertIndexContains(manager, store, LargeRowCount + 1L, -1L);
            AssertIndexContains(manager, store, 0L, -2L);
            AssertIndexContains(manager, store, 1L, 1L);
            AssertIndexContains(manager, store, LargeRowCount, LargeRowCount);
        }
        finally
        {
            releaseBeforeFreeze.Set();
            releaseAfterFreeze.Set();
            store.CheckpointTestHook = null;
            manager.OnlineIndexPageCompletedTestHook = null;
            await WaitForConditionAsync(() => !store.AutoCheckpointQueued,
                TimeSpan.FromSeconds(15), CancellationToken.None);
        }
    }

    /// <summary>关闭自动检查点时真实大覆盖层持续返回等待，显式检查点后才能完成索引发布。</summary>
    [Fact]
    public async Task LargeOverlay_AutomaticCheckpointDisabled_WaitsForExplicitCheckpoint()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var manager = CreateManager("manual", autoCheckpointEnabled: false);
        TableStore store = CreateStore(manager);
        SeedRows(store, LargeRowCount, timeout.Token);

        await AssertWaitingRetriesAsync(manager, store, expectedSchedules: 0, timeout.Token);
        Assert.False(store.AutoCheckpointQueued);
        Assert.Null(manager.Catalog.TryGet("captures")!.TryGetIndex("idx_capture_time"));
        store.Insert([LargeRowCount + 1L, -1L]);
        Assert.Equal(-1L, store.GetByPrimaryKey([LargeRowCount + 1L])!.Values[1]);
        Assert.Equal(0, store.AutoCheckpointScheduleCount);

        timeout.Token.ThrowIfCancellationRequested();
        store.Compact();
        timeout.Token.ThrowIfCancellationRequested();
        await CompleteOnlineAsync(manager, store, timeout.Token, explicitCheckpointStore: store);

        Assert.Equal(0, store.AutoCheckpointScheduleCount);
        Assert.False(store.AutoCheckpointQueued);
        AssertIndexContains(manager, store, LargeRowCount + 1L, -1L);
        AssertIndexContains(manager, store, LargeRowCount, LargeRowCount);
    }

    /// <summary>小 KV 故障旅程验证 ONLINE 准入不跳过既有一秒失败退避，也不重复调度排队重试。</summary>
    [Fact]
    public async Task OrderedAdmission_CheckpointFailurePreservesRetryBackoff()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var keyspace = KvKeyspace.Open("failure", Path.Combine(_root, "failure"), CreateOptions(true));
        using var retryBeforeFreeze = new ManualResetEventSlim();
        using var releaseRetry = new ManualResetEventSlim();
        int beforeFreezeCalls = 0;
        int freezes = 0;
        long failureTimestamp = 0;
        long retryTimestamp = 0;
        keyspace.CheckpointTestHook = phase =>
        {
            if (phase == KvCheckpointPhase.AfterFreeze && Interlocked.Increment(ref freezes) == 1)
            {
                Volatile.Write(ref failureTimestamp, Stopwatch.GetTimestamp());
                throw new IOException("测试检查点首次写盘失败。");
            }
            if (phase == KvCheckpointPhase.BeforeFreeze
                && Interlocked.Increment(ref beforeFreezeCalls) == 2)
            {
                Volatile.Write(ref retryTimestamp, Stopwatch.GetTimestamp());
                retryBeforeFreeze.Set();
                if (!releaseRetry.Wait(TimeSpan.FromSeconds(15), timeout.Token))
                    throw new TimeoutException("测试未及时释放失败后的检查点重试。");
            }
        };

        try
        {
            keyspace.Put("one", [1]);
            keyspace.Put("two", [2]);
            Assert.False(keyspace.TryEnableOrderedOverlayScans(1, timeout.Token));
            await WaitForConditionAsync(
                () => keyspace.LastCheckpointException is IOException && keyspace.AutoCheckpointScheduleCount == 2,
                TimeSpan.FromSeconds(10), timeout.Token);
            Assert.True(keyspace.AutoCheckpointQueued);

            // 四次准入共约一百毫秒；不能把已排队的一秒延迟重试改成即时 worker。
            for (int retry = 0; retry < 4; retry++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                Assert.False(keyspace.TryEnableOrderedOverlayScans(1, timeout.Token));
                Assert.Equal(2, keyspace.AutoCheckpointScheduleCount);
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            }
            Assert.True(retryBeforeFreeze.Wait(TimeSpan.FromSeconds(10), timeout.Token));
            Assert.True(Stopwatch.GetElapsedTime(
                Volatile.Read(ref failureTimestamp), Volatile.Read(ref retryTimestamp)) >= TimeSpan.FromMilliseconds(900));
            Assert.Equal(2, keyspace.AutoCheckpointScheduleCount);
            releaseRetry.Set();
            await WaitForConditionAsync(() => !keyspace.AutoCheckpointQueued,
                TimeSpan.FromSeconds(10), timeout.Token);
            Assert.Null(keyspace.LastCheckpointException);
            Assert.True(keyspace.TryEnableOrderedOverlayScans(1, timeout.Token));
            Assert.Equal([1], keyspace.Get("one")!);
            Assert.Equal([2], keyspace.Get("two")!);
        }
        finally
        {
            releaseRetry.Set();
            keyspace.CheckpointTestHook = null;
            await WaitForConditionAsync(() => !keyspace.AutoCheckpointQueued,
                TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    /// <summary>创建本测试专用关系表管理器。</summary>
    private TableManager CreateManager(string directory, bool autoCheckpointEnabled)
        => new(Path.Combine(_root, directory), CreateOptions(autoCheckpointEnabled));

    /// <summary>让日常与恢复预算均高于十万条 ONLINE 门槛，避免写入提前触发检查点掩盖缺陷。</summary>
    private static KvOptions CreateOptions(bool autoCheckpointEnabled)
        => KvOptions.Default with
        {
            AutoCheckpointEnabled = autoCheckpointEnabled,
            SyncWalOnEveryWrite = false,
            MaxWalBytes = long.MaxValue,
            MaxOverlayEntries = 1_000_000,
            MaxSnapshotOverlayEntries = 1_000_000,
            IndexRebuildMaxWalBytes = long.MaxValue,
            IndexRebuildMaxOverlayEntries = 1_000_000,
            ExpirerEnabled = false,
            CleanupEnabled = false,
        };

    /// <summary>创建只有两个整数列的真实表，避免媒体或统计成本干扰准入回归。</summary>
    private static TableStore CreateStore(TableManager manager)
    {
        manager.Create(TableSchema.Create("captures",
            [("id", TableColumnType.Int64, false), ("capture_time", TableColumnType.Int64, false)], ["id"]));
        return manager.Open("captures");
    }

    /// <summary>最多一百零一批、每批一千行并受四十五秒墙钟和取消约束，供小烟测与真实大表共用。</summary>
    private static void SeedRows(TableStore store, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, LargeRowCount);
        long started = Stopwatch.GetTimestamp();
        int next = 1;
        for (int batch = 0; batch < MaximumSeedBatches && next <= count; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(45), "测试准备超过墙钟预算。");
            var rows = new List<IReadOnlyList<object?>>(Math.Min(SeedBatchSize, count - next + 1));
            for (int item = 0; item < SeedBatchSize && next <= count; item++, next++)
                rows.Add([Convert.ToInt64(next), Convert.ToInt64(next)]);
            Assert.Equal(rows.Count, store.InsertMany(rows));
        }
        Assert.Equal(count, store.RowCount);
    }

    /// <summary>单次最多六十四页，沿用生产 ONLINE 入口与页间退避。</summary>
    private static TableOnlineIndexBuildResult AdvanceOnline(
        TableManager manager, CancellationToken cancellationToken, int maximumPages = 64)
        => manager.CreateIndexOnline("captures",
            new TableIndexDefinition("idx_capture_time", ["capture_time"], IsUnique: false),
            cancellationToken, maximumPages);

    /// <summary>固定四次等待重试，验证同一排队或冻结检查点不会被强制重排。</summary>
    private static async Task AssertWaitingRetriesAsync(
        TableManager manager, TableStore store, long expectedSchedules, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        for (int retry = 0; retry < 4; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
            TableOnlineIndexBuildResult result = AdvanceOnline(manager, cancellationToken, maximumPages: 1);
            Assert.Equal("waiting_checkpoint", result.Status);
            Assert.Equal(0, result.RowsProcessed);
            Assert.Equal(expectedSchedules, store.AutoCheckpointScheduleCount);
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    /// <summary>最多一百六十批并受一百五十秒墙钟和取消限制，检查点等待不消耗空续建调用。</summary>
    private static async Task CompleteOnlineAsync(
        TableManager manager, TableStore store, CancellationToken cancellationToken, TableStore? explicitCheckpointStore = null)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(150));
        long started = Stopwatch.GetTimestamp();
        long processedRows = 0;
        TableOnlineIndexBuildResult? lastResult = null;
        try
        {
            for (int batch = 0; batch < MaximumOnlineBatches
                && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(150); batch++)
            {
                budget.Token.ThrowIfCancellationRequested();
                lastResult = AdvanceOnline(manager, budget.Token);
                processedRows += lastResult.RowsProcessed;
                if (lastResult.Completed)
                    return;
                if (lastResult.Status == "waiting_checkpoint")
                {
                    if (explicitCheckpointStore is not null)
                    {
                        // 关闭自动维护时，新建索引条目也可能再次跨过门槛；仅显式推进，不能暗启 worker。
                        budget.Token.ThrowIfCancellationRequested();
                        explicitCheckpointStore.Compact();
                        budget.Token.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        // 异步检查点可能需要多秒；等待同一个 worker 完成，避免快速重试耗尽批次数。
                        await WaitForOnlineCheckpointAsync(store, lastResult, processedRows, budget.Token);
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(10), budget.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("ONLINE 续建取消或超过墙钟预算；" + DescribeOnlineProgress(store, lastResult, processedRows));
        }
        Assert.Fail("ONLINE 在固定批次数或墙钟预算内未发布索引；"
            + DescribeOnlineProgress(store, lastResult, processedRows));
    }

    /// <summary>最多两千五百次且二十五秒内等待当前自动 worker 收束，超时保留实际构建进度。</summary>
    private static async Task WaitForOnlineCheckpointAsync(
        TableStore store, TableOnlineIndexBuildResult lastResult, long processedRows, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(25));
        long started = Stopwatch.GetTimestamp();
        try
        {
            for (int attempt = 0; attempt < 2_500
                && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(25); attempt++)
            {
                budget.Token.ThrowIfCancellationRequested();
                if (!store.AutoCheckpointQueued)
                    return;
                await Task.Delay(TimeSpan.FromMilliseconds(10), budget.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 本次二十五秒等待已经耗尽，下面的断言携带进度和检查点故障信息。
        }
        Assert.Fail("ONLINE 自动检查点超过二十五秒等待预算；"
            + DescribeOnlineProgress(store, lastResult, processedRows));
    }

    /// <summary>记录调度次数、异常、末次状态和累计行数，区分真实停滞与测试等待不足。</summary>
    private static string DescribeOnlineProgress(
        TableStore store, TableOnlineIndexBuildResult? lastResult, long processedRows)
    {
        return $"SchedulerCount={store.AutoCheckpointScheduleCount}; CheckpointQueued={store.AutoCheckpointQueued}; "
            + $"LastCheckpointException={store.LastCheckpointException?.ToString() ?? "none"}; "
            + $"LastStatus={lastResult?.Status ?? "not_started"}; ProcessedRows={processedRows}.";
    }

    /// <summary>按已发布索引真实点读行，验证旧行与两个不同阶段的晚新增行均有索引证据。</summary>
    private static void AssertIndexContains(TableManager manager, TableStore store, long id, long captureTime)
    {
        TableIndex index = Assert.Single(manager.Catalog.TryGet("captures")!.Indexes);
        Assert.Equal("idx_capture_time", index.Name);
        TableRow row = Assert.Single(store.GetByIndex(index, [captureTime]));
        Assert.Equal(id, row.Values[0]);
        Assert.Equal(captureTime, row.Values[1]);
    }

    /// <summary>最多一千五百次、每次十毫秒退避并同时观察墙钟和取消，避免忙等待或遗漏 worker 收束。</summary>
    private static async Task WaitForConditionAsync(
        Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        for (int attempt = 0; attempt < 1_500 && Stopwatch.GetElapsedTime(started) < timeout; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition())
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
        Assert.True(condition(), "检查点未在有界等待内完成。");
    }
}
