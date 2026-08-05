using SonnetDB.Modbus;
using Xunit;

namespace SonnetDB.Core.Tests.Modbus;

public sealed class ModbusAddressTests
{
    /// <summary>
    /// 验证零基寻址完整覆盖 PDU 的 0..65535 边界。
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(65_535, 65_535)]
    public void ToPduAddress_ZeroBasedAddress_ReturnsSameOffset(int declaredAddress, int expected)
    {
        ushort actual = ModbusAddress.ToPduAddress(
            declaredAddress,
            ModbusRegisterArea.HoldingRegister,
            ModbusAddressingMode.ZeroBased);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// 验证一基寻址把 1..65536 规范化为 0..65535。
    /// </summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(65_536, 65_535)]
    public void ToPduAddress_OneBasedAddress_SubtractsOne(int declaredAddress, int expected)
    {
        ushort actual = ModbusAddress.ToPduAddress(
            declaredAddress,
            ModbusRegisterArea.InputRegister,
            ModbusAddressingMode.OneBased);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// 验证 Modicon 五位常用地址按各自区域前缀规范化。
    /// </summary>
    [Theory]
    [InlineData(ModbusRegisterArea.Coil, 1, 0)]
    [InlineData(ModbusRegisterArea.Coil, 9_999, 9_998)]
    [InlineData(ModbusRegisterArea.DiscreteInput, 10_001, 0)]
    [InlineData(ModbusRegisterArea.DiscreteInput, 19_999, 9_998)]
    [InlineData(ModbusRegisterArea.InputRegister, 30_001, 0)]
    [InlineData(ModbusRegisterArea.InputRegister, 39_999, 9_998)]
    [InlineData(ModbusRegisterArea.HoldingRegister, 40_001, 0)]
    [InlineData(ModbusRegisterArea.HoldingRegister, 49_999, 9_998)]
    public void ToPduAddress_ModiconFiveDigitAddress_UsesAreaPrefix(
        ModbusRegisterArea area,
        int declaredAddress,
        int expected)
    {
        ushort actual = ModbusAddress.ToPduAddress(
            declaredAddress,
            area,
            ModbusAddressingMode.Modicon);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// 验证 Modicon 六位扩展地址可覆盖完整 16 位 PDU 上界。
    /// </summary>
    [Theory]
    [InlineData(ModbusRegisterArea.Coil, 65_536, 65_535)]
    [InlineData(ModbusRegisterArea.DiscreteInput, 100_001, 0)]
    [InlineData(ModbusRegisterArea.DiscreteInput, 165_536, 65_535)]
    [InlineData(ModbusRegisterArea.InputRegister, 300_001, 0)]
    [InlineData(ModbusRegisterArea.InputRegister, 365_536, 65_535)]
    [InlineData(ModbusRegisterArea.HoldingRegister, 400_001, 0)]
    [InlineData(ModbusRegisterArea.HoldingRegister, 465_536, 65_535)]
    public void ToPduAddress_ModiconExtendedAddress_CoversFullPduRange(
        ModbusRegisterArea area,
        int declaredAddress,
        int expected)
    {
        ushort actual = ModbusAddress.ToPduAddress(
            declaredAddress,
            area,
            ModbusAddressingMode.Modicon);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// 验证零基和一基模式拒绝各自范围之外的地址。
    /// </summary>
    [Theory]
    [InlineData(-1, ModbusAddressingMode.ZeroBased)]
    [InlineData(65_536, ModbusAddressingMode.ZeroBased)]
    [InlineData(0, ModbusAddressingMode.OneBased)]
    [InlineData(65_537, ModbusAddressingMode.OneBased)]
    public void ToPduAddress_BaseModeOutOfRange_Throws(
        int declaredAddress,
        ModbusAddressingMode addressingMode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusAddress.ToPduAddress(
            declaredAddress,
            ModbusRegisterArea.HoldingRegister,
            addressingMode));
    }

    /// <summary>
    /// 验证 Modicon 非线圈区域拒绝缺少前缀或超出扩展上界的地址。
    /// </summary>
    [Theory]
    [InlineData(ModbusRegisterArea.DiscreteInput, 1)]
    [InlineData(ModbusRegisterArea.DiscreteInput, 165_537)]
    [InlineData(ModbusRegisterArea.InputRegister, 365_537)]
    [InlineData(ModbusRegisterArea.HoldingRegister, 465_537)]
    public void ToPduAddress_ModiconAddressOutsideAreaRange_Throws(
        ModbusRegisterArea area,
        int declaredAddress)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusAddress.ToPduAddress(
            declaredAddress,
            area,
            ModbusAddressingMode.Modicon));
    }

    /// <summary>
    /// 验证 Modicon 地址前缀与声明区域不一致时稳定拒绝。
    /// </summary>
    [Theory]
    [InlineData(ModbusRegisterArea.HoldingRegister, 30_001)]
    [InlineData(ModbusRegisterArea.InputRegister, 400_001)]
    [InlineData(ModbusRegisterArea.DiscreteInput, 300_001)]
    public void ToPduAddress_ModiconPrefixMismatch_Throws(
        ModbusRegisterArea area,
        int declaredAddress)
    {
        Assert.Throws<ArgumentException>(() => ModbusAddress.ToPduAddress(
            declaredAddress,
            area,
            ModbusAddressingMode.Modicon));
    }
}
