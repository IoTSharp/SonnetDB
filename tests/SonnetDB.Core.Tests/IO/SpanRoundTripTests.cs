using System.Runtime.InteropServices;
using System.Text;
using SonnetDB.IO;
using Xunit;

namespace SonnetDB.Core.Tests.IO;

/// <summary>
/// <see cref="SpanWriter"/> 与 <see cref="SpanReader"/> 综合 round-trip 测试。
/// 完整模拟"写一个数据块后原样读回"的场景。
/// </summary>
public sealed class SpanRoundTripTests
{
    // ────────────────────────────── 辅助结构 ──────────────────────────────

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TestHeader
    {
        public ulong SeriesId;
        public long MinTimestamp;
        public long MaxTimestamp;
        public int Count;
    }

    // ────────────────────────────── 基础类型 round-trip ──────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(255)]
    public void Byte_RoundTrip(byte value)
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteByte(value);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadByte());
    }

    [Theory]
    [InlineData(sbyte.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(sbyte.MaxValue)]
    public void SByte_RoundTrip(sbyte value)
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteSByte(value);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadSByte());
    }

    [Theory]
    [InlineData(short.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(short.MaxValue)]
    public void Int16_RoundTrip(short value)
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteInt16(value);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadInt16());
    }

    [Theory]
    [InlineData(ushort.MinValue)]
    [InlineData(1)]
    [InlineData(ushort.MaxValue)]
    public void UInt16_RoundTrip(ushort value)
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteUInt16(value);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadUInt16());
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Int32_RoundTrip(int value)
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteInt32(value);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadInt32());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(uint.MaxValue)]
    public void UInt32_RoundTrip(uint value)
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteUInt32(value);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadUInt32());
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    public void Int64_RoundTrip(long value)
    {
        Span<byte> buf = stackalloc byte[16];
        var writer = new SpanWriter(buf);
        writer.WriteInt64(value);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadInt64());
    }

    [Theory]
    [InlineData(0ul)]
    [InlineData(1ul)]
    [InlineData(ulong.MaxValue)]
    public void UInt64_RoundTrip(ulong value)
    {
        Span<byte> buf = stackalloc byte[16];
        var writer = new SpanWriter(buf);
        writer.WriteUInt64(value);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadUInt64());
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(1.0f)]
    [InlineData(-1.0f)]
    [InlineData(float.MinValue)]
    [InlineData(float.MaxValue)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Single_RoundTrip(float value)
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteSingle(value);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadSingle());
    }

    [Fact]
    public void Single_NaN_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteSingle(float.NaN);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.True(float.IsNaN(reader.ReadSingle()));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(double.MinValue)]
    [InlineData(double.MaxValue)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Double_RoundTrip(double value)
    {
        Span<byte> buf = stackalloc byte[16];
        var writer = new SpanWriter(buf);
        writer.WriteDouble(value);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadDouble());
    }

    [Fact]
    public void Double_NaN_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[16];
        var writer = new SpanWriter(buf);
        writer.WriteDouble(double.NaN);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.True(double.IsNaN(reader.ReadDouble()));
    }

    // ────────────────────────────── WriteBytes ↔ ReadBytes ──────────────────────────────

    [Fact]
    public void Bytes_RoundTrip()
    {
        byte[] original = [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0xFF];
        Span<byte> buf = stackalloc byte[32];
        var writer = new SpanWriter(buf);
        writer.WriteBytes(original);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(original, reader.ReadBytes(original.Length).ToArray());
    }

    // ────────────────────────────── WriteStruct ↔ ReadStruct ──────────────────────────────

    [Fact]
    public void Struct_RoundTrip_AllFieldsEqual()
    {
        var original = new TestHeader
        {
            SeriesId = 0xDEADBEEFCAFEBABEUL,
            MinTimestamp = long.MinValue,
            MaxTimestamp = long.MaxValue,
            Count = int.MaxValue,
        };

        Span<byte> buf = stackalloc byte[64];
        var writer = new SpanWriter(buf);
        writer.WriteStruct(in original);

        var reader = new SpanReader(writer.WrittenSpan);
        TestHeader result = reader.ReadStruct<TestHeader>();

        Assert.Equal(original.SeriesId, result.SeriesId);
        Assert.Equal(original.MinTimestamp, result.MinTimestamp);
        Assert.Equal(original.MaxTimestamp, result.MaxTimestamp);
        Assert.Equal(original.Count, result.Count);
    }

    // ────────────────────────────── WriteStructs ↔ ReadStructs ──────────────────────────────

    [Fact]
    public void Structs_RoundTrip_AllFieldsEqual()
    {
        var originals = new TestHeader[]
        {
            new() { SeriesId = 1, MinTimestamp = 100, MaxTimestamp = 200, Count = 10 },
            new() { SeriesId = 2, MinTimestamp = 300, MaxTimestamp = 400, Count = 20 },
            new() { SeriesId = 3, MinTimestamp = 500, MaxTimestamp = 600, Count = 30 },
        };

        Span<byte> buf = stackalloc byte[256];
        var writer = new SpanWriter(buf);
        writer.WriteStructs<TestHeader>(originals);

        var reader = new SpanReader(writer.WrittenSpan);
        ReadOnlySpan<TestHeader> results = reader.ReadStructs<TestHeader>(originals.Length);

        for (int i = 0; i < originals.Length; i++)
        {
            Assert.Equal(originals[i].SeriesId, results[i].SeriesId);
            Assert.Equal(originals[i].MinTimestamp, results[i].MinTimestamp);
            Assert.Equal(originals[i].MaxTimestamp, results[i].MaxTimestamp);
            Assert.Equal(originals[i].Count, results[i].Count);
        }
    }

    // ────────────────────────────── VarUInt round-trip ──────────────────────────────

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(127u)]
    [InlineData(128u)]
    [InlineData(16383u)]
    [InlineData(16384u)]
    [InlineData(uint.MaxValue)]
    public void VarUInt32_RoundTrip(uint value)
    {
        Span<byte> buf = stackalloc byte[16];
        var writer = new SpanWriter(buf);
        writer.WriteVarUInt32(value);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadVarUInt32());
        Assert.True(reader.IsEnd);
    }

    [Theory]
    [InlineData(0ul)]
    [InlineData(1ul)]
    [InlineData(127ul)]
    [InlineData(128ul)]
    [InlineData(16383ul)]
    [InlineData(16384ul)]
    [InlineData(uint.MaxValue)]
    [InlineData(ulong.MaxValue)]
    public void VarUInt64_RoundTrip(ulong value)
    {
        Span<byte> buf = stackalloc byte[16];
        var writer = new SpanWriter(buf);
        writer.WriteVarUInt64(value);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadVarUInt64());
        Assert.True(reader.IsEnd);
    }

    // ────────────────────────────── WriteString ↔ ReadString ──────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Hello")]
    [InlineData("Hello, World!")]
    public void String_Ascii_RoundTrip(string? value)
    {
        byte[] buf = new byte[256];
        var writer = new SpanWriter(buf);
        writer.WriteString(value, Encoding.UTF8);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadString(Encoding.UTF8));
    }

    [Theory]
    [InlineData("时序数据库")]
    [InlineData("你好世界")]
    [InlineData("测试中文字符串 ABC 123")]
    public void String_Chinese_RoundTrip(string value)
    {
        byte[] buf = new byte[256];
        var writer = new SpanWriter(buf);
        writer.WriteString(value, Encoding.UTF8);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadString(Encoding.UTF8));
    }

    // ────────────────────────────── WriteVarString ↔ ReadVarString ──────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("Hello, World!")]
    [InlineData("时序数据库")]
    [InlineData("mixed 中英 mix 🚀")]
    public void VarString_RoundTrip(string value)
    {
        byte[] buf = new byte[1024];
        var writer = new SpanWriter(buf);
        writer.WriteVarString(value);
        Assert.Equal(SpanWriter.MeasureVarString(value), writer.Position);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadVarString());
        Assert.True(reader.IsEnd);
    }

    [Fact]
    public void VarString_512ByteBoundary_RoundTrip()
    {
        // 512 字节 = 帧协议名字上限；LEB128 长度前缀在 128 处从 1 字节变 2 字节
        string value = new('x', 512);
        byte[] buf = new byte[1024];
        var writer = new SpanWriter(buf);
        writer.WriteVarString(value);
        Assert.Equal(2 + 512, writer.Position);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(value, reader.ReadVarString());
    }

    [Fact]
    public void VarString_LengthExceedsBuffer_Throws()
    {
        byte[] buf = new byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteVarUInt32(100); // 声明 100 字节但缓冲区没有
        var truncated = writer.WrittenSpan.ToArray();
        Assert.Throws<InvalidOperationException>(() =>
        {
            var reader = new SpanReader(truncated);
            reader.ReadVarString();
        });
    }

    [Theory]
    [InlineData(0u, 1)]
    [InlineData(127u, 1)]
    [InlineData(128u, 2)]
    [InlineData(16383u, 2)]
    [InlineData(16384u, 3)]
    [InlineData(uint.MaxValue, 5)]
    public void MeasureVarUInt32_MatchesWrittenLength(uint value, int expected)
    {
        Assert.Equal(expected, SpanWriter.MeasureVarUInt32(value));
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteVarUInt32(value);
        Assert.Equal(expected, writer.Position);
    }

    [Theory]
    [InlineData(0ul, 1)]
    [InlineData(127ul, 1)]
    [InlineData(128ul, 2)]
    [InlineData((ulong)uint.MaxValue, 5)]
    [InlineData(ulong.MaxValue, 10)]
    public void MeasureVarUInt64_MatchesWrittenLength(ulong value, int expected)
    {
        Assert.Equal(expected, SpanWriter.MeasureVarUInt64(value));
        Span<byte> buf = stackalloc byte[16];
        var writer = new SpanWriter(buf);
        writer.WriteVarUInt64(value);
        Assert.Equal(expected, writer.Position);
    }

    // ────────────────────────────── 综合 block round-trip ──────────────────────────────

    /// <summary>
    /// 模拟写入一个完整"数据块"然后原样读回的综合场景。
    /// </summary>
    [Fact]
    public void CompleteBlock_RoundTrip()
    {
        const int BlockSize = 512;
        byte[] buf = new byte[BlockSize];
        var writer = new SpanWriter(buf);

        // 写入 block header
        var header = new TestHeader
        {
            SeriesId = 12345678UL,
            MinTimestamp = 1_700_000_000_000L,
            MaxTimestamp = 1_700_000_001_000L,
            Count = 5,
        };
        writer.WriteStruct(in header);

        // 写入 VarUInt32 版本标记
        writer.WriteVarUInt32(1u);

        // 写入字符串标签
        writer.WriteString("cpu_usage", Encoding.UTF8);
        writer.WriteString("host=server-1", Encoding.UTF8);

        // 写入数值序列
        long[] timestamps = [1_700_000_000_000L, 1_700_000_000_200L, 1_700_000_000_400L, 1_700_000_000_600L, 1_700_000_000_800L];
        double[] values = [63.2, 61.8, 65.0, 70.1, 68.5];

        foreach (long ts in timestamps)
            writer.WriteInt64(ts);

        foreach (double v in values)
            writer.WriteDouble(v);

        // 写入 checksum（mock）
        writer.WriteUInt32(0xDEADBEEF);

        // ── 读回 ──────────────────────────────────────────────
        var reader = new SpanReader(writer.WrittenSpan);

        var readHeader = reader.ReadStruct<TestHeader>();
        Assert.Equal(header.SeriesId, readHeader.SeriesId);
        Assert.Equal(header.MinTimestamp, readHeader.MinTimestamp);
        Assert.Equal(header.MaxTimestamp, readHeader.MaxTimestamp);
        Assert.Equal(header.Count, readHeader.Count);

        Assert.Equal(1u, reader.ReadVarUInt32());
        Assert.Equal("cpu_usage", reader.ReadString(Encoding.UTF8));
        Assert.Equal("host=server-1", reader.ReadString(Encoding.UTF8));

        for (int i = 0; i < 5; i++)
            Assert.Equal(timestamps[i], reader.ReadInt64());

        for (int i = 0; i < 5; i++)
            Assert.Equal(values[i], reader.ReadDouble());

        Assert.Equal(0xDEADBEEFU, reader.ReadUInt32());
        Assert.True(reader.IsEnd);
    }

    // ────────────────────────────── 位置精确性 ──────────────────────────────

    [Fact]
    public void Position_AfterMultipleWrites_IsCorrect()
    {
        Span<byte> buf = stackalloc byte[64];
        var writer = new SpanWriter(buf);
        writer.WriteByte(0);         // +1
        writer.WriteInt16(0);        // +2
        writer.WriteInt32(0);        // +4
        writer.WriteInt64(0);        // +8
        Assert.Equal(15, writer.Position);
    }

    [Fact]
    public void Position_AfterReset_IsZero()
    {
        Span<byte> buf = stackalloc byte[16];
        var writer = new SpanWriter(buf);
        writer.WriteInt64(42);
        writer.Reset();
        Assert.Equal(0, writer.Position);

        // 可以重写
        writer.WriteInt32(99);
        Assert.Equal(4, writer.Position);
    }
}
