using System.Buffers.Binary;
using SonnetDB.Engine;

namespace SonnetDB.Modbus;

internal static class ModbusTcpExceptionCodes
{
    internal const byte IllegalFunction = 0x01;
    internal const byte IllegalDataAddress = 0x02;
    internal const byte IllegalDataValue = 0x03;
    internal const byte ServerDeviceFailure = 0x04;
    internal const byte GatewayTargetDeviceFailedToRespond = 0x0B;
}

internal static class ModbusTcpSlaveProtocol
{
    internal const int MbapHeaderLength = 7;
    internal const int MaximumMbapLength = 254;

    internal static ModbusSlaveResponse ProcessRequest(
        Tsdb database,
        ModbusEndpointDefinition endpoint,
        ushort transactionId,
        byte requestUnitId,
        ReadOnlySpan<byte> pdu)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (pdu.IsEmpty)
        {
            return new ModbusSlaveResponse(
                BuildException(transactionId, requestUnitId, 0, ModbusTcpExceptionCodes.IllegalDataValue),
                IsReadRequest: false,
                Succeeded: false,
                Area: null);
        }

        byte functionCode = pdu[0];
        ModbusRegisterArea? area = GetReadArea(functionCode);
        bool isReadRequest = area is not null;
        if (requestUnitId != endpoint.UnitId)
        {
            return new ModbusSlaveResponse(
                BuildException(
                    transactionId,
                    requestUnitId,
                    functionCode,
                    ModbusTcpExceptionCodes.GatewayTargetDeviceFailedToRespond),
                isReadRequest,
                Succeeded: false,
                area);
        }

        if (area is null)
        {
            return new ModbusSlaveResponse(
                BuildException(
                    transactionId,
                    requestUnitId,
                    functionCode,
                    ModbusTcpExceptionCodes.IllegalFunction),
                IsReadRequest: false,
                Succeeded: false,
                Area: null);
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
                Succeeded: false,
                area);
        }

        ModbusEndpointReadResult read = ModbusEndpointValueReader.Read(
            database,
            endpoint.Name,
            area.Value,
            startAddress,
            count);
        if (!read.Succeeded)
        {
            return new ModbusSlaveResponse(
                BuildException(transactionId, requestUnitId, functionCode, read.ExceptionCode),
                IsReadRequest: true,
                Succeeded: false,
                area);
        }

        return new ModbusSlaveResponse(
            BuildReadResponse(transactionId, requestUnitId, functionCode, area.Value, read.Values!),
            IsReadRequest: true,
            Succeeded: true,
            area);
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
}

internal readonly record struct ModbusSlaveResponse(
    byte[] Adu,
    bool IsReadRequest,
    bool Succeeded,
    ModbusRegisterArea? Area);
