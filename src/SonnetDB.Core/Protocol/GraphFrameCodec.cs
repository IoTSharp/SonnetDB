using System.Buffers;
using SonnetDB.Graphs;
using SonnetDB.Graphs.Storage;
using SonnetDB.IO;

namespace SonnetDB.Protocol;

/// <summary>
/// 原生属性图 expand Frame 编解码器。响应按同一 stream ID 输出 meta、零到多个 row、end。
/// </summary>
public static class GraphFrameCodec
{
    /// <summary>数据库名或图名称的最大 UTF-8 字节数。</summary>
    public const int MaxNameBytes = 512;

    /// <summary>单次 expand 的最大结果数。</summary>
    public const int MaxResults = 10_000;

    /// <summary>单次底层读取的最大页大小。</summary>
    public const int MaxPageSize = 1_000;

    /// <summary>编码 expand 请求。</summary>
    /// <param name="writer">目标缓冲区。</param>
    /// <param name="streamId">调用方关联标识。</param>
    /// <param name="database">数据库名。</param>
    /// <param name="graph">图名称。</param>
    /// <param name="vertexId">扩展锚点。</param>
    /// <param name="direction">扩展方向。</param>
    /// <param name="edgeLabelId">可选边标签。</param>
    /// <param name="pageSize">底层读取页大小。</param>
    /// <param name="maxResults">结果上限。</param>
    public static void EncodeExpandRequest(
        IBufferWriter<byte> writer,
        uint streamId,
        string database,
        string graph,
        GraphElementId vertexId,
        GraphDirection direction = GraphDirection.Outgoing,
        LabelId? edgeLabelId = null,
        int pageSize = 256,
        int maxResults = MaxResults)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(graph);
        ValidateOptions(direction, edgeLabelId, pageSize, maxResults);
        int payloadLength = checked(
            SpanWriter.MeasureVarString(database)
            + SpanWriter.MeasureVarString(graph)
            + sizeof(long)
            + sizeof(byte)
            + sizeof(int)
            + sizeof(int)
            + sizeof(int));
        var header = new FrameHeader(
            (uint)payloadLength,
            FrameHeader.CurrentVersion,
            (byte)FrameService.Graph,
            (byte)GraphFrameOp.Expand,
            (byte)FrameFlags.None,
            streamId);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + payloadLength);
        header.Write(span);
        var body = new SpanWriter(span.Slice(FrameHeader.Size, payloadLength));
        body.WriteVarString(database);
        body.WriteVarString(graph);
        body.WriteInt64(vertexId.Value);
        body.WriteByte((byte)direction);
        body.WriteInt32(edgeLabelId?.Value ?? 0);
        body.WriteInt32(pageSize);
        body.WriteInt32(maxResults);
        writer.Advance(FrameHeader.Size + payloadLength);
    }

    /// <summary>解码 expand 请求帧体。</summary>
    /// <param name="payload">请求帧体。</param>
    /// <returns>已验证并持有自身字符串的请求。</returns>
    public static GraphExpandFrameRequest DecodeExpandRequest(ReadOnlySpan<byte> payload)
    {
        try
        {
            return DecodeExpandRequestCore(payload);
        }
        catch (FrameFormatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new FrameFormatException("graph expand 请求帧体无效：" + exception.Message);
        }
    }

    private static GraphExpandFrameRequest DecodeExpandRequestCore(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        string database = ReadBoundedName(ref reader, "database");
        string graph = ReadBoundedName(ref reader, "graph");
        long vertexId = reader.ReadInt64();
        byte directionValue = reader.ReadByte();
        int labelValue = reader.ReadInt32();
        int pageSize = reader.ReadInt32();
        int maxResults = reader.ReadInt32();
        if (reader.Remaining != 0)
            throw new FrameFormatException("graph expand 请求帧体尾部有多余字节。");
        if (database.Length == 0 || graph.Length == 0 || vertexId <= 0 || labelValue < 0)
            throw new FrameFormatException("graph expand 的 database、graph、vertexId 或 edgeLabelId 无效。");
        GraphDirection direction = (GraphDirection)directionValue;
        LabelId? edgeLabelId = labelValue == 0 ? null : new LabelId(labelValue);
        try
        {
            ValidateOptions(direction, edgeLabelId, pageSize, maxResults);
        }
        catch (ArgumentException exception)
        {
            throw new FrameFormatException(exception.Message);
        }
        return new GraphExpandFrameRequest(
            database,
            graph,
            new GraphElementId(vertexId),
            direction,
            edgeLabelId,
            pageSize,
            maxResults);
    }

    /// <summary>编码 expand meta 响应帧。</summary>
    /// <param name="writer">目标缓冲区。</param>
    /// <param name="streamId">调用方关联标识。</param>
    /// <param name="snapshotSequence">稳定读快照序列号。</param>
    public static void EncodeExpandMetaFrame(
        IBufferWriter<byte> writer,
        uint streamId,
        long snapshotSequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotSequence);
        WriteScalarFrame(writer, streamId, GraphFrameChunkKind.Meta, snapshotSequence);
    }

    /// <summary>解码 expand meta 响应帧。</summary>
    /// <param name="payload">响应帧体。</param>
    /// <returns>稳定读快照序列号。</returns>
    public static long DecodeExpandMetaFrame(ReadOnlySpan<byte> payload)
        => ReadScalarFrame(payload, GraphFrameChunkKind.Meta);

    /// <summary>编码单条 expand row 响应帧。</summary>
    /// <param name="writer">目标缓冲区。</param>
    /// <param name="streamId">调用方关联标识。</param>
    /// <param name="expansion">邻接命中。</param>
    public static void EncodeExpandRowFrame(
        IBufferWriter<byte> writer,
        uint streamId,
        GraphExpansion expansion)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(expansion);
        GraphEdge edge = expansion.Edge;
        byte[] edgeRecord = GraphElementRecordCodec.EncodeEdge(new GraphEdgeRecord(
            edge.Id,
            edge.ElementVersion,
            edge.SourceId,
            edge.TargetId,
            edge.LabelId,
            edge.Properties));
        int payloadLength = checked(
            sizeof(byte)
            + sizeof(long)
            + sizeof(long)
            + sizeof(byte)
            + SpanWriter.MeasureVarUInt32((uint)edgeRecord.Length)
            + edgeRecord.Length);
        if ((uint)payloadLength > FrameHeader.MaxFramePayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(expansion), "Graph expand row 超过单帧 payload 上限。");
        var header = ResponseHeader(payloadLength, streamId);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + payloadLength);
        header.Write(span);
        var body = new SpanWriter(span.Slice(FrameHeader.Size, payloadLength));
        body.WriteByte((byte)GraphFrameChunkKind.Row);
        body.WriteInt64(expansion.AnchorId.Value);
        body.WriteInt64(expansion.NeighborId.Value);
        body.WriteByte((byte)expansion.Direction);
        body.WriteVarUInt32((uint)edgeRecord.Length);
        body.WriteBytes(edgeRecord);
        writer.Advance(FrameHeader.Size + payloadLength);
    }

    /// <summary>解码单条 expand row 响应帧。</summary>
    /// <param name="payload">响应帧体。</param>
    /// <returns>不可变邻接命中。</returns>
    public static GraphExpansion DecodeExpandRowFrame(ReadOnlySpan<byte> payload)
    {
        try
        {
            return DecodeExpandRowFrameCore(payload);
        }
        catch (FrameFormatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new FrameFormatException("graph expand row 帧体无效：" + exception.Message);
        }
    }

    private static GraphExpansion DecodeExpandRowFrameCore(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        RequireChunk(ref reader, GraphFrameChunkKind.Row);
        var anchorId = new GraphElementId(reader.ReadInt64());
        var neighborId = new GraphElementId(reader.ReadInt64());
        GraphDirection direction = (GraphDirection)reader.ReadByte();
        uint recordLength = reader.ReadVarUInt32();
        if (recordLength > (uint)reader.Remaining)
            throw new FrameFormatException("graph expand row 的 edge record 长度超出帧体。");
        GraphEdgeRecord record;
        try
        {
            record = GraphElementRecordCodec.DecodeEdge(reader.ReadBytes((int)recordLength));
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            throw new FrameFormatException("graph expand row 的 edge record 无效：" + exception.Message);
        }
        if (reader.Remaining != 0)
            throw new FrameFormatException("graph expand row 帧体尾部有多余字节。");
        try
        {
            return new GraphExpansion(
                anchorId,
                neighborId,
                direction,
                new GraphEdge(
                    record.Id,
                    record.ElementVersion,
                    record.SourceId,
                    record.TargetId,
                    record.LabelId,
                    record.Properties));
        }
        catch (ArgumentException exception)
        {
            throw new FrameFormatException(exception.Message);
        }
    }

    /// <summary>编码 expand end 响应帧。</summary>
    /// <param name="writer">目标缓冲区。</param>
    /// <param name="streamId">调用方关联标识。</param>
    /// <param name="rowCount">已返回行数。</param>
    public static void EncodeExpandEndFrame(
        IBufferWriter<byte> writer,
        uint streamId,
        long rowCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        WriteScalarFrame(writer, streamId, GraphFrameChunkKind.End, rowCount);
    }

    /// <summary>解码 expand end 响应帧。</summary>
    /// <param name="payload">响应帧体。</param>
    /// <returns>总行数。</returns>
    public static long DecodeExpandEndFrame(ReadOnlySpan<byte> payload)
        => ReadScalarFrame(payload, GraphFrameChunkKind.End);

    /// <summary>读取响应帧块类型。</summary>
    /// <param name="payload">响应帧体。</param>
    /// <returns>meta、row 或 end。</returns>
    public static GraphFrameChunkKind PeekChunkKind(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload[0] is < 1 or > 3)
            throw new FrameFormatException("graph expand 响应块类型无效。");
        return (GraphFrameChunkKind)payload[0];
    }

    private static void ValidateOptions(
        GraphDirection direction,
        LabelId? edgeLabelId,
        int pageSize,
        int maxResults)
    {
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (edgeLabelId is { Value: <= 0 })
            throw new ArgumentOutOfRangeException(nameof(edgeLabelId));
        if (pageSize is <= 0 or > MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (maxResults is <= 0 or > MaxResults)
            throw new ArgumentOutOfRangeException(nameof(maxResults));
    }

    private static string ReadBoundedName(ref SpanReader reader, string field)
    {
        uint length = reader.ReadVarUInt32();
        if (length > MaxNameBytes || length > (uint)reader.Remaining)
            throw new FrameFormatException($"graph {field} 长度无效。");
        return System.Text.Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    private static FrameHeader ResponseHeader(int payloadLength, uint streamId)
        => new(
            (uint)payloadLength,
            FrameHeader.CurrentVersion,
            (byte)FrameService.Graph,
            (byte)GraphFrameOp.Expand,
            (byte)FrameFlags.Response,
            streamId);

    private static void WriteScalarFrame(
        IBufferWriter<byte> writer,
        uint streamId,
        GraphFrameChunkKind kind,
        long value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        const int PayloadLength = sizeof(byte) + sizeof(long);
        FrameHeader header = ResponseHeader(PayloadLength, streamId);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + PayloadLength);
        header.Write(span);
        var body = new SpanWriter(span.Slice(FrameHeader.Size, PayloadLength));
        body.WriteByte((byte)kind);
        body.WriteInt64(value);
        writer.Advance(FrameHeader.Size + PayloadLength);
    }

    private static long ReadScalarFrame(ReadOnlySpan<byte> payload, GraphFrameChunkKind kind)
    {
        var reader = new SpanReader(payload);
        RequireChunk(ref reader, kind);
        long value = reader.ReadInt64();
        if (value < 0 || reader.Remaining != 0)
            throw new FrameFormatException($"graph {kind} 响应帧体无效。");
        return value;
    }

    private static void RequireChunk(ref SpanReader reader, GraphFrameChunkKind expected)
    {
        byte actual = reader.ReadByte();
        if (actual != (byte)expected)
            throw new FrameFormatException($"期望 graph {expected} 块，实际为 {actual}。");
    }
}

/// <summary>Graph Frame expand 请求。</summary>
/// <param name="Database">数据库名。</param>
/// <param name="Graph">图名称。</param>
/// <param name="VertexId">扩展锚点。</param>
/// <param name="Direction">扩展方向。</param>
/// <param name="EdgeLabelId">可选边标签。</param>
/// <param name="PageSize">底层读取页大小。</param>
/// <param name="MaxResults">结果上限。</param>
public sealed record GraphExpandFrameRequest(
    string Database,
    string Graph,
    GraphElementId VertexId,
    GraphDirection Direction,
    LabelId? EdgeLabelId,
    int PageSize,
    int MaxResults);

/// <summary>Graph Frame expand 响应块类型。</summary>
public enum GraphFrameChunkKind : byte
{
    /// <summary>稳定快照元数据。</summary>
    Meta = 1,

    /// <summary>单条邻接命中。</summary>
    Row = 2,

    /// <summary>结果流结束与总行数。</summary>
    End = 3,
}
