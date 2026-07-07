using System.Runtime.InteropServices;
using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;

namespace SonnetDB.Query;

/// <summary>
/// brute-force KNN 召回执行器。
/// <para>
/// 对 MemTable 与相关 Segment 的 VECTOR 列做顺扫，维护大小为 k 的候选集，
/// 最终按距离升序返回前 k 条最近邻结果。段侧用 <see cref="MultiSegmentIndex"/> 的
/// block skip-index（series/field/时间窗 prefix-max 剪枝）只取候选 block，
/// 而非逐 series 全块扫描（I8）。
/// </para>
/// <para>
/// 靠 <see cref="System.Threading.Tasks.Parallel.ForEach"/> 并行扫描多序列；
/// 命中 metric 一致且无墓碑的 block 走段内 HNSW ANN 加速，否则精确扫描。
/// </para>
/// </summary>
internal static class KnnExecutor
{
    /// <summary>
    /// 执行 KNN 搜索。
    /// </summary>
    /// <param name="memTables">MemTable 内存层（active + sealing）。</param>
    /// <param name="segmentIndex">
    /// 当前段集合的联合索引快照；用于按 (series, field, 时间窗) 做 block 级跳跃剪枝，
    /// 只召回候选 block 而非逐 series 全块扫描。必须与 <paramref name="segmentReaders"/> 同源
    /// （同一读租约 / 快照），否则可能出现 SegmentId 对不上而漏读的候选段。
    /// </param>
    /// <param name="segmentReaders">当前段读取器快照（只读，可并发访问）。</param>
    /// <param name="matchedSeries">经过 tag 过滤后的候选序列列表。</param>
    /// <param name="vectorField">向量列名（必须是 <see cref="FieldType.Vector"/> 类型的 FIELD 列）。</param>
    /// <param name="queryVector">查询向量；维度必须与列定义一致。</param>
    /// <param name="k">返回最近邻数量上限（≥ 1）。</param>
    /// <param name="metric">距离度量方式。</param>
    /// <param name="timeRange">时间范围过滤（闭区间，毫秒 UTC）。</param>
    /// <param name="tombstones">
    /// 墓碑集合，用于过滤已被逻辑删除的点；为 null 时不过滤。
    /// 必须显式传入（通常为 <c>tsdb.Tombstones</c>），避免漏过滤已删除向量。
    /// </param>
    /// <returns>
    /// 按距离升序排列的最近邻结果列表，长度 ≤ <paramref name="k"/>。
    /// 若无候选点则返回空列表。
    /// </returns>
    public static IReadOnlyList<KnnSearchResult> Execute(
        IReadOnlyList<MemTable> memTables,
        MultiSegmentIndex segmentIndex,
        IReadOnlyList<SegmentReader> segmentReaders,
        IReadOnlyList<SeriesEntry> matchedSeries,
        string vectorField,
        ReadOnlyMemory<float> queryVector,
        int k,
        KnnMetric metric,
        TimeRange timeRange,
        TombstoneTable? tombstones)
    {
        ArgumentNullException.ThrowIfNull(memTables);
        ArgumentNullException.ThrowIfNull(segmentIndex);
        ArgumentNullException.ThrowIfNull(segmentReaders);
        ArgumentNullException.ThrowIfNull(matchedSeries);
        ArgumentNullException.ThrowIfNull(vectorField);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        if (matchedSeries.Count == 0)
            return [];

        // SegmentId → SegmentReader 映射（只读，跨并行任务共享）。候选 block 由
        // MultiSegmentIndex 给出，需按 SegmentId 回到对应 reader 读取真实 payload。
        var readersBySegmentId = BuildReaderMap(segmentReaders);

        // 候选集（锁保护跨线程合并）
        var allCandidates = new List<(double Dist, long Ts, ulong Sid)>();
        var mergeLock = new object();

        // 并行扫描：每个 series 独立收集候选，最后合并
        Parallel.ForEach(
            matchedSeries,
            () => new List<(double Dist, long Ts, ulong Sid)>(),
            (series, _, localCandidates) =>
            {
                // 1. 扫描全部 MemTable（active + sealing）
                foreach (var memTable in memTables)
                    ScanMemTable(memTable, series.Id, vectorField, queryVector, metric, timeRange, localCandidates);

                // 此 series/field 上若存在墓碑，ANN sidecar 路径返回的候选可能在后续墓碑过滤后剩余 < k，
                // 故强制走精确扫描（详见 ScanSegmentBlock 注释）。此判定只依赖 series/field，
                // 对该 series 的所有候选 block 一致，故按 series 计算一次。
                bool hasTombstonesForSeriesField = tombstones is not null
                    && tombstones.Count > 0
                    && tombstones.GetForSeriesField(series.Id, vectorField).Count > 0;

                // 2. 扫描 Segments：用 block skip-index 只取与 (series, field, 时间窗) 相交的候选 block，
                //    段级时间剪枝 + block 级 prefix-max 剪枝均在 LookupCandidates 内完成（I8）。
                var candidates = segmentIndex.LookupCandidates(
                    series.Id, vectorField, timeRange.FromInclusive, timeRange.ToInclusive);
                foreach (var blockRef in candidates)
                {
                    if (!readersBySegmentId.TryGetValue(blockRef.SegmentId, out var reader))
                        continue;

                    ScanSegmentBlock(
                        reader, blockRef.Descriptor, series.Id, vectorField, queryVector,
                        k, metric, timeRange, hasTombstonesForSeriesField, localCandidates);
                }

                return localCandidates;
            },
            localCandidates =>
            {
                if (localCandidates.Count == 0)
                    return;

                lock (mergeLock)
                    allCandidates.AddRange(localCandidates);
            });

        if (allCandidates.Count == 0)
            return [];

        // 过滤已被逻辑删除（墓碑覆盖）的点，语义与 QueryEngine 点查一致。
        // 压缩前墓碑仍在 TombstoneTable 中，若不在此过滤会返回已删除的向量。
        if (tombstones is not null && tombstones.Count > 0)
        {
            allCandidates.RemoveAll(c => tombstones.IsCovered(c.Sid, vectorField, c.Ts));
            if (allCandidates.Count == 0)
                return [];
        }

        // 按距离选 top-k：用大小 ≤ k 的最大堆（PriorityQueue + 反向 comparer），
        // O(N log k) 代替原 List.Sort 的 O(N log N)；N 远大于 k 时显著降低排序开销。
        // 堆顶始终是当前 top-k 里距离最远的候选；新候选触发 EnqueueDequeue 自动淘汰更远者。
        int take = Math.Min(k, allCandidates.Count);
        var heap = new PriorityQueue<(long Ts, ulong Sid), double>(
            initialCapacity: take,
            comparer: Comparer<double>.Create(static (a, b) => b.CompareTo(a)));

        foreach (var c in CollectionsMarshal.AsSpan(allCandidates))
        {
            if (heap.Count < take)
                heap.Enqueue((c.Ts, c.Sid), c.Dist);
            else
                heap.EnqueueDequeue((c.Ts, c.Sid), c.Dist);
        }

        // 堆按距离降序 dequeue，结果需升序——从数组末尾倒序填回。
        var results = new KnnSearchResult[take];
        for (int i = take - 1; i >= 0; i--)
        {
            heap.TryDequeue(out var element, out double dist);
            results[i] = new KnnSearchResult(element.Ts, element.Sid, dist);
        }

        return results;
    }

    // ── 私有：SegmentId → SegmentReader 映射 ────────────────────────────────

    private static Dictionary<long, SegmentReader> BuildReaderMap(IReadOnlyList<SegmentReader> readers)
    {
        var map = new Dictionary<long, SegmentReader>(readers.Count);
        foreach (var r in readers)
            map[r.Header.SegmentId] = r;
        return map;
    }

    // ── 私有：扫描 MemTable ──────────────────────────────────────────────────

    private static void ScanMemTable(
        MemTable memTable,
        ulong seriesId,
        string vectorField,
        ReadOnlyMemory<float> queryVector,
        KnnMetric metric,
        TimeRange timeRange,
        List<(double Dist, long Ts, ulong Sid)> candidates)
    {
        var key = new SeriesFieldKey(seriesId, vectorField);
        var bucket = memTable.TryGet(in key);
        if (bucket is null || bucket.FieldType != FieldType.Vector)
            return;

        var querySpan = queryVector.Span;
        var slice = bucket.SnapshotRange(timeRange.FromInclusive, timeRange.ToInclusive);
        foreach (var dp in slice.Span)
        {
            var vecSpan = dp.Value.AsVector().Span;
            double dist = VectorDistance.Compute(metric, querySpan, vecSpan);
            candidates.Add((dist, dp.Timestamp, seriesId));
        }
    }

    // ── 私有：扫描单个候选 Block ────────────────────────────────────────────

    /// <summary>
    /// 扫描一个已由 block skip-index 命中的候选 Block（其 SeriesId/时间窗已与查询相交，
    /// 但 FieldType 仍需最终确认）。命中 metric 一致且无墓碑时走段内 ANN 加速，否则精确扫描。
    /// </summary>
    private static void ScanSegmentBlock(
        SegmentReader reader,
        in BlockDescriptor block,
        ulong seriesId,
        string vectorField,
        ReadOnlyMemory<float> queryVector,
        int k,
        KnnMetric metric,
        TimeRange timeRange,
        bool hasTombstonesForSeriesField,
        List<(double Dist, long Ts, ulong Sid)> candidates)
    {
        // skip-index 已按 (SeriesId, FieldName) 精确定位桶并做过时间窗剪枝，
        // 这里仅需确认列类型（同名列理论上恒为 Vector，保险起见判定一次）。
        if (block.FieldType != FieldType.Vector)
            return;

        var querySpan = queryVector.Span;

        // I7：只有当 block 上的向量索引建图度量与查询度量一致时才走 ANN 加速；
        // 度量不一致（如 L2 查询命中 cosine 建的图）会落到下方精确扫描，保证结果正确。
        // 此 series/field 上若存在墓碑，ANN sidecar 按 candidateLimit (≈ k*8) 截断后再喂给上层去重，
        // 若大量候选恰好是已删除点，最终结果会少于用户请求的 K——为保正确性强制走精确扫描。
        if (!hasTombstonesForSeriesField
            && reader.TryGetVectorIndexReader(block, out var vectorIndex)
            && vectorIndex.Metric == metric)
        {
            var data = reader.ReadBlock(block);
            var timestamps = BlockDecoder.DecodeTimestamps(block, data.TimestampPayload);
            int candidateLimit = Math.Min(block.Count, Math.Max(k * 8, vectorIndex.Ef * 2));
            var annHits = vectorIndex.Search(
                querySpan,
                data.ValuePayload,
                timestamps,
                candidateLimit,
                metric);
            CollectIndexedBlockCandidates(
                querySpan,
                data.ValuePayload,
                timestamps,
                annHits,
                block.Count,
                k,
                candidateLimit,
                metric,
                timeRange,
                seriesId,
                candidates);
            return;
        }

        var points = reader.DecodeBlockRange(block, timeRange.FromInclusive, timeRange.ToInclusive);
        foreach (var dp in points)
        {
            var vecSpan = dp.Value.AsVector().Span;
            double dist = VectorDistance.Compute(metric, querySpan, vecSpan);
            candidates.Add((dist, dp.Timestamp, seriesId));
        }
    }

    /// <summary>
    /// 合并 ANN 命中与必要的精确补扫结果。
    /// 当 ANN 命中已足够覆盖 Top-K，或本次 ANN 已覆盖整个 block 时，直接采用 ANN 结果；
    /// 否则对未命中的点位做一次精确补扫，避免“部分 ANN 命中 + 整块精确回退”把同一点重复计入候选。
    /// </summary>
    internal static void CollectIndexedBlockCandidates(
        ReadOnlySpan<float> queryVector,
        ReadOnlySpan<byte> valPayload,
        ReadOnlySpan<long> timestamps,
        IReadOnlyList<VectorSearchResult> annHits,
        int pointCount,
        int k,
        int candidateLimit,
        KnnMetric metric,
        TimeRange timeRange,
        ulong seriesId,
        List<(double Dist, long Ts, ulong Sid)> candidates)
    {
        ArgumentNullException.ThrowIfNull(annHits);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        var acceptedHits = new List<VectorSearchResult>(Math.Min(k, annHits.Count));
        foreach (var hit in annHits)
        {
            if (!timeRange.Contains(hit.Timestamp))
                continue;

            acceptedHits.Add(hit);
            if (acceptedHits.Count >= k)
                break;
        }

        if (acceptedHits.Count >= k || candidateLimit >= pointCount)
        {
            AddAnnHits(acceptedHits, seriesId, candidates);
            return;
        }

        HashSet<int>? acceptedPointIndexes = null;
        if (acceptedHits.Count > 0)
        {
            acceptedPointIndexes = new HashSet<int>(acceptedHits.Count);
            foreach (var hit in acceptedHits)
            {
                candidates.Add((hit.Distance, hit.Timestamp, seriesId));
                acceptedPointIndexes.Add(hit.PointIndex);
            }
        }

        int dimension = queryVector.Length;
        for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
        {
            if (acceptedPointIndexes?.Contains(pointIndex) == true)
                continue;

            long timestamp = timestamps[pointIndex];
            if (!timeRange.Contains(timestamp))
                continue;

            double distance = VectorDistance.Compute(metric, queryVector, GetVector(valPayload, pointIndex, dimension));
            candidates.Add((distance, timestamp, seriesId));
        }
    }

    private static void AddAnnHits(
        IReadOnlyList<VectorSearchResult> hits,
        ulong seriesId,
        List<(double Dist, long Ts, ulong Sid)> candidates)
    {
        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            candidates.Add((hit.Distance, hit.Timestamp, seriesId));
        }
    }

    private static ReadOnlySpan<float> GetVector(ReadOnlySpan<byte> valPayload, int pointIndex, int dimension)
        => MemoryMarshal.Cast<byte, float>(
            valPayload.Slice(pointIndex * dimension * sizeof(float), dimension * sizeof(float)));
}
