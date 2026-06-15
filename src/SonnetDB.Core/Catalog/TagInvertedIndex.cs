using System.Collections.Frozen;
using System.Threading;

namespace SonnetDB.Catalog;

/// <summary>
/// Tag 倒排索引：维护 <c>(measurement, tagKey, tagValue) → SeriesId 集合</c> 的多级映射，
/// 用于把 <see cref="SeriesCatalog.Find"/> 在 tag 过滤下的复杂度从全表扫描降到候选集交集大小。
/// </summary>
/// <remarks>
/// <para>
/// 写入路径在同步块内维护 mutable builder，并发布新的 <see cref="FrozenDictionary{TKey,TValue}"/>
/// / <see cref="FrozenSet{T}"/> 快照；查询路径只读取已发布快照，不暴露内部可变集合。
/// </para>
/// <para>
/// 索引是 <see cref="SeriesCatalog"/> 的内部派生数据，不直接持久化；
/// 启动时由 <see cref="CatalogFileCodec"/> 通过 <see cref="SeriesCatalog.LoadEntry"/> 重建。
/// </para>
/// </remarks>
internal sealed class TagInvertedIndex
{
    private readonly object _sync = new();

    /// <summary>measurement → 该 measurement 下的所有 SeriesId 集合。</summary>
    private readonly Dictionary<string, HashSet<ulong>> _byMeasurement = new(StringComparer.Ordinal);

    /// <summary>measurement → tagKey → tagValue → SeriesId 集合。</summary>
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, HashSet<ulong>>>> _byTag =
        new(StringComparer.Ordinal);

    private Snapshot _snapshot = Snapshot.Empty;

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
            PublishSnapshot();
        }
    }

    /// <summary>
    /// 批量加入多条 series，并在全部 mutable builder 更新后只发布一次冻结快照。
    /// </summary>
    /// <param name="entries">要加入的目录项集合。</param>
    public void AddRange(IEnumerable<SeriesEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        lock (_sync)
        {
            bool changed = false;
            foreach (var entry in entries)
            {
                ArgumentNullException.ThrowIfNull(entry);
                AddMutable(entry);
                changed = true;
            }

            if (changed)
                PublishSnapshot();
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
        return Volatile.Read(ref _snapshot).Find(measurement, tagFilter);
    }

    /// <summary>清空整个倒排索引（仅供 <see cref="SeriesCatalog.Clear"/> 调用）。</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _byMeasurement.Clear();
            _byTag.Clear();
            Volatile.Write(ref _snapshot, Snapshot.Empty);
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
            var changed = _byMeasurement.Remove(measurement);
            changed |= _byTag.Remove(measurement);
            if (changed)
                PublishSnapshot();
        }
    }

    private void AddMutable(SeriesEntry entry)
    {
        if (!_byMeasurement.TryGetValue(entry.Measurement, out var measurementSet))
        {
            measurementSet = new HashSet<ulong>();
            _byMeasurement.Add(entry.Measurement, measurementSet);
        }
        measurementSet.Add(entry.Id);

        if (entry.Tags.Count == 0)
            return;

        if (!_byTag.TryGetValue(entry.Measurement, out var tagKeyMap))
        {
            tagKeyMap = new Dictionary<string, Dictionary<string, HashSet<ulong>>>(StringComparer.Ordinal);
            _byTag.Add(entry.Measurement, tagKeyMap);
        }

        foreach (var (tagKey, tagValue) in entry.Tags)
        {
            if (!tagKeyMap.TryGetValue(tagKey, out var valueMap))
            {
                valueMap = new Dictionary<string, HashSet<ulong>>(StringComparer.Ordinal);
                tagKeyMap.Add(tagKey, valueMap);
            }

            if (!valueMap.TryGetValue(tagValue, out var idSet))
            {
                idSet = new HashSet<ulong>();
                valueMap.Add(tagValue, idSet);
            }
            idSet.Add(entry.Id);
        }
    }

    private void PublishSnapshot()
        => Volatile.Write(ref _snapshot, Snapshot.Create(_byMeasurement, _byTag));

    private sealed class Snapshot
    {
        internal static readonly Snapshot Empty = Create(
            new Dictionary<string, HashSet<ulong>>(0, StringComparer.Ordinal),
            new Dictionary<string, Dictionary<string, Dictionary<string, HashSet<ulong>>>>(
                0,
                StringComparer.Ordinal));

        private readonly FrozenDictionary<string, FrozenSet<ulong>> _byMeasurement;
        private readonly FrozenDictionary<
            string,
            FrozenDictionary<string, FrozenDictionary<string, FrozenSet<ulong>>>> _byTag;

        private Snapshot(
            FrozenDictionary<string, FrozenSet<ulong>> byMeasurement,
            FrozenDictionary<string, FrozenDictionary<string, FrozenDictionary<string, FrozenSet<ulong>>>> byTag)
        {
            _byMeasurement = byMeasurement;
            _byTag = byTag;
        }

        internal static Snapshot Create(
            Dictionary<string, HashSet<ulong>> byMeasurement,
            Dictionary<string, Dictionary<string, Dictionary<string, HashSet<ulong>>>> byTag)
        {
            var measurementSnapshot = new Dictionary<string, FrozenSet<ulong>>(
                byMeasurement.Count,
                StringComparer.Ordinal);
            foreach (var (measurement, ids) in byMeasurement)
                measurementSnapshot[measurement] = ids.ToFrozenSet();

            var tagSnapshot = new Dictionary<
                string,
                FrozenDictionary<string, FrozenDictionary<string, FrozenSet<ulong>>>>(
                byTag.Count,
                StringComparer.Ordinal);

            foreach (var (measurement, tagKeyMap) in byTag)
            {
                var tagKeySnapshot = new Dictionary<string, FrozenDictionary<string, FrozenSet<ulong>>>(
                    tagKeyMap.Count,
                    StringComparer.Ordinal);

                foreach (var (tagKey, valueMap) in tagKeyMap)
                {
                    var valueSnapshot = new Dictionary<string, FrozenSet<ulong>>(
                        valueMap.Count,
                        StringComparer.Ordinal);
                    foreach (var (tagValue, ids) in valueMap)
                        valueSnapshot[tagValue] = ids.ToFrozenSet();

                    tagKeySnapshot[tagKey] = valueSnapshot.ToFrozenDictionary(StringComparer.Ordinal);
                }

                tagSnapshot[measurement] = tagKeySnapshot.ToFrozenDictionary(StringComparer.Ordinal);
            }

            return new Snapshot(
                measurementSnapshot.ToFrozenDictionary(StringComparer.Ordinal),
                tagSnapshot.ToFrozenDictionary(StringComparer.Ordinal));
        }

        internal IReadOnlyList<ulong> Find(string measurement, IReadOnlyDictionary<string, string>? tagFilter)
        {
            if (tagFilter == null || tagFilter.Count == 0)
            {
                if (!_byMeasurement.TryGetValue(measurement, out var allIds) || allIds.Count == 0)
                    return [];
                return [.. allIds];
            }

            if (!_byTag.TryGetValue(measurement, out var tagKeyMap))
                return [];

            // 收集每个 (tagKey, tagValue) 对应的候选集合；任一缺失即结果为空。
            var perFilterSets = new FrozenSet<ulong>[tagFilter.Count];
            int idx = 0;
            foreach (var (tagKey, tagValue) in tagFilter)
            {
                if (!tagKeyMap.TryGetValue(tagKey, out var valueMap) ||
                    !valueMap.TryGetValue(tagValue, out var idSet) ||
                    idSet.Count == 0)
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
            foreach (var id in smallest)
            {
                bool inAll = true;
                for (int i = 0; i < perFilterSets.Length; i++)
                {
                    if (!ReferenceEquals(perFilterSets[i], smallest) && !perFilterSets[i].Contains(id))
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
    }
}
