namespace SonnetDB.Kv;

/// <summary>覆盖层保留哈希点读，同时维护可定位的有序键集合。</summary>
internal sealed class KvOrderedOverlay : Dictionary<byte[], KvValueEntry>
{
    private SortedSet<byte[]>? _keys;

    internal KvOrderedOverlay(bool orderedScans = false) : base(KvKeyComparer.Instance)
    {
        if (orderedScans)
            EnableOrderedScans();
    }

    internal KvOrderedOverlay(Dictionary<byte[], KvValueEntry> values) : base(values, KvKeyComparer.Instance) { }

    internal bool OrderedScansEnabled => _keys is not null;

    internal void EnableOrderedScans(CancellationToken cancellationToken = default)
    {
        if (_keys is not null)
            return;
        var keys = new SortedSet<byte[]>(KvKeyComparer.Instance);
        foreach (byte[] key in Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            keys.Add(key);
        }
        _keys = keys;
    }

    /// <summary>写入值并维护有序键。</summary>
    public new KvValueEntry this[byte[] key]
    {
        get => base[key];
        set
        {
            base[key] = value;
            try { _keys?.Add(key); }
            catch (OutOfMemoryException)
            {
                // WAL 后发布不能因可重建的加速结构分配失败而撕裂权威批次。
                // 丢弃加速结构；有界读取会显式拒绝，普通 KV 仍可使用原有扫描路径。
                _keys = null;
            }
        }
    }

    /// <summary>移除值和对应的有序键。</summary>
    public new bool Remove(byte[] key)
    {
        _keys?.Remove(key);
        return base.Remove(key);
    }

    /// <summary>清空覆盖层及有序键。</summary>
    public new void Clear()
    {
        _keys?.Clear();
        base.Clear();
    }

    internal IEnumerable<KeyValuePair<byte[], KvValueEntry>> Scan(
        byte[] prefix, byte[]? startInclusive, byte[]? endExclusive, byte[]? afterKey,
        CancellationToken cancellationToken, Action? candidateVisited)
    {
        SortedSet<byte[]> keys = _keys ?? throw new IOException("KV ordered overlay is unavailable; reopen the object store to rebuild it.");
        byte[] lower = prefix;
        if (startInclusive is not null && KvKeyComparer.Instance.Compare(startInclusive, lower) > 0)
            lower = startInclusive;
        if (afterKey is not null && KvKeyComparer.Instance.Compare(afterKey, lower) > 0)
            lower = afterKey;
        byte[]? maximum = keys.Max;
        if (maximum is null || KvKeyComparer.Instance.Compare(lower, maximum) > 0)
            yield break;

        // 每个候选重新定位，允许调用方在 yield 后执行惰性过期删除。
        // 不读取 view.Count，它会遍历子集；每次 seek 只访问树高数量的节点。
        int budget = keys.Count;
        for (int candidate = 0; candidate < budget; candidate++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? key;
            using (var cursor = keys.GetViewBetween(lower, maximum).GetEnumerator())
            {
                key = cursor.MoveNext() ? cursor.Current : null;
                if (key is not null && afterKey is not null && KvKeyComparer.Instance.Compare(key, afterKey) <= 0)
                    key = cursor.MoveNext() ? cursor.Current : null;
            }
            if (key is null)
                yield break;
            if (!key.AsSpan().StartsWith(prefix)
                || (endExclusive is not null && KvKeyComparer.Instance.Compare(key, endExclusive) >= 0))
                yield break;
            lower = key;
            afterKey = key;
            candidateVisited?.Invoke();
            KvValueEntry value = base[key];
            if (!value.IsDeleted)
                yield return new KeyValuePair<byte[], KvValueEntry>(key, value);
        }
    }
}
