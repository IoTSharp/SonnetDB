using System.Buffers.Binary;
using SonnetDB.Engine;

namespace SonnetDB.Modbus;

internal static class ModbusTcpExceptionCodes
{
    internal const byte IllegalFunction = 0x01;
    internal const byte IllegalDataAddress = 0x02;
    internal const byte IllegalDataValue = 0x03;
    internal const byte ServerDeviceFailure = 0x04;
    internal const byte ServerDeviceBusy = 0x06;
    internal const byte GatewayTargetDeviceFailedToRespond = 0x0B;
}

internal static class ModbusTcpSlaveProtocol
{
    internal const int MbapHeaderLength = 7;
    internal const int MaximumMbapLength = 254;

    internal static ModbusSlaveResponse ProcessRequest(
        Tsdb database,
        string databaseName,
        ModbusEndpointDefinition endpoint,
        ModbusEndpointWriteService endpointWriteService,
        string remoteEndpoint,
        ushort transactionId,
        byte requestUnitId,
        ReadOnlySpan<byte> pdu)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(endpointWriteService);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteEndpoint);
        if (pdu.IsEmpty)
        {
            return new ModbusSlaveResponse(
                BuildException(transactionId, requestUnitId, 0, ModbusTcpExceptionCodes.IllegalDataValue),
                IsReadRequest: false,
                IsWriteRequest: false,
                Succeeded: false,
                Area: null);
        }

        byte functionCode = pdu[0];
        ModbusRegisterArea? area = GetReadArea(functionCode);
        bool isReadRequest = area is not null;
        bool isWriteRequest = IsWriteFunction(functionCode);
        if (requestUnitId != endpoint.UnitId)
        {
            return new ModbusSlaveResponse(
                BuildException(
                    transactionId,
                    requestUnitId,
                    functionCode,
                    ModbusTcpExceptionCodes.GatewayTargetDeviceFailedToRespond),
                isReadRequest,
                isWriteRequest,
                Succeeded: false,
                area);
        }

        if (!isReadRequest && !isWriteRequest)
        {
            return new ModbusSlaveResponse(
                BuildException(
                    transactionId,
                    requestUnitId,
                    functionCode,
                    ModbusTcpExceptionCodes.IllegalFunction),
                IsReadRequest: false,
                IsWriteRequest: false,
                Succeeded: false,
                Area: null);
        }

        if (isWriteRequest)
        {
            if (!TryParseWriteCommand(pdu, out ModbusEndpointWriteCommand command, out byte exceptionCode))
            {
                return new ModbusSlaveResponse(
                    BuildException(transactionId, requestUnitId, functionCode, exceptionCode),
                    IsReadRequest: false,
                    IsWriteRequest: true,
                    Succeeded: false,
                    Area: GetWriteArea(functionCode));
            }

            ModbusEndpointStageResult staged = endpointWriteService.Stage(
                database,
                databaseName,
                endpoint,
                remoteEndpoint,
                transactionId,
                requestUnitId,
                command);
            if (!staged.Succeeded)
            {
                return new ModbusSlaveResponse(
                    BuildException(transactionId, requestUnitId, functionCode, staged.ExceptionCode),
                    IsReadRequest: false,
                    IsWriteRequest: true,
                    Succeeded: false,
                    command.Area);
            }

            return new ModbusSlaveResponse(
                BuildWriteResponse(transactionId, requestUnitId, pdu, command),
                IsReadRequest: false,
                IsWriteRequest: true,
                Succeeded: true,
                command.Area);
        }

        if (pdu.Length != 5)
        {
            return new ModbusSlaveResponse(
                BuildException(
                    transactionId,
                    requestUnitId,
                    functionCode,
                    ModbusTcpExceptionCodes.IllegalDataValue),
                IsReadRequest: true,
                IsWriteRequest: false,
                Succeeded: false,
                area);
        }

        ushort startAddress = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        ushort count = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        int maximumCount = area is ModbusRegisterArea.Coil or ModbusRegisterArea.DiscreteInput
            ? 2_000
            : 125;
        if (count == 0 || count > maximumCount || startAddress + count > 65_536)
        {
            return new ModbusSlaveResponse(
                BuildException(
                    transactionId,
                    requestUnitId,
                    functionCode,
                    ModbusTcpExceptionCodes.IllegalDataValue),
                IsReadRequest: true,
                IsWriteRequest: false,
                Succeeded: false,
                area);
        }

        ModbusEndpointReadResult read = ModbusEndpointValueReader.Read(
            database,
            endpoint.Name,
            area.GetValueOrDefault(),
            startAddress,
            count);
        if (!read.Succeeded)
        {
            return new ModbusSlaveResponse(
                BuildException(transactionId, requestUnitId, functionCode, read.ExceptionCode),
                IsReadRequest: true,
                IsWriteRequest: false,
                Succeeded: false,
                area);
        }

        return new ModbusSlaveResponse(
            BuildReadResponse(transactionId, requestUnitId, functionCode, area.GetValueOrDefault(), read.Values!),
            IsReadRequest: true,
            IsWriteRequest: false,
            Succeeded: true,
            area);
    }

    private static bool TryParseWriteCommand(
        ReadOnlySpan<byte> pdu,
        out ModbusEndpointWriteCommand command,
        out byte exceptionCode)
    {
        command = default;
        exceptionCode = ModbusTcpExceptionCodes.IllegalDataValue;
        byte functionCode = pdu[0];
        ModbusRegisterArea area = GetWriteArea(functionCode)
            ?? throw new ArgumentOutOfRangeException(nameof(pdu), "未知的 Modbus 写 function code。");
        if (functionCode is 0x05 or 0x06)
        {
            if (pdu.Length != 5)
                return false;
            ushort address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
            ushort wireValue = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
            ushort value;
            if (functionCode == 0x05)
            {
                if (wireValue is not 0x0000 and not 0xFF00)
                    return false;
                value = wireValue == 0xFF00 ? (ushort)1 : (ushort)0;
            }
            else
            {
                value = wireValue;
            }

            command = new ModbusEndpointWriteCommand(functionCode, area, address, [value]);
            return true;
        }

        if (pdu.Length < 7)
            return false;
        ushort startAddress = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        ushort count = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        byte byteCount = pdu[5];
        int expectedByteCount;
        if (functionCode == 0x0F)
        {
            if (count is 0 or > 1_968)
                return false;
            expectedByteCount = (count + 7) / 8;
        }
        else
        {
            if (count is 0 or > 123)
                return false;
            expectedByteCount = count * sizeof(ushort);
        }
        if (startAddress + count > 65_536
            || byteCount != expectedByteCount
            || pdu.Length != 6 + byteCount)
        {
            return false;
        }

        var values = new ushort[count];
        if (functionCode == 0x0F)
        {
            for (int index = 0; index < count; index++)
                values[index] = (ushort)((pdu[6 + (index / 8)] >> (index % 8)) & 0x01);
        }
        else
        {
            for (int index = 0; index < count; index++)
            {
                values[index] = BinaryPrimitives.ReadUInt16BigEndian(
                    pdu[(6 + (index * sizeof(ushort)))..]);
            }
        }

        command = new ModbusEndpointWriteCommand(functionCode, area, startAddress, values);
        return true;
    }

    private static byte[] BuildWriteResponse(
        ushort transactionId,
        byte unitId,
        ReadOnlySpan<byte> requestPdu,
        ModbusEndpointWriteCommand command)
    {
        var response = new byte[12];
        WriteHeader(response, transactionId, length: 6, unitId);
        response[7] = command.FunctionCode;
        requestPdu[1..5].CopyTo(response.AsSpan(8));
        return response;
    }

    private static byte[] BuildReadResponse(
        ushort transactionId,
        byte unitId,
        byte functionCode,
        ModbusRegisterArea area,
        IReadOnlyList<ushort> values)
    {
        int byteCount = area is ModbusRegisterArea.Coil or ModbusRegisterArea.DiscreteInput
            ? (values.Count + 7) / 8
            : values.Count * sizeof(ushort);
        var response = new byte[9 + byteCount];
        WriteHeader(response, transactionId, checked((ushort)(3 + byteCount)), unitId);
        response[7] = functionCode;
        response[8] = checked((byte)byteCount);
        if (area is ModbusRegisterArea.Coil or ModbusRegisterArea.DiscreteInput)
        {
            for (int index = 0; index < values.Count; index++)
            {
                if (values[index] != 0)
                    response[9 + (index / 8)] |= checked((byte)(1 << (index % 8)));
            }
        }
        else
        {
            for (int index = 0; index < values.Count; index++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(
                    response.AsSpan(9 + (index * sizeof(ushort))),
                    values[index]);
            }
        }

        return response;
    }

    private static byte[] BuildException(
        ushort transactionId,
        byte unitId,
        byte functionCode,
        byte exceptionCode)
    {
        var response = new byte[9];
        WriteHeader(response, transactionId, length: 3, unitId);
        response[7] = (byte)(functionCode | 0x80);
        response[8] = exceptionCode;
        return response;
    }

    private static void WriteHeader(Span<byte> destination, ushort transactionId, ushort length, byte unitId)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], length);
        destination[6] = unitId;
    }

    private static ModbusRegisterArea? GetReadArea(byte functionCode) => functionCode switch
    {
        0x01 => ModbusRegisterArea.Coil,
        0x02 => ModbusRegisterArea.DiscreteInput,
        0x03 => ModbusRegisterArea.HoldingRegister,
        0x04 => ModbusRegisterArea.InputRegister,
        _ => null,
    };

    private static bool IsWriteFunction(byte functionCode)
        => functionCode is 0x05 or 0x06 or 0x0F or 0x10;

    private static ModbusRegisterArea? GetWriteArea(byte functionCode) => functionCode switch
    {
        0x05 or 0x0F => ModbusRegisterArea.Coil,
        0x06 or 0x10 => ModbusRegisterArea.HoldingRegister,
        _ => null,
    };
}

internal readonly record struct ModbusSlaveResponse(
    byte[] Adu,
    bool IsReadRequest,
    bool IsWriteRequest,
    bool Succeeded,
    ModbusRegisterArea? Area);
