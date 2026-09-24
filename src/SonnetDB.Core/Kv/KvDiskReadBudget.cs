using SonnetDB.Diagnostics;

namespace SonnetDB.Kv;

/// <summary>
/// 限制同一嵌入式数据库内 KV state 文件的并发随机读。
/// </summary>
/// <remarks>
/// 许可只覆盖实际的 RandomAccess 读取，不覆盖 keyspace 锁、CRC 或调用方消费；
/// 因此慢调用方不会占住数据库写锁，也不会把结果缓冲计入 I/O 并发额度。
/// </remarks>
internal sealed class KvDiskReadBudget : IDisposable
{
    internal const int DefaultMaxConcurrentReads = 8;

    private readonly SemaphoreSlim _permits;
    private readonly object _lifecycleSync = new();
    private int _activeReads;
    private int _queuedReads;
    private int _peakConcurrentReads;
    private int _stateReferences;
    private bool _ownerReleased;
    private bool _semaphoreDisposed;
    private long _completedReads;
    private long _completedBytes;
    private long _canceledWaits;

    internal KvDiskReadBudget(int maxConcurrentReads)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentReads);
        MaxConcurrentReads = maxConcurrentReads;
        _permits = new SemaphoreSlim(maxConcurrentReads, maxConcurrentReads);
    }

    internal int MaxConcurrentReads { get; }

    internal int ActiveReads => Volatile.Read(ref _activeReads);

    internal int QueuedReads => Volatile.Read(ref _queuedReads);

    internal int PeakConcurrentReads => Volatile.Read(ref _peakConcurrentReads);

    internal long CompletedReads => Interlocked.Read(ref _completedReads);

    internal long CompletedBytes => Interlocked.Read(ref _completedBytes);

    internal long CanceledWaits => Interlocked.Read(ref _canceledWaits);

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

    internal ReadLease Acquire(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycleSync)
        {
            ThrowIfSemaphoreDisposedLocked();
            Interlocked.Increment(ref _queuedReads);
        }
        long waitStarted = SonnetDbMeter.StartKvStateReadWaitTiming();
        int active = 0;
        try
        {
            _permits.Wait(cancellationToken);
            active = Interlocked.Increment(ref _activeReads);
            UpdatePeak(active);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _canceledWaits);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _queuedReads);
            SonnetDbMeter.RecordKvStateReadWait(waitStarted);
            // A successful wait increments active before this point, so a concurrent
            // owner Dispose cannot close the semaphore before the returned lease exists.
            TryDisposeSemaphore();
        }

        return new ReadLease(this);
    }

    private void UpdatePeak(int active)
    {
        while (true)
        {
            int observed = Volatile.Read(ref _peakConcurrentReads);
            if (active <= observed)
                return;
            if (Interlocked.CompareExchange(ref _peakConcurrentReads, active, observed) == observed)
                return;
        }
    }

    private void Release(int bytesRead, bool completed)
    {
        if (completed)
        {
            Interlocked.Increment(ref _completedReads);
            Interlocked.Add(ref _completedBytes, bytesRead);
        }

        lock (_lifecycleSync)
        {
            Interlocked.Decrement(ref _activeReads);
            _permits.Release();
            // Keep the active-count decrement and permit return in the same
            // lifecycle critical section so owner disposal cannot close the
            // semaphore between those two operations.
            TryDisposeSemaphoreLocked();
        }
    }

    public void Dispose()
    {
        lock (_lifecycleSync)
        {
            _ownerReleased = true;
            TryDisposeSemaphoreLocked();
        }
    }

    private void TryDisposeSemaphore()
    {
        lock (_lifecycleSync)
            TryDisposeSemaphoreLocked();
    }

    private void TryDisposeSemaphoreLocked()
    {
        if (_semaphoreDisposed
            || !_ownerReleased
            || _stateReferences != 0
            || Volatile.Read(ref _activeReads) != 0
            || Volatile.Read(ref _queuedReads) != 0)
        {
            return;
        }

        _semaphoreDisposed = true;
        _permits.Dispose();
    }

    private void ThrowIfSemaphoreDisposedLocked()
    {
        if (_semaphoreDisposed)
            throw new ObjectDisposedException(nameof(KvDiskReadBudget));
    }

    /// <summary>持有一个随机读许可；释放时必须只调用一次。</summary>
    internal sealed class ReadLease : IDisposable
    {
        private KvDiskReadBudget? _owner;
        private int _bytesRead;
        private bool _completed;

        internal ReadLease(KvDiskReadBudget owner) => _owner = owner;

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

        public void Dispose()
        {
            KvDiskReadBudget? owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(_bytesRead, _completed);
        }
    }
}
