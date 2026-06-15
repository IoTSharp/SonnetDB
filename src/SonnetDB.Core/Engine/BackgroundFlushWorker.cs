namespace SonnetDB.Engine;

/// <summary>
/// 后台 Flush 工作线程：
/// 监听写路径的 "MemTable 可能命中阈值" 信号；
/// 周期性自查 <see cref="SonnetDB.Memory.MemTable.ShouldFlush"/>；
/// 命中阈值时调用 <see cref="Tsdb"/> 内部 Flush 入口（与同步 FlushNow 共享 _writeSync 锁）。
/// 线程安全 / 单实例 / 可优雅关闭。
/// </summary>
internal sealed class BackgroundFlushWorker : IDisposable
{
    private readonly Tsdb _owner;
    private readonly BackgroundFlushOptions _options;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private bool _disposed;

    private long _triggeredCount;
    private long _failureCount;
    private Exception? _lastError;

    /// <summary>当前累计已触发 Flush 尝试次数（仅当满足 FlushPolicy 阈值时；含成功和失败）。仅用于测试/诊断。</summary>
    public long TriggeredCount => Interlocked.Read(ref _triggeredCount);

    /// <summary>当前累计 Flush 异常次数。</summary>
    public long FailureCount => Interlocked.Read(ref _failureCount);

    /// <summary>最近一次 Flush 抛出的异常（仅最近一次；用于诊断）。</summary>
    public Exception? LastError => Volatile.Read(ref _lastError);

    /// <summary>
    /// 创建 <see cref="BackgroundFlushWorker"/> 实例（尚未启动）。
    /// </summary>
    /// <param name="owner">所属 <see cref="Tsdb"/> 实例。</param>
    /// <param name="options">后台 Flush 线程运行参数。</param>
    /// <exception cref="ArgumentNullException">任何参数为 null 时抛出。</exception>
    public BackgroundFlushWorker(Tsdb owner, BackgroundFlushOptions options)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(options);
        _owner = owner;
        _options = options;
    }

    /// <summary>
    /// 启动后台工作线程。只能调用一次。
    /// </summary>
    public void Start()
    {
        if (_thread != null)
            throw new InvalidOperationException("BackgroundFlushWorker 已启动。");

        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SonnetDB-FlushWorker",
        };
        _thread.Start();
    }

    /// <summary>
    /// 来自写路径的非阻塞信号：MemTable 刚追加了数据，可能需要 Flush。
    /// 多次调用幂等（SemaphoreSlim 容量为 1）。
    /// </summary>
    public void Signal()
    {
        // 容量为 1，已有信号时忽略（保证幂等）
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // 已经有待处理信号，忽略
            }
        }
    }

    /// <summary>
    /// 关闭后台线程，等待其优雅退出；超时则记录但不抛异常。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _cts.Cancel();

        // 唤醒可能阻塞的信号等待
        try { _signal.Release(); } catch (SemaphoreFullException) { }

        if (_thread != null)
        {
            bool exited = _thread.Join(_options.ShutdownTimeout);
            if (!exited)
            {
                // 超时：记录但不抛，防止死锁
                Interlocked.Exchange(ref _lastError,
                    new TimeoutException($"BackgroundFlushWorker 关闭超时（{_options.ShutdownTimeout}）。"));
            }
        }

        _cts.Dispose();
        _signal.Dispose();
    }

    // ── 私有 ──────────────────────────────────────────────────────────────────

    private void WorkerLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            // 等待信号或周期超时
            try
            {
                _signal.Wait(_options.PollInterval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_cts.IsCancellationRequested)
                break;

            // 检查是否需要 Flush
            if (_owner.MemTable.ShouldFlush(_owner.BackgroundFlushPolicy))
            {
                Interlocked.Increment(ref _triggeredCount);
                try
                {
                    _owner.InternalFlushFromBackground();
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failureCount);
                    Volatile.Write(ref _lastError, ex);
                    _owner.ReportBackgroundWorkerDiagnostic(
                        "BackgroundFlushWorker.Flush",
                        TsdbDiagnosticSeverity.Error,
                        "后台 Flush 执行失败；异常已被捕获，后续轮询会继续尝试。",
                        ex);
                }
            }
        }
    }
}
