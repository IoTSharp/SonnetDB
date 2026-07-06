using System.Buffers;
using SonnetDB.Kv;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Core.Tests.Protocol;

/// <summary>
/// <see cref="KvFrameCodec"/> get / put / scan 三个 opcode 的编解码测试（M28 P5b #240）。
/// </summary>
public sealed class KvFrameCodecTests
{
    // ────────────────────────────── get ──────────────────────────────

    [Fact]
    public void Get_RoundTrip()
    {
        byte[] key = [0x01, 0xFF, 0x00, 0x7A];
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeGetRequest(writer, 7, "demo", "cache", key);

        var frame = ParseSingleFrame(writer, out FrameHeader header);
        Assert.Equal((byte)FrameService.Kv, header.Service);
        Assert.Equal((byte)KvFrameOp.Get, header.Op);
        Assert.Equal(7u, header.StreamId);
        Assert.False(header.IsResponse);

        KvGetFrameRequest request = KvFrameCodec.DecodeGetRequest(frame);
        Assert.Equal("demo", request.Db);
        Assert.Equal("cache", request.Keyspace);
        Assert.Equal(key, request.Key.ToArray());
    }

    [Fact]
    public void GetResponse_Found_RoundTrip_WithExpiry()
    {
        var expires = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var entry = new KvEntry(new byte[] { 1 }, new byte[] { 0xAA, 0xBB }, 42, expires);
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeGetResponse(writer, 3, entry);

        var frame = ParseSingleFrame(writer, out FrameHeader header);
        Assert.True(header.IsResponse);
        Assert.False(header.IsError);

        KvGetFrameResult? result = KvFrameCodec.DecodeGetResponse(frame);
        Assert.NotNull(result);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, result.Value);
        Assert.Equal(42, result.Version);
        Assert.Equal(expires, result.ExpiresAtUtc);
    }

    [Fact]
    public void GetResponse_NotFound_RoundTrip()
    {
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeGetResponse(writer, 3, entry: null);

        var frame = ParseSingleFrame(writer, out _);
        Assert.Null(KvFrameCodec.DecodeGetResponse(frame));
    }

    [Fact]
    public void GetResponse_EmptyValue_NoExpiry_RoundTrip()
    {
        var entry = new KvEntry(new byte[] { 1 }, ReadOnlyMemory<byte>.Empty, 1);
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeGetResponse(writer, 1, entry);

        KvGetFrameResult? result = KvFrameCodec.DecodeGetResponse(ParseSingleFrame(writer, out _));
        Assert.NotNull(result);
        Assert.Empty(result.Value);
        Assert.Null(result.ExpiresAtUtc);
    }

    // ────────────────────────────── put ──────────────────────────────

    [Fact]
    public void Put_RoundTrip_WithExpiry()
    {
        byte[] key = "device:001"u8.ToArray();
        byte[] value = new byte[1024];
        Random.Shared.NextBytes(value);
        var expires = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodePutRequest(writer, 9, "demo", "cache", key, value, expires);

        var frame = ParseSingleFrame(writer, out FrameHeader header);
        Assert.Equal((byte)KvFrameOp.Put, header.Op);

        KvPutFrameRequest request = KvFrameCodec.DecodePutRequest(frame);
        Assert.Equal("demo", request.Db);
        Assert.Equal("cache", request.Keyspace);
        Assert.Equal(key, request.Key.ToArray());
        Assert.Equal(value, request.Value.ToArray());
        Assert.Equal(expires, request.ExpiresAtUtc);
    }

    [Fact]
    public void Put_RoundTrip_EmptyValue_NoExpiry()
    {
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodePutRequest(writer, 1, "d", "k", [0x01], ReadOnlySpan<byte>.Empty);

        KvPutFrameRequest request = KvFrameCodec.DecodePutRequest(ParseSingleFrame(writer, out _));
        Assert.True(request.Value.IsEmpty);
        Assert.Null(request.ExpiresAtUtc);
    }

    [Fact]
    public void PutResponse_RoundTrip()
    {
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodePutResponse(writer, 2, 987654321L);

        var frame = ParseSingleFrame(writer, out FrameHeader header);
        Assert.True(header.IsResponse);
        Assert.Equal(987654321L, KvFrameCodec.DecodePutResponse(frame.Span));
    }

    // ────────────────────────────── scan ──────────────────────────────

    [Fact]
    public void Scan_RoundTrip_AllOptions()
    {
        byte[] prefix = "user:"u8.ToArray();
        byte[] afterKey = "user:1000"u8.ToArray();
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeScanRequest(writer, 4, "demo", "cache", prefix, afterKey, 250);

        var frame = ParseSingleFrame(writer, out FrameHeader header);
        Assert.Equal((byte)KvFrameOp.Scan, header.Op);

        KvScanFrameRequest request = KvFrameCodec.DecodeScanRequest(frame);
        Assert.Equal(prefix, request.Prefix.ToArray());
        Assert.Equal(afterKey, request.AfterKey.ToArray());
        Assert.Equal(250, request.Limit);
    }

    [Fact]
    public void Scan_RoundTrip_EmptyPrefix_DefaultLimit()
    {
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeScanRequest(writer, 1, "d", "k", ReadOnlySpan<byte>.Empty);

        KvScanFrameRequest request = KvFrameCodec.DecodeScanRequest(ParseSingleFrame(writer, out _));
        Assert.True(request.Prefix.IsEmpty);
        Assert.True(request.AfterKey.IsEmpty);
        Assert.Equal(0, request.Limit);
    }

    [Fact]
    public void ScanResponse_RoundTrip_MixedEntries()
    {
        var expires = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var entries = new List<KvEntry>
        {
            new("a"u8.ToArray(), new byte[] { 1, 2, 3 }, 10),
            new("b"u8.ToArray(), ReadOnlyMemory<byte>.Empty, 11, expires),
            new(new byte[] { 0x00, 0xFE }, new byte[512], 12),
        };
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeScanResponse(writer, 6, entries);

        KvScanFrameEntry[] decoded = KvFrameCodec.DecodeScanResponse(ParseSingleFrame(writer, out _));
        Assert.Equal(3, decoded.Length);
        Assert.Equal("a"u8.ToArray(), decoded[0].Key);
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded[0].Value);
        Assert.Equal(10, decoded[0].Version);
        Assert.Null(decoded[0].ExpiresAtUtc);
        Assert.Empty(decoded[1].Value);
        Assert.Equal(expires, decoded[1].ExpiresAtUtc);
        Assert.Equal(new byte[] { 0x00, 0xFE }, decoded[2].Key);
        Assert.Equal(512, decoded[2].Value.Length);
    }

    [Fact]
    public void ScanResponse_RoundTrip_Empty()
    {
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeScanResponse(writer, 6, []);

        Assert.Empty(KvFrameCodec.DecodeScanResponse(ParseSingleFrame(writer, out _)));
    }

    // ────────────────────────────── 解码防御 ──────────────────────────────

    [Fact]
    public void DecodeGet_KeyLengthBomb_ThrowsBeforeAllocation()
    {
        var writer = new ArrayBufferWriter<byte>();
        var payload = new ArrayBufferWriter<byte>();
        WriteVarString(payload, "d");
        WriteVarString(payload, "k");
        WriteVarUInt(payload, 100_000); // key 长度声明超过 MaxKeyBytes
        WriteFrame(writer, (byte)KvFrameOp.Get, payload);

        var frame = ParseSingleFrame(writer, out _);
        var ex = Assert.Throws<FrameFormatException>(() => KvFrameCodec.DecodeGetRequest(frame));
        Assert.Contains("key", ex.Message);
    }

    [Fact]
    public void DecodePut_ValueLengthExceedsRemaining_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        var payload = new ArrayBufferWriter<byte>();
        WriteVarString(payload, "d");
        WriteVarString(payload, "k");
        WriteVarUInt(payload, 1);
        payload.Write(new byte[] { 0x01 });
        payload.Write(new byte[] { 0x00 }); // 无过期时间
        WriteVarUInt(payload, 500); // value 声明 500 字节但帧体没有
        WriteFrame(writer, (byte)KvFrameOp.Put, payload);

        Assert.Throws<FrameFormatException>(() => KvFrameCodec.DecodePutRequest(ParseSingleFrame(writer, out _)));
    }

    [Fact]
    public void DecodePut_BadExpiryMarker_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        var payload = new ArrayBufferWriter<byte>();
        WriteVarString(payload, "d");
        WriteVarString(payload, "k");
        WriteVarUInt(payload, 1);
        payload.Write(new byte[] { 0x01 });
        payload.Write(new byte[] { 0x07 }); // 过期时间标记非法
        WriteVarUInt(payload, 0);
        WriteFrame(writer, (byte)KvFrameOp.Put, payload);

        Assert.Throws<FrameFormatException>(() => KvFrameCodec.DecodePutRequest(ParseSingleFrame(writer, out _)));
    }

    [Fact]
    public void DecodeGet_TrailingBytes_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeGetRequest(writer, 1, "d", "k", [0x01]);
        // 尾部追加多余字节后重打帧
        var frame = ParseSingleFrame(writer, out _);
        byte[] tampered = [.. frame.ToArray(), 0xEE];
        var rewrapped = new ArrayBufferWriter<byte>();
        WriteRawFrame(rewrapped, (byte)KvFrameOp.Get, tampered);

        Assert.Throws<FrameFormatException>(() => KvFrameCodec.DecodeGetRequest(ParseSingleFrame(rewrapped, out _)));
    }

    [Fact]
    public void EncodeGet_KeyTooLong_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        byte[] hugeKey = new byte[KvFrameCodec.MaxKeyBytes + 1];
        Assert.Throws<ArgumentException>(() => KvFrameCodec.EncodeGetRequest(writer, 1, "d", "k", hugeKey));
    }

    // ────────────────────────────── 辅助 ──────────────────────────────

    private static ReadOnlyMemory<byte> ParseSingleFrame(ArrayBufferWriter<byte> writer, out FrameHeader header)
    {
        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        Assert.True(FrameCodec.TryReadFrame(ref buffer, out header, out ReadOnlySequence<byte> payload));
        Assert.Equal(0, buffer.Length);
        return payload.ToArray();
    }

    private static void WriteFrame(ArrayBufferWriter<byte> writer, byte op, ArrayBufferWriter<byte> payload)
        => WriteRawFrame(writer, op, payload.WrittenSpan.ToArray());

    private static void WriteRawFrame(ArrayBufferWriter<byte> writer, byte op, byte[] payload)
    {
        var header = new FrameHeader((uint)payload.Length, FrameHeader.CurrentVersion,
            (byte)FrameService.Kv, op, 0, 1);
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
