namespace SonnetDB.Modbus;

internal readonly record struct ModbusReadBatch(
    ModbusRegisterArea Area,
    ushort StartAddress,
    ushort Count)
{
    internal byte FunctionCode => Area switch
    {
        ModbusRegisterArea.Coil => 0x01,
        ModbusRegisterArea.DiscreteInput => 0x02,
        ModbusRegisterArea.HoldingRegister => 0x03,
        ModbusRegisterArea.InputRegister => 0x04,
        _ => throw new ArgumentOutOfRangeException(nameof(Area), Area, "未知的 Modbus 地址空间。"),
    };
}

internal static class ModbusReadPlanner
{
    private const int MaximumBitCount = 2_000;
    private const int MaximumRegisterCount = 125;

    internal static IReadOnlyList<ModbusReadBatch> Create(
        IReadOnlyList<ModbusTableBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        var ranges = new List<ReadRange>();
        foreach (ModbusTableBinding binding in bindings)
        {
            foreach (ModbusColumnMapping mapping in binding.Columns)
            {
                if (mapping.Access == ModbusAccessMode.Write)
                    continue;

                ranges.Add(new ReadRange(
                    mapping.Area,
                    mapping.PduAddress,
                    checked(mapping.PduAddress + mapping.RegisterCount)));
            }
        }

        if (ranges.Count == 0)
            return [];

        var result = new List<ModbusReadBatch>();
        foreach (IGrouping<ModbusRegisterArea, ReadRange> areaGroup in ranges
                     .GroupBy(static range => range.Area)
                     .OrderBy(static group => group.Key))
        {
            int mergedStart = -1;
            int mergedEndExclusive = -1;
            foreach (ReadRange range in areaGroup
                         .OrderBy(static range => range.Start)
                         .ThenBy(static range => range.EndExclusive))
            {
                if (mergedStart < 0)
                {
                    mergedStart = range.Start;
                    mergedEndExclusive = range.EndExclusive;
                    continue;
                }

                if (range.Start <= mergedEndExclusive)
                {
                    mergedEndExclusive = Math.Max(mergedEndExclusive, range.EndExclusive);
                    continue;
                }

                AddSplitBatches(result, areaGroup.Key, mergedStart, mergedEndExclusive);
                mergedStart = range.Start;
                mergedEndExclusive = range.EndExclusive;
            }

            AddSplitBatches(result, areaGroup.Key, mergedStart, mergedEndExclusive);
        }

        return result;
    }

    private static void AddSplitBatches(
        List<ModbusReadBatch> destination,
        ModbusRegisterArea area,
        int start,
        int endExclusive)
    {
        int maximumCount = area is ModbusRegisterArea.Coil or ModbusRegisterArea.DiscreteInput
            ? MaximumBitCount
            : MaximumRegisterCount;
        while (start < endExclusive)
        {
            int count = Math.Min(maximumCount, endExclusive - start);
            destination.Add(new ModbusReadBatch(area, checked((ushort)start), checked((ushort)count)));
            start += count;
        }
    }

    private readonly record struct ReadRange(
        ModbusRegisterArea Area,
        int Start,
        int EndExclusive);
}
