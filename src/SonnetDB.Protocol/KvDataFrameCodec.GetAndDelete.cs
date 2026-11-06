using System.Buffers;
using SonnetDB.IO;

namespace SonnetDB.Protocol;

/// <summary>与引擎解耦的 KV 原子交换结果；旧值独立拥有，缺失与空二进制值保持区分。</summary>
/// <param name="PreviousValue">交换前的记录，缺失时为空引用。</param>
/// <param name="MutationVersion">已确认的变更版本，未发生变更时为空引用；线上的零版本转换为空引用。</param>
public sealed record KvDataFrameExchangeResult(KvDataFrameValue? PreviousValue, long? MutationVersion);

public static partial class KvDataFrameCodec
{
    /// <summary>编码既有 GetAndDelete 请求帧，写入前检查名称、键和完整请求字节预算。</summary>
    /// <param name="writer">目标写入器。</param>
    /// <param name="streamId">请求关联编号。</param>
    /// <param name="db">数据库名。</param>
    /// <param name="keyspace">keyspace 名。</param>
    /// <param name="key">原始二进制键。</param>
    /// <param name="maxPayloadBytes">请求编码 payload 上限。</param>
    public static void EncodeGetAndDeleteRequest(IBufferWriter<byte> writer, uint streamId,
        string db, string keyspace, ReadOnlySpan<byte> key,
        int maxPayloadBytes = (int)FrameHeader.MaxFramePayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        int names = MeasureNames(db, keyspace);
        ValidateKey(key.Length);
        int length = CheckLength((long)names + SpanWriter.MeasureVarUInt32((uint)key.Length) + key.Length, maxPayloadBytes);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + length);
        WriteHeader(span, length, KvFrameOp.GetAndDelete, streamId);
        var body = new SpanWriter(span.Slice(FrameHeader.Size, length));
        WriteNames(ref body, db, keyspace);
        WriteBytes(ref body, key);
        writer.Advance(FrameHeader.Size + length);
    }

    /// <summary>解码既有原子交换响应，保留固定 Int64 版本、旧值存在性及完整 UTC ticks；拒绝无效状态和尾字节。</summary>
    /// <param name="payload">完整交换响应 payload，不能使用 Get 的变长版本布局。</param>
    /// <returns>已确认的变更版本和独立拥有的旧记录；旧记录缺失时仍保留响应中的变更版本。</returns>
    public static KvDataFrameExchangeResult DecodeExchangeResponse(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        long? mutationVersion = ReadExchangeVersion(ref reader);
        byte found = reader.ReadByte();
        if (found > 1) throw new FrameFormatException("KV exchange found 标记必须为 0 或 1。");
        if (found == 0)
        {
            RequireEnd(ref reader);
            return new(null, mutationVersion);
        }
        if (mutationVersion is null)
            throw new FrameFormatException("存在旧记录的交换必须返回变更版本。");
        long previousVersion = ReadExchangeVersion(ref reader)
            ?? throw new FrameFormatException("旧值版本不能为 0。");
        DateTimeOffset? expiry = ReadExpiry(ref reader);
        ReadOnlySpan<byte> value = ReadBytes(ref reader, int.MaxValue);
        RequireEnd(ref reader);
        return new(new(value.ToArray(), previousVersion, expiry), mutationVersion);
    }

    private static long? ReadExchangeVersion(ref SpanReader reader)
    {
        long version = reader.ReadInt64();
        if (version < 0) throw new FrameFormatException("KV exchange 版本不能为负数。");
        return version == 0 ? null : version;
    }
}
