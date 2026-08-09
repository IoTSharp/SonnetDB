using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SonnetDB.Modbus;

namespace SonnetDB.Tests.Modbus;

internal sealed class ModbusTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<int, ushort> _values = [];
    private readonly ConcurrentBag<Task> _connections = [];
    private Task? _acceptTask;
    private int _connectionCount;
    private int _requestCount;
    private int _dropRequestsRemaining;

    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    internal int ConnectionCount => Volatile.Read(ref _connectionCount);

    internal int RequestCount => Volatile.Read(ref _requestCount);

    internal int DropRequestsRemaining
    {
        get => Volatile.Read(ref _dropRequestsRemaining);
        set => Volatile.Write(ref _dropRequestsRemaining, value);
    }

    internal TimeSpan ResponseDelay { get; set; }

    internal ConcurrentQueue<ModbusTestRequest> Requests { get; } = new();

    internal void SetValue(ModbusRegisterArea area, ushort address, ushort value)
        => _values[GetKey(area, address)] = value;

    internal void Start()
    {
        _listener.Start();
        _acceptTask = AcceptAsync(_cancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _listener.Stop();
        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }

        try
        {
            await Task.WhenAll(_connections.ToArray()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Test server shutdown cancels outstanding delayed responses.
        }
        _cancellation.Dispose();
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _connectionCount);
            Task connection = HandleConnectionAsync(client, cancellationToken);
            _connections.Add(connection);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            NetworkStream stream = client.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] header = new byte[7];
                try
                {
                    await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    return;
                }
                catch (IOException)
                {
                    return;
                }

                ushort length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                if (length < 2)
                    return;
                byte[] pdu = new byte[length - 1];
                await stream.ReadExactlyAsync(pdu, cancellationToken).ConfigureAwait(false);
                if (pdu.Length != 5)
                    return;

                var request = new ModbusTestRequest(
                    pdu[0],
                    BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1)),
                    BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3)));
                Requests.Enqueue(request);
                Interlocked.Increment(ref _requestCount);
                if (TryConsumeDropRequest())
                    return;

                if (ResponseDelay > TimeSpan.Zero)
                    await Task.Delay(ResponseDelay, cancellationToken).ConfigureAwait(false);

                byte[] response = BuildResponse(header, request);
                await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private byte[] BuildResponse(ReadOnlySpan<byte> requestHeader, ModbusTestRequest request)
    {
        ModbusRegisterArea area = request.FunctionCode switch
        {
            0x01 => ModbusRegisterArea.Coil,
            0x02 => ModbusRegisterArea.DiscreteInput,
            0x03 => ModbusRegisterArea.HoldingRegister,
            0x04 => ModbusRegisterArea.InputRegister,
            _ => throw new InvalidOperationException($"测试 Modbus server 不支持 function 0x{request.FunctionCode:X2}。"),
        };
        int byteCount = area is ModbusRegisterArea.Coil or ModbusRegisterArea.DiscreteInput
            ? (request.Count + 7) / 8
            : request.Count * sizeof(ushort);
        byte[] response = new byte[9 + byteCount];
        requestHeader[..4].CopyTo(response);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), checked((ushort)(3 + byteCount)));
        response[6] = requestHeader[6];
        response[7] = request.FunctionCode;
        response[8] = checked((byte)byteCount);

        if (area is ModbusRegisterArea.Coil or ModbusRegisterArea.DiscreteInput)
        {
            for (int i = 0; i < request.Count; i++)
            {
                if (GetValue(area, checked((ushort)(request.StartAddress + i))) != 0)
                    response[9 + (i / 8)] |= checked((byte)(1 << (i % 8)));
            }
        }
        else
        {
            for (int i = 0; i < request.Count; i++)
            {
                ushort value = GetValue(area, checked((ushort)(request.StartAddress + i)));
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(9 + (i * sizeof(ushort))), value);
            }
        }

        return response;
    }

    private ushort GetValue(ModbusRegisterArea area, ushort address)
        => _values.GetValueOrDefault(GetKey(area, address));

    private bool TryConsumeDropRequest()
    {
        while (true)
        {
            int remaining = Volatile.Read(ref _dropRequestsRemaining);
            if (remaining <= 0)
                return false;
            if (Interlocked.CompareExchange(ref _dropRequestsRemaining, remaining - 1, remaining) == remaining)
                return true;
        }
    }

    private static int GetKey(ModbusRegisterArea area, ushort address)
        => ((int)area << 16) | address;
}

internal readonly record struct ModbusTestRequest(
    byte FunctionCode,
    ushort StartAddress,
    ushort Count);
