using System.Buffers.Binary;
using SonnetDB.Engine;
using SonnetDB.Tables;

namespace SonnetDB.Modbus;

internal static class ModbusEndpointValueReader
{
    internal static ModbusEndpointReadResult Read(
        Tsdb database,
        string endpointName,
        ModbusRegisterArea area,
        ushort startAddress,
        ushort count)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);

        var owners = new MappingSource?[count];
        int requestEndExclusive = startAddress + count;
        foreach (ModbusTableBinding binding in database.Modbus.Catalog.ListBindings())
        {
            if (binding.Direction != ModbusMappingDirection.TableToEndpoint
                || !string.Equals(binding.TargetName, endpointName, StringComparison.Ordinal))
            {
                continue;
            }

            // Phase A 没有 binding 启停 DDL，旧 catalog 会持久化 false；存在即参与运行时。
            foreach (ModbusColumnMapping mapping in binding.Columns)
            {
                if (mapping.Area != area || mapping.Access == ModbusAccessMode.Write)
                    continue;

                int mappingStart = mapping.PduAddress;
                int mappingEndExclusive = mappingStart + mapping.RegisterCount;
                int overlapStart = Math.Max(startAddress, mappingStart);
                int overlapEndExclusive = Math.Min(requestEndExclusive, mappingEndExclusive);
                if (overlapStart >= overlapEndExclusive)
                    continue;

                var source = new MappingSource(binding, mapping);
                for (int address = overlapStart; address < overlapEndExclusive; address++)
                {
                    int requestIndex = address - startAddress;
                    if (owners[requestIndex] is not null)
                        return ModbusEndpointReadResult.Failure(ModbusTcpExceptionCodes.ServerDeviceFailure);
                    owners[requestIndex] = source;
                }
            }
        }

        if (owners.Any(static owner => owner is null))
            return ModbusEndpointReadResult.Failure(ModbusTcpExceptionCodes.IllegalDataAddress);

        try
        {
            var encodedMappings = new Dictionary<MappingSource, ushort[]>();
            var values = new ushort[count];
            for (int index = 0; index < owners.Length; index++)
            {
                MappingSource source = owners[index]!;
                if (!encodedMappings.TryGetValue(source, out ushort[]? encoded))
                {
                    encoded = EncodeMapping(database, source);
                    encodedMappings.Add(source, encoded);
                }

                int address = startAddress + index;
                values[index] = encoded[address - source.Mapping.PduAddress];
            }

            return ModbusEndpointReadResult.Success(values);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidDataException
                                          or InvalidOperationException
                                          or IOException
                                          or ObjectDisposedException
                                          or OverflowException)
        {
            return ModbusEndpointReadResult.Failure(ModbusTcpExceptionCodes.ServerDeviceFailure);
        }
    }

    private static ushort[] EncodeMapping(Tsdb database, MappingSource source)
    {
        ModbusTableBinding binding = source.Binding;
        ModbusColumnMapping mapping = source.Mapping;
        TableStore store = database.Tables.Open(binding.TableName);
        TableSchema schema = store.Schema;
        TableColumn column = schema.TryGetColumn(mapping.ColumnName)
            ?? throw new InvalidOperationException(
                $"Modbus table '{binding.TableName}' 不存在映射列 '{mapping.ColumnName}'。");
        long rowKey = binding.RowKey
            ?? throw new InvalidOperationException(
                $"Modbus endpoint table '{binding.TableName}' 缺少固定 ROW KEY。");
        TableRow row = store.GetByPrimaryKey([rowKey])
            ?? throw new InvalidOperationException(
                $"Modbus endpoint table '{binding.TableName}' 不存在 ROW KEY {rowKey}。");
        object value = row.Values[column.Ordinal]
            ?? throw new InvalidOperationException(
                $"Modbus endpoint table '{binding.TableName}' 的映射列 '{mapping.ColumnName}' 为 NULL。");

        var encoded = new ushort[mapping.RegisterCount];
        if (mapping.ValueType == ModbusValueType.Bit
            && mapping.Area is ModbusRegisterArea.HoldingRegister or ModbusRegisterArea.InputRegister)
        {
            if (value is not bool bitValue || mapping.BitIndex is null)
                throw new InvalidOperationException("Modbus 寄存器 BIT 映射必须提供 Boolean 值和 bit index。");
            ushort register = bitValue ? checked((ushort)(1 << mapping.BitIndex.Value)) : (ushort)0;
            encoded[0] = mapping.ByteOrder == ModbusByteOrder.LittleEndian
                ? BinaryPrimitives.ReverseEndianness(register)
                : register;
            return encoded;
        }

        _ = ModbusValueCodec.Encode(
            value,
            encoded,
            mapping.Area,
            mapping.ValueType,
            mapping.StringLength,
            mapping.BitIndex ?? 0,
            mapping.ByteOrder,
            mapping.WordOrder,
            mapping.Scale,
            mapping.Offset);
        return encoded;
    }

    private sealed class MappingSource(
        ModbusTableBinding binding,
        ModbusColumnMapping mapping)
    {
        internal ModbusTableBinding Binding { get; } = binding;

        internal ModbusColumnMapping Mapping { get; } = mapping;
    }
}

internal readonly record struct ModbusEndpointReadResult(
    ushort[]? Values,
    byte ExceptionCode)
{
    internal bool Succeeded => Values is not null;

    internal static ModbusEndpointReadResult Success(ushort[] values) => new(values, 0);

    internal static ModbusEndpointReadResult Failure(byte exceptionCode) => new(null, exceptionCode);
}
