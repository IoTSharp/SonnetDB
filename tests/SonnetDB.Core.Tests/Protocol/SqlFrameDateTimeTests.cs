using System.Buffers;
using System.Buffers.Binary;
using SonnetDB.Protocol;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Protocol;

/// <summary>v4 日期传输保留钟面与 Kind，并把旧编码及畸形载荷边界固定下来。</summary>
public sealed class SqlFrameDateTimeTests
{
    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public void RowsV4_DateTimeDenseNullableVariantAndBounds_PreserveTicksAndKind(DateTimeKind kind)
    {
        var values = new[] { new DateTime(0, kind), new DateTime(2024, 2, 29, 0, 0, 0, kind).AddTicks(1),
            new DateTime(DateTime.MaxValue.Ticks, kind) };
        IReadOnlyList<IReadOnlyList<object?>> rows = values.Select(date => (IReadOnlyList<object?>)new object?[]
            { date, date, date }).Append(new object?[] { values[1], null, "text" }).ToArray();
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRowsFrame(writer, 1, rows, 0, rows.Count, 3, exactDecimal: true, nativeDateTime: true);
        byte[] payload = Payload(writer);
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryRowsFrame(payload));
        var result = SqlFrameCodec.DecodeQueryRowsFrame(payload, nativeDateTime: true);
        for (int row = 0; row < rows.Count; row++)
            for (int column = 0; column < 3; column++)
            {
                if (rows[row][column] is DateTime expected)
                {
                    var actual = Assert.IsType<DateTime>(result[row][column]);
                    Assert.Equal(expected.Ticks, actual.Ticks);
                    Assert.Equal(expected.Kind, actual.Kind);
                }
                else Assert.Equal(rows[row][column], result[row][column]);
            }
    }

    [Fact]
    public void RowsV4_LegacyOverloads_KeepTimestampBytesAndUtcMeaning()
    {
        var utc = new DateTime(2024, 2, 28, 16, 30, 0, DateTimeKind.Utc).AddTicks(123);
        var local = utc.ToLocalTime();
        var oldWriter = new ArrayBufferWriter<byte>();
        var explicitOldWriter = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRowsFrame(oldWriter, 1, [new object?[] { local }], 0, 1, 1);
        SqlFrameCodec.EncodeQueryRowsFrame(explicitOldWriter, 1, [new object?[] { local }], 0, 1, 1,
            exactDecimal: true, nativeDateTime: false);
        byte[] expected = [2, 1, 1, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt64LittleEndian(expected.AsSpan(5), utc.Ticks);
        Assert.Equal(expected, Payload(oldWriter));
        Assert.Equal(expected, Payload(explicitOldWriter));
        var value = Assert.IsType<DateTime>(SqlFrameCodec.DecodeQueryRowsFrame(expected)[0][0]);
        Assert.Equal(utc.Ticks, value.Ticks);
        Assert.Equal(DateTimeKind.Utc, value.Kind);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(3155378976000000000L, 0)]
    [InlineData(0, 3)]
    [InlineData(0, 255)]
    public void RowsV4_InvalidTicksOrKind_Rejects(long ticks, byte kind)
    {
        byte[] payload = [2, 1, 1, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, kind];
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(5), ticks);
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryRowsFrame(payload, nativeDateTime: true));
    }

    [Fact]
    public void MetaV4_EmptyDeclaredDateTime_ConfirmsCapabilityWithoutChangingV3Bytes()
    {
        var info = new[] { new SelectColumnInfo(TableColumnType.DateTime, true) };
        var oldWriter = new ArrayBufferWriter<byte>();
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryMetaFrame(oldWriter, 1, ["d"], info, fullDeclarations: true);
        SqlFrameCodec.EncodeQueryMetaFrame(writer, 1, ["d"], info, fullDeclarations: true, nativeDateTime: true);
        byte[] oldPayload = Payload(oldWriter);
        byte[] payload = Payload(writer);
        Assert.Equal(2, oldPayload[4]);
        Assert.Equal(3, payload[4]);
        var decoded = SqlFrameCodec.DecodeQueryMetaFrameWithInfo(payload, out bool nativeDateTime);
        Assert.True(nativeDateTime);
        Assert.Equal(info, decoded.ColumnInfo);
        SqlFrameCodec.DecodeQueryMetaFrameWithInfo(oldPayload, out bool oldNativeDateTime);
        Assert.False(oldNativeDateTime);
        payload[4] = 2;
        Assert.Equal(oldPayload, payload);
    }

    [Fact]
    public void RowsV4_TruncatedNativeDateTimeAndUnknownTags_Reject()
    {
        byte[] complete = [2, 1, 1, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        for (int length = 5; length < complete.Length; length++)
        {
            byte[] truncated = complete[..length];
            Assert.Throws<InvalidOperationException>(() => SqlFrameCodec.DecodeQueryRowsFrame(truncated, nativeDateTime: true));
        }
        complete[3] = 11;
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryRowsFrame(complete, nativeDateTime: true));
        byte[] variant = [2, 1, 1, 255, 11];
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryRowsFrame(variant, nativeDateTime: true));
    }

    private static byte[] Payload(ArrayBufferWriter<byte> writer)
    {
        ReadOnlySequence<byte> frames = new(writer.WrittenMemory);
        Assert.True(FrameCodec.TryReadFrame(ref frames, out _, out var payload));
        Assert.Equal(0, frames.Length);
        return payload.ToArray();
    }
}
