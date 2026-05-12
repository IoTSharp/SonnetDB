namespace SonnetDB.Storage.Segments;

/// <summary>
/// 一组段的只读联合索引快照。引擎在段集合变更（Flush / Compact）时重建一份新的快照并原子替换。
/// <para>
/// 剪枝顺序：段级时间剪枝 → series 命中 → field 命中 → 段内时间窗二分。
/// </para>
/// </summary>
public sealed class MultiSegmentIndex
{
    /// <summary>所有段的索引列表（按 SegmentId 升序）。</summary>
    public IReadOnlyList<SegmentIndex> Segments { get; }

    /// <summary>包含的段数量。</summary>
    public int SegmentCount => Segments.Count;

    /// <summary>
    /// 所有段的全局最小时间戳（毫秒 UTC）；空集合时为 <see cref="long.MaxValue"/>。
    /// </summary>
    public long MinTimestamp { get; }

    /// <summary>
    /// 所有段的全局最大时间戳（毫秒 UTC）；空集合时为 <see cref="long.MinValue"/>。
    /// </summary>
    public long MaxTimestamp { get; }

    /// <summary>空快照单例：不含任何段。</summary>
    public static readonly MultiSegmentIndex Empty = new(Array.Empty<SegmentIndex>());

    /// <summary>
    /// 初始化 <see cref="MultiSegmentIndex"/> 实例。
    /// </summary>
    /// <param name="segments">段索引列表（调用方保证按 SegmentId 升序）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="segments"/> 为 null 时抛出。</exception>
    public MultiSegmentIndex(IReadOnlyList<SegmentIndex> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        Segments = segments;

        if (segments.Count == 0)
        {
            MinTimestamp = long.MaxValue;
            MaxTimestamp = long.MinValue;
        }
        else
        {
            long min = long.MaxValue;
            long max = long.MinValue;
            foreach (var seg in segments)
            {
                if (seg.MinTimestamp < min) min = seg.MinTimestamp;
                if (seg.MaxTimestamp > max) max = seg.MaxTimestamp;
            }
            MinTimestamp = min;
            MaxTimestamp = max;
        }
    }

    /// <summary>
    /// 跨所有段查找候选 Block（按段 SegmentId 升序，再段内按 MinTimestamp 升序）。
    /// <para>
    /// 剪枝顺序：①段级时间过滤（<see cref="SegmentIndex.OverlapsTimeRange"/>） →
    /// ②series 命中 → ③field 命中 → ④段内时间窗二分（<see cref="SegmentIndex.GetBlocks(ulong, string, long, long)"/>）。
    /// </para>
    /// </summary>
    /// <param name="seriesId">目标序列 ID。</param>
    /// <param name="fieldName">目标字段名。</param>
    /// <param name="fromInclusive">查询起始时间戳（毫秒，inclusive）。</param>
    /// <param name="toInclusive">查询结束时间戳（毫秒，inclusive）。</param>
    /// <returns>与时间窗相交的候选 <see cref="SegmentBlockRef"/> 列表。</returns>
    public IReadOnlyList<SegmentBlockRef> LookupCandidates(
        ulong seriesId,
        string fieldName,
        long fromInclusive,
        long toInclusive)
    {
        ArgumentNullException.ThrowIfNull(fieldName);

        var result = new List<SegmentBlockRef>();
        foreach (var seg in Segments)
        {
            // 段级时间剪枝
            if (!seg.OverlapsTimeRange(fromInclusive, toInclusive))
                continue;

            var blocks = seg.GetBlocks(seriesId, fieldName, fromInclusive, toInclusive);
            foreach (var block in blocks)
                result.Add(new SegmentBlockRef(seg.SegmentId, seg.SegmentPath, block));
        }
        return result;
    }

    /// <summary>
    /// 跨所有段，单 series 多 field 的版本。
    /// 返回该序列所有字段在时间窗 [<paramref name="fromInclusive"/>, <paramref name="toInclusive"/>] 内的候选 Block。
    /// </summary>
    /// <param name="seriesId">目标序列 ID。</param>
    /// <param name="fromInclusive">查询起始时间戳（毫秒，inclusive）。</param>
    /// <param name="toInclusive">查询结束时间戳（毫秒，inclusive）。</param>
    /// <returns>与时间窗相交的候选 <see cref="SegmentBlockRef"/> 列表。</returns>
    public IReadOnlyList<SegmentBlockRef> LookupCandidates(
        ulong seriesId,
        long fromInclusive,
        long toInclusive)
    {
        var result = new List<SegmentBlockRef>();
        foreach (var seg in Segments)
        {
            // 段级时间剪枝
            if (!seg.OverlapsTimeRange(fromInclusive, toInclusive))
                continue;

            // 段内：使用 SegmentIndex 的 prefix-max 跳跃索引完成 block 级时间剪枝。
            var blocks = seg.GetBlocks(seriesId, fromInclusive, toInclusive);
            foreach (var block in blocks)
                result.Add(new SegmentBlockRef(seg.SegmentId, seg.SegmentPath, block));
        }
        return result;
    }
}
