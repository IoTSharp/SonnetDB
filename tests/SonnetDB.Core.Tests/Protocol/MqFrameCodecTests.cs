using System.Buffers;
using SonnetDB.IO;
using SonnetDB.Protocol;
using SonnetMQ;
using Xunit;

namespace SonnetDB.Core.Tests.Protocol;

/// <summary>
/// <see cref="MqFrameCodec"/> 四个 opcode 的编解码测试。
/// </summary>
public sealed class MqFrameCodecTests
{
    // ────────────────────────────── publish ──────────────────────────────

    [Fact]
    public void Publish_RoundTrip_NoHeaders()
    {
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishRequest(writer, 5, "demo", "sensor-events", null, payload);

        var frame = ParseSingleFrame(writer, out FrameHeader header);
        Assert.Equal((byte)FrameService.Mq, header.Service);
        Assert.Equal((byte)MqFrameOp.Publish, header.Op);
        Assert.Equal(5u, header.StreamId);
        Assert.False(header.IsResponse);

        MqPublishFrameRequest request = MqFrameCodec.DecodePublishRequest(frame);
        Assert.Equal("demo", request.Db);
        Assert.Equal("sensor-events", request.Topic);
        Assert.Empty(request.Headers);
        Assert.Equal(payload, request.Payload.ToArray());
    }

    [Fact]
    public void Publish_RoundTrip_UnicodeHeaders_EmptyPayload()
    {
        var headers = new Dictionary<string, string>
        {
            ["source"] = "边缘节点-01",
            ["空值键"] = "",
            ["trace-id"] = "abc-123",
        };
        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishRequest(writer, 1, "db1", "t1", headers, ReadOnlySpan<byte>.Empty);

        MqPublishFrameRequest request = MqFrameCodec.DecodePublishRequest(ParseSingleFrame(writer, out _));
        Assert.Equal(3, request.Headers.Count);
        Assert.Equal("边缘节点-01", request.Headers["source"]);
        Assert.Equal("", request.Headers["空值键"]);
        Assert.Equal("abc-123", request.Headers["trace-id"]);
        Assert.True(request.Payload.IsEmpty);
    }

    [Fact]
    public void PublishResponse_RoundTrip()
    {
        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishResponse(writer, 5, 123456789L);

        var frame = ParseSingleFrame(writer, out FrameHeader header);
        Assert.True(header.IsResponse);
        Assert.False(header.IsError);
        Assert.Equal(123456789L, MqFrameCodec.DecodePublishResponse(frame.Span));
    }

    // ────────────────────────────── publish-batch ──────────────────────────────

    [Fact]
    public void PublishBatch_RoundTrip_MixedEntries()
    {
        var entries = new List<SonnetMqPublishEntry>
        {
            new(new byte[] { 1, 2, 3 }),
            new(ReadOnlyMemory<byte>.Empty, new Dictionary<string, string> { ["k"] = "v" }),
            new(new byte[] { 0xFF }, new Dictionary<string, string> { ["中文"] = "值", ["b"] = "2" }),
        };
        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishBatchRequest(writer, 9, "demo", "topic-a", entries);

        MqPublishBatchFrameRequest request = MqFrameCodec.DecodePublishBatchRequest(ParseSingleFrame(writer, out FrameHeader header));
        Assert.Equal((byte)MqFrameOp.PublishBatch, header.Op);
        Assert.Equal("demo", request.Db);
        Assert.Equal("topic-a", request.Topic);
        Assert.Equal(3, request.Entries.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, request.Entries[0].Payload.ToArray());
        Assert.Null(request.Entries[0].Headers);
        Assert.True(request.Entries[1].Payload.IsEmpty);
        Assert.Equal("v", request.Entries[1].Headers!["k"]);
        Assert.Equal("值", request.Entries[2].Headers!["中文"]);
    }

    [Fact]
    public void PublishBatchResponse_RoundTrip()
    {
        long[] offsets = [0L, 1L, 128L, long.MaxValue];
        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishBatchResponse(writer, 2, offsets);
        Assert.Equal(offsets, MqFrameCodec.DecodePublishBatchResponse(ParseSingleFrame(writer, out _).Span));
    }

    [Fact]
    public void PublishBatch_ZeroMessages_Throws()
    {
        // 手工构造 count=0 的 batch 帧体
        byte[] buf = new byte[64];
        var meta = new SpanWriter(buf);
        meta.WriteVarString("db");
        meta.WriteVarString("t");
        meta.WriteVarUInt32(0);
        byte[] payload = buf[..meta.Position];
        Assert.Throws<FrameFormatException>(() => MqFrameCodec.DecodePublishBatchRequest(payload));
    }

    // ────────────────────────────── pull ──────────────────────────────

    [Fact]
    public void PullRequest_RoundTrip()
    {
        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePullRequest(writer, 77, "demo", "topic-a", "group-1", 250);

        MqPullFrameRequest request = MqFrameCodec.DecodePullRequest(ParseSingleFrame(writer, out FrameHeader header));
        Assert.Equal((byte)MqFrameOp.Pull, header.Op);
        Assert.Equal(77u, header.StreamId);
        Assert.Equal("demo", request.Db);
        Assert.Equal("topic-a", request.Topic);
        Assert.Equal("group-1", request.ConsumerGroup);
        Assert.Equal(250, request.MaxCount);
    }

    [Fact]
    public void PullResponse_RoundTrip_TimestampTicksExact()
    {
        var ts1 = new DateTimeOffset(2026, 7, 5, 12, 34, 56, 789, TimeSpan.Zero).AddTicks(1234);
        var ts2 = DateTimeOffset.UnixEpoch;
        var messages = new List<SonnetMqMessage>
        {
            new("t", 10, ts1, new Dictionary<string, string> { ["k"] = "v" }, [1, 2, 3]),
            new("t", 11, ts2, new Dictionary<string, string>(), []),
        };
        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePullResponse(writer, 8, messages);

        SonnetMqMessage[] decoded = MqFrameCodec.DecodePullResponse(ParseSingleFrame(writer, out FrameHeader header), "t");
        Assert.True(header.IsResponse);
        Assert.Equal(2, decoded.Length);
        Assert.Equal(10, decoded[0].Offset);
        Assert.Equal(ts1.UtcTicks, decoded[0].TimestampUtc.UtcTicks);
        Assert.Equal("v", decoded[0].Headers["k"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded[0].Payload);
        Assert.Equal(11, decoded[1].Offset);
        Assert.Equal(ts2.UtcTicks, decoded[1].TimestampUtc.UtcTicks);
        Assert.Empty(decoded[1].Headers);
        Assert.Empty(decoded[1].Payload);
    }

    [Fact]
    public void PullResponse_Empty_RoundTrip()
    {
        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePullResponse(writer, 8, []);
        Assert.Empty(MqFrameCodec.DecodePullResponse(ParseSingleFrame(writer, out _), "t"));
    }

    // ────────────────────────────── ack ──────────────────────────────

    [Fact]
    public void Ack_RoundTrip()
    {
        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodeAckRequest(writer, 3, "demo", "topic-a", "group-1", 42);

        MqAckFrameRequest request = MqFrameCodec.DecodeAckRequest(ParseSingleFrame(writer, out FrameHeader header));
        Assert.Equal((byte)MqFrameOp.Ack, header.Op);
        Assert.Equal("demo", request.Db);
        Assert.Equal("topic-a", request.Topic);
        Assert.Equal("group-1", request.ConsumerGroup);
        Assert.Equal(42, request.Offset);

        var responseWriter = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodeAckResponse(responseWriter, 3, 43);
        Assert.Equal(43, MqFrameCodec.DecodeAckResponse(ParseSingleFrame(responseWriter, out _).Span));
    }

    // ────────────────────────────── 畸形 payload ──────────────────────────────

    [Fact]
    public void Decode_TruncatedVarint_Throws()
    {
        byte[] payload = [0x80]; // 未完结的 varint
        Assert.ThrowsAny<InvalidOperationException>(() => MqFrameCodec.DecodePullRequest(payload));
    }

    [Fact]
    public void Decode_StringLengthExceedsRemaining_Throws()
    {
        byte[] buf = new byte[16];
        var meta = new SpanWriter(buf);
        meta.WriteVarUInt32(200); // 声明 200 字节 db 名，但帧体没有
        byte[] payload = buf[..meta.Position];
        Assert.Throws<FrameFormatException>(() => MqFrameCodec.DecodePublishRequest(payload));
    }

    [Fact]
    public void Decode_NameOverLimit_Throws()
    {
        byte[] buf = new byte[2048];
        var meta = new SpanWriter(buf);
        meta.WriteVarString(new string('x', MqFrameCodec.MaxNameBytes + 1));
        byte[] payload = buf[..meta.Position];
        Assert.Throws<FrameFormatException>(() => MqFrameCodec.DecodePublishRequest(payload));
    }

    [Fact]
    public void Decode_HeaderCountOverLimit_Throws()
    {
        byte[] buf = new byte[64];
        var meta = new SpanWriter(buf);
        meta.WriteVarString("db");
        meta.WriteVarString("t");
        meta.WriteVarUInt32(MqFrameCodec.MaxHeaderCount + 1);
        byte[] payload = buf[..meta.Position];
        Assert.Throws<FrameFormatException>(() => MqFrameCodec.DecodePublishRequest(payload));
    }

    [Fact]
    public void Decode_BodyLengthExceedsRemaining_Throws()
    {
        byte[] buf = new byte[64];
        var meta = new SpanWriter(buf);
        meta.WriteVarString("db");
        meta.WriteVarString("t");
        meta.WriteVarUInt32(0); // headers
        meta.WriteVarUInt32(999); // payload 长度声明超出实际
        byte[] payload = buf[..meta.Position];
        Assert.Throws<FrameFormatException>(() => MqFrameCodec.DecodePublishRequest(payload));
    }

    // ────────────────────────────── 辅助 ──────────────────────────────

    private static ReadOnlyMemory<byte> ParseSingleFrame(ArrayBufferWriter<byte> writer, out FrameHeader header)
    {
        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        Assert.True(FrameCodec.TryReadFrame(ref buffer, out header, out ReadOnlySequence<byte> payload));
        Assert.Equal(0, buffer.Length);
        return payload.ToArray();
    }
}
