using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Hosting;

namespace SonnetDB.Modbus;

internal sealed class ModbusSlaveService : BackgroundService
{
    private readonly TsdbRegistry _registry;
    private readonly ServerMetrics _metrics;
    private readonly ModbusRuntimeOptions _options;
    private readonly ILogger<ModbusSlaveService> _logger;
    private readonly Dictionary<EndpointKey, EndpointWorker> _workers = [];

    public ModbusSlaveService(
        TsdbRegistry registry,
        ServerMetrics metrics,
        IOptions<ServerOptions> options,
        ILogger<ModbusSlaveService> logger)
    {
        _registry = registry;
        _metrics = metrics;
        _options = options.Value.Modbus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        _logger.ModbusSlaveStarted();
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
            EndpointWorker[] workers = _workers.Values.ToArray();
            _workers.Clear();
            foreach (EndpointWorker worker in workers)
                worker.Cancellation.Cancel();
            await Task.WhenAll(workers.Select(static worker => worker.Task)).ConfigureAwait(false);
            foreach (EndpointWorker worker in workers)
                worker.Cancellation.Dispose();
        }
    }

    private async Task ReconcileWorkersAsync(CancellationToken stoppingToken)
    {
        var desired = new HashSet<EndpointKey>();
        foreach (string databaseName in _registry.ListDatabases())
        {
            if (!_registry.TryGet(databaseName, out Tsdb database))
                continue;

            IReadOnlyList<ModbusEndpointDefinition> endpoints;
            try
            {
                endpoints = database.Modbus.Catalog.ListEndpoints();
            }
            catch (ObjectDisposedException)
            {
                continue;
            }

            foreach (ModbusEndpointDefinition endpoint in endpoints)
            {
                var key = new EndpointKey(databaseName, endpoint.Name);
                if (!endpoint.Enabled)
                {
                    database.Modbus.ClearEndpointRuntimeStatus(endpoint.Name);
                    continue;
                }

                desired.Add(key);
                if (_workers.TryGetValue(key, out EndpointWorker? current)
                    && ReferenceEquals(current.Database, database)
                    && current.Endpoint == endpoint
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
                var worker = new EndpointWorker(
                    database,
                    endpoint,
                    cancellation,
                    RunEndpointAsync(databaseName, database, endpoint, cancellation.Token));
                _workers.Add(key, worker);
            }
        }

        EndpointKey[] removed = _workers.Keys.Where(key => !desired.Contains(key)).ToArray();
        foreach (EndpointKey key in removed)
        {
            EndpointWorker worker = _workers[key];
            _workers.Remove(key);
            await StopWorkerAsync(worker).ConfigureAwait(false);
        }
    }

    private async Task RunEndpointAsync(
        string databaseName,
        Tsdb database,
        ModbusEndpointDefinition endpoint,
        CancellationToken cancellationToken)
    {
        string? lastErrorCode = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var listening = false;
                TryReportStatus(database, endpoint.Name, new ModbusEndpointRuntimeStatus(
                    RuntimeEnabled: true,
                    ModbusEndpointRuntimeHealth.Starting,
                    lastErrorCode));
                try
                {
                    var listener = new ModbusTcpSlaveListener(
                        databaseName,
                        database,
                        endpoint,
                        _metrics,
                        _logger);
                    await listener.RunAsync(
                        localEndpoint =>
                        {
                            listening = true;
                            TryReportStatus(database, endpoint.Name, new ModbusEndpointRuntimeStatus(
                                RuntimeEnabled: true,
                                ModbusEndpointRuntimeHealth.Listening,
                                lastErrorCode));
                            _logger.ModbusEndpointListening(
                                databaseName,
                                endpoint.Name,
                                localEndpoint.ToString(),
                                endpoint.UnitId,
                                endpoint.MaxConnections);
                        },
                        cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (exception is IOException
                                                  or SocketException
                                                  or ObjectDisposedException)
                {
                    lastErrorCode = listening
                        ? ModbusErrorCodes.EndpointListener
                        : ModbusErrorCodes.EndpointBind;
                    TryReportStatus(database, endpoint.Name, new ModbusEndpointRuntimeStatus(
                        RuntimeEnabled: true,
                        ModbusEndpointRuntimeHealth.Degraded,
                        lastErrorCode));
                    _logger.ModbusEndpointListenerFailed(
                        exception,
                        databaseName,
                        endpoint.Name,
                        FormatEndpoint(endpoint),
                        lastErrorCode);
                    await Task.Delay(_options.DiscoveryIntervalMilliseconds, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Endpoint reconciliation or host shutdown.
        }
        finally
        {
            TryReportStatus(database, endpoint.Name, new ModbusEndpointRuntimeStatus(
                RuntimeEnabled: false,
                ModbusEndpointRuntimeHealth.Disabled,
                lastErrorCode));
        }
    }

    private static string FormatEndpoint(ModbusEndpointDefinition endpoint)
        => IPAddress.Parse(endpoint.BindAddress).AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{endpoint.BindAddress}]:{endpoint.Port}"
            : $"{endpoint.BindAddress}:{endpoint.Port}";

    private static void TryReportStatus(
        Tsdb database,
        string endpointName,
        ModbusEndpointRuntimeStatus status)
    {
        try
        {
            database.Modbus.ReportEndpointRuntimeStatus(endpointName, status);
        }
        catch (ObjectDisposedException)
        {
            // Registry reconciliation will remove the worker.
        }
    }

    private static async Task StopWorkerAsync(EndpointWorker worker)
    {
        worker.Cancellation.Cancel();
        await worker.Task.ConfigureAwait(false);
        worker.Cancellation.Dispose();
    }

    private readonly record struct EndpointKey(string DatabaseName, string EndpointName);

    private sealed record EndpointWorker(
        Tsdb Database,
        ModbusEndpointDefinition Endpoint,
        CancellationTokenSource Cancellation,
        Task Task);
}
