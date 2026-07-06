using System.Buffers;
using System.Text;
using SonnetDB.Documents;
using SonnetDB.IO;

namespace SonnetDB.Protocol;

/// <summary>
/// doc service（<see cref="FrameService.Doc"/>）find / insert 两个 opcode 的帧体编解码（M28 P5b #240）。
/// JSON 文档文本以原始 UTF-8 字节直传——零 JSON 信封转义、零嵌套序列化；
/// find 只承载 ID 点查 / ID 列表 / 扫描分页（高吞吐数据面），
/// filter / projection / sort / aggregate 等复杂查询不进帧（走 REST 文档端点或 SQL）。
/// 解码结果中的文档文本已物化为 string，不依赖输入缓冲存活期。
/// </summary>
public static class DocFrameCodec
{
    /// <summary>名字（db / collection / 文档 id）UTF-8 字节数上限。</summary>
    public const int MaxNameBytes = 512;

    /// <summary>单帧文档条数上限（find ids / insert documents）。</summary>
    public const int MaxDocumentCount = 4096;

    /// <summary>单条文档 JSON UTF-8 字节数上限（16 MiB，对齐 KV value 默认上限）。</summary>
    public const int MaxDocumentBytes = 16 * 1024 * 1024;

    // ────────────────────────────── find (op=1) ──────────────────────────────

    /// <summary>
    /// 编码 find 请求帧：db, collection, ids（空 = 扫描分页）, afterId（仅扫描：空串 = 从头）,
    /// limit（0 = 服务端默认）。ids 非空时 afterId / limit 被忽略。
    /// </summary>
    public static void EncodeFindRequest(
        IBufferWriter<byte> writer,
        uint streamId,
        string db,
        string collection,
        IReadOnlyList<string>? ids = null,
        string? afterId = null,
        int limit = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(collection);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        int idCount = ids?.Count ?? 0;
        if (idCount > MaxDocumentCount)
            throw new ArgumentException($"ids 条数 {idCount} 超过上限 {MaxDocumentCount}。", nameof(ids));

        long payloadLength =
            SpanWriter.MeasureVarString(db) +
            SpanWriter.MeasureVarString(collection) +
            SpanWriter.MeasureVarUInt32((uint)idCount);
        if (ids is not null)
        {
            for (int i = 0; i < ids.Count; i++)
                payloadLength += SpanWriter.MeasureVarString(ids[i]);
        }
        payloadLength += SpanWriter.MeasureVarString(afterId ?? string.Empty)
            + SpanWriter.MeasureVarUInt32((uint)limit);
        ValidateFramePayloadLength(payloadLength);

        var header = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            (byte)FrameService.Doc, (byte)DocFrameOp.Find, (byte)FrameFlags.None, streamId);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + (int)payloadLength);
        header.Write(span);
        var w = new SpanWriter(span.Slice(FrameHeader.Size, (int)payloadLength));
        w.WriteVarString(db);
        w.WriteVarString(collection);
        w.WriteVarUInt32((uint)idCount);
        if (ids is not null)
        {
            for (int i = 0; i < ids.Count; i++)
                w.WriteVarString(ids[i]);
        }
        w.WriteVarString(afterId ?? string.Empty);
        w.WriteVarUInt32((uint)limit);
        writer.Advance(FrameHeader.Size + (int)payloadLength);
    }

    /// <summary>
    /// 解码 find 请求帧体。
    /// </summary>
    public static DocFindFrameRequest DecodeFindRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        string db = ReadBoundedString(ref reader, MaxNameBytes, "db");
        string collection = ReadBoundedString(ref reader, MaxNameBytes, "collection");
        uint idCount = reader.ReadVarUInt32();
        if (idCount > MaxDocumentCount)
            throw new FrameFormatException($"ids 条数 {idCount} 超过上限 {MaxDocumentCount}。");
        if (idCount > (uint)reader.Remaining)
            throw new FrameFormatException($"ids 条数 {idCount} 超出帧体剩余长度。");

        string[] ids = idCount == 0 ? [] : new string[idCount];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = ReadBoundedString(ref reader, MaxNameBytes, "文档 id");
            if (ids[i].Length == 0)
                throw new FrameFormatException("文档 id 不可为空。");
        }

        string afterId = ReadBoundedString(ref reader, MaxNameBytes, "afterId");
        uint limit = reader.ReadVarUInt32();
        if (limit > int.MaxValue)
            throw new FrameFormatException($"find limit {limit} 非法。");
        RequireEnd(ref reader, "find 请求");
        return new DocFindFrameRequest(db, collection, ids, afterId.Length == 0 ? null : afterId, (int)limit);
    }

    /// <summary>
    /// 编码 find 响应帧：count + 每条 (id, version, JSON 原始 UTF-8 字节)。
    /// </summary>
    public static void EncodeFindResponse(IBufferWriter<byte> writer, uint streamId, IReadOnlyList<DocumentRow> rows)
    {
        long payloadLength = SpanWriter.MeasureVarUInt32((uint)rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            DocumentRow row = rows[i];
            payloadLength += SpanWriter.MeasureVarString(row.Id)
                + SpanWriter.MeasureVarUInt64((ulong)row.Version)
                + SpanWriter.MeasureVarString(row.Json);
        }

        ValidateFramePayloadLength(payloadLength);

        var countHeader = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            (byte)FrameService.Doc, (byte)DocFrameOp.Find, (byte)FrameFlags.Response, streamId);
        int countLength = SpanWriter.MeasureVarUInt32((uint)rows.Count);
        Span<byte> head = writer.GetSpan(FrameHeader.Size + countLength);
        countHeader.Write(head);
        var counter = new SpanWriter(head.Slice(FrameHeader.Size, countLength));
        counter.WriteVarUInt32((uint)rows.Count);
        writer.Advance(FrameHeader.Size + countLength);

        for (int i = 0; i < rows.Count; i++)
        {
            DocumentRow row = rows[i];
            int rowLength = SpanWriter.MeasureVarString(row.Id)
                + SpanWriter.MeasureVarUInt64((ulong)row.Version)
                + SpanWriter.MeasureVarString(row.Json);
            Span<byte> span = writer.GetSpan(rowLength);
            var w = new SpanWriter(span[..rowLength]);
            w.WriteVarString(row.Id);
            w.WriteVarUInt64((ulong)row.Version);
            w.WriteVarString(row.Json);
            writer.Advance(rowLength);
        }
    }

    /// <summary>
    /// 解码 find 响应帧体。文档已物化。
    /// </summary>
    public static DocumentRow[] DecodeFindResponse(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        uint count = reader.ReadVarUInt32();
        if (count > MaxDocumentCount)
            throw new FrameFormatException($"find 响应条数 {count} 超过上限 {MaxDocumentCount}。");
        if (count > (uint)payload.Length)
            throw new FrameFormatException($"find 响应条数 {count} 超出帧体长度。");

        var rows = new DocumentRow[count];
        for (int i = 0; i < rows.Length; i++)
        {
            string id = ReadBoundedString(ref reader, MaxNameBytes, "文档 id");
            long version = ReadVersion(ref reader);
            string json = ReadBoundedString(ref reader, MaxDocumentBytes, "文档 JSON");
            rows[i] = new DocumentRow(id, json, version);
        }

        RequireEnd(ref reader, "find 响应");
        return rows;
    }

    // ────────────────────────────── insert (op=2) ──────────────────────────────

    /// <summary>
    /// 编码 insert 请求帧：db, collection, ordered u8, count + 每条 (id, JSON 原始 UTF-8 字节)。
    /// </summary>
    public static void EncodeInsertRequest(
        IBufferWriter<byte> writer,
        uint streamId,
        string db,
        string collection,
        IReadOnlyList<DocumentWriteRequest> documents,
        bool ordered = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(collection);
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count is 0 or > MaxDocumentCount)
            throw new ArgumentException($"insert 文档条数 {documents.Count} 非法（1 ~ {MaxDocumentCount}）。", nameof(documents));

        long payloadLength =
            SpanWriter.MeasureVarString(db) +
            SpanWriter.MeasureVarString(collection) +
            1 +
            SpanWriter.MeasureVarUInt32((uint)documents.Count);
        for (int i = 0; i < documents.Count; i++)
            payloadLength += SpanWriter.MeasureVarString(documents[i].Id) + SpanWriter.MeasureVarString(documents[i].Json);
        ValidateFramePayloadLength(payloadLength);

        var header = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            (byte)FrameService.Doc, (byte)DocFrameOp.Insert, (byte)FrameFlags.None, streamId);
        int headLength =
            SpanWriter.MeasureVarString(db) +
            SpanWriter.MeasureVarString(collection) +
            1 +
            SpanWriter.MeasureVarUInt32((uint)documents.Count);
        Span<byte> head = writer.GetSpan(FrameHeader.Size + headLength);
        header.Write(head);
        var w = new SpanWriter(head.Slice(FrameHeader.Size, headLength));
        w.WriteVarString(db);
        w.WriteVarString(collection);
        w.WriteByte(ordered ? (byte)1 : (byte)0);
        w.WriteVarUInt32((uint)documents.Count);
        writer.Advance(FrameHeader.Size + headLength);

        for (int i = 0; i < documents.Count; i++)
        {
            DocumentWriteRequest document = documents[i];
            int rowLength = SpanWriter.MeasureVarString(document.Id) + SpanWriter.MeasureVarString(document.Json);
            Span<byte> span = writer.GetSpan(rowLength);
            var rowWriter = new SpanWriter(span[..rowLength]);
            rowWriter.WriteVarString(document.Id);
            rowWriter.WriteVarString(document.Json);
            writer.Advance(rowLength);
        }
    }

    /// <summary>
    /// 解码 insert 请求帧体。文档文本已物化（引擎写入需要 string）。
    /// </summary>
    public static DocInsertFrameRequest DecodeInsertRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        string db = ReadBoundedString(ref reader, MaxNameBytes, "db");
        string collection = ReadBoundedString(ref reader, MaxNameBytes, "collection");
        byte ordered = reader.ReadByte();
        if (ordered > 1)
            throw new FrameFormatException($"insert ordered 标记 {ordered} 非法（0/1）。");
        uint count = reader.ReadVarUInt32();
        if (count is 0 or > MaxDocumentCount)
            throw new FrameFormatException($"insert 文档条数 {count} 非法（1 ~ {MaxDocumentCount}）。");
        if (count > (uint)reader.Remaining)
            throw new FrameFormatException($"insert 文档条数 {count} 超出帧体剩余长度。");

        var documents = new DocumentWriteRequest[count];
        for (int i = 0; i < documents.Length; i++)
        {
            string id = ReadBoundedString(ref reader, MaxNameBytes, "文档 id");
            string json = ReadBoundedString(ref reader, MaxDocumentBytes, "文档 JSON");
            documents[i] = new DocumentWriteRequest(id, json);
        }

        RequireEnd(ref reader, "insert 请求");
        return new DocInsertFrameRequest(db, collection, documents, ordered == 1);
    }

    /// <summary>
    /// 编码 insert 响应帧：inserted / matched / modified / deleted + 每条错误 (index, id, code, message, severity)。
    /// </summary>
    public static void EncodeInsertResponse(IBufferWriter<byte> writer, uint streamId, DocumentWriteResult result)
    {
        long payloadLength =
            SpanWriter.MeasureVarUInt32((uint)result.Inserted) +
            SpanWriter.MeasureVarUInt32((uint)result.Matched) +
            SpanWriter.MeasureVarUInt32((uint)result.Modified) +
            SpanWriter.MeasureVarUInt32((uint)result.Deleted) +
            SpanWriter.MeasureVarUInt32((uint)result.Errors.Count);
        for (int i = 0; i < result.Errors.Count; i++)
        {
            DocumentWriteError error = result.Errors[i];
            payloadLength += SpanWriter.MeasureVarUInt32((uint)error.Index)
                + SpanWriter.MeasureVarString(error.Id ?? string.Empty)
                + SpanWriter.MeasureVarString(error.Code)
                + SpanWriter.MeasureVarString(error.Message)
                + SpanWriter.MeasureVarString(error.Severity);
        }

        ValidateFramePayloadLength(payloadLength);

        var header = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            (byte)FrameService.Doc, (byte)DocFrameOp.Insert, (byte)FrameFlags.Response, streamId);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + (int)payloadLength);
        header.Write(span);
        var w = new SpanWriter(span.Slice(FrameHeader.Size, (int)payloadLength));
        w.WriteVarUInt32((uint)result.Inserted);
        w.WriteVarUInt32((uint)result.Matched);
        w.WriteVarUInt32((uint)result.Modified);
        w.WriteVarUInt32((uint)result.Deleted);
        w.WriteVarUInt32((uint)result.Errors.Count);
        for (int i = 0; i < result.Errors.Count; i++)
        {
            DocumentWriteError error = result.Errors[i];
            w.WriteVarUInt32((uint)error.Index);
            w.WriteVarString(error.Id ?? string.Empty);
            w.WriteVarString(error.Code);
            w.WriteVarString(error.Message);
            w.WriteVarString(error.Severity);
        }
        writer.Advance(FrameHeader.Size + (int)payloadLength);
    }

    /// <summary>
    /// 解码 insert 响应帧体。
    /// </summary>
    public static DocumentWriteResult DecodeInsertResponse(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        uint inserted = ReadCount(ref reader, "inserted");
        uint matched = ReadCount(ref reader, "matched");
        uint modified = ReadCount(ref reader, "modified");
        uint deleted = ReadCount(ref reader, "deleted");
        uint errorCount = reader.ReadVarUInt32();
        if (errorCount > MaxDocumentCount)
            throw new FrameFormatException($"insert 响应错误条数 {errorCount} 超过上限 {MaxDocumentCount}。");
        if (errorCount > (uint)payload.Length)
            throw new FrameFormatException($"insert 响应错误条数 {errorCount} 超出帧体长度。");

        DocumentWriteError[] errors = errorCount == 0 ? [] : new DocumentWriteError[errorCount];
        for (int i = 0; i < errors.Length; i++)
        {
            uint index = ReadCount(ref reader, "错误 index");
            string id = ReadBoundedString(ref reader, MaxNameBytes, "错误 id");
            string code = ReadBoundedString(ref reader, MaxNameBytes, "错误 code");
            string message = ReadBoundedString(ref reader, MaxDocumentBytes, "错误 message");
            string severity = ReadBoundedString(ref reader, MaxNameBytes, "错误 severity");
            errors[i] = new DocumentWriteError((int)index, id.Length == 0 ? null : id, code, message, severity);
        }

        RequireEnd(ref reader, "insert 响应");
        return new DocumentWriteResult((int)inserted, (int)matched, (int)modified, (int)deleted, errors);
    }

    // ────────────────────────────── 内部辅助 ──────────────────────────────

    private static void ValidateFramePayloadLength(long payloadLength)
    {
        if (payloadLength > FrameHeader.MaxFramePayloadBytes)
            throw new ArgumentException($"帧 payload 长度 {payloadLength} 超过上限 {FrameHeader.MaxFramePayloadBytes}。");
    }

    private static uint ReadCount(ref SpanReader reader, string field)
    {
        uint value = reader.ReadVarUInt32();
        if (value > int.MaxValue)
            throw new FrameFormatException($"{field} {value} 非法。");
        return value;
    }

    private static long ReadVersion(ref SpanReader reader)
    {
        ulong value = reader.ReadVarUInt64();
        if (value > long.MaxValue)
            throw new FrameFormatException($"version {value} 超出 long 范围。");
        return (long)value;
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

/// <summary>find 请求帧解码结果。</summary>
/// <param name="Db">数据库名。</param>
/// <param name="Collection">集合名。</param>
/// <param name="Ids">目标文档 ID 列表；为空表示扫描分页。</param>
/// <param name="AfterId">扫描起始 ID（其后开始）；为 null 表示从头。</param>
/// <param name="Limit">最大返回条数（0 = 服务端默认；仅扫描分页有效）。</param>
public sealed record DocFindFrameRequest(
    string Db,
    string Collection,
    IReadOnlyList<string> Ids,
    string? AfterId,
    int Limit);

/// <summary>insert 请求帧解码结果。</summary>
/// <param name="Db">数据库名。</param>
/// <param name="Collection">集合名。</param>
/// <param name="Documents">待插入文档（id + JSON 文本）。</param>
/// <param name="Ordered">为 true 时任一错误都会阻止本批提交。</param>
public sealed record DocInsertFrameRequest(
    string Db,
    string Collection,
    IReadOnlyList<DocumentWriteRequest> Documents,
    bool Ordered);
