using System.Buffers;
using System.Buffers.Binary;
using SonnetDB.Protocol;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Protocol;

/// <summary>v6 原生时刻保留 UTC ticks，旧客户端与畸形载荷不能降精度解释。</summary>
public sealed class SqlFrameDateTimeOffsetTests
{
    [Fact]
    public void RowsV6_DenseNullableVariantAndBounds_PreserveUtcTicks()
    {
        DateTimeOffset[] values = [DateTimeOffset.MinValue, new(1, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 8, 20, 34, 56, TimeSpan.FromHours(8)).AddTicks(1230001), DateTimeOffset.MaxValue];
        IReadOnlyList<IReadOnlyList<object?>> rows = values.Select(value => (IReadOnlyList<object?>)new object?[]
            { value, value, value }).Append(new object?[] { DateTimeOffset.MinValue, null, "text" }).ToArray();
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRowsFrame(writer, 1, rows, 0, rows.Count, 3, true, true, true, true);
        byte[] payload = Payload(writer);
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryRowsFrame(payload, true, true));
        var result = SqlFrameCodec.DecodeQueryRowsFrame(payload, true, true, true);
        for (int row = 0; row < rows.Count; row++)
        {
            Assert.Equal(rows[row], result[row]);
            foreach (DateTimeOffset instant in result[row].OfType<DateTimeOffset>())
                Assert.Equal(TimeSpan.Zero, instant.Offset);
        }
    }

    [Fact]
    public void MetaV6_EmptyNativeDeclaration_RequiresExplicitCapability()
    {
        var info = new[] { new SelectColumnInfo(TableColumnType.DateTimeOffset, true) };
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryMetaFrame(writer, 1, ["d"], info, true, true, true, true);
        byte[] payload = Payload(writer);
        Assert.Equal(5, payload[4]);
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryMetaFrameWithInfo(payload, out _, out _));
        var decoded = SqlFrameCodec.DecodeQueryMetaFrameWithInfo(payload, out bool dateTime, out bool duration, out bool offset);
        Assert.True(dateTime);
        Assert.True(duration);
        Assert.True(offset);
        Assert.Equal(info, decoded.ColumnInfo);
        payload[4] = 4;
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryMetaFrameWithInfo(payload, out _, out _, out _));
        Assert.Throws<InvalidOperationException>(() => SqlFrameCodec.EncodeQueryMetaFrame(
            new ArrayBufferWriter<byte>(), 1, ["d"], info, true, true, true));
    }

    [Fact]
    public void RowsV6_TruncatedOutOfRangeAndUnacknowledged_Reject()
    {
        byte[] complete = [2, 1, 1, 12, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt64LittleEndian(complete.AsSpan(5), DateTimeOffset.MaxValue.UtcTicks);
        Assert.Equal(DateTimeOffset.MaxValue, SqlFrameCodec.DecodeQueryRowsFrame(complete, true, true, true)[0][0]);
        for (int length = 5; length < complete.Length; length++)
        {
            byte[] truncated = complete[..length];
            Assert.Throws<InvalidOperationException>(() => SqlFrameCodec.DecodeQueryRowsFrame(truncated, true, true, true));
        }
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryRowsFrame(complete, true, true));
        foreach (long ticks in new[] { -1L, DateTimeOffset.MaxValue.UtcTicks + 1, long.MaxValue, long.MinValue })
        {
            BinaryPrimitives.WriteInt64LittleEndian(complete.AsSpan(5), ticks);
            Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryRowsFrame(complete, true, true, true));
        }
    }

    private static byte[] Payload(ArrayBufferWriter<byte> writer)
    {
        ReadOnlySequence<byte> frames = new(writer.WrittenMemory);
        Assert.True(FrameCodec.TryReadFrame(ref frames, out _, out var payload));
        Assert.Equal(0, frames.Length);
        return payload.ToArray();
    }
}
