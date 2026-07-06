using System.Buffers;
using SonnetDB.ObjectStorage;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Core.Tests.Protocol;

/// <summary>
/// <see cref="ObjectFrameCodec"/> get / put 两个 opcode 的编解码测试（M28 P5b #240）。
/// </summary>
public sealed class ObjectFrameCodecTests
{
    private static SndbObjectInfo MakeInfo(
        long size = 3,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyDictionary<string, string>? tags = null)
        => new(
            "bkt", "path/to/key", "v1abc", "application/octet-stream", size,
            "\"etag123\"", new string('a', 64), IsDeleteMarker: false,
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero),
            metadata ?? new Dictionary<string, string>(),
            tags ?? new Dictionary<string, string>());

    // ────────────────────────────── get 请求 ──────────────────────────────

    [Fact]
    public void GetRequest_RoundTrip_LatestVersion()
    {
        var writer = new ArrayBufferWriter<byte>();
        ObjectFrameCodec.EncodeGetRequest(writer, 8, "demo", "assets", "docs/图片.png");

        var frame = ParseSingleFrame(writer, out FrameHeader header);
        Assert.Equal((byte)FrameService.Object, header.Service);
        Assert.Equal((byte)ObjectFrameOp.Get, header.Op);
        Assert.Equal(8u, header.StreamId);

        ObjectGetFrameRequest request = ObjectFrameCodec.DecodeGetRequest(frame.Span);
        Assert.Equal("demo", request.Db);
        Assert.Equal("assets", request.Bucket);
        Assert.Equal("docs/图片.png", request.Key);
        Assert.Null(request.VersionId);
    }

    [Fact]
    public void GetRequest_RoundTrip_ExplicitVersion()
    {
        var writer = new ArrayBufferWriter<byte>();
        ObjectFrameCodec.EncodeGetRequest(writer, 1, "d", "bkt", "k", "v-123");

        ObjectGetFrameRequest request = ObjectFrameCodec.DecodeGetRequest(ParseSingleFrame(writer, out _).Span);
        Assert.Equal("v-123", request.VersionId);
    }

    // ────────────────────────────── get 响应帧序列 ──────────────────────────────

    [Fact]
    public void GetResponseSequence_MetaDataEnd_RoundTrip()
    {
        var metadata = new Dictionary<string, string> { ["author"] = "张三" };
        var tags = new Dictionary<string, string> { ["env"] = "prod", ["ver"] = "2" };
        var info = MakeInfo(size: 6, metadata, tags);

        var writer = new ArrayBufferWriter<byte>();
        ObjectFrameCodec.EncodeGetMetaFrame(writer, 5, info);
        ObjectFrameCodec.EncodeGetDataFrame(writer, 5, [1, 2, 3]);
        ObjectFrameCodec.EncodeGetDataFrame(writer, 5, [4, 5, 6]);
        ObjectFrameCodec.EncodeGetEndFrame(writer, 5, 6);

        var frames = ParseFrames(writer);
        Assert.Equal(4, frames.Count);
        Assert.All(frames, f => Assert.True(f.Header.IsResponse));
        Assert.All(frames, f => Assert.Equal(5u, f.Header.StreamId));

        Assert.Equal(ObjectChunkKind.Meta, ObjectFrameCodec.PeekChunkKind(frames[0].Payload.Span));
        ObjectGetFrameMeta meta = ObjectFrameCodec.DecodeGetMetaFrame(frames[0].Payload.Span);
        Assert.Equal("v1abc", meta.VersionId);
        Assert.Equal("application/octet-stream", meta.ContentType);
        Assert.Equal(6, meta.SizeBytes);
        Assert.Equal("\"etag123\"", meta.ETag);
        Assert.Equal(new string('a', 64), meta.Sha256);
        Assert.Equal("张三", meta.Metadata["author"]);
        Assert.Equal(2, meta.Tags.Count);

        Assert.Equal(ObjectChunkKind.Data, ObjectFrameCodec.PeekChunkKind(frames[1].Payload.Span));
        Assert.Equal(new byte[] { 1, 2, 3 }, ObjectFrameCodec.DecodeGetDataFrame(frames[1].Payload).ToArray());
        Assert.Equal(new byte[] { 4, 5, 6 }, ObjectFrameCodec.DecodeGetDataFrame(frames[2].Payload).ToArray());

        Assert.Equal(ObjectChunkKind.End, ObjectFrameCodec.PeekChunkKind(frames[3].Payload.Span));
        Assert.Equal(6, ObjectFrameCodec.DecodeGetEndFrame(frames[3].Payload.Span));
    }

    [Fact]
    public void GetDataFrame_Empty_RoundTrip()
    {
        var writer = new ArrayBufferWriter<byte>();
        ObjectFrameCodec.EncodeGetDataFrame(writer, 1, ReadOnlySpan<byte>.Empty);

        Assert.True(ObjectFrameCodec.DecodeGetDataFrame(ParseSingleFrame(writer, out _)).IsEmpty);
    }

    // ────────────────────────────── put ──────────────────────────────

    [Fact]
    public void PutRequest_RoundTrip_AllOptions()
    {
        byte[] content = new byte[4096];
        Random.Shared.NextBytes(content);
        var metadata = new Dictionary<string, string> { ["k1"] = "v1" };
        var tags = new Dictionary<string, string> { ["t"] = "1" };

        var writer = new ArrayBufferWriter<byte>();
        ObjectFrameCodec.EncodePutRequest(writer, 3, "demo", "assets", "firmware/fw.bin",
            content, "application/firmware", metadata, tags);

        var frame = ParseSingleFrame(writer, out FrameHeader header);
        Assert.Equal((byte)ObjectFrameOp.Put, header.Op);

        ObjectPutFrameRequest request = ObjectFrameCodec.DecodePutRequest(frame);
        Assert.Equal("demo", request.Db);
        Assert.Equal("assets", request.Bucket);
        Assert.Equal("firmware/fw.bin", request.Key);
        Assert.Equal("application/firmware", request.ContentType);
        Assert.Equal(content, request.Content.ToArray());
        Assert.Equal("v1", request.Metadata["k1"]);
        Assert.Equal("1", request.Tags["t"]);
    }

    [Fact]
    public void PutRequest_RoundTrip_Defaults()
    {
        var writer = new ArrayBufferWriter<byte>();
        ObjectFrameCodec.EncodePutRequest(writer, 1, "d", "bkt", "k", [0x42]);

        ObjectPutFrameRequest request = ObjectFrameCodec.DecodePutRequest(ParseSingleFrame(writer, out _));
        Assert.Null(request.ContentType);
        Assert.Empty(request.Metadata);
        Assert.Empty(request.Tags);
        Assert.Equal(new byte[] { 0x42 }, request.Content.ToArray());
    }

    [Fact]
    public void PutResponse_RoundTrip()
    {
        var writer = new ArrayBufferWriter<byte>();
        ObjectFrameCodec.EncodePutResponse(writer, 2, MakeInfo(size: 1024));

        var frame = ParseSingleFrame(writer, out FrameHeader header);
        Assert.True(header.IsResponse);

        ObjectPutFrameResult result = ObjectFrameCodec.DecodePutResponse(frame.Span);
        Assert.Equal("v1abc", result.VersionId);
        Assert.Equal(1024, result.SizeBytes);
        Assert.Equal("\"etag123\"", result.ETag);
        Assert.Equal(new string('a', 64), result.Sha256);
    }

    // ────────────────────────────── 解码防御 ──────────────────────────────

    [Fact]
    public void DecodePut_ContentLengthExceedsRemaining_Throws()
    {
        var payload = new ArrayBufferWriter<byte>();
        WriteVarString(payload, "d");
        WriteVarString(payload, "bkt");
        WriteVarString(payload, "k");
        WriteVarString(payload, "");
        WriteVarUInt(payload, 0); // metadata
        WriteVarUInt(payload, 0); // tags
        WriteVarUInt(payload, 1_000_000); // content 声明超过剩余
        var writer = new ArrayBufferWriter<byte>();
        WriteRawFrame(writer, (byte)ObjectFrameOp.Put, payload.WrittenSpan.ToArray());

        Assert.Throws<FrameFormatException>(() => ObjectFrameCodec.DecodePutRequest(ParseSingleFrame(writer, out _)));
    }

    [Fact]
    public void DecodePut_MapCountBomb_Throws()
    {
        var payload = new ArrayBufferWriter<byte>();
        WriteVarString(payload, "d");
        WriteVarString(payload, "bkt");
        WriteVarString(payload, "k");
        WriteVarString(payload, "");
        WriteVarUInt(payload, 10_000); // metadata 条目数超上限
        var writer = new ArrayBufferWriter<byte>();
        WriteRawFrame(writer, (byte)ObjectFrameOp.Put, payload.WrittenSpan.ToArray());

        var ex = Assert.Throws<FrameFormatException>(() => ObjectFrameCodec.DecodePutRequest(ParseSingleFrame(writer, out _)));
        Assert.Contains("metadata", ex.Message);
    }

    [Fact]
    public void PeekChunkKind_Invalid_Throws()
    {
        Assert.Throws<FrameFormatException>(() => ObjectFrameCodec.PeekChunkKind([9]));
        Assert.Throws<FrameFormatException>(() => ObjectFrameCodec.PeekChunkKind(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void GetRequest_TrailingBytes_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        ObjectFrameCodec.EncodeGetRequest(writer, 1, "d", "bkt", "k");
        byte[] tampered = [.. ParseSingleFrame(writer, out _).ToArray(), 0xEE];
        var rewrapped = new ArrayBufferWriter<byte>();
        WriteRawFrame(rewrapped, (byte)ObjectFrameOp.Get, tampered);

        Assert.Throws<FrameFormatException>(() => ObjectFrameCodec.DecodeGetRequest(ParseSingleFrame(rewrapped, out _).Span));
    }

    // ────────────────────────────── 辅助 ──────────────────────────────

    private static ReadOnlyMemory<byte> ParseSingleFrame(ArrayBufferWriter<byte> writer, out FrameHeader header)
    {
        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        Assert.True(FrameCodec.TryReadFrame(ref buffer, out header, out ReadOnlySequence<byte> payload));
        Assert.Equal(0, buffer.Length);
        return payload.ToArray();
    }

    private static List<(FrameHeader Header, ReadOnlyMemory<byte> Payload)> ParseFrames(ArrayBufferWriter<byte> writer)
    {
        var frames = new List<(FrameHeader, ReadOnlyMemory<byte>)>();
        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        while (FrameCodec.TryReadFrame(ref buffer, out FrameHeader header, out ReadOnlySequence<byte> payload))
            frames.Add((header, payload.ToArray()));
        Assert.Equal(0, buffer.Length);
        return frames;
    }

    private static void WriteRawFrame(ArrayBufferWriter<byte> writer, byte op, byte[] payload)
    {
        var header = new FrameHeader((uint)payload.Length, FrameHeader.CurrentVersion,
            (byte)FrameService.Object, op, 0, 1);
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
