using System.Diagnostics;
using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;

namespace SonnetDB.Engine.Compaction;

/// <summary>
/// 执行单个 <see cref="CompactionPlan"/>：读多个 <see cref="SegmentReader"/> → 合并 → 写新段。
/// <para>
/// 合并策略（v1）：
/// <list type="bullet">
///   <item><description>按 (SeriesId, FieldName) 分组；校验 FieldType 一致；</description></item>
///   <item><description>N 路堆合并按 timestamp 升序产出；同 timestamp 全部保留（不去重）；</description></item>
///   <item><description>输出使用 <see cref="SegmentWriter.Write"/> 写入新段。</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class SegmentCompactor
{
    private readonly SegmentWriter _writer;

    /// <summary>
    /// 创建 <see cref="SegmentCompactor"/> 实例。
    /// </summary>
    /// <param name="writerOptions">段写入选项；为 null 时使用默认。</param>
    public SegmentCompactor(SegmentWriterOptions? writerOptions = null)
    {
        _writer = new SegmentWriter(writerOptions);
    }

    /// <summary>
    /// 执行一个 <see cref="CompactionPlan"/>：从 <paramref name="readers"/> 中读取指定段，
    /// 合并后写入 <paramref name="newSegmentPath"/>。不修改 <see cref="SegmentManager"/>。
    /// </summary>
    /// <param name="plan">要执行的 Compaction 计划。</param>
    /// <param name="readers">按 SegmentId 索引的所有已打开 Reader 字典。</param>
    /// <param name="newSegmentId">新段的 SegmentId。</param>
    /// <param name="newSegmentPath">新段的输出文件路径。</param>
    /// <param name="tombstones">可选的墓碑集合；若非 null，合并时过滤被墓碑覆盖的数据点（物理删除）。</param>
    /// <param name="seriesCatalog">可选的 Series 目录；提供时会按 schema 为声明了向量索引的字段构建段内索引 section。</param>
    /// <param name="measurementCatalog">可选的 measurement schema 目录；需与 <paramref name="seriesCatalog"/> 一起提供。</param>
    /// <returns>Compaction 执行结果统计。</returns>
    /// <exception cref="ArgumentNullException">任何必选参数为 null 时抛出。</exception>
    /// <exception cref="InvalidOperationException">同 (SeriesId, FieldName) 的 FieldType 不一致时抛出。</exception>
    public CompactionResult Execute(
        CompactionPlan plan,
        IReadOnlyDictionary<long, SegmentReader> readers,
        long newSegmentId,
        string newSegmentPath,
        TombstoneTable? tombstones = null,
        SeriesCatalog? seriesCatalog = null,
        MeasurementCatalog? measurementCatalog = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(newSegmentPath);
        cancellationToken.ThrowIfCancellationRequested();

        var sw = Stopwatch.StartNew();

        // 1. 按 (SeriesId, FieldName) → (FieldType, List<(SegmentReader, BlockDescriptor)>) 分组
        var groups = CollectGroups(plan, readers, cancellationToken);

        int inputBlockCount = groups.Values.Sum(static g => g.Blocks.Count);

        // 2. 为每个 group 构建 MemTableSeries（N 路合并），并应用墓碑过滤
        var seriesList = new List<MemTableSeries>(groups.Count);
        foreach (var (key, group) in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mts = new MemTableSeries(key, group.FieldType);
            MergeAndAppend(mts, group.Blocks, readers, tombstones, cancellationToken);
            // 若该桶所有点均被墓碑覆盖，不生成空 Block（SegmentWriter 会跳过空 series）
            if (mts.Count > 0)
                seriesList.Add(mts);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 3. 写入新段
        IReadOnlyDictionary<SeriesFieldKey, VectorIndexDefinition>? vectorIndexes = null;
        if (seriesCatalog is not null && measurementCatalog is not null)
            vectorIndexes = VectorIndexBuildMap.Build(seriesList, seriesCatalog, measurementCatalog);
        cancellationToken.ThrowIfCancellationRequested();
        var buildResult = _writer.Write(seriesList, newSegmentId, newSegmentPath, vectorIndexes);
        cancellationToken.ThrowIfCancellationRequested();

        sw.Stop();
        long durationMicros = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;

        return new CompactionResult(
            NewSegmentId: newSegmentId,
            NewSegmentPath: newSegmentPath,
            RemovedSegmentIds: plan.SourceSegmentIds,
            InputBlockCount: inputBlockCount,
            OutputBlockCount: buildResult.BlockCount,
            OutputBytes: buildResult.TotalBytes,
            DurationMicros: durationMicros);
    }

    // ── 私有辅助 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 从计划的源段中，按 (SeriesId, FieldName) 分组收集所有 BlockDescriptor。
    /// </summary>
    private static Dictionary<SeriesFieldKey, FieldGroup> CollectGroups(
        CompactionPlan plan,
        IReadOnlyDictionary<long, SegmentReader> readers,
        CancellationToken cancellationToken)
    {
        var groups = new Dictionary<SeriesFieldKey, FieldGroup>();

        foreach (long segId in plan.SourceSegmentIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!readers.TryGetValue(segId, out var reader))
                continue;

            foreach (var block in reader.Blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = new SeriesFieldKey(block.SeriesId, block.FieldName);
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new FieldGroup(block.FieldType);
                    groups[key] = group;
                }
                else if (group.FieldType != block.FieldType)
                {
                    throw new InvalidOperationException(
                        $"FieldType conflict during compaction for series {block.SeriesId:X16}/{block.FieldName}: " +
                        $"expected {group.FieldType}, got {block.FieldType}.");
                }

                group.Blocks.Add((segId, block));
            }
        }

        return groups;
    }

    /// <summary>
    /// N 路堆合并多个 Block 的 DataPoint 到 <paramref name="mts"/>，按 timestamp 升序。
    /// 若 <paramref name="tombstones"/> 非 null，过滤被墓碑覆盖的数据点。
    /// </summary>
    private static void MergeAndAppend(
        MemTableSeries mts,
        List<(long SegId, BlockDescriptor Block)> blockRefs,
        IReadOnlyDictionary<long, SegmentReader> readers,
        TombstoneTable? tombstones,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (blockRefs.Count == 0)
            return;

        // 预先获取该 (SeriesId, FieldName) 对应的墓碑列表（避免重复查询）
        IReadOnlyList<Tombstone>? tombstoneList = null;
        if (tombstones != null)
            tombstoneList = tombstones.GetForSeriesField(mts.Key.SeriesId, mts.Key.FieldName);

        bool hasTombstones = tombstoneList is { Count: > 0 };

        if (blockRefs.Count == 1)
        {
            // 直接追加单个来源，无需堆合并
            var (segId, block) = blockRefs[0];
            if (readers.TryGetValue(segId, out var reader))
            {
                var points = reader.DecodeBlock(block);
                foreach (var dp in points)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (hasTombstones && IsCoveredByTombstones(dp.Timestamp, tombstoneList!))
                        continue;
                    mts.Append(dp.Timestamp, dp.Value);
                }
            }
            return;
        }

        // N 路最小堆合并
        // 优先队列：(timestamp, seqOrder, pointIndex, sourceIndex)
        // seqOrder 用于保持同 timestamp 的来源顺序稳定（按 SourceSegmentIds 原始顺序）
        var pq = new PriorityQueue<HeapEntry, HeapKey>();
        var decoded = new DataPoint[blockRefs.Count][];

        for (int i = 0; i < blockRefs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (segId, block) = blockRefs[i];
            if (!readers.TryGetValue(segId, out var reader))
            {
                decoded[i] = [];
                continue;
            }
            decoded[i] = reader.DecodeBlock(block);
            if (decoded[i].Length > 0)
            {
                pq.Enqueue(
                    new HeapEntry(i, 0),
                    new HeapKey(decoded[i][0].Timestamp, i, 0));
            }
        }

        while (pq.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = pq.Dequeue();
            var dp = decoded[entry.SourceIndex][entry.PointIndex];

            if (!hasTombstones || !IsCoveredByTombstones(dp.Timestamp, tombstoneList!))
                mts.Append(dp.Timestamp, dp.Value);

            int nextIdx = entry.PointIndex + 1;
            if (nextIdx < decoded[entry.SourceIndex].Length)
            {
                pq.Enqueue(
                    new HeapEntry(entry.SourceIndex, nextIdx),
                    new HeapKey(decoded[entry.SourceIndex][nextIdx].Timestamp, entry.SourceIndex, nextIdx));
            }
        }
    }

    /// <summary>
    /// 判定 timestamp 是否被列表中任意墓碑覆盖。
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

    private readonly record struct HeapEntry(int SourceIndex, int PointIndex);

    private readonly record struct HeapKey(long Timestamp, int SourceIndex, int PointIndex)
        : IComparable<HeapKey>
    {
        public int CompareTo(HeapKey other)
        {
            int cmp = Timestamp.CompareTo(other.Timestamp);
            if (cmp != 0) return cmp;
            cmp = SourceIndex.CompareTo(other.SourceIndex);
            if (cmp != 0) return cmp;
            return PointIndex.CompareTo(other.PointIndex);
        }
    }

    private sealed class FieldGroup
    {
        public FieldType FieldType { get; }
        public List<(long SegId, BlockDescriptor Block)> Blocks { get; } = [];

        public FieldGroup(FieldType fieldType)
        {
            FieldType = fieldType;
        }
    }
}
