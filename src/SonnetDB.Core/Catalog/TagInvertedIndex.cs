using System.Collections.Concurrent;

namespace SonnetDB.Catalog;

/// <summary>
/// Tag 倒排索引：维护 <c>(measurement, tagKey, tagValue) → SeriesId 集合</c> 的多级映射，
/// 用于把 <see cref="SeriesCatalog.Find"/> 在 tag 过滤下的复杂度从全表扫描降到候选集交集大小。
/// </summary>
/// <remarks>
/// <para>
/// I5：高基数写入下不再每 <see cref="Add"/> 一条 series 就全量 <c>ToFrozenDictionary()</c> /
/// <c>ToFrozenSet()</c> 重建整棵快照（原实现每 Add O(N)、整体 O(N²) 且大量瞬时分配）；
/// 改为多级 <see cref="ConcurrentDictionary{TKey,TValue}"/> 原地增量插入，查询无锁读取，写者立即可见。
/// </para>
/// <para>
/// SeriesId 集合以 <c>ConcurrentDictionary&lt;ulong, byte&gt;</c> 充当并发 set。
/// 索引是 <see cref="SeriesCatalog"/> 的内部派生数据，不直接持久化；
/// 启动时由 <c>CatalogFileCodec</c> 通过 <see cref="SeriesCatalog.LoadEntry"/> 重建。
/// </para>
/// </remarks>
internal sealed class TagInvertedIndex
{
    private readonly object _sync = new();

    /// <summary>measurement → 该 measurement 下的所有 SeriesId 集合。</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ulong, byte>> _byMeasurement =
        new(StringComparer.Ordinal);

    /// <summary>measurement → tagKey → tagValue → SeriesId 集合。</summary>
    private readonly ConcurrentDictionary<
        string,
        ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<ulong, byte>>>> _byTag =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 把一条 series 加入索引；同一 SeriesId 重复加入幂等。
    /// </summary>
    /// <param name="entry">要加入的目录项。</param>
    public void Add(SeriesEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_sync)
        {
            AddMutable(entry);
        }
    }

    /// <summary>
    /// 批量加入多条 series。
    /// </summary>
    /// <param name="entries">要加入的目录项集合。</param>
    public void AddRange(IEnumerable<SeriesEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        lock (_sync)
        {
            foreach (var entry in entries)
            {
                ArgumentNullException.ThrowIfNull(entry);
                AddMutable(entry);
            }
        }
    }

    /// <summary>
    /// 按 measurement 与可选的 tag 等值过滤集合查找候选 SeriesId 集合；
    /// 调用方仍需通过 <see cref="SeriesCatalog.TryGet(ulong)"/> 解析为 <see cref="SeriesEntry"/>
    /// 并执行最终防御性校验。
    /// </summary>
    /// <param name="measurement">measurement 名称。</param>
    /// <param name="tagFilter">tag 等值过滤集合；为 null 或空时返回 measurement 下全部 SeriesId。</param>
    /// <returns>候选 SeriesId 列表（无序快照；可能为空）。</returns>
    public IReadOnlyList<ulong> Find(string measurement, IReadOnlyDictionary<string, string>? tagFilter)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        if (tagFilter == null || tagFilter.Count == 0)
        {
            if (!_byMeasurement.TryGetValue(measurement, out var allIds) || allIds.IsEmpty)
                return [];
            return [.. allIds.Keys];
        }

        if (!_byTag.TryGetValue(measurement, out var tagKeyMap))
            return [];

        // 收集每个 (tagKey, tagValue) 对应的候选集合；任一缺失即结果为空。
        var perFilterSets = new ConcurrentDictionary<ulong, byte>[tagFilter.Count];
        int idx = 0;
        foreach (var (tagKey, tagValue) in tagFilter)
        {
            if (!tagKeyMap.TryGetValue(tagKey, out var valueMap) ||
                !valueMap.TryGetValue(tagValue, out var idSet) ||
                idSet.IsEmpty)
            {
                return [];
            }
            perFilterSets[idx++] = idSet;
        }

        // 选最小集合作为基准做交集，规模上界 = min(|S_i|)。
        var smallest = perFilterSets[0];
        for (int i = 1; i < perFilterSets.Length; i++)
        {
            if (perFilterSets[i].Count < smallest.Count)
                smallest = perFilterSets[i];
        }

        var result = new List<ulong>(smallest.Count);
        foreach (var id in smallest.Keys)
        {
            bool inAll = true;
            for (int i = 0; i < perFilterSets.Length; i++)
            {
                if (!ReferenceEquals(perFilterSets[i], smallest) && !perFilterSets[i].ContainsKey(id))
                {
                    inAll = false;
                    break;
                }
            }
            if (inAll)
                result.Add(id);
        }
        return result;
    }

    /// <summary>清空整个倒排索引（仅供 <see cref="SeriesCatalog.Clear"/> 调用）。</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _byMeasurement.Clear();
            _byTag.Clear();
        }
    }

    /// <summary>
    /// 移除指定 measurement 下的全部索引项。
    /// </summary>
    /// <param name="measurement">measurement 名称。</param>
    public void RemoveMeasurement(string measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        lock (_sync)
        {
            _byMeasurement.TryRemove(measurement, out _);
            _byTag.TryRemove(measurement, out _);
        }
    }

    private void AddMutable(SeriesEntry entry)
    {
        var measurementSet = _byMeasurement.GetOrAdd(entry.Measurement, static _ => new ConcurrentDictionary<ulong, byte>());
        measurementSet.TryAdd(entry.Id, 0);

        if (entry.Tags.Count == 0)
            return;

        var tagKeyMap = _byTag.GetOrAdd(
            entry.Measurement,
            static _ => new ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<ulong, byte>>>(StringComparer.Ordinal));

        foreach (var (tagKey, tagValue) in entry.Tags)
        {
            var valueMap = tagKeyMap.GetOrAdd(
                tagKey,
                static _ => new ConcurrentDictionary<string, ConcurrentDictionary<ulong, byte>>(StringComparer.Ordinal));
            var idSet = valueMap.GetOrAdd(tagValue, static _ => new ConcurrentDictionary<ulong, byte>());
            idSet.TryAdd(entry.Id, 0);
        }
    }
}
