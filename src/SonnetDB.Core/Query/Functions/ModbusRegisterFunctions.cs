using System.Globalization;

namespace SonnetDB.Query.Functions;

/// <summary>
/// 提供 Modbus 两寄存器 32 位值的字节序归一化与类型解码。
/// </summary>
internal static class ModbusRegisterFunctions
{
    /// <summary>
    /// 按指定源字节序把两个 16 位寄存器解码为有符号 32 位整数，并以 SQL Int64 返回。
    /// </summary>
    internal static object? DecodeInt32(IReadOnlyList<object?> arguments)
    {
        if (ContainsNull(arguments))
            return null;

        uint bits = DecodeBits(arguments, "modbus_int32");
        return (long)unchecked((int)bits);
    }

    /// <summary>
    /// 按指定源字节序把两个 16 位寄存器解码为无符号 32 位整数，并以可完整容纳它的 SQL Int64 返回。
    /// </summary>
    internal static object? DecodeUInt32(IReadOnlyList<object?> arguments)
    {
        if (ContainsNull(arguments))
            return null;

        return (long)DecodeBits(arguments, "modbus_uint32");
    }

    /// <summary>
    /// 按指定源字节序把两个 16 位寄存器解码为 IEEE-754 单精度值，并以 SQL Float64 返回。
    /// </summary>
    internal static object? DecodeFloat32(IReadOnlyList<object?> arguments)
    {
        if (ContainsNull(arguments))
            return null;

        uint bits = DecodeBits(arguments, "modbus_float32");
        return (double)BitConverter.Int32BitsToSingle(unchecked((int)bits));
    }

    /// <summary>
    /// 判断严格型 Modbus 函数的任一参数是否为 NULL；NULL 输入直接传播。
    /// </summary>
    private static bool ContainsNull(IReadOnlyList<object?> arguments)
        => arguments.Any(static argument => argument is null);

    /// <summary>
    /// 读取两个寄存器及源顺序，并将收到的四个字节恢复为标准 ABCD 位布局。
    /// </summary>
    private static uint DecodeBits(IReadOnlyList<object?> arguments, string functionName)
    {
        ushort first = ReadRegister(arguments[0]!, functionName, "first_register");
        ushort second = ReadRegister(arguments[1]!, functionName, "second_register");
        ModbusByteOrder order = ReadOrder(arguments[2]!, functionName);

        byte firstHigh = (byte)(first >> 8);
        byte firstLow = (byte)first;
        byte secondHigh = (byte)(second >> 8);
        byte secondLow = (byte)second;

        (byte a, byte b, byte c, byte d) = order switch
        {
            ModbusByteOrder.Abcd => (firstHigh, firstLow, secondHigh, secondLow),
            ModbusByteOrder.Badc => (firstLow, firstHigh, secondLow, secondHigh),
            ModbusByteOrder.Cdab => (secondHigh, secondLow, firstHigh, firstLow),
            ModbusByteOrder.Dcba => (secondLow, secondHigh, firstLow, firstHigh),
            _ => throw new InvalidOperationException($"函数 {functionName} 收到未知字节序。"),
        };

        return ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;
    }

    /// <summary>
    /// 校验并读取一个 Modbus 16 位寄存器值，拒绝小数、负数和超过 65535 的输入。
    /// </summary>
    private static ushort ReadRegister(object value, string functionName, string argumentName)
    {
        if (value is not (byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal))
        {
            throw new InvalidOperationException(
                $"函数 {functionName} 的 {argumentName} 必须是 0..65535 的整数。");
        }

        if (value is float floatValue)
            return ReadFloatingRegister(floatValue, functionName, argumentName);
        if (value is double doubleValue)
            return ReadFloatingRegister(doubleValue, functionName, argumentName);

        decimal numeric;
        try
        {
            numeric = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException(
                $"函数 {functionName} 的 {argumentName} 超出 0..65535 范围。", ex);
        }

        if (numeric < ushort.MinValue || numeric > ushort.MaxValue || decimal.Truncate(numeric) != numeric)
        {
            throw new InvalidOperationException(
                $"函数 {functionName} 的 {argumentName} 必须是 0..65535 的整数。");
        }

        return (ushort)numeric;
    }

    /// <summary>
    /// 严格校验浮点寄存器值，避免先转 decimal 时的有效数字舍入把非整数误判为整数。
    /// </summary>
    private static ushort ReadFloatingRegister(double value, string functionName, string argumentName)
    {
        if (!double.IsFinite(value)
            || value < ushort.MinValue
            || value > ushort.MaxValue
            || Math.Truncate(value) != value)
        {
            throw new InvalidOperationException(
                $"函数 {functionName} 的 {argumentName} 必须是 0..65535 的整数。");
        }

        return (ushort)value;
    }

    /// <summary>
    /// 解析不区分大小写的 ABCD、BADC、CDAB 或 DCBA 源字节序名称。
    /// </summary>
    private static ModbusByteOrder ReadOrder(object value, string functionName)
    {
        if (value is not string text)
            throw new InvalidOperationException($"函数 {functionName} 的 byte_order 必须是字符串。");

        return text.Trim().ToUpperInvariant() switch
        {
            "ABCD" => ModbusByteOrder.Abcd,
            "BADC" => ModbusByteOrder.Badc,
            "CDAB" => ModbusByteOrder.Cdab,
            "DCBA" => ModbusByteOrder.Dcba,
            _ => throw new InvalidOperationException(
                $"函数 {functionName} 的 byte_order 仅支持 ABCD、BADC、CDAB、DCBA。"),
        };
    }

    /// <summary>
    /// Modbus 32 位值在两只寄存器中的四种常见字节排列。
    /// </summary>
    private enum ModbusByteOrder
    {
        Abcd,
        Badc,
        Cdab,
        Dcba,
    }
}
