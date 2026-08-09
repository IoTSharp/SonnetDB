using SonnetDB.Modbus;
using Xunit;

namespace SonnetDB.Tests.Modbus;

public sealed class ModbusReadPlannerTests
{
    [Fact]
    public void Create_WithAdjacentAndLargeMappings_MergesAndSplitsAtProtocolLimits()
    {
        var binding = new ModbusTableBinding(
            "samples",
            ModbusMappingDirection.SourceToTable,
            "plc",
            [
                Mapping("coil0", ModbusRegisterArea.Coil, 0, 1, ModbusValueType.Bit),
                Mapping("coil1", ModbusRegisterArea.Coil, 1, 1, ModbusValueType.Bit),
                Mapping("register0", ModbusRegisterArea.HoldingRegister, 0, 2, ModbusValueType.Int32),
                Mapping("register2", ModbusRegisterArea.HoldingRegister, 2, 1, ModbusValueType.UInt16),
                Mapping("large", ModbusRegisterArea.HoldingRegister, 200, 130, ModbusValueType.String, 260),
            ]);

        IReadOnlyList<ModbusReadBatch> batches = ModbusReadPlanner.Create([binding]);

        Assert.Equal(
            [
                new ModbusReadBatch(ModbusRegisterArea.Coil, 0, 2),
                new ModbusReadBatch(ModbusRegisterArea.HoldingRegister, 0, 3),
                new ModbusReadBatch(ModbusRegisterArea.HoldingRegister, 200, 125),
                new ModbusReadBatch(ModbusRegisterArea.HoldingRegister, 325, 5),
            ],
            batches);
    }

    private static ModbusColumnMapping Mapping(
        string name,
        ModbusRegisterArea area,
        ushort address,
        int count,
        ModbusValueType valueType,
        int stringLength = 0)
        => new(
            name,
            area,
            address,
            address,
            valueType,
            count,
            stringLength);
}
