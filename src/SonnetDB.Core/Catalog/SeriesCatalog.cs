using System.Collections.Concurrent;
using SonnetDB.Model;

namespace SonnetDB.Catalog;

/// <summary>
/// 序列目录：维护 SeriesKey ↔ SeriesId ↔ <see cref="SeriesEntry"/> 的双向映射。
/// 线程安全：读路径无锁读取 <see cref="ConcurrentDictionary{TKey,TValue}"/>，写路径在 <c>_sync</c> 内
/// 协调 id 计算 / 碰撞检查 / 双表插入。
/// </summary>
/// <remarks>
/// <para>
/// 并发幂等性保证：<see cref="GetOrAdd(string,IReadOnlyDictionary{string,string})"/> 对同一
/// <see cref="SeriesKey"/> 的多次调用（包括并发调用）返回同一 <see cref="SeriesEntry"/> 实例。
/// miss 路径会进入写锁重新检查，只有第一个调用方创建并插入。
/// </para>
/// <para>
/// I5：高基数写入下不再每新增一条 series 就全量 <c>ToFrozenDictionary()</c> 重建快照
/// （原实现每 Add O(N)、整体 O(N²) 且大量瞬时分配）；改为原地增量插入并发字典，读者立即可见。
/// </para>
/// </remarks>
public sealed class SeriesCatalog
{
    private readonly object _sync = new();

    private readonly ConcurrentDictionary<string, SeriesEntry> _byCanonical = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ulong, SeriesEntry> _byId = new();
    private readonly TagInvertedIndex _tagIndex = new();

    /// <summary>目录中的序列数量。</summary>
    public int Count => _byCanonical.Count;

    /// <summary>
    /// 取得或创建一条 series 目录项。同一 <see cref="SeriesKey"/> 重复调用幂等。
    /// </summary>
    /// <param name="measurement">Measurement 名称。</param>
    /// <param name="tags">Tag 键值对；为 null 时等同于空字典。</param>
    /// <returns>对应的 <see cref="SeriesEntry"/>（已存在则返回已有实例）。</returns>
    /// <exception cref="InvalidOperationException">检测到 SeriesId 哈希碰撞时抛出。</exception>
    public SeriesEntry GetOrAdd(string measurement, IReadOnlyDictionary<string, string>? tags)
    {
        var key = new SeriesKey(measurement, tags);
        return GetOrAddInternal(key);
    }

    /// <summary>
    /// 从 <see cref="Point"/> 直接派生，取得或创建对应的目录项。
    /// </summary>
    /// <param name="point">已校验的数据点。</param>
    /// <returns>对应的 <see cref="SeriesEntry"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="point"/> 为 null。</exception>
    /// <exception cref="InvalidOperationException">检测到 SeriesId 哈希碰撞时抛出。</exception>
    public SeriesEntry GetOrAdd(Point point)
    {
        ArgumentNullException.ThrowIfNull(point);
        var key = SeriesKey.FromPoint(point);
        return GetOrAddInternal(key);
    }

    /// <summary>
    /// 按 SeriesId 查找；未命中返回 null。
    /// </summary>
    /// <param name="id">序列唯一标识（XxHash64 值）。</param>
    /// <returns>找到的 <see cref="SeriesEntry"/>，未命中返回 null。</returns>
    public SeriesEntry? TryGet(ulong id)
        => _byId.TryGetValue(id, out var entry) ? entry : null;

    /// <summary>
    /// 按 <see cref="SeriesKey"/> 查找；未命中返回 null。
    /// </summary>
    /// <param name="key">规范化序列键。</param>
    /// <returns>找到的 <see cref="SeriesEntry"/>，未命中返回 null。</returns>
    public SeriesEntry? TryGet(in SeriesKey key)
        => _byCanonical.TryGetValue(key.Canonical, out var entry) ? entry : null;

    /// <summary>
    /// 按 measurement 和部分 tag 过滤匹配的 series。
    /// 背后由 <see cref="TagInvertedIndex"/> 在 <c>(measurement, tagKey, tagValue)</c> 三级映射上
    /// 做候选交集，避免全表扫描；带 tag 过滤时复杂度 = 最小候选集大小 × 过滤条目数。
    /// 返回前依然在上层做防御性 tag 重校验，以容忍倒排索引与目录快照瞬间不一致。
    /// </summary>
    /// <param name="measurement">要筛选的 Measurement 名称。</param>
    /// <param name="tagFilter">Tag 子集过滤条件；为 null 或空时仅按 measurement 筛选。</param>
    /// <returns>匹配的 <see cref="SeriesEntry"/> 列表（快照）。</returns>
    public IReadOnlyList<SeriesEntry> Find(
        string measurement,
        IReadOnlyDictionary<string, string>? tagFilter)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        var candidateIds = _tagIndex.Find(measurement, tagFilter);
        if (candidateIds.Count == 0)
            return [];

        var results = new List<SeriesEntry>(candidateIds.Count);
        foreach (var id in candidateIds)
        {
            if (!_byId.TryGetValue(id, out var entry))
                continue;
            // 防御性二次校验：measurement 与 tag 过滤全部命中才返回。
            if (!string.Equals(entry.Measurement, measurement, StringComparison.Ordinal))
                continue;
            if (tagFilter != null && tagFilter.Count > 0 && !MatchesTags(entry, tagFilter))
                continue;
            results.Add(entry);
        }
        return results;
    }

    /// <summary>
    /// 枚举全部目录项（调用时拷贝的快照）。
    /// </summary>
    /// <returns>包含所有目录项的只读列表。</returns>
    public IReadOnlyList<SeriesEntry> Snapshot()
        => [.. _byCanonical.Values];

    /// <summary>
    /// 删除指定 measurement 下的全部 series 目录项。
    /// </summary>
    /// <param name="measurement">measurement 名称。</param>
    /// <returns>被删除的目录项快照；若不存在则返回空列表。</returns>
    public IReadOnlyList<SeriesEntry> RemoveMeasurement(string measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        lock (_sync)
        {
            var removed = new List<SeriesEntry>();
            foreach (var entry in _byCanonical.Values)
            {
                if (string.Equals(entry.Measurement, measurement, StringComparison.Ordinal))
                    removed.Add(entry);
            }

            if (removed.Count == 0)
                return [];

            foreach (var entry in removed)
            {
                _byCanonical.TryRemove(entry.Key.Canonical, out _);
                _byId.TryRemove(entry.Id, out _);
            }

            _tagIndex.RemoveMeasurement(measurement);
            return removed.AsReadOnly();
        }
    }

    /// <summary>
    /// 清空目录（仅供测试 / 重建用）。
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _byCanonical.Clear();
            _byId.Clear();
            _tagIndex.Clear();
        }
    }

    // ── 内部辅助 ──────────────────────────────────────────────────────────────

    private static bool MatchesTags(SeriesEntry entry, IReadOnlyDictionary<string, string> tagFilter)
    {
        foreach (var (k, v) in tagFilter)
        {
            if (!entry.Tags.TryGetValue(k, out var entryVal) ||
                !string.Equals(entryVal, v, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private SeriesEntry GetOrAddInternal(SeriesKey key)
    {
        // 快速路径：已存在（无锁读并发字典）。
        if (_byCanonical.TryGetValue(key.Canonical, out var existing))
            return existing;

        lock (_sync)
        {
            if (_byCanonical.TryGetValue(key.Canonical, out existing))
                return existing;

            ulong id = SeriesId.Compute(key);
            if (_byId.TryGetValue(id, out var idEntry))
            {
                if (!string.Equals(idEntry.Key.Canonical, key.Canonical, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"SeriesId hash collision detected for series: {key.Canonical}");
                }

                _byCanonical[key.Canonical] = idEntry;
                return idEntry;
            }

            long createdAt = DateTime.UtcNow.Ticks;
            var entry = new SeriesEntry(id, key, key.Measurement, key.Tags, createdAt);

            // 先发布 byId 再发布 byCanonical：经 canonical 命中的读者随后按 id 解析必然也命中。
            _byId[id] = entry;
            _byCanonical[key.Canonical] = entry;
            _tagIndex.Add(entry);
            return entry;
        }
    }

    /// <summary>
    /// 从持久化加载时直接注入条目（跳过 GetOrAdd 路径以保留原始 CreatedAtUtcTicks）。
    /// </summary>
    /// <param name="entry">要注入的目录项。</param>
    internal void LoadEntry(SeriesEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            if (_byCanonical.TryGetValue(entry.Key.Canonical, out var existing))
            {
                if (existing.Id != entry.Id)
                {
                    throw new InvalidOperationException(
                        $"Series catalog entry mismatch detected for series: {entry.Key.Canonical}");
                }
                return;
            }

            if (_byId.TryGetValue(entry.Id, out var idEntry))
            {
                if (!string.Equals(idEntry.Key.Canonical, entry.Key.Canonical, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"SeriesId hash collision detected for series: {entry.Key.Canonical}");
                }

                _byCanonical[entry.Key.Canonical] = idEntry;
                _tagIndex.Add(idEntry);
                return;
            }

            _byId[entry.Id] = entry;
            _byCanonical[entry.Key.Canonical] = entry;
            _tagIndex.Add(entry);
        }
    }

    /// <summary>
    /// 从持久化批量注入条目，并在批量完成后只发布一次索引。
    /// </summary>
    /// <param name="entries">要注入的目录项集合。</param>
    internal void LoadEntries(IReadOnlyList<SeriesEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            return;

        var indexedEntries = new List<SeriesEntry>(entries.Count);
        lock (_sync)
        {
            foreach (var entry in entries)
            {
                ArgumentNullException.ThrowIfNull(entry);
                if (_byCanonical.TryGetValue(entry.Key.Canonical, out var existing))
                {
                    if (existing.Id != entry.Id)
                    {
                        throw new InvalidOperationException(
                            $"Series catalog entry mismatch detected for series: {entry.Key.Canonical}");
                    }
                    continue;
                }

                if (_byId.TryGetValue(entry.Id, out var idEntry))
                {
                    if (!string.Equals(idEntry.Key.Canonical, entry.Key.Canonical, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"SeriesId hash collision detected for series: {entry.Key.Canonical}");
                    }

                    _byCanonical[entry.Key.Canonical] = idEntry;
                    indexedEntries.Add(idEntry);
                    continue;
                }

                _byId[entry.Id] = entry;
                _byCanonical[entry.Key.Canonical] = entry;
                indexedEntries.Add(entry);
            }

            if (indexedEntries.Count == 0)
                return;

            _tagIndex.AddRange(indexedEntries);
        }
    }
}
