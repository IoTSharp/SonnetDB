using SonnetDB.Ingest;
using SonnetDB.Model;
using SonnetDB.Storage.Format;

namespace SonnetDB.Protocol;

/// <summary>
/// 把一批 <see cref="Point"/>（行式）聚合为 <see cref="TsdbColumnarBlock"/> 列式块序列（M28 P5b #261）。
/// 是 <see cref="TsdbColumnarPointReader"/> 的逆过程：按 tag 组（同一序列族）分块，块内每个字段一列，
/// 缺值行以 presence 位图标记稀疏。客户端 SDK 把 Line Protocol / JSON 解析出的点集经此编码为
/// <see cref="TsdbFrameCodec.EncodeWriteColumnarRequest"/> 的列式写帧。
/// </summary>
/// <remarks>
/// <para>分块规则：tag 键值对完全相同的点归入同一块，块内行按传入顺序排列（时间戳列随行排布）。</para>
/// <para>一个字段列只能承载单一 <see cref="FieldType"/>；若同名字段在同一块内出现不同类型（或向量维度
/// 不一致），抛 <see cref="BulkIngestException"/>——调用方据此回落 REST 让服务端按 schema 权威处理。</para>
/// </remarks>
public static class TsdbColumnarBlockBuilder
{
    /// <summary>
    /// 把 <paramref name="points"/> 聚合为列式块序列。所有点应属于同一 measurement（本类不校验，
    /// measurement 由帧头单独携带）。
    /// </summary>
    /// <param name="points">行式点集（每个点至少含一个字段）。</param>
    /// <returns>列式块序列（每块 ≥1 行、≥1 列）；<paramref name="points"/> 为空时返回空列表。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="points"/> 为 null。</exception>
    /// <exception cref="BulkIngestException">同名字段在同一块内类型冲突或向量维度不一致。</exception>
    public static IReadOnlyList<TsdbColumnarBlock> Build(IReadOnlyList<Point> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            return [];

        var blocks = new List<BlockAccumulator>();
        var index = new Dictionary<IReadOnlyDictionary<string, string>, int>(TagSetComparer.Instance);

        foreach (Point point in points)
        {
            IReadOnlyDictionary<string, string> tags = point.Tags;
            if (!index.TryGetValue(tags, out int blockIndex))
            {
                blockIndex = blocks.Count;
                blocks.Add(new BlockAccumulator(tags));
                index[tags] = blockIndex;
            }

            blocks[blockIndex].AddRow(point);
        }

        var result = new TsdbColumnarBlock[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
            result[i] = blocks[i].Build();
        return result;
    }

    private sealed class BlockAccumulator
    {
        private readonly IReadOnlyDictionary<string, string> _tags;
        private readonly List<long> _timestamps = [];
        private readonly List<ColumnAccumulator> _columns = [];
        private readonly Dictionary<string, int> _columnIndex = new(StringComparer.Ordinal);

        public BlockAccumulator(IReadOnlyDictionary<string, string> tags) => _tags = tags;

        public void AddRow(Point point)
        {
            int rowIndex = _timestamps.Count;
            _timestamps.Add(point.Timestamp);

            foreach (KeyValuePair<string, FieldValue> field in point.Fields)
            {
                if (!_columnIndex.TryGetValue(field.Key, out int col))
                {
                    col = _columns.Count;
                    _columns.Add(new ColumnAccumulator(field.Key, field.Value.Type));
                    _columnIndex[field.Key] = col;
                }

                _columns[col].Add(rowIndex, field.Value);
            }
        }

        public TsdbColumnarBlock Build()
        {
            int rowCount = _timestamps.Count;
            long[] timestamps = _timestamps.ToArray();
            var columns = new TsdbColumnarColumn[_columns.Count];
            for (int i = 0; i < _columns.Count; i++)
                columns[i] = _columns[i].Build(rowCount);

            // 无 tag 时传 null（与 TsdbColumnarBlock 语义一致，避免空字典占位）。
            IReadOnlyDictionary<string, string>? tags = _tags.Count == 0 ? null : _tags;
            return new TsdbColumnarBlock(tags, timestamps, columns);
        }
    }

    private sealed class ColumnAccumulator
    {
        private readonly string _name;
        private readonly FieldType _type;
        private readonly List<int> _presentRows = [];
        private readonly List<FieldValue> _values = [];
        private int _vectorDim = -1;

        public ColumnAccumulator(string name, FieldType type)
        {
            _name = name;
            _type = type;
        }

        public void Add(int rowIndex, FieldValue value)
        {
            if (value.Type != _type)
                throw new BulkIngestException(
                    $"字段列 '{_name}' 在同一序列内出现类型冲突（{_type} vs {value.Type}）——列式帧要求单一类型。");

            if (_type == FieldType.Vector)
            {
                int dim = value.VectorDimension;
                if (_vectorDim < 0)
                    _vectorDim = dim;
                else if (_vectorDim != dim)
                    throw new BulkIngestException(
                        $"向量字段列 '{_name}' 维度不一致（{_vectorDim} vs {dim}）。");
            }

            _presentRows.Add(rowIndex);
            _values.Add(value);
        }

        public TsdbColumnarColumn Build(int rowCount)
        {
            int present = _values.Count;

            // present == rowCount 且行索引严格递增 ⟺ 每行都有值 → 稠密（presence 为空）。
            ReadOnlyMemory<bool> presence = default;
            if (present != rowCount)
            {
                var bitmap = new bool[rowCount];
                foreach (int row in _presentRows)
                    bitmap[row] = true;
                presence = bitmap;
            }

            switch (_type)
            {
                case FieldType.Float64:
                {
                    var values = new double[present];
                    for (int i = 0; i < present; i++)
                        values[i] = _values[i].AsDouble();
                    return TsdbColumnarColumn.Float64(_name, values, presence);
                }
                case FieldType.Int64:
                {
                    var values = new long[present];
                    for (int i = 0; i < present; i++)
                        values[i] = _values[i].AsLong();
                    return TsdbColumnarColumn.Int64(_name, values, presence);
                }
                case FieldType.Boolean:
                {
                    var values = new bool[present];
                    for (int i = 0; i < present; i++)
                        values[i] = _values[i].AsBool();
                    return TsdbColumnarColumn.Boolean(_name, values, presence);
                }
                case FieldType.String:
                {
                    var values = new string[present];
                    for (int i = 0; i < present; i++)
                        values[i] = _values[i].AsString();
                    return TsdbColumnarColumn.String(_name, values, presence);
                }
                case FieldType.Vector:
                {
                    int dim = _vectorDim;
                    var values = new float[checked(dim * present)];
                    for (int i = 0; i < present; i++)
                        _values[i].AsVector().Span.CopyTo(values.AsSpan(i * dim, dim));
                    return TsdbColumnarColumn.Vector(_name, dim, values, presence);
                }
                case FieldType.GeoPoint:
                {
                    var values = new GeoPoint[present];
                    for (int i = 0; i < present; i++)
                        values[i] = _values[i].AsGeoPoint();
                    return TsdbColumnarColumn.GeoPoint(_name, values, presence);
                }
                default:
                    throw new BulkIngestException($"字段列 '{_name}' 的类型 {_type} 不支持列式编码。");
            }
        }
    }

    private sealed class TagSetComparer : IEqualityComparer<IReadOnlyDictionary<string, string>>
    {
        public static readonly TagSetComparer Instance = new();

        public bool Equals(IReadOnlyDictionary<string, string>? x, IReadOnlyDictionary<string, string>? y)
        {
            if (ReferenceEquals(x, y)) return true;
            int cx = x?.Count ?? 0;
            int cy = y?.Count ?? 0;
            if (cx != cy) return false;
            if (cx == 0) return true;
            foreach (KeyValuePair<string, string> pair in x!)
            {
                if (!y!.TryGetValue(pair.Key, out string? value) || !string.Equals(pair.Value, value, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        public int GetHashCode(IReadOnlyDictionary<string, string> obj)
        {
            if (obj is null || obj.Count == 0) return 0;
            // 顺序无关的异或组合——保证同一组 tag 不同迭代顺序哈希一致。
            int hash = 0;
            foreach (KeyValuePair<string, string> pair in obj)
                hash ^= HashCode.Combine(pair.Key, pair.Value);
            return hash;
        }
    }
}
