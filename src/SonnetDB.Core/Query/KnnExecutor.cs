using System.Runtime.InteropServices;
using SonnetDB.Catalog;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;

namespace SonnetDB.Query;

/// <summary>
/// brute-force KNN 召回执行器。
/// <para>
/// 对 MemTable 与全部 Segment 的 VECTOR 列做顺扫（段级时间窗剪枝 + 并行），
/// 维护大小为 k 的候选集，最终按距离升序返回前 k 条最近邻结果。
/// </para>
/// <para>
/// 第一版无 ANN 索引（无 HNSW），靠 <see cref="System.Threading.Tasks.Parallel.ForEach"/> 并行扫描多序列。
/// SIMD 加速与 HNSW 段内索引将在后续 PR 中追加。
/// </para>
/// </summary>
internal static class KnnExecutor
{
    /// <summary>
    /// 执行 KNN 搜索。
    /// </summary>
    /// <param name="memTable">MemTable 内存层。</param>
    /// <param name="segmentReaders">当前段读取器快照（只读，可并发访问）。</param>
    /// <param name="matchedSeries">经过 tag 过滤后的候选序列列表。</param>
    /// <param name="vectorField">向量列名（必须是 <see cref="FieldType.Vector"/> 类型的 FIELD 列）。</param>
    /// <param name="queryVector">查询向量；维度必须与列定义一致。</param>
    /// <param name="k">返回最近邻数量上限（≥ 1）。</param>
    /// <param name="metric">距离度量方式。</param>
    /// <param name="timeRange">时间范围过滤（闭区间，毫秒 UTC）。</param>
    /// <returns>
    /// 按距离升序排列的最近邻结果列表，长度 ≤ <paramref name="k"/>。
    /// 若无候选点则返回空列表。
    /// </returns>
    public static IReadOnlyList<KnnSearchResult> Execute(
        MemTable memTable,
        IReadOnlyList<SegmentReader> segmentReaders,
        IReadOnlyList<SeriesEntry> matchedSeries,
        string vectorField,
        ReadOnlyMemory<float> queryVector,
        int k,
        KnnMetric metric,
        TimeRange timeRange)
    {
        ArgumentNullException.ThrowIfNull(memTable);
        ArgumentNullException.ThrowIfNull(segmentReaders);
        ArgumentNullException.ThrowIfNull(matchedSeries);
        ArgumentNullException.ThrowIfNull(vectorField);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        if (matchedSeries.Count == 0)
            return [];

        // 候选集（锁保护跨线程合并）
        var allCandidates = new List<(double Dist, long Ts, ulong Sid)>();
        var mergeLock = new object();

        // 并行扫描：每个 series 独立收集候选，最后合并
        Parallel.ForEach(
            matchedSeries,
            () => new List<(double Dist, long Ts, ulong Sid)>(),
            (series, _, localCandidates) =>
            {
                // 1. 扫描 MemTable
                ScanMemTable(memTable, series.Id, vectorField, queryVector, metric, timeRange, localCandidates);

                // 2. 扫描 Segments（段级时间窗剪枝）
                foreach (var reader in segmentReaders)
                {
                    // 段不与查询时间窗重叠 → 跳过整段
                    if (reader.MaxTimestamp < timeRange.FromInclusive
                        || reader.MinTimestamp > timeRange.ToInclusive)
                        continue;

                    ScanSegment(reader, series.Id, vectorField, queryVector, k, metric, timeRange, localCandidates);
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

        // 按距离升序排序，取前 k 条。
        // TODO：候选量远大于 k 时可改用 O(N log k) 的 max-heap 维护，减少排序开销（PR #6x 优化）。
        allCandidates.Sort(static (a, b) => a.Dist.CompareTo(b.Dist));

        int take = Math.Min(k, allCandidates.Count);
        var results = new KnnSearchResult[take];
        for (int i = 0; i < take; i++)
        {
            var (dist, ts, sid) = allCandidates[i];
            results[i] = new KnnSearchResult(ts, sid, dist);
        }

        return results;
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

    // ── 私有：扫描单个 Segment ──────────────────────────────────────────────

    private static void ScanSegment(
        SegmentReader reader,
        ulong seriesId,
        string vectorField,
        ReadOnlyMemory<float> queryVector,
        int k,
        KnnMetric metric,
        TimeRange timeRange,
        List<(double Dist, long Ts, ulong Sid)> candidates)
    {
        var querySpan = queryVector.Span;

        foreach (var block in reader.Blocks)
        {
            // Block 过滤：SeriesId + FieldName + FieldType + 时间窗
            if (block.SeriesId != seriesId)
                continue;
            if (!string.Equals(block.FieldName, vectorField, StringComparison.Ordinal))
                continue;
            if (block.FieldType != FieldType.Vector)
                continue;
            if (block.MaxTimestamp < timeRange.FromInclusive
                || block.MinTimestamp > timeRange.ToInclusive)
                continue;

            if (metric == KnnMetric.Cosine && reader.TryGetVectorIndexReader(block, out var vectorIndex))
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
                continue;
            }

            var points = reader.DecodeBlockRange(block, timeRange.FromInclusive, timeRange.ToInclusive);
            foreach (var dp in points)
            {
                var vecSpan = dp.Value.AsVector().Span;
                double dist = VectorDistance.Compute(metric, querySpan, vecSpan);
                candidates.Add((dist, dp.Timestamp, seriesId));
            }
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
