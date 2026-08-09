using System.Buffers.Binary;
using System.Net.Sockets;

namespace SonnetDB.Modbus;

internal sealed class ModbusTcpMasterClient : IAsyncDisposable
{
    private const int MbapHeaderLength = 7;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private ushort _transactionId;

    internal bool IsConnected => _stream is not null;

    internal async Task<ushort[]> ReadAsync(
        ModbusSourceDefinition source,
        ModbusReadBatch batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(source.TimeoutMilliseconds);

        try
        {
            await EnsureConnectedAsync(source, timeout.Token).ConfigureAwait(false);
            NetworkStream stream = _stream!;
            ushort transactionId = unchecked(++_transactionId);
            byte[] request = new byte[12];
            BinaryPrimitives.WriteUInt16BigEndian(request, transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), 6);
            request[6] = source.UnitId;
            request[7] = batch.FunctionCode;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), batch.StartAddress);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10), batch.Count);

            await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);

            byte[] header = new byte[MbapHeaderLength];
            await stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
            ValidateHeader(header, transactionId, source.UnitId, out int pduLength);

            byte[] pdu = new byte[pduLength];
            await stream.ReadExactlyAsync(pdu, timeout.Token).ConfigureAwait(false);
            return DecodeReadResponse(pdu, batch);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Disconnect();
            throw new TimeoutException(
                $"Modbus TCP 请求在 {source.TimeoutMilliseconds}ms 内未完成。");
        }
        catch
        {
            Disconnect();
            throw;
        }
    }

    internal async Task WriteAsync(
        ModbusSourceDefinition source,
        ModbusWritePayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(payload.Values);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(source.TimeoutMilliseconds);

        try
        {
            await EnsureConnectedAsync(source, timeout.Token).ConfigureAwait(false);
            NetworkStream stream = _stream!;
            ushort transactionId = unchecked(++_transactionId);
            byte[] request = BuildWriteRequest(source, payload, transactionId);
            await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);

            byte[] header = new byte[MbapHeaderLength];
            await stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
            ValidateHeader(header, transactionId, source.UnitId, out int pduLength);
            byte[] pdu = new byte[pduLength];
            await stream.ReadExactlyAsync(pdu, timeout.Token).ConfigureAwait(false);
            ValidateWriteResponse(pdu, payload);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Disconnect();
            throw new TimeoutException(
                $"Modbus TCP 请求在 {source.TimeoutMilliseconds}ms 内未完成。");
        }
        catch
        {
            Disconnect();
            throw;
        }
    }

    internal void Disconnect()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    public ValueTask DisposeAsync()
    {
        Disconnect();
        return ValueTask.CompletedTask;
    }

    private async Task EnsureConnectedAsync(
        ModbusSourceDefinition source,
        CancellationToken cancellationToken)
    {
        if (_stream is not null)
            return;

        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(source.Host, source.Port, cancellationToken).ConfigureAwait(false);
            _client = client;
            _stream = client.GetStream();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static void ValidateHeader(
        ReadOnlySpan<byte> header,
        ushort expectedTransactionId,
        byte expectedUnitId,
        out int pduLength)
    {
        ushort transactionId = BinaryPrimitives.ReadUInt16BigEndian(header);
        ushort protocolId = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        if (transactionId != expectedTransactionId)
            throw new ModbusProtocolException(ModbusErrorCodes.TransactionMismatch, "Modbus TCP transaction id 不匹配。");
        if (protocolId != 0)
            throw new ModbusProtocolException(ModbusErrorCodes.InvalidProtocol, "Modbus TCP protocol id 必须为 0。");
        if (header[6] != expectedUnitId)
            throw new ModbusProtocolException(ModbusErrorCodes.UnitMismatch, "Modbus TCP unit id 不匹配。");
        if (length is < 3 or > 254)
            throw new ModbusProtocolException(ModbusErrorCodes.InvalidLength, $"Modbus TCP 响应长度 {length} 无效。");

        pduLength = length - 1;
    }

    private static ushort[] DecodeReadResponse(
        ReadOnlySpan<byte> pdu,
        ModbusReadBatch batch)
    {
        byte expectedFunction = batch.FunctionCode;
        byte actualFunction = pdu[0];
        if (actualFunction == (expectedFunction | 0x80))
        {
            if (pdu.Length != 2)
                throw new ModbusProtocolException(ModbusErrorCodes.InvalidException, "Modbus TCP 异常响应长度无效。");
            throw new ModbusProtocolException(
                ModbusErrorCodes.DeviceException(pdu[1]),
                $"Modbus 设备返回异常码 0x{pdu[1]:X2}。");
        }
        if (actualFunction != expectedFunction)
            throw new ModbusProtocolException(ModbusErrorCodes.FunctionMismatch, "Modbus TCP function code 不匹配。");
        if (pdu.Length < 2)
            throw new ModbusProtocolException(ModbusErrorCodes.InvalidPayload, "Modbus TCP 读取响应缺少 byte count。");

        int expectedByteCount = batch.Area is ModbusRegisterArea.Coil or ModbusRegisterArea.DiscreteInput
            ? (batch.Count + 7) / 8
            : batch.Count * sizeof(ushort);
        int byteCount = pdu[1];
        if (byteCount != expectedByteCount || pdu.Length != byteCount + 2)
        {
            throw new ModbusProtocolException(
                ModbusErrorCodes.InvalidByteCount,
                $"Modbus TCP 读取响应 byte count 为 {byteCount}，预期 {expectedByteCount}。");
        }

        var values = new ushort[batch.Count];
        ReadOnlySpan<byte> payload = pdu[2..];
        if (batch.Area is ModbusRegisterArea.Coil or ModbusRegisterArea.DiscreteInput)
        {
            for (int i = 0; i < values.Length; i++)
                values[i] = (ushort)((payload[i / 8] >> (i % 8)) & 1);
        }
        else
        {
            for (int i = 0; i < values.Length; i++)
                values[i] = BinaryPrimitives.ReadUInt16BigEndian(payload[(i * sizeof(ushort))..]);
        }

        return values;
    }

    private static byte[] BuildWriteRequest(
        ModbusSourceDefinition source,
        ModbusWritePayload payload,
        ushort transactionId)
    {
        if (payload.Values.Count == 0)
            throw new ArgumentException("Modbus 写入至少需要一个地址值。", nameof(payload));

        byte functionCode = payload.FunctionCode;
        if (functionCode == 0x10 && payload.Values.Count > 123)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Values.Count,
                "单次 Holding Register 写入最多支持 123 个寄存器；受限控制写不会拆成部分请求。");
        }

        int pduLength = functionCode == 0x10
            ? checked(6 + (payload.Values.Count * sizeof(ushort)))
            : 5;
        byte[] request = new byte[MbapHeaderLength + pduLength];
        BinaryPrimitives.WriteUInt16BigEndian(request, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), checked((ushort)(pduLength + 1)));
        request[6] = source.UnitId;
        request[7] = functionCode;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), payload.StartAddress);

        if (functionCode == 0x05)
        {
            ushort coil = payload.Values[0] switch
            {
                0 => 0x0000,
                1 => 0xFF00,
                _ => throw new ArgumentException("Coil 写入值必须为 0 或 1。", nameof(payload)),
            };
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10), coil);
        }
        else if (functionCode == 0x06)
        {
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10), payload.Values[0]);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(
                request.AsSpan(10),
                checked((ushort)payload.Values.Count));
            request[12] = checked((byte)(payload.Values.Count * sizeof(ushort)));
            for (int index = 0; index < payload.Values.Count; index++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(
                    request.AsSpan(13 + (index * sizeof(ushort))),
                    payload.Values[index]);
            }
        }

        return request;
    }

    private static void ValidateWriteResponse(
        ReadOnlySpan<byte> pdu,
        ModbusWritePayload payload)
    {
        byte expectedFunction = payload.FunctionCode;
        if (pdu.Length == 0)
            throw new ModbusProtocolException(ModbusErrorCodes.InvalidPayload, "Modbus TCP 写响应为空。");
        byte actualFunction = pdu[0];
        if (actualFunction == (expectedFunction | 0x80))
        {
            if (pdu.Length != 2)
                throw new ModbusProtocolException(ModbusErrorCodes.InvalidException, "Modbus TCP 异常响应长度无效。");
            throw new ModbusProtocolException(
                ModbusErrorCodes.DeviceException(pdu[1]),
                $"Modbus 设备返回异常码 0x{pdu[1]:X2}。");
        }
        if (actualFunction != expectedFunction)
            throw new ModbusProtocolException(ModbusErrorCodes.FunctionMismatch, "Modbus TCP function code 不匹配。");
        if (pdu.Length != 5)
            throw new ModbusProtocolException(ModbusErrorCodes.InvalidPayload, "Modbus TCP 写响应长度无效。");

        ushort address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        if (address != payload.StartAddress)
            throw new ModbusProtocolException(ModbusErrorCodes.AddressMismatch, "Modbus TCP 写响应地址不匹配。");

        ushort echoed = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        ushort expected = expectedFunction switch
        {
            0x05 => payload.Values[0] == 0 ? (ushort)0x0000 : (ushort)0xFF00,
            0x06 => payload.Values[0],
            0x10 => checked((ushort)payload.Values.Count),
            _ => throw new ArgumentOutOfRangeException(nameof(payload), expectedFunction, "未知的写功能码。"),
        };
        if (echoed != expected)
            throw new ModbusProtocolException(ModbusErrorCodes.WriteEchoMismatch, "Modbus TCP 写响应回显值不匹配。");
    }
}

internal sealed record ModbusWritePayload(
    ModbusRegisterArea Area,
    ushort StartAddress,
    IReadOnlyList<ushort> Values)
{
    internal byte FunctionCode => Area switch
    {
        ModbusRegisterArea.Coil when Values.Count == 1 => 0x05,
        ModbusRegisterArea.HoldingRegister when Values.Count == 1 => 0x06,
        ModbusRegisterArea.HoldingRegister => 0x10,
        _ => throw new ArgumentOutOfRangeException(nameof(Area), Area, "该 Modbus 地址空间不支持远端写入。"),
    };
}

internal sealed class ModbusProtocolException : IOException
{
    internal ModbusProtocolException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    internal string ErrorCode { get; }
}
