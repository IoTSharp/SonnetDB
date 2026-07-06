using System.Buffers;
using System.Text;
using SonnetDB.IO;
using SonnetDB.ObjectStorage;

namespace SonnetDB.Protocol;

/// <summary>
/// object service（<see cref="FrameService.Object"/>）get / put 两个 opcode 的帧体编解码（M28 P5b #240）。
/// 对象内容以原始字节直传（零 Base64）；get 响应为**同 streamId 的帧序列**
/// meta（对象元数据）→ data × N（内容分块，默认 256 KiB）→ end（总字节数），
/// 复用 #238 的流式分块思路——服务端逐块 flush，响应缓冲内存上界 = 单块，大 blob 增量到达客户端。
/// put 为单帧（内容 ≤ 单帧 payload 上限 132 MiB）；更大对象走 REST multipart 上传。
/// bucket 管理 / 版本列表 / 生命周期等管理面不进帧（走 REST S3 兼容端点）。
/// </summary>
public static class ObjectFrameCodec
{
    /// <summary>名字（db / bucket / versionId / contentType / metadata·tags key）UTF-8 字节数上限。</summary>
    public const int MaxNameBytes = 512;

    /// <summary>对象 key UTF-8 字节数上限（引擎权威上限 1024 字符，UTF-8 放大 4 倍）。</summary>
    public const int MaxKeyBytes = 4096;

    /// <summary>metadata / tags 单张字典条目数上限。</summary>
    public const int MaxMapCount = 64;

    /// <summary>metadata / tags 单张字典（key+value UTF-8 字节）总量上限。</summary>
    public const int MaxMapBytes = 16 * 1024;

    /// <summary>get 响应 data 帧默认分块字节数。</summary>
    public const int DefaultDataChunkBytes = 256 * 1024;

    private static readonly IReadOnlyDictionary<string, string> EmptyMap =
        new Dictionary<string, string>(0);

    // ────────────────────────────── get (op=1) 请求 ──────────────────────────────

    /// <summary>
    /// 编码 get 请求帧：db, bucket, key, versionId（空串 = 最新版本）。
    /// </summary>
    public static void EncodeGetRequest(
        IBufferWriter<byte> writer,
        uint streamId,
        string db,
        string bucket,
        string key,
        string? versionId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucket);
        ArgumentException.ThrowIfNullOrEmpty(key);
        int payloadLength =
            SpanWriter.MeasureVarString(db) +
            SpanWriter.MeasureVarString(bucket) +
            SpanWriter.MeasureVarString(key) +
            SpanWriter.MeasureVarString(versionId ?? string.Empty);

        var header = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            (byte)FrameService.Object, (byte)ObjectFrameOp.Get, (byte)FrameFlags.None, streamId);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + payloadLength);
        header.Write(span);
        var w = new SpanWriter(span.Slice(FrameHeader.Size, payloadLength));
        w.WriteVarString(db);
        w.WriteVarString(bucket);
        w.WriteVarString(key);
        w.WriteVarString(versionId ?? string.Empty);
        writer.Advance(FrameHeader.Size + payloadLength);
    }

    /// <summary>
    /// 解码 get 请求帧体。
    /// </summary>
    public static ObjectGetFrameRequest DecodeGetRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        string db = ReadBoundedString(ref reader, MaxNameBytes, "db");
        string bucket = ReadBoundedString(ref reader, MaxNameBytes, "bucket");
        string key = ReadBoundedString(ref reader, MaxKeyBytes, "key");
        string versionId = ReadBoundedString(ref reader, MaxNameBytes, "versionId");
        RequireEnd(ref reader, "get 请求");
        return new ObjectGetFrameRequest(db, bucket, key, versionId.Length == 0 ? null : versionId);
    }

    // ────────────────────────────── get 响应帧序列 ──────────────────────────────

    /// <summary>
    /// 编码 get meta 响应帧（chunkKind=1）：对象元数据。永远是响应流的第一帧。
    /// </summary>
    public static void EncodeGetMetaFrame(IBufferWriter<byte> writer, uint streamId, SndbObjectInfo info)
    {
        long payloadLength = 1
            + SpanWriter.MeasureVarString(info.VersionId)
            + SpanWriter.MeasureVarString(info.ContentType)
            + SpanWriter.MeasureVarUInt64((ulong)info.SizeBytes)
            + SpanWriter.MeasureVarString(info.ETag)
            + SpanWriter.MeasureVarString(info.Sha256)
            + MeasureMap(info.Metadata)
            + MeasureMap(info.Tags);
        ValidateFramePayloadLength(payloadLength);

        var header = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            (byte)FrameService.Object, (byte)ObjectFrameOp.Get, (byte)FrameFlags.Response, streamId);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + (int)payloadLength);
        header.Write(span);
        var w = new SpanWriter(span.Slice(FrameHeader.Size, (int)payloadLength));
        w.WriteByte((byte)ObjectChunkKind.Meta);
        w.WriteVarString(info.VersionId);
        w.WriteVarString(info.ContentType);
        w.WriteVarUInt64((ulong)info.SizeBytes);
        w.WriteVarString(info.ETag);
        w.WriteVarString(info.Sha256);
        WriteMap(ref w, info.Metadata);
        WriteMap(ref w, info.Tags);
        writer.Advance(FrameHeader.Size + (int)payloadLength);
    }

    /// <summary>
    /// 解码 get meta 响应帧体。
    /// </summary>
    public static ObjectGetFrameMeta DecodeGetMetaFrame(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        RequireChunkKind(ref reader, ObjectChunkKind.Meta);
        string versionId = ReadBoundedString(ref reader, MaxNameBytes, "versionId");
        string contentType = ReadBoundedString(ref reader, MaxNameBytes, "contentType");
        ulong sizeBytes = reader.ReadVarUInt64();
        if (sizeBytes > long.MaxValue)
            throw new FrameFormatException($"对象大小 {sizeBytes} 超出 long 范围。");
        string etag = ReadBoundedString(ref reader, MaxNameBytes, "etag");
        string sha256 = ReadBoundedString(ref reader, MaxNameBytes, "sha256");
        IReadOnlyDictionary<string, string> metadata = ReadMap(ref reader, "metadata");
        IReadOnlyDictionary<string, string> tags = ReadMap(ref reader, "tags");
        RequireEnd(ref reader, "get meta 响应");
        return new ObjectGetFrameMeta(versionId, contentType, (long)sizeBytes, etag, sha256, metadata, tags);
    }

    /// <summary>
    /// 编码 get data 响应帧（chunkKind=2）：一段原始内容字节（帧长定界，无内嵌长度前缀）。
    /// </summary>
    public static void EncodeGetDataFrame(IBufferWriter<byte> writer, uint streamId, ReadOnlySpan<byte> chunk)
    {
        long payloadLength = 1L + chunk.Length;
        ValidateFramePayloadLength(payloadLength);

        var header = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            (byte)FrameService.Object, (byte)ObjectFrameOp.Get, (byte)FrameFlags.Response, streamId);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + 1);
        header.Write(span);
        span[FrameHeader.Size] = (byte)ObjectChunkKind.Data;
        writer.Advance(FrameHeader.Size + 1);
        if (!chunk.IsEmpty)
            writer.Write(chunk);
    }

    /// <summary>
    /// 解码 get data 响应帧体，返回内容分块的零拷贝视图（chunkKind 字节之后的全部字节）。
    /// </summary>
    public static ReadOnlyMemory<byte> DecodeGetDataFrame(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
            throw new FrameFormatException("object 响应帧体为空。");
        if (payload.Span[0] != (byte)ObjectChunkKind.Data)
            throw new FrameFormatException($"期望块类型 {(byte)ObjectChunkKind.Data}（Data），实际 {payload.Span[0]}。");
        return payload[1..];
    }

    /// <summary>
    /// 编码 get end 响应帧（chunkKind=3）：内容总字节数。永远是响应流的最后一帧。
    /// </summary>
    public static void EncodeGetEndFrame(IBufferWriter<byte> writer, uint streamId, long totalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);
        int payloadLength = 1 + SpanWriter.MeasureVarUInt64((ulong)totalBytes);
        var header = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            (byte)FrameService.Object, (byte)ObjectFrameOp.Get, (byte)FrameFlags.Response, streamId);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + payloadLength);
        header.Write(span);
        var w = new SpanWriter(span.Slice(FrameHeader.Size, payloadLength));
        w.WriteByte((byte)ObjectChunkKind.End);
        w.WriteVarUInt64((ulong)totalBytes);
        writer.Advance(FrameHeader.Size + payloadLength);
    }

    /// <summary>
    /// 解码 get end 响应帧体，返回内容总字节数。
    /// </summary>
    public static long DecodeGetEndFrame(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        RequireChunkKind(ref reader, ObjectChunkKind.End);
        ulong totalBytes = reader.ReadVarUInt64();
        if (totalBytes > long.MaxValue)
            throw new FrameFormatException($"end 帧总字节数 {totalBytes} 超出 long 范围。");
        RequireEnd(ref reader, "get end 响应");
        return (long)totalBytes;
    }

    /// <summary>
    /// 读取 get 响应帧体首字节的块类型（meta/data/end），供客户端分发到对应解码器。
    /// </summary>
    public static ObjectChunkKind PeekChunkKind(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
            throw new FrameFormatException("object 响应帧体为空。");
        byte kind = payload[0];
        if (kind is < (byte)ObjectChunkKind.Meta or > (byte)ObjectChunkKind.End)
            throw new FrameFormatException($"object 响应帧块类型 {kind} 非法（1=meta / 2=data / 3=end）。");
        return (ObjectChunkKind)kind;
    }

    // ────────────────────────────── put (op=2) ──────────────────────────────

    /// <summary>
    /// 编码 put 请求帧：db, bucket, key, contentType（空串 = 服务端默认）, metadata, tags, 内容原始字节。
    /// </summary>
    public static void EncodePutRequest(
        IBufferWriter<byte> writer,
        uint streamId,
        string db,
        string bucket,
        string key,
        ReadOnlySpan<byte> content,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyDictionary<string, string>? tags = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucket);
        ArgumentException.ThrowIfNullOrEmpty(key);
        int metaLength =
            SpanWriter.MeasureVarString(db) +
            SpanWriter.MeasureVarString(bucket) +
            SpanWriter.MeasureVarString(key) +
            SpanWriter.MeasureVarString(contentType ?? string.Empty) +
            MeasureMap(metadata) +
            MeasureMap(tags) +
            SpanWriter.MeasureVarUInt32((uint)content.Length);
        long payloadLength = (long)metaLength + content.Length;
        ValidateFramePayloadLength(payloadLength);

        int contentLength = content.Length;
        var header = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            (byte)FrameService.Object, (byte)ObjectFrameOp.Put, (byte)FrameFlags.None, streamId);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + metaLength);
        header.Write(span);
        var w = new SpanWriter(span.Slice(FrameHeader.Size, metaLength));
        w.WriteVarString(db);
        w.WriteVarString(bucket);
        w.WriteVarString(key);
        w.WriteVarString(contentType ?? string.Empty);
        WriteMap(ref w, metadata);
        WriteMap(ref w, tags);
        w.WriteVarUInt32((uint)contentLength);
        writer.Advance(FrameHeader.Size + metaLength);
        if (!content.IsEmpty)
            writer.Write(content);
    }

    /// <summary>
    /// 解码 put 请求帧体。<see cref="ObjectPutFrameRequest.Content"/> 为输入缓冲的零拷贝视图。
    /// </summary>
    public static ObjectPutFrameRequest DecodePutRequest(ReadOnlyMemory<byte> payload)
    {
        var reader = new SpanReader(payload.Span);
        string db = ReadBoundedString(ref reader, MaxNameBytes, "db");
        string bucket = ReadBoundedString(ref reader, MaxNameBytes, "bucket");
        string key = ReadBoundedString(ref reader, MaxKeyBytes, "key");
        string contentType = ReadBoundedString(ref reader, MaxNameBytes, "contentType");
        IReadOnlyDictionary<string, string> metadata = ReadMap(ref reader, "metadata");
        IReadOnlyDictionary<string, string> tags = ReadMap(ref reader, "tags");
        uint length = reader.ReadVarUInt32();
        if (length > (uint)reader.Remaining)
            throw new FrameFormatException($"content 长度 {length} 超出帧体剩余长度。");
        ReadOnlyMemory<byte> content = payload.Slice(reader.Position, (int)length);
        reader.Skip((int)length);
        RequireEnd(ref reader, "put 请求");
        return new ObjectPutFrameRequest(
            db, bucket, key, content,
            contentType.Length == 0 ? null : contentType,
            metadata, tags);
    }

    /// <summary>
    /// 编码 put 响应帧：versionId, sizeBytes, etag, sha256。
    /// </summary>
    public static void EncodePutResponse(IBufferWriter<byte> writer, uint streamId, SndbObjectInfo info)
    {
        int payloadLength =
            SpanWriter.MeasureVarString(info.VersionId) +
            SpanWriter.MeasureVarUInt64((ulong)info.SizeBytes) +
            SpanWriter.MeasureVarString(info.ETag) +
            SpanWriter.MeasureVarString(info.Sha256);

        var header = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            (byte)FrameService.Object, (byte)ObjectFrameOp.Put, (byte)FrameFlags.Response, streamId);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + payloadLength);
        header.Write(span);
        var w = new SpanWriter(span.Slice(FrameHeader.Size, payloadLength));
        w.WriteVarString(info.VersionId);
        w.WriteVarUInt64((ulong)info.SizeBytes);
        w.WriteVarString(info.ETag);
        w.WriteVarString(info.Sha256);
        writer.Advance(FrameHeader.Size + payloadLength);
    }

    /// <summary>
    /// 解码 put 响应帧体。
    /// </summary>
    public static ObjectPutFrameResult DecodePutResponse(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        string versionId = ReadBoundedString(ref reader, MaxNameBytes, "versionId");
        ulong sizeBytes = reader.ReadVarUInt64();
        if (sizeBytes > long.MaxValue)
            throw new FrameFormatException($"对象大小 {sizeBytes} 超出 long 范围。");
        string etag = ReadBoundedString(ref reader, MaxNameBytes, "etag");
        string sha256 = ReadBoundedString(ref reader, MaxNameBytes, "sha256");
        RequireEnd(ref reader, "put 响应");
        return new ObjectPutFrameResult(versionId, (long)sizeBytes, etag, sha256);
    }

    // ────────────────────────────── 内部辅助 ──────────────────────────────

    private static void ValidateFramePayloadLength(long payloadLength)
    {
        if (payloadLength > FrameHeader.MaxFramePayloadBytes)
            throw new ArgumentException($"帧 payload 长度 {payloadLength} 超过上限 {FrameHeader.MaxFramePayloadBytes}。");
    }

    private static int MeasureMap(IReadOnlyDictionary<string, string>? map)
    {
        if (map is null || map.Count == 0)
            return SpanWriter.MeasureVarUInt32(0);

        if (map.Count > MaxMapCount)
            throw new ArgumentException($"字典条目数 {map.Count} 超过上限 {MaxMapCount}。");
        int total = SpanWriter.MeasureVarUInt32((uint)map.Count);
        foreach (KeyValuePair<string, string> pair in map)
            total += SpanWriter.MeasureVarString(pair.Key) + SpanWriter.MeasureVarString(pair.Value);
        return total;
    }

    private static void WriteMap(ref SpanWriter writer, IReadOnlyDictionary<string, string>? map)
    {
        if (map is null || map.Count == 0)
        {
            writer.WriteVarUInt32(0);
            return;
        }

        writer.WriteVarUInt32((uint)map.Count);
        foreach (KeyValuePair<string, string> pair in map)
        {
            writer.WriteVarString(pair.Key);
            writer.WriteVarString(pair.Value);
        }
    }

    private static IReadOnlyDictionary<string, string> ReadMap(ref SpanReader reader, string field)
    {
        uint count = reader.ReadVarUInt32();
        if (count == 0)
            return EmptyMap;
        if (count > MaxMapCount)
            throw new FrameFormatException($"{field} 条目数 {count} 超过上限 {MaxMapCount}。");

        int startPosition = reader.Position;
        var map = new Dictionary<string, string>((int)count, StringComparer.Ordinal);
        for (uint i = 0; i < count; i++)
        {
            string key = ReadBoundedString(ref reader, MaxNameBytes, $"{field} key");
            string value = ReadBoundedString(ref reader, MaxMapBytes, $"{field} value");
            map[key] = value;
            if (reader.Position - startPosition > MaxMapBytes)
                throw new FrameFormatException($"{field} 总量超过上限 {MaxMapBytes} 字节。");
        }

        return map;
    }

    private static void RequireChunkKind(ref SpanReader reader, ObjectChunkKind expected)
    {
        byte kind = reader.ReadByte();
        if (kind != (byte)expected)
            throw new FrameFormatException($"期望块类型 {(byte)expected}（{expected}），实际 {kind}。");
    }

    private static string ReadBoundedString(ref SpanReader reader, int maxBytes, string field)
    {
        uint length = reader.ReadVarUInt32();
        if (length > (uint)maxBytes)
            throw new FrameFormatException($"{field} 长度 {length} 超过上限 {maxBytes} 字节。");
        if (length == 0)
            return string.Empty;
        if (length > (uint)reader.Remaining)
            throw new FrameFormatException($"{field} 长度 {length} 超出帧体剩余长度。");
        return Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    private static void RequireEnd(ref SpanReader reader, string frame)
    {
        if (reader.Remaining != 0)
            throw new FrameFormatException($"{frame}帧体尾部有多余字节。");
    }
}

/// <summary>
/// object get 响应帧的块类型（帧体首字节）。同一 streamId 的响应流 = meta → data × N → end。
/// </summary>
public enum ObjectChunkKind : byte
{
    /// <summary>对象元数据（首帧）。</summary>
    Meta = 1,

    /// <summary>内容分块（原始字节，帧长定界）。</summary>
    Data = 2,

    /// <summary>结束统计（末帧）：内容总字节数。</summary>
    End = 3,
}

/// <summary>get 请求帧解码结果。</summary>
/// <param name="Db">数据库名。</param>
/// <param name="Bucket">bucket 名。</param>
/// <param name="Key">对象 key。</param>
/// <param name="VersionId">目标版本；为 null 表示最新版本。</param>
public readonly record struct ObjectGetFrameRequest(
    string Db,
    string Bucket,
    string Key,
    string? VersionId);

/// <summary>get meta 响应帧解码结果。</summary>
/// <param name="VersionId">对象版本。</param>
/// <param name="ContentType">内容类型。</param>
/// <param name="SizeBytes">内容总字节数。</param>
/// <param name="ETag">内容 ETag。</param>
/// <param name="Sha256">内容 SHA-256（hex）。</param>
/// <param name="Metadata">用户 metadata。</param>
/// <param name="Tags">对象 tags。</param>
public sealed record ObjectGetFrameMeta(
    string VersionId,
    string ContentType,
    long SizeBytes,
    string ETag,
    string Sha256,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>put 请求帧解码结果。<see cref="Content"/> 为输入缓冲的零拷贝视图。</summary>
/// <param name="Db">数据库名。</param>
/// <param name="Bucket">bucket 名。</param>
/// <param name="Key">对象 key。</param>
/// <param name="Content">内容字节视图。</param>
/// <param name="ContentType">内容类型；为 null 表示服务端默认。</param>
/// <param name="Metadata">用户 metadata（可能为空字典）。</param>
/// <param name="Tags">对象 tags（可能为空字典）。</param>
public readonly record struct ObjectPutFrameRequest(
    string Db,
    string Bucket,
    string Key,
    ReadOnlyMemory<byte> Content,
    string? ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>put 响应帧解码结果。</summary>
/// <param name="VersionId">新写入的对象版本。</param>
/// <param name="SizeBytes">内容总字节数。</param>
/// <param name="ETag">内容 ETag。</param>
/// <param name="Sha256">内容 SHA-256（hex）。</param>
public sealed record ObjectPutFrameResult(
    string VersionId,
    long SizeBytes,
    string ETag,
    string Sha256);
