using System.Buffers;
using System.Runtime.InteropServices;
using SonnetDB.Ingest;
using SonnetDB.IO;
using SonnetDB.Model;
using SonnetDB.Storage.Format;

namespace SonnetDB.Protocol;

/// <summary>
/// tsdb service（<see cref="FrameService.Tsdb"/>）列式批量写 opcode 的帧体编解码（M28 P5b #237）。
/// 帧体 = db + measurement + flushMode + 列式块序列；每块 = 一组 tag 取值 + 时间戳列（i64×n，little-endian
/// 定宽，可整段 <see cref="MemoryMarshal"/> 直传）+ 若干字段列（类型 + 可选 presence 位图 + 紧凑值序列）。
/// 对齐 IoTDB Tablet / PG COPY BINARY 的列式批思路，消灭行式 JSON 的解析/体积税。
/// 解码结果是输入缓冲上的零拷贝视图，仅在缓冲存活期内有效（服务端在 AdvanceTo 之前同步消费完毕）。
/// </summary>
public static class TsdbFrameCodec
{
    /// <summary>名字（db / measurement / tag key / tag value / 字段名）UTF-8 字节数上限。</summary>
    public const int MaxNameBytes = 512;

    /// <summary>单块 tag 数上限。</summary>
    public const int MaxTagCount = 1024;

    // ────────────────────────────── write-columnar (op=1) 请求 ──────────────────────────────

    /// <summary>
    /// 编码列式批量写请求帧：db, measurement, flushMode, 列式块序列。
    /// </summary>
    public static void EncodeWriteColumnarRequest(
        IBufferWriter<byte> writer,
        uint streamId,
        string db,
        string measurement,
        BulkFlushMode flushMode,
        IReadOnlyList<TsdbColumnarBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count == 0)
            throw new ArgumentException("列式写请求需包含至少 1 个块。", nameof(blocks));
        if (flushMode is < BulkFlushMode.None or > BulkFlushMode.Sync)
            throw new ArgumentOutOfRangeException(nameof(flushMode));

        int topMetaLength =
            SpanWriter.MeasureVarString(db) +
            SpanWriter.MeasureVarString(measurement) +
            1 +
            SpanWriter.MeasureVarUInt32((uint)blocks.Count);
        long payloadLength = topMetaLength;
        for (int i = 0; i < blocks.Count; i++)
            payloadLength += MeasureBlock(blocks[i]);
        if (payloadLength > FrameHeader.MaxFramePayloadBytes)
            throw new ArgumentException($"帧 payload 长度 {payloadLength} 超过上限 {FrameHeader.MaxFramePayloadBytes}。");

        var header = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            (byte)FrameService.Tsdb, (byte)TsdbFrameOp.WriteColumnar, (byte)FrameFlags.None, streamId);
        Span<byte> headerSpan = writer.GetSpan(FrameHeader.Size);
        header.Write(headerSpan);
        writer.Advance(FrameHeader.Size);

        WriteChunk(writer, topMetaLength, (ref SpanWriter w) =>
        {
            w.WriteVarString(db);
            w.WriteVarString(measurement);
            w.WriteByte((byte)flushMode);
            w.WriteVarUInt32((uint)blocks.Count);
        });

        for (int i = 0; i < blocks.Count; i++)
            WriteBlock(writer, blocks[i]);
    }

    /// <summary>
    /// 解码列式写请求帧体的头部（db / measurement / flushMode / 块数），块序列保持未解码的
    /// 零拷贝切片，由 <see cref="TsdbColumnarPointReader"/> 流式列转行消费。
    /// </summary>
    public static TsdbWriteColumnarFrameRequest DecodeWriteColumnarRequest(ReadOnlyMemory<byte> payload)
    {
        var reader = new SpanReader(payload.Span);
        string db = ReadName(ref reader, "db");
        string measurement = ReadName(ref reader, "measurement");
        byte flushMode = reader.ReadByte();
        if (flushMode > (byte)BulkFlushMode.Sync)
            throw new FrameFormatException($"flushMode {flushMode} 非法（0=none / 1=async / 2=sync）。");
        uint blockCount = reader.ReadVarUInt32();
        if (blockCount == 0)
            throw new FrameFormatException("列式写请求需包含至少 1 个块。");
        if (blockCount > (uint)reader.Remaining)
            throw new FrameFormatException($"块数 {blockCount} 超出帧体剩余长度。");
        return new TsdbWriteColumnarFrameRequest(
            db, measurement, (BulkFlushMode)flushMode, (int)blockCount, payload[reader.Position..]);
    }

    // ────────────────────────────── write-columnar (op=1) 响应 ──────────────────────────────

    /// <summary>
    /// 编码列式写响应帧：written（成功写入点数）。
    /// </summary>
    public static void EncodeWriteColumnarResponse(IBufferWriter<byte> writer, uint streamId, int written)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(written);
        int payloadLength = SpanWriter.MeasureVarUInt32((uint)written);
        var header = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            (byte)FrameService.Tsdb, (byte)TsdbFrameOp.WriteColumnar, (byte)FrameFlags.Response, streamId);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + payloadLength);
        header.Write(span);
        var meta = new SpanWriter(span.Slice(FrameHeader.Size, payloadLength));
        meta.WriteVarUInt32((uint)written);
        writer.Advance(FrameHeader.Size + payloadLength);
    }

    /// <summary>
    /// 解码列式写响应帧体。
    /// </summary>
    public static int DecodeWriteColumnarResponse(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        uint written = reader.ReadVarUInt32();
        if (written > int.MaxValue)
            throw new FrameFormatException($"written {written} 超出 int 范围。");
        return (int)written;
    }

    // ────────────────────────────── 编码内部 ──────────────────────────────

    private static long MeasureBlock(TsdbColumnarBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        int rowCount = block.Timestamps.Length;
        if (rowCount == 0)
            throw new ArgumentException("列式块的时间戳列不得为空。");
        if (block.Columns is null || block.Columns.Count == 0)
            throw new ArgumentException("列式块需包含至少 1 个字段列。");

        int tagCount = block.Tags?.Count ?? 0;
        if (tagCount > MaxTagCount)
            throw new ArgumentException($"tag 数 {tagCount} 超过上限 {MaxTagCount}。");

        long total = SpanWriter.MeasureVarUInt32((uint)tagCount);
        if (block.Tags is not null)
        {
            foreach (KeyValuePair<string, string> tag in block.Tags)
                total += SpanWriter.MeasureVarString(tag.Key) + SpanWriter.MeasureVarString(tag.Value);
        }

        total += SpanWriter.MeasureVarUInt32((uint)rowCount);
        total += 8L * rowCount;
        total += SpanWriter.MeasureVarUInt32((uint)block.Columns.Count);
        for (int i = 0; i < block.Columns.Count; i++)
            total += MeasureColumn(block.Columns[i], rowCount);
        return total;
    }

    private static long MeasureColumn(in TsdbColumnarColumn column, int rowCount)
    {
        int present = column.GetPresentCount(rowCount);
        long total = SpanWriter.MeasureVarString(column.Name) + 2;
        if (!column.Presence.IsEmpty)
            total += (rowCount + 7) >> 3;

        switch (column.Type)
        {
            case FieldType.Float64:
                RequireValueCount(column.Float64Values.Length, present, column.Name);
                total += 8L * present;
                break;
            case FieldType.Int64:
                RequireValueCount(column.Int64Values.Length, present, column.Name);
                total += 8L * present;
                break;
            case FieldType.Boolean:
                RequireValueCount(column.BooleanValues.Length, present, column.Name);
                total += present;
                break;
            case FieldType.String:
                RequireValueCount(column.StringValues?.Count ?? 0, present, column.Name);
                for (int i = 0; i < present; i++)
                    total += SpanWriter.MeasureVarString(column.StringValues![i]);
                break;
            case FieldType.Vector:
                if (column.VectorDim < 1)
                    throw new ArgumentException($"字段列 '{column.Name}' 的向量维度须 ≥ 1。");
                RequireValueCount(column.VectorValues.Length, checked(column.VectorDim * present), column.Name);
                total += SpanWriter.MeasureVarUInt32((uint)column.VectorDim) + 4L * column.VectorDim * present;
                break;
            case FieldType.GeoPoint:
                RequireValueCount(column.GeoPointValues.Length, present, column.Name);
                total += 16L * present;
                break;
            default:
                throw new ArgumentException($"字段列 '{column.Name}' 的类型 {column.Type} 不支持列式编码。");
        }

        return total;
    }

    private static void RequireValueCount(int actual, int expected, string name)
    {
        if (actual != expected)
            throw new ArgumentException($"字段列 '{name}' 的值数量 {actual} 与 present 行数 {expected} 不一致。");
    }

    private static void WriteBlock(IBufferWriter<byte> writer, TsdbColumnarBlock block)
    {
        int rowCount = block.Timestamps.Length;
        int tagCount = block.Tags?.Count ?? 0;

        int prefixLength = SpanWriter.MeasureVarUInt32((uint)tagCount);
        if (block.Tags is not null)
        {
            foreach (KeyValuePair<string, string> tag in block.Tags)
                prefixLength += SpanWriter.MeasureVarString(tag.Key) + SpanWriter.MeasureVarString(tag.Value);
        }
        prefixLength += SpanWriter.MeasureVarUInt32((uint)rowCount);

        var tags = block.Tags;
        WriteChunk(writer, prefixLength, (ref SpanWriter w) =>
        {
            w.WriteVarUInt32((uint)tagCount);
            if (tags is not null)
            {
                foreach (KeyValuePair<string, string> tag in tags)
                {
                    w.WriteVarString(tag.Key);
                    w.WriteVarString(tag.Value);
                }
            }
            w.WriteVarUInt32((uint)rowCount);
        });

        writer.Write(MemoryMarshal.AsBytes(block.Timestamps.Span));

        int columnCount = block.Columns.Count;
        WriteChunk(writer, SpanWriter.MeasureVarUInt32((uint)columnCount),
            (ref SpanWriter w) => w.WriteVarUInt32((uint)columnCount));

        for (int i = 0; i < columnCount; i++)
            WriteColumn(writer, block.Columns[i], rowCount);
    }

    private static void WriteColumn(IBufferWriter<byte> writer, in TsdbColumnarColumn column, int rowCount)
    {
        bool sparse = !column.Presence.IsEmpty;
        int bitmapLength = sparse ? (rowCount + 7) >> 3 : 0;
        int present = column.GetPresentCount(rowCount);
        int headLength = SpanWriter.MeasureVarString(column.Name) + 2 + bitmapLength
            + (column.Type == FieldType.Vector ? SpanWriter.MeasureVarUInt32((uint)column.VectorDim) : 0);

        string name = column.Name;
        FieldType type = column.Type;
        ReadOnlyMemory<bool> presence = column.Presence;
        int vectorDim = column.VectorDim;
        WriteChunk(writer, headLength, (ref SpanWriter w) =>
        {
            w.WriteVarString(name);
            w.WriteByte((byte)type);
            w.WriteByte(sparse ? (byte)1 : (byte)0);
            if (sparse)
                WriteBitmap(ref w, presence.Span, bitmapLength);
            if (type == FieldType.Vector)
                w.WriteVarUInt32((uint)vectorDim);
        });

        switch (column.Type)
        {
            case FieldType.Float64:
                writer.Write(MemoryMarshal.AsBytes(column.Float64Values.Span));
                break;
            case FieldType.Int64:
                writer.Write(MemoryMarshal.AsBytes(column.Int64Values.Span));
                break;
            case FieldType.Boolean:
            {
                ReadOnlyMemory<bool> values = column.BooleanValues;
                WriteChunk(writer, present, (ref SpanWriter w) =>
                {
                    ReadOnlySpan<bool> span = values.Span;
                    for (int i = 0; i < span.Length; i++)
                        w.WriteByte(span[i] ? (byte)1 : (byte)0);
                });
                break;
            }
            case FieldType.String:
            {
                IReadOnlyList<string> values = column.StringValues!;
                for (int i = 0; i < values.Count; i++)
                {
                    string value = values[i];
                    WriteChunk(writer, SpanWriter.MeasureVarString(value),
                        (ref SpanWriter w) => w.WriteVarString(value));
                }
                break;
            }
            case FieldType.Vector:
                writer.Write(MemoryMarshal.AsBytes(column.VectorValues.Span));
                break;
            case FieldType.GeoPoint:
            {
                ReadOnlyMemory<GeoPoint> values = column.GeoPointValues;
                WriteChunk(writer, 16 * present, (ref SpanWriter w) =>
                {
                    ReadOnlySpan<GeoPoint> span = values.Span;
                    for (int i = 0; i < span.Length; i++)
                    {
                        w.WriteDouble(span[i].Lat);
                        w.WriteDouble(span[i].Lon);
                    }
                });
                break;
            }
        }
    }

    private static void WriteBitmap(ref SpanWriter writer, ReadOnlySpan<bool> presence, int bitmapLength)
    {
        for (int byteIndex = 0; byteIndex < bitmapLength; byteIndex++)
        {
            int baseRow = byteIndex << 3;
            int bits = Math.Min(8, presence.Length - baseRow);
            byte value = 0;
            for (int i = 0; i < bits; i++)
            {
                if (presence[baseRow + i])
                    value |= (byte)(1 << i);
            }
            writer.WriteByte(value);
        }
    }

    // ────────────────────────────── 共享辅助 ──────────────────────────────

    private delegate void ChunkWriter(ref SpanWriter writer);

    private static void WriteChunk(IBufferWriter<byte> writer, int length, ChunkWriter write)
    {
        Span<byte> span = writer.GetSpan(length);
        var w = new SpanWriter(span[..length]);
        write(ref w);
        writer.Advance(length);
    }

    internal static string ReadName(ref SpanReader reader, string field)
    {
        uint length = reader.ReadVarUInt32();
        if (length > MaxNameBytes)
            throw new FrameFormatException($"{field} 长度 {length} 超过上限 {MaxNameBytes} 字节。");
        if (length == 0)
            return string.Empty;
        if (length > (uint)reader.Remaining)
            throw new FrameFormatException($"{field} 长度 {length} 超出帧体剩余长度。");
        return System.Text.Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }
}

/// <summary>
/// 列式写请求帧解码结果（帧体头部）。<see cref="Blocks"/> 是输入缓冲的零拷贝切片，
/// 由 <see cref="TsdbColumnarPointReader"/> 流式消费。
/// </summary>
/// <param name="Db">数据库名。</param>
/// <param name="Measurement">measurement 名。</param>
/// <param name="FlushMode">写入完成后的 Flush 档位。</param>
/// <param name="BlockCount">块数（≥ 1）。</param>
/// <param name="Blocks">未解码的块序列切片。</param>
public readonly record struct TsdbWriteColumnarFrameRequest(
    string Db,
    string Measurement,
    BulkFlushMode FlushMode,
    int BlockCount,
    ReadOnlyMemory<byte> Blocks);

/// <summary>
/// 列式写入的一个块：一组 tag 取值下的时间戳列与若干字段列。
/// 块内所有行共享同一组 tag（同一序列族），字段列长度与时间戳行数经 presence 位图对齐。
/// </summary>
/// <param name="Tags">tag 键值对（null 或空 = 无 tag）。</param>
/// <param name="Timestamps">Unix epoch 毫秒时间戳列（行数 = Length，须 ≥ 1）。</param>
/// <param name="Columns">字段列（须 ≥ 1 列）。</param>
public sealed record TsdbColumnarBlock(
    IReadOnlyDictionary<string, string>? Tags,
    ReadOnlyMemory<long> Timestamps,
    IReadOnlyList<TsdbColumnarColumn> Columns);

/// <summary>
/// 列式写入的一个字段列：字段名 + 类型 + 可选 presence 位图 + 紧凑值序列（仅 present 行，按行序）。
/// 通过静态工厂按类型构造，零装箱。
/// </summary>
public readonly struct TsdbColumnarColumn
{
    /// <summary>字段名。</summary>
    public string Name { get; }

    /// <summary>字段类型。</summary>
    public FieldType Type { get; }

    /// <summary>presence 位图（每行一位；空 = 稠密，所有行都有值）。</summary>
    public ReadOnlyMemory<bool> Presence { get; }

    internal ReadOnlyMemory<double> Float64Values { get; }
    internal ReadOnlyMemory<long> Int64Values { get; }
    internal ReadOnlyMemory<bool> BooleanValues { get; }
    internal IReadOnlyList<string>? StringValues { get; }
    internal ReadOnlyMemory<float> VectorValues { get; }
    internal int VectorDim { get; }
    internal ReadOnlyMemory<GeoPoint> GeoPointValues { get; }

    private TsdbColumnarColumn(
        string name, FieldType type, ReadOnlyMemory<bool> presence,
        ReadOnlyMemory<double> float64 = default, ReadOnlyMemory<long> int64 = default,
        ReadOnlyMemory<bool> booleans = default, IReadOnlyList<string>? strings = null,
        ReadOnlyMemory<float> vectors = default, int vectorDim = 0,
        ReadOnlyMemory<GeoPoint> geoPoints = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Type = type;
        Presence = presence;
        Float64Values = float64;
        Int64Values = int64;
        BooleanValues = booleans;
        StringValues = strings;
        VectorValues = vectors;
        VectorDim = vectorDim;
        GeoPointValues = geoPoints;
    }

    /// <summary>创建 Float64 字段列。</summary>
    public static TsdbColumnarColumn Float64(string name, ReadOnlyMemory<double> values, ReadOnlyMemory<bool> presence = default)
        => new(name, FieldType.Float64, presence, float64: values);

    /// <summary>创建 Int64 字段列。</summary>
    public static TsdbColumnarColumn Int64(string name, ReadOnlyMemory<long> values, ReadOnlyMemory<bool> presence = default)
        => new(name, FieldType.Int64, presence, int64: values);

    /// <summary>创建 Boolean 字段列。</summary>
    public static TsdbColumnarColumn Boolean(string name, ReadOnlyMemory<bool> values, ReadOnlyMemory<bool> presence = default)
        => new(name, FieldType.Boolean, presence, booleans: values);

    /// <summary>创建 String 字段列。</summary>
    public static TsdbColumnarColumn String(string name, IReadOnlyList<string> values, ReadOnlyMemory<bool> presence = default)
        => new(name, FieldType.String, presence, strings: values ?? throw new ArgumentNullException(nameof(values)));

    /// <summary>创建定长向量字段列（values 为按行序紧凑排布的 dim × presentCount 个分量）。</summary>
    public static TsdbColumnarColumn Vector(string name, int dim, ReadOnlyMemory<float> values, ReadOnlyMemory<bool> presence = default)
        => new(name, FieldType.Vector, presence, vectors: values, vectorDim: dim);

    /// <summary>创建 GeoPoint 字段列。</summary>
    public static TsdbColumnarColumn GeoPoint(string name, ReadOnlyMemory<GeoPoint> values, ReadOnlyMemory<bool> presence = default)
        => new(name, FieldType.GeoPoint, presence, geoPoints: values);

    internal int GetPresentCount(int rowCount)
    {
        if (Presence.IsEmpty)
            return rowCount;
        if (Presence.Length != rowCount)
            throw new ArgumentException($"字段列 '{Name}' 的 presence 长度 {Presence.Length} 与块行数 {rowCount} 不一致。");
        int count = 0;
        ReadOnlySpan<bool> span = Presence.Span;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i])
                count++;
        }
        return count;
    }
}
