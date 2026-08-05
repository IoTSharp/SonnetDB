using SonnetDB.Modbus;
using Xunit;

namespace SonnetDB.Core.Tests.Modbus;

public sealed class ModbusValueCodecTests
{
    /// <summary>
    /// 验证所有非字符串 wire type 的固定地址数量。
    /// </summary>
    [Theory]
    [InlineData(ModbusValueType.Bit, 1)]
    [InlineData(ModbusValueType.Int16, 1)]
    [InlineData(ModbusValueType.UInt16, 1)]
    [InlineData(ModbusValueType.Int32, 2)]
    [InlineData(ModbusValueType.UInt32, 2)]
    [InlineData(ModbusValueType.Float32, 2)]
    [InlineData(ModbusValueType.Float64, 4)]
    [InlineData(ModbusValueType.Bcd16, 1)]
    [InlineData(ModbusValueType.Bcd32, 2)]
    public void GetRegisterCount_FixedWidthType_ReturnsContractCount(
        ModbusValueType valueType,
        int expected)
    {
        Assert.Equal(expected, ModbusValueCodec.GetRegisterCount(valueType));
    }

    /// <summary>
    /// 验证奇偶 STRING 字节长度均按向上取整占用寄存器。
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    public void GetRegisterCount_StringLength_RoundsUpToRegister(int stringLength, int expected)
    {
        Assert.Equal(expected, ModbusValueCodec.GetRegisterCount(ModbusValueType.String, stringLength));
    }

    /// <summary>
    /// 验证线圈和离散输入只接受 0/1，并返回布尔语义。
    /// </summary>
    [Theory]
    [InlineData(ModbusRegisterArea.Coil, 0, false)]
    [InlineData(ModbusRegisterArea.Coil, 1, true)]
    [InlineData(ModbusRegisterArea.DiscreteInput, 0, false)]
    [InlineData(ModbusRegisterArea.DiscreteInput, 1, true)]
    public void Decode_BitAreaValue_ReturnsBoolean(
        ModbusRegisterArea area,
        ushort raw,
        bool expected)
    {
        object actual = ModbusValueCodec.Decode([raw], area, ModbusValueType.Bit);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// 验证寄存器 BIT 解码使用指定的 0 与 15 位边界。
    /// </summary>
    [Theory]
    [InlineData(ModbusRegisterArea.HoldingRegister, 0x0001, 0, true)]
    [InlineData(ModbusRegisterArea.HoldingRegister, 0x0002, 0, false)]
    [InlineData(ModbusRegisterArea.InputRegister, 0x8000, 15, true)]
    [InlineData(ModbusRegisterArea.InputRegister, 0x7FFF, 15, false)]
    public void Decode_RegisterBit_ReturnsSelectedBit(
        ModbusRegisterArea area,
        ushort raw,
        int bitIndex,
        bool expected)
    {
        object actual = ModbusValueCodec.Decode(
            [raw],
            area,
            ModbusValueType.Bit,
            bitIndex: bitIndex);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// 验证寄存器 BIT 遵循单寄存器字节序，而线圈值不受字节序设置影响。
    /// </summary>
    [Theory]
    [InlineData(ModbusRegisterArea.HoldingRegister, 0x0100, 0, true)]
    [InlineData(ModbusRegisterArea.InputRegister, 0x0080, 15, true)]
    [InlineData(ModbusRegisterArea.Coil, 0x0001, 0, true)]
    public void Decode_BitWithLittleEndian_AppliesByteOrderForRegistersOnly(
        ModbusRegisterArea area,
        ushort raw,
        int bitIndex,
        bool expected)
    {
        object actual = ModbusValueCodec.Decode(
            [raw],
            area,
            ModbusValueType.Bit,
            bitIndex: bitIndex,
            byteOrder: ModbusByteOrder.LittleEndian);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// 验证 BIT 编码为 Coil 与 Discrete Input 读响应写入规范的 0/1。
    /// </summary>
    [Theory]
    [InlineData(ModbusRegisterArea.Coil, false, 0)]
    [InlineData(ModbusRegisterArea.Coil, true, 1)]
    [InlineData(ModbusRegisterArea.DiscreteInput, false, 0)]
    [InlineData(ModbusRegisterArea.DiscreteInput, true, 1)]
    public void Encode_BitAreaValue_WritesCanonicalValue(
        ModbusRegisterArea area,
        bool value,
        ushort expected)
    {
        var destination = new ushort[1];

        int written = ModbusValueCodec.Encode(
            value,
            destination,
            area,
            ModbusValueType.Bit);

        Assert.Equal(1, written);
        Assert.Equal(expected, destination[0]);
    }

    /// <summary>
    /// 验证非法 bit wire 值以及寄存器 bit 写入都会被拒绝。
    /// </summary>
    [Fact]
    public void Bit_InvalidWireOrRegisterWrite_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ModbusValueCodec.Decode(
            [2],
            ModbusRegisterArea.Coil,
            ModbusValueType.Bit));
        Assert.Throws<ArgumentException>(() => ModbusValueCodec.Encode(
            true,
            new ushort[1],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Bit,
            bitIndex: 3));
    }

    /// <summary>
    /// 验证全部整数 wire type 可在其边界值上无损往返。
    /// </summary>
    [Theory]
    [InlineData(ModbusValueType.Int16, -32_768L, 1)]
    [InlineData(ModbusValueType.Int16, 32_767L, 1)]
    [InlineData(ModbusValueType.UInt16, 0L, 1)]
    [InlineData(ModbusValueType.UInt16, 65_535L, 1)]
    [InlineData(ModbusValueType.Int32, -2_147_483_648L, 2)]
    [InlineData(ModbusValueType.Int32, 2_147_483_647L, 2)]
    [InlineData(ModbusValueType.UInt32, 0L, 2)]
    [InlineData(ModbusValueType.UInt32, 4_294_967_295L, 2)]
    public void EncodeDecode_IntegerBoundary_RoundTrips(
        ModbusValueType valueType,
        long value,
        int registerCount)
    {
        var registers = new ushort[registerCount];

        Assert.Equal(registerCount, ModbusValueCodec.Encode(
            value,
            registers,
            ModbusRegisterArea.HoldingRegister,
            valueType));
        object decoded = ModbusValueCodec.Decode(
            registers,
            ModbusRegisterArea.HoldingRegister,
            valueType);

        Assert.Equal(value, decoded);
    }

    /// <summary>
    /// 验证 32 位规范字节在四种 byte/word 组合下产生 ABCD/BADC/CDAB/DCBA 布局。
    /// </summary>
    [Theory]
    [InlineData(ModbusByteOrder.BigEndian, ModbusWordOrder.BigEndian, 0x1234, 0x5678)]
    [InlineData(ModbusByteOrder.LittleEndian, ModbusWordOrder.BigEndian, 0x3412, 0x7856)]
    [InlineData(ModbusByteOrder.BigEndian, ModbusWordOrder.LittleEndian, 0x5678, 0x1234)]
    [InlineData(ModbusByteOrder.LittleEndian, ModbusWordOrder.LittleEndian, 0x7856, 0x3412)]
    public void EncodeDecode_UInt32ByteAndWordOrder_MatchesKnownLayout(
        ModbusByteOrder byteOrder,
        ModbusWordOrder wordOrder,
        int first,
        int second)
    {
        var registers = new ushort[2];

        ModbusValueCodec.Encode(
            0x1234_5678L,
            registers,
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.UInt32,
            byteOrder: byteOrder,
            wordOrder: wordOrder);

        Assert.Equal([(ushort)first, (ushort)second], registers);
        Assert.Equal(0x1234_5678L, ModbusValueCodec.Decode(
            registers,
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.UInt32,
            byteOrder: byteOrder,
            wordOrder: wordOrder));
    }

    /// <summary>
    /// 验证 Float32 与 Float64 的有限值可按各自精度无损往返。
    /// </summary>
    [Fact]
    public void EncodeDecode_FiniteFloatingValues_RoundTrips()
    {
        var float32Registers = new ushort[2];
        ModbusValueCodec.Encode(
            1.5d,
            float32Registers,
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Float32,
            byteOrder: ModbusByteOrder.LittleEndian,
            wordOrder: ModbusWordOrder.LittleEndian);
        Assert.Equal(1.5d, ModbusValueCodec.Decode(
            float32Registers,
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Float32,
            byteOrder: ModbusByteOrder.LittleEndian,
            wordOrder: ModbusWordOrder.LittleEndian));

        var float64Registers = new ushort[4];
        ModbusValueCodec.Encode(
            Math.PI,
            float64Registers,
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Float64,
            wordOrder: ModbusWordOrder.LittleEndian);
        Assert.Equal(Math.PI, ModbusValueCodec.Decode(
            float64Registers,
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Float64,
            wordOrder: ModbusWordOrder.LittleEndian));
    }

    /// <summary>
    /// 验证整数缩放读取和逆缩放写入使用同一公式并精确恢复 raw 值。
    /// </summary>
    [Theory]
    [InlineData(10.3, 0.1, -2.0, 123)]
    [InlineData(80.0, -2.0, 100.0, 10)]
    public void EncodeDecode_ScaledInteger_AppliesExactInverse(
        double logicalValue,
        double scale,
        double offset,
        int expectedRaw)
    {
        var registers = new ushort[1];

        ModbusValueCodec.Encode(
            Convert.ToDecimal(logicalValue),
            registers,
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Int16,
            scale: Convert.ToDecimal(scale),
            offset: Convert.ToDecimal(offset));
        object decoded = ModbusValueCodec.Decode(
            registers,
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Int16,
            scale: Convert.ToDecimal(scale),
            offset: Convert.ToDecimal(offset));

        Assert.Equal((ushort)expectedRaw, registers[0]);
        Assert.Equal(logicalValue, Assert.IsType<double>(decoded), 10);
    }

    /// <summary>
    /// 验证整数逆缩放拒绝小数 raw 结果、目标范围溢出和零 scale。
    /// </summary>
    [Fact]
    public void Encode_IntegerInverseNotExactOrOutOfRange_Throws()
    {
        Assert.Throws<ArgumentException>(() => ModbusValueCodec.Encode(
            10.35m,
            new ushort[1],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Int16,
            scale: 0.1m,
            offset: -2m));
        Assert.Throws<OverflowException>(() => ModbusValueCodec.Encode(
            65_536L,
            new ushort[1],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.UInt16));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusValueCodec.Encode(
            1L,
            new ushort[1],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Int16,
            scale: 0m));
    }

    /// <summary>
    /// 验证靠近整数的 Float32/Float64 不会在 Decimal 转换时被静默舍入后写入整数 wire type。
    /// </summary>
    [Fact]
    public void Encode_NearIntegerFloatingValue_RejectsDecimalConversionRounding()
    {
        Assert.Throws<ArgumentException>(() => ModbusValueCodec.Encode(
            1.0000000000000002d,
            new ushort[1],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.UInt16));
        Assert.Throws<ArgumentException>(() => ModbusValueCodec.Encode(
            0.9999999999999999d,
            new ushort[1],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.UInt16));
        Assert.Throws<ArgumentException>(() => ModbusValueCodec.Encode(
            1.0000001f,
            new ushort[1],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.UInt16));
    }

    /// <summary>
    /// 验证 BCD16 与 BCD32 的已知 packed BCD 向量可双向转换。
    /// </summary>
    [Fact]
    public void EncodeDecode_BcdKnownVectors_RoundTrips()
    {
        var bcd16 = new ushort[1];
        ModbusValueCodec.Encode(
            1_234L,
            bcd16,
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Bcd16);
        Assert.Equal([0x1234], bcd16);
        Assert.Equal(1_234L, ModbusValueCodec.Decode(
            bcd16,
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Bcd16));

        var bcd32 = new ushort[2];
        ModbusValueCodec.Encode(
            12_345_678L,
            bcd32,
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Bcd32);
        Assert.Equal([0x1234, 0x5678], bcd32);
        Assert.Equal(12_345_678L, ModbusValueCodec.Decode(
            bcd32,
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Bcd32));
    }

    /// <summary>
    /// 验证非法 BCD 半字节、负数和超出位数上限的值均被拒绝。
    /// </summary>
    [Fact]
    public void Bcd_InvalidNibbleOrRange_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ModbusValueCodec.Decode(
            [0x12FA],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Bcd16));
        Assert.Throws<InvalidDataException>(() => ModbusValueCodec.Decode(
            [0x1234, 0x56FA],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Bcd32));
        Assert.Throws<OverflowException>(() => ModbusValueCodec.Encode(
            -1L,
            new ushort[1],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Bcd16));
        Assert.Throws<OverflowException>(() => ModbusValueCodec.Encode(
            100_000_000L,
            new ushort[2],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Bcd32));
    }

    /// <summary>
    /// 验证奇数字节 STRING 在四种顺序组合下保持 ASCII 与物理 NUL padding。
    /// </summary>
    [Theory]
    [InlineData(ModbusByteOrder.BigEndian, ModbusWordOrder.BigEndian, 0x4142, 0x4300)]
    [InlineData(ModbusByteOrder.LittleEndian, ModbusWordOrder.BigEndian, 0x4241, 0x0043)]
    [InlineData(ModbusByteOrder.BigEndian, ModbusWordOrder.LittleEndian, 0x4300, 0x4142)]
    [InlineData(ModbusByteOrder.LittleEndian, ModbusWordOrder.LittleEndian, 0x0043, 0x4241)]
    public void EncodeDecode_OddLengthString_MatchesOrderAndPadding(
        ModbusByteOrder byteOrder,
        ModbusWordOrder wordOrder,
        int first,
        int second)
    {
        var registers = new ushort[2];

        ModbusValueCodec.Encode(
            "ABC",
            registers,
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.String,
            stringLength: 3,
            byteOrder: byteOrder,
            wordOrder: wordOrder);

        Assert.Equal([(ushort)first, (ushort)second], registers);
        Assert.Equal("ABC", ModbusValueCodec.Decode(
            registers,
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.String,
            stringLength: 3,
            byteOrder: byteOrder,
            wordOrder: wordOrder));
    }

    /// <summary>
    /// 验证 STRING 编码拒绝超长、非 ASCII 与嵌入 NUL 的输入。
    /// </summary>
    [Theory]
    [InlineData("ABCDE")]
    [InlineData("中")]
    [InlineData("A\0B")]
    public void Encode_StringInvalidInput_Throws(string value)
    {
        Assert.Throws<ArgumentException>(() => ModbusValueCodec.Encode(
            value,
            new ushort[2],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.String,
            stringLength: 4));
    }

    /// <summary>
    /// 验证 STRING 解码拒绝 NUL 后非零、非 ASCII 和奇数长度物理 padding 非零。
    /// </summary>
    [Theory]
    [InlineData(0x4100, 0x4200, 4)]
    [InlineData(0x4180, 0x0000, 4)]
    [InlineData(0x4142, 0x437F, 3)]
    public void Decode_StringInvalidWirePadding_Throws(int first, int second, int stringLength)
    {
        Assert.Throws<InvalidDataException>(() => ModbusValueCodec.Decode(
            [(ushort)first, (ushort)second],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.String,
            stringLength: stringLength));
    }

    /// <summary>
    /// 验证浮点解码拒绝线上 NaN 和 Infinity 位模式。
    /// </summary>
    [Fact]
    public void Decode_FloatingNaNOrInfinity_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ModbusValueCodec.Decode(
            [0x7FC0, 0x0000],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Float32));
        Assert.Throws<InvalidDataException>(() => ModbusValueCodec.Decode(
            [0x7F80, 0x0000],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Float32));
        Assert.Throws<InvalidDataException>(() => ModbusValueCodec.Decode(
            [0x7FF8, 0x0000, 0x0000, 0x0000],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Float64));
        Assert.Throws<InvalidDataException>(() => ModbusValueCodec.Decode(
            [0x7FF0, 0x0000, 0x0000, 0x0000],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Float64));
    }

    /// <summary>
    /// 验证浮点编码拒绝 NaN、Infinity、Float32 溢出和非精确回算。
    /// </summary>
    [Fact]
    public void Encode_FloatingInvalidOrLossyValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusValueCodec.Encode(
            double.NaN,
            new ushort[2],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Float32));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusValueCodec.Encode(
            double.PositiveInfinity,
            new ushort[4],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Float64));
        Assert.Throws<OverflowException>(() => ModbusValueCodec.Encode(
            double.MaxValue,
            new ushort[2],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Float32));
        Assert.Throws<ArgumentException>(() => ModbusValueCodec.Encode(
            0.1d,
            new ushort[2],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Float32));
        Assert.Throws<ArgumentException>(() => ModbusValueCodec.Encode(
            0.1m,
            new ushort[4],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Float64));
        Assert.Throws<ArgumentException>(() => ModbusValueCodec.Encode(
            -63.9d,
            new ushort[4],
            ModbusRegisterArea.HoldingRegister,
            ModbusValueType.Float64,
            scale: 0.1m));
    }

    /// <summary>
    /// 验证浮点 wire type 拒绝在转换为 Float64 时已经丢失整数精度的输入。
    /// </summary>
    [Theory]
    [InlineData(ModbusValueType.Float32, 2)]
    [InlineData(ModbusValueType.Float64, 4)]
    public void Encode_FloatingWireWithLossyIntegerToDouble_Throws(
        ModbusValueType valueType,
        int registerCount)
    {
        Assert.Throws<ArgumentException>(() => ModbusValueCodec.Encode(
            9_007_199_254_740_993L,
            new ushort[registerCount],
            ModbusRegisterArea.HoldingRegister,
            valueType));
        Assert.Throws<ArgumentException>(() => ModbusValueCodec.Encode(
            9_007_199_254_740_993m,
            new ushort[registerCount],
            ModbusRegisterArea.HoldingRegister,
            valueType));
        Assert.Throws<ArgumentException>(() => ModbusValueCodec.Encode(
            ulong.MaxValue,
            new ushort[registerCount],
            ModbusRegisterArea.HoldingRegister,
            valueType));

        var exactlyRepresentable = new ushort[registerCount];
        ModbusValueCodec.Encode(
            9_007_199_254_740_992L,
            exactlyRepresentable,
            ModbusRegisterArea.HoldingRegister,
            valueType);
        Assert.Equal(
            9_007_199_254_740_992d,
            ModbusValueCodec.Decode(
                exactlyRepresentable,
                ModbusRegisterArea.HoldingRegister,
                valueType));
    }
}
