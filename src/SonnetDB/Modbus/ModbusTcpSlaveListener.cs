using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SonnetDB.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Hosting;

namespace SonnetDB.Modbus;

internal sealed class ModbusTcpSlaveListener
{
    private readonly string _databaseName;
    private readonly Tsdb _database;
    private readonly ModbusEndpointDefinition _endpoint;
    private readonly ModbusClientAllowlist _allowlist;
    private readonly ServerMetrics _metrics;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _connectionSlots;
    private readonly ConcurrentDictionary<long, Task> _connections = [];
    private long _nextConnectionId;

    internal ModbusTcpSlaveListener(
        string databaseName,
        Tsdb database,
        ModbusEndpointDefinition endpoint,
        ServerMetrics metrics,
        ILogger logger)
    {
        _databaseName = databaseName;
        _database = database;
        _endpoint = endpoint;
        _allowlist = new ModbusClientAllowlist(endpoint.AllowedClientNetworks);
        _metrics = metrics;
        _logger = logger;
        _connectionSlots = new SemaphoreSlim(endpoint.MaxConnections, endpoint.MaxConnections);
    }

    internal async Task RunAsync(Action<IPEndPoint> onListening, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onListening);
        var listener = new TcpListener(IPAddress.Parse(_endpoint.BindAddress), _endpoint.Port);
        try
        {
            listener.Start(Math.Max(1, _endpoint.MaxConnections));
            onListening((IPEndPoint)listener.LocalEndpoint);
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                Dispatch(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown or endpoint reconciliation.
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            // TcpListener cancellation may surface as a socket error on some platforms.
        }
        finally
        {
            listener.Stop();
            Task[] connections = _connections.Values.ToArray();
            if (connections.Length > 0)
                await Task.WhenAll(connections).ConfigureAwait(false);
            _connectionSlots.Dispose();
        }
    }

    private void Dispatch(TcpClient client, CancellationToken cancellationToken)
    {
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        if (remote is null || !_allowlist.IsAllowed(remote.Address))
        {
            _metrics.RecordModbusSlaveConnectionRejected();
            ModbusSlaveDiagnostics.RecordConnectionRejected(allowlist: true);
            _logger.ModbusEndpointClientRejected(
                _databaseName,
                _endpoint.Name,
                remote?.ToString() ?? "unknown",
                "allowlist");
            client.Dispose();
            return;
        }

        if (!_connectionSlots.Wait(0))
        {
            _metrics.RecordModbusSlaveConnectionRejected();
            ModbusSlaveDiagnostics.RecordConnectionRejected(allowlist: false);
            _logger.ModbusEndpointClientRejected(
                _databaseName,
                _endpoint.Name,
                remote.ToString(),
                "max_connections");
            client.Dispose();
            return;
        }

        _metrics.RecordModbusSlaveConnectionOpened();
        ModbusSlaveDiagnostics.RecordConnectionOpened();
        long connectionId = Interlocked.Increment(ref _nextConnectionId);
        Task task = HandleConnectionAsync(client, cancellationToken);
        _connections[connectionId] = task;
        _ = task.ContinueWith(
            static (completedTask, state) =>
            {
                var completion = (ConnectionCompletion)state!;
                _ = completion.Connections.TryRemove(completion.ConnectionId, out _);
            },
            new ConnectionCompletion(_connections, connectionId),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();
                while (!cancellationToken.IsCancellationRequested)
                {
                    var header = new byte[ModbusTcpSlaveProtocol.MbapHeaderLength];
                    await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                    ushort protocolId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
                    ushort length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                    if (protocolId != 0 || length is < 2 or > ModbusTcpSlaveProtocol.MaximumMbapLength)
                        return;

                    var pdu = new byte[length - 1];
                    await stream.ReadExactlyAsync(pdu, cancellationToken).ConfigureAwait(false);
                    ushort transactionId = BinaryPrimitives.ReadUInt16BigEndian(header);
                    ModbusSlaveResponse response = ModbusTcpSlaveProtocol.ProcessRequest(
                        _database,
                        _endpoint,
                        transactionId,
                        header[6],
                        pdu);
                    if (response.IsReadRequest && response.Area is { } area)
                    {
                        _metrics.RecordModbusSlaveRead(response.Succeeded);
                        ModbusSlaveDiagnostics.RecordRead(area, response.Succeeded);
                    }

                    await stream.WriteAsync(response.Adu, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Endpoint reconciliation or host shutdown.
            }
            catch (EndOfStreamException)
            {
                // Peer closed the persistent connection.
            }
            catch (IOException)
            {
                // Peer reset or closed the connection.
            }
            catch (SocketException)
            {
                // Peer reset or closed the connection.
            }
            catch (ObjectDisposedException)
            {
                // Database or socket shutdown raced with request processing.
            }
            catch (Exception exception)
            {
                _logger.ModbusEndpointClientFailed(
                    exception,
                    _databaseName,
                    _endpoint.Name,
                    client.Client.RemoteEndPoint?.ToString() ?? "unknown");
            }
            finally
            {
                _connectionSlots.Release();
                _metrics.RecordModbusSlaveConnectionClosed();
                ModbusSlaveDiagnostics.RecordConnectionClosed();
            }
        }
    }

    private sealed record ConnectionCompletion(
        ConcurrentDictionary<long, Task> Connections,
        long ConnectionId);
}
