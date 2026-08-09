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
            throw new ModbusProtocolException("transaction_mismatch", "Modbus TCP transaction id 不匹配。");
        if (protocolId != 0)
            throw new ModbusProtocolException("invalid_protocol", "Modbus TCP protocol id 必须为 0。");
        if (header[6] != expectedUnitId)
            throw new ModbusProtocolException("unit_mismatch", "Modbus TCP unit id 不匹配。");
        if (length is < 3 or > 254)
            throw new ModbusProtocolException("invalid_length", $"Modbus TCP 响应长度 {length} 无效。");

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
                throw new ModbusProtocolException("invalid_exception", "Modbus TCP 异常响应长度无效。");
            throw new ModbusProtocolException(
                $"device_exception_{pdu[1]:x2}",
                $"Modbus 设备返回异常码 0x{pdu[1]:X2}。");
        }
        if (actualFunction != expectedFunction)
            throw new ModbusProtocolException("function_mismatch", "Modbus TCP function code 不匹配。");
        if (pdu.Length < 2)
            throw new ModbusProtocolException("invalid_payload", "Modbus TCP 读取响应缺少 byte count。");

        int expectedByteCount = batch.Area is ModbusRegisterArea.Coil or ModbusRegisterArea.DiscreteInput
            ? (batch.Count + 7) / 8
            : batch.Count * sizeof(ushort);
        int byteCount = pdu[1];
        if (byteCount != expectedByteCount || pdu.Length != byteCount + 2)
        {
            throw new ModbusProtocolException(
                "invalid_byte_count",
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
