using SonnetDB.Modbus;
using Xunit;

namespace SonnetDB.Core.Tests.Modbus;

public sealed class ModbusMappingValidatorTests
{
    /// <summary>
    /// 验证四类区域使用彼此独立的地址空间，相同 PDU 地址不会冲突。
    /// </summary>
    [Fact]
    public void ValidateBinding_SameAddressInFourAreas_Succeeds()
    {
        var binding = Binding(
            Mapping("coil", ModbusRegisterArea.Coil, 10, ModbusValueType.Bit),
            Mapping("discrete", ModbusRegisterArea.DiscreteInput, 10, ModbusValueType.Bit),
            Mapping("holding", ModbusRegisterArea.HoldingRegister, 10, ModbusValueType.UInt16),
            Mapping("input", ModbusRegisterArea.InputRegister, 10, ModbusValueType.UInt16));

        ModbusMappingValidator.ValidateBinding(binding);
    }

    /// <summary>
    /// 验证同一区域首尾相接但不相交的寄存器跨度可以共存。
    /// </summary>
    [Fact]
    public void ValidateBinding_AdjacentSpans_Succeeds()
    {
        var binding = Binding(
            Mapping("first", ModbusRegisterArea.HoldingRegister, 100, ModbusValueType.UInt32, registerCount: 2),
            Mapping("second", ModbusRegisterArea.HoldingRegister, 102, ModbusValueType.Float64, registerCount: 4));

        ModbusMappingValidator.ValidateBinding(binding);
    }

    /// <summary>
    /// 验证同一区域内嵌套或部分相交的寄存器跨度都会被拒绝。
    /// </summary>
    [Theory]
    [InlineData(101)]
    [InlineData(103)]
    public void ValidateBinding_SameAreaSpanOverlaps_Throws(int overlapStart)
    {
        var binding = Binding(
            Mapping("wide", ModbusRegisterArea.HoldingRegister, 100, ModbusValueType.Float64, registerCount: 4),
            Mapping(
                "overlap",
                ModbusRegisterArea.HoldingRegister,
                checked((ushort)overlapStart),
                ModbusValueType.UInt32,
                registerCount: 2));

        Assert.Throws<ArgumentException>(() => ModbusMappingValidator.ValidateBinding(binding));
    }

    /// <summary>
    /// 验证完整保持寄存器与同地址的寄存器位映射属于冲突区间。
    /// </summary>
    [Fact]
    public void ValidateBinding_FullRegisterAndBitAtSameAddress_Throws()
    {
        var binding = Binding(
            Mapping("register", ModbusRegisterArea.HoldingRegister, 20, ModbusValueType.UInt16),
            Mapping(
                "flag",
                ModbusRegisterArea.HoldingRegister,
                20,
                ModbusValueType.Bit,
                bitIndex: 7));

        Assert.Throws<ArgumentException>(() => ModbusMappingValidator.ValidateBinding(binding));
    }

    /// <summary>
    /// 验证四寄存器 Float64 恰好结束于 PDU 上界时有效。
    /// </summary>
    [Fact]
    public void ValidateColumn_Float64EndingAtUpperBoundary_Succeeds()
    {
        var mapping = Mapping(
            "value",
            ModbusRegisterArea.HoldingRegister,
            65_532,
            ModbusValueType.Float64,
            registerCount: 4);

        ModbusMappingValidator.ValidateColumn(mapping);
    }

    /// <summary>
    /// 验证四寄存器 Float64 从 65533 开始会越过 PDU 上界。
    /// </summary>
    [Fact]
    public void ValidateColumn_Float64ExceedingUpperBoundary_Throws()
    {
        var mapping = Mapping(
            "value",
            ModbusRegisterArea.HoldingRegister,
            65_533,
            ModbusValueType.Float64,
            registerCount: 4);

        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusMappingValidator.ValidateColumn(mapping));
    }

    /// <summary>
    /// 验证显式寄存器数量与 wire 类型固定宽度不一致时被拒绝。
    /// </summary>
    [Fact]
    public void ValidateColumn_ExplicitRegisterCountMismatch_Throws()
    {
        var mapping = Mapping(
            "value",
            ModbusRegisterArea.HoldingRegister,
            10,
            ModbusValueType.UInt32,
            registerCount: 1);

        Assert.Throws<ArgumentException>(() => ModbusMappingValidator.ValidateColumn(mapping));
    }

    /// <summary>
    /// 验证离散输入和输入寄存器不允许 WRITE 或 READWRITE 访问。
    /// </summary>
    [Theory]
    [InlineData(ModbusRegisterArea.DiscreteInput, ModbusAccessMode.Write)]
    [InlineData(ModbusRegisterArea.DiscreteInput, ModbusAccessMode.ReadWrite)]
    [InlineData(ModbusRegisterArea.InputRegister, ModbusAccessMode.Write)]
    [InlineData(ModbusRegisterArea.InputRegister, ModbusAccessMode.ReadWrite)]
    public void ValidateColumn_ReadOnlyAreaWithWritableAccess_Throws(
        ModbusRegisterArea area,
        ModbusAccessMode access)
    {
        ModbusValueType valueType = area == ModbusRegisterArea.DiscreteInput
            ? ModbusValueType.Bit
            : ModbusValueType.UInt16;
        var mapping = Mapping("value", area, 10, valueType, access: access);

        Assert.Throws<ArgumentException>(() => ModbusMappingValidator.ValidateColumn(mapping));
    }

    /// <summary>
    /// 验证保持寄存器和输入寄存器的 BIT 映射第一版均只允许读取。
    /// </summary>
    [Theory]
    [InlineData(ModbusRegisterArea.HoldingRegister, ModbusAccessMode.Write)]
    [InlineData(ModbusRegisterArea.HoldingRegister, ModbusAccessMode.ReadWrite)]
    [InlineData(ModbusRegisterArea.InputRegister, ModbusAccessMode.Write)]
    [InlineData(ModbusRegisterArea.InputRegister, ModbusAccessMode.ReadWrite)]
    public void ValidateColumn_WritableRegisterBit_Throws(
        ModbusRegisterArea area,
        ModbusAccessMode access)
    {
        var mapping = Mapping(
            "flag",
            area,
            10,
            ModbusValueType.Bit,
            bitIndex: 3,
            access: access);

        Assert.Throws<ArgumentException>(() => ModbusMappingValidator.ValidateColumn(mapping));
    }

    /// <summary>
    /// 验证线圈 BIT 映射允许 WRITE 和 READWRITE 访问。
    /// </summary>
    [Theory]
    [InlineData(ModbusAccessMode.Write)]
    [InlineData(ModbusAccessMode.ReadWrite)]
    public void ValidateColumn_WritableCoilBit_Succeeds(ModbusAccessMode access)
    {
        var mapping = Mapping(
            "flag",
            ModbusRegisterArea.Coil,
            10,
            ModbusValueType.Bit,
            access: access);

        ModbusMappingValidator.ValidateColumn(mapping);
    }

    /// <summary>
    /// 验证 BIT 与 STRING 类型都不允许声明 SCALE 或 OFFSET 变换。
    /// </summary>
    [Fact]
    public void ValidateColumn_BitOrStringWithScaleOrOffset_Throws()
    {
        Assert.Throws<ArgumentException>(() => ModbusMappingValidator.ValidateColumn(Mapping(
            "scaled_bit",
            ModbusRegisterArea.Coil,
            10,
            ModbusValueType.Bit,
            scale: 2m)));
        Assert.Throws<ArgumentException>(() => ModbusMappingValidator.ValidateColumn(Mapping(
            "offset_bit",
            ModbusRegisterArea.Coil,
            10,
            ModbusValueType.Bit,
            offset: 1m)));
        Assert.Throws<ArgumentException>(() => ModbusMappingValidator.ValidateColumn(Mapping(
            "scaled_string",
            ModbusRegisterArea.HoldingRegister,
            10,
            ModbusValueType.String,
            registerCount: 2,
            stringLength: 4,
            scale: 2m)));
        Assert.Throws<ArgumentException>(() => ModbusMappingValidator.ValidateColumn(Mapping(
            "offset_string",
            ModbusRegisterArea.HoldingRegister,
            10,
            ModbusValueType.String,
            registerCount: 2,
            stringLength: 4,
            offset: 1m)));
    }

    /// <summary>
    /// 验证线圈和离散输入区域不能映射寄存器值类型。
    /// </summary>
    [Theory]
    [InlineData(ModbusRegisterArea.Coil)]
    [InlineData(ModbusRegisterArea.DiscreteInput)]
    public void ValidateColumn_BitAreaWithRegisterValueType_Throws(ModbusRegisterArea area)
    {
        var mapping = Mapping("value", area, 10, ModbusValueType.UInt16);

        Assert.Throws<ArgumentException>(() => ModbusMappingValidator.ValidateColumn(mapping));
    }

    /// <summary>
    /// 创建只包含当前测试映射的 source 表绑定。
    /// </summary>
    private static ModbusTableBinding Binding(params ModbusColumnMapping[] mappings)
        => new(
            "devices",
            ModbusMappingDirection.SourceToTable,
            "plc",
            mappings);

    /// <summary>
    /// 创建已规范化的列映射，便于单独组合边界与冲突场景。
    /// </summary>
    private static ModbusColumnMapping Mapping(
        string name,
        ModbusRegisterArea area,
        ushort pduAddress,
        ModbusValueType valueType,
        int registerCount = 1,
        int? bitIndex = null,
        ModbusAccessMode access = ModbusAccessMode.Read,
        decimal scale = 1m,
        decimal offset = 0m,
        int stringLength = 0)
        => new(
            name,
            area,
            pduAddress,
            pduAddress,
            valueType,
            registerCount,
            stringLength,
            bitIndex,
            Scale: scale,
            Offset: offset,
            Access: access);
}
