using System.Buffers;
using System.Text;
using SonnetDB.IO;
using SonnetDB.Kv;

namespace SonnetDB.Protocol;

/// <summary>
/// kv service（<see cref="FrameService.Kv"/>）get / put / scan 三个 opcode 的帧体编解码（M28 P5b #240）。
/// key / value 以原始字节直传（零 Base64）；解码结果中的 key / value 字段是输入缓冲上的零拷贝视图，
/// 仅在缓冲存活期内有效（服务端在 PipeReader AdvanceTo 之前处理完毕；引擎 Put 内部自行拷贝）。
/// TTL / incr / cas / remove 等操作不进帧（走 REST KV 端点）。
/// </summary>
public static class KvFrameCodec
{
    /// <summary>名字（db / keyspace）UTF-8 字节数上限。</summary>
    public const int MaxNameBytes = 512;

    /// <summary>key / prefix / afterKey 字节数上限（对齐 <c>KvOptions.MaxKeyBytes</c> 默认值）。</summary>
    public const int MaxKeyBytes = 64 * 1024;

    // ────────────────────────────── get (op=1) ──────────────────────────────

    /// <summary>
    /// 编码 get 请求帧：db, keyspace, key。
    /// </summary>
    public static void EncodeGetRequest(
        IBufferWriter<byte> writer,
        uint streamId,
        string db,
        string keyspace,
        ReadOnlySpan<byte> key)
    {
        ValidateKeyLength(key.Length, "key");
        int metaLength =
            SpanWriter.MeasureVarString(db) +
            SpanWriter.MeasureVarString(keyspace) +
            SpanWriter.MeasureVarUInt32((uint)key.Length);
        long payloadLength = (long)metaLength + key.Length;
        ValidateFramePayloadLength(payloadLength);

        int keyLength = key.Length;
        WriteHeaderAndMeta(
            writer,
            new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
                (byte)FrameService.Kv, (byte)KvFrameOp.Get, (byte)FrameFlags.None, streamId),
            metaLength,
            (ref SpanWriter meta) =>
            {
                meta.WriteVarString(db);
                meta.WriteVarString(keyspace);
                meta.WriteVarUInt32((uint)keyLength);
            });
        if (!key.IsEmpty)
            writer.Write(key);
    }

    /// <summary>
    /// 解码 get 请求帧体。
    /// </summary>
    public static KvGetFrameRequest DecodeGetRequest(ReadOnlyMemory<byte> payload)
    {
        var reader = new SpanReader(payload.Span);
        string db = ReadName(ref reader, "db");
        string keyspace = ReadName(ref reader, "keyspace");
        ReadOnlyMemory<byte> key = ReadBoundedBytes(ref reader, payload, MaxKeyBytes, "key");
        RequireEnd(ref reader, "get 请求");
        return new KvGetFrameRequest(db, keyspace, key);
    }

    /// <summary>
    /// 编码 get 响应帧：found u8；命中时附 version varuint64 + 过期时间 + value 原始字节。
    /// </summary>
    public static void EncodeGetResponse(IBufferWriter<byte> writer, uint streamId, KvEntry? entry)
    {
        if (entry is null)
        {
            WriteHeaderAndMeta(
                writer,
                new FrameHeader(1, FrameHeader.CurrentVersion,
                    (byte)FrameService.Kv, (byte)KvFrameOp.Get, (byte)FrameFlags.Response, streamId),
                1,
                (ref SpanWriter meta) => meta.WriteByte(0));
            return;
        }

        int metaLength = 1
            + SpanWriter.MeasureVarUInt64((ulong)entry.Version)
            + MeasureExpiry(entry.ExpiresAtUtc)
            + SpanWriter.MeasureVarUInt32((uint)entry.Value.Length);
        long payloadLength = (long)metaLength + entry.Value.Length;
        ValidateFramePayloadLength(payloadLength);

        KvEntry local = entry;
        WriteHeaderAndMeta(
            writer,
            new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
                (byte)FrameService.Kv, (byte)KvFrameOp.Get, (byte)FrameFlags.Response, streamId),
            metaLength,
            (ref SpanWriter meta) =>
            {
                meta.WriteByte(1);
                meta.WriteVarUInt64((ulong)local.Version);
                WriteExpiry(ref meta, local.ExpiresAtUtc);
                meta.WriteVarUInt32((uint)local.Value.Length);
            });
        if (entry.Value.Length > 0)
            writer.Write(entry.Value.Span);
    }

    /// <summary>
    /// 解码 get 响应帧体。未命中时返回 null；命中时 value 已物化为独立数组。
    /// </summary>
    public static KvGetFrameResult? DecodeGetResponse(ReadOnlyMemory<byte> payload)
    {
        var reader = new SpanReader(payload.Span);
        byte found = reader.ReadByte();
        if (found > 1)
            throw new FrameFormatException($"get 响应 found 标记 {found} 非法（0/1）。");
        if (found == 0)
        {
            RequireEnd(ref reader, "get 响应");
            return null;
        }

        long version = ReadVersion(ref reader);
        DateTimeOffset? expiresAtUtc = ReadExpiry(ref reader);
        ReadOnlyMemory<byte> value = ReadBody(ref reader, payload, "value");
        RequireEnd(ref reader, "get 响应");
        return new KvGetFrameResult(value.ToArray(), version, expiresAtUtc);
    }

    // ────────────────────────────── put (op=2) ──────────────────────────────

    /// <summary>
    /// 编码 put 请求帧：db, keyspace, key, 可选过期时间, value。
    /// </summary>
    public static void EncodePutRequest(
        IBufferWriter<byte> writer,
        uint streamId,
        string db,
        string keyspace,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> value,
        DateTimeOffset? expiresAtUtc = null)
    {
        ValidateKeyLength(key.Length, "key");
        int headLength =
            SpanWriter.MeasureVarString(db) +
            SpanWriter.MeasureVarString(keyspace) +
            SpanWriter.MeasureVarUInt32((uint)key.Length);
        int tailLength = MeasureExpiry(expiresAtUtc) + SpanWriter.MeasureVarUInt32((uint)value.Length);
        long payloadLength = (long)headLength + key.Length + tailLength + value.Length;
        ValidateFramePayloadLength(payloadLength);

        int keyLength = key.Length;
        WriteHeaderAndMeta(
            writer,
            new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
                (byte)FrameService.Kv, (byte)KvFrameOp.Put, (byte)FrameFlags.None, streamId),
            headLength,
            (ref SpanWriter meta) =>
            {
                meta.WriteVarString(db);
                meta.WriteVarString(keyspace);
                meta.WriteVarUInt32((uint)keyLength);
            });
        if (!key.IsEmpty)
            writer.Write(key);

        Span<byte> tail = writer.GetSpan(tailLength);
        var tailWriter = new SpanWriter(tail[..tailLength]);
        WriteExpiry(ref tailWriter, expiresAtUtc);
        tailWriter.WriteVarUInt32((uint)value.Length);
        writer.Advance(tailLength);
        if (!value.IsEmpty)
            writer.Write(value);
    }

    /// <summary>
    /// 解码 put 请求帧体。
    /// </summary>
    public static KvPutFrameRequest DecodePutRequest(ReadOnlyMemory<byte> payload)
    {
        var reader = new SpanReader(payload.Span);
        string db = ReadName(ref reader, "db");
        string keyspace = ReadName(ref reader, "keyspace");
        ReadOnlyMemory<byte> key = ReadBoundedBytes(ref reader, payload, MaxKeyBytes, "key");
        DateTimeOffset? expiresAtUtc = ReadExpiry(ref reader);
        ReadOnlyMemory<byte> value = ReadBody(ref reader, payload, "value");
        RequireEnd(ref reader, "put 请求");
        return new KvPutFrameRequest(db, keyspace, key, value, expiresAtUtc);
    }

    /// <summary>
    /// 编码 put 响应帧：本次写入的单调版本号。
    /// </summary>
    public static void EncodePutResponse(IBufferWriter<byte> writer, uint streamId, long version)
    {
        int payloadLength = SpanWriter.MeasureVarUInt64((ulong)version);
        WriteHeaderAndMeta(
            writer,
            new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
                (byte)FrameService.Kv, (byte)KvFrameOp.Put, (byte)FrameFlags.Response, streamId),
            payloadLength,
            (ref SpanWriter meta) => meta.WriteVarUInt64((ulong)version));
    }

    /// <summary>
    /// 解码 put 响应帧体。
    /// </summary>
    public static long DecodePutResponse(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        long version = ReadVersion(ref reader);
        RequireEnd(ref reader, "put 响应");
        return version;
    }

    // ────────────────────────────── scan (op=3) ──────────────────────────────

    /// <summary>
    /// 编码 scan 请求帧：db, keyspace, prefix（空 = 全部）, afterKey（空 = 从前缀起点）,
    /// limit（0 = 服务端默认）。
    /// </summary>
    public static void EncodeScanRequest(
        IBufferWriter<byte> writer,
        uint streamId,
        string db,
        string keyspace,
        ReadOnlySpan<byte> prefix,
        ReadOnlySpan<byte> afterKey = default,
        int limit = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ValidateKeyLength(prefix.Length, "prefix");
        ValidateKeyLength(afterKey.Length, "afterKey");
        int payloadLength =
            SpanWriter.MeasureVarString(db) +
            SpanWriter.MeasureVarString(keyspace) +
            SpanWriter.MeasureVarUInt32((uint)prefix.Length) + prefix.Length +
            SpanWriter.MeasureVarUInt32((uint)afterKey.Length) + afterKey.Length +
            SpanWriter.MeasureVarUInt32((uint)limit);

        var header = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            (byte)FrameService.Kv, (byte)KvFrameOp.Scan, (byte)FrameFlags.None, streamId);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + payloadLength);
        header.Write(span);
        var w = new SpanWriter(span.Slice(FrameHeader.Size, payloadLength));
        w.WriteVarString(db);
        w.WriteVarString(keyspace);
        w.WriteVarUInt32((uint)prefix.Length);
        w.WriteBytes(prefix);
        w.WriteVarUInt32((uint)afterKey.Length);
        w.WriteBytes(afterKey);
        w.WriteVarUInt32((uint)limit);
        writer.Advance(FrameHeader.Size + payloadLength);
    }

    /// <summary>
    /// 解码 scan 请求帧体。
    /// </summary>
    public static KvScanFrameRequest DecodeScanRequest(ReadOnlyMemory<byte> payload)
    {
        var reader = new SpanReader(payload.Span);
        string db = ReadName(ref reader, "db");
        string keyspace = ReadName(ref reader, "keyspace");
        ReadOnlyMemory<byte> prefix = ReadBoundedBytes(ref reader, payload, MaxKeyBytes, "prefix");
        ReadOnlyMemory<byte> afterKey = ReadBoundedBytes(ref reader, payload, MaxKeyBytes, "afterKey");
        uint limit = reader.ReadVarUInt32();
        if (limit > int.MaxValue)
            throw new FrameFormatException($"scan limit {limit} 非法。");
        RequireEnd(ref reader, "scan 请求");
        return new KvScanFrameRequest(db, keyspace, prefix, afterKey, (int)limit);
    }

    /// <summary>
    /// 编码 scan 响应帧：count + 每条 (key, version, 过期时间, value)，key 字节序升序。
    /// </summary>
    public static void EncodeScanResponse(IBufferWriter<byte> writer, uint streamId, IReadOnlyList<KvEntry> entries)
    {
        long payloadLength = SpanWriter.MeasureVarUInt32((uint)entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            KvEntry entry = entries[i];
            payloadLength += SpanWriter.MeasureVarUInt32((uint)entry.Key.Length) + entry.Key.Length
                + SpanWriter.MeasureVarUInt64((ulong)entry.Version)
                + MeasureExpiry(entry.ExpiresAtUtc)
                + SpanWriter.MeasureVarUInt32((uint)entry.Value.Length) + entry.Value.Length;
        }

        ValidateFramePayloadLength(payloadLength);

        WriteHeaderAndMeta(
            writer,
            new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
                (byte)FrameService.Kv, (byte)KvFrameOp.Scan, (byte)FrameFlags.Response, streamId),
            SpanWriter.MeasureVarUInt32((uint)entries.Count),
            (ref SpanWriter meta) => meta.WriteVarUInt32((uint)entries.Count));

        for (int i = 0; i < entries.Count; i++)
        {
            KvEntry entry = entries[i];
            int metaLength = SpanWriter.MeasureVarUInt32((uint)entry.Key.Length)
                + entry.Key.Length
                + SpanWriter.MeasureVarUInt64((ulong)entry.Version)
                + MeasureExpiry(entry.ExpiresAtUtc)
                + SpanWriter.MeasureVarUInt32((uint)entry.Value.Length);
            Span<byte> span = writer.GetSpan(metaLength);
            var meta = new SpanWriter(span[..metaLength]);
            meta.WriteVarUInt32((uint)entry.Key.Length);
            meta.WriteBytes(entry.Key.Span);
            meta.WriteVarUInt64((ulong)entry.Version);
            WriteExpiry(ref meta, entry.ExpiresAtUtc);
            meta.WriteVarUInt32((uint)entry.Value.Length);
            writer.Advance(metaLength);
            if (entry.Value.Length > 0)
                writer.Write(entry.Value.Span);
        }
    }

    /// <summary>
    /// 解码 scan 响应帧体。key / value 已物化为独立数组。
    /// </summary>
    public static KvScanFrameEntry[] DecodeScanResponse(ReadOnlyMemory<byte> payload)
    {
        var reader = new SpanReader(payload.Span);
        uint count = reader.ReadVarUInt32();
        if (count > (uint)payload.Length)
            throw new FrameFormatException($"scan 响应条目数 {count} 超出帧体长度。");

        var entries = new KvScanFrameEntry[count];
        for (int i = 0; i < entries.Length; i++)
        {
            ReadOnlyMemory<byte> key = ReadBoundedBytes(ref reader, payload, MaxKeyBytes, "key");
            long version = ReadVersion(ref reader);
            DateTimeOffset? expiresAtUtc = ReadExpiry(ref reader);
            ReadOnlyMemory<byte> value = ReadBody(ref reader, payload, "value");
            entries[i] = new KvScanFrameEntry(key.ToArray(), value.ToArray(), version, expiresAtUtc);
        }

        RequireEnd(ref reader, "scan 响应");
        return entries;
    }

    // ────────────────────────────── 内部辅助 ──────────────────────────────

    private delegate void MetaWriter(ref SpanWriter writer);

    private static void WriteHeaderAndMeta(IBufferWriter<byte> writer, in FrameHeader header, int metaLength, MetaWriter writeMeta)
    {
        Span<byte> span = writer.GetSpan(FrameHeader.Size + metaLength);
        header.Write(span);
        var meta = new SpanWriter(span.Slice(FrameHeader.Size, metaLength));
        writeMeta(ref meta);
        writer.Advance(FrameHeader.Size + metaLength);
    }

    private static void ValidateFramePayloadLength(long payloadLength)
    {
        if (payloadLength > FrameHeader.MaxFramePayloadBytes)
            throw new ArgumentException($"帧 payload 长度 {payloadLength} 超过上限 {FrameHeader.MaxFramePayloadBytes}。");
    }

    private static void ValidateKeyLength(int length, string field)
    {
        if (length > MaxKeyBytes)
            throw new ArgumentException($"{field} 长度 {length} 超过上限 {MaxKeyBytes} 字节。");
    }

    private static int MeasureExpiry(DateTimeOffset? expiresAtUtc)
        => expiresAtUtc is null ? 1 : 1 + sizeof(long);

    private static void WriteExpiry(ref SpanWriter writer, DateTimeOffset? expiresAtUtc)
    {
        if (expiresAtUtc is null)
        {
            writer.WriteByte(0);
            return;
        }

        writer.WriteByte(1);
        writer.WriteInt64(expiresAtUtc.Value.UtcTicks);
    }

    private static DateTimeOffset? ReadExpiry(ref SpanReader reader)
    {
        byte hasExpiry = reader.ReadByte();
        if (hasExpiry > 1)
            throw new FrameFormatException($"过期时间标记 {hasExpiry} 非法（0/1）。");
        if (hasExpiry == 0)
            return null;

        long ticks = reader.ReadInt64();
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            throw new FrameFormatException($"过期时间 ticks {ticks} 超出范围。");
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static long ReadVersion(ref SpanReader reader)
    {
        ulong value = reader.ReadVarUInt64();
        if (value > long.MaxValue)
            throw new FrameFormatException($"version {value} 超出 long 范围。");
        return (long)value;
    }

    private static string ReadName(ref SpanReader reader, string field)
    {
        uint length = reader.ReadVarUInt32();
        if (length > MaxNameBytes)
            throw new FrameFormatException($"{field} 长度 {length} 超过上限 {MaxNameBytes} 字节。");
        if (length == 0)
            return string.Empty;
        if (length > (uint)reader.Remaining)
            throw new FrameFormatException($"{field} 长度 {length} 超出帧体剩余长度。");
        return Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    private static ReadOnlyMemory<byte> ReadBoundedBytes(ref SpanReader reader, ReadOnlyMemory<byte> payload, int maxBytes, string field)
    {
        uint length = reader.ReadVarUInt32();
        if (length > (uint)maxBytes)
            throw new FrameFormatException($"{field} 长度 {length} 超过上限 {maxBytes} 字节。");
        if (length > (uint)reader.Remaining)
            throw new FrameFormatException($"{field} 长度 {length} 超出帧体剩余长度。");
        ReadOnlyMemory<byte> body = payload.Slice(reader.Position, (int)length);
        reader.Skip((int)length);
        return body;
    }

    private static ReadOnlyMemory<byte> ReadBody(ref SpanReader reader, ReadOnlyMemory<byte> payload, string field)
    {
        uint length = reader.ReadVarUInt32();
        if (length > (uint)reader.Remaining)
            throw new FrameFormatException($"{field} 长度 {length} 超出帧体剩余长度。");
        ReadOnlyMemory<byte> body = payload.Slice(reader.Position, (int)length);
        reader.Skip((int)length);
        return body;
    }

    private static void RequireEnd(ref SpanReader reader, string frame)
    {
        if (reader.Remaining != 0)
            throw new FrameFormatException($"{frame}帧体尾部有多余字节。");
    }
}

/// <summary>get 请求帧解码结果。<see cref="Key"/> 为输入缓冲的零拷贝视图。</summary>
/// <param name="Db">数据库名。</param>
/// <param name="Keyspace">keyspace 名。</param>
/// <param name="Key">key 字节视图。</param>
public readonly record struct KvGetFrameRequest(
    string Db,
    string Keyspace,
    ReadOnlyMemory<byte> Key);

/// <summary>get 响应帧解码结果（命中时）。</summary>
/// <param name="Value">value 字节（已物化）。</param>
/// <param name="Version">最后一次写入该 key 的单调版本号。</param>
/// <param name="ExpiresAtUtc">UTC 过期时间；为空表示永不过期。</param>
public sealed record KvGetFrameResult(
    byte[] Value,
    long Version,
    DateTimeOffset? ExpiresAtUtc);

/// <summary>put 请求帧解码结果。<see cref="Key"/> / <see cref="Value"/> 为输入缓冲的零拷贝视图。</summary>
/// <param name="Db">数据库名。</param>
/// <param name="Keyspace">keyspace 名。</param>
/// <param name="Key">key 字节视图。</param>
/// <param name="Value">value 字节视图。</param>
/// <param name="ExpiresAtUtc">UTC 过期时间；为空表示永不过期。</param>
public readonly record struct KvPutFrameRequest(
    string Db,
    string Keyspace,
    ReadOnlyMemory<byte> Key,
    ReadOnlyMemory<byte> Value,
    DateTimeOffset? ExpiresAtUtc);

/// <summary>scan 请求帧解码结果。<see cref="Prefix"/> / <see cref="AfterKey"/> 为输入缓冲的零拷贝视图。</summary>
/// <param name="Db">数据库名。</param>
/// <param name="Keyspace">keyspace 名。</param>
/// <param name="Prefix">key 前缀视图（空 = 全部）。</param>
/// <param name="AfterKey">起始 key 视图（空 = 从前缀起点开始）。</param>
/// <param name="Limit">最大返回条数（0 = 服务端默认）。</param>
public readonly record struct KvScanFrameRequest(
    string Db,
    string Keyspace,
    ReadOnlyMemory<byte> Prefix,
    ReadOnlyMemory<byte> AfterKey,
    int Limit);

/// <summary>scan 响应帧中的一条记录（已物化）。</summary>
/// <param name="Key">key 字节。</param>
/// <param name="Value">value 字节。</param>
/// <param name="Version">最后一次写入该 key 的单调版本号。</param>
/// <param name="ExpiresAtUtc">UTC 过期时间；为空表示永不过期。</param>
public sealed record KvScanFrameEntry(
    byte[] Key,
    byte[] Value,
    long Version,
    DateTimeOffset? ExpiresAtUtc);
