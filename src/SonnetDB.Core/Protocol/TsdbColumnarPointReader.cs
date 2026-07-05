using System.Buffers.Binary;
using SonnetDB.Ingest;
using SonnetDB.IO;
using SonnetDB.Model;
using SonnetDB.Storage.Format;

namespace SonnetDB.Protocol;

/// <summary>
/// 列式写请求帧体的流式列转行 reader（M28 P5b #237）：逐块解码
/// <see cref="TsdbWriteColumnarFrameRequest.Blocks"/>，按行产出 <see cref="Point"/> 直通
/// <see cref="BulkIngestor"/>（复用 P0/P2 已硬化的 <c>WriteMany</c> 背压路径）。
/// 名称/时间戳校验按块做一次（块内所有行共享 tag 与字段名），行内只做取值与装配。
/// 输入缓冲须在 reader 消费期内存活（服务端在 PipeReader AdvanceTo 之前同步完成 Ingest）。
/// 结构性解码失败抛 <see cref="FrameFormatException"/>（不可按行恢复）；
/// 行级语义错误（时间戳为负、整行无字段）抛 <see cref="BulkIngestException"/>（游标已推进，可跳过）。
/// </summary>
public sealed class TsdbColumnarPointReader : IPointReader
{
    private readonly ReadOnlyMemory<byte> _blocks;
    private readonly string _measurement;
    private readonly int _blockCount;

    private int _position;
    private int _blocksRead;

    // ── 当前块状态 ──────────────────────────────────────────────────────────
    private IReadOnlyDictionary<string, string> _tags = EmptyDictionary<string, string>.Instance;
    private int _rowCount;
    private int _rowIndex;
    private int _timestampsOffset;
    private ColumnState[] _columns = [];
    private int _columnCount;

    /// <summary>
    /// 从解码后的请求头构造 reader。
    /// </summary>
    /// <param name="request">列式写请求帧解码结果。</param>
    public TsdbColumnarPointReader(in TsdbWriteColumnarFrameRequest request)
    {
        PointValidation.ValidateMeasurement(request.Measurement);
        _blocks = request.Blocks;
        _measurement = request.Measurement;
        _blockCount = request.BlockCount;
    }

    /// <inheritdoc />
    public bool TryRead(out Point point)
    {
        while (_rowIndex >= _rowCount)
        {
            if (_blocksRead >= _blockCount)
            {
                if (_position != _blocks.Length)
                    throw new FrameFormatException($"列式帧体在 {_blockCount} 个块之后仍有 {_blocks.Length - _position} 字节残留。");
                point = null!;
                return false;
            }

            DecodeBlockHeader();
        }

        int row = _rowIndex++;
        long timestamp = BinaryPrimitives.ReadInt64LittleEndian(
            _blocks.Span.Slice(_timestampsOffset + row * 8, 8));

        var fields = new Dictionary<string, FieldValue>(_columnCount, StringComparer.Ordinal);
        for (int i = 0; i < _columnCount; i++)
        {
            ref ColumnState column = ref _columns[i];
            if (!column.IsPresent(_blocks.Span, row))
                continue;
            fields[column.Name] = column.ReadNextValue(_blocks.Span);
        }

        if (fields.Count == 0)
            throw new BulkIngestException($"块 {_blocksRead} 第 {row} 行无任何字段值。");
        if (timestamp < 0)
            throw new BulkIngestException($"块 {_blocksRead} 第 {row} 行时间戳 {timestamp} 为负。");

        // 名称已按块整体校验（ValidateBlockNames），此处直接装配，绕开 Point.Create 的逐行重复校验。
        point = new Point
        {
            Measurement = _measurement,
            Tags = _tags,
            Fields = fields,
            Timestamp = timestamp,
        };
        return true;
    }

    // ────────────────────────────── 块解码 ──────────────────────────────

    private void DecodeBlockHeader()
    {
        var reader = new SpanReader(_blocks.Span[_position..]);

        uint tagCount = reader.ReadVarUInt32();
        if (tagCount > TsdbFrameCodec.MaxTagCount)
            throw new FrameFormatException($"tag 数 {tagCount} 超过上限 {TsdbFrameCodec.MaxTagCount}。");
        if (tagCount == 0)
        {
            _tags = EmptyDictionary<string, string>.Instance;
        }
        else
        {
            var tags = new Dictionary<string, string>((int)tagCount, StringComparer.Ordinal);
            for (uint i = 0; i < tagCount; i++)
            {
                string key = TsdbFrameCodec.ReadName(ref reader, "tag key");
                string value = TsdbFrameCodec.ReadName(ref reader, "tag value");
                ValidateBlockName(key, "tag key");
                ValidateBlockName(value, "tag value");
                tags[key] = value;
            }
            _tags = tags;
        }

        uint rowCount = reader.ReadVarUInt32();
        if (rowCount == 0)
            throw new FrameFormatException($"块 {_blocksRead} 的行数为 0。");
        if (rowCount > (uint)(reader.Remaining / 8))
            throw new FrameFormatException($"块 {_blocksRead} 声明行数 {rowCount} 超出帧体剩余长度。");
        _rowCount = (int)rowCount;
        _rowIndex = 0;
        _timestampsOffset = _position + reader.Position;
        reader.Skip(_rowCount * 8);

        uint columnCount = reader.ReadVarUInt32();
        if (columnCount == 0)
            throw new FrameFormatException($"块 {_blocksRead} 无字段列。");
        if (columnCount > (uint)reader.Remaining)
            throw new FrameFormatException($"块 {_blocksRead} 声明列数 {columnCount} 超出帧体剩余长度。");
        _columnCount = (int)columnCount;
        if (_columns.Length < _columnCount)
            _columns = new ColumnState[_columnCount];

        for (int i = 0; i < _columnCount; i++)
            DecodeColumnHeader(ref reader, ref _columns[i]);

        _position += reader.Position;
        _blocksRead++;
    }

    private void DecodeColumnHeader(ref SpanReader reader, ref ColumnState column)
    {
        string name = TsdbFrameCodec.ReadName(ref reader, "field name");
        ValidateBlockName(name, "field key");
        byte typeRaw = reader.ReadByte();
        if (typeRaw is < (byte)FieldType.Float64 or > (byte)FieldType.GeoPoint)
            throw new FrameFormatException($"字段列 '{name}' 类型 {typeRaw} 非法。");
        var type = (FieldType)typeRaw;
        byte sparse = reader.ReadByte();
        if (sparse > 1)
            throw new FrameFormatException($"字段列 '{name}' 的 presence 标志 {sparse} 非法。");

        int presenceOffset = -1;
        int present = _rowCount;
        if (sparse == 1)
        {
            int bitmapLength = (_rowCount + 7) >> 3;
            reader.EnsureRemaining(bitmapLength);
            presenceOffset = _position + reader.Position;
            present = CountPresentBits(reader.ReadBytes(bitmapLength), _rowCount);
        }

        int vectorDim = 0;
        if (type == FieldType.Vector)
        {
            uint dim = reader.ReadVarUInt32();
            if (dim is 0 or > int.MaxValue / 4)
                throw new FrameFormatException($"字段列 '{name}' 向量维度 {dim} 非法。");
            vectorDim = (int)dim;
        }

        int valuesOffset = _position + reader.Position;
        string[]? strings = null;
        switch (type)
        {
            // present ≤ rowCount ≤ remaining/8 ≤ 132MiB/8，16×present 不会溢出 int。
            case FieldType.Float64:
            case FieldType.Int64:
                reader.Skip(8 * present);
                break;
            case FieldType.Boolean:
                reader.Skip(present);
                break;
            case FieldType.String:
                strings = new string[present];
                for (int i = 0; i < present; i++)
                    strings[i] = ReadBoundedVarString(ref reader, name);
                break;
            case FieldType.Vector:
            {
                long byteLength = 4L * vectorDim * present;
                if (byteLength > reader.Remaining)
                    throw new FrameFormatException($"字段列 '{name}' 的向量数据长度 {byteLength} 超出帧体剩余长度。");
                reader.Skip((int)byteLength);
                break;
            }
            case FieldType.GeoPoint:
                reader.Skip(16 * present);
                break;
        }

        column = new ColumnState(name, type, presenceOffset, valuesOffset, vectorDim, strings);
    }

    private static string ReadBoundedVarString(ref SpanReader reader, string columnName)
    {
        uint length = reader.ReadVarUInt32();
        if (length > (uint)reader.Remaining)
            throw new FrameFormatException($"字段列 '{columnName}' 的字符串值长度 {length} 超出帧体剩余长度。");
        if (length == 0)
            return string.Empty;
        return System.Text.Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    private static int CountPresentBits(ReadOnlySpan<byte> bitmap, int rowCount)
    {
        int count = 0;
        for (int row = 0; row < rowCount; row++)
        {
            if ((bitmap[row >> 3] & (1 << (row & 7))) != 0)
                count++;
        }
        return count;
    }

    private static void ValidateBlockName(string name, string kind)
    {
        try
        {
            PointValidation.ValidateName(name, kind);
        }
        catch (ArgumentException ex)
        {
            throw new FrameFormatException(ex.Message);
        }
    }

    /// <summary>
    /// 单个字段列的解码游标：presence 判定按行号读位图，取值按 present 序号推进。
    /// </summary>
    private struct ColumnState(string name, FieldType type, int presenceOffset, int valuesOffset, int vectorDim, string[]? strings)
    {
        public readonly string Name = name;
        private readonly FieldType _type = type;
        private readonly int _presenceOffset = presenceOffset;
        private readonly int _valuesOffset = valuesOffset;
        private readonly int _vectorDim = vectorDim;
        private readonly string[]? _strings = strings;
        private int _cursor;

        public readonly bool IsPresent(ReadOnlySpan<byte> buffer, int row)
        {
            if (_presenceOffset < 0)
                return true;
            return (buffer[_presenceOffset + (row >> 3)] & (1 << (row & 7))) != 0;
        }

        public FieldValue ReadNextValue(ReadOnlySpan<byte> buffer)
        {
            int index = _cursor++;
            switch (_type)
            {
                case FieldType.Float64:
                    return FieldValue.FromDouble(BinaryPrimitives.ReadDoubleLittleEndian(
                        buffer.Slice(_valuesOffset + index * 8, 8)));
                case FieldType.Int64:
                    return FieldValue.FromLong(BinaryPrimitives.ReadInt64LittleEndian(
                        buffer.Slice(_valuesOffset + index * 8, 8)));
                case FieldType.Boolean:
                    return FieldValue.FromBool(buffer[_valuesOffset + index] != 0);
                case FieldType.String:
                    return FieldValue.FromString(_strings![index]);
                case FieldType.Vector:
                {
                    // 输入缓冲是瞬态的（AdvanceTo 后失效），而 FieldValue 的向量随 Point 进入 MemTable
                    // 长期存活，必须落到自有 float[]。总量已在头部解码时按帧体剩余长度校验，偏移不会越界。
                    var vector = new float[_vectorDim];
                    ReadOnlySpan<byte> source = buffer.Slice(_valuesOffset + index * _vectorDim * 4, _vectorDim * 4);
                    for (int i = 0; i < vector.Length; i++)
                        vector[i] = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(i * 4, 4));
                    return FieldValue.FromVector(vector);
                }
                case FieldType.GeoPoint:
                {
                    ReadOnlySpan<byte> source = buffer.Slice(_valuesOffset + index * 16, 16);
                    double lat = BinaryPrimitives.ReadDoubleLittleEndian(source);
                    double lon = BinaryPrimitives.ReadDoubleLittleEndian(source[8..]);
                    return FieldValue.FromGeoPoint(lat, lon);
                }
                default:
                    throw new FrameFormatException($"字段列 '{Name}' 类型 {_type} 非法。");
            }
        }
    }
}
