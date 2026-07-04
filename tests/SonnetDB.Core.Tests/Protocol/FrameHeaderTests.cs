using System.Buffers;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Core.Tests.Protocol;

/// <summary>
/// <see cref="FrameHeader"/> 与 <see cref="FrameCodec"/> 帧信封测试。
/// </summary>
public sealed class FrameHeaderTests
{
    [Fact]
    public void Header_RoundTrip_AllFields()
    {
        var original = new FrameHeader(
            payloadLength: 0xAABBCC,
            version: FrameHeader.CurrentVersion,
            service: (byte)FrameService.Mq,
            op: (byte)MqFrameOp.Pull,
            flags: (byte)(FrameFlags.Response | FrameFlags.Error),
            streamId: 0xDEADBEEF);

        Span<byte> buf = stackalloc byte[FrameHeader.Size];
        original.Write(buf);

        Assert.True(FrameHeader.TryRead(buf, out FrameHeader result));
        Assert.Equal(original.PayloadLength, result.PayloadLength);
        Assert.Equal(original.Version, result.Version);
        Assert.Equal(original.Service, result.Service);
        Assert.Equal(original.Op, result.Op);
        Assert.Equal(original.Flags, result.Flags);
        Assert.Equal(original.StreamId, result.StreamId);
        Assert.True(result.IsResponse);
        Assert.True(result.IsError);
    }

    [Fact]
    public void TryRead_LessThan12Bytes_ReturnsFalse()
    {
        Span<byte> buf = stackalloc byte[FrameHeader.Size - 1];
        Assert.False(FrameHeader.TryRead(buf, out _));
    }

    [Fact]
    public void RequestFrame_FlagsNotSet()
    {
        var header = new FrameHeader(0, FrameHeader.CurrentVersion, (byte)FrameService.Mq, (byte)MqFrameOp.Publish, (byte)FrameFlags.None, 1);
        Assert.False(header.IsResponse);
        Assert.False(header.IsError);
    }

    [Fact]
    public void WriteFrame_ThenTryReadFrame_RoundTrips()
    {
        var writer = new ArrayBufferWriter<byte>();
        byte[] payload = [1, 2, 3, 4, 5];
        var header = new FrameHeader((uint)payload.Length, FrameHeader.CurrentVersion,
            (byte)FrameService.Mq, (byte)MqFrameOp.Publish, (byte)FrameFlags.None, 42);
        FrameCodec.WriteFrame(writer, in header, payload);

        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        Assert.True(FrameCodec.TryReadFrame(ref buffer, out FrameHeader parsed, out ReadOnlySequence<byte> parsedPayload));
        Assert.Equal(42u, parsed.StreamId);
        Assert.Equal(payload, parsedPayload.ToArray());
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void WriteFrame_LengthMismatch_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        var header = new FrameHeader(10, FrameHeader.CurrentVersion, (byte)FrameService.Mq, 1, 0, 1);
        Assert.Throws<ArgumentException>(() => FrameCodec.WriteFrame(writer, in header, [1, 2, 3]));
    }

    [Fact]
    public void TryReadFrame_IncompleteHeader_ReturnsFalse_NoConsume()
    {
        var buffer = new ReadOnlySequence<byte>(new byte[FrameHeader.Size - 1]);
        long before = buffer.Length;
        Assert.False(FrameCodec.TryReadFrame(ref buffer, out _, out _));
        Assert.Equal(before, buffer.Length);
    }

    [Fact]
    public void TryReadFrame_IncompletePayload_ReturnsFalse_NoConsume()
    {
        var writer = new ArrayBufferWriter<byte>();
        var header = new FrameHeader(100, FrameHeader.CurrentVersion, (byte)FrameService.Mq, 1, 0, 1);
        Span<byte> headerBytes = stackalloc byte[FrameHeader.Size];
        header.Write(headerBytes);
        writer.Write(headerBytes);
        writer.Write(new byte[10]); // 只有 10/100 字节 payload

        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        long before = buffer.Length;
        Assert.False(FrameCodec.TryReadFrame(ref buffer, out _, out _));
        Assert.Equal(before, buffer.Length);
    }

    [Fact]
    public void TryReadFrame_OversizePayloadLength_Throws()
    {
        var header = new FrameHeader(FrameHeader.MaxFramePayloadBytes + 1, FrameHeader.CurrentVersion, (byte)FrameService.Mq, 1, 0, 1);
        byte[] headerBytes = new byte[FrameHeader.Size];
        header.Write(headerBytes);

        var buffer = new ReadOnlySequence<byte>(headerBytes);
        Assert.Throws<FrameFormatException>(() =>
        {
            var local = buffer;
            FrameCodec.TryReadFrame(ref local, out _, out _);
        });
    }

    [Fact]
    public void TryReadFrame_MultipleFrames_ParsesSequentially()
    {
        var writer = new ArrayBufferWriter<byte>();
        for (uint i = 1; i <= 3; i++)
        {
            byte[] payload = [(byte)i];
            var header = new FrameHeader(1, FrameHeader.CurrentVersion, (byte)FrameService.Mq, 1, 0, i);
            FrameCodec.WriteFrame(writer, in header, payload);
        }

        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        for (uint i = 1; i <= 3; i++)
        {
            Assert.True(FrameCodec.TryReadFrame(ref buffer, out FrameHeader h, out ReadOnlySequence<byte> p));
            Assert.Equal(i, h.StreamId);
            Assert.Equal((byte)i, p.ToArray()[0]);
        }

        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TryReadFrame_MultiSegmentSequence_Parses()
    {
        // 帧头与 payload 被切成多个 segment，验证跨 segment 解析
        var writer = new ArrayBufferWriter<byte>();
        byte[] payload = new byte[64];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)i;
        var header = new FrameHeader((uint)payload.Length, FrameHeader.CurrentVersion,
            (byte)FrameService.Mq, (byte)MqFrameOp.Publish, (byte)FrameFlags.None, 7);
        FrameCodec.WriteFrame(writer, in header, payload);

        byte[] whole = writer.WrittenMemory.ToArray();
        var buffer = BuildSegmented(whole, segmentSize: 5);

        Assert.True(FrameCodec.TryReadFrame(ref buffer, out FrameHeader parsed, out ReadOnlySequence<byte> parsedPayload));
        Assert.Equal(7u, parsed.StreamId);
        Assert.Equal(payload, parsedPayload.ToArray());
    }

    [Fact]
    public void ErrorFrame_RoundTrip()
    {
        var writer = new ArrayBufferWriter<byte>();
        FrameCodec.WriteErrorFrame(writer, (byte)FrameService.Mq, (byte)MqFrameOp.Publish, 99, "forbidden", "无权限访问数据库 'demo'。");

        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        Assert.True(FrameCodec.TryReadFrame(ref buffer, out FrameHeader header, out ReadOnlySequence<byte> payload));
        Assert.True(header.IsError);
        Assert.True(header.IsResponse);
        Assert.Equal(99u, header.StreamId);

        (string code, string message) = FrameCodec.ReadErrorPayload(payload.ToArray());
        Assert.Equal("forbidden", code);
        Assert.Equal("无权限访问数据库 'demo'。", message);
    }

    private static ReadOnlySequence<byte> BuildSegmented(byte[] data, int segmentSize)
    {
        var segments = new List<byte[]>();
        for (int i = 0; i < data.Length; i += segmentSize)
            segments.Add(data[i..Math.Min(i + segmentSize, data.Length)]);

        var first = new TestSegment(segments[0], 0);
        TestSegment last = first;
        foreach (byte[] chunk in segments.Skip(1))
            last = last.Append(chunk);
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class TestSegment : ReadOnlySequenceSegment<byte>
    {
        public TestSegment(byte[] data, long runningIndex)
        {
            Memory = data;
            RunningIndex = runningIndex;
        }

        public TestSegment Append(byte[] data)
        {
            var segment = new TestSegment(data, RunningIndex + Memory.Length);
            Next = segment;
            return segment;
        }
    }
}
