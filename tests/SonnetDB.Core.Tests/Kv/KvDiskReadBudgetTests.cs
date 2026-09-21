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
