namespace SonnetDB.Modbus;

/// <summary>
/// 提供 Modbus 声明地址到 PDU 偏移地址的规范化转换。
/// </summary>
public static class ModbusAddress
{
    /// <summary>
    /// 将指定寻址模式下的声明地址转换为从 0 开始的 PDU 地址。
    /// </summary>
    /// <param name="declaredAddress">DDL 或配置中声明的地址。</param>
    /// <param name="area">声明地址所属的 Modbus 地址空间。</param>
    /// <param name="addressingMode">声明地址的解释方式。</param>
    /// <returns>范围为 0..65535 的 PDU 地址。</returns>
    /// <exception cref="ArgumentOutOfRangeException">地址、地址空间或寻址模式无效。</exception>
    /// <exception cref="ArgumentException">Modicon 地址前缀与声明地址空间不一致。</exception>
    public static ushort ToPduAddress(
        int declaredAddress,
        ModbusRegisterArea area,
        ModbusAddressingMode addressingMode)
    {
        if (!Enum.IsDefined(area))
            throw new ArgumentOutOfRangeException(nameof(area), area, "未知的 Modbus 地址空间。");
        if (!Enum.IsDefined(addressingMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(addressingMode),
                addressingMode,
                "未知的 Modbus 寻址模式。");
        }

        return addressingMode switch
        {
            ModbusAddressingMode.ZeroBased => ConvertZeroBased(declaredAddress),
            ModbusAddressingMode.OneBased => ConvertOneBased(declaredAddress),
            ModbusAddressingMode.Modicon => ConvertModicon(declaredAddress, area),
            _ => throw new ArgumentOutOfRangeException(nameof(addressingMode)),
        };
    }

    /// <summary>
    /// 校验并转换从 0 开始的地址。
    /// </summary>
    private static ushort ConvertZeroBased(int declaredAddress)
    {
        if ((uint)declaredAddress > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(declaredAddress),
                declaredAddress,
                "从 0 开始的 Modbus 地址必须位于 0..65535。");
        }

        return (ushort)declaredAddress;
    }

    /// <summary>
    /// 校验并转换从 1 开始的地址。
    /// </summary>
    private static ushort ConvertOneBased(int declaredAddress)
    {
        if (declaredAddress is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(declaredAddress),
                declaredAddress,
                "从 1 开始的 Modbus 地址必须位于 1..65536。");
        }

        return (ushort)(declaredAddress - 1);
    }

    /// <summary>
    /// 优先识别 Modicon 区域前缀，再兼容当前区域内从 1 开始的历史简写。
    /// </summary>
    private static ushort ConvertModicon(int declaredAddress, ModbusRegisterArea expectedArea)
    {
        // 线圈的 0 前缀在整数声明中会丢失，因此其五位和六位形式都表现为一基地址。
        if (expectedArea == ModbusRegisterArea.Coil)
            return ConvertOneBased(declaredAddress);

        if (TryDecodePrefixedAddress(declaredAddress, out var actualArea, out ushort pduAddress))
        {
            if (actualArea != expectedArea)
            {
                throw new ArgumentException(
                    $"Modicon 地址 {declaredAddress} 属于 {actualArea}，与声明区域 {expectedArea} 不一致。",
                    nameof(declaredAddress));
            }

            return pduAddress;
        }

        throw new ArgumentOutOfRangeException(
            nameof(declaredAddress),
            declaredAddress,
            $"{expectedArea} 的 Modicon 地址必须使用对应的五位或六位区域前缀。");
    }

    /// <summary>
    /// 识别传统五位及扩展六位的非线圈 Modicon 前缀地址。
    /// </summary>
    private static bool TryDecodePrefixedAddress(
        int declaredAddress,
        out ModbusRegisterArea area,
        out ushort pduAddress)
    {
        if (TryDecodeRange(
                declaredAddress,
                100_001,
                ModbusRegisterArea.DiscreteInput,
                out area,
                out pduAddress)
            || TryDecodeRange(
                declaredAddress,
                300_001,
                ModbusRegisterArea.InputRegister,
                out area,
                out pduAddress)
            || TryDecodeRange(
                declaredAddress,
                400_001,
                ModbusRegisterArea.HoldingRegister,
                out area,
                out pduAddress)
            || TryDecodeRange(
                declaredAddress,
                10_001,
                ModbusRegisterArea.DiscreteInput,
                out area,
                out pduAddress,
                9_999)
            || TryDecodeRange(
                declaredAddress,
                30_001,
                ModbusRegisterArea.InputRegister,
                out area,
                out pduAddress,
                9_999)
            || TryDecodeRange(
                declaredAddress,
                40_001,
                ModbusRegisterArea.HoldingRegister,
                out area,
                out pduAddress,
                9_999))
        {
            return true;
        }

        area = default;
        pduAddress = default;
        return false;
    }

    /// <summary>
    /// 尝试把一个带区域基址的连续一基地址范围转换为 PDU 偏移。
    /// </summary>
    private static bool TryDecodeRange(
        int declaredAddress,
        int firstAddress,
        ModbusRegisterArea candidateArea,
        out ModbusRegisterArea area,
        out ushort pduAddress,
        int length = 65_536)
    {
        long offset = (long)declaredAddress - firstAddress;
        if ((ulong)offset >= (uint)length)
        {
            area = default;
            pduAddress = default;
            return false;
        }

        area = candidateArea;
        pduAddress = (ushort)offset;
        return true;
    }
}
