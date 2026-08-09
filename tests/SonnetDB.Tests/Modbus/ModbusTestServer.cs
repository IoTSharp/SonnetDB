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
    private int _dropWriteRequestsRemaining;

    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    internal int ConnectionCount => Volatile.Read(ref _connectionCount);

    internal int RequestCount => Volatile.Read(ref _requestCount);

    internal int DropRequestsRemaining
    {
        get => Volatile.Read(ref _dropRequestsRemaining);
        set => Volatile.Write(ref _dropRequestsRemaining, value);
    }

    internal int DropWriteRequestsRemaining
    {
        get => Volatile.Read(ref _dropWriteRequestsRemaining);
        set => Volatile.Write(ref _dropWriteRequestsRemaining, value);
    }

    internal byte? WriteExceptionCode { get; set; }

    internal ushort? WriteResponseAddressOverride { get; set; }

    internal TimeSpan ResponseDelay { get; set; }

    internal ConcurrentQueue<ModbusTestRequest> Requests { get; } = new();

    internal void SetValue(ModbusRegisterArea area, ushort address, ushort value)
        => _values[GetKey(area, address)] = value;

    internal ushort GetValue(ModbusRegisterArea area, ushort address)
        => _values.GetValueOrDefault(GetKey(area, address));

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
                ModbusTestRequest request = ParseRequest(pdu);
                Requests.Enqueue(request);
                Interlocked.Increment(ref _requestCount);
                if (TryConsumeDropRequest()
                    || (request.FunctionCode is 0x05 or 0x06 or 0x10 && TryConsumeDropWriteRequest()))
                    return;

                if (ResponseDelay > TimeSpan.Zero)
                    await Task.Delay(ResponseDelay, cancellationToken).ConfigureAwait(false);

                byte[] response = request.FunctionCode switch
                {
                    0x01 or 0x02 or 0x03 or 0x04 => BuildReadResponse(header, request),
                    0x05 or 0x06 or 0x10 => BuildWriteResponse(header, request),
                    _ => throw new InvalidOperationException(
                        $"测试 Modbus server 不支持 function 0x{request.FunctionCode:X2}。"),
                };
                await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static ModbusTestRequest ParseRequest(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 5)
            throw new InvalidDataException("测试 Modbus server 收到的 PDU 太短。");
        byte functionCode = pdu[0];
        ushort startAddress = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        if (functionCode == 0x10)
        {
            ushort count = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
            int byteCount = pdu[5];
            if (count == 0 || byteCount != count * sizeof(ushort) || pdu.Length != 6 + byteCount)
                throw new InvalidDataException("测试 Modbus server 收到的多寄存器写 PDU 无效。");
            var values = new ushort[count];
            for (int index = 0; index < values.Length; index++)
                values[index] = BinaryPrimitives.ReadUInt16BigEndian(pdu[(6 + (index * sizeof(ushort)))..]);
            return new ModbusTestRequest(functionCode, startAddress, count, values);
        }

        if (pdu.Length != 5)
            throw new InvalidDataException("测试 Modbus server 收到的 PDU 长度无效。");
        ushort thirdField = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        return functionCode switch
        {
            0x05 => new ModbusTestRequest(
                functionCode,
                startAddress,
                1,
                [thirdField == 0xFF00 ? (ushort)1 : thirdField == 0 ? (ushort)0 : throw new InvalidDataException("线圈写值无效。")]),
            0x06 => new ModbusTestRequest(functionCode, startAddress, 1, [thirdField]),
            _ => new ModbusTestRequest(functionCode, startAddress, thirdField),
        };
    }

    private byte[] BuildReadResponse(ReadOnlySpan<byte> requestHeader, ModbusTestRequest request)
    {
        ModbusRegisterArea area = request.FunctionCode switch
        {
            0x01 => ModbusRegisterArea.Coil,
            0x02 => ModbusRegisterArea.DiscreteInput,
            0x03 => ModbusRegisterArea.HoldingRegister,
            0x04 => ModbusRegisterArea.InputRegister,
            _ => throw new InvalidOperationException($"测试 Modbus server 不支持读 function 0x{request.FunctionCode:X2}。"),
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

    private byte[] BuildWriteResponse(ReadOnlySpan<byte> requestHeader, ModbusTestRequest request)
    {
        if (WriteExceptionCode is { } exceptionCode)
        {
            byte[] exception = new byte[9];
            requestHeader[..4].CopyTo(exception);
            BinaryPrimitives.WriteUInt16BigEndian(exception.AsSpan(4), 3);
            exception[6] = requestHeader[6];
            exception[7] = (byte)(request.FunctionCode | 0x80);
            exception[8] = exceptionCode;
            return exception;
        }

        IReadOnlyList<ushort> values = request.Values
            ?? throw new InvalidDataException("测试 Modbus 写请求缺少值。");
        ModbusRegisterArea area = request.FunctionCode == 0x05
            ? ModbusRegisterArea.Coil
            : ModbusRegisterArea.HoldingRegister;
        for (int index = 0; index < values.Count; index++)
            SetValue(area, checked((ushort)(request.StartAddress + index)), values[index]);

        byte[] response = new byte[12];
        requestHeader[..4].CopyTo(response);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), 6);
        response[6] = requestHeader[6];
        response[7] = request.FunctionCode;
        BinaryPrimitives.WriteUInt16BigEndian(
            response.AsSpan(8),
            WriteResponseAddressOverride ?? request.StartAddress);
        ushort echoed = request.FunctionCode switch
        {
            0x05 => values[0] == 0 ? (ushort)0 : (ushort)0xFF00,
            0x06 => values[0],
            0x10 => request.Count,
            _ => throw new InvalidOperationException("未知写功能码。"),
        };
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(10), echoed);
        return response;
    }

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

    private bool TryConsumeDropWriteRequest()
    {
        while (true)
        {
            int remaining = Volatile.Read(ref _dropWriteRequestsRemaining);
            if (remaining <= 0)
                return false;
            if (Interlocked.CompareExchange(ref _dropWriteRequestsRemaining, remaining - 1, remaining) == remaining)
                return true;
        }
    }

    private static int GetKey(ModbusRegisterArea area, ushort address)
        => ((int)area << 16) | address;
}

internal readonly record struct ModbusTestRequest(
    byte FunctionCode,
    ushort StartAddress,
    ushort Count,
    IReadOnlyList<ushort>? Values = null);
