using System.Buffers;
using SonnetDB.IO;
using SonnetDB.Kv;

namespace SonnetDB.Protocol;

public static partial class KvFrameCodec
{
    /// <summary>编码原子写帧。条件、期望版本和过期时间显式传递，原始值不做 Base64 转换。</summary>
    /// <param name="writer">目标缓冲。</param>
    /// <param name="streamId">请求关联编号。</param>
    /// <param name="op">条件写、交换、CAS 或 expire。</param>
    /// <param name="db">数据库名。</param>
    /// <param name="keyspace">keyspace 名。</param>
    /// <param name="key">非空原始 key。</param>
    /// <param name="value">新值；expire 使用空值。</param>
    /// <param name="condition">条件写条件；其他操作使用 Always。</param>
    /// <param name="expectedVersion">CAS 的期望版本；其他操作使用 0。</param>
    /// <param name="expiresAtUtc">新值的 UTC 过期时间。</param>
    public static void EncodeAtomicWriteRequest(IBufferWriter<byte> writer, uint streamId, KvFrameOp op,
        string db, string keyspace, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value,
        KvSetCondition condition = KvSetCondition.Always, long expectedVersion = 0, DateTimeOffset? expiresAtUtc = null)
    {
        ValidateAtomicWrite(op, condition, expectedVersion, expiresAtUtc, value.Length);
        ValidateKeyLength(key.Length, nameof(key));
        int length = checked(SpanWriter.MeasureVarString(db) + SpanWriter.MeasureVarString(keyspace)
            + SpanWriter.MeasureVarUInt32((uint)key.Length) + key.Length + 1 + sizeof(long)
            + MeasureExpiry(expiresAtUtc) + SpanWriter.MeasureVarUInt32((uint)value.Length) + value.Length);
        ValidateFramePayloadLength(length);
        var span = writer.GetSpan(FrameHeader.Size + length);
        new FrameHeader((uint)length, FrameHeader.CurrentVersion, (byte)FrameService.Kv, (byte)op,
            (byte)FrameFlags.None, streamId).Write(span);
        var body = new SpanWriter(span.Slice(FrameHeader.Size, length));
        body.WriteVarString(db);
        body.WriteVarString(keyspace);
        body.WriteVarUInt32((uint)key.Length);
        body.WriteBytes(key);
        body.WriteByte((byte)condition);
        body.WriteInt64(expectedVersion);
        WriteExpiry(ref body, expiresAtUtc);
        body.WriteVarUInt32((uint)value.Length);
        body.WriteBytes(value);
        writer.Advance(FrameHeader.Size + length);
    }

    /// <summary>解码原子写请求；key/value 在请求缓冲存活期间有效。</summary>
    /// <param name="op">帧操作码。</param>
    /// <param name="payload">完整帧体。</param>
    /// <returns>经过语义字段校验的请求。</returns>
    public static KvAtomicWriteFrameRequest DecodeAtomicWriteRequest(KvFrameOp op, ReadOnlyMemory<byte> payload)
    {
        var reader = new SpanReader(payload.Span);
        string db = ReadName(ref reader, "db");
        string keyspace = ReadName(ref reader, "keyspace");
        var key = ReadBoundedBytes(ref reader, payload, MaxKeyBytes, "key");
        var condition = (KvSetCondition)reader.ReadByte();
        long expectedVersion = reader.ReadInt64();
        var expiresAtUtc = ReadExpiry(ref reader);
        var value = ReadBody(ref reader, payload, "value");
        RequireEnd(ref reader, "atomic write");
        ValidateAtomicWrite(op, condition, expectedVersion, expiresAtUtc, value.Length);
        return new KvAtomicWriteFrameRequest(db, keyspace, key, value, condition, expectedVersion, expiresAtUtc);
    }

    /// <summary>编码仅含 key 的原子删除、persist 或 TTL 查询请求。</summary>
    /// <param name="writer">目标缓冲。</param>
    /// <param name="streamId">请求关联编号。</param>
    /// <param name="op">原子删除、persist 或 TTL。</param>
    /// <param name="db">数据库名。</param>
    /// <param name="keyspace">keyspace 名。</param>
    /// <param name="key">原始 key。</param>
    public static void EncodeAtomicKeyRequest(IBufferWriter<byte> writer, uint streamId, KvFrameOp op,
        string db, string keyspace, ReadOnlySpan<byte> key)
    {
        if (op is not (KvFrameOp.GetAndDelete or KvFrameOp.Persist or KvFrameOp.GetTimeToLive))
            throw new ArgumentOutOfRangeException(nameof(op));
        ValidateKeyLength(key.Length, nameof(key));
        int length = checked(SpanWriter.MeasureVarString(db) + SpanWriter.MeasureVarString(keyspace)
            + SpanWriter.MeasureVarUInt32((uint)key.Length) + key.Length);
        ValidateFramePayloadLength(length);
        var span = writer.GetSpan(FrameHeader.Size + length);
        new FrameHeader((uint)length, FrameHeader.CurrentVersion, (byte)FrameService.Kv, (byte)op,
            (byte)FrameFlags.None, streamId).Write(span);
        var body = new SpanWriter(span.Slice(FrameHeader.Size, length));
        body.WriteVarString(db);
        body.WriteVarString(keyspace);
        body.WriteVarUInt32((uint)key.Length);
        body.WriteBytes(key);
        writer.Advance(FrameHeader.Size + length);
    }

    /// <summary>编码条件写结果，0 版本表示未写入。</summary>
    public static void EncodeConditionalSetResponse(IBufferWriter<byte> writer, uint streamId, KvSetResult result) =>
        WriteAtomicResponse(writer, streamId, KvFrameOp.SetConditional, sizeof(long),
            (ref SpanWriter body) => body.WriteInt64(result.Version ?? 0));

    /// <summary>解码条件写结果。</summary>
    public static KvSetResult DecodeConditionalSetResponse(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        long? version = ReadMutationVersion(ref reader);
        RequireEnd(ref reader, "conditional set");
        return new KvSetResult(version.HasValue, version);
    }

    /// <summary>编码原子交换结果，保留旧值是否存在、原始值、版本和 TTL。</summary>
    public static void EncodeExchangeResponse(IBufferWriter<byte> writer, uint streamId, KvFrameOp op, KvExchangeResult result)
    {
        if (op is not (KvFrameOp.GetAndSet or KvFrameOp.GetAndDelete))
            throw new ArgumentOutOfRangeException(nameof(op));
        var previous = result.PreviousEntry;
        int length = sizeof(long) + 1;
        if (previous is not null)
            length = checked(length + sizeof(long) + MeasureExpiry(previous.ExpiresAtUtc)
                + SpanWriter.MeasureVarUInt32((uint)previous.Value.Length) + previous.Value.Length);
        WriteAtomicResponse(writer, streamId, op, length, (ref SpanWriter body) =>
        {
            body.WriteInt64(result.MutationVersion ?? 0);
            body.WriteByte(previous is null ? (byte)0 : (byte)1);
            if (previous is not null)
            {
                body.WriteInt64(previous.Version);
                WriteExpiry(ref body, previous.ExpiresAtUtc);
                body.WriteVarUInt32((uint)previous.Value.Length);
                body.WriteBytes(previous.Value.Span);
            }
        });
    }

    /// <summary>解码交换结果。旧记录的 key 由请求确定，返回空 key，值拥有独立缓冲。</summary>
    public static KvExchangeResult DecodeExchangeResponse(ReadOnlyMemory<byte> payload)
    {
        var reader = new SpanReader(payload.Span);
        long? version = ReadMutationVersion(ref reader);
        bool found = ReadAtomicBoolean(ref reader);
        KvEntry? previous = null;
        if (found)
        {
            long previousVersion = ReadMutationVersion(ref reader) ?? throw new FrameFormatException("旧值版本不能为 0。");
            var expiry = ReadExpiry(ref reader);
            var value = ReadBody(ref reader, payload, "previous value");
            previous = new KvEntry(ReadOnlyMemory<byte>.Empty, value.ToArray(), previousVersion, expiry);
        }
        RequireEnd(ref reader, "exchange");
        if (found && version is null)
            throw new FrameFormatException("存在旧记录的交换必须返回变更版本。");
        return new KvExchangeResult(previous, version);
    }

    /// <summary>编码 CAS 结果。</summary>
    public static void EncodeCasResponse(IBufferWriter<byte> writer, uint streamId, KvCasResult result) =>
        WriteAtomicResponse(writer, streamId, KvFrameOp.CompareAndSet, 1 + 2 * sizeof(long), (ref SpanWriter body) =>
        {
            body.WriteByte(result.Succeeded ? (byte)1 : (byte)0);
            body.WriteInt64(result.CurrentVersion);
            body.WriteInt64(result.NewVersion ?? 0);
        });

    /// <summary>解码 CAS 结果，拒绝成功标记与版本不一致的响应。</summary>
    public static KvCasResult DecodeCasResponse(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        bool succeeded = ReadAtomicBoolean(ref reader);
        long current = ReadMutationVersion(ref reader) ?? 0;
        long? next = ReadMutationVersion(ref reader);
        RequireEnd(ref reader, "cas");
        if (succeeded != next.HasValue)
            throw new FrameFormatException("CAS 成功标记与版本不一致。");
        return new KvCasResult(succeeded, current, next);
    }

    /// <summary>编码 expire/persist 的成功标记。</summary>
    public static void EncodeBooleanResponse(IBufferWriter<byte> writer, uint streamId, KvFrameOp op, bool succeeded) =>
        WriteAtomicResponse(writer, streamId, op, 1, (ref SpanWriter body) => body.WriteByte(succeeded ? (byte)1 : (byte)0));

    /// <summary>解码 expire/persist 的成功标记。</summary>
    public static bool DecodeBooleanResponse(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        bool result = ReadAtomicBoolean(ref reader);
        RequireEnd(ref reader, "boolean");
        return result;
    }

    /// <summary>编码 TTL，缺失为 -2，永不过期为 -1。</summary>
    public static void EncodeTtlResponse(IBufferWriter<byte> writer, uint streamId, KvTtlResult result) =>
        WriteAtomicResponse(writer, streamId, KvFrameOp.GetTimeToLive, sizeof(long) + MeasureExpiry(result.ExpiresAtUtc),
            (ref SpanWriter body) => { body.WriteInt64(result.Milliseconds); WriteExpiry(ref body, result.ExpiresAtUtc); });

    /// <summary>解码 TTL 响应。</summary>
    public static KvTtlResult DecodeTtlResponse(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        long milliseconds = reader.ReadInt64();
        var expiry = ReadExpiry(ref reader);
        RequireEnd(ref reader, "ttl");
        if (milliseconds < -2 || (milliseconds >= 0) != expiry.HasValue)
            throw new FrameFormatException("TTL 状态与过期时间不一致。");
        return new KvTtlResult(milliseconds, expiry);
    }

    private static void WriteAtomicResponse(IBufferWriter<byte> writer, uint streamId, KvFrameOp op, int length, MetaWriter write)
    {
        ValidateFramePayloadLength(length);
        WriteHeaderAndMeta(writer, new FrameHeader((uint)length, FrameHeader.CurrentVersion,
            (byte)FrameService.Kv, (byte)op, (byte)FrameFlags.Response, streamId), length, write);
    }

    private static long? ReadMutationVersion(ref SpanReader reader)
    {
        long version = reader.ReadInt64();
        if (version < 0) throw new FrameFormatException("版本不能为负数。");
        return version == 0 ? null : version;
    }

    private static bool ReadAtomicBoolean(ref SpanReader reader) => reader.ReadByte() switch
    {
        0 => false,
        1 => true,
        _ => throw new FrameFormatException("布尔标记必须为 0 或 1。"),
    };

    private static void ValidateAtomicWrite(KvFrameOp op, KvSetCondition condition, long version, DateTimeOffset? expiry, int valueLength)
    {
        if (op is not (KvFrameOp.SetConditional or KvFrameOp.GetAndSet or KvFrameOp.CompareAndSet or KvFrameOp.Expire))
            throw new ArgumentOutOfRangeException(nameof(op));
        if (condition is < KvSetCondition.Always or > KvSetCondition.IfExists || (op != KvFrameOp.SetConditional && condition != KvSetCondition.Always))
            throw new ArgumentOutOfRangeException(nameof(condition));
        if (version < 0 || (op != KvFrameOp.CompareAndSet && version != 0))
            throw new ArgumentOutOfRangeException(nameof(version));
        if (op == KvFrameOp.Expire && (expiry is null || valueLength != 0))
            throw new ArgumentException("expire 必须包含过期时间且不能包含 value。");
    }
}

/// <summary>原子 KV 写请求。key/value 是请求缓冲上的视图。</summary>
/// <param name="Db">数据库名。</param>
/// <param name="Keyspace">keyspace 名。</param>
/// <param name="Key">原始 key。</param>
/// <param name="Value">原始值。</param>
/// <param name="Condition">写入条件。</param>
/// <param name="ExpectedVersion">期望版本。</param>
/// <param name="ExpiresAtUtc">UTC 过期时间。</param>
public readonly record struct KvAtomicWriteFrameRequest(string Db, string Keyspace, ReadOnlyMemory<byte> Key,
    ReadOnlyMemory<byte> Value, KvSetCondition Condition, long ExpectedVersion, DateTimeOffset? ExpiresAtUtc);
