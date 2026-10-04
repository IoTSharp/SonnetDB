using SonnetDB.Diagnostics;
using SonnetDB.Exceptions;

namespace SonnetDB.Kv;

/// <summary>限制同一数据库的随机读并发和请求等待，另保留一个 checkpoint 读取槽。</summary>
/// <remarks>许可只覆盖实际 I/O；CRC 与调用方消费不会占用物理读槽。</remarks>
internal sealed class KvDiskReadBudget : IDisposable
{
    internal const int DefaultMaxConcurrentReads = 8;
    internal const int DefaultMaxQueuedReads = 64;
    internal const int DefaultReadWaitTimeoutMilliseconds = 5000;
    internal const int DefaultMaintenanceReadWaitTimeoutMilliseconds = 120_000;
    private readonly SemaphoreSlim _permits;
    private readonly SemaphoreSlim _maintenancePermit = new(1, 1);
    private readonly object _lifecycleSync = new();
    private int _activeReads;
    private int _queuedReads;
    private int _maintenanceActiveReads;
    private int _maintenanceQueuedReads;
    private int _peakConcurrentReads;
    private int _stateReferences;
    private bool _ownerReleased;
    private bool _semaphoreDisposed;
    private long _completedReads;
    private long _completedBytes;
    private long _canceledWaits;
    private long _rejectedReads;
    private long _timedOutWaits;

    /// <summary>创建有界请求预算；维护槽不计入请求并发额度。</summary>
    internal KvDiskReadBudget(int maxConcurrentReads,
        int maxQueuedReads = DefaultMaxQueuedReads, TimeSpan? readWaitTimeout = null,
        TimeSpan? maintenanceReadWaitTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentReads);
        ArgumentOutOfRangeException.ThrowIfNegative(maxQueuedReads);
        var timeout = readWaitTimeout ?? TimeSpan.FromMilliseconds(DefaultReadWaitTimeoutMilliseconds);
        if (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(readWaitTimeout));
        var maintenanceTimeout = maintenanceReadWaitTimeout
            ?? TimeSpan.FromMilliseconds(DefaultMaintenanceReadWaitTimeoutMilliseconds);
        if (maintenanceTimeout < TimeSpan.Zero || maintenanceTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maintenanceReadWaitTimeout));
        MaxConcurrentReads = maxConcurrentReads;
        MaxQueuedReads = maxQueuedReads;
        ReadWaitTimeout = timeout;
        MaintenanceReadWaitTimeout = maintenanceTimeout;
        _permits = new SemaphoreSlim(maxConcurrentReads, maxConcurrentReads);
    }

    internal int MaxConcurrentReads { get; }
    internal int MaxQueuedReads { get; }
    internal TimeSpan ReadWaitTimeout { get; }
    internal TimeSpan MaintenanceReadWaitTimeout { get; }
    internal int ActiveReads => Volatile.Read(ref _activeReads);
    internal int QueuedReads => Volatile.Read(ref _queuedReads);
    internal int MaintenanceActiveReads => Volatile.Read(ref _maintenanceActiveReads);
    internal int PeakConcurrentReads => Volatile.Read(ref _peakConcurrentReads);
    internal long CompletedReads => Interlocked.Read(ref _completedReads);
    internal long CompletedBytes => Interlocked.Read(ref _completedBytes);
    internal long CanceledWaits => Interlocked.Read(ref _canceledWaits);
    internal long RejectedReads => Interlocked.Read(ref _rejectedReads);
    internal long TimedOutWaits => Interlocked.Read(ref _timedOutWaits);

    /// <summary>保留 state 生命周期，允许现有快照在数据库关闭后完成读取。</summary>
    internal void AddStateReference()
    {
        lock (_lifecycleSync)
        {
            ThrowIfSemaphoreDisposedLocked();
            if (_ownerReleased)
                throw new ObjectDisposedException(nameof(KvDiskReadBudget));
            _stateReferences = checked(_stateReferences + 1);
        }
    }

    /// <summary>释放 state 引用，必要时关闭预算。</summary>
    internal void ReleaseStateReference()
    {
        lock (_lifecycleSync)
        {
            if (_stateReferences <= 0)
                throw new InvalidOperationException("KV disk read budget state 引用计数无效。");
            _stateReferences--;
            TryDisposeSemaphoreLocked();
        }
    }

    /// <summary>取得请求或维护许可；请求过载不会占用或拒绝维护槽。</summary>
    internal ReadLease Acquire(CancellationToken cancellationToken, bool maintenanceRead = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SemaphoreSlim permits = maintenanceRead ? _maintenancePermit : _permits;
        lock (_lifecycleSync)
        {
            ThrowIfSemaphoreDisposedLocked();
            if (_ownerReleased && _stateReferences == 0)
                throw new ObjectDisposedException(nameof(KvDiskReadBudget));
            // 只有真正等待的请求才占有界队列名额。
            if (permits.Wait(0))
                return ActivateReadLocked(maintenanceRead);
            if (!maintenanceRead && _queuedReads >= MaxQueuedReads)
            {
                Interlocked.Increment(ref _rejectedReads);
                SonnetDbMeter.KvStateReadRejected.Add(1, new KeyValuePair<string, object?>("reason", "queue_full"));
                throw new KvReadOverloadedException("KV 物理读取等待队列已满。");
            }
            ChangeQueuedReadsLocked(maintenanceRead, 1);
        }

        long waitStarted = SonnetDbMeter.StartKvStateReadWaitTiming();
        try
        {
            if (maintenanceRead)
            {
                // 维护等待独立的有限期限，不套用请求队列容量或请求等待期限。
                if (!permits.Wait(MaintenanceReadWaitTimeout, cancellationToken))
                {
                    SonnetDbMeter.KvStateReadMaintenanceTimeouts.Add(1);
                    throw new TimeoutException("KV checkpoint 维护读取等待超过独立时限。");
                }
            }
            else if (!permits.Wait(ReadWaitTimeout, cancellationToken))
            {
                Interlocked.Increment(ref _timedOutWaits);
                SonnetDbMeter.KvStateReadRejected.Add(1, new KeyValuePair<string, object?>("reason", "wait_timeout"));
                throw new KvReadOverloadedException("KV 物理读取等待超过有界时限。");
            }
            lock (_lifecycleSync)
                return ActivateReadLocked(maintenanceRead);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _canceledWaits);
            throw;
        }
        finally
        {
            lock (_lifecycleSync)
            {
                // 转为活跃读后才移出队列，Dispose 无法提前关闭许可。
                ChangeQueuedReadsLocked(maintenanceRead, -1);
                TryDisposeSemaphoreLocked();
            }
            SonnetDbMeter.RecordKvStateReadWait(waitStarted);
        }
    }

    /// <summary>登记活跃读取，避免等待、释放和关闭之间的竞态。</summary>
    private ReadLease ActivateReadLocked(bool maintenanceRead)
    {
        if (maintenanceRead)
            _maintenanceActiveReads++;
        else
            _peakConcurrentReads = Math.Max(_peakConcurrentReads, ++_activeReads);
        SonnetDbMeter.KvStateReadActive.Add(1, ReadKindTag(maintenanceRead));
        return new ReadLease(this, maintenanceRead);
    }

    /// <summary>更新排队数量及低基数指标。</summary>
    private void ChangeQueuedReadsLocked(bool maintenanceRead, int delta)
    {
        if (maintenanceRead)
            _maintenanceQueuedReads += delta;
        else
            _queuedReads += delta;
        SonnetDbMeter.KvStateReadQueued.Add(delta, ReadKindTag(maintenanceRead));
    }

    /// <summary>只区分请求与维护，避免数据库名和文件路径使指标维度膨胀。</summary>
    private static KeyValuePair<string, object?> ReadKindTag(bool maintenanceRead)
        => new("kind", maintenanceRead ? "maintenance" : "request");

    /// <summary>归还许可，并在同一临界区维护生命周期计数。</summary>
    private void Release(int bytesRead, bool completed, bool maintenanceRead)
    {
        if (completed)
        {
            Interlocked.Increment(ref _completedReads);
            Interlocked.Add(ref _completedBytes, bytesRead);
        }
        lock (_lifecycleSync)
        {
            if (maintenanceRead)
                _maintenanceActiveReads--;
            else
                _activeReads--;
            SonnetDbMeter.KvStateReadActive.Add(-1, ReadKindTag(maintenanceRead));
            (maintenanceRead ? _maintenancePermit : _permits).Release();
            TryDisposeSemaphoreLocked();
        }
    }

    /// <summary>释放所有者；现有 state 和读租约继续保持许可存活。</summary>
    public void Dispose()
    {
        lock (_lifecycleSync)
        {
            _ownerReleased = true;
            TryDisposeSemaphoreLocked();
        }
    }

    /// <summary>在所有引用、请求和维护读取结束后关闭信号量。</summary>
    private void TryDisposeSemaphoreLocked()
    {
        if (_semaphoreDisposed || !_ownerReleased || _stateReferences != 0
            || _activeReads != 0 || _queuedReads != 0
            || _maintenanceActiveReads != 0 || _maintenanceQueuedReads != 0)
            return;
        _semaphoreDisposed = true;
        _permits.Dispose();
        _maintenancePermit.Dispose();
    }

    /// <summary>拒绝使用已关闭的预算。</summary>
    private void ThrowIfSemaphoreDisposedLocked()
    {
        if (_semaphoreDisposed)
            throw new ObjectDisposedException(nameof(KvDiskReadBudget));
    }

    /// <summary>持有一个读取许可，允许幂等释放。</summary>
    internal sealed class ReadLease : IDisposable
    {
        private KvDiskReadBudget? _owner;
        private readonly bool _maintenanceRead;
        private int _bytesRead;
        private bool _completed;

        /// <summary>记录预算及读取类型。</summary>
        internal ReadLease(KvDiskReadBudget owner, bool maintenanceRead)
        {
            _owner = owner;
            _maintenanceRead = maintenanceRead;
        }

        /// <summary>记录一次实际读取的完成字节数。</summary>
        internal void RecordRead(int bytesRead)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytesRead);
            if (_owner is null)
                throw new ObjectDisposedException(nameof(ReadLease));
            if (_completed)
                throw new InvalidOperationException("KV state read lease 已经记录完成。");
            _bytesRead = bytesRead;
            _completed = true;
        }

        /// <summary>仅归还一次许可。</summary>
        public void Dispose()
        {
            KvDiskReadBudget? owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(_bytesRead, _completed, _maintenanceRead);
        }
    }
}
