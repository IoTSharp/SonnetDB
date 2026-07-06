using System.Buffers;
using SonnetDB.IO;
using SonnetDB.Model;
using SonnetDB.Sql;

namespace SonnetDB.Protocol;

/// <summary>
/// sql service（<see cref="FrameService.Sql"/>）流式查询 opcode 的帧体编解码（M28 P5b #238）。
/// 请求帧 = db + sql 文本 + 可选命名标量参数；响应为**同 streamId 的帧序列**：
/// meta 帧（列名）→ 0..N 个 rows 帧（列式二进制行块）→ end 帧（行数 + 耗时），
/// 服务端逐块编码逐块 flush，响应缓冲内存上界 = 单块，消灭全量 JSON 物化与数字文本税。
/// rows 帧按块内逐列推断值类型：单一类型列走稠密定宽/紧凑编码（可选 null 位图），
/// 混合类型列回退 variant（逐值带类型标记），大 long 不会被折成 double（对齐 #219 Q15 精度语义）。
/// 解码结果为持有型数据（string/byte[] 等已物化），不依赖输入缓冲存活期。
/// </summary>
public static class SqlFrameCodec
{
    /// <summary>名字（db / 列名 / 参数名）UTF-8 字节数上限。</summary>
    public const int MaxNameBytes = 512;

    /// <summary>SQL 文本 UTF-8 字节数上限。</summary>
    public const int MaxSqlBytes = 1024 * 1024;

    /// <summary>单请求命名参数数量上限。</summary>
    public const int MaxParameterCount = 256;

    /// <summary>结果集列数上限。</summary>
    public const int MaxColumnCount = 4096;

    /// <summary>单 rows 帧行数上限（解码防御；编码默认用 <see cref="DefaultMaxChunkRows"/>）。</summary>
    public const int MaxChunkRows = 65536;

    /// <summary>单 rows 帧解码单元格总数上限（rowCount × columnCount，防御分配炸弹）。</summary>
    public const long MaxChunkCells = 16_777_216;

    /// <summary>编码默认单 rows 帧行数上限。</summary>
    public const int DefaultMaxChunkRows = 4096;

    /// <summary>编码默认单 rows 帧目标字节数（达到即切块，软上限）。</summary>
    public const int DefaultTargetChunkBytes = 256 * 1024;

    /// <summary>variant 列编码标记（列内值类型不一致时逐值带 <see cref="SqlValueKind"/> 标记）。</summary>
    private const byte VariantColumnKind = 255;

    // ────────────────────────────── query (op=1) 请求 ──────────────────────────────

    /// <summary>
    /// 编码查询请求帧：db, sql, 可选命名标量参数（null/long/double/bool/string，
    /// 经 <see cref="SqlParameterBinder"/> 绑定 <c>@name</c> / <c>:name</c> 占位符）。
    /// </summary>
    public static void EncodeQueryRequest(
        IBufferWriter<byte> writer,
        uint streamId,
        string db,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(sql);
        int paramCount = parameters?.Count ?? 0;
        if (paramCount > MaxParameterCount)
            throw new ArgumentException($"参数数 {paramCount} 超过上限 {MaxParameterCount}。", nameof(parameters));

        long payloadLength =
            SpanWriter.MeasureVarString(db) +
            SpanWriter.MeasureVarString(sql) +
            SpanWriter.MeasureVarUInt32((uint)paramCount);
        if (parameters is not null)
        {
            foreach (KeyValuePair<string, object?> parameter in parameters)
                payloadLength += SpanWriter.MeasureVarString(parameter.Key) + 1 + MeasureParameterValue(parameter.Value);
        }

        if (payloadLength > FrameHeader.MaxFramePayloadBytes)
            throw new ArgumentException($"帧 payload 长度 {payloadLength} 超过上限 {FrameHeader.MaxFramePayloadBytes}。");

        var header = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            (byte)FrameService.Sql, (byte)SqlFrameOp.Query, (byte)FrameFlags.None, streamId);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + (int)payloadLength);
        header.Write(span);
        var w = new SpanWriter(span.Slice(FrameHeader.Size, (int)payloadLength));
        w.WriteVarString(db);
        w.WriteVarString(sql);
        w.WriteVarUInt32((uint)paramCount);
        if (parameters is not null)
        {
            foreach (KeyValuePair<string, object?> parameter in parameters)
            {
                w.WriteVarString(parameter.Key);
                WriteParameterValue(ref w, parameter.Value);
            }
        }
        writer.Advance(FrameHeader.Size + (int)payloadLength);
    }

    /// <summary>
    /// 解码查询请求帧体。参数集合已物化为 <see cref="SqlParameters"/>（无参数时为 null）。
    /// </summary>
    public static SqlQueryFrameRequest DecodeQueryRequest(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        string db = ReadBoundedString(ref reader, "db", MaxNameBytes);
        string sql = ReadBoundedString(ref reader, "sql", MaxSqlBytes);
        if (sql.Length == 0)
            throw new FrameFormatException("sql 文本不可为空。");

        uint paramCount = reader.ReadVarUInt32();
        if (paramCount > MaxParameterCount)
            throw new FrameFormatException($"参数数 {paramCount} 超过上限 {MaxParameterCount}。");

        SqlParameters? parameters = null;
        for (uint i = 0; i < paramCount; i++)
        {
            string name = ReadBoundedString(ref reader, "参数名", MaxNameBytes);
            if (name.Length == 0)
                throw new FrameFormatException("参数名不可为空。");
            object? value = ReadParameterValue(ref reader);
            (parameters ??= new SqlParameters()).AddNamed(name, value);
        }

        if (reader.Remaining != 0)
            throw new FrameFormatException("query 请求帧体尾部有多余字节。");
        return new SqlQueryFrameRequest(db, sql, parameters);
    }

    private static long MeasureParameterValue(object? value) => value switch
    {
        null => 0,
        long or int or short or sbyte or byte or ushort or uint => 8,
        double or float => 8,
        bool => 1,
        string s => SpanWriter.MeasureVarString(s),
        _ => throw new ArgumentException($"不支持的参数值类型 {value.GetType().Name}（仅 null/long/double/bool/string）。"),
    };

    private static void WriteParameterValue(ref SpanWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteByte((byte)SqlValueKind.Null);
                break;
            case long or int or short or sbyte or byte or ushort or uint:
                writer.WriteByte((byte)SqlValueKind.Int64);
                writer.WriteInt64(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
            case double or float:
                writer.WriteByte((byte)SqlValueKind.Float64);
                writer.WriteDouble(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
            case bool b:
                writer.WriteByte((byte)SqlValueKind.Boolean);
                writer.WriteByte(b ? (byte)1 : (byte)0);
                break;
            case string s:
                writer.WriteByte((byte)SqlValueKind.String);
                writer.WriteVarString(s);
                break;
            default:
                throw new ArgumentException($"不支持的参数值类型 {value.GetType().Name}（仅 null/long/double/bool/string）。");
        }
    }

    private static object? ReadParameterValue(ref SpanReader reader)
    {
        byte tag = reader.ReadByte();
        return (SqlValueKind)tag switch
        {
            SqlValueKind.Null => null,
            SqlValueKind.Int64 => reader.ReadInt64(),
            SqlValueKind.Float64 => reader.ReadDouble(),
            SqlValueKind.Boolean => ReadBooleanByte(ref reader),
            SqlValueKind.String => reader.ReadVarString(),
            _ => throw new FrameFormatException($"参数值类型标记 {tag} 非法（仅 0=null/1=int64/2=float64/3=bool/4=string）。"),
        };
    }

    // ────────────────────────────── meta 响应帧 ──────────────────────────────

    /// <summary>
    /// 编码 meta 响应帧（chunkKind=1）：结果集列名序列。永远是响应流的第一帧。
    /// </summary>
    public static void EncodeQueryMetaFrame(IBufferWriter<byte> writer, uint streamId, IReadOnlyList<string> columns)
        => EncodeMetaFrameCore(writer, (byte)FrameService.Sql, (byte)SqlFrameOp.Query, streamId, columns);

    /// <summary>
    /// meta 帧编码内核：块布局固定，帧头 service/op 由调用方指定
    /// （sql query 与 vector search 共用同一响应块词汇表）。
    /// </summary>
    internal static void EncodeMetaFrameCore(
        IBufferWriter<byte> writer, byte service, byte op, uint streamId, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count > MaxColumnCount)
            throw new ArgumentException($"列数 {columns.Count} 超过上限 {MaxColumnCount}。", nameof(columns));

        long payloadLength = 1 + SpanWriter.MeasureVarUInt32((uint)columns.Count);
        for (int i = 0; i < columns.Count; i++)
            payloadLength += SpanWriter.MeasureVarString(columns[i]);
        if (payloadLength > FrameHeader.MaxFramePayloadBytes)
            throw new ArgumentException($"帧 payload 长度 {payloadLength} 超过上限 {FrameHeader.MaxFramePayloadBytes}。");

        var header = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            service, op, (byte)FrameFlags.Response, streamId);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + (int)payloadLength);
        header.Write(span);
        var w = new SpanWriter(span.Slice(FrameHeader.Size, (int)payloadLength));
        w.WriteByte((byte)SqlQueryChunkKind.Meta);
        w.WriteVarUInt32((uint)columns.Count);
        for (int i = 0; i < columns.Count; i++)
            w.WriteVarString(columns[i]);
        writer.Advance(FrameHeader.Size + (int)payloadLength);
    }

    /// <summary>
    /// 解码 meta 响应帧体，返回列名数组。
    /// </summary>
    public static string[] DecodeQueryMetaFrame(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        RequireChunkKind(ref reader, SqlQueryChunkKind.Meta);
        uint columnCount = reader.ReadVarUInt32();
        if (columnCount > MaxColumnCount)
            throw new FrameFormatException($"列数 {columnCount} 超过上限 {MaxColumnCount}。");
        if (columnCount > (uint)reader.Remaining)
            throw new FrameFormatException($"列数 {columnCount} 超出帧体剩余长度。");

        var columns = new string[columnCount];
        for (int i = 0; i < columns.Length; i++)
            columns[i] = ReadBoundedString(ref reader, "列名", MaxNameBytes);
        if (reader.Remaining != 0)
            throw new FrameFormatException("meta 帧体尾部有多余字节。");
        return columns;
    }

    // ────────────────────────────── rows 响应帧 ──────────────────────────────

    /// <summary>
    /// 编码一个 rows 响应帧（chunkKind=2）：<paramref name="rows"/> 的 [start, start+count) 行块，
    /// 块内逐列推断类型走列式编码。行块字节数应由调用方经 <see cref="SelectChunkRowCount"/> 控制。
    /// </summary>
    public static void EncodeQueryRowsFrame(
        IBufferWriter<byte> writer,
        uint streamId,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int start,
        int count,
        int columnCount)
        => EncodeRowsFrameCore(writer, (byte)FrameService.Sql, (byte)SqlFrameOp.Query,
            streamId, rows, start, count, columnCount);

    /// <summary>
    /// rows 帧编码内核：块布局固定，帧头 service/op 由调用方指定。
    /// </summary>
    internal static void EncodeRowsFrameCore(
        IBufferWriter<byte> writer,
        byte service,
        byte op,
        uint streamId,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int start,
        int count,
        int columnCount)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxChunkRows);
        ArgumentOutOfRangeException.ThrowIfLessThan(columnCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columnCount, MaxColumnCount);
        if (start + (long)count > rows.Count)
            throw new ArgumentOutOfRangeException(nameof(count), "行块范围超出行集合。");

        for (int r = start; r < start + count; r++)
        {
            if (rows[r].Count != columnCount)
                throw new ArgumentException($"第 {r} 行的列数 {rows[r].Count} 与结果列数 {columnCount} 不一致。", nameof(rows));
        }

        var plans = new ColumnPlan[columnCount];
        long payloadLength = 1
            + SpanWriter.MeasureVarUInt32((uint)count)
            + SpanWriter.MeasureVarUInt32((uint)columnCount);
        for (int c = 0; c < columnCount; c++)
        {
            plans[c] = PlanColumn(rows, start, count, c);
            payloadLength += plans[c].Bytes;
        }

        if (payloadLength > FrameHeader.MaxFramePayloadBytes)
            throw new ArgumentException($"帧 payload 长度 {payloadLength} 超过上限 {FrameHeader.MaxFramePayloadBytes}；请缩小行块。");

        var header = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            service, op, (byte)FrameFlags.Response, streamId);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + (int)payloadLength);
        header.Write(span);
        var w = new SpanWriter(span.Slice(FrameHeader.Size, (int)payloadLength));
        w.WriteByte((byte)SqlQueryChunkKind.Rows);
        w.WriteVarUInt32((uint)count);
        w.WriteVarUInt32((uint)columnCount);
        for (int c = 0; c < columnCount; c++)
            WriteColumn(ref w, rows, start, count, c, plans[c]);
        writer.Advance(FrameHeader.Size + (int)payloadLength);
    }

    /// <summary>
    /// 解码一个 rows 响应帧体，返回行块（行主序；值为持有型数据）。
    /// 值运行时类型：long / double / bool / string / byte[] / <see cref="DateTime"/>(UTC) / float[] / <see cref="GeoPoint"/> / null。
    /// </summary>
    public static object?[][] DecodeQueryRowsFrame(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        RequireChunkKind(ref reader, SqlQueryChunkKind.Rows);
        uint rowCount = reader.ReadVarUInt32();
        if (rowCount is 0 or > MaxChunkRows)
            throw new FrameFormatException($"rows 帧行数 {rowCount} 非法（1 ~ {MaxChunkRows}）。");
        uint columnCount = reader.ReadVarUInt32();
        if (columnCount is 0 or > MaxColumnCount)
            throw new FrameFormatException($"rows 帧列数 {columnCount} 非法（1 ~ {MaxColumnCount}）。");
        if ((long)rowCount * columnCount > MaxChunkCells)
            throw new FrameFormatException($"rows 帧单元格数 {(long)rowCount * columnCount} 超过上限 {MaxChunkCells}。");

        var rows = new object?[rowCount][];
        for (int r = 0; r < rows.Length; r++)
            rows[r] = new object?[columnCount];

        for (int c = 0; c < columnCount; c++)
        {
            byte kind = reader.ReadByte();
            if (kind == (byte)SqlValueKind.Null)
                continue;

            if (kind == VariantColumnKind)
            {
                for (int r = 0; r < rows.Length; r++)
                {
                    byte tag = reader.ReadByte();
                    if (tag != (byte)SqlValueKind.Null)
                        rows[r][c] = ReadValue(ref reader, tag);
                }
                continue;
            }

            RequireValueKind(kind);
            byte hasNulls = reader.ReadByte();
            if (hasNulls > 1)
                throw new FrameFormatException($"rows 帧 hasNulls 标记 {hasNulls} 非法（0/1）。");

            if (hasNulls == 0)
            {
                for (int r = 0; r < rows.Length; r++)
                    rows[r][c] = ReadValue(ref reader, kind);
                continue;
            }

            int bitmapLength = ((int)rowCount + 7) >> 3;
            ReadOnlySpan<byte> bitmap = reader.ReadBytes(bitmapLength);
            for (int r = 0; r < rows.Length; r++)
            {
                if ((bitmap[r >> 3] & (1 << (r & 7))) != 0)
                    rows[r][c] = ReadValue(ref reader, kind);
            }
        }

        if (reader.Remaining != 0)
            throw new FrameFormatException("rows 帧体尾部有多余字节。");
        return rows;
    }

    // ────────────────────────────── end 响应帧 ──────────────────────────────

    /// <summary>
    /// 编码 end 响应帧（chunkKind=3）：总行数 + 服务端执行耗时（毫秒）。永远是响应流的最后一帧。
    /// </summary>
    public static void EncodeQueryEndFrame(IBufferWriter<byte> writer, uint streamId, long rowCount, double elapsedMilliseconds)
        => EncodeEndFrameCore(writer, (byte)FrameService.Sql, (byte)SqlFrameOp.Query, streamId, rowCount, elapsedMilliseconds);

    /// <summary>
    /// end 帧编码内核：块布局固定，帧头 service/op 由调用方指定。
    /// </summary>
    internal static void EncodeEndFrameCore(
        IBufferWriter<byte> writer, byte service, byte op, uint streamId, long rowCount, double elapsedMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        int payloadLength = 1 + SpanWriter.MeasureVarUInt64((ulong)rowCount) + 8;
        var header = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            service, op, (byte)FrameFlags.Response, streamId);
        Span<byte> span = writer.GetSpan(FrameHeader.Size + payloadLength);
        header.Write(span);
        var w = new SpanWriter(span.Slice(FrameHeader.Size, payloadLength));
        w.WriteByte((byte)SqlQueryChunkKind.End);
        w.WriteVarUInt64((ulong)rowCount);
        w.WriteDouble(elapsedMilliseconds);
        writer.Advance(FrameHeader.Size + payloadLength);
    }

    /// <summary>
    /// 解码 end 响应帧体。
    /// </summary>
    public static (long RowCount, double ElapsedMilliseconds) DecodeQueryEndFrame(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        RequireChunkKind(ref reader, SqlQueryChunkKind.End);
        ulong rowCount = reader.ReadVarUInt64();
        if (rowCount > long.MaxValue)
            throw new FrameFormatException($"end 帧行数 {rowCount} 超出 long 范围。");
        double elapsed = reader.ReadDouble();
        if (reader.Remaining != 0)
            throw new FrameFormatException("end 帧体尾部有多余字节。");
        return ((long)rowCount, elapsed);
    }

    // ────────────────────────────── 块切分 ──────────────────────────────

    /// <summary>
    /// 从 <paramref name="start"/> 起选择下一个 rows 帧的行数：按行字节估算累加，
    /// 达到 <paramref name="targetChunkBytes"/> 或 <paramref name="maxRows"/> 即切块（至少 1 行）。
    /// </summary>
    public static int SelectChunkRowCount(
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int start,
        int targetChunkBytes = DefaultTargetChunkBytes,
        int maxRows = DefaultMaxChunkRows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(start, rows.Count);

        long bytes = 0;
        int count = 0;
        while (count < maxRows && start + count < rows.Count)
        {
            bytes += EstimateRowBytes(rows[start + count]);
            count++;
            if (bytes >= targetChunkBytes)
                break;
        }
        return Math.Max(count, 1);
    }

    private static long EstimateRowBytes(IReadOnlyList<object?> row)
    {
        long bytes = 2;
        for (int i = 0; i < row.Count; i++)
        {
            bytes += row[i] switch
            {
                null => 1,
                string s => s.Length + 3,
                byte[] b => b.Length + 5,
                float[] f => 4L * f.Length + 5,
                bool => 2,
                GeoPoint => 17,
                _ => 9,
            };
        }
        return bytes;
    }

    // ────────────────────────────── 列式编码内部 ──────────────────────────────

    private struct ColumnPlan
    {
        public byte Kind;       // SqlValueKind（0 = 全 null）或 VariantColumnKind
        public bool HasNulls;
        public long Bytes;      // 该列编码后的总字节数（含 kind 字节）
    }

    private static ColumnPlan PlanColumn(IReadOnlyList<IReadOnlyList<object?>> rows, int start, int count, int col)
    {
        byte kind = (byte)SqlValueKind.Null;
        bool variant = false;
        int presentCount = 0;
        for (int r = start; r < start + count; r++)
        {
            object? value = rows[r][col];
            if (value is null)
                continue;
            presentCount++;
            byte k = (byte)ClassifyValue(value);
            if (kind == (byte)SqlValueKind.Null)
                kind = k;
            else if (kind != k)
                variant = true;
        }

        var plan = new ColumnPlan { HasNulls = presentCount < count };
        if (presentCount == 0)
        {
            plan.Kind = (byte)SqlValueKind.Null;
            plan.Bytes = 1;
            return plan;
        }

        if (variant)
        {
            plan.Kind = VariantColumnKind;
            long bytes = 1 + count; // kind 字节 + 每行 1 字节 tag
            for (int r = start; r < start + count; r++)
            {
                object? value = rows[r][col];
                if (value is not null)
                    bytes += MeasureValue(ClassifyValue(value), value);
            }
            plan.Bytes = bytes;
            return plan;
        }

        plan.Kind = kind;
        long typedBytes = 1 + 1; // kind + hasNulls
        if (plan.HasNulls)
            typedBytes += (count + 7) >> 3;
        var valueKind = (SqlValueKind)kind;
        for (int r = start; r < start + count; r++)
        {
            object? value = rows[r][col];
            if (value is not null)
                typedBytes += MeasureValue(valueKind, value);
        }
        plan.Bytes = typedBytes;
        return plan;
    }

    private static void WriteColumn(
        ref SpanWriter writer,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int start,
        int count,
        int col,
        in ColumnPlan plan)
    {
        writer.WriteByte(plan.Kind);
        if (plan.Kind == (byte)SqlValueKind.Null)
            return;

        if (plan.Kind == VariantColumnKind)
        {
            for (int r = start; r < start + count; r++)
            {
                object? value = rows[r][col];
                if (value is null)
                {
                    writer.WriteByte((byte)SqlValueKind.Null);
                    continue;
                }
                SqlValueKind k = ClassifyValue(value);
                writer.WriteByte((byte)k);
                WriteValue(ref writer, k, value);
            }
            return;
        }

        writer.WriteByte(plan.HasNulls ? (byte)1 : (byte)0);
        if (plan.HasNulls)
        {
            int bitmapLength = (count + 7) >> 3;
            for (int byteIndex = 0; byteIndex < bitmapLength; byteIndex++)
            {
                int baseRow = byteIndex << 3;
                int bits = Math.Min(8, count - baseRow);
                byte b = 0;
                for (int i = 0; i < bits; i++)
                {
                    if (rows[start + baseRow + i][col] is not null)
                        b |= (byte)(1 << i);
                }
                writer.WriteByte(b);
            }
        }

        var kind = (SqlValueKind)plan.Kind;
        for (int r = start; r < start + count; r++)
        {
            object? value = rows[r][col];
            if (value is not null)
                WriteValue(ref writer, kind, value);
        }
    }

    /// <summary>
    /// 值类型归类（与 <c>NdjsonRowWriter</c> 的覆盖面对齐）：整型族 → Int64，浮点族 → Float64，
    /// <see cref="Guid"/> 与未识别类型 → String（ToString 回退）。整型与浮点混列不合并（走 variant），
    /// 避免大 long → double 的精度损失。
    /// </summary>
    private static SqlValueKind ClassifyValue(object value) => value switch
    {
        bool => SqlValueKind.Boolean,
        byte or sbyte or short or ushort or int or uint or long => SqlValueKind.Int64,
        ulong u => u <= long.MaxValue ? SqlValueKind.Int64 : SqlValueKind.Float64,
        float or double or decimal => SqlValueKind.Float64,
        string => SqlValueKind.String,
        DateTime or DateTimeOffset => SqlValueKind.Timestamp,
        byte[] => SqlValueKind.Bytes,
        float[] => SqlValueKind.Vector,
        GeoPoint => SqlValueKind.GeoPoint,
        _ => SqlValueKind.String,
    };

    private static long MeasureValue(SqlValueKind kind, object value) => kind switch
    {
        SqlValueKind.Int64 or SqlValueKind.Float64 or SqlValueKind.Timestamp => 8,
        SqlValueKind.Boolean => 1,
        SqlValueKind.GeoPoint => 16,
        SqlValueKind.String => SpanWriter.MeasureVarString(value as string ?? value.ToString() ?? string.Empty),
        SqlValueKind.Bytes => SpanWriter.MeasureVarUInt32((uint)((byte[])value).Length) + ((byte[])value).Length,
        SqlValueKind.Vector => SpanWriter.MeasureVarUInt32((uint)((float[])value).Length) + 4L * ((float[])value).Length,
        _ => throw new ArgumentException($"不支持的值类型标记 {kind}。"),
    };

    private static void WriteValue(ref SpanWriter writer, SqlValueKind kind, object value)
    {
        switch (kind)
        {
            case SqlValueKind.Int64:
                writer.WriteInt64(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
            case SqlValueKind.Float64:
                writer.WriteDouble(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
            case SqlValueKind.Boolean:
                writer.WriteByte((bool)value ? (byte)1 : (byte)0);
                break;
            case SqlValueKind.String:
                writer.WriteVarString(value as string ?? value.ToString() ?? string.Empty);
                break;
            case SqlValueKind.Bytes:
            {
                var bytes = (byte[])value;
                writer.WriteVarUInt32((uint)bytes.Length);
                writer.WriteBytes(bytes);
                break;
            }
            case SqlValueKind.Timestamp:
                writer.WriteInt64(ToUtcTicks(value));
                break;
            case SqlValueKind.Vector:
            {
                var vector = (float[])value;
                writer.WriteVarUInt32((uint)vector.Length);
                writer.WriteStructs<float>(vector);
                break;
            }
            case SqlValueKind.GeoPoint:
            {
                var geo = (GeoPoint)value;
                writer.WriteDouble(geo.Lat);
                writer.WriteDouble(geo.Lon);
                break;
            }
            default:
                throw new ArgumentException($"不支持的值类型标记 {kind}。");
        }
    }

    private static object ReadValue(ref SpanReader reader, byte tag)
    {
        switch ((SqlValueKind)tag)
        {
            case SqlValueKind.Int64:
                return reader.ReadInt64();
            case SqlValueKind.Float64:
                return reader.ReadDouble();
            case SqlValueKind.Boolean:
                return ReadBooleanByte(ref reader);
            case SqlValueKind.String:
                return reader.ReadVarString();
            case SqlValueKind.Bytes:
            {
                uint length = reader.ReadVarUInt32();
                if (length > (uint)reader.Remaining)
                    throw new FrameFormatException($"Bytes 值长度 {length} 超出帧体剩余长度。");
                return reader.ReadBytes((int)length).ToArray();
            }
            case SqlValueKind.Timestamp:
            {
                long ticks = reader.ReadInt64();
                if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                    throw new FrameFormatException($"Timestamp ticks {ticks} 超出 DateTime 范围。");
                return new DateTime(ticks, DateTimeKind.Utc);
            }
            case SqlValueKind.Vector:
            {
                uint dim = reader.ReadVarUInt32();
                if (4L * dim > reader.Remaining)
                    throw new FrameFormatException($"Vector 维度 {dim} 超出帧体剩余长度。");
                return reader.ReadStructs<float>((int)dim).ToArray();
            }
            case SqlValueKind.GeoPoint:
            {
                double lat = reader.ReadDouble();
                double lon = reader.ReadDouble();
                return new GeoPoint(lat, lon);
            }
            default:
                throw new FrameFormatException($"值类型标记 {tag} 非法。");
        }
    }

    private static long ToUtcTicks(object value) => value switch
    {
        DateTimeOffset dto => dto.UtcTicks,
        DateTime { Kind: DateTimeKind.Utc } dt => dt.Ticks,
        DateTime { Kind: DateTimeKind.Local } dt => dt.ToUniversalTime().Ticks,
        DateTime dt => dt.Ticks, // Unspecified 按 UTC 处理（与 TableKeyCodec 对齐）
        _ => throw new ArgumentException($"不支持的时间类型 {value.GetType().Name}。"),
    };

    // ────────────────────────────── 共享辅助 ──────────────────────────────

    /// <summary>
    /// 读取响应帧体首字节的块类型（meta/rows/end），供客户端分发到对应解码器。
    /// </summary>
    public static SqlQueryChunkKind PeekChunkKind(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
            throw new FrameFormatException("sql 响应帧体为空。");
        byte kind = payload[0];
        if (kind is < (byte)SqlQueryChunkKind.Meta or > (byte)SqlQueryChunkKind.End)
            throw new FrameFormatException($"sql 响应帧块类型 {kind} 非法（1=meta / 2=rows / 3=end）。");
        return (SqlQueryChunkKind)kind;
    }

    private static void RequireChunkKind(ref SpanReader reader, SqlQueryChunkKind expected)
    {
        byte kind = reader.ReadByte();
        if (kind != (byte)expected)
            throw new FrameFormatException($"期望块类型 {(byte)expected}（{expected}），实际 {kind}。");
    }

    private static void RequireValueKind(byte kind)
    {
        if (kind is < (byte)SqlValueKind.Int64 or > (byte)SqlValueKind.GeoPoint)
            throw new FrameFormatException($"列值类型标记 {kind} 非法。");
    }

    private static bool ReadBooleanByte(ref SpanReader reader)
    {
        byte b = reader.ReadByte();
        return b switch
        {
            0 => false,
            1 => true,
            _ => throw new FrameFormatException($"Boolean 值 {b} 非法（0/1）。"),
        };
    }

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
/// sql 响应帧的块类型（帧体首字节）。同一 streamId 的响应流 = meta → rows × N → end。
/// </summary>
public enum SqlQueryChunkKind : byte
{
    /// <summary>列名元数据（首帧）。</summary>
    Meta = 1,

    /// <summary>列式行块。</summary>
    Rows = 2,

    /// <summary>结束统计（末帧）：总行数 + 耗时。</summary>
    End = 3,
}

/// <summary>
/// 结果集单元格的值类型标记（列级或 variant 值级）。
/// </summary>
public enum SqlValueKind : byte
{
    /// <summary>null（列级 = 全 null 列；variant 值级 = 单元格 null）。</summary>
    Null = 0,

    /// <summary>64 位有符号整数（i64 LE）。</summary>
    Int64 = 1,

    /// <summary>64 位双精度浮点（f64 LE）。</summary>
    Float64 = 2,

    /// <summary>布尔（u8 0/1）。</summary>
    Boolean = 3,

    /// <summary>UTF-8 字符串（varstr）。</summary>
    String = 4,

    /// <summary>原始字节（varuint 长度 + 字节，零 Base64）。</summary>
    Bytes = 5,

    /// <summary>UTC 时间（i64 LE UtcTicks，解码为 <see cref="DateTime"/> UTC）。</summary>
    Timestamp = 6,

    /// <summary>float 向量（varuint 维度 + f32 LE × 维度）。</summary>
    Vector = 7,

    /// <summary>地理点（f64 lat + f64 lon）。</summary>
    GeoPoint = 8,
}

/// <summary>
/// sql query 请求帧解码结果。
/// </summary>
/// <param name="Db">数据库名。</param>
/// <param name="Sql">SQL 文本。</param>
/// <param name="Parameters">命名标量参数（无参数时为 null），经 <see cref="SqlParameterBinder"/> 绑定。</param>
public sealed record SqlQueryFrameRequest(string Db, string Sql, SqlParameters? Parameters);
