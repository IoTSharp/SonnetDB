using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Hosting;

namespace SonnetDB.Modbus;

internal sealed class ModbusMasterService : BackgroundService
{
    private readonly TsdbRegistry _registry;
    private readonly ServerMetrics _metrics;
    private readonly ModbusSourceOperationCoordinator _operationCoordinator;
    private readonly ModbusRuntimeOptions _options;
    private readonly ILogger<ModbusMasterService> _logger;
    private readonly Dictionary<SourceKey, SourceWorker> _workers = [];

    public ModbusMasterService(
        TsdbRegistry registry,
        ServerMetrics metrics,
        ModbusSourceOperationCoordinator operationCoordinator,
        IOptions<ServerOptions> options,
        ILogger<ModbusMasterService> logger)
    {
        _registry = registry;
        _metrics = metrics;
        _operationCoordinator = operationCoordinator;
        _options = options.Value.Modbus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        _logger.ModbusMasterStarted();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ReconcileWorkersAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(_options.DiscoveryIntervalMilliseconds, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
        finally
        {
            SourceWorker[] workers = _workers.Values.ToArray();
            _workers.Clear();
            foreach (SourceWorker worker in workers)
                worker.Cancellation.Cancel();
            await Task.WhenAll(workers.Select(static worker => worker.Task)).ConfigureAwait(false);
            foreach (SourceWorker worker in workers)
                worker.Cancellation.Dispose();
        }
    }

    private async Task ReconcileWorkersAsync(CancellationToken stoppingToken)
    {
        var desired = new HashSet<SourceKey>();
        foreach (string databaseName in _registry.ListDatabases())
        {
            if (!_registry.TryGet(databaseName, out Tsdb database))
                continue;

            IReadOnlyList<ModbusSourceDefinition> sources;
            try
            {
                sources = database.Modbus.Catalog.ListSources();
            }
            catch (ObjectDisposedException)
            {
                continue;
            }

            foreach (ModbusSourceDefinition source in sources)
            {
                var key = new SourceKey(databaseName, source.Name);
                if (!source.Enabled)
                {
                    database.Modbus.ClearSourceRuntimeStatus(source.Name);
                    continue;
                }

                desired.Add(key);
                if (_workers.TryGetValue(key, out SourceWorker? current)
                    && ReferenceEquals(current.Database, database)
                    && current.Source == source
                    && !current.Task.IsCompleted)
                {
                    continue;
                }

                if (current is not null)
                {
                    _workers.Remove(key);
                    await StopWorkerAsync(current).ConfigureAwait(false);
                }

                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var worker = new SourceWorker(
                    database,
                    source,
                    cancellation,
                    RunSourceAsync(databaseName, database, source, cancellation.Token));
                _workers.Add(key, worker);
            }
        }

        SourceKey[] removed = _workers.Keys.Where(key => !desired.Contains(key)).ToArray();
        foreach (SourceKey key in removed)
        {
            SourceWorker worker = _workers[key];
            _workers.Remove(key);
            await StopWorkerAsync(worker).ConfigureAwait(false);
        }
    }

    private async Task RunSourceAsync(
        string databaseName,
        Tsdb database,
        ModbusSourceDefinition source,
        CancellationToken cancellationToken)
    {
        _logger.ModbusSourceWorkerStarted(databaseName, source.Name, source.Host, source.Port);
        TryReportStatus(database, source.Name, new ModbusSourceRuntimeStatus(
            RuntimeEnabled: true,
            ModbusSourceRuntimeHealth.Starting));

        DateTimeOffset? lastSuccess = null;
        DateTimeOffset? lastAttempt = null;
        DateTimeOffset? lastError = null;
        string? lastErrorCode = null;
        long consecutiveFailures = 0;
        long lastSampleUnixMilliseconds = 0;
        int reconnectDelay = _options.ReconnectBaseDelayMilliseconds;
        var tableWriterState = new ModbusTableWriterState();
        await using var client = new ModbusTcpMasterClient();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Phase A 没有 binding 启停 DDL，并把当时创建的绑定统一持久化为 false。
                // 运行时按“绑定存在即参与”兼容旧 catalog；#291 起新建绑定会写入 true。
                IReadOnlyList<ModbusTableBinding> bindings = database.Modbus.Catalog.ListBindings()
                    .Where(binding => binding.Direction == ModbusMappingDirection.SourceToTable
                                      && string.Equals(
                                          binding.TargetName,
                                          source.Name,
                                          StringComparison.Ordinal))
                    .ToArray();
                IReadOnlyList<ModbusReadBatch> batches = ModbusReadPlanner.Create(bindings);
                if (batches.Count == 0)
                {
                    TryReportStatus(database, source.Name, new ModbusSourceRuntimeStatus(
                        RuntimeEnabled: true,
                        ModbusSourceRuntimeHealth.Idle,
                        lastSuccess,
                        lastErrorCode)
                    {
                        LastAttemptAtUtc = lastAttempt,
                        LastErrorAtUtc = lastError,
                        ConsecutiveFailures = consecutiveFailures,
                    });
                    await Task.Delay(source.PollIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                long started = Stopwatch.GetTimestamp();
                try
                {
                    bool pollFailed = false;
                    int failureDelay = 0;
                    await using (ModbusSourceOperationCoordinator.Lease operationLease =
                                 await _operationCoordinator.AcquireAsync(
                                     databaseName,
                                     source.Name,
                                     cancellationToken).ConfigureAwait(false))
                    {
                        DateTimeOffset sampledAt = NextSampleTime(ref lastSampleUnixMilliseconds);
                        lastAttempt = sampledAt;
                        ModbusReadAttempt readAttempt = await ReadWithRetriesAsync(
                            client,
                            source,
                            batches,
                            cancellationToken).ConfigureAwait(false);
                        Exception? failure = readAttempt.Error;
                        int rowsWritten = 0;
                        if (failure is null)
                        {
                            try
                            {
                                rowsWritten = ModbusTableWriter.WriteSuccessfulSample(
                                    database,
                                    bindings,
                                    readAttempt.Snapshot,
                                    sampledAt,
                                    tableWriterState);
                            }
                            catch (Exception exception)
                            {
                                failure = exception;
                            }
                        }

                        if (failure is not null)
                        {
                            try
                            {
                                rowsWritten = ModbusTableWriter.WriteFailedSample(
                                    database,
                                    bindings,
                                    readAttempt.Snapshot,
                                    sampledAt,
                                    tableWriterState);
                            }
                            catch (Exception exception)
                            {
                                failure = new InvalidOperationException(
                                    "Modbus 失败采样策略无法写入本地关系表。",
                                    exception);
                                rowsWritten = 0;
                            }

                            string errorCode = GetErrorCode(failure);
                            lastError = sampledAt;
                            lastErrorCode = errorCode;
                            consecutiveFailures = consecutiveFailures == long.MaxValue
                                ? long.MaxValue
                                : consecutiveFailures + 1;
                            TryReportStatus(database, source.Name, new ModbusSourceRuntimeStatus(
                                RuntimeEnabled: true,
                                ModbusSourceRuntimeHealth.Degraded,
                                lastSuccess,
                                lastErrorCode)
                            {
                                LastAttemptAtUtc = lastAttempt,
                                LastErrorAtUtc = lastError,
                                ConsecutiveFailures = consecutiveFailures,
                            });
                            double failedElapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                            _metrics.RecordModbusPoll(succeeded: false, rowsWritten);
                            ModbusMasterDiagnostics.RecordPoll(
                                succeeded: false,
                                failedElapsed,
                                rowsWritten);
                            _logger.ModbusSourcePollFailed(
                                failure,
                                databaseName,
                                source.Name,
                                errorCode,
                                reconnectDelay);
                            client.Disconnect();
                            _metrics.RecordModbusReconnect();
                            ModbusMasterDiagnostics.RecordReconnect();
                            failureDelay = reconnectDelay;
                            reconnectDelay = DoubleWithLimit(
                                reconnectDelay,
                                _options.MaxReconnectDelayMilliseconds);
                            pollFailed = true;
                        }

                        if (!pollFailed)
                        {
                            lastSuccess = sampledAt;
                            consecutiveFailures = 0;
                            reconnectDelay = _options.ReconnectBaseDelayMilliseconds;
                            TryReportStatus(database, source.Name, new ModbusSourceRuntimeStatus(
                                RuntimeEnabled: true,
                                ModbusSourceRuntimeHealth.Healthy,
                                lastSuccess,
                                lastErrorCode)
                            {
                                LastAttemptAtUtc = lastAttempt,
                                LastErrorAtUtc = lastError,
                                ConsecutiveFailures = consecutiveFailures,
                            });
                            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                            _metrics.RecordModbusPoll(succeeded: true, rowsWritten);
                            ModbusMasterDiagnostics.RecordPoll(succeeded: true, elapsed, rowsWritten);
                        }
                    }
                    if (pollFailed)
                    {
                        await Task.Delay(failureDelay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    await Task.Delay(source.PollIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    string errorCode = GetErrorCode(exception);
                    DateTimeOffset failedAt = NextSampleTime(ref lastSampleUnixMilliseconds);
                    lastAttempt = failedAt;
                    lastError = failedAt;
                    lastErrorCode = errorCode;
                    consecutiveFailures = consecutiveFailures == long.MaxValue
                        ? long.MaxValue
                        : consecutiveFailures + 1;
                    double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    TryReportStatus(database, source.Name, new ModbusSourceRuntimeStatus(
                        RuntimeEnabled: true,
                        ModbusSourceRuntimeHealth.Degraded,
                        lastSuccess,
                        lastErrorCode)
                    {
                        LastAttemptAtUtc = lastAttempt,
                        LastErrorAtUtc = lastError,
                        ConsecutiveFailures = consecutiveFailures,
                    });
                    _metrics.RecordModbusPoll(succeeded: false, rowsWritten: 0);
                    ModbusMasterDiagnostics.RecordPoll(succeeded: false, elapsed, rowsWritten: 0);
                    _logger.ModbusSourcePollFailed(
                        exception,
                        databaseName,
                        source.Name,
                        errorCode,
                        reconnectDelay);
                    client.Disconnect();
                    _metrics.RecordModbusReconnect();
                    ModbusMasterDiagnostics.RecordReconnect();
                    await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);
                    reconnectDelay = DoubleWithLimit(
                        reconnectDelay,
                        _options.MaxReconnectDelayMilliseconds);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Worker reconciliation or host shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Database drop or shutdown raced with registry reconciliation.
        }
        catch (Exception exception)
        {
            _logger.ModbusSourcePollFailed(
                exception,
                databaseName,
                source.Name,
                GetErrorCode(exception),
                reconnectDelay);
        }
        finally
        {
            TryReportStatus(database, source.Name, new ModbusSourceRuntimeStatus(
                RuntimeEnabled: false,
                ModbusSourceRuntimeHealth.Disabled,
                lastSuccess,
                lastErrorCode)
            {
                LastAttemptAtUtc = lastAttempt,
                LastErrorAtUtc = lastError,
                ConsecutiveFailures = consecutiveFailures,
            });
        }
    }

    private async Task<ModbusReadAttempt> ReadWithRetriesAsync(
        ModbusTcpMasterClient client,
        ModbusSourceDefinition source,
        IReadOnlyList<ModbusReadBatch> batches,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            var snapshot = new ModbusReadSnapshot();
            try
            {
                foreach (ModbusReadBatch batch in batches)
                {
                    ushort[] values = await client.ReadAsync(source, batch, cancellationToken)
                        .ConfigureAwait(false);
                    snapshot.Add(batch, values);
                    _metrics.RecordModbusReadBatch();
                    ModbusMasterDiagnostics.RecordRead(batch.Area);
                }

                return new ModbusReadAttempt(snapshot, Error: null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRetryable(exception) && attempt < source.RetryCount)
            {
                client.Disconnect();
                _metrics.RecordModbusReconnect();
                ModbusMasterDiagnostics.RecordReconnect();
                int delay = Backoff(
                    _options.RetryBaseDelayMilliseconds,
                    _options.MaxRetryDelayMilliseconds,
                    attempt);
                _logger.ModbusSourceRetry(source.Name, attempt + 1, source.RetryCount, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return new ModbusReadAttempt(snapshot, exception);
            }
        }
    }

    private static bool IsRetryable(Exception exception)
        => exception is IOException or SocketException or TimeoutException;

    private static string GetErrorCode(Exception exception) => exception switch
    {
        TimeoutException => ModbusErrorCodes.Timeout,
        ModbusProtocolException protocolException => protocolException.ErrorCode,
        InvalidDataException or OverflowException => ModbusErrorCodes.Decode,
        SocketException => ModbusErrorCodes.Connection,
        IOException => ModbusErrorCodes.Connection,
        ObjectDisposedException => ModbusErrorCodes.DatabaseClosed,
        InvalidOperationException => ModbusErrorCodes.Ingest,
        _ => ModbusErrorCodes.Runtime,
    };

    private static DateTimeOffset NextSampleTime(ref long previousUnixMilliseconds)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long next = Math.Max(now, checked(previousUnixMilliseconds + 1));
        previousUnixMilliseconds = next;
        return DateTimeOffset.FromUnixTimeMilliseconds(next);
    }

    private static int Backoff(int initial, int maximum, int exponent)
    {
        int delay = initial;
        for (int i = 0; i < exponent && delay < maximum; i++)
            delay = DoubleWithLimit(delay, maximum);
        return delay;
    }

    private static int DoubleWithLimit(int value, int maximum)
        => value >= maximum / 2 ? maximum : value * 2;

    private static void TryReportStatus(
        Tsdb database,
        string sourceName,
        ModbusSourceRuntimeStatus status)
    {
        try
        {
            database.Modbus.ReportSourceRuntimeStatus(sourceName, status);
        }
        catch (ObjectDisposedException)
        {
            // Registry reconciliation will remove the worker.
        }
    }

    private static async Task StopWorkerAsync(SourceWorker worker)
    {
        worker.Cancellation.Cancel();
        await worker.Task.ConfigureAwait(false);
        worker.Cancellation.Dispose();
    }

    private readonly record struct SourceKey(string DatabaseName, string SourceName);

    private sealed record SourceWorker(
        Tsdb Database,
        ModbusSourceDefinition Source,
        CancellationTokenSource Cancellation,
        Task Task);

    private sealed record ModbusReadAttempt(
        ModbusReadSnapshot Snapshot,
        Exception? Error);
}
