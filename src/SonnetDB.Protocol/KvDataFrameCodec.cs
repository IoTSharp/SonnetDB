using System.Buffers;
using System.Text;
using SonnetDB.IO;

namespace SonnetDB.Protocol;

/// <summary>与引擎解耦的 KV Get 响应；值数组独立拥有。</summary>
/// <param name="Value">原始值字节。</param>
/// <param name="Version">非负单调版本号。</param>
/// <param name="ExpiresAtUtc">完整 UTC ticks 过期时间，空表示永不过期。</param>
public sealed record KvDataFrameValue(byte[] Value, long Version, DateTimeOffset? ExpiresAtUtc);

/// <summary>与引擎解耦的 KV 单页扫描记录；键和值数组独立拥有。</summary>
/// <param name="Key">原始键字节。</param>
/// <param name="Value">原始值字节。</param>
/// <param name="Version">非负单调版本号。</param>
/// <param name="ExpiresAtUtc">完整 UTC ticks 过期时间。</param>
public sealed record KvDataFrameEntry(byte[] Key, byte[] Value, long Version, DateTimeOffset? ExpiresAtUtc);

/// <summary>KV Get/Put/Scan/GetAndDelete 的引擎无关请求编码和响应解码，保持既有 Frame v1 字节合同。</summary>
public static partial class KvDataFrameCodec
{
    /// <summary>数据库及 keyspace 名称的 UTF-8 字节上限。</summary>
    public const int MaxNameBytes = 512;

    /// <summary>键、前缀及起始键的字节上限。</summary>
    public const int MaxKeyBytes = 64 * 1024;

    /// <summary>显式单页扫描上限，与现有 Server 限制一致。</summary>
    public const int MaxScanEntries = 10_000;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>编码既有 Get 请求帧，写入前检查完整请求预算。</summary>
    /// <param name="writer">目标写入器。</param>
    /// <param name="streamId">关联编号。</param>
    /// <param name="db">数据库名。</param>
    /// <param name="keyspace">keyspace 名。</param>
    /// <param name="key">原始键。</param>
    /// <param name="maxPayloadBytes">请求编码 payload 上限。</param>
    public static void EncodeGetRequest(IBufferWriter<byte> writer, uint streamId,
        string db, string keyspace, ReadOnlySpan<byte> key,
        int maxPayloadBytes = (int)FrameHeader.MaxFramePayloadBytes)
    {
        int names = MeasureNames(db, keyspace);
        ValidateKey(key.Length);
        int length = CheckLength((long)names + SpanWriter.MeasureVarUInt32((uint)key.Length) + key.Length, maxPayloadBytes);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + length);
        WriteHeader(span, length, KvFrameOp.Get, streamId);
        var body = new SpanWriter(span.Slice(FrameHeader.Size, length));
        WriteNames(ref body, db, keyspace);
        WriteBytes(ref body, key);
        writer.Advance(FrameHeader.Size + length);
    }

    /// <summary>编码既有 Put 请求帧，原始二进制值及 UTC ticks 均无损。</summary>
    /// <param name="writer">目标写入器。</param>
    /// <param name="streamId">关联编号。</param>
    /// <param name="db">数据库名。</param>
    /// <param name="keyspace">keyspace 名。</param>
    /// <param name="key">原始键。</param>
    /// <param name="value">原始值，可为空。</param>
    /// <param name="expiresAtUtc">绝对过期时间。</param>
    /// <param name="maxPayloadBytes">请求编码 payload 上限。</param>
    public static void EncodePutRequest(IBufferWriter<byte> writer, uint streamId,
        string db, string keyspace, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value,
        DateTimeOffset? expiresAtUtc = null, int maxPayloadBytes = (int)FrameHeader.MaxFramePayloadBytes)
    {
        int names = MeasureNames(db, keyspace);
        ValidateKey(key.Length);
        int length = CheckLength((long)names + SpanWriter.MeasureVarUInt32((uint)key.Length) + key.Length
            + (expiresAtUtc is null ? 1 : 9) + SpanWriter.MeasureVarUInt32((uint)value.Length) + value.Length, maxPayloadBytes);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + length);
        WriteHeader(span, length, KvFrameOp.Put, streamId);
        var body = new SpanWriter(span.Slice(FrameHeader.Size, length));
        WriteNames(ref body, db, keyspace);
        WriteBytes(ref body, key);
        body.WriteByte(expiresAtUtc is null ? (byte)0 : (byte)1);
        if (expiresAtUtc is { } expiry) body.WriteInt64(expiry.UtcTicks);
        WriteBytes(ref body, value);
        writer.Advance(FrameHeader.Size + length);
    }

    /// <summary>编码既有 Scan 请求帧；零 limit 保留旧 Server 默认行为。</summary>
    /// <param name="writer">目标写入器。</param>
    /// <param name="streamId">关联编号。</param>
    /// <param name="db">数据库名。</param>
    /// <param name="keyspace">keyspace 名。</param>
    /// <param name="prefix">原始键前缀。</param>
    /// <param name="afterKey">排除性的起始键。</param>
    /// <param name="limit">请求条数；零使用 Server 默认。</param>
    /// <param name="maxPayloadBytes">请求编码 payload 上限。</param>
    public static void EncodeScanRequest(IBufferWriter<byte> writer, uint streamId,
        string db, string keyspace, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> afterKey = default,
        int limit = 0, int maxPayloadBytes = (int)FrameHeader.MaxFramePayloadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        int names = MeasureNames(db, keyspace);
        ValidateKey(prefix.Length);
        ValidateKey(afterKey.Length);
        int length = CheckLength((long)names + SpanWriter.MeasureVarUInt32((uint)prefix.Length) + prefix.Length
            + SpanWriter.MeasureVarUInt32((uint)afterKey.Length) + afterKey.Length
            + SpanWriter.MeasureVarUInt32((uint)limit), maxPayloadBytes);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + length);
        WriteHeader(span, length, KvFrameOp.Scan, streamId);
        var body = new SpanWriter(span.Slice(FrameHeader.Size, length));
        WriteNames(ref body, db, keyspace);
        WriteBytes(ref body, prefix);
        WriteBytes(ref body, afterKey);
        body.WriteVarUInt32((uint)limit);
        writer.Advance(FrameHeader.Size + length);
    }

    /// <summary>解码完整 Get 响应；缺失键返回空引用，空值保留为零长度数组。</summary>
    /// <param name="payload">响应 payload。</param>
    /// <returns>独立拥有的值、版本及过期时间。</returns>
    public static KvDataFrameValue? DecodeGetResponse(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        byte found = reader.ReadByte();
        if (found > 1) throw new FrameFormatException("KV Get found 标记必须为 0 或 1。");
        if (found == 0)
        {
            RequireEnd(ref reader);
            return null;
        }
        long version = ReadVersion(ref reader);
        DateTimeOffset? expiry = ReadExpiry(ref reader);
        byte[] value = ReadBytes(ref reader, int.MaxValue).ToArray();
        RequireEnd(ref reader);
        return new(value, version, expiry);
    }

    /// <summary>解码完整 Put 响应版本，拒绝尾字节及溢出。</summary>
    /// <param name="payload">响应 payload。</param>
    /// <returns>服务器确认的写入版本。</returns>
    public static long DecodePutResponse(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        long version = ReadVersion(ref reader);
        RequireEnd(ref reader);
        return version;
    }

    /// <summary>解码单页 Scan 响应，在分配条目数组前检查显式条数和 payload 边界。</summary>
    /// <param name="payload">响应 payload。</param>
    /// <param name="maxEntries">允许的最大返回条数。</param>
    /// <returns>独立拥有的单页记录。</returns>
    public static KvDataFrameEntry[] DecodeScanResponse(ReadOnlySpan<byte> payload, int maxEntries = MaxScanEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxEntries);
        var reader = new SpanReader(payload);
        uint count = reader.ReadVarUInt32();
        // 每条至少包含 key 长度、version、expiry 标记和 value 长度四个字节。
        if (count > (uint)maxEntries || count > (uint)(reader.Remaining / 4))
            throw new FrameFormatException("KV Scan 条数超过本页预算或 payload 剩余长度。");
        var entries = new KvDataFrameEntry[(int)count];
        for (int i = 0; i < entries.Length; i++)
        {
            byte[] key = ReadBytes(ref reader, MaxKeyBytes).ToArray();
            long version = ReadVersion(ref reader);
            DateTimeOffset? expiry = ReadExpiry(ref reader);
            byte[] value = ReadBytes(ref reader, int.MaxValue).ToArray();
            entries[i] = new(key, value, version, expiry);
        }
        RequireEnd(ref reader);
        return entries;
    }

    private static int MeasureNames(string db, string keyspace)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(keyspace);
        if (StrictUtf8.GetByteCount(db) > MaxNameBytes || StrictUtf8.GetByteCount(keyspace) > MaxNameBytes)
            throw new ArgumentException("KV 名称超过 UTF-8 字节预算。");
        return SpanWriter.MeasureVarString(db) + SpanWriter.MeasureVarString(keyspace);
    }

    private static int CheckLength(long length, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximum, (int)FrameHeader.MaxFramePayloadBytes);
        if (length > maximum) throw new ArgumentOutOfRangeException(nameof(maximum), "KV 请求超过编码字节预算。");
        return (int)length;
    }

    private static void ValidateKey(int length)
        => ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaxKeyBytes);

    private static void WriteHeader(Span<byte> span, int length, KvFrameOp op, uint streamId)
        => new FrameHeader((uint)length, FrameHeader.CurrentVersion, (byte)FrameService.Kv,
            (byte)op, (byte)FrameFlags.None, streamId).Write(span);

    private static void WriteNames(ref SpanWriter writer, string db, string keyspace)
    {
        writer.WriteVarString(db);
        writer.WriteVarString(keyspace);
    }

    private static void WriteBytes(ref SpanWriter writer, ReadOnlySpan<byte> bytes)
    {
        writer.WriteVarUInt32((uint)bytes.Length);
        writer.WriteBytes(bytes);
    }

    private static ReadOnlySpan<byte> ReadBytes(ref SpanReader reader, int maximum)
    {
        uint length = reader.ReadVarUInt32();
        if (length > (uint)maximum || length > (uint)reader.Remaining)
            throw new FrameFormatException("KV 字节字段长度超过上限或 payload 剩余长度。");
        return reader.ReadBytes((int)length);
    }

    private static long ReadVersion(ref SpanReader reader)
    {
        ulong version = reader.ReadVarUInt64();
        if (version > long.MaxValue) throw new FrameFormatException("KV 版本超出 Int64 范围。");
        return (long)version;
    }

    private static DateTimeOffset? ReadExpiry(ref SpanReader reader)
    {
        byte flag = reader.ReadByte();
        if (flag > 1) throw new FrameFormatException("KV expiry 标记必须为 0 或 1。");
        if (flag == 0) return null;
        long ticks = reader.ReadInt64();
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            throw new FrameFormatException("KV expiry ticks 超出范围。");
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static void RequireEnd(ref SpanReader reader)
    {
        if (reader.Remaining != 0) throw new FrameFormatException("KV 响应含尾部多余字节。");
    }
}
