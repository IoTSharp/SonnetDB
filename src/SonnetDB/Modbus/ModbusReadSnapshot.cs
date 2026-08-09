namespace SonnetDB.Modbus;

internal sealed class ModbusReadSnapshot
{
    private readonly Dictionary<int, ushort> _values = [];

    internal void Add(ModbusReadBatch batch, IReadOnlyList<ushort> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != batch.Count)
            throw new ArgumentException("Modbus 批次返回值数量与请求不一致。", nameof(values));

        for (int i = 0; i < values.Count; i++)
        {
            ushort address = checked((ushort)(batch.StartAddress + i));
            _values[GetKey(batch.Area, address)] = values[i];
        }
    }

    internal void CopyTo(
        ModbusRegisterArea area,
        ushort startAddress,
        Span<ushort> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            ushort address = checked((ushort)(startAddress + i));
            if (!_values.TryGetValue(GetKey(area, address), out ushort value))
            {
                throw new InvalidDataException(
                    $"Modbus 轮询快照缺少 {area} 地址 {address}。");
            }

            destination[i] = value;
        }
    }

    private static int GetKey(ModbusRegisterArea area, ushort address)
        => ((int)area << 16) | address;
}
