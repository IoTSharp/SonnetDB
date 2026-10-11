using SonnetDB.Exceptions;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

/// <summary>
/// 验证 KV state 随机读预算的并发、取消和生命周期合同。
/// </summary>
public sealed class KvDiskReadBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-kv-read-budget-" + Guid.NewGuid().ToString("N"));

    public KvDiskReadBudgetTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不应掩盖预算断言；测试不会把该目录当作交付物。
        }
    }

    [Fact]
    public async Task Acquire_WhenPermitHeld_CancellationReleasesWaiter()
    {
        using var budget = new KvDiskReadBudget(maxConcurrentReads: 1);
        KvDiskReadBudget.ReadLease first = budget.Acquire(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task<KvDiskReadBudget.ReadLease> waiting = Task.Run(
            () => budget.Acquire(cancellation.Token));

        try
        {
            await WaitUntilAsync(
                () => budget.QueuedReads == 1,
                TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => waiting.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(1, budget.CanceledWaits);
            Assert.Equal(1, budget.ActiveReads);
        }
        finally
        {
            cancellation.Cancel();
            first.Dispose();
            try
            {
                // 释放主许可后，异常路径中的等待者最多再运行一个有界调度周期；
                // 如果它在取消观察前抢到许可，也必须释放返回的 lease。
                using KvDiskReadBudget.ReadLease lateLease =
                    await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
        }

        using KvDiskReadBudget.ReadLease second = budget.Acquire(CancellationToken.None);
        second.RecordRead(7);
        Assert.Equal(1, budget.ActiveReads);
        second.Dispose();
        Assert.Equal(1, budget.CompletedReads);
        Assert.Equal(7, budget.CompletedBytes);
    }

    [Fact]
    public async Task Acquire_TracksPeakAndCompletedBytesWithinConfiguredLimit()
    {
        using var budget = new KvDiskReadBudget(maxConcurrentReads: 2);
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        int startedReaders = 0;
        int activeReaders = 0;
        Task[] readers = Enumerable.Range(1, 4)
            .Select(bytes => Task.Run(() =>
            {
                if (Interlocked.Increment(ref startedReaders) == 4)
                    started.Set();
                using KvDiskReadBudget.ReadLease lease = budget.Acquire(CancellationToken.None);
                if (Interlocked.Increment(ref activeReaders) >= 2)
                    entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("测试未在预算期限内释放随机读许可。");
                lease.RecordRead(bytes);
            }))
            .ToArray();

        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, budget.ActiveReads);
            await WaitUntilAsync(
                () => budget.QueuedReads >= 2,
                TimeSpan.FromSeconds(5));
            release.Set();
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(2, budget.PeakConcurrentReads);
        Assert.Equal(4, budget.CompletedReads);
        Assert.Equal(10, budget.CompletedBytes);
    }

    [Fact]
    public void Open_WithInvalidReadBudget_DoesNotRetainLifecycleLease()
    {
        string keyspaceRoot = Path.Combine(_root, "invalid-options");
        KvOptions invalid = new() { MaxConcurrentStateReads = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => KvKeyspace.Open("sample", keyspaceRoot, invalid));

        using KvKeyspace reopened = KvKeyspace.Open(
            "sample",
            keyspaceRoot,
            new KvOptions { MaxConcurrentStateReads = 1 });
    }

    /// <summary>验证满队列立即拒绝，并且不会破坏现有读取租约。</summary>
    [Fact]
    public async Task Acquire_WhenQueueFull_RejectsAndPreservesExistingWaiter()
    {
        using var budget = new KvDiskReadBudget(1, maxQueuedReads: 1);
        using var first = budget.Acquire(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<KvDiskReadBudget.ReadLease> waiting = Task.Run(() => budget.Acquire(cancellation.Token));
        try
        {
            await WaitUntilAsync(() => budget.QueuedReads == 1, TimeSpan.FromSeconds(3));
            Assert.Throws<KvReadOverloadedException>(() => budget.Acquire(CancellationToken.None));
            Assert.Equal(1, budget.RejectedReads);
            Assert.Equal(1, budget.QueuedReads);
        }
        finally
        {
            first.Dispose();
            using var resumed = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, budget.QueuedReads);
        Assert.Equal(0, budget.ActiveReads);
    }

    /// <summary>验证超时释放队列名额，调用方取消保留单独语义。</summary>
    [Fact]
    public async Task Acquire_WhenWaitTimesOut_RejectsAndReleasesQueueSlot()
    {
        using var budget = new KvDiskReadBudget(1, 1, TimeSpan.FromMilliseconds(30));
        using var first = budget.Acquire(CancellationToken.None);
        await Assert.ThrowsAsync<KvReadOverloadedException>(() => Task.Run(
            () => budget.Acquire(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, budget.TimedOutWaits);
        Assert.Equal(0, budget.QueuedReads);
        Assert.Equal(0, budget.CanceledWaits);
        first.Dispose();
        using var resumed = budget.Acquire(CancellationToken.None);
    }

    /// <summary>验证关闭时现有等待者可以完成，但没有 state 引用的新请求不能继续进入。</summary>
    [Fact]
    public async Task Dispose_WhenReadQueued_PreservesLeaseUntilWaiterCompletes()
    {
        using var budget = new KvDiskReadBudget(1);
        using var first = budget.Acquire(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<KvDiskReadBudget.ReadLease> waiting = Task.Run(() => budget.Acquire(cancellation.Token));
        await WaitUntilAsync(() => budget.QueuedReads == 1, TimeSpan.FromSeconds(3));
        budget.Dispose();
        Assert.Throws<ObjectDisposedException>(() => budget.Acquire(CancellationToken.None));
        first.Dispose();
        using var resumed = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        resumed.Dispose();
        resumed.Dispose();
        Assert.Equal(0, budget.ActiveReads);
        Assert.Equal(0, budget.QueuedReads);
        Assert.Throws<ObjectDisposedException>(() => budget.Acquire(CancellationToken.None));
    }

    /// <summary>验证请求槽完全占满且不允许排队时，checkpoint 仍能合并旧文件。</summary>
    [Fact]
    public async Task CreateSnapshot_WhenRequestBudgetFull_UsesReservedMaintenanceRead()
    {
        using var budget = new KvDiskReadBudget(1, maxQueuedReads: 0);
        using var keyspace = KvKeyspace.Open("checkpoint", Path.Combine(_root, "checkpoint"),
            KvOptions.Default, budget);
        keyspace.Set("old", new byte[] { 1 });
        keyspace.CreateSnapshot();
        keyspace.Set("new", new byte[] { 2 });
        using var held = budget.Acquire(CancellationToken.None);
        await Task.Run(() => keyspace.CreateSnapshot()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(keyspace.LastCheckpointException);
        Assert.Equal(0, budget.RejectedReads);
        Assert.Equal(0, budget.MaintenanceActiveReads);
        held.Dispose();
        Assert.Equal(new byte[] { 1 }, keyspace.Get("old"));
        Assert.Equal(new byte[] { 2 }, keyspace.Get("new"));
    }

    /// <summary>验证维护超时不损坏旧快照、WAL 或永久写状态，释放后可重试恢复。</summary>
    [Fact]
    public async Task CreateSnapshot_WhenMaintenanceWaitTimesOut_PreservesDataAndRecovers()
    {
        using var budget = new KvDiskReadBudget(1, maxQueuedReads: 0,
            maintenanceReadWaitTimeout: TimeSpan.FromMilliseconds(30));
        using var keyspace = KvKeyspace.Open("maintenance-timeout", Path.Combine(_root, "maintenance-timeout"),
            KvOptions.Default, budget);
        keyspace.Set("old", new byte[] { 1 });
        keyspace.CreateSnapshot();
        keyspace.Set("new", new byte[] { 2 });
        using var maintenance = budget.Acquire(CancellationToken.None, maintenanceRead: true);
        await Assert.ThrowsAsync<TimeoutException>(() => Task.Run(
            () => keyspace.CreateSnapshot()).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<TimeoutException>(keyspace.LastCheckpointException);
        Assert.False(File.Exists(KvKeyspace.SnapshotPath(keyspace.RootDirectory, keyspace.LastSequence)));
        Assert.Equal(0, budget.RejectedReads);
        Assert.Equal(0, budget.TimedOutWaits);
        Assert.Equal(new byte[] { 1 }, keyspace.Get("old"));
        Assert.Equal(new byte[] { 2 }, keyspace.Get("new"));
        maintenance.Dispose();
        keyspace.Set("after-timeout", new byte[] { 3 });
        keyspace.CreateSnapshot();
        // 先恢复已冻结的 checkpoint，再保存超时后新增的覆盖层。
        long sequence = keyspace.CreateSnapshot();
        Assert.Null(keyspace.LastCheckpointException);
        Assert.Equal(new byte[] { 3 }, keyspace.Get("after-timeout"));
        using var restored = KvStateFile.OpenDiskState(
            KvKeyspace.SnapshotPath(keyspace.RootDirectory, sequence));
        restored.ValidateAllEntries();
    }

    /// <summary>验证所有者关闭后已有 state 引用继续保留快照读取能力。</summary>
    [Fact]
    public void Dispose_WhenStateRetained_AllowsReadUntilLastReferenceReleased()
    {
        using var budget = new KvDiskReadBudget(1);
        budget.AddStateReference();
        budget.Dispose();
        using var retainedRead = budget.Acquire(CancellationToken.None);
        budget.ReleaseStateReference();
        Assert.Throws<ObjectDisposedException>(() => budget.Acquire(CancellationToken.None));
        retainedRead.Dispose();
        Assert.Equal(0, budget.ActiveReads);
    }

    /// <summary>验证请求预算参数拒绝非法或无限的等待配置。</summary>
    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 1, -1)]
    [InlineData(1, 1, 2147483648d)]
    public void Constructor_WithInvalidBounds_Throws(int concurrency, int queued, double milliseconds)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new KvDiskReadBudget(
            concurrency, queued, TimeSpan.FromMilliseconds(milliseconds)));

    /// <summary>有界检查异步调度条件，避免测试留下无限等待。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        int attempts = Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds / 10));
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (condition())
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        throw new TimeoutException("测试条件未在有界等待期限内满足。");
    }
}
