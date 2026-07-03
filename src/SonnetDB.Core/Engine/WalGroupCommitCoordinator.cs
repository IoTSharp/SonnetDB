using SonnetDB.Wal;

namespace SonnetDB.Engine;

internal sealed class WalGroupCommitCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly bool _enabled;
    private readonly TimeSpan _flushWindow;

    private TaskCompletionSource? _pendingCompletion;
    private bool _disposed;

    public WalGroupCommitCoordinator(WalGroupCommitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.FlushWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.FlushWindow), "WAL group-commit 窗口不能为负数。");

        _enabled = options.Enabled;
        _flushWindow = options.FlushWindow;
    }

    public WalGroupCommitTicket Prepare(WalSegmentSet walSet)
    {
        ArgumentNullException.ThrowIfNull(walSet);

        if (!_enabled || _flushWindow == TimeSpan.Zero)
        {
            lock (_sync)
                ThrowIfDisposed();
            // 不在此处（调用方仍持 _writeSync）执行 fsync，否则所有写入者串行排在 fsync 后（S10/C5）。
            // 返回携带 walSet 的 ticket，由调用方在释放写锁后于 Wait() 内 fsync，使并发 fsync 可在 OS 层重叠。
            return new WalGroupCommitTicket(walSet);
        }

        Task task;
        bool shouldSchedule = false;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_pendingCompletion is null)
            {
                _pendingCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                shouldSchedule = true;
            }

            task = _pendingCompletion.Task;
        }

        if (shouldSchedule)
            _ = FlushAfterDelayAsync(walSet);

        return new WalGroupCommitTicket(task);
    }

    public void FlushPending(WalSegmentSet walSet)
    {
        ArgumentNullException.ThrowIfNull(walSet);
        if (!_enabled || _flushWindow == TimeSpan.Zero)
            return;

        CompletePending(walSet);
    }

    public void Dispose()
    {
        TaskCompletionSource? pending;
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            pending = _pendingCompletion;
            _pendingCompletion = null;
        }

        pending?.TrySetException(new ObjectDisposedException(nameof(WalGroupCommitCoordinator)));
    }

    private async Task FlushAfterDelayAsync(WalSegmentSet walSet)
    {
        await Task.Delay(_flushWindow).ConfigureAwait(false);
        CompletePending(walSet);
    }

    private void CompletePending(WalSegmentSet walSet)
    {
        TaskCompletionSource? completion;
        lock (_sync)
        {
            completion = _pendingCompletion;
            _pendingCompletion = null;
        }

        if (completion is null)
            return;

        try
        {
            walSet.Sync();
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WalGroupCommitCoordinator));
    }
}

internal readonly struct WalGroupCommitTicket
{
    private readonly Task? _completion;
    private readonly WalSegmentSet? _deferredSyncTarget;

    /// <summary>group-commit 模式：等待共享 fsync 批次完成。</summary>
    public WalGroupCommitTicket(Task completion)
    {
        _completion = completion;
        _deferredSyncTarget = null;
    }

    /// <summary>
    /// 直写模式（group-commit 禁用 / window=0）：把 fsync 推迟到 <see cref="Wait"/>，
    /// 由调用方在释放 <c>_writeSync</c> 后执行，避免锁内 fsync 串行化所有写入者（S10/C5）。
    /// </summary>
    public WalGroupCommitTicket(WalSegmentSet deferredSyncTarget)
    {
        _completion = null;
        _deferredSyncTarget = deferredSyncTarget;
    }

    public void Wait()
    {
        _completion?.GetAwaiter().GetResult();

        if (_deferredSyncTarget is null)
            return;

        try
        {
            _deferredSyncTarget.Sync();
        }
        catch (ObjectDisposedException)
        {
            // 与并发 Dispose 竞争：WAL 已被关闭，而 Dispose 路径自身会 fsync active writer
            // （WalSegmentSet.Dispose）保证持久性，这里的推迟 fsync 可安全跳过。
        }
    }
}
