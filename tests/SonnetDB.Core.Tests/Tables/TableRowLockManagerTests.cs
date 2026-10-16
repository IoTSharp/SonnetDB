using SonnetDB.Exceptions;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>#248：独立行锁组件的生命周期与并发合同；不代表 SQL 已接线。</summary>
public sealed class TableRowLockManagerTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public void Acquire_NoWaitConflict_DoesNotAcquireEarlierResourcesOrReleaseExisting()
    {
        using var manager = new TableRowLockManager();
        using var first = manager.CreateOwner();
        using var second = manager.CreateOwner();
        using var observer = manager.CreateOwner();
        object table = new();
        var a = Key(table, 1);
        var b = Key(table, 2);
        var c = Key(table, 3);
        manager.Acquire(first, [b], true, Budget).Retain();
        manager.Acquire(second, [a], true, Budget).Retain();
        var error = Assert.Throws<SqlRowLockException>(() => manager.Acquire(second, [a, c, b], true, Budget));
        Assert.Equal(SqlRowLockException.NotAvailable, error.Code);
        using var free = manager.Acquire(observer, [c], true, Budget);
        Assert.Throws<SqlRowLockException>(() => manager.Acquire(observer, [a], true, Budget));
    }

    [Fact]
    public async Task Acquire_NoWaitConflicts_ReturnsCompletedFailureWithoutWaiter()
    {
        using var manager = new TableRowLockManager();
        using var first = manager.CreateOwner();
        using var second = manager.CreateOwner();
        var key = Key(new object(), 1);
        manager.Acquire(first, [key], true, Budget).Retain();
        Task<TableRowLockManager.Lease> task = manager.AcquireAsync(second, [key], true, Budget);
        Assert.True(task.IsCompleted);
        Assert.Equal(SqlRowLockException.NotAvailable, (await Assert.ThrowsAsync<SqlRowLockException>(() => task)).Code);
        Assert.Equal(0, manager.Inspect().Waiting);
    }

    [Fact]
    public async Task Acquire_WaitAndDifferentRows_ResumesAfterOwnerCompletion()
    {
        using var manager = new TableRowLockManager();
        using var first = manager.CreateOwner();
        using var second = manager.CreateOwner();
        using var third = manager.CreateOwner();
        object table = new();
        var a = Key(table, 1);
        manager.Acquire(first, [a], true, Budget).Retain();
        var pending = manager.AcquireAsync(second, [a], false, Budget);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, manager.Inspect().Waiting);
        using var independent = manager.Acquire(third, [Key(table, 2)], true, Budget);
        // 已持锁事务重复读取不受后来排队者反向阻塞。
        using var repeated = manager.Acquire(first, [a], true, Budget);
        first.Dispose();
        using var acquired = await pending.WaitAsync(Budget);
        Assert.Equal(0, manager.Inspect().Waiting);
    }

    [Fact]
    public async Task Acquire_Cancellation_RemovesWaiterAndKeepsOwnersLock()
    {
        using var manager = new TableRowLockManager();
        using var first = manager.CreateOwner();
        using var second = manager.CreateOwner();
        using var cancellation = new CancellationTokenSource();
        var key = Key(new object(), 1);
        manager.Acquire(first, [key], true, Budget).Retain();
        var pending = manager.AcquireAsync(second, [key], false, Budget, cancellation.Token);
        Assert.Equal(1, manager.Inspect().Waiting);
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal((1, 0, 2), manager.Inspect());
        first.Dispose();
        using var acquired = manager.Acquire(second, [key], true, Budget);
    }

    [Fact]
    public async Task Acquire_Timeout_DoesNotLeakPartialBatch()
    {
        using var manager = new TableRowLockManager();
        using var first = manager.CreateOwner();
        using var second = manager.CreateOwner();
        object table = new();
        manager.Acquire(first, [Key(table, 1)], true, Budget).Retain();
        var pending = manager.AcquireAsync(second, [Key(table, 2), Key(table, 1)], false, TimeSpan.FromMilliseconds(30));
        Assert.Equal(SqlRowLockException.WaitTimeout, (await Assert.ThrowsAsync<SqlRowLockException>(() => pending)).Code);
        Assert.Equal((1, 0, 2), manager.Inspect());
        using var free = manager.Acquire(second, [Key(table, 2)], true, Budget);
    }

    [Fact]
    public async Task Acquire_Deadlock_FailsNewWaitAndPreservesPriorLocks()
    {
        using var manager = new TableRowLockManager();
        using var first = manager.CreateOwner();
        using var second = manager.CreateOwner();
        object table = new();
        var a = Key(table, 1);
        var b = Key(table, 2);
        manager.Acquire(first, [a], true, Budget).Retain();
        manager.Acquire(second, [b], true, Budget).Retain();
        var pending = manager.AcquireAsync(first, [b], false, Budget);
        Assert.Equal(1, manager.Inspect().Waiting);
        var error = await Assert.ThrowsAsync<SqlRowLockException>(() => manager.AcquireAsync(second, [a], false, Budget));
        Assert.Equal(SqlRowLockException.Deadlock, error.Code);
        Assert.Equal(2, manager.Inspect().Held);
        second.Dispose();
        using var acquired = await pending.WaitAsync(Budget);
    }

    [Fact]
    public async Task Acquire_TableExclusive_ConflictsWithRowsAndPreventsQueueBypass()
    {
        using var manager = new TableRowLockManager();
        using var first = manager.CreateOwner();
        using var schema = manager.CreateOwner();
        using var third = manager.CreateOwner();
        object table = new();
        manager.Acquire(first, [Key(table, 1)], true, Budget).Retain();
        var pending = manager.AcquireAsync(schema, [TableRowLockManager.Resource.TableExclusive(table)], false, Budget);
        Assert.Throws<SqlRowLockException>(() => manager.Acquire(third, [Key(table, 2)], true, Budget));
        first.Dispose();
        using (await pending.WaitAsync(Budget))
            Assert.Throws<SqlRowLockException>(() => manager.Acquire(third, [Key(table, 1)], true, Budget));
        using var released = manager.Acquire(third, [Key(table, 1)], true, Budget);
    }

    [Fact]
    public void Lease_DisposePreservesPriorReferenceAndRetainEndsWithOwner()
    {
        using var manager = new TableRowLockManager();
        using var first = manager.CreateOwner();
        using var second = manager.CreateOwner();
        var key = Key(new object(), 1);
        manager.Acquire(first, [key], true, Budget).Retain();
        using (manager.Acquire(first, [key, key], true, Budget)) { }
        Assert.Throws<SqlRowLockException>(() => manager.Acquire(second, [key], true, Budget));
        first.Dispose();
        using var free = manager.Acquire(second, [key], true, Budget);
    }

    [Fact]
    public void Acquire_EmptyAndEqualTableObjects_KeepNoMatchAndReferenceIdentity()
    {
        using var manager = new TableRowLockManager();
        using var first = manager.CreateOwner();
        using var second = manager.CreateOwner();
        manager.Acquire(first, [], true, Budget).Retain();
        Assert.Equal(0, manager.Inspect().Held);
        var table = new EqualTable(1);
        using var original = manager.Acquire(first, [Key(table, 1)], true, Budget);
        using var independent = manager.Acquire(second, [Key(new EqualTable(1), 1)], true, Budget);
        Assert.Throws<SqlRowLockException>(() => manager.Acquire(second, [Key(table, 1)], true, Budget));
    }

    [Fact]
    public void Acquire_CopiedKey_DoesNotFollowCallerMutation()
    {
        using var manager = new TableRowLockManager();
        using var first = manager.CreateOwner();
        using var second = manager.CreateOwner();
        object table = new();
        byte[] bytes = [1, 2, 3];
        var key = TableRowLockManager.Resource.Row(table, bytes);
        bytes[0] = 9;
        using var held = manager.Acquire(first, [key], true, Budget);
        Assert.Throws<SqlRowLockException>(() => manager.Acquire(second, [TableRowLockManager.Resource.Row(table, [1, 2, 3])], true, Budget));
        using var changed = manager.Acquire(second, [TableRowLockManager.Resource.Row(table, bytes)], true, Budget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_OwnerOrManager_ReleasesAndInterruptsWait(bool disposeManager)
    {
        using var manager = new TableRowLockManager();
        using var first = manager.CreateOwner();
        using var second = manager.CreateOwner();
        var key = Key(new object(), 1);
        manager.Acquire(first, [key], true, Budget).Retain();
        var pending = manager.AcquireAsync(second, [key], false, Budget);
        if (disposeManager) manager.Dispose();
        else second.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        Assert.Equal(0, manager.Inspect().Waiting);
        first.Dispose();
        Assert.Equal(0, manager.Inspect().Held);
    }

    [Fact]
    public async Task Acquire_Limits_DoNotRetainRejectedResources()
    {
        using var manager = new TableRowLockManager(maxResources: 1, maxWaiters: 1);
        using var first = manager.CreateOwner();
        using var second = manager.CreateOwner();
        using var third = manager.CreateOwner();
        using var cancellation = new CancellationTokenSource();
        object table = new();
        var key = Key(table, 1);
        manager.Acquire(first, [key], true, Budget).Retain();
        Assert.Equal(SqlRowLockException.LimitExceeded,
            Assert.Throws<SqlRowLockException>(() => manager.Acquire(second, [Key(table, 2)], true, Budget)).Code);
        var pending = manager.AcquireAsync(second, [key], false, Budget, cancellation.Token);
        Assert.Equal(SqlRowLockException.LimitExceeded,
            (await Assert.ThrowsAsync<SqlRowLockException>(() => manager.AcquireAsync(third, [key], false, Budget))).Code);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal((1, 0, 3), manager.Inspect());
    }

    [Fact]
    public async Task Acquire_ThreeOwnerCycle_FailsNewestRequestWithoutPartialLocks()
    {
        using var manager = new TableRowLockManager();
        using var first = manager.CreateOwner();
        using var second = manager.CreateOwner();
        using var third = manager.CreateOwner();
        object table = new();
        manager.Acquire(first, [Key(table, 1)], true, Budget).Retain();
        manager.Acquire(second, [Key(table, 2)], true, Budget).Retain();
        manager.Acquire(third, [Key(table, 3)], true, Budget).Retain();
        var firstWait = manager.AcquireAsync(first, [Key(table, 2)], false, Budget);
        var secondWait = manager.AcquireAsync(second, [Key(table, 3)], false, Budget);
        var error = await Assert.ThrowsAsync<SqlRowLockException>(() =>
            manager.AcquireAsync(third, [Key(table, 4), Key(table, 1)], false, Budget));
        Assert.Equal(SqlRowLockException.Deadlock, error.Code);
        Assert.Equal((3, 2, 3), manager.Inspect());
        using var free = manager.Acquire(third, [Key(table, 4)], true, Budget);
        third.Dispose();
        using var secondAcquired = await secondWait.WaitAsync(Budget);
        second.Dispose();
        using var firstAcquired = await firstWait.WaitAsync(Budget);
    }

    [Fact]
    public async Task Acquire_CancelFirstWaiter_PreservesNextWaiterUntilLockIsReleased()
    {
        using var manager = new TableRowLockManager();
        using var first = manager.CreateOwner();
        using var second = manager.CreateOwner();
        using var third = manager.CreateOwner();
        using var cancellation = new CancellationTokenSource();
        var key = Key(new object(), 1);
        manager.Acquire(first, [key], true, Budget).Retain();
        var cancelled = manager.AcquireAsync(second, [key], false, Budget, cancellation.Token);
        var next = manager.AcquireAsync(third, [key], false, Budget);
        Assert.Equal(2, manager.Inspect().Waiting);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, manager.Inspect().Waiting);
        Assert.False(next.IsCompleted);
        first.Dispose();
        using var acquired = await next.WaitAsync(Budget);
        Assert.Equal(0, manager.Inspect().Waiting);
    }

    [Fact]
    public async Task Acquire_KeyBudget_AccountsForWaitersAndReleasesCancelledReservation()
    {
        using var manager = new TableRowLockManager(maxResourceBytes: 600);
        using var first = manager.CreateOwner();
        using var second = manager.CreateOwner();
        using var third = manager.CreateOwner();
        using var cancellation = new CancellationTokenSource();
        object table = new();
        var key = Key(table, 1);
        manager.Acquire(first, [key], true, Budget).Retain();
        var pending = manager.AcquireAsync(second, [key], false, Budget, cancellation.Token);
        var largerKey = TableRowLockManager.Resource.Row(table, new byte[60]);
        Assert.Equal(SqlRowLockException.LimitExceeded,
            Assert.Throws<SqlRowLockException>(() => manager.Acquire(third, [largerKey], true, Budget)).Code);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        using (manager.Acquire(third, [largerKey], true, Budget)) { }
        first.Dispose();
        using var largest = manager.Acquire(third, [TableRowLockManager.Resource.Row(table, new byte[100])], true, Budget);
    }

    [Fact]
    public void Acquire_KeyBudgetFailure_DoesNotAcquirePartialRequest()
    {
        using var manager = new TableRowLockManager(maxResourceBytes: 600);
        using var first = manager.CreateOwner();
        using var observer = manager.CreateOwner();
        object table = new();
        var largeKey = TableRowLockManager.Resource.Row(table, new byte[100]);
        var smallKey = Key(table, 1);
        Assert.Equal(SqlRowLockException.LimitExceeded,
            Assert.Throws<SqlRowLockException>(() => manager.Acquire(first, [smallKey, largeKey], true, Budget)).Code);
        Assert.Equal(0, manager.Inspect().Held);
        using var free = manager.Acquire(observer, [largeKey], true, Budget);
    }

    [Fact]
    public void Acquire_UnregisteredOrForeignOwner_RejectsWithoutLocks()
    {
        using var manager = new TableRowLockManager();
        using var other = new TableRowLockManager();
        using var foreign = other.CreateOwner();
        using var unregistered = new TableRowLockManager.Owner(manager);
        var key = Key(new object(), 1);
        Assert.Throws<ArgumentException>(() => manager.Acquire(foreign, [key], true, Budget));
        Assert.Throws<ArgumentException>(() => manager.Acquire(unregistered, [key], true, Budget));
        Assert.Equal((0, 0, 0), manager.Inspect());
    }

    [Fact]
    public async Task Operation_CapturedByDelayedWorker_DoesNotReuseDisposedOwner()
    {
        using var manager = new TableRowLockManager();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? worker = null;
        TableLockOperation.Run(manager, () =>
        {
            worker = Task.Run(async () =>
            {
                await release.Task;
                TableLockOperation.Run(manager, () => TableLockOperation.Require(manager, [Key(new object(), 1)]));
            });
        });
        release.SetResult();
        await worker!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((0, 0, 0), manager.Inspect());
    }

    private static TableRowLockManager.Resource Key(object table, byte key)
        => TableRowLockManager.Resource.Row(table, [key]);

    private sealed record EqualTable(int Number);
}
