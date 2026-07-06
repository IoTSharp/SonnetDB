using System.Buffers;
using SonnetDB.Documents;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Core.Tests.Protocol;

/// <summary>
/// <see cref="DocFrameCodec"/> find / insert 两个 opcode 的编解码测试（M28 P5b #240）。
/// </summary>
public sealed class DocFrameCodecTests
{
    // ────────────────────────────── find ──────────────────────────────

    [Fact]
    public void FindRequest_RoundTrip_ByIds()
    {
        var writer = new ArrayBufferWriter<byte>();
        DocFrameCodec.EncodeFindRequest(writer, 7, "demo", "users", ["u-1", "u-2", "文档-3"]);

        var frame = ParseSingleFrame(writer, out FrameHeader header);
        Assert.Equal((byte)FrameService.Doc, header.Service);
        Assert.Equal((byte)DocFrameOp.Find, header.Op);
        Assert.Equal(7u, header.StreamId);

        DocFindFrameRequest request = DocFrameCodec.DecodeFindRequest(frame.Span);
        Assert.Equal("demo", request.Db);
        Assert.Equal("users", request.Collection);
        Assert.Equal(["u-1", "u-2", "文档-3"], request.Ids);
        Assert.Null(request.AfterId);
    }

    [Fact]
    public void FindRequest_RoundTrip_ScanPaging()
    {
        var writer = new ArrayBufferWriter<byte>();
        DocFrameCodec.EncodeFindRequest(writer, 1, "d", "c", ids: null, afterId: "u-500", limit: 200);

        DocFindFrameRequest request = DocFrameCodec.DecodeFindRequest(ParseSingleFrame(writer, out _).Span);
        Assert.Empty(request.Ids);
        Assert.Equal("u-500", request.AfterId);
        Assert.Equal(200, request.Limit);
    }

    [Fact]
    public void FindResponse_RoundTrip()
    {
        var rows = new List<DocumentRow>
        {
            new("u-1", """{"name":"张三","age":30}""", 5),
            new("u-2", """{"name":"li","nested":{"a":[1,2,3]}}""", 9),
        };
        var writer = new ArrayBufferWriter<byte>();
        DocFrameCodec.EncodeFindResponse(writer, 4, rows);

        var frame = ParseSingleFrame(writer, out FrameHeader header);
        Assert.True(header.IsResponse);

        DocumentRow[] decoded = DocFrameCodec.DecodeFindResponse(frame.Span);
        Assert.Equal(2, decoded.Length);
        Assert.Equal("u-1", decoded[0].Id);
        Assert.Equal("""{"name":"张三","age":30}""", decoded[0].Json);
        Assert.Equal(5, decoded[0].Version);
        Assert.Equal(9, decoded[1].Version);
    }

    [Fact]
    public void FindResponse_RoundTrip_Empty()
    {
        var writer = new ArrayBufferWriter<byte>();
        DocFrameCodec.EncodeFindResponse(writer, 4, []);

        Assert.Empty(DocFrameCodec.DecodeFindResponse(ParseSingleFrame(writer, out _).Span));
    }

    // ────────────────────────────── insert ──────────────────────────────

    [Fact]
    public void InsertRequest_RoundTrip()
    {
        var documents = new List<DocumentWriteRequest>
        {
            new("a", """{"v":1}"""),
            new("b", """{"v":2,"中文":"值"}"""),
        };
        var writer = new ArrayBufferWriter<byte>();
        DocFrameCodec.EncodeInsertRequest(writer, 9, "demo", "users", documents, ordered: false);

        var frame = ParseSingleFrame(writer, out FrameHeader header);
        Assert.Equal((byte)DocFrameOp.Insert, header.Op);

        DocInsertFrameRequest request = DocFrameCodec.DecodeInsertRequest(frame.Span);
        Assert.Equal("demo", request.Db);
        Assert.Equal("users", request.Collection);
        Assert.False(request.Ordered);
        Assert.Equal(2, request.Documents.Count);
        Assert.Equal("a", request.Documents[0].Id);
        Assert.Equal("""{"v":2,"中文":"值"}""", request.Documents[1].Json);
    }

    [Fact]
    public void InsertResponse_RoundTrip_Success()
    {
        var writer = new ArrayBufferWriter<byte>();
        DocFrameCodec.EncodeInsertResponse(writer, 2, new DocumentWriteResult(inserted: 42));

        var frame = ParseSingleFrame(writer, out FrameHeader header);
        Assert.True(header.IsResponse);

        DocumentWriteResult result = DocFrameCodec.DecodeInsertResponse(frame.Span);
        Assert.Equal(42, result.Inserted);
        Assert.Empty(result.Errors);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void InsertResponse_RoundTrip_WithErrors()
    {
        var errors = new List<DocumentWriteError>
        {
            new(1, "dup-id", DocumentWriteErrorCodes.DuplicateKey, "document id 'dup-id' 已存在。"),
            new(3, null, DocumentWriteErrorCodes.ValidationFailed, "JSON 非法。", DocumentWriteErrorSeverity.Warning),
        };
        var writer = new ArrayBufferWriter<byte>();
        DocFrameCodec.EncodeInsertResponse(writer, 2, new DocumentWriteResult(inserted: 2, errors: errors));

        DocumentWriteResult result = DocFrameCodec.DecodeInsertResponse(ParseSingleFrame(writer, out _).Span);
        Assert.Equal(2, result.Inserted);
        Assert.Equal(2, result.Errors.Count);
        Assert.Equal(1, result.Errors[0].Index);
        Assert.Equal("dup-id", result.Errors[0].Id);
        Assert.Equal(DocumentWriteErrorCodes.DuplicateKey, result.Errors[0].Code);
        Assert.Null(result.Errors[1].Id);
        Assert.Equal(DocumentWriteErrorSeverity.Warning, result.Errors[1].Severity);
        Assert.True(result.HasErrors);
        Assert.True(result.HasWarnings);
    }

    // ────────────────────────────── 编码防御 ──────────────────────────────

    [Fact]
    public void EncodeInsert_EmptyDocuments_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>(() =>
            DocFrameCodec.EncodeInsertRequest(writer, 1, "d", "c", []));
    }

    [Fact]
    public void EncodeFind_TooManyIds_Throws()
    {
        var ids = new string[DocFrameCodec.MaxDocumentCount + 1];
        Array.Fill(ids, "x");
        var writer = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>(() =>
            DocFrameCodec.EncodeFindRequest(writer, 1, "d", "c", ids));
    }

    // ────────────────────────────── 解码防御 ──────────────────────────────

    [Fact]
    public void DecodeFind_IdCountBomb_ThrowsBeforeAllocation()
    {
        var payload = new ArrayBufferWriter<byte>();
        WriteVarString(payload, "d");
        WriteVarString(payload, "c");
        WriteVarUInt(payload, 4000); // 声明 4000 条 id 但帧体没有
        var writer = new ArrayBufferWriter<byte>();
        WriteRawFrame(writer, (byte)DocFrameOp.Find, payload.WrittenSpan.ToArray());

        Assert.Throws<FrameFormatException>(() => DocFrameCodec.DecodeFindRequest(ParseSingleFrame(writer, out _).Span));
    }

    [Fact]
    public void DecodeFind_EmptyId_Throws()
    {
        var payload = new ArrayBufferWriter<byte>();
        WriteVarString(payload, "d");
        WriteVarString(payload, "c");
        WriteVarUInt(payload, 1);
        WriteVarString(payload, ""); // 空 id
        WriteVarString(payload, "");
        WriteVarUInt(payload, 0);
        var writer = new ArrayBufferWriter<byte>();
        WriteRawFrame(writer, (byte)DocFrameOp.Find, payload.WrittenSpan.ToArray());

        Assert.Throws<FrameFormatException>(() => DocFrameCodec.DecodeFindRequest(ParseSingleFrame(writer, out _).Span));
    }

    [Fact]
    public void DecodeInsert_ZeroCount_Throws()
    {
        var payload = new ArrayBufferWriter<byte>();
        WriteVarString(payload, "d");
        WriteVarString(payload, "c");
        payload.Write(new byte[] { 0x01 }); // ordered
        WriteVarUInt(payload, 0); // count=0
        var writer = new ArrayBufferWriter<byte>();
        WriteRawFrame(writer, (byte)DocFrameOp.Insert, payload.WrittenSpan.ToArray());

        Assert.Throws<FrameFormatException>(() => DocFrameCodec.DecodeInsertRequest(ParseSingleFrame(writer, out _).Span));
    }

    [Fact]
    public void DecodeInsert_BadOrderedMarker_Throws()
    {
        var payload = new ArrayBufferWriter<byte>();
        WriteVarString(payload, "d");
        WriteVarString(payload, "c");
        payload.Write(new byte[] { 0x05 }); // ordered 非 0/1
        WriteVarUInt(payload, 1);
        WriteVarString(payload, "a");
        WriteVarString(payload, "{}");
        var writer = new ArrayBufferWriter<byte>();
        WriteRawFrame(writer, (byte)DocFrameOp.Insert, payload.WrittenSpan.ToArray());

        Assert.Throws<FrameFormatException>(() => DocFrameCodec.DecodeInsertRequest(ParseSingleFrame(writer, out _).Span));
    }

    [Fact]
    public void DecodeInsert_TrailingBytes_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        DocFrameCodec.EncodeInsertRequest(writer, 1, "d", "c", [new DocumentWriteRequest("a", "{}")]);
        byte[] tampered = [.. ParseSingleFrame(writer, out _).ToArray(), 0xEE];
        var rewrapped = new ArrayBufferWriter<byte>();
        WriteRawFrame(rewrapped, (byte)DocFrameOp.Insert, tampered);

        Assert.Throws<FrameFormatException>(() => DocFrameCodec.DecodeInsertRequest(ParseSingleFrame(rewrapped, out _).Span));
    }

    // ────────────────────────────── 辅助 ──────────────────────────────

    private static ReadOnlyMemory<byte> ParseSingleFrame(ArrayBufferWriter<byte> writer, out FrameHeader header)
    {
        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        Assert.True(FrameCodec.TryReadFrame(ref buffer, out header, out ReadOnlySequence<byte> payload));
        Assert.Equal(0, buffer.Length);
        return payload.ToArray();
    }

    private static void WriteRawFrame(ArrayBufferWriter<byte> writer, byte op, byte[] payload)
    {
        var header = new FrameHeader((uint)payload.Length, FrameHeader.CurrentVersion,
            (byte)FrameService.Doc, op, 0, 1);
        Span<byte> headerBytes = stackalloc byte[FrameHeader.Size];
        header.Write(headerBytes);
        writer.Write(headerBytes);
        writer.Write(payload);
    }

    private static void WriteVarString(ArrayBufferWriter<byte> writer, string value)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
        WriteVarUInt(writer, (uint)bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteVarUInt(ArrayBufferWriter<byte> writer, uint value)
    {
        while (value >= 0x80)
        {
            writer.Write([(byte)(value | 0x80)]);
            value >>= 7;
        }
        writer.Write([(byte)value]);
    }
}
