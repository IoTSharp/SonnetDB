using System.Buffers;
using System.Net;
using System.Text;
using SonnetDB.Data.Remote;
using SonnetDB.Protocol;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests;

public sealed class RemoteDateTime2ResponseTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(7, true)]
    public void QueryFrames_LegacyAndV7EmptyOrMultiChunk_AcceptHistoricalContracts(int version, bool empty)
    {
        var writer = new ArrayBufferWriter<byte>();
        DateTime value = version == 7 ? DateTime.UnixEpoch.AddTicks(1) : DateTime.UnixEpoch;
        SqlFrameCodec.EncodeQueryMetaFrame(writer, 1, ["d"], version == 1 ? null
            : [new SelectColumnInfo(version == 7 ? TableColumnType.DateTime2 : TableColumnType.DateTime)],
            version >= 3, version >= 4, version >= 5, version >= 6, version >= 7);
        if (!empty)
            for (int chunk = 0; chunk < 2; chunk++)
                SqlFrameCodec.EncodeQueryRowsFrame(writer, 1, [new object?[] { value }], 0, 1, 1,
                    version >= 2, version >= 4, version >= 5, version >= 6);
        SqlFrameCodec.EncodeQueryEndFrame(writer, 1, empty ? 0 : 2, 0);
        using var result = RemoteConnectionImpl.BuildSqlResult(Frames(writer));
        Assert.Equal("d", Assert.Single(result.Columns));
        int rows = 0;
        for (; rows < 3 && result.ReadNextRow(); rows++)
            Assert.Equal(value, result.GetValue(0));
        Assert.Equal(empty ? 0 : 2, rows);
        if (version == 7) Assert.Equal(TableColumnType.DateTime2, result.GetColumnMetadata(0)?.DataType);
    }

    [Fact]
    public void QueryFrames_DuplicateMissingOrMismatchedTerminals_RejectWithoutPublishingResult()
    {
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryMetaFrame(writer, 1, ["d"], [new(TableColumnType.DateTime2)], true, true, true, true, true);
        SqlFrameCodec.EncodeQueryRowsFrame(writer, 1, [new object?[] { DateTime.UnixEpoch.AddTicks(1) }], 0, 1, 1, true, true, true, true);
        SqlFrameCodec.EncodeQueryEndFrame(writer, 1, 1, 0);
        FrameMessage[] valid = Frames(writer);
        var countWriter = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryEndFrame(countWriter, 1, 2, 0);
        FrameMessage wrongCount = Assert.Single(Frames(countWriter));
        var columnsWriter = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRowsFrame(columnsWriter, 1, [new object?[] { DateTime.UnixEpoch.AddTicks(1), null }], 0, 1, 2, true, true, true, true);
        FrameMessage wrongColumns = Assert.Single(Frames(columnsWriter));
        var typeWriter = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRowsFrame(typeWriter, 1, [new object?[] { DateTimeOffset.UnixEpoch }], 0, 1, 1, true, true, true, true);
        FrameMessage wrongType = Assert.Single(Frames(typeWriter));
        var wrongStream = valid[2] with
        {
            Header = new FrameHeader((uint)valid[2].Payload.Length,
            FrameHeader.CurrentVersion, (byte)FrameService.Sql, (byte)SqlFrameOp.Query, (byte)FrameFlags.Response, 2)
        };
        FrameMessage[][] malformed =
        [
            [valid[0], valid[0], valid[1], valid[2]],
            [valid[1], valid[0], valid[2]],
            [valid[0], valid[1]],
            [valid[0], valid[1], valid[2], valid[2]],
            [valid[0], valid[1], wrongCount],
            [valid[0], wrongColumns, valid[2]],
            [valid[0], wrongType, valid[2]],
            [valid[0], valid[1], wrongStream],
            [],
        ];
        foreach (var frames in malformed)
            Assert.Throws<InvalidDataException>(() => RemoteConnectionImpl.BuildSqlResult(frames));
    }

    private static FrameMessage[] Frames(ArrayBufferWriter<byte> writer)
    {
        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        var frames = new List<FrameMessage>();
        for (int index = 0; buffer.Length > 0 && index < 5; index++)
        {
            Assert.True(FrameCodec.TryReadFrame(ref buffer, out var header, out var payload));
            frames.Add(new(header, payload.ToArray()));
        }
        Assert.Equal(0, buffer.Length);
        return frames.ToArray();
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(621355967999999999L)]
    [InlineData(621355968000000001L)]
    [InlineData(639270596961230001L)]
    [InlineData(3155378975999999999L)]
    public async Task DateTime2_EntireRange_PreservesDateTimeTicks(long ticks)
    {
        using var response = Response("7");
        using var stream = Payload($"{{\"$type\":\"datetime\",\"ticks\":{ticks},\"kind\":1}}");
        using var result = await RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None);
        Assert.Equal(typeof(DateTime), result.GetFieldType(0));
        Assert.Equal("DateTime2", result.GetColumnMetadata(0)?.DataType?.ToString());
        Assert.True(result.ReadNextRow());
        DateTime value = Assert.IsType<DateTime>(result.GetValue(0));
        Assert.Equal(ticks, value.Ticks);
        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.False(result.ReadNextRow());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("4")]
    [InlineData("5")]
    [InlineData("6")]
    [InlineData("7,7")]
    public async Task DateTime2_MissingOrAmbiguousCapability_RejectsBeforeRows(string? version)
    {
        using var response = Response(version);
        using var stream = Payload(null);
        await Assert.ThrowsAsync<InvalidDataException>(() => RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"$type\":\"datetime\",\"ticks\":-1,\"kind\":1}")]
    [InlineData("{\"$type\":\"datetime\",\"ticks\":3155378976000000000,\"kind\":1}")]
    [InlineData("{\"$type\":\"datetime\",\"ticks\":0,\"kind\":3}")]
    [InlineData("{\"$type\":\"datetime\",\"ticks\":0,\"ticks\":1,\"kind\":1}")]
    [InlineData("{\"$type\":\"datetimeoffset\",\"ticks\":0}")]
    [InlineData("\"1970-01-01T00:00:00Z\"")]
    [InlineData("0")]
    public async Task DateTime2_MalformedEnvelope_Rejects(string value)
    {
        using var response = Response("7");
        using var stream = Payload(value);
        using var result = await RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => result.ReadNextRow());
    }

    [Fact]
    public async Task DateTime2_MissingEnd_PreservesUnknownResult()
    {
        using var response = Response("7");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"type\":\"meta\",\"columns\":[\"value\"],\"columnTypes\":[\"datetime2\"]}\n"));
        using var result = await RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => result.ReadNextRow());
    }

    private static HttpResponseMessage Response(string? version)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (version is not null) response.Headers.Add(SqlFrameCodec.ResultVersionHeader, version);
        return response;
    }

    private static MemoryStream Payload(string? value)
        => new(Encoding.UTF8.GetBytes("{\"type\":\"meta\",\"columns\":[\"value\"],\"columnTypes\":[\"datetime2\"]}\n"
            + (value is null ? string.Empty : $"[{value}]\n")
            + $"{{\"type\":\"end\",\"rowCount\":{(value is null ? 0 : 1)},\"recordsAffected\":-1}}\n"));
}
