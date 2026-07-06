using System.Buffers;
using SonnetDB.IO;
using SonnetDB.Query;

namespace SonnetDB.Protocol;

/// <summary>
/// vector service（<see cref="FrameService.Vector"/>）KNN 检索 opcode 的帧体编解码（M28 P5b #239）。
/// 请求帧的查询向量以紧凑二进制（f32 LE × 维度，<see cref="System.Runtime.InteropServices.MemoryMarshal"/>
/// 整段直传）承载，消灭 JSON 数字文本编解码税；可选 tag 等值过滤 + 闭区间时间窗，
/// 能力与 SQL <c>knn(measurement, column, query_vector, k[, metric]) [WHERE ...]</c> TVF 对齐。
/// 响应为**同 streamId 的帧序列** meta → rows × N → end，块布局与 sql service（#238）完全一致
/// （帧头 service/op 为 vector/search），客户端用 <see cref="SqlFrameCodec.PeekChunkKind"/> 与
/// <see cref="SqlFrameCodec.DecodeQueryMetaFrame"/> / <see cref="SqlFrameCodec.DecodeQueryRowsFrame"/> /
/// <see cref="SqlFrameCodec.DecodeQueryEndFrame"/> 同一套解码器解析。
/// 向量**插入**不设独立 opcode：tsdb write-columnar 帧（#237）的 Vector 列已是 f32 二进制直传通道。
/// </summary>
public static class VectorFrameCodec
{
    /// <summary>名字（db / measurement / 列名 / tag key / tag value）UTF-8 字节数上限。</summary>
    public const int MaxNameBytes = 512;

    /// <summary>单请求 tag 过滤条目数上限（对齐 <see cref="TsdbFrameCodec.MaxTagCount"/>）。</summary>
    public const int MaxTagFilterCount = 1024;

    // ────────────────────────────── search (op=1) 请求 ──────────────────────────────

    /// <summary>
    /// 编码 KNN 检索请求帧：db、measurement、向量列名、查询向量（f32 二进制）、k、度量、
    /// 可选 tag 等值过滤与时间窗（省略 = 全时间轴）。
    /// </summary>
    public static void EncodeSearchRequest(
        IBufferWriter<byte> writer,
        uint streamId,
        string db,
        string measurement,
        string column,
        ReadOnlySpan<float> queryVector,
        int k,
        KnnMetric metric = KnnMetric.Cosine,
        IReadOnlyDictionary<string, string>? tagFilter = null,
        TimeRange? timeRange = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(measurement);
        ArgumentException.ThrowIfNullOrEmpty(column);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
        if (queryVector.IsEmpty)
            throw new ArgumentException("查询向量不可为空。", nameof(queryVector));
        if (metric is < KnnMetric.Cosine or > KnnMetric.InnerProduct)
            throw new ArgumentOutOfRangeException(nameof(metric));
        int tagCount = tagFilter?.Count ?? 0;
        if (tagCount > MaxTagFilterCount)
            throw new ArgumentException($"tag 过滤条目数 {tagCount} 超过上限 {MaxTagFilterCount}。", nameof(tagFilter));

        TimeRange range = timeRange ?? TimeRange.All;

        long payloadLength =
            SpanWriter.MeasureVarString(db) +
            SpanWriter.MeasureVarString(measurement) +
            SpanWriter.MeasureVarString(column) +
            SpanWriter.MeasureVarUInt32((uint)k) +
            1 +
            SpanWriter.MeasureVarUInt32((uint)tagCount);
        if (tagFilter is not null)
        {
            foreach (KeyValuePair<string, string> tag in tagFilter)
                payloadLength += SpanWriter.MeasureVarString(tag.Key) + SpanWriter.MeasureVarString(tag.Value);
        }
        payloadLength += 8 + 8 + SpanWriter.MeasureVarUInt32((uint)queryVector.Length) + 4L * queryVector.Length;

        if (payloadLength > FrameHeader.MaxFramePayloadBytes)
            throw new ArgumentException($"帧 payload 长度 {payloadLength} 超过上限 {FrameHeader.MaxFramePayloadBytes}。");

        var header = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            (byte)FrameService.Vector, (byte)VectorFrameOp.Search, (byte)FrameFlags.None, streamId);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + (int)payloadLength);
        header.Write(span);
        var w = new SpanWriter(span.Slice(FrameHeader.Size, (int)payloadLength));
        w.WriteVarString(db);
        w.WriteVarString(measurement);
        w.WriteVarString(column);
        w.WriteVarUInt32((uint)k);
        w.WriteByte((byte)metric);
        w.WriteVarUInt32((uint)tagCount);
        if (tagFilter is not null)
        {
            foreach (KeyValuePair<string, string> tag in tagFilter)
            {
                w.WriteVarString(tag.Key);
                w.WriteVarString(tag.Value);
            }
        }
        w.WriteInt64(range.FromInclusive);
        w.WriteInt64(range.ToInclusive);
        w.WriteVarUInt32((uint)queryVector.Length);
        w.WriteStructs(queryVector);
        writer.Advance(FrameHeader.Size + (int)payloadLength);
    }

    /// <summary>
    /// 解码 KNN 检索请求帧体。查询向量已物化为持有型 <c>float[]</c>（不依赖输入缓冲存活期）。
    /// </summary>
    public static VectorSearchFrameRequest DecodeSearchRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        string db = ReadBoundedString(ref reader, "db", MaxNameBytes);
        string measurement = ReadBoundedString(ref reader, "measurement", MaxNameBytes);
        if (measurement.Length == 0)
            throw new FrameFormatException("measurement 不可为空。");
        string column = ReadBoundedString(ref reader, "column", MaxNameBytes);
        if (column.Length == 0)
            throw new FrameFormatException("column 不可为空。");

        uint k = reader.ReadVarUInt32();
        if (k is 0 or > int.MaxValue)
            throw new FrameFormatException($"k 值 {k} 非法（1 ~ {int.MaxValue}）。");

        byte metric = reader.ReadByte();
        if (metric > (byte)KnnMetric.InnerProduct)
            throw new FrameFormatException($"metric {metric} 非法（0=cosine / 1=l2 / 2=inner_product）。");

        uint tagCount = reader.ReadVarUInt32();
        if (tagCount > MaxTagFilterCount)
            throw new FrameFormatException($"tag 过滤条目数 {tagCount} 超过上限 {MaxTagFilterCount}。");
        Dictionary<string, string>? tagFilter = null;
        for (uint i = 0; i < tagCount; i++)
        {
            string key = ReadBoundedString(ref reader, "tag key", MaxNameBytes);
            if (key.Length == 0)
                throw new FrameFormatException("tag key 不可为空。");
            string value = ReadBoundedString(ref reader, "tag value", MaxNameBytes);
            tagFilter ??= new Dictionary<string, string>(StringComparer.Ordinal);
            if (!tagFilter.TryAdd(key, value))
                throw new FrameFormatException($"tag 过滤 key '{key}' 重复。");
        }

        long fromInclusive = reader.ReadInt64();
        long toInclusive = reader.ReadInt64();
        if (fromInclusive > toInclusive)
            throw new FrameFormatException($"时间窗起点 {fromInclusive} 大于终点 {toInclusive}。");

        uint dim = reader.ReadVarUInt32();
        if (dim == 0)
            throw new FrameFormatException("查询向量维度不可为 0。");
        if (4L * dim > reader.Remaining)
            throw new FrameFormatException($"查询向量维度 {dim} 超出帧体剩余长度。");
        float[] queryVector = reader.ReadStructs<float>((int)dim).ToArray();

        if (reader.Remaining != 0)
            throw new FrameFormatException("search 请求帧体尾部有多余字节。");
        return new VectorSearchFrameRequest(
            db, measurement, column, (int)k, (KnnMetric)metric, tagFilter,
            new TimeRange(fromInclusive, toInclusive), queryVector);
    }

    // ────────────────────────────── search (op=1) 响应 ──────────────────────────────
    // 块布局与 sql service 完全一致，仅帧头 service/op 不同；解码直接用 SqlFrameCodec 的
    // PeekChunkKind / DecodeQueryMetaFrame / DecodeQueryRowsFrame / DecodeQueryEndFrame。

    /// <summary>
    /// 编码 meta 响应帧（chunkKind=1，帧头 vector/search）：结果集列名序列。永远是响应流的第一帧。
    /// </summary>
    public static void EncodeSearchMetaFrame(IBufferWriter<byte> writer, uint streamId, IReadOnlyList<string> columns)
        => SqlFrameCodec.EncodeMetaFrameCore(writer, (byte)FrameService.Vector, (byte)VectorFrameOp.Search, streamId, columns);

    /// <summary>
    /// 编码一个 rows 响应帧（chunkKind=2，帧头 vector/search）：列式二进制行块，
    /// 向量列以 f32 二进制承载（<see cref="SqlValueKind.Vector"/>）。
    /// </summary>
    public static void EncodeSearchRowsFrame(
        IBufferWriter<byte> writer,
        uint streamId,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int start,
        int count,
        int columnCount)
        => SqlFrameCodec.EncodeRowsFrameCore(writer, (byte)FrameService.Vector, (byte)VectorFrameOp.Search,
            streamId, rows, start, count, columnCount);

    /// <summary>
    /// 编码 end 响应帧（chunkKind=3，帧头 vector/search）：总行数 + 耗时。永远是响应流的最后一帧。
    /// </summary>
    public static void EncodeSearchEndFrame(IBufferWriter<byte> writer, uint streamId, long rowCount, double elapsedMilliseconds)
        => SqlFrameCodec.EncodeEndFrameCore(writer, (byte)FrameService.Vector, (byte)VectorFrameOp.Search,
            streamId, rowCount, elapsedMilliseconds);

    // ────────────────────────────── 共享辅助 ──────────────────────────────

    private static string ReadBoundedString(ref SpanReader reader, string field, int maxBytes)
    {
        uint length = reader.ReadVarUInt32();
        if (length > (uint)maxBytes)
            throw new FrameFormatException($"{field} 长度 {length} 超过上限 {maxBytes} 字节。");
        if (length == 0)
            return string.Empty;
        if (length > (uint)reader.Remaining)
            throw new FrameFormatException($"{field} 长度 {length} 超出帧体剩余长度。");
        return System.Text.Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }
}

/// <summary>
/// vector search 请求帧解码结果。
/// </summary>
/// <param name="Db">数据库名。</param>
/// <param name="Measurement">measurement 名。</param>
/// <param name="Column">向量列名（必须是 VECTOR 类型 FIELD 列）。</param>
/// <param name="K">返回最近邻数量上限（≥1）。</param>
/// <param name="Metric">距离度量。</param>
/// <param name="TagFilter">tag 等值过滤（无过滤时为 null）。</param>
/// <param name="TimeRange">闭区间时间窗（毫秒 UTC；全时间轴 = [long.MinValue, long.MaxValue]）。</param>
/// <param name="QueryVector">查询向量（持有型，已从帧体拷贝）。</param>
public sealed record VectorSearchFrameRequest(
    string Db,
    string Measurement,
    string Column,
    int K,
    KnnMetric Metric,
    IReadOnlyDictionary<string, string>? TagFilter,
    TimeRange TimeRange,
    float[] QueryVector);
