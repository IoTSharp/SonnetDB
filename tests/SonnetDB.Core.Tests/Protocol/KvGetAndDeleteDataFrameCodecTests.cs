using System.Buffers;
using System.Buffers.Binary;
using SonnetDB.Kv;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Core.Tests.Protocol;

/// <summary>共享 GetAndDelete codec 与实际 Core 编码器的字节兼容、边界和缓冲所有权。</summary>
public sealed class KvGetAndDeleteDataFrameCodecTests
{
    /// <summary>空键、单字节键和最大键与实际 Core 编码器逐字节相同。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(KvDataFrameCodec.MaxKeyBytes)]
    public void EncodeGetAndDeleteRequest_WithBoundaryKeys_MatchesCoreWire(int keyLength)
    {
        byte[] key = new byte[keyLength];
        if (key.Length > 0) key[^1] = 255;
        var actual = new ArrayBufferWriter<byte>();
        var expected = new ArrayBufferWriter<byte>();

        KvDataFrameCodec.EncodeGetAndDeleteRequest(actual, uint.MaxValue, "数据库", "Cache", key);
        KvFrameCodec.EncodeAtomicKeyRequest(expected, uint.MaxValue, KvFrameOp.GetAndDelete, "数据库", "Cache", key);

        Assert.Equal(expected.WrittenSpan.ToArray(), actual.WrittenSpan.ToArray());
        ReadOnlyMemory<byte> payload = Payload(actual, KvFrameOp.GetAndDelete, out FrameHeader header);
        Assert.Equal(uint.MaxValue, header.StreamId);
        Assert.Equal((byte)FrameFlags.None, header.Flags);
        KvGetFrameRequest decoded = KvFrameCodec.DecodeGetRequest(payload);
        Assert.Equal("数据库", decoded.Db);
        Assert.Equal("Cache", decoded.Keyspace);
        Assert.Equal(key, decoded.Key.ToArray());
    }

    /// <summary>请求 payload 的精确预算成功，小一个字节时写入前拒绝。</summary>
    [Fact]
    public void EncodeGetAndDeleteRequest_AtExactPayloadBudget_WritesOnlyWhenItFits()
    {
        var core = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeAtomicKeyRequest(core, 7, KvFrameOp.GetAndDelete, "d", "k", [0, 255]);
        int budget = core.WrittenCount - FrameHeader.Size;
        var exact = new ArrayBufferWriter<byte>();
        var rejected = new ArrayBufferWriter<byte>();

        KvDataFrameCodec.EncodeGetAndDeleteRequest(exact, 7, "d", "k", [0, 255], budget);

        Assert.Equal(core.WrittenSpan.ToArray(), exact.WrittenSpan.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            KvDataFrameCodec.EncodeGetAndDeleteRequest(rejected, 7, "d", "k", [0, 255], budget - 1));
        Assert.Equal(0, rejected.WrittenCount);
    }

    /// <summary>UTF-8 名称按字节接受精确上限，与 Core 的有效字节合同保持一致。</summary>
    [Fact]
    public void EncodeGetAndDeleteRequest_AtNameByteLimit_MatchesCoreWire()
    {
        string name = new string('中', 170) + "ab";
        var actual = new ArrayBufferWriter<byte>();
        var core = new ArrayBufferWriter<byte>();

        KvDataFrameCodec.EncodeGetAndDeleteRequest(actual, 1, name, name, []);
        KvFrameCodec.EncodeAtomicKeyRequest(core, 1, KvFrameOp.GetAndDelete, name, name, []);

        Assert.Equal(core.WrittenSpan.ToArray(), actual.WrittenSpan.ToArray());
    }

    /// <summary>无效名称、键和预算在触碰目标缓冲前失败。</summary>
    [Theory]
    [InlineData("null-db")]
    [InlineData("null-keyspace")]
    [InlineData("long-db")]
    [InlineData("long-keyspace")]
    [InlineData("invalid-utf8")]
    [InlineData("long-key")]
    [InlineData("zero-budget")]
    [InlineData("negative-budget")]
    [InlineData("oversize-budget")]
    public void EncodeGetAndDeleteRequest_WithInvalidInput_RejectsBeforeWriting(string kind)
    {
        var writer = new ArrayBufferWriter<byte>();
        string db = kind == "null-db" ? null! : kind == "long-db" ? new string('中', 171) : kind == "invalid-utf8" ? "\ud800" : "d";
        string keyspace = kind == "null-keyspace" ? null! : kind == "long-keyspace" ? new string('a', 513) : "k";
        byte[] key = new byte[kind == "long-key" ? KvDataFrameCodec.MaxKeyBytes + 1 : 1];
        int budget = kind switch
        {
            "zero-budget" => 0,
            "negative-budget" => -1,
            "oversize-budget" => (int)FrameHeader.MaxFramePayloadBytes + 1,
            _ => (int)FrameHeader.MaxFramePayloadBytes,
        };

        Assert.ThrowsAny<ArgumentException>(() =>
            KvDataFrameCodec.EncodeGetAndDeleteRequest(writer, 1, db, keyspace, key, budget));
        Assert.Equal(0, writer.WrittenCount);
    }

    /// <summary>空、缺失、正版本缺失和原始二进制旧值对照 Core exchange 编解码，不误用 Get 布局。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DecodeExchangeResponse_FromCoreEncoder_PreservesGenericExchangeContract(int kind)
    {
        DateTimeOffset? expiry = kind switch
        {
            3 => new DateTimeOffset(2030, 11, 7, 1, 2, 3, TimeSpan.FromHours(8)).AddTicks(7),
            4 => DateTimeOffset.MinValue,
            5 => DateTimeOffset.MaxValue,
            _ => null,
        };
        byte[] value = kind == 2 ? [] : [0, 128, 255];
        KvEntry? previous = kind <= 1 ? null : new KvEntry(new byte[] { 255 }, value, long.MaxValue, expiry);
        long? mutation = kind == 0 ? null : long.MaxValue;
        byte[] payload = CoreExchangePayload(new(previous, mutation));

        KvDataFrameExchangeResult actual = KvDataFrameCodec.DecodeExchangeResponse(payload);
        KvExchangeResult core = KvFrameCodec.DecodeExchangeResponse(payload);

        Assert.Equal(core.MutationVersion, actual.MutationVersion);
        if (previous is null)
        {
            Assert.Null(actual.PreviousValue);
            Assert.Equal(mutation, actual.MutationVersion);
            return;
        }
        Assert.NotNull(actual.PreviousValue);
        Assert.Equal(core.PreviousEntry!.Version, actual.PreviousValue.Version);
        Assert.Equal(core.PreviousEntry.Value.ToArray(), actual.PreviousValue.Value);
        Assert.Equal(expiry?.UtcTicks, actual.PreviousValue.ExpiresAtUtc?.UtcTicks);
        if (expiry is not null) Assert.Equal(TimeSpan.Zero, actual.PreviousValue.ExpiresAtUtc!.Value.Offset);
    }

    /// <summary>非法标记、负版本、缺失变更版本、无效 ticks、长度越界和尾字节全部拒绝。</summary>
    [Theory]
    [InlineData("negative-mutation")]
    [InlineData("invalid-found")]
    [InlineData("zero-previous")]
    [InlineData("negative-previous")]
    [InlineData("found-without-mutation")]
    [InlineData("invalid-expiry")]
    [InlineData("negative-expiry")]
    [InlineData("overflow-expiry")]
    [InlineData("value-overrun")]
    [InlineData("value-length-overflow")]
    [InlineData("missing-trailing")]
    [InlineData("found-trailing")]
    public void DecodeExchangeResponse_WithMalformedPayload_RejectsWireState(string kind)
    {
        byte[] payload = kind switch
        {
            "negative-mutation" => [.. Int64(-1), 0],
            "invalid-found" => [.. Int64(1), 2],
            "zero-previous" => [.. Int64(1), 1, .. Int64(0), 0, 0],
            "negative-previous" => [.. Int64(1), 1, .. Int64(-1), 0, 0],
            "found-without-mutation" => [.. Int64(0), 1, .. Int64(1), 0, 0],
            "invalid-expiry" => [.. Int64(1), 1, .. Int64(1), 2, 0],
            "negative-expiry" => [.. Int64(1), 1, .. Int64(1), 1, .. Int64(-1), 0],
            "overflow-expiry" => [.. Int64(1), 1, .. Int64(1), 1, .. Int64(long.MaxValue), 0],
            "value-overrun" => [.. Int64(1), 1, .. Int64(1), 0, 2, 42],
            "value-length-overflow" => [.. Int64(1), 1, .. Int64(1), 0, 255, 255, 255, 255, 15],
            "missing-trailing" => [.. Int64(0), 0, 0],
            "found-trailing" => [.. Int64(1), 1, .. Int64(1), 0, 0, 0],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Assert.Throws<FrameFormatException>(() => KvDataFrameCodec.DecodeExchangeResponse(payload));
    }

    /// <summary>固定 Int64、过期时间、长度及值被截断时不得返回成功记录。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(27)]
    [InlineData(29)]
    public void DecodeExchangeResponse_WithTruncatedCorePayload_RejectsIncompleteResponse(int length)
    {
        var previous = new KvEntry(new byte[] { 1 }, new byte[] { 0, 128, 255 }, 42, DateTimeOffset.MaxValue);
        byte[] complete = CoreExchangePayload(new(previous, 43));
        Assert.True(length < complete.Length);
        byte[] truncated = complete[..length];

        Exception error = Assert.ThrowsAny<Exception>(() => KvDataFrameCodec.DecodeExchangeResponse(truncated));

        Assert.True(error is FrameFormatException or InvalidOperationException);
    }

    /// <summary>返回值既不借用响应缓冲，也不与另一次解码结果共享可变数组。</summary>
    [Fact]
    public void DecodeExchangeResponse_AfterInputOrResultChanges_OwnsIndependentValueBuffers()
    {
        byte[] original = [0, 128, 255];
        byte[] payload = CoreExchangePayload(new(new KvEntry(new byte[] { 1 }, original, 41, null), 42));
        KvDataFrameExchangeResult first = KvDataFrameCodec.DecodeExchangeResponse(payload);
        KvDataFrameExchangeResult second = KvDataFrameCodec.DecodeExchangeResponse(payload);
        Assert.NotNull(first.PreviousValue);
        Assert.NotNull(second.PreviousValue);

        payload[^1] = 1;
        first.PreviousValue.Value[0] = 7;

        Assert.Equal(new byte[] { 7, 128, 255 }, first.PreviousValue.Value);
        Assert.Equal(new byte[] { 0, 128, 255 }, second.PreviousValue.Value);
        Assert.Equal(new byte[] { 0, 128, 255 }, original);
        Assert.Equal(42L, second.MutationVersion);
    }

    private static byte[] CoreExchangePayload(KvExchangeResult value)
    {
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeExchangeResponse(writer, 9, KvFrameOp.GetAndDelete, value);
        ReadOnlyMemory<byte> payload = Payload(writer, KvFrameOp.GetAndDelete, out FrameHeader header);
        Assert.Equal((byte)FrameFlags.Response, header.Flags);
        return payload.ToArray();
    }

    private static ReadOnlyMemory<byte> Payload(ArrayBufferWriter<byte> writer, KvFrameOp op, out FrameHeader header)
    {
        Assert.True(FrameHeader.TryRead(writer.WrittenSpan, out header));
        Assert.Equal(FrameHeader.CurrentVersion, header.Version);
        Assert.Equal((byte)FrameService.Kv, header.Service);
        Assert.Equal((byte)op, header.Op);
        Assert.Equal(writer.WrittenCount - FrameHeader.Size, (int)header.PayloadLength);
        return writer.WrittenMemory[FrameHeader.Size..];
    }

    private static byte[] Int64(long value)
    {
        byte[] bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }
}
