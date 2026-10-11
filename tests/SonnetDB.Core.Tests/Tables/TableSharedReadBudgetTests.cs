using System.Diagnostics;
using SonnetDB.Exceptions;
using SonnetDB.Kv;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>验证关系表管理器把全部表的物理请求和维护读取约束到共享预算。</summary>
public sealed class TableSharedReadBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-table-shared-read-budget-" + Guid.NewGuid().ToString("N"));

    /// <summary>创建仅属于本测试的数据库目录。</summary>
    public TableSharedReadBudgetTests() => Directory.CreateDirectory(_root);

    /// <summary>验证首次打开及冷恢复的两张表共享一个请求槽和一个等待名额。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotRead_AcrossTables_SharesConcurrencyQueueAndRejection(bool restart)
    {
        using var manager = CreatePreparedManager(maxQueuedReads: 1, restart: restart);
        TableStore weights = manager.Open("weights");
        TableStore captures = manager.Open("captures");
        using TableReadSnapshot weightSnapshot = weights.AcquireTableReadSnapshot();
        using TableReadSnapshot captureSnapshot = captures.AcquireTableReadSnapshot();
        using TableReadSnapshot overflowSnapshot = captures.AcquireTableReadSnapshot();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        KvDiskReadBudget budget = manager.DiskReadBudget;
        var reads = new List<Task<TableRow?>>();

        weights.ConfigureDiskReadTestHook(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(8), deadline.Token))
                throw new TimeoutException("跨表请求测试未及时释放物理读取槽。");
        });

        try
        {
            // 使用稳定快照绕开单表写锁，观测真正跨表竞争的物理 I/O 许可。
            reads.Add(Task.Run(() => ReadRow(weights, weightSnapshot, 1L)));
            await WaitUntilAsync(() => entered.IsSet, deadline.Token);
            Assert.Equal(1, budget.ActiveReads);

            reads.Add(Task.Run(() => ReadRow(captures, captureSnapshot, 1L)));
            await WaitUntilAsync(() => budget.QueuedReads == 1, deadline.Token);

            Assert.Equal(1, budget.ActiveReads);
            Assert.False(reads[1].IsCompleted);
            Assert.Throws<KvReadOverloadedException>(() => ReadRow(captures, overflowSnapshot, 1L));
            Assert.Equal(1, budget.RejectedReads);
            Assert.Equal(1, budget.QueuedReads);

            release.Set();
            TableRow?[] results = await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            Assert.Equal("weights-1", Assert.IsType<TableRow>(results[0]).Values[1]);
            Assert.Equal("captures-1", Assert.IsType<TableRow>(results[1]).Values[1]);
            Assert.Equal(1, budget.PeakConcurrentReads);
            Assert.Equal(0, budget.ActiveReads);
            Assert.Equal(0, budget.QueuedReads);
        }
        finally
        {
            // 释放钩子后必须收束已创建任务，避免异常断言遗留后台读或目录租约。
            release.Set();
            await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(12));
            weights.ConfigureDiskReadTestHook(null);
        }
    }

    /// <summary>验证两表 checkpoint 共用独立维护单槽，请求槽占满时维护仍能完成。</summary>
    [Fact]
    public async Task Checkpoint_AcrossTables_SharesOneMaintenanceLaneIndependentOfRequests()
    {
        using var manager = CreatePreparedManager(maxQueuedReads: 0);
        TableStore weights = manager.Open("weights");
        TableStore captures = manager.Open("captures");
        weights.Insert([2L, "weights-2"]);
        captures.Insert([2L, "captures-2"]);
        using TableReadSnapshot captureSnapshot = captures.AcquireTableReadSnapshot();
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var releaseMaintenance = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        KvDiskReadBudget budget = manager.DiskReadBudget;
        using KvDiskReadBudget.ReadLease request = budget.Acquire(deadline.Token);
        var checkpoints = new List<Task<long>>();

        weights.ConfigureDiskReadTestHook(() =>
        {
            firstEntered.Set();
            if (!releaseMaintenance.Wait(TimeSpan.FromSeconds(8), deadline.Token))
                throw new TimeoutException("跨表维护测试未及时释放读取槽。");
        });
        captures.ConfigureDiskReadTestHook(() => secondEntered.Set());

        try
        {
            checkpoints.Add(Task.Run(() => weights.CreateSnapshot()));
            await WaitUntilAsync(() => firstEntered.IsSet, deadline.Token);
            Assert.Equal(1, budget.ActiveReads);
            Assert.Equal(1, budget.MaintenanceActiveReads);

            checkpoints.Add(Task.Run(() => captures.CreateSnapshot()));
            await WaitUntilAsync(() => budget.MaintenanceQueuedReads == 1, deadline.Token);
            Assert.False(secondEntered.IsSet);
            Assert.Equal(1, budget.MaintenanceActiveReads);
            Assert.Equal(0, budget.QueuedReads);

            // 请求队列禁用时的过载拒绝不影响另一表已排队的维护读取。
            Assert.Throws<KvReadOverloadedException>(() => ReadRow(captures, captureSnapshot, 1L));
            Assert.Equal(1, budget.RejectedReads);
            Assert.Equal(1, budget.MaintenanceQueuedReads);

            releaseMaintenance.Set();
            await Task.WhenAll(checkpoints).WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            Assert.True(secondEntered.IsSet);
            Assert.Equal(0, budget.MaintenanceActiveReads);
            Assert.Equal(0, budget.MaintenanceQueuedReads);
            Assert.Equal(1, budget.ActiveReads);

            request.Dispose();
            Assert.Equal("weights-1", weights.GetByPrimaryKey([1L])!.Values[1]);
            Assert.Equal("weights-2", weights.GetByPrimaryKey([2L])!.Values[1]);
            Assert.Equal("captures-1", captures.GetByPrimaryKey([1L])!.Values[1]);
            Assert.Equal("captures-2", captures.GetByPrimaryKey([2L])!.Values[1]);
        }
        finally
        {
            releaseMaintenance.Set();
            request.Dispose();
            await Task.WhenAll(checkpoints).WaitAsync(TimeSpan.FromSeconds(12));
            weights.ConfigureDiskReadTestHook(null);
            captures.ConfigureDiskReadTestHook(null);
        }
    }

    /// <summary>验证关闭管理器后两个既有磁盘快照仍可读取，最后一个释放后预算才终止。</summary>
    [Fact]
    public void Dispose_WithSnapshotsFromTwoTables_PreservesReadsUntilLastSnapshotReleased()
    {
        using var manager = CreatePreparedManager(maxQueuedReads: 1);
        TableStore weights = manager.Open("weights");
        TableStore captures = manager.Open("captures");
        using TableReadSnapshot weightSnapshot = weights.AcquireTableReadSnapshot();
        using TableReadSnapshot captureSnapshot = captures.AcquireTableReadSnapshot();
        KvDiskReadBudget budget = manager.DiskReadBudget;

        manager.Dispose();

        Assert.Throws<ObjectDisposedException>(() => manager.Open("weights"));
        Assert.Throws<ObjectDisposedException>(() => manager.Open("new-store"));
        Assert.Throws<ObjectDisposedException>(() => weights.AcquireTableReadSnapshot());
        Assert.Equal("weights-1", ReadRow(weights, weightSnapshot, 1L)!.Values[1]);
        Assert.Equal("captures-1", ReadRow(captures, captureSnapshot, 1L)!.Values[1]);

        weightSnapshot.Dispose();
        Assert.Equal("captures-1", ReadRow(captures, captureSnapshot, 1L)!.Values[1]);
        captureSnapshot.Dispose();

        Assert.Equal(0, budget.ActiveReads);
        Assert.Equal(0, budget.QueuedReads);
        Assert.Throws<ObjectDisposedException>(() => budget.Acquire(CancellationToken.None));
    }

    /// <summary>验证超时关闭后仍在压实的表保留预算租约，允许验证临时 state 并丢弃未发布结果。</summary>
    [Fact]
    public async Task Dispose_WithDelayedCompactionAndSnapshot_AllowsCheckpointToFinish()
    {
        using var manager = CreatePreparedManager(
            maxQueuedReads: 1, checkpointShutdownTimeout: TimeSpan.FromMilliseconds(100));
        TableStore weights = manager.Open("weights");
        weights.Compact();
        using TableReadSnapshot snapshot = weights.AcquireTableReadSnapshot();
        weights.Insert([2L, "weights-2"]);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        KvDiskReadBudget budget = manager.DiskReadBudget;
        var work = new List<Task>();

        weights.ConfigureDiskReadTestHook(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(8), deadline.Token))
                throw new TimeoutException("关闭回归未及时释放压实读取槽。");
        });

        try
        {
            Task compaction = Task.Run(() => weights.Compact());
            work.Add(compaction);
            await WaitUntilAsync(() => entered.IsSet, deadline.Token);
            Assert.Equal(1, budget.MaintenanceActiveReads);

            // 关闭只等待配置的100毫秒；延迟压实持有的 keyspace 租约不能随所有者释放。
            var closeElapsed = Stopwatch.StartNew();
            Task closed = Task.Run(() => manager.Dispose());
            work.Add(closed);
            await closed.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
            Assert.True(closeElapsed.Elapsed < TimeSpan.FromSeconds(5));
            Assert.False(compaction.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => manager.Open("weights"));

            release.Set();
            await compaction.WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            Assert.Equal(0, budget.MaintenanceActiveReads);
            Assert.Equal("weights-1", ReadRow(weights, snapshot, 1L)!.Values[1]);
            Assert.Null(ReadRow(weights, snapshot, 2L));

            snapshot.Dispose();
            Assert.Throws<ObjectDisposedException>(() => budget.Acquire(CancellationToken.None));
        }
        finally
        {
            // 管理器已关闭时不再修改 store 钩子；先释放并收束任务，再释放旧快照与事件。
            release.Set();
            await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(12));
        }
    }

    /// <summary>写入并 checkpoint 两张最小关系表，可选择重启以覆盖冷开预算接入。</summary>
    private TableManager CreatePreparedManager(
        int maxQueuedReads,
        bool restart = false,
        TimeSpan? checkpointShutdownTimeout = null)
    {
        KvOptions options = KvOptions.Default with
        {
            MaxConcurrentStateReads = 1,
            MaxQueuedStateReads = maxQueuedReads,
            StateReadWaitTimeoutMilliseconds = 10_000,
            AutoCheckpointEnabled = false,
            SyncWalOnEveryWrite = false,
            ExpirerEnabled = false,
            CleanupEnabled = false,
            CheckpointShutdownTimeout = checkpointShutdownTimeout ?? TimeSpan.FromSeconds(5),
        };
        var manager = new TableManager(_root, options);
        try
        {
            manager.Create(CreateSchema("weights"));
            manager.Create(CreateSchema("captures"));
            manager.Open("weights").Insert([1L, "weights-1"]);
            manager.Open("captures").Insert([1L, "captures-1"]);
            manager.CheckpointAll();
            if (restart)
            {
                manager.Dispose();
                manager = new TableManager(_root, options);
            }
            return manager;
        }
        catch
        {
            manager.Dispose();
            throw;
        }
    }

    /// <summary>创建同构主键表，使跨表测试只观察读取预算而不引入额外索引维护。</summary>
    private static TableSchema CreateSchema(string name)
        => TableSchema.Create(name,
            [("id", TableColumnType.Int64, false), ("value", TableColumnType.String, false)], ["id"]);

    /// <summary>在既有稳定快照中读取真实落盘行，避免通过覆盖层或表锁绕过预算。</summary>
    private static TableRow? ReadRow(TableStore store, TableReadSnapshot snapshot, long id)
        => store.GetByPrimaryKey(snapshot.Snapshot, snapshot.Schema, [id]);

    /// <summary>最多500轮及五秒等待诊断条件，整个测试的取消令牌同样约束退出。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        for (int attempt = 0; attempt < 500 && elapsed.Elapsed < TimeSpan.FromSeconds(5); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition())
                return;
            await Task.Delay(10, cancellationToken);
        }
        throw new TimeoutException("跨表读取预算条件未在有界等待期限内满足。");
    }

    /// <summary>核对独占目录归属后清理本测试产生的数据库；清理异常应暴露资源泄漏。</summary>
    public void Dispose()
    {
        string absolute = Path.GetFullPath(_root);
        string temporaryParent = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(absolute), temporaryParent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(absolute).StartsWith("sndb-table-shared-read-budget-", StringComparison.Ordinal))
            throw new InvalidOperationException("拒绝清理无法核对归属的测试目录。");
        if (Directory.Exists(absolute))
            Directory.Delete(absolute, recursive: true);
    }
}
