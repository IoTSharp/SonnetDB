namespace SonnetDB.Kv.Lsm;

/// <summary>
/// 一次范围删除请求：删除 user key 落在 <c>[StartInclusive, EndExclusive)</c> 的全部较早版本。
/// </summary>
/// <remarks>
/// 同一批次内范围删除先于点写入生效：tombstone 只遮蔽 sequence 严格小于自身的条目，
/// 而同批次点写入与它共享 sequence，因此不被遮蔽。<c>StartInclusive</c> 可为空（从最小 key 开始），
/// <c>EndExclusive</c> 必须非空且大于起点；不支持无上界删除（整库清空使用 generation）。
/// </remarks>
internal sealed record KvRangeDeletion(byte[] StartInclusive, byte[] EndExclusive)
{
    /// <summary>校验范围。</summary>
    /// <exception cref="ArgumentException">起止为空引用、终点为空或起点不小于终点。</exception>
    internal void Validate()
    {
        if (StartInclusive is null || EndExclusive is null)
            throw new ArgumentException("KV-LSM range deletion bounds cannot be null.");
        if (EndExclusive.Length == 0)
            throw new ArgumentException("KV-LSM range deletion end must be non-empty (unbounded deletes are not supported).");
        if (StartInclusive.AsSpan().SequenceCompareTo(EndExclusive) >= 0)
            throw new ArgumentException("KV-LSM range deletion start must be smaller than its end.");
    }
}

/// <summary>带 sequence 的持久化 range tombstone。</summary>
/// <param name="Start">包含的起点 user key。</param>
/// <param name="End">不包含的终点 user key。</param>
/// <param name="Sequence">写入 sequence；只遮蔽 sequence 更小的条目。</param>
internal readonly record struct KvRangeTombstone(byte[] Start, byte[] End, long Sequence)
{
    /// <summary><paramref name="userKey"/> 是否落在 <c>[Start, End)</c>。</summary>
    internal bool Contains(ReadOnlySpan<byte> userKey)
        => userKey.SequenceCompareTo(Start) >= 0 && userKey.SequenceCompareTo(End) < 0;

    /// <summary>与 <c>[low, high]</c>（闭区间）是否相交。</summary>
    internal bool Overlaps(ReadOnlySpan<byte> low, ReadOnlySpan<byte> high)
        => End.AsSpan().SequenceCompareTo(low) > 0 && Start.AsSpan().SequenceCompareTo(high) <= 0;

    /// <summary>
    /// 在 <paramref name="tombstones"/> 中找出覆盖 <paramref name="userKey"/> 且 sequence ≤
    /// <paramref name="snapshotSequence"/> 的最大 sequence；没有时返回 0。
    /// </summary>
    internal static long MaxCovering(IReadOnlyList<KvRangeTombstone> tombstones, ReadOnlySpan<byte> userKey, long snapshotSequence)
    {
        long max = 0;
        foreach (KvRangeTombstone tombstone in tombstones)
        {
            if (tombstone.Sequence <= snapshotSequence && tombstone.Sequence > max && tombstone.Contains(userKey))
                max = tombstone.Sequence;
        }
        return max;
    }
}

/// <summary>
/// 某个快照可见的全部 range tombstone 的分段视图：把相互重叠的区间切成互不重叠的片段，
/// 每段记录覆盖它的最大 sequence，查询为 O(log 片段数)。
/// </summary>
internal sealed class KvRangeTombstoneSet
{
    private readonly byte[][] _starts;
    private readonly byte[][] _ends;
    private readonly long[] _sequences;

    private KvRangeTombstoneSet(byte[][] starts, byte[][] ends, long[] sequences)
    {
        _starts = starts;
        _ends = ends;
        _sequences = sequences;
    }

    /// <summary>空集合。</summary>
    internal static KvRangeTombstoneSet Empty { get; } = new([], [], []);

    /// <summary>是否没有任何片段。</summary>
    internal bool IsEmpty => _starts.Length == 0;

    /// <summary>由 sequence ≤ <paramref name="snapshotSequence"/> 的 tombstone 构建分段视图。</summary>
    internal static KvRangeTombstoneSet Build(IEnumerable<KvRangeTombstone> tombstones, long snapshotSequence)
    {
        var visible = tombstones.Where(t => t.Sequence <= snapshotSequence).ToList();
        if (visible.Count == 0)
            return Empty;

        var comparer = Comparer<byte[]>.Create(static (a, b) => a.AsSpan().SequenceCompareTo(b));
        var boundaries = new SortedSet<byte[]>(comparer);
        foreach (KvRangeTombstone tombstone in visible)
        {
            boundaries.Add(tombstone.Start);
            boundaries.Add(tombstone.End);
        }

        byte[][] points = [.. boundaries];
        var starts = new List<byte[]>();
        var ends = new List<byte[]>();
        var sequences = new List<long>();
        for (int i = 0; i + 1 < points.Length; i++)
        {
            byte[] low = points[i];
            byte[] high = points[i + 1];
            long max = 0;
            foreach (KvRangeTombstone tombstone in visible)
            {
                if (tombstone.Sequence > max
                    && tombstone.Start.AsSpan().SequenceCompareTo(low) <= 0
                    && tombstone.End.AsSpan().SequenceCompareTo(high) >= 0)
                {
                    max = tombstone.Sequence;
                }
            }
            if (max == 0)
                continue;
            if (starts.Count > 0 && sequences[^1] == max && ends[^1].AsSpan().SequenceEqual(low))
            {
                ends[^1] = high;
                continue;
            }
            starts.Add(low);
            ends.Add(high);
            sequences.Add(max);
        }
        return new KvRangeTombstoneSet([.. starts], [.. ends], [.. sequences]);
    }

    /// <summary>覆盖 <paramref name="userKey"/> 的最大 sequence；未被覆盖时返回 0。</summary>
    internal long MaxCovering(ReadOnlySpan<byte> userKey)
    {
        int low = 0;
        int high = _starts.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (userKey.SequenceCompareTo(_starts[middle]) < 0)
                high = middle - 1;
            else if (userKey.SequenceCompareTo(_ends[middle]) >= 0)
                low = middle + 1;
            else
                return _sequences[middle];
        }
        return 0;
    }
}
