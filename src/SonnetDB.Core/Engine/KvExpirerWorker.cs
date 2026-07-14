using System.Diagnostics;
using SonnetDB.Diagnostics;
using SonnetDB.Kv;

namespace SonnetDB.Engine;

/// <summary>
/// KV 后台维护线程，周期性清理过期 key 与 generation cleanup manifest。
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
    private long _cleanupRounds;
    private long _cleanupFiles;
    private long _cleanupPendingFiles;
    private long _cleanupPendingBytes;
    private long _cleanupRateBits;
    private long _throttledRounds;
    private int _lastThrottleReason;
    private long _failureCount;
    private Exception? _lastError;
    private long _lastExpirerTimestamp;
    private long _lastCleanupTimestamp;
    private long _lastCpuTimestamp;
    private TimeSpan _lastProcessorTime;

    public KvExpirerWorker(Tsdb owner, KvOptions options)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(options);
        _owner = owner;
        _options = options;
    }

    public long ExecutedRounds => Interlocked.Read(ref _executedRounds);

    public long RemovedKeys => Interlocked.Read(ref _removedKeys);

    public long CleanupRounds => Interlocked.Read(ref _cleanupRounds);

    public long CleanupFiles => Interlocked.Read(ref _cleanupFiles);

    public long CleanupPendingFiles => Interlocked.Read(ref _cleanupPendingFiles);

    public long CleanupPendingBytes => Interlocked.Read(ref _cleanupPendingBytes);

    public double LastCleanupBytesPerSecond
        => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _cleanupRateBits));

    public long ThrottledRounds => Interlocked.Read(ref _throttledRounds);

    public KvCleanupThrottleReason LastThrottleReason
        => (KvCleanupThrottleReason)Volatile.Read(ref _lastThrottleReason);

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
                Thread.Sleep(PollInterval());
            }
            catch (ThreadInterruptedException)
            {
                break;
            }

            if (_cts.IsCancellationRequested)
                break;

            if (_options.ExpirerEnabled && IsDue(ref _lastExpirerTimestamp, _options.ExpirerPollInterval))
            {
                try
                {
                    int limit = _options.ExpirerBatchSize <= 0 ? int.MaxValue : _options.ExpirerBatchSize;
                    int removed = _owner.CleanExpiredKeyspacesFromBackground(limit);
                    Interlocked.Add(ref _removedKeys, removed);
                    Interlocked.Increment(ref _executedRounds);
                }
                catch (Exception ex)
                {
                    ReportFailure(
                        "KvExpirerWorker.CleanExpired",
                        "后台 KV 过期清理失败；异常已被捕获，后续轮询会继续尝试。",
                        ex);
                }
            }

            if (_options.CleanupEnabled && IsDue(ref _lastCleanupTimestamp, _options.CleanupPollInterval))
            {
                try
                {
                    KvCleanupThrottleReason throttleReason = GetCleanupThrottleReason();
                    if (throttleReason != KvCleanupThrottleReason.None)
                    {
                        KvCleanupRoundResult pending = _owner.GetCleanupStatusFromBackground();
                        UpdatePending(pending);
                        Volatile.Write(ref _lastThrottleReason, (int)throttleReason);
                        Interlocked.Increment(ref _throttledRounds);
                        SonnetDbMeter.KvCleanupThrottled.Add(1, ThrottleReasonTag(throttleReason));
                        continue;
                    }

                    Volatile.Write(ref _lastThrottleReason, (int)KvCleanupThrottleReason.None);
                    int limit = _options.CleanupMaxFilesPerRound <= 0
                        ? 1
                        : _options.CleanupMaxFilesPerRound;
                    long started = Stopwatch.GetTimestamp();
                    KvCleanupRoundResult result = _owner.CleanupKeyspacesFromBackground(limit);
                    TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
                    Interlocked.Add(ref _cleanupFiles, result.DeletedFiles);
                    if (result.ProcessedEntries > 0)
                        Interlocked.Increment(ref _cleanupRounds);
                    UpdatePending(result);
                    if (result.RemovedBytes > 0 && elapsed > TimeSpan.Zero)
                    {
                        double rate = result.RemovedBytes / elapsed.TotalSeconds;
                        Interlocked.Exchange(ref _cleanupRateBits, BitConverter.DoubleToInt64Bits(rate));
                    }
                }
                catch (Exception ex)
                {
                    SonnetDbMeter.KvCleanupFailures.Add(1);
                    ReportFailure(
                        "KvExpirerWorker.CleanupGeneration",
                        "后台 KV generation 文件回收失败；异常已被捕获，后续轮询会继续尝试。",
                        ex);
                }
            }
        }
    }

    public KvMaintenanceStatus GetStatus(KvCleanupRoundResult current)
        => new(
            CleanupRounds,
            CleanupFiles,
            current.PendingFiles,
            current.PendingBytes,
            LastCleanupBytesPerSecond,
            ThrottledRounds,
            LastThrottleReason == KvCleanupThrottleReason.None
                ? null
                : ThrottleReasonName(LastThrottleReason),
            LastError?.GetType().FullName);

    private void ReportFailure(string operation, string message, Exception exception)
    {
        Interlocked.Increment(ref _failureCount);
        Volatile.Write(ref _lastError, exception);
        _owner.ReportBackgroundWorkerDiagnostic(
            operation,
            TsdbDiagnosticSeverity.Error,
            message,
            exception);
    }

    private TimeSpan PollInterval()
    {
        TimeSpan interval = _options.ExpirerEnabled
            ? _options.ExpirerPollInterval
            : _options.CleanupPollInterval;
        if (_options.CleanupEnabled && _options.CleanupPollInterval < interval)
            interval = _options.CleanupPollInterval;
        return interval > TimeSpan.Zero ? interval : TimeSpan.FromMilliseconds(100);
    }

    private KvCleanupThrottleReason GetCleanupThrottleReason()
    {
        double? cpuPercent = SampleCpuPercent();
        KvCleanupThrottleReason injected = _owner.GetInjectedCleanupThrottleReason();
        if (injected != KvCleanupThrottleReason.None)
            return injected;
        if (_options.CleanupPauseWhenQueriesActive && QueryActivityTracker.ActiveQueries > 0)
            return KvCleanupThrottleReason.ActiveQueries;
        if (_options.CleanupPauseWhenFlushPending && _owner.FlushPumpPendingCount > 0)
            return KvCleanupThrottleReason.FlushPending;

        if (_options.CleanupMaxMemoryLoadPercent > 0)
        {
            GCMemoryInfo memory = GC.GetGCMemoryInfo();
            if (memory.HighMemoryLoadThresholdBytes > 0
                && memory.MemoryLoadBytes * 100d / memory.HighMemoryLoadThresholdBytes
                    >= _options.CleanupMaxMemoryLoadPercent)
            {
                return KvCleanupThrottleReason.MemoryPressure;
            }
        }

        return _options.CleanupMaxCpuPercent > 0
            && cpuPercent >= _options.CleanupMaxCpuPercent
                ? KvCleanupThrottleReason.CpuPressure
                : KvCleanupThrottleReason.None;
    }

    private double? SampleCpuPercent()
    {
        long timestamp = Stopwatch.GetTimestamp();
        using Process process = Process.GetCurrentProcess();
        TimeSpan processorTime = process.TotalProcessorTime;
        if (_lastCpuTimestamp == 0)
        {
            _lastCpuTimestamp = timestamp;
            _lastProcessorTime = processorTime;
            return null;
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(_lastCpuTimestamp, timestamp);
        TimeSpan cpu = processorTime - _lastProcessorTime;
        _lastCpuTimestamp = timestamp;
        _lastProcessorTime = processorTime;
        if (elapsed <= TimeSpan.Zero || cpu < TimeSpan.Zero)
            return null;
        return cpu.TotalMilliseconds / elapsed.TotalMilliseconds / Environment.ProcessorCount * 100d;
    }

    private void UpdatePending(KvCleanupRoundResult result)
    {
        Interlocked.Exchange(ref _cleanupPendingFiles, result.PendingFiles);
        Interlocked.Exchange(ref _cleanupPendingBytes, result.PendingBytes);
    }

    private static KeyValuePair<string, object?> ThrottleReasonTag(KvCleanupThrottleReason reason)
        => reason switch
        {
            KvCleanupThrottleReason.ActiveQueries => SonnetDbMeter.ThrottleActiveQueries,
            KvCleanupThrottleReason.FlushPending => SonnetDbMeter.ThrottleFlushPending,
            KvCleanupThrottleReason.CpuPressure => SonnetDbMeter.ThrottleCpuPressure,
            KvCleanupThrottleReason.MemoryPressure => SonnetDbMeter.ThrottleMemoryPressure,
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };

    private static string ThrottleReasonName(KvCleanupThrottleReason reason)
        => reason switch
        {
            KvCleanupThrottleReason.ActiveQueries => "active_queries",
            KvCleanupThrottleReason.FlushPending => "flush_pending",
            KvCleanupThrottleReason.CpuPressure => "cpu_pressure",
            KvCleanupThrottleReason.MemoryPressure => "memory_pressure",
            _ => "none",
        };

    private static bool IsDue(ref long lastTimestamp, TimeSpan interval)
    {
        long now = Stopwatch.GetTimestamp();
        if (lastTimestamp != 0 && Stopwatch.GetElapsedTime(lastTimestamp, now) < interval)
            return false;
        lastTimestamp = now;
        return true;
    }
}
