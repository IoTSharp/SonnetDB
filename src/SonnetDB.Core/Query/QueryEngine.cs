using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SonnetDB.Catalog;
using SonnetDB.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query.Functions;
using SonnetDB.Query.Functions.Aggregates;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;

namespace SonnetDB.Query;

/// <summary>
/// 查询执行器：合并 MemTable 与 SegmentManager 的候选 Block，对外提供原始点查询、聚合查询。
/// <para>
/// 线程安全：内部不持锁，所有数据源（MemTable / SegmentManager / Catalog）自身保证只读并发。
/// </para>
/// <para>
/// 查询路径只读：绝不修改 MemTable / Segment / Catalog 的任何数据。
/// </para>
/// </summary>
public sealed class QueryEngine
{
    private readonly SegmentManager _segments;
    private readonly SeriesCatalog _catalog;
    private readonly TombstoneTable? _tombstones;
    private readonly bool _useSimdNumericAggregates;
    private ReaderMapCache? _readerMapCache;

    /// <summary>
    /// 初始化 <see cref="QueryEngine"/> 实例。
    /// </summary>
    /// <param name="memTable">内存层数据源；构造时登记为 <paramref name="segments"/> 的初始活跃 MemTable，此后查询统一从段快照读取 active/sealing MemTable。</param>
    /// <param name="segments">段集合与索引快照管理器（同时承载 MemTable 维度）。</param>
    /// <param name="catalog">序列目录。</param>
    /// <param name="tombstones">可选的墓碑集合，用于查询时过滤被删除的数据点；为 null 时不过滤。</param>
    /// <exception cref="ArgumentNullException"><paramref name="memTable"/>、<paramref name="segments"/> 或 <paramref name="catalog"/> 为 null 时抛出。</exception>
    public QueryEngine(MemTable memTable, SegmentManager segments, SeriesCatalog catalog, TombstoneTable? tombstones = null)
        : this(memTable, segments, catalog, tombstones, useSimdNumericAggregates: true)
    {
    }

    internal QueryEngine(
        MemTable memTable,
        SegmentManager segments,
        SeriesCatalog catalog,
        TombstoneTable? tombstones,
        bool useSimdNumericAggregates)
    {
        ArgumentNullException.ThrowIfNull(memTable);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(catalog);

        _segments = segments;
        _catalog = catalog;
        _tombstones = tombstones;
        _useSimdNumericAggregates = useSimdNumericAggregates;

        // 把传入的 MemTable 登记为统一快照的初始活跃 MemTable；此后所有读取都从快照取，
        // 不再持有独立 _memTable 引用，避免 flush 换表后引用陈旧（原子快照，修 #190）。
        segments.InitializeActiveMemTable(memTable);
    }

    /// <summary>
    /// 从一次已获取的段快照租约中，收集指定 (series, field) 在所有 MemTable（active + sealing）
    /// 上的排序切片，并输出统一的字段类型（用于与段块做类型一致性校验）。
    /// </summary>
    private static List<ReadOnlyMemory<DataPoint>> CollectMemTableSlices(
        in SegmentManagerSnapshotLease lease,
        in SeriesFieldKey key,
        long from,
        long to,
        out FieldType? memFieldType)
    {
        memFieldType = null;
        var slices = new List<ReadOnlyMemory<DataPoint>>();

        // 顺序：先 sealing（较旧），后 active（最新）——保证同时间戳稳定合并时最新写入在后。
        foreach (var sealing in lease.SealingMemTables)
            AppendBucketSlice(sealing, in key, from, to, slices, ref memFieldType);

        var active = lease.ActiveMemTable;
        if (active is not null)
            AppendBucketSlice(active, in key, from, to, slices, ref memFieldType);

        return slices;
    }

    private static void AppendBucketSlice(
        MemTable memTable,
        in SeriesFieldKey key,
        long from,
        long to,
        List<ReadOnlyMemory<DataPoint>> slices,
        ref FieldType? memFieldType)
    {
        var bucket = memTable.TryGet(in key);
        if (bucket is null)
            return;

        memFieldType ??= bucket.FieldType;
        var slice = bucket.SnapshotRange(from, to);
        if (slice.Length > 0)
            slices.Add(slice);
    }

    /// <summary>
    /// 原始点查询；按时间戳升序流式返回指定 (series, field) 在时间范围内的数据点。
    /// </summary>
    /// <param name="query">点查询参数。</param>
    /// <returns>按时间戳升序排列的 <see cref="DataPoint"/> 序列。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> 为 null 时抛出。</exception>
    /// <exception cref="InvalidOperationException">MemTable 与 Segment 中同 (series, field) 的 FieldType 不一致时抛出。</exception>
    public IEnumerable<DataPoint> Execute(PointQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        // 计量覆盖整个流式枚举周期（含提前 break：finally 在枚举器 Dispose 时运行）。
        // 未监听时 StartOperation 返回 null、Enabled=false 短路，近零开销（M17 #89）。
        using var activity = SonnetDbActivitySource.StartOperation("sonnetdb.query.points", "points");
        long startTimestamp = SonnetDbMeter.QueryDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
        try
        {
            long from = query.Range.FromInclusive;
            long to = query.Range.ToInclusive;

            var key = new SeriesFieldKey(query.SeriesId, query.FieldName);

            // 墓碑集合与查询范围无关，可在进入租约前一次取好。
            IReadOnlyList<Tombstone> tombstones = Array.Empty<Tombstone>();
            if (_tombstones is not null)
            {
                var tombstoneList = _tombstones.GetForSeriesField(query.SeriesId, query.FieldName);
                if (tombstoneList.Count > 0)
                    tombstones = FilterTombstonesForQueryRange(tombstoneList, from, to);
            }

            // 单次租约拿到 {active + sealing MemTable + segments} 一致视图（SuperVersion）。
            // 关键：租约必须在整个流式合并期间保持——段 block 惰性解码发生在下方 foreach 内，
            // 依赖 reader 存活。iterator 被消费完 / 提前 break 时，using 释放租约（#220 C9）。
            using var snapshotLease = _segments.AcquireSnapshot();

            var memSlices = CollectMemTableSlices(in snapshotLease, in key, from, to, out var memFieldType);

            var snapshot = snapshotLease.Snapshot;
            var candidates = snapshot.Index.LookupCandidates(query.SeriesId, query.FieldName, from, to);
            var readers = BuildReaderMap(snapshot);

            // 构建惰性 block 源：只捕获 (reader, descriptor)，解码推迟到该 block 抵达合并前沿，
            // 从而把解码工作集限制为 overlap depth，而非候选 block 总数（LOH 峰值消除）。
            var segmentBlocks = new List<BlockSourceMerger.LazyBlock>(candidates.Count);
            foreach (var blockRef in candidates)
            {
                if (query.GeoFilter is not null
                    && blockRef.Descriptor.HasGeoHashRange
                    && !GeoHash32.Overlaps(
                        blockRef.Descriptor.GeoHashMin,
                        blockRef.Descriptor.GeoHashMax,
                        query.GeoFilter.HashMin,
                        query.GeoFilter.HashMax))
                {
                    continue;
                }

                ThrowIfFieldTypeMismatch(memFieldType, blockRef);

                if (!readers.TryGetValue(blockRef.SegmentId, out var reader))
                    continue;

                var descriptor = blockRef.Descriptor;
                long lowerBound = Math.Max(from, descriptor.MinTimestamp);
                segmentBlocks.Add(new BlockSourceMerger.LazyBlock(
                    lowerBound,
                    () => reader.DecodeBlockRangeView(descriptor, from, to)));
            }

            // N 路流式有序合并（MemTable 侧可能多路：sealing + active）+ 墓碑过滤 + Limit。
            // Limit 必须在 tombstone 过滤之后计数。
            int emitted = 0;
            int? limit = query.Limit;
            foreach (var dp in BlockSourceMerger.Merge(memSlices, segmentBlocks))
            {
                if (tombstones.Count > 0 && IsCoveredByTombstones(dp.Timestamp, tombstones))
                    continue;

                if (limit.HasValue && emitted >= limit.Value)
                    yield break;

                yield return dp;
                emitted++;
            }
        }
        finally
        {
            if (startTimestamp != 0)
            {
                SonnetDbMeter.QueryDuration.Record(
                    Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                    SonnetDbMeter.OperationPoints);
            }
        }
    }

    /// <summary>
    /// 在指定 (series, field, time-range) 内读取最新点；用于 <c>ORDER BY time DESC LIMIT 1</c> 等最新值查询。
    /// </summary>
    public bool TryGetLatestPoint(
        ulong seriesId,
        string fieldName,
        TimeRange range,
        out DataPoint point)
    {
        ArgumentNullException.ThrowIfNull(fieldName);

        point = default;
        long from = range.FromInclusive;
        long to = range.ToInclusive;
        if (from > to)
            return false;

        IReadOnlyList<Tombstone> tombstones = GetTombstonesForQueryRange(seriesId, fieldName, from, to);
        bool hasBest = false;
        var best = default(DataPoint);

        var key = new SeriesFieldKey(seriesId, fieldName);

        using (var snapshotLease = _segments.AcquireSnapshot())
        {
            FieldType? memFieldType = null;
            foreach (var memTable in EnumerateMemTables(in snapshotLease))
            {
                var bucket = memTable.TryGet(in key);
                if (bucket is null)
                    continue;

                memFieldType ??= bucket.FieldType;
                if (TryGetLatestFromMemTable(bucket, from, to, tombstones, out var memPoint)
                    && (!hasBest || memPoint.Timestamp > best.Timestamp))
                {
                    best = memPoint;
                    hasBest = true;
                }
            }

            var snapshot = snapshotLease.Snapshot;
            var candidates = snapshot.Index.LookupCandidates(seriesId, fieldName, from, to);
            if (candidates.Count > 0)
            {
                var orderedCandidates = new List<SegmentBlockRef>(candidates.Count);
                for (int i = 0; i < candidates.Count; i++)
                    orderedCandidates.Add(candidates[i]);

                orderedCandidates.Sort(static (x, y) =>
                    y.Descriptor.MaxTimestamp.CompareTo(x.Descriptor.MaxTimestamp));

                var readers = BuildReaderMap(snapshot);
                foreach (var blockRef in orderedCandidates)
                {
                    if (hasBest && blockRef.Descriptor.MaxTimestamp < best.Timestamp)
                        break;

                    ThrowIfFieldTypeMismatch(memFieldType, blockRef);

                    if (!readers.TryGetValue(blockRef.SegmentId, out var reader))
                        continue;

                    var slice = reader.DecodeBlockRangeView(blockRef.Descriptor, from, to);
                    if (TryGetLatestFromDecoded(slice.Span, tombstones, out var segmentPoint)
                        && (!hasBest || segmentPoint.Timestamp > best.Timestamp))
                    {
                        best = segmentPoint;
                        hasBest = true;
                    }
                }
            }
        }

        if (!hasBest)
            return false;

        point = best;
        return true;
    }

    /// <summary>
    /// 按合并优先级枚举一次租约内的全部 MemTable：先 sealing（较旧），后 active（最新）。
    /// </summary>
    private static IReadOnlyList<MemTable> EnumerateMemTables(in SegmentManagerSnapshotLease lease)
    {
        var sealing = lease.SealingMemTables;
        var active = lease.ActiveMemTable;
        var result = new List<MemTable>(sealing.Count + (active is null ? 0 : 1));
        result.AddRange(sealing);
        if (active is not null)
            result.Add(active);
        return result;
    }

    private IReadOnlyList<Tombstone> GetTombstonesForQueryRange(
        ulong seriesId,
        string fieldName,
        long from,
        long toInclusive)
    {
        if (_tombstones is null)
            return Array.Empty<Tombstone>();

        var tombstoneList = _tombstones.GetForSeriesField(seriesId, fieldName);
        return tombstoneList.Count == 0
            ? Array.Empty<Tombstone>()
            : FilterTombstonesForQueryRange(tombstoneList, from, toInclusive);
    }

    private static bool TryGetLatestFromDecoded(
        ReadOnlySpan<DataPoint> points,
        IReadOnlyList<Tombstone> tombstones,
        out DataPoint point)
    {
        point = default;
        for (int i = points.Length - 1; i >= 0; i--)
        {
            var candidate = points[i];
            if (tombstones.Count > 0 && IsCoveredByTombstones(candidate.Timestamp, tombstones))
                continue;

            point = candidate;
            return true;
        }

        return false;
    }

    private static bool TryGetLatestFromMemTable(
        MemTableSeries bucket,
        long from,
        long toInclusive,
        IReadOnlyList<Tombstone> tombstones,
        out DataPoint point)
    {
        point = default;
        if (!bucket.TryGetLatest(from, toInclusive, out var latest))
            return false;

        if (tombstones.Count == 0 || !IsCoveredByTombstones(latest.Timestamp, tombstones))
        {
            point = latest;
            return true;
        }

        var slice = bucket.SnapshotRange(from, toInclusive);
        return TryGetLatestFromDecoded(slice.Span, tombstones, out point);
    }

    private static IReadOnlyList<Tombstone> FilterTombstonesForQueryRange(
        IReadOnlyList<Tombstone> tombstones,
        long from,
        long toInclusive)
    {
        int firstOverlap = -1;
        for (int i = 0; i < tombstones.Count; i++)
        {
            if (Overlaps(tombstones[i], from, toInclusive))
            {
                firstOverlap = i;
                break;
            }
        }

        if (firstOverlap < 0)
            return Array.Empty<Tombstone>();

        bool allOverlap = firstOverlap == 0;
        if (allOverlap)
        {
            for (int i = 1; i < tombstones.Count; i++)
            {
                if (!Overlaps(tombstones[i], from, toInclusive))
                {
                    allOverlap = false;
                    break;
                }
            }
        }

        if (allOverlap)
            return tombstones;

        var filtered = new List<Tombstone>(tombstones.Count - firstOverlap);
        for (int i = firstOverlap; i < tombstones.Count; i++)
        {
            if (Overlaps(tombstones[i], from, toInclusive))
                filtered.Add(tombstones[i]);
        }

        return filtered.Count == 0 ? Array.Empty<Tombstone>() : filtered;
    }

    private static bool Overlaps(in Tombstone tombstone, long from, long toInclusive)
    {
        return tombstone.FromTimestamp <= toInclusive && tombstone.ToTimestamp >= from;
    }

    /// <summary>
    /// 聚合查询；按 BucketStart 升序返回聚合桶。
    /// <para>
    /// 支持字段类型：Float64 / Int64 / Boolean（Bool 走数值聚合，true=1, false=0）。
    /// String 字段会抛出 <see cref="NotSupportedException"/>。
    /// </para>
    /// <para>
    /// BucketSizeMs &lt;= 0 时全局单桶聚合；&gt; 0 时按 <see cref="TimeBucket.Floor"/> 分桶。
    /// 空桶不输出。
    /// </para>
    /// </summary>
    /// <param name="query">聚合查询参数。</param>
    /// <returns>按 BucketStart 升序排列的 <see cref="AggregateBucket"/> 序列（空数据集返回空序列）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> 为 null 时抛出。</exception>
    /// <exception cref="NotSupportedException">字段类型为 String 时抛出。</exception>
    public IEnumerable<AggregateBucket> Execute(AggregateQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var buckets = ShouldUsePointAggregatePath(query)
            ? ExecuteAggregateViaPoints(query)
            : ExecuteAggregateFast(query);

        return InstrumentAggregate(buckets);
    }

    /// <summary>
    /// 聚合查询计量包装：覆盖整个流式枚举周期（含提前中止）。未监听时直接透传原枚举，零包装开销。
    /// </summary>
    private static IEnumerable<AggregateBucket> InstrumentAggregate(IEnumerable<AggregateBucket> buckets)
    {
        if (!SonnetDbMeter.QueryDuration.Enabled && !SonnetDbActivitySource.Source.HasListeners())
            return buckets;

        return Enumerate(buckets);

        static IEnumerable<AggregateBucket> Enumerate(IEnumerable<AggregateBucket> source)
        {
            using var activity = SonnetDbActivitySource.StartOperation("sonnetdb.query.aggregate", "aggregate");
            long startTimestamp = Stopwatch.GetTimestamp();
            try
            {
                foreach (var bucket in source)
                    yield return bucket;
            }
            finally
            {
                SonnetDbMeter.QueryDuration.Record(
                    Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                    SonnetDbMeter.OperationAggregate);
            }
        }
    }

    private IEnumerable<AggregateBucket> ExecuteAggregateViaPoints(AggregateQuery query)
    {
        // 用 PointQuery 取原始点流（利用已实现的合并逻辑）
        var pointQuery = new PointQuery(query.SeriesId, query.FieldName, query.Range);
        var points = Execute(pointQuery);

        // 检查 FieldType（先从 MemTable 或 Segment 中取得，若数据为空则无法校验，直接返回）
        // 字段类型校验在第一条点时进行
        bool fieldTypeChecked = false;

        if (query.BucketSizeMs <= 0)
        {
            // ── 全局单桶聚合 ────────────────────────────────────────────────────
            long bucketStart = query.Range.FromInclusive == long.MinValue
                ? long.MinValue  // 延迟到第一条点确定
                : query.Range.FromInclusive;
            long bucketEnd = query.Range.ToInclusive == long.MaxValue
                ? long.MaxValue
                : query.Range.ToInclusive + 1;

            bool firstPoint = true;
            long count = 0;
            double sum = 0, min = double.PositiveInfinity, max = double.NegativeInfinity;
            double firstValue = 0, lastValue = 0;

            foreach (var dp in points)
            {
                if (!fieldTypeChecked)
                {
                    ThrowIfString(dp.Value.Type);
                    fieldTypeChecked = true;
                }

                if (firstPoint && query.Range.FromInclusive == long.MinValue)
                {
                    bucketStart = dp.Timestamp;
                    firstPoint = false;
                }

                double v = ToDouble(dp.Value);
                count++;
                sum += v;
                if (v < min) min = v;
                if (v > max) max = v;
                if (count == 1) firstValue = v;
                lastValue = v;
            }

            if (count == 0)
                yield break;

            // 确定 bucketEnd（全局单桶时终点 = ToInclusive+1 或 MaxValue）
            bucketEnd = query.Range.ToInclusive < long.MaxValue
                ? query.Range.ToInclusive + 1
                : long.MaxValue;

            yield return new AggregateBucket(
                bucketStart, bucketEnd, count,
                ComputeValue(query.Aggregator, count, sum, min, max, firstValue, lastValue));
        }
        else
        {
            // ── 桶聚合（GROUP BY time(BucketSizeMs)）─────────────────────────────
            long bucketSizeMs = query.BucketSizeMs;
            long currentBucketStart = long.MinValue;
            long currentBucketEnd = long.MinValue;
            long count = 0;
            double sum = 0, min = double.PositiveInfinity, max = double.NegativeInfinity;
            double firstValue = 0, lastValue = 0;
            bool hasCurrent = false;

            foreach (var dp in points)
            {
                if (!fieldTypeChecked)
                {
                    ThrowIfString(dp.Value.Type);
                    fieldTypeChecked = true;
                }

                long bucketStart = TimeBucket.Floor(dp.Timestamp, bucketSizeMs);
                long bucketEnd = bucketStart + bucketSizeMs;

                if (!hasCurrent)
                {
                    // 初始化第一个桶
                    currentBucketStart = bucketStart;
                    currentBucketEnd = bucketEnd;
                    hasCurrent = true;
                }
                else if (bucketStart != currentBucketStart)
                {
                    // 桶切换：emit 当前桶
                    yield return new AggregateBucket(
                        currentBucketStart, currentBucketEnd, count,
                        ComputeValue(query.Aggregator, count, sum, min, max, firstValue, lastValue));

                    // 重置当前桶状态
                    currentBucketStart = bucketStart;
                    currentBucketEnd = bucketEnd;
                    count = 0;
                    sum = 0;
                    min = double.PositiveInfinity;
                    max = double.NegativeInfinity;
                    firstValue = 0;
                    lastValue = 0;
                }

                double v = ToDouble(dp.Value);
                if (count == 0) firstValue = v;
                lastValue = v;
                count++;
                sum += v;
                if (v < min) min = v;
                if (v > max) max = v;
            }

            // emit 最后一个桶
            if (hasCurrent && count > 0)
            {
                yield return new AggregateBucket(
                    currentBucketStart, currentBucketEnd, count,
                    ComputeValue(query.Aggregator, count, sum, min, max, firstValue, lastValue));
            }
        }
    }

    private IReadOnlyList<AggregateBucket> ExecuteAggregateFast(AggregateQuery query)
    {
        using var snapshotLease = _segments.AcquireSnapshot();
        var snapshot = snapshotLease.Snapshot;
        return ExecuteAggregateFast(query, snapshot.Index, BuildReaderMap(snapshot), EnumerateMemTables(in snapshotLease));
    }

    private IReadOnlyList<AggregateBucket> ExecuteAggregateFast(
        AggregateQuery query,
        MultiSegmentIndex index,
        Dictionary<long, SegmentReader> readers,
        IReadOnlyList<MemTable> memTables)
    {
        long from = query.Range.FromInclusive;
        long to = query.Range.ToInclusive;

        var key = new SeriesFieldKey(query.SeriesId, query.FieldName);
        FieldType? memFieldType = null;
        foreach (var memTable in memTables)
        {
            var b = memTable.TryGet(in key);
            if (b is not null)
            {
                memFieldType = b.FieldType;
                break;
            }
        }

        var candidates = index.LookupCandidates(
            query.SeriesId, query.FieldName, from, to);

        if (query.BucketSizeMs <= 0)
        {
            long bucketStart = query.Range.FromInclusive == long.MinValue
                ? long.MaxValue
                : query.Range.FromInclusive;
            long bucketEnd = query.Range.ToInclusive < long.MaxValue
                ? query.Range.ToInclusive + 1
                : long.MaxValue;
            var state = new AggregateState(bucketStart, bucketEnd);
            bool useObservedStart = query.Range.FromInclusive == long.MinValue;

            foreach (var memTable in memTables)
                AddMemTableToGlobal(memTable, in key, from, to, useObservedStart, ref state);

            AddSegmentBlocksToGlobal(
                candidates,
                readers,
                memFieldType,
                query.Range,
                query.Aggregator,
                _useSimdNumericAggregates,
                ref state,
                useObservedStart);

            if (!state.HasData)
                return Array.Empty<AggregateBucket>();

            return new[] { state.ToBucket(query.Aggregator) };
        }

        var buckets = new Dictionary<long, AggregateState>();

        foreach (var memTable in memTables)
            AddMemTableToBuckets(memTable, in key, from, to, query.BucketSizeMs, buckets);

        AddSegmentBlocksToBuckets(
            candidates, readers, memFieldType, query.Range, query.BucketSizeMs, query.Aggregator, buckets);

        if (buckets.Count == 0)
            return Array.Empty<AggregateBucket>();

        var bucketStarts = buckets.Keys.ToArray();
        Array.Sort(bucketStarts);

        var result = new List<AggregateBucket>(bucketStarts.Length);
        foreach (long bucketStart in bucketStarts)
            result.Add(buckets[bucketStart].ToBucket(query.Aggregator));

        return result;
    }

    /// <summary>把单个 MemTable 对某 (series,field) 的贡献并入全局单桶聚合状态。</summary>
    private static void AddMemTableToGlobal(
        MemTable memTable,
        in SeriesFieldKey key,
        long from,
        long to,
        bool useObservedStart,
        ref AggregateState state)
    {
        var bucket = memTable.TryGet(in key);
        if (bucket is null)
            return;

        // MemTable 快路径：数值字段 + 切片整体落在查询范围内 → 直接用运行期聚合，免逐点。
        if (bucket.TryGetNumericAggregateSnapshot(
                out int memAggCount, out long memAggMinTs, out long memAggMaxTs,
                out double memAggSum, out double memAggMin, out double memAggMax)
            && memAggMinTs >= from && memAggMaxTs <= to)
        {
            state.AddMemTableAggregate(
                memAggCount, memAggMinTs, memAggSum, memAggMin, memAggMax, useObservedStart);
        }
        else
        {
            AddDecodedPointsToGlobal(bucket.SnapshotRange(from, to), ref state, useObservedStart);
        }
    }

    /// <summary>把单个 MemTable 对某 (series,field) 的贡献并入分桶聚合字典。</summary>
    private static void AddMemTableToBuckets(
        MemTable memTable,
        in SeriesFieldKey key,
        long from,
        long to,
        long bucketSizeMs,
        Dictionary<long, AggregateState> buckets)
    {
        var bucket = memTable.TryGet(in key);
        if (bucket is null)
            return;

        if (bucket.TryGetNumericAggregateSnapshot(
                out int memAggCount, out long memAggMinTs, out long memAggMaxTs,
                out double memAggSum, out double memAggMin, out double memAggMax)
            && memAggMinTs >= from && memAggMaxTs <= to
            && TimeBucket.Floor(memAggMinTs, bucketSizeMs) == TimeBucket.Floor(memAggMaxTs, bucketSizeMs))
        {
            long bStart = TimeBucket.Floor(memAggMinTs, bucketSizeMs);
            ref var st = ref CollectionsMarshal.GetValueRefOrAddDefault(buckets, bStart, out bool exists);
            if (!exists)
                st = new AggregateState(bStart, bStart + bucketSizeMs);
            st.AddMemTableAggregate(
                memAggCount, memAggMinTs, memAggSum, memAggMin, memAggMax, useObservedStart: false);
        }
        else
        {
            // 跨桶或非数值字段：回退到逐点路径。
            AddDecodedPointsToBuckets(bucket.SnapshotRange(from, to), bucketSizeMs, buckets);
        }
    }

    /// <summary>
    /// 批量聚合：对一组 series 做相同的聚合查询（field / range / aggregator / bucketSizeMs 共享）。
    /// </summary>
    /// <param name="seriesIds">目标序列 ID 列表。</param>
    /// <param name="fieldName">目标字段名称。</param>
    /// <param name="range">查询时间范围。</param>
    /// <param name="aggregator">聚合函数类型。</param>
    /// <param name="bucketSizeMs">桶大小（毫秒）；&lt;= 0 表示全局单桶聚合。</param>
    /// <returns>以 SeriesId 为键的聚合结果字典（各 series 的桶列表）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="seriesIds"/> 或 <paramref name="fieldName"/> 为 null 时抛出。</exception>
    public IReadOnlyDictionary<ulong, IReadOnlyList<AggregateBucket>> ExecuteMany(
        IReadOnlyList<ulong> seriesIds,
        string fieldName,
        TimeRange range,
        Aggregator aggregator,
        long bucketSizeMs = 0)
    {
        ArgumentNullException.ThrowIfNull(seriesIds);
        ArgumentNullException.ThrowIfNull(fieldName);

        var result = new Dictionary<ulong, IReadOnlyList<AggregateBucket>>(seriesIds.Count);

        // 共享一份段索引快照与 reader 映射，避免每个 series 重复重建。
        using var snapshotLease = _segments.AcquireSnapshot();
        var snapshot = snapshotLease.Snapshot;
        var index = snapshot.Index;
        var readers = BuildReaderMap(snapshot);
        var memTables = EnumerateMemTables(in snapshotLease);

        foreach (var seriesId in seriesIds)
        {
            var q = new AggregateQuery(seriesId, fieldName, range, aggregator, bucketSizeMs);

            // 仍走完整的快/慢路径分流；ShouldUsePointAggregatePath 依赖 tombstones，会按 series 单独决定。
            IReadOnlyList<AggregateBucket> buckets = ShouldUsePointAggregatePath(q)
                ? Execute(q).ToList().AsReadOnly()
                : ExecuteAggregateFast(q, index, readers, memTables);

            result[seriesId] = buckets;
        }

        return result;
    }

    /// <summary>
    /// 为 SQL 扩展聚合尝试走 block sketch 快路径；不支持或遇到 tombstone 时返回 false，由调用方回退逐点扫描。
    /// </summary>
    internal bool TryAddExtendedAggregateSketches(
        ulong seriesId,
        string fieldName,
        TimeRange range,
        IAggregateAccumulator accumulator,
        out long observedCount)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        ArgumentNullException.ThrowIfNull(accumulator);

        observedCount = 0;
        if (!CanUseAggregateSketchAccumulator(accumulator))
            return false;

        if (_tombstones is not null)
        {
            var tombstoneList = _tombstones.GetForSeriesField(seriesId, fieldName);
            if (tombstoneList.Count > 0
                && FilterTombstonesForQueryRange(
                    tombstoneList,
                    range.FromInclusive,
                    range.ToInclusive).Count > 0)
            {
                return false;
            }
        }

        var key = new SeriesFieldKey(seriesId, fieldName);

        using (var snapshotLease = _segments.AcquireSnapshot())
        {
            FieldType? memFieldType = null;
            foreach (var memTable in EnumerateMemTables(in snapshotLease))
            {
                var bucket = memTable.TryGet(in key);
                if (bucket is null)
                    continue;
                memFieldType ??= bucket.FieldType;
                observedCount += AddDecodedPointsToAccumulator(
                    bucket.SnapshotRange(range.FromInclusive, range.ToInclusive), accumulator);
            }

            var snapshot = snapshotLease.Snapshot;
            var candidates = snapshot.Index.LookupCandidates(
                seriesId, fieldName, range.FromInclusive, range.ToInclusive);
            var readers = BuildReaderMap(snapshot);

            foreach (var blockRef in candidates)
            {
                ThrowIfFieldTypeMismatch(memFieldType, blockRef);

                if (!readers.TryGetValue(blockRef.SegmentId, out var reader))
                    continue;

                if (CanUseAggregateSketch(blockRef.Descriptor, range)
                    && reader.TryGetAggregateSketch(blockRef.Descriptor, out var sketch)
                    && TryMergeAggregateSketch(accumulator, sketch))
                {
                    observedCount += blockRef.Descriptor.Count;
                    continue;
                }

                var data = reader.ReadBlock(blockRef.Descriptor);
                observedCount += AddBlockToAccumulator(
                    blockRef.Descriptor,
                    data.TimestampPayload,
                    data.ValuePayload,
                    range,
                    accumulator);
            }
        }

        return true;
    }

    // ── 私有辅助 ──────────────────────────────────────────────────────────────

    private bool ShouldUsePointAggregatePath(AggregateQuery query)
    {
        if (query.Aggregator is Aggregator.First or Aggregator.Last)
            return true;

        if (_tombstones == null)
            return false;

        var tombstoneList = _tombstones.GetForSeriesField(query.SeriesId, query.FieldName);
        return tombstoneList.Count > 0;
    }

    private static void AddDecodedPointsToGlobal(
        ReadOnlyMemory<DataPoint>? points,
        ref AggregateState state,
        bool useObservedStart)
    {
        if (!points.HasValue)
            return;

        AddDecodedPointsToGlobal(points.Value.Span, ref state, useObservedStart);
    }

    private static void AddDecodedPointsToGlobal(
        ReadOnlySpan<DataPoint> points,
        ref AggregateState state,
        bool useObservedStart)
    {
        for (int i = 0; i < points.Length; i++)
        {
            var point = points[i];
            state.Add(point.Timestamp, ToDouble(point.Value), useObservedStart);
        }
    }

    private static void AddDecodedPointsToBuckets(
        ReadOnlyMemory<DataPoint>? points,
        long bucketSizeMs,
        Dictionary<long, AggregateState> buckets)
    {
        if (!points.HasValue)
            return;

        AddDecodedPointsToBuckets(points.Value.Span, bucketSizeMs, buckets);
    }

    private static void AddDecodedPointsToBuckets(
        ReadOnlySpan<DataPoint> points,
        long bucketSizeMs,
        Dictionary<long, AggregateState> buckets)
    {
        for (int i = 0; i < points.Length; i++)
        {
            var point = points[i];
            AddValueToBucket(buckets, bucketSizeMs, point.Timestamp, ToDouble(point.Value));
        }
    }

    private static long AddDecodedPointsToAccumulator(
        ReadOnlyMemory<DataPoint>? points,
        IAggregateAccumulator accumulator)
    {
        if (!points.HasValue)
            return 0;

        return AddDecodedPointsToAccumulator(points.Value.Span, accumulator);
    }

    private static long AddDecodedPointsToAccumulator(
        ReadOnlySpan<DataPoint> points,
        IAggregateAccumulator accumulator)
    {
        for (int i = 0; i < points.Length; i++)
        {
            var point = points[i];
            accumulator.Add(point.Timestamp, ToDouble(point.Value));
        }

        return points.Length;
    }

    private static long AddBlockToAccumulator(
        in BlockDescriptor descriptor,
        ReadOnlySpan<byte> tsPayload,
        ReadOnlySpan<byte> valPayload,
        TimeRange range,
        IAggregateAccumulator accumulator)
    {
        if (CanAggregateRawBlock(descriptor))
        {
            int start = LowerBoundRaw(tsPayload, descriptor.Count, range.FromInclusive);
            int end = UpperBoundRaw(tsPayload, descriptor.Count, range.ToInclusive);
            if (start >= end)
                return 0;

            ThrowIfString(descriptor.FieldType);
            for (int i = start; i < end; i++)
            {
                long timestamp = ReadRawTimestamp(tsPayload, i);
                double value = ReadRawNumericValue(descriptor.FieldType, valPayload, i);
                accumulator.Add(timestamp, value);
            }
            return end - start;
        }

        if (CanFuseDeltaTimestampInline(descriptor))
            return FuseDeltaBlockToAccumulator(descriptor, tsPayload, valPayload, range, accumulator);

        var points = BlockDecoder.DecodeRange(
            descriptor,
            tsPayload,
            valPayload,
            range.FromInclusive,
            range.ToInclusive);
        return AddDecodedPointsToAccumulator(points.AsSpan(), accumulator);
    }

    private static bool TryAddContiguousRangeAggregate(
        FieldType fieldType,
        ReadOnlySpan<byte> valPayload,
        int start,
        int end,
        long firstTimestamp,
        Aggregator aggregator,
        bool useSimdNumericAggregates,
        ref AggregateState state,
        bool useObservedStart)
    {
        if (aggregator == Aggregator.Count)
        {
            state.AddCountRange(firstTimestamp, end - start, useObservedStart);
            return true;
        }

        if (!useSimdNumericAggregates
            || aggregator is not (Aggregator.Sum or Aggregator.Avg or Aggregator.Min or Aggregator.Max)
            || fieldType is not (FieldType.Float64 or FieldType.Int64))
        {
            return false;
        }

        var aggregate = NumericAggregateVector.Aggregate(
            fieldType,
            valPayload,
            start,
            end,
            useSimd: true);

        if (aggregate.Count == 0)
            return true;

        state.AddAggregateRange(firstTimestamp, aggregate, useObservedStart);
        return true;
    }

    private static void AddSegmentBlocksToGlobal(
        IReadOnlyList<SegmentBlockRef> candidates,
        Dictionary<long, SegmentReader> readers,
        FieldType? memFieldType,
        TimeRange range,
        Aggregator aggregator,
        bool useSimdNumericAggregates,
        ref AggregateState state,
        bool useObservedStart)
    {
        foreach (var blockRef in candidates)
        {
            ThrowIfFieldTypeMismatch(memFieldType, blockRef);

            if (!readers.TryGetValue(blockRef.SegmentId, out var reader))
                continue;

            if (CanUseAggregateMetadata(blockRef.Descriptor, range, aggregator))
            {
                state.AddMetadataBlock(blockRef.Descriptor, useObservedStart);
                continue;
            }

            var data = reader.ReadBlock(blockRef.Descriptor);
            AddBlockToGlobal(
                blockRef.Descriptor,
                data.TimestampPayload,
                data.ValuePayload,
                range,
                aggregator,
                useSimdNumericAggregates,
                ref state,
                useObservedStart);
        }
    }

    private static void AddSegmentBlocksToBuckets(
        IReadOnlyList<SegmentBlockRef> candidates,
        Dictionary<long, SegmentReader> readers,
        FieldType? memFieldType,
        TimeRange range,
        long bucketSizeMs,
        Aggregator aggregator,
        Dictionary<long, AggregateState> buckets)
    {
        foreach (var blockRef in candidates)
        {
            ThrowIfFieldTypeMismatch(memFieldType, blockRef);

            if (!readers.TryGetValue(blockRef.SegmentId, out var reader))
                continue;

            // 快路径：block 完整落入查询范围且整体落在同一个桶内时，直接合并元数据。
            if (TryUseAggregateMetadataForBucket(
                    blockRef.Descriptor, range, bucketSizeMs, aggregator,
                    out long bucketStart, out long bucketEndExclusive))
            {
                ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(
                    buckets, bucketStart, out bool exists);

                if (!exists)
                    state = new AggregateState(bucketStart, bucketEndExclusive);

                state.AddMetadataBlock(blockRef.Descriptor, useObservedStart: false);
                continue;
            }

            var data = reader.ReadBlock(blockRef.Descriptor);
            AddBlockToBuckets(
                blockRef.Descriptor,
                data.TimestampPayload,
                data.ValuePayload,
                range,
                bucketSizeMs,
                buckets);
        }
    }

    private static void AddBlockToGlobal(
        in BlockDescriptor descriptor,
        ReadOnlySpan<byte> tsPayload,
        ReadOnlySpan<byte> valPayload,
        TimeRange range,
        Aggregator aggregator,
        bool useSimdNumericAggregates,
        ref AggregateState state,
        bool useObservedStart)
    {
        if (CanAggregateRawBlock(descriptor))
        {
            int start = LowerBoundRaw(tsPayload, descriptor.Count, range.FromInclusive);
            int end = UpperBoundRaw(tsPayload, descriptor.Count, range.ToInclusive);
            if (start >= end)
                return;

            ThrowIfString(descriptor.FieldType);
            long firstTimestamp = ReadRawTimestamp(tsPayload, start);
            if (TryAddContiguousRangeAggregate(
                    descriptor.FieldType,
                    valPayload,
                    start,
                    end,
                    firstTimestamp,
                    aggregator,
                    useSimdNumericAggregates,
                    ref state,
                    useObservedStart))
            {
                return;
            }

            for (int i = start; i < end; i++)
            {
                long timestamp = ReadRawTimestamp(tsPayload, i);
                double value = ReadRawNumericValue(descriptor.FieldType, valPayload, i);
                state.Add(timestamp, value, useObservedStart);
            }
            return;
        }

        // 中间快路径：delta-of-delta 时间戳 + 原始数值（无 XOR/delta 压缩）。
        // 仅解码 timestamps 一次到 ArrayPool 借出的 long[]，逐点内联读取原始 value，
        // 避免 BlockDecoder.DecodeRange 分配 DataPoint[]。
        if (CanFuseDeltaTimestampInline(descriptor))
        {
            FuseDeltaBlockToGlobal(
                descriptor,
                tsPayload,
                valPayload,
                range,
                aggregator,
                useSimdNumericAggregates,
                ref state,
                useObservedStart);
            return;
        }

        var points = BlockDecoder.DecodeRange(
            descriptor,
            tsPayload,
            valPayload,
            range.FromInclusive,
            range.ToInclusive);
        AddDecodedPointsToGlobal(points.AsSpan(), ref state, useObservedStart);
    }

    private static void AddBlockToBuckets(
        in BlockDescriptor descriptor,
        ReadOnlySpan<byte> tsPayload,
        ReadOnlySpan<byte> valPayload,
        TimeRange range,
        long bucketSizeMs,
        Dictionary<long, AggregateState> buckets)
    {
        if (CanAggregateRawBlock(descriptor))
        {
            int start = LowerBoundRaw(tsPayload, descriptor.Count, range.FromInclusive);
            int end = UpperBoundRaw(tsPayload, descriptor.Count, range.ToInclusive);
            if (start >= end)
                return;

            ThrowIfString(descriptor.FieldType);
            for (int i = start; i < end; i++)
            {
                long timestamp = ReadRawTimestamp(tsPayload, i);
                double value = ReadRawNumericValue(descriptor.FieldType, valPayload, i);
                AddValueToBucket(buckets, bucketSizeMs, timestamp, value);
            }
            return;
        }

        // 中间快路径（同 AddBlockToGlobal）：跨桶大 block 仍可避开 DataPoint[] 分配。
        if (CanFuseDeltaTimestampInline(descriptor))
        {
            FuseDeltaBlockToBuckets(descriptor, tsPayload, valPayload, range, bucketSizeMs, buckets);
            return;
        }

        var points = BlockDecoder.DecodeRange(
            descriptor,
            tsPayload,
            valPayload,
            range.FromInclusive,
            range.ToInclusive);
        AddDecodedPointsToBuckets(points.AsSpan(), bucketSizeMs, buckets);
    }

    /// <summary>
    /// 判定是否可对 (delta-of-delta 时间戳 + 原始数值) 编码的数值 block 走融合内联路径。
    /// </summary>
    private static bool CanFuseDeltaTimestampInline(in BlockDescriptor descriptor)
    {
        // 时间戳必须是 delta-of-delta（否则 raw 路径已覆盖）。
        if ((descriptor.TimestampEncoding & BlockEncoding.DeltaTimestamp) == 0)
            return false;
        // 值必须是原始（无 XOR / delta 压缩）。
        if ((descriptor.ValueEncoding & BlockEncoding.DeltaValue) != 0)
            return false;
        // 仅支持数值字段。
        return descriptor.FieldType is FieldType.Float64 or FieldType.Int64 or FieldType.Boolean;
    }

    private static void FuseDeltaBlockToGlobal(
        in BlockDescriptor descriptor,
        ReadOnlySpan<byte> tsPayload,
        ReadOnlySpan<byte> valPayload,
        TimeRange range,
        Aggregator aggregator,
        bool useSimdNumericAggregates,
        ref AggregateState state,
        bool useObservedStart)
    {
        int count = descriptor.Count;
        if (count == 0) return;

        long[] rented = ArrayPool<long>.Shared.Rent(count);
        try
        {
            Span<long> timestamps = rented.AsSpan(0, count);
            TimestampCodec.ReadDeltaOfDelta(tsPayload, timestamps);

            int start = BinarySearchLowerBound(timestamps, range.FromInclusive);
            int end = BinarySearchUpperBound(timestamps, range.ToInclusive);
            if (start >= end) return;

            FieldType fieldType = descriptor.FieldType;
            if (TryAddContiguousRangeAggregate(
                    fieldType,
                    valPayload,
                    start,
                    end,
                    timestamps[start],
                    aggregator,
                    useSimdNumericAggregates,
                    ref state,
                    useObservedStart))
            {
                return;
            }

            for (int i = start; i < end; i++)
            {
                double value = ReadRawNumericValue(fieldType, valPayload, i);
                state.Add(timestamps[i], value, useObservedStart);
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(rented);
        }
    }

    private static void FuseDeltaBlockToBuckets(
        in BlockDescriptor descriptor,
        ReadOnlySpan<byte> tsPayload,
        ReadOnlySpan<byte> valPayload,
        TimeRange range,
        long bucketSizeMs,
        Dictionary<long, AggregateState> buckets)
    {
        int count = descriptor.Count;
        if (count == 0) return;

        long[] rented = ArrayPool<long>.Shared.Rent(count);
        try
        {
            Span<long> timestamps = rented.AsSpan(0, count);
            TimestampCodec.ReadDeltaOfDelta(tsPayload, timestamps);

            int start = BinarySearchLowerBound(timestamps, range.FromInclusive);
            int end = BinarySearchUpperBound(timestamps, range.ToInclusive);
            if (start >= end) return;

            FieldType fieldType = descriptor.FieldType;
            for (int i = start; i < end; i++)
            {
                double value = ReadRawNumericValue(fieldType, valPayload, i);
                AddValueToBucket(buckets, bucketSizeMs, timestamps[i], value);
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(rented);
        }
    }

    private static long FuseDeltaBlockToAccumulator(
        in BlockDescriptor descriptor,
        ReadOnlySpan<byte> tsPayload,
        ReadOnlySpan<byte> valPayload,
        TimeRange range,
        IAggregateAccumulator accumulator)
    {
        int count = descriptor.Count;
        if (count == 0) return 0;

        long[] rented = ArrayPool<long>.Shared.Rent(count);
        try
        {
            Span<long> timestamps = rented.AsSpan(0, count);
            TimestampCodec.ReadDeltaOfDelta(tsPayload, timestamps);

            int start = BinarySearchLowerBound(timestamps, range.FromInclusive);
            int end = BinarySearchUpperBound(timestamps, range.ToInclusive);
            if (start >= end) return 0;

            FieldType fieldType = descriptor.FieldType;
            for (int i = start; i < end; i++)
            {
                double value = ReadRawNumericValue(fieldType, valPayload, i);
                accumulator.Add(timestamps[i], value);
            }

            return end - start;
        }
        finally
        {
            ArrayPool<long>.Shared.Return(rented);
        }
    }

    private static int BinarySearchLowerBound(ReadOnlySpan<long> timestamps, long value)
    {
        int lo = 0, hi = timestamps.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (timestamps[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int BinarySearchUpperBound(ReadOnlySpan<long> timestamps, long value)
    {
        int lo = 0, hi = timestamps.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (timestamps[mid] <= value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static bool CanUseAggregateMetadata(
        in BlockDescriptor descriptor,
        TimeRange range,
        Aggregator aggregator)
    {
        if (descriptor.MinTimestamp < range.FromInclusive
            || descriptor.MaxTimestamp > range.ToInclusive)
            return false;

        return aggregator switch
        {
            // Count 始终可用：descriptor.Count 总是有效的。
            Aggregator.Count => true,
            // Sum / Avg 依赖 sum 元数据。
            Aggregator.Sum or Aggregator.Avg => descriptor.HasAggregateSumCount,
            // Min / Max 依赖无损 min/max 元数据。
            Aggregator.Min or Aggregator.Max => descriptor.HasAggregateMinMax,
            _ => false,
        };
    }

    private static bool CanUseAggregateSketchAccumulator(IAggregateAccumulator accumulator)
        => accumulator is PercentileAccumulator
            or TDigestAggAccumulator
            or DistinctCountAccumulator;

    private static bool CanUseAggregateSketch(
        in BlockDescriptor descriptor,
        TimeRange range)
    {
        return descriptor.MinTimestamp >= range.FromInclusive
            && descriptor.MaxTimestamp <= range.ToInclusive
            && descriptor.FieldType is FieldType.Float64 or FieldType.Int64 or FieldType.Boolean;
    }

    private static bool TryMergeAggregateSketch(
        IAggregateAccumulator accumulator,
        BlockAggregateSketch sketch)
    {
        switch (accumulator)
        {
            case PercentileAccumulator percentile when sketch.TDigest is not null:
                percentile.MergeDigest(sketch.TDigest);
                return true;

            case TDigestAggAccumulator digestAgg when sketch.TDigest is not null:
                digestAgg.MergeDigest(sketch.TDigest);
                return true;

            case DistinctCountAccumulator distinct when sketch.HyperLogLog is not null:
                distinct.MergeSketch(sketch.HyperLogLog, sketch.ValueCount);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// 桶聚合快路径判定：仅当 block 完整落入查询范围、整体落在同一个桶内、且元数据满足聚合函数要求时返回 <c>true</c>。
    /// </summary>
    private static bool TryUseAggregateMetadataForBucket(
        in BlockDescriptor descriptor,
        TimeRange range,
        long bucketSizeMs,
        Aggregator aggregator,
        out long bucketStart,
        out long bucketEndExclusive)
    {
        bucketStart = 0;
        bucketEndExclusive = 0;

        if (!CanUseAggregateMetadata(descriptor, range, aggregator))
            return false;

        long startBucket = TimeBucket.Floor(descriptor.MinTimestamp, bucketSizeMs);
        long endBucket = TimeBucket.Floor(descriptor.MaxTimestamp, bucketSizeMs);
        if (startBucket != endBucket)
            return false;

        bucketStart = startBucket;
        bucketEndExclusive = startBucket + bucketSizeMs;
        return true;
    }

    private static void AddValueToBucket(
        Dictionary<long, AggregateState> buckets,
        long bucketSizeMs,
        long timestamp,
        double value)
    {
        long bucketStart = TimeBucket.Floor(timestamp, bucketSizeMs);
        ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(
            buckets,
            bucketStart,
            out bool exists);

        if (!exists)
            state = new AggregateState(bucketStart, bucketStart + bucketSizeMs);

        state.Add(timestamp, value, useObservedStart: false);
    }

    private static bool CanAggregateRawBlock(in BlockDescriptor descriptor)
    {
        return (descriptor.TimestampEncoding & BlockEncoding.DeltaTimestamp) == 0
            && (descriptor.ValueEncoding & BlockEncoding.DeltaValue) == 0;
    }

    private static double ReadRawNumericValue(
        FieldType fieldType,
        ReadOnlySpan<byte> valPayload,
        int index)
    {
        return fieldType switch
        {
            FieldType.Float64 => BinaryPrimitives.ReadDoubleLittleEndian(valPayload.Slice(index * 8, 8)),
            FieldType.Int64 => (double)BinaryPrimitives.ReadInt64LittleEndian(valPayload.Slice(index * 8, 8)),
            FieldType.Boolean => valPayload[index] != 0 ? 1.0 : 0.0,
            _ => throw new NotSupportedException(
                $"字段类型 {fieldType} 不支持数值聚合。仅支持 Float64 / Int64 / Boolean 字段。"),
        };
    }

    private static int LowerBoundRaw(ReadOnlySpan<byte> tsPayload, int count, long value)
    {
        int lo = 0, hi = count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (ReadRawTimestamp(tsPayload, mid) < value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static int UpperBoundRaw(ReadOnlySpan<byte> tsPayload, int count, long value)
    {
        int lo = 0, hi = count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (ReadRawTimestamp(tsPayload, mid) <= value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static long ReadRawTimestamp(ReadOnlySpan<byte> tsPayload, int index)
        => BinaryPrimitives.ReadInt64LittleEndian(tsPayload.Slice(index * 8, 8));

    private static void ThrowIfFieldTypeMismatch(FieldType? memFieldType, SegmentBlockRef blockRef)
    {
        if (memFieldType.HasValue
            && memFieldType.Value != blockRef.FieldType
            && !IsIntFloatCompatible(memFieldType.Value, blockRef.FieldType))
        {
            throw new InvalidOperationException(
                $"FieldType mismatch across MemTable and Segment for series {blockRef.SeriesId:X16}/{blockRef.FieldName}: " +
                $"MemTable={memFieldType.Value}, Segment(id={blockRef.SegmentId})={blockRef.FieldType}。");
        }
    }

    private static bool IsIntFloatCompatible(FieldType left, FieldType right)
        => (left == FieldType.Int64 && right == FieldType.Float64)
            || (left == FieldType.Float64 && right == FieldType.Int64);

    private struct AggregateState
    {
        public long BucketStart;
        public long BucketEndExclusive;
        public long Count;
        public double Sum;
        public double Min;
        public double Max;
        public double FirstValue;
        public double LastValue;

        public AggregateState(long bucketStart, long bucketEndExclusive)
        {
            BucketStart = bucketStart;
            BucketEndExclusive = bucketEndExclusive;
            Count = 0;
            Sum = 0;
            Min = double.PositiveInfinity;
            Max = double.NegativeInfinity;
            FirstValue = 0;
            LastValue = 0;
        }

        public readonly bool HasData => Count > 0;

        public void Add(long timestamp, double value, bool useObservedStart)
        {
            if (useObservedStart && timestamp < BucketStart)
                BucketStart = timestamp;

            if (Count == 0)
                FirstValue = value;

            LastValue = value;
            Count++;
            Sum += value;
            if (value < Min) Min = value;
            if (value > Max) Max = value;
        }

        public void AddCountRange(long firstTimestamp, long count, bool useObservedStart)
        {
            if (count <= 0) return;

            if (useObservedStart && firstTimestamp < BucketStart)
                BucketStart = firstTimestamp;

            Count += count;
        }

        public void AddAggregateRange(
            long firstTimestamp,
            NumericAggregateVectorResult aggregate,
            bool useObservedStart)
        {
            if (aggregate.Count == 0) return;

            if (useObservedStart && firstTimestamp < BucketStart)
                BucketStart = firstTimestamp;

            if (Count == 0)
                FirstValue = aggregate.Min;
            LastValue = aggregate.Max;

            Count += aggregate.Count;
            Sum += aggregate.Sum;
            if (aggregate.Min < Min) Min = aggregate.Min;
            if (aggregate.Max > Max) Max = aggregate.Max;
        }

        public void AddMetadataBlock(in BlockDescriptor descriptor, bool useObservedStart)
        {
            if (useObservedStart && descriptor.MinTimestamp < BucketStart)
                BucketStart = descriptor.MinTimestamp;

            // First/Last 不会走元数据快路径（ShouldUsePointAggregatePath 已强制走点路径），
            // 因此这里仅在两个标记集都满足时维护 First/Last 的“最佳近似”，否则保持不变。
            if (Count == 0 && descriptor.HasAggregateMinMax)
                FirstValue = descriptor.AggregateMin;

            if (descriptor.HasAggregateMinMax)
                LastValue = descriptor.AggregateMax;

            Count += descriptor.Count;

            if (descriptor.HasAggregateSumCount)
                Sum += descriptor.AggregateSum;

            if (descriptor.HasAggregateMinMax)
            {
                if (descriptor.AggregateMin < Min) Min = descriptor.AggregateMin;
                if (descriptor.AggregateMax > Max) Max = descriptor.AggregateMax;
            }
        }

        /// <summary>
        /// 用 MemTable Series 的运行期聚合快照合并到当前桶，避免逐点扫描 <see cref="ReadOnlyMemory{DataPoint}"/>。
        /// 调用方必须保证 MemTable 切片完整落入当前桶范围。
        /// </summary>
        public void AddMemTableAggregate(
            int count, long minTs, double sum, double min, double max, bool useObservedStart)
        {
            if (count == 0) return;

            if (useObservedStart && minTs < BucketStart)
                BucketStart = minTs;

            if (Count == 0)
                FirstValue = min;
            LastValue = max;

            Count += count;
            Sum += sum;
            if (min < Min) Min = min;
            if (max > Max) Max = max;
        }

        public readonly AggregateBucket ToBucket(Aggregator aggregator)
        {
            return new AggregateBucket(
                BucketStart,
                BucketEndExclusive,
                Count,
                ComputeValue(aggregator, Count, Sum, Min, Max, FirstValue, LastValue));
        }
    }

    /// <summary>构建或复用与 <see cref="SegmentManagerSnapshot"/> 绑定的 SegmentId → SegmentReader 映射。</summary>
    private Dictionary<long, SegmentReader> BuildReaderMap(SegmentManagerSnapshot snapshot)
    {
        var cached = Volatile.Read(ref _readerMapCache);
        if (cached is not null && ReferenceEquals(cached.Snapshot, snapshot))
            return cached.ReadersBySegmentId;

        var readers = snapshot.Readers;
        var map = new Dictionary<long, SegmentReader>(readers.Count);
        foreach (var r in readers)
            map[r.Header.SegmentId] = r;

        Volatile.Write(ref _readerMapCache, new ReaderMapCache(snapshot, map));
        return map;
    }

    private sealed class ReaderMapCache
    {
        public ReaderMapCache(
            SegmentManagerSnapshot snapshot,
            Dictionary<long, SegmentReader> readersBySegmentId)
        {
            Snapshot = snapshot;
            ReadersBySegmentId = readersBySegmentId;
        }

        public SegmentManagerSnapshot Snapshot { get; }

        public Dictionary<long, SegmentReader> ReadersBySegmentId { get; }
    }

    /// <summary>
    /// 判定 <paramref name="timestamp"/> 是否被 <paramref name="tombstones"/> 列表中的任意墓碑覆盖。
    /// 对小集合（≤ 4 个）线性扫描；超过 4 个时仍线性扫描（v1 简化，通常墓碑数量很少）。
    /// </summary>
    private static bool IsCoveredByTombstones(long timestamp, IReadOnlyList<Tombstone> tombstones)
    {
        foreach (var tomb in tombstones)
        {
            if (timestamp >= tomb.FromTimestamp && timestamp <= tomb.ToTimestamp)
                return true;
        }
        return false;
    }

    /// <summary>将 <see cref="FieldValue"/> 转换为 double，用于数值聚合。</summary>
    private static double ToDouble(FieldValue value)
    {
        return value.Type switch
        {
            FieldType.Float64 => value.AsDouble(),
            FieldType.Int64 => (double)value.AsLong(),
            FieldType.Boolean => value.AsBool() ? 1.0 : 0.0,
            _ => throw new NotSupportedException(
                $"字段类型 {value.Type} 不支持数值聚合。仅支持 Float64 / Int64 / Boolean。"),
        };
    }

    /// <summary>若字段类型为 String，则抛出 <see cref="NotSupportedException"/>。</summary>
    private static void ThrowIfString(FieldType fieldType)
    {
        if (fieldType == FieldType.String)
            throw new NotSupportedException(
                "String 字段不支持聚合查询。仅支持 Float64 / Int64 / Boolean 字段。");
    }

    /// <summary>根据聚合函数类型计算最终值。</summary>
    private static double ComputeValue(
        Aggregator aggregator,
        long count,
        double sum,
        double min,
        double max,
        double firstValue,
        double lastValue)
    {
        return aggregator switch
        {
            Aggregator.Count => (double)count,
            Aggregator.Sum => sum,
            Aggregator.Min => min,
            Aggregator.Max => max,
            Aggregator.Avg => count == 0 ? 0.0 : sum / count,
            Aggregator.First => firstValue,
            Aggregator.Last => lastValue,
            Aggregator.None => 0.0,
            _ => throw new ArgumentOutOfRangeException(nameof(aggregator), aggregator, null),
        };
    }
}
