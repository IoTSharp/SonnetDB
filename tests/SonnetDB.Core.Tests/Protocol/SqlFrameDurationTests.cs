using System.Buffers;
using System.Buffers.Binary;
using SonnetDB.Protocol;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Protocol;

/// <summary>v5 原生时长必须协商、保留全范围 ticks，并拒绝畸形载荷。</summary>
public sealed class SqlFrameDurationTests
{
    [Fact]
    public void RowsV5_DenseNullableVariantAndBounds_PreserveSignedTicks()
    {
        TimeSpan[] values = [TimeSpan.MinValue, TimeSpan.FromTicks(-1), TimeSpan.Zero,
            TimeSpan.FromHours(27).Add(TimeSpan.FromTicks(1)), TimeSpan.MaxValue];
        IReadOnlyList<IReadOnlyList<object?>> rows = values.Select(value => (IReadOnlyList<object?>)new object?[]
            { value, value, value }).Append(new object?[] { TimeSpan.Zero, null, "text" }).ToArray();
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRowsFrame(writer, 1, rows, 0, rows.Count, 3, true, true, true);
        byte[] payload = Payload(writer);
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryRowsFrame(payload, true));
        var result = SqlFrameCodec.DecodeQueryRowsFrame(payload, true, true);
        for (int row = 0; row < rows.Count; row++)
            Assert.Equal(rows[row], result[row]);
        Assert.Throws<InvalidOperationException>(() => SqlFrameCodec.EncodeQueryRowsFrame(
            new ArrayBufferWriter<byte>(), 1, rows, 0, rows.Count, 3, true, true));
    }

    [Fact]
    public void MetaV5_EmptyDurationDeclaration_RequiresExplicitCapability()
    {
        var info = new[] { new SelectColumnInfo(TableColumnType.Duration, true) };
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryMetaFrame(writer, 1, ["d"], info, true, true, true);
        byte[] payload = Payload(writer);
        Assert.Equal(4, payload[4]);
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryMetaFrameWithInfo(payload, out _));
        var decoded = SqlFrameCodec.DecodeQueryMetaFrameWithInfo(payload, out bool nativeDateTime, out bool nativeDuration);
        Assert.True(nativeDateTime);
        Assert.True(nativeDuration);
        Assert.Equal(info, decoded.ColumnInfo);
        payload[4] = 3;
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryMetaFrameWithInfo(payload, out _, out _));
    }

    [Fact]
    public void RowsV5_TruncationUnknownTagsAndMissingAcknowledgement_Reject()
    {
        byte[] complete = [2, 1, 1, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt64LittleEndian(complete.AsSpan(5), long.MinValue);
        Assert.Equal(TimeSpan.MinValue, SqlFrameCodec.DecodeQueryRowsFrame(complete, true, true)[0][0]);
        for (int length = 5; length < complete.Length; length++)
        {
            byte[] truncated = complete[..length];
            Assert.Throws<InvalidOperationException>(() => SqlFrameCodec.DecodeQueryRowsFrame(truncated, true, true));
        }
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryRowsFrame(complete, true));
        complete[3] = 12;
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryRowsFrame(complete, true, true));
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryRowsFrame([2, 1, 1, 255, 12], true, true));
    }

    private static byte[] Payload(ArrayBufferWriter<byte> writer)
    {
        ReadOnlySequence<byte> frames = new(writer.WrittenMemory);
        Assert.True(FrameCodec.TryReadFrame(ref frames, out _, out var payload));
        Assert.Equal(0, frames.Length);
        return payload.ToArray();
    }
}
