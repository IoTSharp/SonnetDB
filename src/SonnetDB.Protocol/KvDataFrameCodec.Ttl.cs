using System.Buffers;
using SonnetDB.IO;

namespace SonnetDB.Protocol;

/// <summary>KV TTL 查询的引擎无关结果。</summary>
/// <param name="Milliseconds">剩余 TTL 毫秒；缺失键为 <see cref="Missing"/>，永不过期为 <see cref="NoExpiration"/>。</param>
/// <param name="ExpiresAtUtc">服务端确认的 UTC 过期时间；缺失键和永不过期时为空。</param>
public sealed record KvDataFrameTtl(long Milliseconds, DateTimeOffset? ExpiresAtUtc)
{
    /// <summary>目标键不存在时的 TTL 哨兵值。</summary>
    public const long Missing = -2;

    /// <summary>目标键存在但没有过期时间时的 TTL 哨兵值。</summary>
    public const long NoExpiration = -1;
}

public static partial class KvDataFrameCodec
{
    /// <summary>编码 KV GetTimeToLive 请求，复用 key-only Frame v1 合同。</summary>
    /// <param name="writer">目标写入器。</param>
    /// <param name="streamId">请求关联编号。</param>
    /// <param name="db">数据库名。</param>
    /// <param name="keyspace">keyspace 名。</param>
    /// <param name="key">原始键。</param>
    /// <param name="maxPayloadBytes">请求 payload 上限。</param>
    public static void EncodeGetTimeToLiveRequest(
        IBufferWriter<byte> writer,
        uint streamId,
        string db,
        string keyspace,
        ReadOnlySpan<byte> key,
        int maxPayloadBytes = (int)FrameHeader.MaxFramePayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        int names = MeasureNames(db, keyspace);
        ValidateKey(key.Length);
        int length = CheckLength(
            (long)names + SpanWriter.MeasureVarUInt32((uint)key.Length) + key.Length,
            maxPayloadBytes);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + length);
        WriteHeader(span, length, KvFrameOp.GetTimeToLive, streamId);
        var body = new SpanWriter(span.Slice(FrameHeader.Size, length));
        WriteNames(ref body, db, keyspace);
        WriteBytes(ref body, key);
        writer.Advance(FrameHeader.Size + length);
    }

    /// <summary>解码完整 TTL 响应并验证哨兵值、过期时间和尾部字节。</summary>
    /// <param name="payload">完整响应 payload。</param>
    /// <returns>服务端确认的剩余毫秒数和绝对过期时间。</returns>
    public static KvDataFrameTtl DecodeGetTimeToLiveResponse(ReadOnlySpan<byte> payload)
    {
        var reader = new SonnetDB.IO.SpanReader(payload);
        long milliseconds = reader.ReadInt64();
        DateTimeOffset? expiresAtUtc = ReadExpiry(ref reader);
        RequireEnd(ref reader);
        if (milliseconds < KvDataFrameTtl.Missing
            || (milliseconds >= 0) != expiresAtUtc.HasValue)
        {
            throw new FrameFormatException("KV TTL 状态与过期时间不一致。");
        }

        return new KvDataFrameTtl(milliseconds, expiresAtUtc);
    }
}
