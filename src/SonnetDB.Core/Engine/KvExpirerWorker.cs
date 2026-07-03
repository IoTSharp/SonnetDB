using SonnetDB.Kv;

namespace SonnetDB.Engine;

/// <summary>
/// KV 后台过期清理线程，周期性清理已打开 keyspace 中的过期 key。
/// </summary>
internal sealed class KvExpirerWorker : IDisposable
{
    private readonly Tsdb _owner;
    private readonly KvOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private bool _disposed;
    private long _executedRounds;
    private long _removedKeys;
    private long _failureCount;
    private Exception? _lastError;

    public KvExpirerWorker(Tsdb owner, KvOptions options)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(options);
        _owner = owner;
        _options = options;
    }

    public long ExecutedRounds => Interlocked.Read(ref _executedRounds);

    public long RemovedKeys => Interlocked.Read(ref _removedKeys);

    public long FailureCount => Interlocked.Read(ref _failureCount);

    public Exception? LastError => Volatile.Read(ref _lastError);

    public void Start()
    {
        if (_thread != null)
            throw new InvalidOperationException("KvExpirerWorker 已启动。");

        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SonnetDB-KvExpirerWorker",
        };
        _thread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();
        if (_thread != null)
        {
            _thread.Interrupt();
            bool exited = _thread.Join(_options.ExpirerShutdownTimeout);
            if (!exited)
            {
                Volatile.Write(ref _lastError,
                    new TimeoutException($"KvExpirerWorker 关闭超时（{_options.ExpirerShutdownTimeout}）。"));
            }
        }

        _cts.Dispose();
    }

    private void WorkerLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                Thread.Sleep(_options.ExpirerPollInterval);
            }
            catch (ThreadInterruptedException)
            {
                break;
            }

            if (_cts.IsCancellationRequested)
                break;

            try
            {
                int limit = _options.ExpirerBatchSize <= 0 ? int.MaxValue : _options.ExpirerBatchSize;
                int removed = _owner.CleanExpiredKeyspacesFromBackground(limit);
                Interlocked.Add(ref _removedKeys, removed);
                Interlocked.Increment(ref _executedRounds);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failureCount);
                Volatile.Write(ref _lastError, ex);
                _owner.ReportBackgroundWorkerDiagnostic(
                    "KvExpirerWorker.CleanExpired",
                    TsdbDiagnosticSeverity.Error,
                    "后台 KV 过期清理失败；异常已被捕获，后续轮询会继续尝试。",
                    ex);
            }
        }
    }
}
