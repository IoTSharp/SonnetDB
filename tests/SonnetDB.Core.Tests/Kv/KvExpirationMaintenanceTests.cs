using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

/// <summary>验证后台过期扫描的页边界、续扫回绕及管理器读锁隔离。</summary>
public sealed class KvExpirationMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-expiration-maintenance-" + Guid.NewGuid().ToString("N"));
    private static KvOptions Options => KvOptions.Default with
    {
        AutoCheckpointEnabled = false,
        SyncWalOnEveryWrite = false,
        ExpirerEnabled = false,
        CleanupEnabled = false
    };

    /// <summary>大量无 TTL 磁盘条目不能把一次后台清理变为全表扫描，后续页仍能清除尾部和回绕项。</summary>
    [Fact]
    public void MaintenanceBoundsScanAndEventuallyReachesExpiredTailAndWraparound()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var kv = KvKeyspace.Open("objects", _root, Options);
        var expiry = DateTimeOffset.UtcNow.AddDays(1);
        for (int index = 0; index < 9000; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            kv.Put($"item:{index:D6}", [1]);
        }
        kv.Put("z-expired", [2], expiry);
        kv.Compact();
        int visited = 0;
        kv.ConfigureSnapshotDiskScanTestHooks(null, _ => visited++);
        Assert.Equal(0, kv.CleanExpiredMaintenance(expiry.AddSeconds(1), 1024, deadline.Token));
        Assert.InRange(visited, 1, 4097);
        int removed = 0;
        for (int round = 0; round < 16 && removed == 0; round++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            removed += kv.CleanExpiredMaintenance(expiry.AddSeconds(1), 1024, deadline.Token);
        }
        Assert.Equal(1, removed);
        Assert.Null(kv.Get("z-expired"));
        kv.Put("a-wrapped", [3], expiry);
        removed = 0;
        for (int round = 0; round < 16 && removed == 0; round++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            removed += kv.CleanExpiredMaintenance(expiry.AddSeconds(1), 1024, deadline.Token);
        }
        Assert.Equal(1, removed);
        Assert.NotNull(kv.Get("item:000000"));
    }

    /// <summary>清理一个库时其它已打开库可立即取得，管理器不会覆盖整个维护周期持锁。</summary>
    [Fact(Timeout = 15000)]
    public async Task MaintenanceDoesNotHoldManagerLockAcrossKeyspaceScan()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var manager = new KvKeyspaceManager(_root, Options);
        var first = manager.Open("first");
        first.Put("key", [1]);
        first.Compact();
        var second = manager.Open("second");
        second.Put("live", [2]);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        first.ConfigureSnapshotDiskScanTestHooks(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test scan release");
        }, null);
        var maintenance = Task.Run(() => manager.CleanExpiredOpenedMaintenance(DateTimeOffset.UtcNow, 1024, deadline.Token));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            var reopened = await Task.Run(() => manager.Open("second")).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(second, reopened);
            Assert.Equal(new byte[] { 2 }, reopened.Get("live"));
        }
        finally
        {
            release.Set();
            await maintenance.WaitAsync(TimeSpan.FromSeconds(5));
            first.ConfigureSnapshotDiskScanTestHooks(null, null);
        }
    }

    /// <summary>调用方取消时不推进清理或删除条目，公开手动清理合同保持有效。</summary>
    [Fact]
    public void CancellationDoesNotDeleteAndManualCleanupKeepsItsContract()
    {
        using var kv = KvKeyspace.Open("cancel", _root, Options);
        var expiry = DateTimeOffset.UtcNow.AddDays(1);
        kv.Put("expired", [1], expiry);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => kv.CleanExpiredMaintenance(expiry.AddSeconds(1), 1, canceled.Token));
        Assert.NotNull(kv.Get("expired"));
        Assert.Equal(1, kv.CleanExpired(expiry.AddSeconds(1), 1));
    }

    /// <summary>只回收本测试创建且已关闭的独占目录。</summary>
    public void Dispose()
    {
        var root = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
            && Path.GetFileName(root).StartsWith("sonnetdb-expiration-maintenance-", StringComparison.Ordinal)
            && Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
