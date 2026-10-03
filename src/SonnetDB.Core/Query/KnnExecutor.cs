using System.Diagnostics;
using System.Runtime.InteropServices;
using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Sql.Execution;
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
/// 通过 SQL 共享 worker 与内存预算受控扫描多序列，所有 worker 共用最多 k 个候选；
/// 命中 metric 一致且无墓碑的 block 走段内 HNSW ANN 加速，否则精确扫描。
/// </para>
/// </summary>
internal static class KnnExecutor
{
    private static readonly TimeSpan BudgetedSearchTimeout = TimeSpan.FromMinutes(5);

    internal static void ThrowIfBudgetedCancelled(long startedAt)
    {
        SqlExecutor.ThrowIfCancellationRequested();
        if (SqlRowRetentionBudget.HasExecutionBudget && Stopwatch.GetElapsedTime(startedAt) >= BudgetedSearchTimeout)
            throw new RoutineExecutionException(RoutineErrorCodes.Cancelled,
                "KNN SQL 物化预算查询已超过五分钟执行上限。",
                new TimeoutException("KNN SQL 物化预算查询已超过五分钟执行上限。"));
    }

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
    /// <param name="replacements">向量替换记录。</param>
    /// <param name="queryEngine">提供替换后可见点的查询器。</param>
    /// <param name="cancellationToken">可选取消信号；与当前 SQL 根执行的取消信号共同生效。</param>
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
        TombstoneTable? tombstones,
        MeasurementVectorReplacementStore? replacements = null,
        QueryEngine? queryEngine = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memTables);
        ArgumentNullException.ThrowIfNull(segmentIndex);
        ArgumentNullException.ThrowIfNull(segmentReaders);
        ArgumentNullException.ThrowIfNull(matchedSeries);
        ArgumentNullException.ThrowIfNull(vectorField);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        SqlQueryResources? resources = SqlQueryResources.Current;
        CancellationToken rootToken = resources?.CancellationToken ?? default;
        using CancellationTokenSource? linkedSource = rootToken.CanBeCanceled && cancellationToken.CanBeCanceled
            && rootToken != cancellationToken
                ? CancellationTokenSource.CreateLinkedTokenSource(rootToken, cancellationToken)
                : null;
        CancellationToken token = linkedSource?.Token ?? (rootToken.CanBeCanceled ? rootToken : cancellationToken);
        token.ThrowIfCancellationRequested();

        if (matchedSeries.Count == 0)
            return [];

        // SegmentId → SegmentReader 映射（只读，跨并行任务共享）。候选 block 由
        // MultiSegmentIndex 给出，需按 SegmentId 回到对应 reader 读取真实 payload。
        var readersBySegmentId = BuildReaderMap(segmentReaders);

        using var candidates = new BoundedCandidateSet(k, vectorField, tombstones, token, resources);
        long estimatedRows = resources?.EstimatedRows ?? 0;
        foreach (var memTable in memTables)
            estimatedRows = Math.Max(estimatedRows, memTable.PointCount);

        // worker 只保留当前存储页；不会建立本地候选 List 或 O(worker * k) 的候选堆。
        _ = SqlParallelExecution.MapOrdered(
            matchedSeries,
            series =>
            {
                token.ThrowIfCancellationRequested();
                if (replacements?.HasSeriesField(series.Id, vectorField) == true)
                {
                    if (queryEngine is null)
                        throw new InvalidOperationException("VECTOR 替换记录需要可见点查询器。");
                    foreach (var point in queryEngine.Execute(new PointQuery(series.Id, vectorField, timeRange)))
                    {
                        token.ThrowIfCancellationRequested();
                        double distance = VectorDistance.Compute(
                            metric, queryVector.Span, point.Value.AsVector().Span);
                        candidates.Add(distance, point.Timestamp, series.Id);
                    }
                    return true;
                }

                // 1. 扫描全部 MemTable（active + sealing）
                foreach (var memTable in memTables)
                    ScanMemTable(memTable, series.Id, vectorField, queryVector, metric, timeRange, candidates);

                // 此 series/field 上若存在墓碑，ANN sidecar 路径返回的候选可能在后续墓碑过滤后剩余 < k，
                // 故强制走精确扫描（详见 ScanSegmentBlock 注释）。此判定只依赖 series/field，
                // 对该 series 的所有候选 block 一致，故按 series 计算一次。
                bool hasTombstonesForSeriesField = tombstones is not null
                    && tombstones.Count > 0
                    && tombstones.GetForSeriesField(series.Id, vectorField).Count > 0;

                // 2. 扫描 Segments：用 block skip-index 只取与 (series, field, 时间窗) 相交的候选 block，
                //    段级时间剪枝 + block 级 prefix-max 剪枝均在 LookupCandidates 内完成（I8）。
                var blocks = segmentIndex.LookupCandidates(
                    series.Id, vectorField, timeRange.FromInclusive, timeRange.ToInclusive);
                foreach (var blockRef in blocks)
                {
                    token.ThrowIfCancellationRequested();
                    if (!readersBySegmentId.TryGetValue(blockRef.SegmentId, out var reader))
                        continue;

                    ScanSegmentBlock(
                        reader, blockRef.Descriptor, series.Id, vectorField, queryVector,
                        k, metric, timeRange, hasTombstonesForSeriesField, candidates);
                }

                return true;
            },
            operatorName: "measurement_knn",
            estimatedRows);

        token.ThrowIfCancellationRequested();
        return candidates.GetResults();
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
        BoundedCandidateSet candidates)
    {
        candidates.CancellationToken.ThrowIfCancellationRequested();
        var key = new SeriesFieldKey(seriesId, vectorField);
        var bucket = memTable.TryGet(in key);
        if (bucket is null || bucket.FieldType != FieldType.Vector)
            return;

        var querySpan = queryVector.Span;
        var slice = bucket.SnapshotRange(timeRange.FromInclusive, timeRange.ToInclusive);
        foreach (var dp in slice.Span)
        {
            candidates.CancellationToken.ThrowIfCancellationRequested();
            var vecSpan = dp.Value.AsVector().Span;
            double dist = VectorDistance.Compute(metric, querySpan, vecSpan);
            candidates.Add(dist, dp.Timestamp, seriesId);
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
        BoundedCandidateSet candidates)
    {
        candidates.CancellationToken.ThrowIfCancellationRequested();
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
            int candidateLimit = (int)Math.Min(block.Count, Math.Max((long)k * 8, (long)vectorIndex.Ef * 2));
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
            candidates.CancellationToken.ThrowIfCancellationRequested();
            var vecSpan = dp.Value.AsVector().Span;
            double dist = VectorDistance.Compute(metric, querySpan, vecSpan);
            candidates.Add(dist, dp.Timestamp, seriesId);
        }
    }

    /// <summary>
    /// 合并 ANN 命中与必要的精确补扫结果。
    /// 当 ANN 命中已足够覆盖 Top-K，或本次 ANN 已覆盖整个 block 时，直接采用 ANN 结果；
    /// 否则丢弃此次 ANN 命中并精确扫描整个时间窗，避免同一点重复计入候选。
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
        BoundedCandidateSet candidates)
    {
        ArgumentNullException.ThrowIfNull(annHits);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        int acceptedCount = 0;
        foreach (var hit in annHits)
        {
            candidates.CancellationToken.ThrowIfCancellationRequested();
            if (!timeRange.Contains(hit.Timestamp))
                continue;

            if (++acceptedCount >= k)
                break;
        }

        if (acceptedCount >= k || candidateLimit >= pointCount)
        {
            foreach (var hit in annHits)
            {
                candidates.CancellationToken.ThrowIfCancellationRequested();
                if (timeRange.Contains(hit.Timestamp))
                    candidates.Add(hit.Distance, hit.Timestamp, seriesId);
            }
            return;
        }

        int dimension = queryVector.Length;
        for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
        {
            candidates.CancellationToken.ThrowIfCancellationRequested();
            long timestamp = timestamps[pointIndex];
            if (!timeRange.Contains(timestamp))
                continue;

            double distance = VectorDistance.Compute(metric, queryVector, GetVector(valPayload, pointIndex, dimension));
            candidates.Add(distance, timestamp, seriesId);
        }
    }

    /// <summary>所有扫描 worker 共享的有界最大堆；候选进入堆前过滤墓碑并预留 SQL 工作集预算。</summary>
    internal sealed class BoundedCandidateSet : IDisposable
    {
        // 包括堆扩容余量、值/优先级和最终结果容器；是保守准入估算，不是 CLR heap 硬上限。
        internal const int EstimatedBytesPerCandidate = 256;
        private readonly int _limit;
        private readonly string _field;
        private readonly TombstoneTable? _tombstones;
        private readonly SqlQueryResources.SqlOperatorMemoryReservation? _reservation;
        private readonly PriorityQueue<Candidate, Candidate> _heap = new(CandidateComparer.Instance);
        private readonly object _gate = new();
        private readonly long _materializationStartedAt = Stopwatch.GetTimestamp();

        internal BoundedCandidateSet(int limit, string field, TombstoneTable? tombstones,
            CancellationToken cancellationToken = default, SqlQueryResources? resources = null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
            ArgumentNullException.ThrowIfNull(field);
            _limit = limit;
            _field = field;
            _tombstones = tombstones;
            CancellationToken = cancellationToken;
            // 执行期累计预算已为候选保留阶段预留共享内存，不能再对同一候选重复预留。
            // 非 opt-in 调用继续使用既有的 256 字节 Top-K 工作集准入。
            _reservation = SqlRowRetentionBudget.HasExecutionBudget ? null : resources?.CreateReservation();
        }

        internal CancellationToken CancellationToken { get; }

        internal int Count
        {
            get
            {
                lock (_gate)
                    return _heap.Count;
            }
        }

        internal void Add(double distance, long timestamp, ulong seriesId)
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (SqlRowRetentionBudget.HasExecutionBudget)
                ThrowIfBudgetedCancelled(_materializationStartedAt);
            if (_tombstones?.IsCovered(seriesId, _field, timestamp) == true)
                return;

            var candidate = new Candidate(distance, timestamp, seriesId);
            lock (_gate)
            {
                CancellationToken.ThrowIfCancellationRequested();
                if (_heap.Count < _limit)
                {
                    if (_reservation?.TryReserve(EstimatedBytesPerCandidate) == false)
                        throw new InvalidOperationException("measurement KNN Top-K 候选工作集超过 SQL 共享内存预算。");
                    RetainMaterializedCandidate(candidate);
                    _heap.Enqueue(candidate, candidate);
                }
                else if (Compare(candidate, _heap.Peek()) < 0)
                {
                    RetainMaterializedCandidate(candidate);
                    _heap.DequeueEnqueue(candidate, candidate);
                }
            }
        }

        private static void RetainMaterializedCandidate(Candidate candidate)
        {
            if (SqlRowRetentionBudget.HasExecutionBudget)
                SqlRowRetentionBudget.RetainForExecution([candidate.Timestamp, candidate.SeriesId, candidate.Distance]);
        }

        internal IReadOnlyList<KnnSearchResult> GetResults()
        {
            lock (_gate)
            {
                // 有序结果数组是独立的保留阶段；先为全部条目准入，再分配数组或消耗堆。
                if (SqlRowRetentionBudget.HasExecutionBudget)
                    foreach (var item in _heap.UnorderedItems)
                    {
                        CancellationToken.ThrowIfCancellationRequested();
                        ThrowIfBudgetedCancelled(_materializationStartedAt);
                        RetainMaterializedCandidate(item.Element);
                    }
                var results = new KnnSearchResult[_heap.Count];
                for (int index = results.Length - 1; index >= 0; index--)
                {
                    CancellationToken.ThrowIfCancellationRequested();
                    if (SqlRowRetentionBudget.HasExecutionBudget)
                        ThrowIfBudgetedCancelled(_materializationStartedAt);
                    var candidate = _heap.Dequeue();
                    results[index] = new KnnSearchResult(candidate.Timestamp, candidate.SeriesId, candidate.Distance);
                }
                return results;
            }
        }

        /// <summary>归还候选工作集的共享预算。</summary>
        public void Dispose() => _reservation?.Dispose();

        private static int Compare(Candidate left, Candidate right)
        {
            int distance = left.Distance.CompareTo(right.Distance);
            if (distance != 0)
                return distance;
            int timestamp = left.Timestamp.CompareTo(right.Timestamp);
            return timestamp != 0 ? timestamp : left.SeriesId.CompareTo(right.SeriesId);
        }

        private readonly record struct Candidate(double Distance, long Timestamp, ulong SeriesId);

        private sealed class CandidateComparer : IComparer<Candidate>
        {
            internal static CandidateComparer Instance { get; } = new();

            public int Compare(Candidate left, Candidate right) => BoundedCandidateSet.Compare(right, left);
        }
    }

    private static ReadOnlySpan<float> GetVector(ReadOnlySpan<byte> valPayload, int pointIndex, int dimension)
        => MemoryMarshal.Cast<byte, float>(
            valPayload.Slice(pointIndex * dimension * sizeof(float), dimension * sizeof(float)));
}
