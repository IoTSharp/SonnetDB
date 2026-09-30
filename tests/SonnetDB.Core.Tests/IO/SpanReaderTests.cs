using System.Runtime.InteropServices;
using SonnetDB.IO;
using Xunit;

namespace SonnetDB.Core.Tests.IO;

/// <summary>
/// <see cref="SpanReader"/> 单元测试。
/// </summary>
public sealed class SpanReaderTests
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
        var reader = new SpanReader([1, 2, 3]);
        Assert.Equal(0, reader.Position);
    }

    [Fact]
    public void Length_ReturnsBufferLength()
    {
        var reader = new SpanReader([1, 2, 3, 4, 5]);
        Assert.Equal(5, reader.Length);
    }

    [Fact]
    public void Remaining_InitialValue_EqualsLength()
    {
        byte[] data = [10, 20, 30];
        var reader = new SpanReader(data);
        Assert.Equal(3, reader.Remaining);
    }

    [Fact]
    public void IsEnd_EmptyBuffer_IsTrue()
    {
        var reader = new SpanReader(ReadOnlySpan<byte>.Empty);
        Assert.True(reader.IsEnd);
    }

    [Fact]
    public void IsEnd_NonEmptyBuffer_IsFalse()
    {
        var reader = new SpanReader([0]);
        Assert.False(reader.IsEnd);
    }

    [Fact]
    public void IsEnd_AfterReadingAll_IsTrue()
    {
        var reader = new SpanReader([0]);
        reader.ReadByte();
        Assert.True(reader.IsEnd);
    }

    [Fact]
    public void RemainingSpan_ReturnsUnreadPortion()
    {
        byte[] data = [1, 2, 3, 4];
        var reader = new SpanReader(data);
        reader.ReadByte();
        Assert.Equal(new byte[] { 2, 3, 4 }, reader.RemainingSpan.ToArray());
    }

    // ────────────────────────────── ReadByte / ReadSByte ──────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(255)]
    public void ReadByte_ReturnsByteValue(byte expected)
    {
        var reader = new SpanReader([expected]);
        Assert.Equal(expected, reader.ReadByte());
        Assert.Equal(1, reader.Position);
    }

    [Theory]
    [InlineData(sbyte.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(sbyte.MaxValue)]
    public void ReadSByte_ReturnsSByteValue(sbyte expected)
    {
        var reader = new SpanReader([(byte)expected]);
        Assert.Equal(expected, reader.ReadSByte());
    }

    // ────────────────────────────── 边界异常 ──────────────────────────────

    [Fact]
    public void ReadByte_EmptyBuffer_ThrowsInvalidOperationException()
    {
        // ref struct 不能被 lambda 捕获，使用 try/catch 验证异常
        var reader = new SpanReader(ReadOnlySpan<byte>.Empty);
        var threw = false;
        try { reader.ReadByte(); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void ReadInt32_InsufficientData_ThrowsInvalidOperationException()
    {
        var reader = new SpanReader([1, 2]);
        var threw = false;
        try { reader.ReadInt32(); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void Skip_BeyondRemaining_ThrowsInvalidOperationException()
    {
        var reader = new SpanReader([1, 2, 3]);
        var threw = false;
        try { reader.Skip(10); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void ReadBytes_MoreThanRemaining_ThrowsInvalidOperationException()
    {
        var reader = new SpanReader([1, 2]);
        var threw = false;
        try { reader.ReadBytes(5); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);
    }

    // ────────────────────────────── Skip / Reset ──────────────────────────────

    [Fact]
    public void Skip_AdvancesPosition()
    {
        var reader = new SpanReader([1, 2, 3, 4, 5]);
        reader.Skip(3);
        Assert.Equal(3, reader.Position);
    }

    [Fact]
    public void Reset_SetsPositionToZero()
    {
        byte[] data = [1, 2, 3, 4];
        var reader = new SpanReader(data);
        reader.ReadInt32();
        reader.Reset();
        Assert.Equal(0, reader.Position);
    }

    // ────────────────────────────── ReadBytes ──────────────────────────────

    [Fact]
    public void ReadBytes_ReturnsCorrectSlice()
    {
        byte[] data = [1, 2, 3, 4, 5];
        var reader = new SpanReader(data);
        ReadOnlySpan<byte> slice = reader.ReadBytes(3);
        Assert.Equal(new byte[] { 1, 2, 3 }, slice.ToArray());
        Assert.Equal(3, reader.Position);
    }

    // ────────────────────────────── ReadStruct ──────────────────────────────

    [Fact]
    public void ReadStruct_AdvancesPositionByStructSize()
    {
        Span<byte> buf = stackalloc byte[64];
        var writer = new SpanWriter(buf);
        var h = new TestHeader { SeriesId = 99, MinTimestamp = -1, MaxTimestamp = 1000, Count = 5 };
        writer.WriteStruct(in h);

        var reader = new SpanReader(writer.WrittenSpan);
        var result = reader.ReadStruct<TestHeader>();
        Assert.Equal(28, reader.Position);
        Assert.Equal(99UL, result.SeriesId);
        Assert.Equal(-1L, result.MinTimestamp);
        Assert.Equal(1000L, result.MaxTimestamp);
        Assert.Equal(5, result.Count);
    }

    // ────────────────────────────── ReadStructs ──────────────────────────────

    [Fact]
    public void ReadStructs_ReturnsCorrectCount()
    {
        var headers = new TestHeader[]
        {
            new() { SeriesId = 10, MinTimestamp = 1, MaxTimestamp = 2, Count = 1 },
            new() { SeriesId = 20, MinTimestamp = 3, MaxTimestamp = 4, Count = 2 },
            new() { SeriesId = 30, MinTimestamp = 5, MaxTimestamp = 6, Count = 3 },
        };
        Span<byte> buf = stackalloc byte[256];
        var writer = new SpanWriter(buf);
        writer.WriteStructs<TestHeader>(headers);

        var reader = new SpanReader(writer.WrittenSpan);
        ReadOnlySpan<TestHeader> result = reader.ReadStructs<TestHeader>(3);
        Assert.Equal(3, result.Length);
        Assert.Equal(10UL, result[0].SeriesId);
        Assert.Equal(20UL, result[1].SeriesId);
        Assert.Equal(30UL, result[2].SeriesId);
    }

    // ────────────────────────────── VarUInt ──────────────────────────────

    // ────────────────────────────── ReadString ──────────────────────────────

    // ──────────────────────── VarUInt 非法编码负例 ────────────────────────

    [Fact]
    public void ReadVarUInt32_TooManyContinuationBytes_Throws()
    {
        byte[] data = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80];
        var reader = new SpanReader(data);

        bool threw = false;
        try { reader.ReadVarUInt32(); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void ReadVarUInt32_FifthByteHasInvalidHighBits_Throws()
    {
        byte[] data = [0x80, 0x80, 0x80, 0x80, 0x10];
        var reader = new SpanReader(data);

        bool threw = false;
        try { reader.ReadVarUInt32(); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void ReadVarUInt64_TenthByteHasInvalidHighBits_Throws()
    {
        byte[] data = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x02];
        var reader = new SpanReader(data);

        bool threw = false;
        try { reader.ReadVarUInt64(); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);
    }

    // ──────────────────────── ReadString 格式非法负例 ────────────────────────

    [Fact]
    public void ReadString_InvalidNegativeLength_ThrowsInvalidOperationException()
    {
        Span<byte> buf = stackalloc byte[4];
        var writer = new SpanWriter(buf);
        writer.WriteInt32(-2);

        var reader = new SpanReader(writer.WrittenSpan);

        bool threw = false;
        try { reader.ReadString(System.Text.Encoding.UTF8); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void ReadString_LengthExceedsRemaining_ThrowsInvalidOperationException()
    {
        Span<byte> buf = stackalloc byte[6];
        var writer = new SpanWriter(buf);
        writer.WriteInt32(4);
        writer.WriteByte((byte)'A');
        writer.WriteByte((byte)'B');

        var reader = new SpanReader(writer.WrittenSpan);

        bool threw = false;
        try { reader.ReadString(System.Text.Encoding.UTF8); }
        catch (InvalidOperationException) { threw = true; }
        Assert.True(threw);
    }
}
