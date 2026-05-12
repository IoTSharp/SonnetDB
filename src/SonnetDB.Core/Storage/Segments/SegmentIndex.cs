namespace SonnetDB.Storage.Segments;

/// <summary>
/// 单个段文件的内存索引。
/// <list type="bullet">
///   <item><description><c>byId</c>：SeriesId → 该段中属于此 series 的 <see cref="BlockDescriptor"/> 列表（按 MinTimestamp 升序）。</description></item>
///   <item><description><c>byField</c>：(SeriesId, FieldName) → <see cref="BlockDescriptor"/> 列表（按 MinTimestamp 升序）。</description></item>
///   <item><description><c>timeRange</c>：段级 (Min, Max) 时间范围，用于快速时间过滤剪枝。</description></item>
/// </list>
/// </summary>
public sealed class SegmentIndex
{
    private readonly Dictionary<ulong, List<BlockDescriptor>> _byId;
    private readonly Dictionary<(ulong SeriesId, string FieldName), List<BlockDescriptor>> _byField;

    /// <summary>
    /// 与 <c>_byField</c> 桶一一对应的 <c>MaxTimestamp</c> 前缀最大值数组，用于 Block 级跳跃索引。
    /// 定义：<c>prefixMax[i] = max(list[0..i].MaxTimestamp)</c>，单调非递减。
    /// 对于按 <c>MinTimestamp</c> 升序排列的 Block 列表，可在该数组上二分查找
    /// "首个 <c>prefixMax &gt;= from</c> 的索引"，作为时间窗 <c>[from, to]</c> 命中候选 Block 的下界
    /// 起点；从该起点起再做一次微扫描以排除 <c>MaxTimestamp &lt; from</c> 的越前块（仅在压缩产生
    /// 重叠 Block 的少见场景下出现）。
    /// </summary>
    private readonly Dictionary<(ulong SeriesId, string FieldName), long[]> _byFieldPrefixMax;

    /// <summary>
    /// 与 <c>_byId</c> 桶一一对应的 <c>MaxTimestamp</c> 前缀最大值数组，定义同 <c>_byFieldPrefixMax</c>。
    /// 用于 <see cref="GetBlocks(ulong, long, long)"/> 多 field 时间剪枝路径的 Block 级跳跃。
    /// </summary>
    private readonly Dictionary<ulong, long[]> _byIdPrefixMax;

    /// <summary>所属段的唯一标识符。</summary>
    public long SegmentId { get; }

    /// <summary>所属段的文件路径。</summary>
    public string SegmentPath { get; }

    /// <summary>段内所有 Block 的最小时间戳（毫秒 UTC）；无 Block 时为 <see cref="long.MaxValue"/>。</summary>
    public long MinTimestamp { get; }

    /// <summary>段内所有 Block 的最大时间戳（毫秒 UTC）；无 Block 时为 <see cref="long.MinValue"/>。</summary>
    public long MaxTimestamp { get; }

    /// <summary>本段索引覆盖的 Block 总数。</summary>
    public int BlockCount { get; }

    private SegmentIndex(
        long segmentId,
        string segmentPath,
        long minTimestamp,
        long maxTimestamp,
        int blockCount,
        Dictionary<ulong, List<BlockDescriptor>> byId,
        Dictionary<(ulong, string), List<BlockDescriptor>> byField,
        Dictionary<ulong, long[]> byIdPrefixMax,
        Dictionary<(ulong, string), long[]> byFieldPrefixMax)
    {
        SegmentId = segmentId;
        SegmentPath = segmentPath;
        MinTimestamp = minTimestamp;
        MaxTimestamp = maxTimestamp;
        BlockCount = blockCount;
        _byId = byId;
        _byField = byField;
        _byIdPrefixMax = byIdPrefixMax;
        _byFieldPrefixMax = byFieldPrefixMax;
    }

    /// <summary>
    /// 遍历 <paramref name="reader"/> 的所有 Block，构建单段内存索引。
    /// </summary>
    /// <param name="reader">已打开的段读取器。</param>
    /// <param name="segmentId">所属段唯一标识符。</param>
    /// <returns>构建完成的 <see cref="SegmentIndex"/> 实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> 为 null 时抛出。</exception>
    public static SegmentIndex Build(SegmentReader reader, long segmentId)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var byId = new Dictionary<ulong, List<BlockDescriptor>>();
        var byField = new Dictionary<(ulong, string), List<BlockDescriptor>>();

        foreach (var block in reader.Blocks)
        {
            // byId
            if (!byId.TryGetValue(block.SeriesId, out var idList))
            {
                idList = new List<BlockDescriptor>();
                byId[block.SeriesId] = idList;
            }
            idList.Add(block);

            // byField
            var key = (block.SeriesId, block.FieldName);
            if (!byField.TryGetValue(key, out var fieldList))
            {
                fieldList = new List<BlockDescriptor>();
                byField[key] = fieldList;
            }
            fieldList.Add(block);
        }

        // 保险起见按 MinTimestamp 升序排列（写入顺序通常已满足，但不假设）
        foreach (var list in byId.Values)
            list.Sort(static (a, b) => a.MinTimestamp.CompareTo(b.MinTimestamp));
        foreach (var list in byField.Values)
            list.Sort(static (a, b) => a.MinTimestamp.CompareTo(b.MinTimestamp));

        // 在按 MinTimestamp 升序排好后，构建 MaxTimestamp 前缀最大值数组（block-level skipping index）。
        // 该数组单调非递减，可被 LowerBoundPrefixMax 二分定位"第一个可能命中 [from, *] 的桶位置"。
        var byIdPrefixMax = new Dictionary<ulong, long[]>(byId.Count);
        foreach (var (sid, list) in byId)
            byIdPrefixMax[sid] = BuildPrefixMaxTimestamps(list);

        var byFieldPrefixMax = new Dictionary<(ulong, string), long[]>(byField.Count);
        foreach (var (key, list) in byField)
            byFieldPrefixMax[key] = BuildPrefixMaxTimestamps(list);

        return new SegmentIndex(
            segmentId,
            reader.Path,
            reader.MinTimestamp,
            reader.MaxTimestamp,
            reader.BlockCount,
            byId,
            byField,
            byIdPrefixMax,
            byFieldPrefixMax);
    }

    /// <summary>
    /// 测试专用工厂：直接从一组合成的 <see cref="BlockDescriptor"/> 构造 <see cref="SegmentIndex"/>，
    /// 跳过 <see cref="Build(SegmentReader, long)"/> 所需的段文件 IO 与 <see cref="SegmentReader"/> 实例。
    /// 用于覆盖单一 <c>MemTable</c> 难以复现的场景，最典型的是压缩（compaction）后产生的
    /// <c>MaxTimestamp</c> 非单调（block 间时间区间相互重叠）的 (series, field) 桶——
    /// 这种分布会触发 <see cref="GetBlocks(ulong, string, long, long)"/> 中 prefix-max 二分定位下界后
    /// 的 <c>MaxTimestamp &gt;= from</c> 微扫描分支。
    /// 该方法不校验 <paramref name="blocks"/> 元素的内部一致性（如 <c>Index</c> 是否唯一、
    /// <c>MinTimestamp &lt;= MaxTimestamp</c> 等），与 <see cref="Build(SegmentReader, long)"/>
    /// 一样仅按 <c>MinTimestamp</c> 升序排序输入并构建 prefix-max 数组。
    /// </summary>
    /// <param name="segmentId">所属段唯一标识符。</param>
    /// <param name="segmentPath">所属段的文件路径（仅作为属性回填，不要求文件实际存在）。</param>
    /// <param name="blocks">合成的 Block 列表（无需预先排序）。</param>
    internal static SegmentIndex BuildFromBlocksForTesting(
        long segmentId,
        string segmentPath,
        IReadOnlyList<BlockDescriptor> blocks)
    {
        ArgumentNullException.ThrowIfNull(segmentPath);
        ArgumentNullException.ThrowIfNull(blocks);

        var byId = new Dictionary<ulong, List<BlockDescriptor>>();
        var byField = new Dictionary<(ulong, string), List<BlockDescriptor>>();

        long minTs = long.MaxValue;
        long maxTs = long.MinValue;

        foreach (var block in blocks)
        {
            if (!byId.TryGetValue(block.SeriesId, out var idList))
            {
                idList = new List<BlockDescriptor>();
                byId[block.SeriesId] = idList;
            }
            idList.Add(block);

            var key = (block.SeriesId, block.FieldName);
            if (!byField.TryGetValue(key, out var fieldList))
            {
                fieldList = new List<BlockDescriptor>();
                byField[key] = fieldList;
            }
            fieldList.Add(block);

            if (block.MinTimestamp < minTs) minTs = block.MinTimestamp;
            if (block.MaxTimestamp > maxTs) maxTs = block.MaxTimestamp;
        }

        foreach (var list in byId.Values)
            list.Sort(static (a, b) => a.MinTimestamp.CompareTo(b.MinTimestamp));
        foreach (var list in byField.Values)
            list.Sort(static (a, b) => a.MinTimestamp.CompareTo(b.MinTimestamp));

        var byIdPrefixMax = new Dictionary<ulong, long[]>(byId.Count);
        foreach (var (sid, list) in byId)
            byIdPrefixMax[sid] = BuildPrefixMaxTimestamps(list);

        var byFieldPrefixMax = new Dictionary<(ulong, string), long[]>(byField.Count);
        foreach (var (key, list) in byField)
            byFieldPrefixMax[key] = BuildPrefixMaxTimestamps(list);

        return new SegmentIndex(
            segmentId,
            segmentPath,
            blocks.Count == 0 ? long.MaxValue : minTs,
            blocks.Count == 0 ? long.MinValue : maxTs,
            blocks.Count,
            byId,
            byField,
            byIdPrefixMax,
            byFieldPrefixMax);
    }

    /// <summary>
    /// 计算单个 (series, field) 桶按 MinTimestamp 升序排序后的 MaxTimestamp 前缀最大值数组。
    /// 即 <c>result[i] = max(blocks[0..i].MaxTimestamp)</c>。结果数组长度等于输入列表长度，
    /// 单调非递减；空列表返回空数组（不分配）。
    /// </summary>
    private static long[] BuildPrefixMaxTimestamps(List<BlockDescriptor> sorted)
    {
        if (sorted.Count == 0)
            return Array.Empty<long>();

        var result = new long[sorted.Count];
        long runningMax = sorted[0].MaxTimestamp;
        result[0] = runningMax;
        for (int i = 1; i < sorted.Count; i++)
        {
            long m = sorted[i].MaxTimestamp;
            if (m > runningMax) runningMax = m;
            result[i] = runningMax;
        }
        return result;
    }

    /// <summary>
    /// 按 <paramref name="seriesId"/> 取候选 Block；未命中返回空列表（不分配）。
    /// </summary>
    /// <param name="seriesId">目标序列 ID。</param>
    /// <returns>属于该序列的 <see cref="BlockDescriptor"/> 只读列表（按 MinTimestamp 升序）。</returns>
    public IReadOnlyList<BlockDescriptor> GetBlocks(ulong seriesId)
    {
        return _byId.TryGetValue(seriesId, out var list) ? list : Array.Empty<BlockDescriptor>();
    }

    /// <summary>
    /// 按 (<paramref name="seriesId"/>, <paramref name="fieldName"/>) 取候选 Block；未命中返回空列表（不分配）。
    /// </summary>
    /// <param name="seriesId">目标序列 ID。</param>
    /// <param name="fieldName">目标字段名。</param>
    /// <returns>属于该序列+字段的 <see cref="BlockDescriptor"/> 只读列表（按 MinTimestamp 升序）。</returns>
    public IReadOnlyList<BlockDescriptor> GetBlocks(ulong seriesId, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        return _byField.TryGetValue((seriesId, fieldName), out var list)
            ? list
            : Array.Empty<BlockDescriptor>();
    }

    /// <summary>
    /// 按 (<paramref name="seriesId"/>, <paramref name="fieldName"/>, [<paramref name="from"/>, <paramref name="toInclusive"/>])
    /// 取与时间窗有重叠的 Block。
    /// <para>
    /// 剪枝路径：①按 <c>MinTimestamp</c> 二分定位上界 <c>upper</c>（首个 <c>MinTimestamp &gt; toInclusive</c> 的位置）；
    /// ②按 <c>MaxTimestamp</c> 前缀最大值数组二分定位下界 <c>lower</c>（首个可能命中 <c>from</c> 的位置）；
    /// ③在 <c>[lower, upper)</c> 内补一次 <c>MaxTimestamp &gt;= from</c> 微扫描以排除压缩重叠场景下的越前块。
    /// 渐近复杂度由原来的 O(upper) 改善为 O(log n + 重叠桶数)，命中桶数远小于桶总数时收益显著。
    /// </para>
    /// </summary>
    /// <param name="seriesId">目标序列 ID。</param>
    /// <param name="fieldName">目标字段名。</param>
    /// <param name="from">查询起始时间戳（毫秒，inclusive）。</param>
    /// <param name="toInclusive">查询结束时间戳（毫秒，inclusive）。</param>
    /// <returns>与时间窗 [<paramref name="from"/>, <paramref name="toInclusive"/>] 相交的 <see cref="BlockDescriptor"/> 列表。</returns>
    public IReadOnlyList<BlockDescriptor> GetBlocks(ulong seriesId, string fieldName, long from, long toInclusive)
    {
        ArgumentNullException.ThrowIfNull(fieldName);

        var key = (seriesId, fieldName);
        if (!_byField.TryGetValue(key, out var list))
            return Array.Empty<BlockDescriptor>();

        // 二分定位上界：第一个 MinTimestamp > toInclusive 的索引
        int upper = FindUpperBound(list, toInclusive);
        if (upper == 0)
            return Array.Empty<BlockDescriptor>();

        // 二分定位下界：在 prefixMax 上找首个 prefixMax >= from 的索引
        long[] prefixMax = _byFieldPrefixMax[key];
        int lower = LowerBoundPrefixMax(prefixMax, upper, from);
        if (lower >= upper)
            return Array.Empty<BlockDescriptor>();

        // 在 [lower, upper) 区间内做最终的 MaxTimestamp >= from 微过滤（处理重叠 block）。
        var result = new List<BlockDescriptor>(upper - lower);
        for (int i = lower; i < upper; i++)
        {
            if (list[i].MaxTimestamp >= from)
                result.Add(list[i]);
        }

        return result.Count == 0 ? Array.Empty<BlockDescriptor>() : result;
    }

    /// <summary>
    /// 按 (<paramref name="seriesId"/>, [<paramref name="from"/>, <paramref name="toInclusive"/>])
    /// 取该序列下任意字段中与时间窗有重叠的 Block；剪枝原理同 <see cref="GetBlocks(ulong, string, long, long)"/>，
    /// 但工作在按 series 聚合的桶上（覆盖该 series 的所有字段）。
    /// </summary>
    /// <param name="seriesId">目标序列 ID。</param>
    /// <param name="from">查询起始时间戳（毫秒，inclusive）。</param>
    /// <param name="toInclusive">查询结束时间戳（毫秒，inclusive）。</param>
    /// <returns>与时间窗 [<paramref name="from"/>, <paramref name="toInclusive"/>] 相交的 <see cref="BlockDescriptor"/> 列表。</returns>
    public IReadOnlyList<BlockDescriptor> GetBlocks(ulong seriesId, long from, long toInclusive)
    {
        if (!_byId.TryGetValue(seriesId, out var list))
            return Array.Empty<BlockDescriptor>();

        int upper = FindUpperBound(list, toInclusive);
        if (upper == 0)
            return Array.Empty<BlockDescriptor>();

        long[] prefixMax = _byIdPrefixMax[seriesId];
        int lower = LowerBoundPrefixMax(prefixMax, upper, from);
        if (lower >= upper)
            return Array.Empty<BlockDescriptor>();

        var result = new List<BlockDescriptor>(upper - lower);
        for (int i = lower; i < upper; i++)
        {
            if (list[i].MaxTimestamp >= from)
                result.Add(list[i]);
        }

        return result.Count == 0 ? Array.Empty<BlockDescriptor>() : result;
    }

    /// <summary>
    /// 段时间范围与 [<paramref name="from"/>, <paramref name="toInclusive"/>] 是否相交。
    /// </summary>
    /// <param name="from">查询起始时间戳（毫秒，inclusive）。</param>
    /// <param name="toInclusive">查询结束时间戳（毫秒，inclusive）。</param>
    /// <returns>若相交返回 <c>true</c>，否则返回 <c>false</c>。</returns>
    public bool OverlapsTimeRange(long from, long toInclusive)
    {
        return MinTimestamp <= toInclusive && MaxTimestamp >= from;
    }

    /// <summary>
    /// 在已按 MinTimestamp 升序排列的列表中，二分查找第一个 MinTimestamp &gt; <paramref name="toInclusive"/> 的索引（上界）。
    /// </summary>
    private static int FindUpperBound(List<BlockDescriptor> sorted, long toInclusive)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (sorted[mid].MinTimestamp <= toInclusive)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// 在单调非递减的 <paramref name="prefixMax"/> 数组的 <c>[0, length)</c> 子区间内，
    /// 二分查找首个 <c>prefixMax[i] &gt;= from</c> 的索引；若不存在则返回 <paramref name="length"/>。
    /// </summary>
    private static int LowerBoundPrefixMax(long[] prefixMax, int length, long from)
    {
        int lo = 0, hi = length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (prefixMax[mid] < from)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }
}
