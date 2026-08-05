using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace SonnetDB.Modbus;

/// <summary>
/// 提供 Modbus 寄存器值的安全编解码与缩放转换。
/// </summary>
public static class ModbusValueCodec
{
    /// <summary>
    /// 返回指定逻辑值占用的线圈、离散输入或寄存器数量。
    /// </summary>
    /// <param name="valueType">逻辑值类型。</param>
    /// <param name="stringLength">STRING 的固定 ASCII 字节数。</param>
    /// <returns>值占用的地址数量。</returns>
    public static int GetRegisterCount(ModbusValueType valueType, int stringLength = 0)
    {
        if (!Enum.IsDefined(valueType))
            throw new ArgumentOutOfRangeException(nameof(valueType), valueType, "未知的 Modbus 值类型。");

        if (valueType == ModbusValueType.String)
        {
            if (stringLength <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stringLength),
                    stringLength,
                    "STRING 长度必须大于 0。");
            }

            long registerCount = ((long)stringLength + 1) / 2;
            if (registerCount > 65_536)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stringLength),
                    stringLength,
                    "STRING 最多可占用 65536 个寄存器。");
            }

            return (int)registerCount;
        }

        if (stringLength != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stringLength),
                stringLength,
                "仅 STRING 类型允许指定字符串长度。");
        }

        return valueType switch
        {
            ModbusValueType.Bit or ModbusValueType.Int16 or ModbusValueType.UInt16
                or ModbusValueType.Bcd16 => 1,
            ModbusValueType.Int32 or ModbusValueType.UInt32 or ModbusValueType.Float32
                or ModbusValueType.Bcd32 => 2,
            ModbusValueType.Float64 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(valueType)),
        };
    }

    /// <summary>
    /// 从 Modbus 地址值解码一个逻辑值，并应用 <c>raw * scale + offset</c> 转换。
    /// </summary>
    /// <param name="registers">按设备顺序排列的线圈、离散输入或寄存器值。</param>
    /// <param name="area">值所属的 Modbus 地址空间。</param>
    /// <param name="valueType">要解码的逻辑值类型。</param>
    /// <param name="stringLength">STRING 的固定 ASCII 字节数。</param>
    /// <param name="bitIndex">寄存器 BIT 的比特索引。</param>
    /// <param name="byteOrder">单个寄存器内的字节顺序。</param>
    /// <param name="wordOrder">多寄存器值的字顺序。</param>
    /// <param name="scale">原始值的缩放乘数。</param>
    /// <param name="offset">缩放后的偏移量。</param>
    /// <returns>与关系表列类型兼容的布尔值、Int64、Float64 或字符串。</returns>
    public static object Decode(
        ReadOnlySpan<ushort> registers,
        ModbusRegisterArea area,
        ModbusValueType valueType,
        int stringLength = 0,
        int bitIndex = 0,
        ModbusByteOrder byteOrder = ModbusByteOrder.BigEndian,
        ModbusWordOrder wordOrder = ModbusWordOrder.BigEndian,
        decimal scale = 1m,
        decimal offset = 0m)
    {
        ValidateCodecOptions(area, valueType, stringLength, bitIndex, byteOrder, wordOrder, scale, offset);
        int registerCount = GetRegisterCount(valueType, stringLength);
        if (registers.Length < registerCount)
        {
            throw new ArgumentException(
                $"解码 {valueType} 至少需要 {registerCount} 个地址值，实际仅有 {registers.Length} 个。",
                nameof(registers));
        }

        if (valueType == ModbusValueType.Bit)
        {
            ushort bitValue = registers[0];
            // 寄存器 BIT 仍遵循寄存器内字节序；线圈与离散输入保持协议的 0/1 语义。
            if (area is ModbusRegisterArea.HoldingRegister or ModbusRegisterArea.InputRegister
                && byteOrder == ModbusByteOrder.LittleEndian)
            {
                bitValue = BinaryPrimitives.ReverseEndianness(bitValue);
            }

            return DecodeBit(bitValue, area, bitIndex);
        }
        if (valueType == ModbusValueType.String)
            return DecodeString(registers, stringLength, byteOrder, wordOrder);

        Span<byte> canonicalBytes = stackalloc byte[8];
        int byteCount = registerCount * sizeof(ushort);
        CopyToCanonicalBytes(
            registers[..registerCount],
            canonicalBytes[..byteCount],
            byteOrder,
            wordOrder);

        return valueType switch
        {
            ModbusValueType.Int16 => ApplyIntegerTransform(
                BinaryPrimitives.ReadInt16BigEndian(canonicalBytes), scale, offset),
            ModbusValueType.UInt16 => ApplyIntegerTransform(
                BinaryPrimitives.ReadUInt16BigEndian(canonicalBytes), scale, offset),
            ModbusValueType.Int32 => ApplyIntegerTransform(
                BinaryPrimitives.ReadInt32BigEndian(canonicalBytes), scale, offset),
            ModbusValueType.UInt32 => ApplyIntegerTransform(
                BinaryPrimitives.ReadUInt32BigEndian(canonicalBytes), scale, offset),
            ModbusValueType.Float32 => ApplyFloatingTransform(
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(canonicalBytes)),
                scale,
                offset),
            ModbusValueType.Float64 => ApplyFloatingTransform(
                BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(canonicalBytes)),
                scale,
                offset),
            ModbusValueType.Bcd16 => ApplyIntegerTransform(
                DecodeBcd(BinaryPrimitives.ReadUInt16BigEndian(canonicalBytes), 4), scale, offset),
            ModbusValueType.Bcd32 => ApplyIntegerTransform(
                DecodeBcd(BinaryPrimitives.ReadUInt32BigEndian(canonicalBytes), 8), scale, offset),
            _ => throw new ArgumentOutOfRangeException(nameof(valueType)),
        };
    }

    /// <summary>
    /// 将逻辑值逆缩放后编码到 Modbus 地址缓冲区。
    /// </summary>
    /// <param name="value">要编码的逻辑值。</param>
    /// <param name="destination">接收线圈或寄存器值的目标缓冲区。</param>
    /// <param name="area">值所属的 Modbus 地址空间。</param>
    /// <param name="valueType">要编码的逻辑值类型。</param>
    /// <param name="stringLength">STRING 的固定 ASCII 字节数。</param>
    /// <param name="bitIndex">保留的寄存器 BIT 参数；写寄存器 BIT 不受支持。</param>
    /// <param name="byteOrder">单个寄存器内的字节顺序。</param>
    /// <param name="wordOrder">多寄存器值的字顺序。</param>
    /// <param name="scale">原始值的缩放乘数。</param>
    /// <param name="offset">缩放后的偏移量。</param>
    /// <returns>写入目标缓冲区的地址数量。</returns>
    public static int Encode(
        object value,
        Span<ushort> destination,
        ModbusRegisterArea area,
        ModbusValueType valueType,
        int stringLength = 0,
        int bitIndex = 0,
        ModbusByteOrder byteOrder = ModbusByteOrder.BigEndian,
        ModbusWordOrder wordOrder = ModbusWordOrder.BigEndian,
        decimal scale = 1m,
        decimal offset = 0m)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateCodecOptions(area, valueType, stringLength, bitIndex, byteOrder, wordOrder, scale, offset);

        int registerCount = GetRegisterCount(valueType, stringLength);
        if (destination.Length < registerCount)
        {
            throw new ArgumentException(
                $"编码 {valueType} 至少需要 {registerCount} 个目标地址，实际仅有 {destination.Length} 个。",
                nameof(destination));
        }

        if (valueType == ModbusValueType.Bit)
        {
            EncodeBit(value, destination, area);
            return 1;
        }

        if (valueType == ModbusValueType.String)
        {
            EncodeString(value, destination, stringLength, byteOrder, wordOrder);
            return registerCount;
        }

        Span<byte> canonicalBytes = stackalloc byte[8];
        int byteCount = registerCount * sizeof(ushort);
        EncodeNumericValue(value, canonicalBytes[..byteCount], valueType, scale, offset);
        CopyFromCanonicalBytes(
            canonicalBytes[..byteCount],
            destination[..registerCount],
            byteOrder,
            wordOrder);
        return registerCount;
    }

    /// <summary>
    /// 校验编解码参数组合和地址空间约束。
    /// </summary>
    private static void ValidateCodecOptions(
        ModbusRegisterArea area,
        ModbusValueType valueType,
        int stringLength,
        int bitIndex,
        ModbusByteOrder byteOrder,
        ModbusWordOrder wordOrder,
        decimal scale,
        decimal offset)
    {
        if (!Enum.IsDefined(area))
            throw new ArgumentOutOfRangeException(nameof(area), area, "未知的 Modbus 地址空间。");
        if (!Enum.IsDefined(valueType))
            throw new ArgumentOutOfRangeException(nameof(valueType), valueType, "未知的 Modbus 值类型。");
        if (!Enum.IsDefined(byteOrder))
            throw new ArgumentOutOfRangeException(nameof(byteOrder), byteOrder, "未知的 Modbus 字节序。");
        if (!Enum.IsDefined(wordOrder))
            throw new ArgumentOutOfRangeException(nameof(wordOrder), wordOrder, "未知的 Modbus 字序。");
        if (scale == 0m)
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "缩放乘数不能为 0。");

        bool bitArea = area is ModbusRegisterArea.Coil or ModbusRegisterArea.DiscreteInput;
        if (bitArea && valueType != ModbusValueType.Bit)
            throw new ArgumentException("线圈和离散输入只能使用 BIT 类型。", nameof(valueType));

        if (valueType == ModbusValueType.Bit)
        {
            if (bitArea && bitIndex != 0)
                throw new ArgumentOutOfRangeException(nameof(bitIndex), bitIndex, "线圈和离散输入不使用比特索引。");
            if (!bitArea && bitIndex is < 0 or > 15)
                throw new ArgumentOutOfRangeException(nameof(bitIndex), bitIndex, "寄存器比特索引必须位于 0..15。");
            if (scale != 1m || offset != 0m)
                throw new ArgumentException("BIT 类型不支持缩放或偏移。", nameof(scale));
        }
        else if (bitIndex != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitIndex), bitIndex, "非 BIT 类型不允许指定比特索引。");
        }

        if (valueType == ModbusValueType.String && (scale != 1m || offset != 0m))
            throw new ArgumentException("STRING 类型不支持缩放或偏移。", nameof(scale));

        _ = GetRegisterCount(valueType, stringLength);
    }

    /// <summary>
    /// 解码线圈、离散输入或寄存器内的单个比特。
    /// </summary>
    private static bool DecodeBit(ushort value, ModbusRegisterArea area, int bitIndex)
    {
        if (area is ModbusRegisterArea.Coil or ModbusRegisterArea.DiscreteInput)
        {
            if (value > 1)
                throw new InvalidDataException("Modbus 线圈或离散输入值必须为 0 或 1。");
            return value == 1;
        }

        return (value & (1 << bitIndex)) != 0;
    }

    /// <summary>
    /// 把布尔值编码为线圈或离散输入读响应使用的规范 0/1。
    /// </summary>
    private static void EncodeBit(object value, Span<ushort> destination, ModbusRegisterArea area)
    {
        if (area is not ModbusRegisterArea.Coil and not ModbusRegisterArea.DiscreteInput)
        {
            throw new ArgumentException(
                "BIT 直接编码仅支持线圈或离散输入；寄存器 BIT 必须通过受控读改写流程处理。",
                nameof(area));
        }
        if (value is not bool bitValue)
            throw new ArgumentException("BIT 类型只接受 Boolean 值。", nameof(value));

        destination[0] = bitValue ? (ushort)1 : (ushort)0;
    }

    /// <summary>
    /// 解码定长 ASCII 字符串，并校验 NUL 后仅包含填充字节。
    /// </summary>
    private static string DecodeString(
        ReadOnlySpan<ushort> registers,
        int stringLength,
        ModbusByteOrder byteOrder,
        ModbusWordOrder wordOrder)
    {
        int registerCount = GetRegisterCount(ModbusValueType.String, stringLength);
        byte[] buffer = new byte[registerCount * sizeof(ushort)];
        CopyToCanonicalBytes(registers[..registerCount], buffer, byteOrder, wordOrder);

        int textLength = stringLength;
        bool paddingStarted = false;
        for (int i = 0; i < stringLength; i++)
        {
            byte current = buffer[i];
            if (current == 0)
            {
                if (!paddingStarted)
                {
                    textLength = i;
                    paddingStarted = true;
                }

                continue;
            }

            if (paddingStarted)
                throw new InvalidDataException("Modbus STRING 的 NUL 填充后包含非零字节。");
            if (current > 0x7F)
                throw new InvalidDataException("Modbus STRING 包含非 ASCII 字节。");
        }

        for (int i = stringLength; i < buffer.Length; i++)
        {
            if (buffer[i] != 0)
                throw new InvalidDataException("Modbus STRING 最后一个寄存器的物理填充字节必须为 NUL。");
        }

        return Encoding.ASCII.GetString(buffer.AsSpan(0, textLength));
    }

    /// <summary>
    /// 编码定长 ASCII 字符串并用 NUL 填满剩余空间。
    /// </summary>
    private static void EncodeString(
        object value,
        Span<ushort> destination,
        int stringLength,
        ModbusByteOrder byteOrder,
        ModbusWordOrder wordOrder)
    {
        if (value is not string text)
            throw new ArgumentException("STRING 类型只接受字符串值。", nameof(value));
        if (text.Length > stringLength)
        {
            throw new ArgumentException(
                $"字符串包含 {text.Length} 个 ASCII 字节，超过固定长度 {stringLength}。",
                nameof(value));
        }

        int registerCount = GetRegisterCount(ModbusValueType.String, stringLength);
        byte[] buffer = new byte[registerCount * sizeof(ushort)];
        for (int i = 0; i < text.Length; i++)
        {
            char current = text[i];
            if (current is '\0' or > '\x7F')
                throw new ArgumentException("Modbus STRING 只接受不含 NUL 的 ASCII 字符。", nameof(value));
            buffer[i] = (byte)current;
        }

        CopyFromCanonicalBytes(buffer, destination[..registerCount], byteOrder, wordOrder);
    }

    /// <summary>
    /// 把设备顺序的寄存器转换为大端字节和高字在前的规范布局。
    /// </summary>
    private static void CopyToCanonicalBytes(
        ReadOnlySpan<ushort> registers,
        Span<byte> destination,
        ModbusByteOrder byteOrder,
        ModbusWordOrder wordOrder)
    {
        for (int canonicalWordIndex = 0; canonicalWordIndex < registers.Length; canonicalWordIndex++)
        {
            int sourceWordIndex = wordOrder == ModbusWordOrder.BigEndian
                ? canonicalWordIndex
                : registers.Length - canonicalWordIndex - 1;
            ushort word = registers[sourceWordIndex];
            Span<byte> wordBytes = destination.Slice(canonicalWordIndex * sizeof(ushort), sizeof(ushort));
            if (byteOrder == ModbusByteOrder.BigEndian)
                BinaryPrimitives.WriteUInt16BigEndian(wordBytes, word);
            else
                BinaryPrimitives.WriteUInt16LittleEndian(wordBytes, word);
        }
    }

    /// <summary>
    /// 把规范大端字节转换为设备要求的寄存器字节序和字序。
    /// </summary>
    private static void CopyFromCanonicalBytes(
        ReadOnlySpan<byte> canonicalBytes,
        Span<ushort> destination,
        ModbusByteOrder byteOrder,
        ModbusWordOrder wordOrder)
    {
        for (int destinationWordIndex = 0; destinationWordIndex < destination.Length; destinationWordIndex++)
        {
            int canonicalWordIndex = wordOrder == ModbusWordOrder.BigEndian
                ? destinationWordIndex
                : destination.Length - destinationWordIndex - 1;
            ReadOnlySpan<byte> wordBytes = canonicalBytes.Slice(
                canonicalWordIndex * sizeof(ushort),
                sizeof(ushort));
            destination[destinationWordIndex] = byteOrder == ModbusByteOrder.BigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(wordBytes)
                : BinaryPrimitives.ReadUInt16LittleEndian(wordBytes);
        }
    }

    /// <summary>
    /// 将原始整数缩放为关系表兼容的 Int64 或 Float64。
    /// </summary>
    private static object ApplyIntegerTransform(long rawValue, decimal scale, decimal offset)
    {
        if (scale == 1m && offset == 0m)
            return rawValue;

        decimal transformed = checked((rawValue * scale) + offset);
        return (double)transformed;
    }

    /// <summary>
    /// 将原始浮点数缩放为有限的 Float64。
    /// </summary>
    private static double ApplyFloatingTransform(double rawValue, decimal scale, decimal offset)
    {
        if (!double.IsFinite(rawValue))
            throw new InvalidDataException("Modbus 浮点寄存器包含 NaN 或 Infinity。");

        double transformed = (rawValue * (double)scale) + (double)offset;
        if (!double.IsFinite(transformed))
            throw new OverflowException("缩放后的 Modbus 浮点值超出有限 Float64 范围。");
        return transformed;
    }

    /// <summary>
    /// 校验 BCD 的每个半字节并解码为十进制整数。
    /// </summary>
    private static long DecodeBcd(uint bits, int digitCount)
    {
        long result = 0;
        for (int index = digitCount - 1; index >= 0; index--)
        {
            uint digit = (bits >> (index * 4)) & 0xF;
            if (digit > 9)
                throw new InvalidDataException("Modbus BCD 值包含大于 9 的半字节。");
            result = (result * 10) + digit;
        }

        return result;
    }

    /// <summary>
    /// 根据目标类型把逻辑数值逆缩放并写入规范大端字节。
    /// </summary>
    private static void EncodeNumericValue(
        object value,
        Span<byte> destination,
        ModbusValueType valueType,
        decimal scale,
        decimal offset)
    {
        switch (valueType)
        {
            case ModbusValueType.Int16:
                BinaryPrimitives.WriteInt16BigEndian(
                    destination,
                    checked((short)ReadInverseScaledInteger(value, scale, offset, short.MinValue, short.MaxValue)));
                break;
            case ModbusValueType.UInt16:
                BinaryPrimitives.WriteUInt16BigEndian(
                    destination,
                    checked((ushort)ReadInverseScaledInteger(value, scale, offset, ushort.MinValue, ushort.MaxValue)));
                break;
            case ModbusValueType.Int32:
                BinaryPrimitives.WriteInt32BigEndian(
                    destination,
                    checked((int)ReadInverseScaledInteger(value, scale, offset, int.MinValue, int.MaxValue)));
                break;
            case ModbusValueType.UInt32:
                BinaryPrimitives.WriteUInt32BigEndian(
                    destination,
                    checked((uint)ReadInverseScaledInteger(value, scale, offset, uint.MinValue, uint.MaxValue)));
                break;
            case ModbusValueType.Float32:
                WriteFloat32(value, destination, scale, offset);
                break;
            case ModbusValueType.Float64:
                WriteFloat64(value, destination, scale, offset);
                break;
            case ModbusValueType.Bcd16:
                BinaryPrimitives.WriteUInt16BigEndian(
                    destination,
                    checked((ushort)EncodeBcd(ReadInverseScaledInteger(value, scale, offset, 0, 9_999), 4)));
                break;
            case ModbusValueType.Bcd32:
                BinaryPrimitives.WriteUInt32BigEndian(
                    destination,
                    EncodeBcd(ReadInverseScaledInteger(value, scale, offset, 0, 99_999_999), 8));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(valueType), valueType, "该类型不是可编码的数值类型。");
        }
    }

    /// <summary>
    /// 严格执行整数逆缩放，并拒绝小数结果与目标类型溢出。
    /// </summary>
    private static long ReadInverseScaledInteger(
        object value,
        decimal scale,
        decimal offset,
        long minimum,
        long maximum)
    {
        decimal logicalValue = ReadDecimal(value);
        decimal rawValue = checked((logicalValue - offset) / scale);
        if (decimal.Truncate(rawValue) != rawValue)
            throw new ArgumentException("逆缩放结果不是整数，编码不会执行隐式舍入。", nameof(value));
        if (rawValue < minimum || rawValue > maximum)
            throw new OverflowException($"逆缩放结果 {rawValue.ToString(CultureInfo.InvariantCulture)} 超出目标类型范围。");

        return decimal.ToInt64(rawValue);
    }

    /// <summary>
    /// 将受支持的数值对象严格转换为有限 Decimal。
    /// </summary>
    private static decimal ReadDecimal(object value)
    {
        try
        {
            return value switch
            {
                byte numeric => numeric,
                sbyte numeric => numeric,
                short numeric => numeric,
                ushort numeric => numeric,
                int numeric => numeric,
                uint numeric => numeric,
                long numeric => numeric,
                ulong numeric => numeric,
                decimal numeric => numeric,
                float numeric when float.IsFinite(numeric) => ConvertFloatingToDecimalExact(numeric),
                double numeric when double.IsFinite(numeric) => ConvertFloatingToDecimalExact(numeric),
                float or double => throw new ArgumentOutOfRangeException(nameof(value), "数值不能是 NaN 或 Infinity。"),
                _ => throw new ArgumentException("Modbus 数值类型只接受 CLR 数值。", nameof(value)),
            };
        }
        catch (OverflowException ex)
        {
            throw new OverflowException("数值超出 Decimal 可表示范围。", ex);
        }
    }

    /// <summary>
    /// 把单精度值无损转换为 Decimal；转换回原类型不一致时拒绝隐式舍入。
    /// </summary>
    private static decimal ConvertFloatingToDecimalExact(float value)
    {
        decimal converted = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        if ((float)converted != value)
            throw new ArgumentException("浮点数不能无损转换为 Decimal，整数 wire type 不执行隐式舍入。", nameof(value));
        return converted;
    }

    /// <summary>
    /// 把双精度值无损转换为 Decimal；转换回原类型不一致时拒绝隐式舍入。
    /// </summary>
    private static decimal ConvertFloatingToDecimalExact(double value)
    {
        decimal converted = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        if ((double)converted != value)
            throw new ArgumentException("浮点数不能无损转换为 Decimal，整数 wire type 不执行隐式舍入。", nameof(value));
        return converted;
    }

    /// <summary>
    /// 将受支持的数值对象转换为有限 Float64。
    /// </summary>
    private static double ReadDouble(object value)
    {
        double result = value switch
        {
            byte numeric => numeric,
            sbyte numeric => numeric,
            short numeric => numeric,
            ushort numeric => numeric,
            int numeric => numeric,
            uint numeric => numeric,
            long numeric => ConvertIntegerToDoubleExact(numeric),
            ulong numeric => ConvertIntegerToDoubleExact(numeric),
            float numeric => numeric,
            double numeric => numeric,
            decimal numeric => ConvertDecimalToDoubleExact(numeric),
            _ => throw new ArgumentException("Modbus 数值类型只接受 CLR 数值。", nameof(value)),
        };

        if (!double.IsFinite(result))
            throw new ArgumentOutOfRangeException(nameof(value), "数值不能是 NaN 或 Infinity。");
        return result;
    }

    /// <summary>
    /// 把 Int64 无损转换为 Float64；回算不一致时拒绝静默舍入。
    /// </summary>
    private static double ConvertIntegerToDoubleExact(long value)
    {
        double converted = value;
        try
        {
            if (checked((long)converted) != value)
                throw new ArgumentException("整数不能无损转换为 Float64，浮点 wire type 不执行隐式舍入。", nameof(value));
        }
        catch (OverflowException ex)
        {
            throw new ArgumentException("整数不能无损转换为 Float64，浮点 wire type 不执行隐式舍入。", nameof(value), ex);
        }

        return converted;
    }

    /// <summary>
    /// 把 UInt64 无损转换为 Float64；回算不一致时拒绝静默舍入。
    /// </summary>
    private static double ConvertIntegerToDoubleExact(ulong value)
    {
        double converted = value;
        try
        {
            if (checked((ulong)converted) != value)
                throw new ArgumentException("整数不能无损转换为 Float64，浮点 wire type 不执行隐式舍入。", nameof(value));
        }
        catch (OverflowException ex)
        {
            throw new ArgumentException("整数不能无损转换为 Float64，浮点 wire type 不执行隐式舍入。", nameof(value), ex);
        }

        return converted;
    }

    /// <summary>
    /// 把 Decimal 无损转换为 Float64；二进制有理数与十进制原值不完全相等时拒绝静默舍入。
    /// </summary>
    private static double ConvertDecimalToDoubleExact(decimal value)
    {
        double converted = (double)value;
        if (!IsDecimalExactlyRepresentedByDouble(value, converted))
        {
            throw new ArgumentException(
                "Decimal 不能无损转换为 Float64，浮点 wire type 不执行隐式舍入。",
                nameof(value));
        }

        return converted;
    }

    /// <summary>按两个值的精确有理数形式判断 Decimal 是否能由当前 binary64 位模式无损表示。</summary>
    private static bool IsDecimalExactlyRepresentedByDouble(decimal value, double converted)
    {
        int[] decimalBits = decimal.GetBits(value);
        BigInteger decimalNumerator = (uint)decimalBits[0];
        decimalNumerator += (BigInteger)(uint)decimalBits[1] << 32;
        decimalNumerator += (BigInteger)(uint)decimalBits[2] << 64;
        if (decimalNumerator.IsZero)
            return converted == 0d;

        ulong doubleBits = (ulong)BitConverter.DoubleToInt64Bits(converted);
        bool decimalNegative = (decimalBits[3] & int.MinValue) != 0;
        bool doubleNegative = (doubleBits & (1UL << 63)) != 0;
        if (decimalNegative != doubleNegative)
            return false;

        const ulong FractionMask = (1UL << 52) - 1;
        int exponentBits = (int)((doubleBits >> 52) & 0x7FFUL);
        ulong significand = doubleBits & FractionMask;
        int binaryExponent;
        if (exponentBits == 0)
        {
            binaryExponent = -1074;
        }
        else
        {
            significand |= 1UL << 52;
            binaryExponent = exponentBits - 1023 - 52;
        }

        BigInteger doubleNumerator = significand;
        BigInteger doubleDenominator = BigInteger.One;
        if (binaryExponent >= 0)
            doubleNumerator <<= binaryExponent;
        else
            doubleDenominator <<= -binaryExponent;

        int decimalScale = (decimalBits[3] >> 16) & 0xFF;
        BigInteger decimalDenominator = BigInteger.Pow(10, decimalScale);
        return decimalNumerator * doubleDenominator == doubleNumerator * decimalDenominator;
    }

    /// <summary>
    /// 逆缩放并写入 IEEE 754 单精度位模式。
    /// </summary>
    private static void WriteFloat32(object value, Span<byte> destination, decimal scale, decimal offset)
    {
        double logicalValue = ReadDouble(value);
        double rawValue = ApplyInverseFloatingTransform(logicalValue, scale, offset);
        float narrowed = (float)rawValue;
        if (!float.IsFinite(narrowed))
            throw new OverflowException("逆缩放结果超出有限 Float32 范围。");

        double roundTrip = ApplyFloatingTransform(narrowed, scale, offset);
        if (roundTrip != logicalValue)
            throw new ArgumentException("Float32 编码会造成精度损失，已拒绝写入。", nameof(value));

        BinaryPrimitives.WriteInt32BigEndian(destination, BitConverter.SingleToInt32Bits(narrowed));
    }

    /// <summary>
    /// 逆缩放并写入 IEEE 754 双精度位模式。
    /// </summary>
    private static void WriteFloat64(object value, Span<byte> destination, decimal scale, decimal offset)
    {
        double logicalValue = ReadDouble(value);
        double rawValue = ApplyInverseFloatingTransform(logicalValue, scale, offset);
        double roundTrip = ApplyFloatingTransform(rawValue, scale, offset);
        if (roundTrip != logicalValue)
            throw new ArgumentException("Float64 编码会造成精度损失，已拒绝写入。", nameof(value));

        BinaryPrimitives.WriteInt64BigEndian(destination, BitConverter.DoubleToInt64Bits(rawValue));
    }

    /// <summary>
    /// 对有限浮点值执行 <c>(value - offset) / scale</c> 逆转换。
    /// </summary>
    private static double ApplyInverseFloatingTransform(double value, decimal scale, decimal offset)
    {
        double rawValue = (value - (double)offset) / (double)scale;
        if (!double.IsFinite(rawValue))
            throw new OverflowException("逆缩放后的 Modbus 浮点值超出有限 Float64 范围。");
        return rawValue;
    }

    /// <summary>
    /// 将非负十进制整数编码为指定宽度的 BCD 半字节。
    /// </summary>
    private static uint EncodeBcd(long value, int digitCount)
    {
        uint result = 0;
        for (int index = 0; index < digitCount; index++)
        {
            uint digit = (uint)(value % 10);
            result |= digit << (index * 4);
            value /= 10;
        }

        return result;
    }
}
