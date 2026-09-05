using System.Buffers;
using System.Buffers.Binary;
using SonnetDB.Kv;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Core.Tests.Protocol;

/// <summary>新增 KV 原子帧的字段、返回值和损坏输入合同。</summary>
public sealed class KvAtomicFrameCodecTests
{
    private static readonly DateTimeOffset Expiry = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234);

    /// <summary>原始 key/value、存在性条件、版本和亚毫秒 TTL 均精确往返。</summary>
    [Theory]
    [InlineData(KvFrameOp.SetConditional, KvSetCondition.IfNotExists, 0L)]
    [InlineData(KvFrameOp.SetConditional, KvSetCondition.IfExists, 0L)]
    [InlineData(KvFrameOp.GetAndSet, KvSetCondition.Always, 0L)]
    [InlineData(KvFrameOp.CompareAndSet, KvSetCondition.Always, long.MaxValue)]
    [InlineData(KvFrameOp.Expire, KvSetCondition.Always, 0L)]
    public void AtomicWrite_WithValidFields_RoundTripsExactBytesAndTicks(KvFrameOp op, KvSetCondition condition, long version)
    {
        byte[] key = [0, 127, 128, 255];
        byte[] value = op == KvFrameOp.Expire ? [] : [255, 0, 128, 1];
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeAtomicWriteRequest(writer, 71, op, "demo", "cache", key, value, condition, version, Expiry);
        ReadOnlyMemory<byte> payload = Payload(writer, out FrameHeader header);
        Assert.Equal((byte)op, header.Op);
        Assert.Equal(71u, header.StreamId);
        Assert.Equal((byte)FrameFlags.None, header.Flags);

        KvAtomicWriteFrameRequest request = KvFrameCodec.DecodeAtomicWriteRequest(op, payload);

        Assert.Equal("demo", request.Db);
        Assert.Equal("cache", request.Keyspace);
        Assert.Equal(key, request.Key.ToArray());
        Assert.Equal(value, request.Value.ToArray());
        Assert.Equal(condition, request.Condition);
        Assert.Equal(version, request.ExpectedVersion);
        Assert.Equal(Expiry, request.ExpiresAtUtc);
    }

    /// <summary>key-only 操作保持自己的 opcode，并复用相同 key 解码合同。</summary>
    [Theory]
    [InlineData(KvFrameOp.GetAndDelete)]
    [InlineData(KvFrameOp.Persist)]
    [InlineData(KvFrameOp.GetTimeToLive)]
    public void AtomicKey_WithValidOperation_RoundTripsRawKey(KvFrameOp op)
    {
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeAtomicKeyRequest(writer, 9, op, "demo", "cache", [0, 255]);
        ReadOnlyMemory<byte> payload = Payload(writer, out FrameHeader header);

        var request = KvFrameCodec.DecodeGetRequest(payload);

        Assert.Equal((byte)op, header.Op);
        Assert.Equal(new byte[] { 0, 255 }, request.Key.ToArray());
        Assert.Equal("demo", request.Db);
        Assert.Equal("cache", request.Keyspace);
    }

    /// <summary>独立 little-endian fixture 验证零版本与正版本的条件写返回值。</summary>
    [Theory]
    [InlineData(0L, false)]
    [InlineData(42L, true)]
    [InlineData(long.MaxValue, true)]
    public void ConditionalSetResponse_WithWireVersion_DecodesAppliedContract(long version, bool applied)
    {
        var result = KvFrameCodec.DecodeConditionalSetResponse(Int64(version));

        Assert.Equal(applied, result.Applied);
        Assert.Equal(applied ? version : (long?)null, result.Version);
    }

    /// <summary>缺失记录、存在的空值和二进制值具有不同交换返回合同。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExchangeResponse_WithMissingEmptyOrBinaryValue_PreservesPresence(int kind)
    {
        byte[] value = kind == 1 ? [] : [0, 128, 255];
        KvEntry? previous = kind == 0 ? null : new KvEntry("item"u8.ToArray(), value, 42, Expiry);
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeExchangeResponse(writer, 9, KvFrameOp.GetAndDelete,
            new KvExchangeResult(previous, previous is null ? null : 43));
        ReadOnlyMemory<byte> payload = Payload(writer, out FrameHeader header);
        Assert.Equal((byte)FrameFlags.Response, header.Flags);

        KvExchangeResult result = KvFrameCodec.DecodeExchangeResponse(payload);

        Assert.Equal(previous is null ? null : 43L, result.MutationVersion);
        if (previous is null)
        {
            Assert.Null(result.PreviousEntry);
            return;
        }
        Assert.NotNull(result.PreviousEntry);
        Assert.True(result.PreviousEntry.Key.IsEmpty);
        Assert.Equal(previous.Value.ToArray(), result.PreviousEntry.Value.ToArray());
        Assert.Equal(42, result.PreviousEntry.Version);
        Assert.Equal(Expiry, result.PreviousEntry.ExpiresAtUtc);
    }

    /// <summary>CAS 的成功标记、旧版本和新版本通过独立 wire fixture 校验。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CasResponse_WithIndependentWireFixture_DecodesVersions(bool succeeded)
    {
        byte[] payload = [(byte)(succeeded ? 1 : 0), .. Int64(41), .. Int64(succeeded ? 42 : 0)];
        KvCasResult result = KvFrameCodec.DecodeCasResponse(payload);

        Assert.Equal(succeeded, result.Succeeded);
        Assert.Equal(41, result.CurrentVersion);
        Assert.Equal(succeeded ? 42L : (long?)null, result.NewVersion);
    }

    /// <summary>TTL 缺失、永久和有到期时间的三态精确往返。</summary>
    [Theory]
    [InlineData(-2L)]
    [InlineData(-1L)]
    [InlineData(0L)]
    public void TtlResponse_WithThreeStates_RoundTripsExpiryContract(long milliseconds)
    {
        var expected = new KvTtlResult(milliseconds, milliseconds >= 0 ? Expiry : null);
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeTtlResponse(writer, 8, expected);

        KvTtlResult actual = KvFrameCodec.DecodeTtlResponse(Payload(writer, out _).Span);

        Assert.Equal(expected, actual);
    }

    /// <summary>损坏响应中的标记、负版本、不一致状态和尾随字节必须失败。</summary>
    [Theory]
    [InlineData("conditional-negative")]
    [InlineData("conditional-trailing")]
    [InlineData("exchange-flag")]
    [InlineData("exchange-previous-zero")]
    [InlineData("exchange-negative")]
    [InlineData("exchange-without-mutation")]
    [InlineData("cas-flag")]
    [InlineData("cas-success-without-version")]
    [InlineData("cas-conflict-with-version")]
    [InlineData("ttl-negative")]
    [InlineData("ttl-without-expiry")]
    [InlineData("ttl-missing-with-expiry")]
    [InlineData("boolean-trailing")]
    public void Response_WithInvalidWireState_ThrowsFrameFormatException(string kind)
    {
        Action decode = kind switch
        {
            "conditional-negative" => () => KvFrameCodec.DecodeConditionalSetResponse(Int64(-1)),
            "conditional-trailing" => () => KvFrameCodec.DecodeConditionalSetResponse([.. Int64(1), 0]),
            "exchange-flag" => () => KvFrameCodec.DecodeExchangeResponse(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 2 }),
            "exchange-previous-zero" => () => KvFrameCodec.DecodeExchangeResponse(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
            "exchange-negative" => () => KvFrameCodec.DecodeExchangeResponse(Join(Int64(-1), [0])),
            "exchange-without-mutation" => () => KvFrameCodec.DecodeExchangeResponse(Join(Int64(0), [1], Int64(1), [0, 0])),
            "cas-flag" => () => KvFrameCodec.DecodeCasResponse(Join([2], Int64(1), Int64(2))),
            "cas-success-without-version" => () => KvFrameCodec.DecodeCasResponse(Join([1], Int64(1), Int64(0))),
            "cas-conflict-with-version" => () => KvFrameCodec.DecodeCasResponse(Join([0], Int64(1), Int64(2))),
            "ttl-negative" => () => KvFrameCodec.DecodeTtlResponse(Join(Int64(-3), [0])),
            "ttl-without-expiry" => () => KvFrameCodec.DecodeTtlResponse(Join(Int64(1), [0])),
            "ttl-missing-with-expiry" => () => KvFrameCodec.DecodeTtlResponse(Join(Int64(-2), [1], Int64(Expiry.UtcTicks))),
            "boolean-trailing" => () => KvFrameCodec.DecodeBooleanResponse([1, 0]),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Assert.Throws<FrameFormatException>(decode);
    }

    /// <summary>请求语义不允许跨操作复用条件或期望版本。</summary>
    [Theory]
    [InlineData(KvFrameOp.SetConditional, (KvSetCondition)255, 0L)]
    [InlineData(KvFrameOp.GetAndSet, KvSetCondition.IfExists, 0L)]
    [InlineData(KvFrameOp.SetConditional, KvSetCondition.Always, 1L)]
    [InlineData(KvFrameOp.CompareAndSet, KvSetCondition.Always, -1L)]
    public void AtomicWrite_WithInvalidSemanticFields_RejectsBeforeWriting(KvFrameOp op, KvSetCondition condition, long version)
    {
        var writer = new ArrayBufferWriter<byte>();

        Assert.ThrowsAny<ArgumentException>(() => KvFrameCodec.EncodeAtomicWriteRequest(
            writer, 1, op, "d", "k", [1], [2], condition, version));

        Assert.Equal(0, writer.WrittenCount);
    }

    /// <summary>解码不能因为编码器已校验而信任外部请求中的条件字段。</summary>
    [Fact]
    public void AtomicWrite_WithTamperedCondition_RejectsIncomingRequest()
    {
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeAtomicWriteRequest(writer, 1, KvFrameOp.SetConditional, "d", "k", [1], [2]);
        byte[] payload = Payload(writer, out _).ToArray();
        // d、k 和单字节 key 各占一个长度字节与一个内容字节。
        payload[6] = 255;

        Assert.ThrowsAny<ArgumentException>(() =>
            KvFrameCodec.DecodeAtomicWriteRequest(KvFrameOp.SetConditional, payload));
    }

    /// <summary>请求尾随字节和 value 截断必须按结构性失败处理。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AtomicWrite_WithTrailingOrTruncatedValue_RejectsPayload(bool trailing)
    {
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeAtomicWriteRequest(writer, 1, KvFrameOp.GetAndSet, "d", "k", [1], [2, 3]);
        byte[] original = Payload(writer, out _).ToArray();
        byte[] malformed = trailing ? [.. original, 0] : original[..^1];

        Assert.Throws<FrameFormatException>(() =>
            KvFrameCodec.DecodeAtomicWriteRequest(KvFrameOp.GetAndSet, malformed));
    }

    /// <summary>最大 key 可往返，超过上限的 key 在写目标缓冲前拒绝。</summary>
    [Fact]
    public void AtomicWrite_AtKeyByteLimit_AcceptsMaximumAndRejectsLargerKey()
    {
        byte[] key = new byte[KvFrameCodec.MaxKeyBytes];
        key[^1] = 255;
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeAtomicWriteRequest(writer, 1, KvFrameOp.GetAndSet, "d", "k", key, []);
        var request = KvFrameCodec.DecodeAtomicWriteRequest(KvFrameOp.GetAndSet, Payload(writer, out _));
        Assert.Equal(key, request.Key.ToArray());
        Assert.True(request.Value.IsEmpty);
        var rejected = new ArrayBufferWriter<byte>();

        Assert.Throws<ArgumentException>(() => KvFrameCodec.EncodeAtomicWriteRequest(
            rejected, 1, KvFrameOp.GetAndSet, "d", "k", new byte[KvFrameCodec.MaxKeyBytes + 1], []));

        Assert.Equal(0, rejected.WrittenCount);
    }

    private static ReadOnlyMemory<byte> Payload(ArrayBufferWriter<byte> writer, out FrameHeader header)
    {
        Assert.True(FrameHeader.TryRead(writer.WrittenSpan, out header));
        Assert.Equal(FrameHeader.CurrentVersion, header.Version);
        Assert.Equal((byte)FrameService.Kv, header.Service);
        Assert.Equal(writer.WrittenCount - FrameHeader.Size, (int)header.PayloadLength);
        return writer.WrittenMemory[FrameHeader.Size..];
    }

    private static byte[] Int64(long value)
    {
        byte[] bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(static part => part).ToArray();
}
