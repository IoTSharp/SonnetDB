using System.Buffers;
using SonnetDB.Protocol;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Protocol;

public sealed class SqlFrameDateTime2Tests
{
    [Fact]
    public void MetaV7_TypedNullAndEmpty_RequireExplicitCapability()
    {
        var declarations = new[] { new SelectColumnInfo(TableColumnType.DateTime2, true) };
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryMetaFrame(writer, 1, ["d"], declarations, true, true, true, true, true);
        byte[] payload = Payload(writer);
        Assert.Equal(6, payload[4]);
        Assert.Equal(12, payload[5]);
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryMetaFrameWithInfo(payload, out _, out _, out _));
        var decoded = SqlFrameCodec.DecodeQueryMetaFrameWithInfo(payload, out bool date, out bool duration, out bool offset, out bool date2);
        Assert.True(date && duration && offset && date2);
        Assert.Equal(declarations, decoded.ColumnInfo);
        Assert.Throws<InvalidOperationException>(() => SqlFrameCodec.EncodeQueryMetaFrame(new ArrayBufferWriter<byte>(),
            1, ["d"], declarations, true, true, true, true));
        payload[4] = 5;
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryMetaFrameWithInfo(payload, out _, out _, out _, out _));
    }

    [Fact]
    public void QueryV7_ExistingKind12_PreservesProvenanceAndTypedNull()
    {
        DateTime value = DateTime.UnixEpoch.AddTicks(1);
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeTypedQueryRequest(writer, 1, "db", "SELECT @d,@n",
            [SqlExecuteFrameCodec.FromClr("d", value), new("n", SqlFrameParameterKind.DateTime, null)]);
        byte[] payload = Payload(writer);
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryRequest(payload));
        var request = SqlFrameCodec.DecodeQueryRequest(payload, allowTypedParameters: true);
        var select = Assert.IsType<SelectStatement>(SqlParameterBinder.Bind(SqlParser.Parse(request.Sql), request.Parameters));
        var precise = Assert.IsType<CastExpression>(select.Projections[0].Expression);
        Assert.Equal(SqlDataType.DateTime2, precise.TargetType);
        var typedNull = Assert.IsType<CastExpression>(select.Projections[1].Expression);
        Assert.Equal(SqlDataType.DateTime2, typedNull.TargetType);
        Assert.Equal(value.Ticks, Assert.IsType<LiteralExpression>(precise.Operand).IntegerValue);
        Assert.True(precise.IsParameterValue);
        for (int length = payload.Length - 5; length < payload.Length; length++)
        {
            byte[] truncated = payload[..length];
            Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryRequest(truncated, true));
        }
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryRequest([.. payload, 0], true));
    }

    [Fact]
    public void RowsV7_ReuseNativeDateTimeLayout_PreserveTicksAndKind()
    {
        IReadOnlyList<IReadOnlyList<object?>> rows =
        [
            new object?[] { DateTime.UnixEpoch.AddTicks(-1), null },
            new object?[] { DateTime.UnixEpoch.AddTicks(1), DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) },
        ];
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRowsFrame(writer, 1, rows, 0, 2, 2, true, true, true, true);
        var decoded = SqlFrameCodec.DecodeQueryRowsFrame(Payload(writer), true, true, true);
        Assert.Equal(rows[0], decoded[0]);
        Assert.Equal(rows[1], decoded[1]);
    }

    private static byte[] Payload(ArrayBufferWriter<byte> writer)
    {
        ReadOnlySequence<byte> frames = new(writer.WrittenMemory);
        Assert.True(FrameCodec.TryReadFrame(ref frames, out _, out var payload));
        Assert.Equal(0, frames.Length);
        return payload.ToArray();
    }
}
