using System.Buffers;
using System.Threading;
using SonnetDB.Model;
using SonnetDB.Storage.Format;

namespace SonnetDB.Memory;

/// <summary>
/// MemTable 的单桶：固定 (SeriesId, FieldName, FieldType) 下的有序 DataPoint 列表。
/// 内部以追加为主，有序性通过"插入时合并 + 一次性排序快照"双路径保证。
/// 单调递增写入时保持 <c>_isSorted = true</c>，出现乱序时在 <see cref="Snapshot"/> 时懒排序。
/// </summary>
public sealed class MemTableSeries
{
    private readonly object _sync = new();
    private readonly List<DataPoint> _points = [];
    private bool _isSorted = true;
    private long _lastTimestamp = long.MinValue;
    private long _minTimestamp = long.MaxValue;
    private long _maxTimestamp = long.MinValue;
    private long _estimatedBytes;
    private long _version;
    private SnapshotCache? _snapshotCache;
    // 数值字段的运行期增量聚合（Float64/Int64/Boolean），用于范围全覆盖时的查询快路径。
    // 非数值字段始终保持 _hasNumericAggregates = false。
    private bool _hasNumericAggregates;
    private double _numericSum;
    private double _numericMin = double.PositiveInfinity;
    private double _numericMax = double.NegativeInfinity;

    /// <summary>该桶的 (SeriesId, FieldName) 复合键。</summary>
    public SeriesFieldKey Key { get; }

    /// <summary>该桶的字段类型（创建时固定）。</summary>
    public FieldType FieldType { get; }

    /// <summary>桶内当前数据点数量。</summary>
    public int Count
    {
        get
        {
            lock (_sync)
                return _points.Count;
        }
    }

    /// <summary>桶内最小时间戳；若桶为空则返回 <see cref="long.MaxValue"/>。</summary>
    public long MinTimestamp
    {
        get
        {
            lock (_sync)
                return _minTimestamp;
        }
    }

    /// <summary>桶内最大时间戳；若桶为空则返回 <see cref="long.MinValue"/>。</summary>
    public long MaxTimestamp
    {
        get
        {
            lock (_sync)
                return _maxTimestamp;
        }
    }

    /// <summary>
    /// 估算的内存占用（字节），用于 MemTable 阈值判定。
    /// </summary>
    /// <remarks>
    /// Double/Long ≈ 16 bytes/point；Bool ≈ 9 bytes/point；
    /// String ≈ 8 + UTF-8 byte count + 8 overhead。
    /// </remarks>
    public long EstimatedBytes
    {
        get
        {
            lock (_sync)
                return _estimatedBytes;
        }
    }

    /// <summary>
    /// 创建一个新的数据点桶。
    /// </summary>
    /// <param name="key">桶的 (SeriesId, FieldName) 复合键。</param>
    /// <param name="fieldType">该桶的字段类型。</param>
    internal MemTableSeries(SeriesFieldKey key, FieldType fieldType)
    {
        Key = key;
        FieldType = fieldType;
        _hasNumericAggregates = fieldType is FieldType.Float64 or FieldType.Int64 or FieldType.Boolean;
    }

    /// <summary>
    /// 是否为数值字段（Float64 / Int64 / Boolean），仅在该字段类型下维护运行期 sum/min/max。
    /// </summary>
    public bool HasNumericAggregates => _hasNumericAggregates;

    /// <summary>
    /// 数值字段累积 Sum 快照（仅当 <see cref="HasNumericAggregates"/> 且 <see cref="Count"/> &gt; 0 时有意义）。
    /// </summary>
    public double NumericSum
    {
        get { lock (_sync) return _numericSum; }
    }

    /// <summary>
    /// 数值字段累积 Min 快照（仅当 <see cref="HasNumericAggregates"/> 且 <see cref="Count"/> &gt; 0 时有意义）。
    /// </summary>
    public double NumericMin
    {
        get { lock (_sync) return _numericMin; }
    }

    /// <summary>
    /// 数值字段累积 Max 快照（仅当 <see cref="HasNumericAggregates"/> 且 <see cref="Count"/> &gt; 0 时有意义）。
    /// </summary>
    public double NumericMax
    {
        get { lock (_sync) return _numericMax; }
    }

    /// <summary>
    /// 一次性原子读取范围全覆盖时所需的全部聚合状态：count / sum / min / max / 时间窗。
    /// 仅当 <see cref="HasNumericAggregates"/> 为 true 且 <paramref name="count"/> &gt; 0 时返回的聚合值有意义。
    /// </summary>
    /// <param name="count">桶内数据点总数。</param>
    /// <param name="minTs">桶内最小时间戳。</param>
    /// <param name="maxTs">桶内最大时间戳。</param>
    /// <param name="sum">数值累积 Sum。</param>
    /// <param name="min">数值累积 Min。</param>
    /// <param name="max">数值累积 Max。</param>
    /// <returns>是否提供有效的 sum/min/max（true 表示数值字段且非空）。</returns>
    public bool TryGetNumericAggregateSnapshot(
        out int count, out long minTs, out long maxTs,
        out double sum, out double min, out double max)
    {
        lock (_sync)
        {
            count = _points.Count;
            minTs = _minTimestamp;
            maxTs = _maxTimestamp;
            sum = _numericSum;
            min = _numericMin;
            max = _numericMax;
            return _hasNumericAggregates && count > 0;
        }
    }

    /// <summary>
    /// 追加一个数据点。线程安全（内部加锁）。
    /// </summary>
    /// <param name="timestamp">数据点时间戳（Unix 毫秒）。</param>
    /// <param name="value">字段值，类型必须与桶的 <see cref="FieldType"/> 一致。</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> 的类型与桶的 <see cref="FieldType"/> 不匹配时抛出。
    /// </exception>
    public void Append(long timestamp, FieldValue value)
        => _ = AppendAndEstimateBytes(timestamp, value);

    internal long AppendAndEstimateBytes(long timestamp, FieldValue value)
    {
        if (value.Type != FieldType)
            throw new ArgumentException(
                $"FieldValue type mismatch: expected {FieldType}, got {value.Type}.", nameof(value));

        lock (_sync)
        {
            long addedBytes = EstimatePointBytes(FieldType, value);

            if (timestamp < _lastTimestamp)
                _isSorted = false;

            _lastTimestamp = timestamp;

            if (timestamp < _minTimestamp)
                _minTimestamp = timestamp;
            if (timestamp > _maxTimestamp)
                _maxTimestamp = timestamp;

            _estimatedBytes += addedBytes;

            if (_hasNumericAggregates)
            {
                double v = FieldType switch
                {
                    FieldType.Float64 => value.AsDouble(),
                    FieldType.Int64 => value.AsLong(),
                    FieldType.Boolean => value.AsBool() ? 1.0 : 0.0,
                    _ => 0.0,
                };
                _numericSum += v;
                if (v < _numericMin) _numericMin = v;
                if (v > _numericMax) _numericMax = v;
            }

            _points.Add(new DataPoint(timestamp, value));
            _version++;
            InvalidateSnapshotCache();
            return addedBytes;
        }
    }

    /// <summary>
    /// 估算单个数据点追加到当前桶后新增的内存占用。
    /// </summary>
    /// <param name="fieldType">字段类型。</param>
    /// <param name="value">字段值。</param>
    /// <returns>估算新增字节数。</returns>
    internal static long EstimatePointBytes(FieldType fieldType, in FieldValue value)
        => fieldType switch
        {
            FieldType.Boolean => 9L,
            FieldType.String => 16L + System.Text.Encoding.UTF8.GetByteCount(value.AsString()),
            FieldType.Vector => 16L + value.VectorDimension * 4L,
            FieldType.GeoPoint => 24L,
            _ => 16L, // Float64 / Int64
        };

    /// <summary>
    /// 返回排序后的只读快照（按时间戳升序；同 timestamp 保留写入顺序，稳定排序）。
    /// 调用后内部缓冲不变，可被 SegmentWriter 直接消费。
    /// </summary>
    /// <returns>按时间戳升序排列的数据点只读内存。</returns>
    public ReadOnlyMemory<DataPoint> Snapshot()
    {
        var cached = Volatile.Read(ref _snapshotCache);
        if (cached != null && cached.Version == Volatile.Read(ref _version))
            return cached.Memory;

        long version;
        DataPoint[] snapshot;
        bool requiresSort;
        lock (_sync)
        {
            cached = _snapshotCache;
            version = _version;
            if (cached != null && cached.Version == version)
                return cached.Memory;

            int count = _points.Count;
            if (count == 0)
                return PublishSnapshot(version, ReadOnlyMemory<DataPoint>.Empty);

            snapshot = _points.ToArray();
            requiresSort = !_isSorted;
        }

        if (requiresSort)
            snapshot = StableSorted(snapshot);

        return TryPublishSnapshot(version, WrapSnapshot(snapshot));
    }

    /// <summary>
    /// 按时间范围 [<paramref name="fromInclusive"/>, <paramref name="toInclusive"/>] 返回切片快照（仍排序）。
    /// </summary>
    /// <param name="fromInclusive">起始时间戳（含）。</param>
    /// <param name="toInclusive">结束时间戳（含）。</param>
    /// <returns>在指定时间范围内的按时间戳升序排列的数据点只读内存。</returns>
    public ReadOnlyMemory<DataPoint> SnapshotRange(long fromInclusive, long toInclusive)
    {
        var cached = Volatile.Read(ref _snapshotCache);
        if (cached != null && cached.Version == Volatile.Read(ref _version))
            return CopyRange(cached.Memory.Span, fromInclusive, toInclusive);

        long version;
        DataPoint[] snapshot;
        lock (_sync)
        {
            int count = _points.Count;
            if (count == 0 || fromInclusive > toInclusive)
                return ReadOnlyMemory<DataPoint>.Empty;

            cached = _snapshotCache;
            version = _version;
            if (cached != null && cached.Version == version)
                return CopyRange(cached.Memory.Span, fromInclusive, toInclusive);

            if (_isSorted)
                return CopyRange(_points, fromInclusive, toInclusive);

            snapshot = _points.ToArray();
        }

        var sorted = WrapSnapshot(StableSorted(snapshot));
        var published = TryPublishSnapshot(version, sorted);
        return CopyRange(published.Span, fromInclusive, toInclusive);
    }

    /// <summary>
    /// 尝试在指定时间窗内读取最新点；命中时不复制整个范围。
    /// </summary>
    public bool TryGetLatest(long fromInclusive, long toInclusive, out DataPoint point)
    {
        point = default;
        if (fromInclusive > toInclusive)
            return false;

        var cached = Volatile.Read(ref _snapshotCache);
        if (cached != null && cached.Version == Volatile.Read(ref _version))
            return TryGetLatest(cached.Memory.Span, fromInclusive, toInclusive, out point);

        long version;
        DataPoint[] snapshot;
        lock (_sync)
        {
            int count = _points.Count;
            if (count == 0 || _maxTimestamp < fromInclusive || _minTimestamp > toInclusive)
                return false;

            cached = _snapshotCache;
            version = _version;
            if (cached != null && cached.Version == version)
                return TryGetLatest(cached.Memory.Span, fromInclusive, toInclusive, out point);

            if (_isSorted)
                return TryGetLatest(_points, fromInclusive, toInclusive, out point);

            snapshot = _points.ToArray();
        }

        var sorted = WrapSnapshot(StableSorted(snapshot));
        var published = TryPublishSnapshot(version, sorted);
        return TryGetLatest(published.Span, fromInclusive, toInclusive, out point);
    }

    private static ReadOnlyMemory<DataPoint> CopyRange(
        IReadOnlyList<DataPoint> sorted, long fromInclusive, long toInclusive)
    {
        int start = LowerBound(sorted, fromInclusive);
        int end = UpperBound(sorted, toInclusive, start);
        int length = end - start;
        if (length <= 0)
            return ReadOnlyMemory<DataPoint>.Empty;

        var result = new DataPoint[length];
        for (int i = 0; i < length; i++)
            result[i] = sorted[start + i];
        return WrapSnapshot(result);
    }

    private static ReadOnlyMemory<DataPoint> CopyRange(
        ReadOnlySpan<DataPoint> sorted, long fromInclusive, long toInclusive)
    {
        int start = LowerBound(sorted, fromInclusive);
        int end = UpperBound(sorted, toInclusive, start);
        int length = end - start;
        if (length <= 0)
            return ReadOnlyMemory<DataPoint>.Empty;

        var result = new DataPoint[length];
        sorted.Slice(start, length).CopyTo(result);
        return WrapSnapshot(result);
    }

    private static bool TryGetLatest(
        IReadOnlyList<DataPoint> sorted,
        long fromInclusive,
        long toInclusive,
        out DataPoint point)
    {
        point = default;
        int end = UpperBound(sorted, toInclusive, 0);
        if (end <= 0)
            return false;

        var candidate = sorted[end - 1];
        if (candidate.Timestamp < fromInclusive)
            return false;

        point = candidate;
        return true;
    }

    private static bool TryGetLatest(
        ReadOnlySpan<DataPoint> sorted,
        long fromInclusive,
        long toInclusive,
        out DataPoint point)
    {
        point = default;
        int end = UpperBound(sorted, toInclusive, 0);
        if (end <= 0)
            return false;

        var candidate = sorted[end - 1];
        if (candidate.Timestamp < fromInclusive)
            return false;

        point = candidate;
        return true;
    }

    // ── 私有辅助 ──────────────────────────────────────────────────────────────

    private static ReadOnlyMemory<DataPoint> WrapSnapshot(DataPoint[] snapshot)
        => new SnapshotMemoryManager(snapshot).Memory.Slice(0, snapshot.Length);

    private ReadOnlyMemory<DataPoint> PublishSnapshot(long version, ReadOnlyMemory<DataPoint> memory)
    {
        var cache = new SnapshotCache(version, memory);
        Volatile.Write(ref _snapshotCache, cache);
        return memory;
    }

    private ReadOnlyMemory<DataPoint> TryPublishSnapshot(long version, ReadOnlyMemory<DataPoint> memory)
    {
        lock (_sync)
        {
            var cached = _snapshotCache;
            if (cached != null && cached.Version == version)
                return cached.Memory;

            if (_version == version)
                return PublishSnapshot(version, memory);

            return memory;
        }
    }

    private void InvalidateSnapshotCache()
    {
        Volatile.Write(ref _snapshotCache, null);
    }

    private static DataPoint[] StableSorted(DataPoint[] appendOrderSnapshot)
    {
        // 稳定排序：通过 (index, point) 对保证同 timestamp 保留追加顺序
        var indexed = new (int Index, DataPoint Point)[appendOrderSnapshot.Length];
        for (int i = 0; i < appendOrderSnapshot.Length; i++)
            indexed[i] = (i, appendOrderSnapshot[i]);

        Array.Sort(indexed, static (a, b) =>
        {
            int cmp = a.Point.Timestamp.CompareTo(b.Point.Timestamp);
            return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
        });

        var result = new DataPoint[indexed.Length];
        for (int i = 0; i < indexed.Length; i++)
            result[i] = indexed[i].Point;

        return result;
    }

    private static int LowerBound(IReadOnlyList<DataPoint> sorted, long timestamp)
    {
        int lo = 0;
        int hi = sorted.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (sorted[mid].Timestamp < timestamp)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static int UpperBound(IReadOnlyList<DataPoint> sorted, long timestamp, int start)
    {
        int lo = start;
        int hi = sorted.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (sorted[mid].Timestamp <= timestamp)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static int LowerBound(ReadOnlySpan<DataPoint> sorted, long timestamp)
    {
        int lo = 0;
        int hi = sorted.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (sorted[mid].Timestamp < timestamp)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static int UpperBound(ReadOnlySpan<DataPoint> sorted, long timestamp, int start)
    {
        int lo = start;
        int hi = sorted.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (sorted[mid].Timestamp <= timestamp)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private sealed class SnapshotCache
    {
        public SnapshotCache(long version, ReadOnlyMemory<DataPoint> memory)
        {
            Version = version;
            Memory = memory;
        }

        public long Version { get; }

        public ReadOnlyMemory<DataPoint> Memory { get; }
    }

    private sealed class SnapshotMemoryManager : MemoryManager<DataPoint>
    {
        private readonly DataPoint[] _items;

        public SnapshotMemoryManager(DataPoint[] items)
        {
            _items = items;
        }

        public override Span<DataPoint> GetSpan() => _items;

        public override MemoryHandle Pin(int elementIndex = 0)
            => throw new NotSupportedException("MemTableSeries 快照不支持 pinning。");

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
