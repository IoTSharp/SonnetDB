using System.Buffers;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Client.Tests;

public sealed class SqlExecuteFrameCodecTests
{
    [Fact]
    public void Request_RoundTripsExactScalarAndTemporalParameters()
    {
        var parameters = new[]
        {
            SqlExecuteFrameCodec.FromClr("null_value", null),
            SqlExecuteFrameCodec.FromClr("i64", long.MinValue),
            SqlExecuteFrameCodec.FromClr("decimal", 1234567890.123456m),
            SqlExecuteFrameCodec.FromClr("blob", new byte[] { 1, 2, 3 }),
            SqlExecuteFrameCodec.FromClr("offset", new DateTimeOffset(638962560000000000, TimeSpan.FromHours(8))),
            SqlExecuteFrameCodec.FromClr("duration", TimeSpan.FromTicks(-1234567)),
            SqlExecuteFrameCodec.FromClr("time", new TimeOnly(12, 34, 56, 789)),
            SqlExecuteFrameCodec.FromClr("date", new DateTime(638962560000000000, DateTimeKind.Utc)),
            SqlExecuteFrameCodec.FromClr("vector", new float[] { 1.5f, -2.25f }),
        };
        var writer = new ArrayBufferWriter<byte>();

        SqlExecuteFrameCodec.EncodeRequest(writer, 81, "demo",
            "INSERT INTO t VALUES (@i64, @decimal, @blob, @offset, @duration, @time, @date, @vector)",
            parameters);

        var sequence = new ReadOnlySequence<byte>(writer.WrittenMemory);
        Assert.True(FrameCodec.TryReadFrame(ref sequence, out var header, out var payload));
        Assert.Equal((byte)SqlFrameOp.Execute, header.Op);
        Assert.Equal(0, sequence.Length);

        var request = SqlExecuteFrameCodec.DecodeRequest(payload.ToArray());
        Assert.Equal("demo", request.Db);
        Assert.Equal(parameters.Length, request.Parameters.Count);
        Assert.Equal(SqlFrameParameterKind.Null, request.Parameters[0].Kind);
        Assert.Equal(long.MinValue, request.Parameters[1].Value);
        Assert.Equal(1234567890.123456m, request.Parameters[2].Value);
        Assert.Equal(new byte[] { 1, 2, 3 }, request.Parameters[3].Value);
        Assert.Equal(
            new DateTimeOffset(638962560000000000, TimeSpan.FromHours(8)).ToUniversalTime(),
            request.Parameters[4].Value);
        Assert.Equal(TimeSpan.FromTicks(-1234567), request.Parameters[5].Value);
        Assert.Equal(new TimeOnly(12, 34, 56, 789), request.Parameters[6].Value);
        Assert.Equal(new DateTime(638962560000000000, DateTimeKind.Utc), request.Parameters[7].Value);
        Assert.Equal(new float[] { 1.5f, -2.25f }, request.Parameters[8].Value);
    }

    [Fact]
    public void Response_RoundTripsCommittedRecordsAffected()
    {
        var writer = new ArrayBufferWriter<byte>();
        SqlExecuteFrameCodec.EncodeResponse(writer, 82, 42);
        var sequence = new ReadOnlySequence<byte>(writer.WrittenMemory);
        Assert.True(FrameCodec.TryReadFrame(ref sequence, out var header, out var payload));
        Assert.Equal((byte)FrameFlags.Response, header.Flags);
        Assert.Equal(42, SqlExecuteFrameCodec.DecodeResponse(payload.ToArray()).RecordsAffected);
    }

    [Fact]
    public void Request_InvalidBooleanPayload_IsRejected()
    {
        var writer = new ArrayBufferWriter<byte>();
        SqlExecuteFrameCodec.EncodeRequest(writer, 83, "demo", "INSERT INTO t VALUES (@flag)",
            [SqlExecuteFrameCodec.FromClr("flag", true)]);
        byte[] bytes = writer.WrittenMemory.ToArray();
        bytes[^1] = 2;
        var sequence = new ReadOnlySequence<byte>(bytes);
        Assert.True(FrameCodec.TryReadFrame(ref sequence, out _, out var payload));
        Assert.Throws<FrameFormatException>(() => SqlExecuteFrameCodec.DecodeRequest(payload.ToArray()));
    }

    [Fact]
    public void Request_TypedNullAndDecimalBits_PreserveDeclarationsAndScale()
    {
        var writer = new ArrayBufferWriter<byte>();
        decimal number = new(0, 0, 0, true, 28);
        SqlExecuteFrameCodec.EncodeRequest(writer, 1, "db", "INSERT INTO t VALUES (@n,@v)",
            [new("n", SqlFrameParameterKind.DateTimeOffset, null), SqlExecuteFrameCodec.FromClr("v", number)]);
        var request = SqlExecuteFrameCodec.DecodeRequest(writer.WrittenSpan[FrameHeader.Size..]);
        Assert.Equal(SqlFrameParameterKind.DateTimeOffset, request.Parameters[0].Kind);
        Assert.Null(request.Parameters[0].Value);
        Assert.Equal(decimal.GetBits(number), decimal.GetBits(Assert.IsType<decimal>(request.Parameters[1].Value)));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("mixed")]
    [InlineData("prefix")]
    [InlineData("nan")]
    [InlineData("invalid_utf16")]
    [InlineData("sql_limit")]
    public void Request_InvalidInput_IsRejectedBeforeWriting(string mode)
    {
        var writer = new ArrayBufferWriter<byte>();
        SqlExecuteFrameParameter[] parameters = mode switch
        {
            "duplicate" => [SqlExecuteFrameCodec.FromClr("Name", 1), SqlExecuteFrameCodec.FromClr("name", 2)],
            "mixed" => [SqlExecuteFrameCodec.FromClr("Name", 1), SqlExecuteFrameCodec.FromClr("", 2)],
            "prefix" => [SqlExecuteFrameCodec.FromClr("@Name", 1)],
            "nan" => [SqlExecuteFrameCodec.FromClr("Name", double.NaN)],
            "invalid_utf16" => [SqlExecuteFrameCodec.FromClr("Name", "\uD800")],
            _ => [],
        };
        Assert.ThrowsAny<ArgumentException>(() => SqlExecuteFrameCodec.EncodeRequest(writer, 1, "db",
            mode == "sql_limit" ? new string('x', SqlExecuteFrameCodec.MaxSqlBytes + 1) : "INSERT INTO t VALUES (@Name)", parameters));
        Assert.Equal(0, writer.WrittenCount);
    }

    [Fact]
    public void Request_EveryTruncationAndTrailingBytes_IsRejected()
    {
        var writer = new ArrayBufferWriter<byte>();
        SqlExecuteFrameCodec.EncodeRequest(writer, 1, "db", "INSERT INTO t VALUES (@v)",
            [SqlExecuteFrameCodec.FromClr("v", decimal.MaxValue)]);
        byte[] payload = writer.WrittenMemory[FrameHeader.Size..].ToArray();
        for (int length = 0; length < payload.Length; length++)
            Assert.Throws<FrameFormatException>(() => SqlExecuteFrameCodec.DecodeRequest(payload.AsSpan(0, length)));
        Assert.Throws<FrameFormatException>(() => SqlExecuteFrameCodec.DecodeRequest([.. payload, 0]));
    }

    [Fact]
    public void Request_MalformedUtf8AndDecimalFlags_IsRejected()
    {
        var writer = new ArrayBufferWriter<byte>();
        SqlExecuteFrameCodec.EncodeRequest(writer, 1, "db", "INSERT INTO t VALUES (@v)",
            [SqlExecuteFrameCodec.FromClr("v", decimal.MaxValue)]);
        byte[] payload = writer.WrittenMemory[FrameHeader.Size..].ToArray();
        payload[2] = 255;
        Assert.Throws<FrameFormatException>(() => SqlExecuteFrameCodec.DecodeRequest(payload));
        payload[2] = (byte)'d';
        payload[^4] = 1; // decimal flags 保留位。
        Assert.Throws<FrameFormatException>(() => SqlExecuteFrameCodec.DecodeRequest(payload));
    }
}
