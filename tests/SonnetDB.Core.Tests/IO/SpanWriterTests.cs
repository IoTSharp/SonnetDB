using System.Runtime.InteropServices;
using SonnetDB.IO;
using Xunit;

namespace SonnetDB.Core.Tests.IO;

/// <summary>
/// <see cref="SpanWriter"/> 单元测试。
/// </summary>
public sealed class SpanWriterTests
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

    // ────────────────────────────── 属性测试 ──────────────────────────────

    [Fact]
    public void Position_InitialValue_IsZero()
    {
        Span<byte> buf = stackalloc byte[16];
        var writer = new SpanWriter(buf);
        Assert.Equal(0, writer.Position);
    }

    [Fact]
    public void Capacity_ReturnsBufferLength()
    {
        Span<byte> buf = stackalloc byte[32];
        var writer = new SpanWriter(buf);
        Assert.Equal(32, writer.Capacity);
    }

    [Fact]
    public void Remaining_InitialValue_EqualsCapacity()
    {
        Span<byte> buf = stackalloc byte[16];
        var writer = new SpanWriter(buf);
        Assert.Equal(16, writer.Remaining);
    }

    [Fact]
    public void WrittenSpan_LengthEqualsPosition()
    {
        Span<byte> buf = stackalloc byte[16];
        var writer = new SpanWriter(buf);
        writer.WriteByte(0xAB);
        writer.WriteInt16(0x1234);
        Assert.Equal(writer.Position, writer.WrittenSpan.Length);
    }

    // ────────────────────────────── WriteByte / WriteSByte ──────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(255)]
    public void WriteByte_SingleByte_AdvancesPosition(byte value)
    {
        Span<byte> buf = stackalloc byte[4];
        var writer = new SpanWriter(buf);
        writer.WriteByte(value);
        Assert.Equal(1, writer.Position);
        Assert.Equal(value, buf[0]);
    }

    [Theory]
    [InlineData(sbyte.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(sbyte.MaxValue)]
    public void WriteSByte_AdvancesPosition(sbyte value)
    {
        Span<byte> buf = stackalloc byte[4];
        var writer = new SpanWriter(buf);
        writer.WriteSByte(value);
        Assert.Equal(1, writer.Position);
        Assert.Equal((byte)value, buf[0]);
    }

    // ────────────────────────────── WriteInt16 / WriteUInt16 ──────────────────────────────

    [Fact]
    public void WriteInt16_AdvancesPositionByTwo()
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteInt16(1);
        Assert.Equal(2, writer.Position);
    }

    [Fact]
    public void WriteUInt16_AdvancesPositionByTwo()
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteUInt16(1);
        Assert.Equal(2, writer.Position);
    }

    // ────────────────────────────── WriteInt32 / WriteUInt32 ──────────────────────────────

    [Fact]
    public void WriteInt32_AdvancesPositionByFour()
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteInt32(1);
        Assert.Equal(4, writer.Position);
    }

    [Fact]
    public void WriteUInt32_AdvancesPositionByFour()
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteUInt32(1);
        Assert.Equal(4, writer.Position);
    }

    // ────────────────────────────── WriteInt64 / WriteUInt64 ──────────────────────────────

    [Fact]
    public void WriteInt64_AdvancesPositionByEight()
    {
        Span<byte> buf = stackalloc byte[16];
        var writer = new SpanWriter(buf);
        writer.WriteInt64(1);
        Assert.Equal(8, writer.Position);
    }

    [Fact]
    public void WriteUInt64_AdvancesPositionByEight()
    {
        Span<byte> buf = stackalloc byte[16];
        var writer = new SpanWriter(buf);
        writer.WriteUInt64(1);
        Assert.Equal(8, writer.Position);
    }

    // ────────────────────────────── WriteSingle / WriteDouble ──────────────────────────────

    [Fact]
    public void WriteSingle_AdvancesPositionByFour()
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteSingle(1.0f);
        Assert.Equal(4, writer.Position);
    }

    [Fact]
    public void WriteDouble_AdvancesPositionByEight()
    {
        Span<byte> buf = stackalloc byte[16];
        var writer = new SpanWriter(buf);
        writer.WriteDouble(1.0);
        Assert.Equal(8, writer.Position);
    }

    // ────────────────────────────── Endianness ──────────────────────────────

    [Fact]
    public void WriteInt32_LittleEndian_ByteOrderCorrect()
    {
        Span<byte> buf = stackalloc byte[4];
        var writer = new SpanWriter(buf);
        writer.WriteInt32(0x01020304);
        Assert.Equal(0x04, buf[0]);
        Assert.Equal(0x03, buf[1]);
        Assert.Equal(0x02, buf[2]);
        Assert.Equal(0x01, buf[3]);
    }

    [Fact]
    public void WriteInt64_LittleEndian_ByteOrderCorrect()
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteInt64(0x0102030405060708L);
        Assert.Equal(0x08, buf[0]);
        Assert.Equal(0x07, buf[1]);
        Assert.Equal(0x06, buf[2]);
        Assert.Equal(0x05, buf[3]);
        Assert.Equal(0x04, buf[4]);
        Assert.Equal(0x03, buf[5]);
        Assert.Equal(0x02, buf[6]);
        Assert.Equal(0x01, buf[7]);
    }

    // ────────────────────────────── WriteBytes ──────────────────────────────

    [Fact]
    public void WriteBytes_CopiesAllBytes()
    {
        byte[] src = [1, 2, 3, 4, 5];
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteBytes(src);
        Assert.Equal(5, writer.Position);
        Assert.Equal(src, buf[..5].ToArray());
    }

    [Fact]
    public void WriteBytes_EmptySpan_DoesNotAdvance()
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteBytes(ReadOnlySpan<byte>.Empty);
        Assert.Equal(0, writer.Position);
    }

    // ────────────────────────────── WriteStruct ──────────────────────────────

    [Fact]
    public void WriteStruct_AdvancesPositionByStructSize()
    {
        Span<byte> buf = stackalloc byte[64];
        var writer = new SpanWriter(buf);
        var h = new TestHeader { SeriesId = 42, MinTimestamp = 100, MaxTimestamp = 200, Count = 3 };
        writer.WriteStruct(in h);
        Assert.Equal(28, writer.Position); // 8+8+8+4 = 28
    }

    // ────────────────────────────── WriteStructs ──────────────────────────────

    [Fact]
    public void WriteStructs_MultipleHeaders_AdvancesPositionCorrectly()
    {
        var headers = new TestHeader[]
        {
            new() { SeriesId = 1, MinTimestamp = 10, MaxTimestamp = 20, Count = 1 },
            new() { SeriesId = 2, MinTimestamp = 30, MaxTimestamp = 40, Count = 2 },
        };
        Span<byte> buf = stackalloc byte[128];
        var writer = new SpanWriter(buf);
        writer.WriteStructs<TestHeader>(headers);
        Assert.Equal(28 * 2, writer.Position);
    }

    // ────────────────────────────── VarUInt ──────────────────────────────

    [Theory]
    [InlineData(0u, 1)]
    [InlineData(1u, 1)]
    [InlineData(127u, 1)]
    [InlineData(128u, 2)]
    [InlineData(16383u, 2)]
    [InlineData(16384u, 3)]
    [InlineData(uint.MaxValue, 5)]
    public void WriteVarUInt32_EncodingLength_MatchesLEB128(uint value, int expectedBytes)
    {
        Span<byte> buf = stackalloc byte[16];
        var writer = new SpanWriter(buf);
        writer.WriteVarUInt32(value);
        Assert.Equal(expectedBytes, writer.Position);
        Assert.Equal(expectedBytes, SpanWriter.MeasureVarUInt32(value));
    }

    [Theory]
    [InlineData(0ul, 1)]
    [InlineData(1ul, 1)]
    [InlineData(127ul, 1)]
    [InlineData(128ul, 2)]
    [InlineData(16383ul, 2)]
    [InlineData(16384ul, 3)]
    [InlineData(uint.MaxValue, 5)]
    [InlineData(ulong.MaxValue, 10)]
    public void WriteVarUInt64_EncodingLength_MatchesLEB128(ulong value, int expectedBytes)
    {
        Span<byte> buf = stackalloc byte[16];
        var writer = new SpanWriter(buf);
        writer.WriteVarUInt64(value);
        Assert.Equal(expectedBytes, writer.Position);
        Assert.Equal(expectedBytes, SpanWriter.MeasureVarUInt64(value));
    }

    // ────────────────────────────── WriteString ──────────────────────────────

    [Fact]
    public void WriteString_Null_WritesMinusOne()
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteString(null, System.Text.Encoding.UTF8);
        Assert.Equal(4, writer.Position);
        // 读回确认 length = -1
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(-1, reader.ReadInt32());
    }

    [Fact]
    public void WriteString_EmptyString_WritesZeroLength()
    {
        Span<byte> buf = stackalloc byte[8];
        var writer = new SpanWriter(buf);
        writer.WriteString(string.Empty, System.Text.Encoding.UTF8);
        Assert.Equal(4, writer.Position);
        var reader = new SpanReader(writer.WrittenSpan);
        Assert.Equal(0, reader.ReadInt32());
    }

    // ────────────────────────────── Advance / Reset ──────────────────────────────

    [Fact]
    public void Advance_IncreasesPosition()
    {
        Span<byte> buf = stackalloc byte[16];
        var writer = new SpanWriter(buf);
        writer.Advance(4);
        Assert.Equal(4, writer.Position);
    }

    [Fact]
    public void Reset_SetsPositionToZero()
    {
        Span<byte> buf = stackalloc byte[16];
        var writer = new SpanWriter(buf);
        writer.WriteInt32(42);
        writer.Reset();
        Assert.Equal(0, writer.Position);
    }

    // ────────────────────────────── 边界异常 ──────────────────────────────

    [Fact]
    public void EnsureRemaining_Overflow_ThrowsInvalidOperationException()
    {
        // ref struct 不能被 lambda 捕获，使用 try/catch 验证异常
        Span<byte> buf = stackalloc byte[2];
        var writer = new SpanWriter(buf);
        var threw = false;
        try { writer.WriteInt32(42); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void Advance_BeyondCapacity_ThrowsInvalidOperationException()
    {
        Span<byte> buf = stackalloc byte[4];
        var writer = new SpanWriter(buf);
        var threw = false;
        try { writer.Advance(8); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void WriteByte_FullBuffer_ThrowsInvalidOperationException()
    {
        Span<byte> buf = stackalloc byte[1];
        var writer = new SpanWriter(buf);
        writer.WriteByte(0);
        var threw = false;
        try { writer.WriteByte(0); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);
    }
}
