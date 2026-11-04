using System.Collections;

namespace SonnetDB.Kv;

/// <summary>保存一页首键及其文件偏移，查询时才解码该页索引。</summary>
internal sealed class KvDiskIndexFence(byte[] firstKey, long offset)
{
    internal byte[] FirstKey { get; } = firstKey;
    internal long Offset { get; } = offset;
    internal WeakReference<KvDiskIndexEntry[]>? Page { get; set; }
}

/// <summary>以稀疏首键定位磁盘页，活动读者保留所用页，其余页交由 GC 按压力回收。</summary>
internal sealed class KvPagedDiskIndex : IReadOnlyList<KvDiskIndexEntry>
{
    // 与磁盘格式的前缀重启块对齐，点查只解码相关块，不为降低目录占用而扩大随机读取。
    internal const int PageSize = 16;
    internal const int EagerIndexThreshold = 1024;
    private readonly KvDiskIndexFence[] _fences;
    private readonly Func<long, int, int, CancellationToken, KvDiskIndexEntry[]> _readPage;
    private KvDiskIndexEntry[]? _hotPage;

    /// <summary>初始化不可变目录与页面读取入口，不按总键数分配完整索引数组。</summary>
    internal KvPagedDiskIndex(int count, KvDiskIndexFence[] fences,
        Func<long, int, int, CancellationToken, KvDiskIndexEntry[]> readPage)
    {
        Count = count;
        _fences = fences;
        _readPage = readPage;
    }

    public int Count { get; }

    public KvDiskIndexEntry this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return Page(index / PageSize)[index % PageSize];
        }
    }

    /// <summary>先在页首键中二分，再只读取目标页，避免一次点查跨多页反复做磁盘定位。</summary>
    internal int LowerBound(ReadOnlySpan<byte> key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Count == 0) return 0;
        int low = 0;
        int high = _fences.Length;
        for (int step = 0; low < high && step < 32; step++)
        {
            int middle = low + ((high - low) / 2);
            if (_fences[middle].FirstKey.AsSpan().SequenceCompareTo(key) <= 0) low = middle + 1;
            else high = middle;
        }
        int pageIndex = Math.Max(0, low - 1);
        KvDiskIndexEntry[] page = Page(pageIndex, cancellationToken);
        low = 0;
        high = page.Length;
        for (int step = 0; low < high && step < 32; step++)
        {
            int middle = low + ((high - low) / 2);
            if (page[middle].Key.AsSpan().SequenceCompareTo(key) < 0) low = middle + 1;
            else high = middle;
        }
        return checked(pageIndex * PageSize + low);
    }

    /// <summary>复用仍被读者或 GC 保留的页；同一页并发缺失只读取一次，不阻塞其它页。</summary>
    private KvDiskIndexEntry[] Page(int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        KvDiskIndexFence fence = _fences[index];
        lock (fence)
        {
            if (fence.Page is not null && fence.Page.TryGetTarget(out KvDiskIndexEntry[]? cached))
            {
                Volatile.Write(ref _hotPage, cached);
                return cached;
            }
            int first = checked(index * PageSize);
            KvDiskIndexEntry[] page = _readPage(fence.Offset, first, Math.Min(PageSize, Count - first), cancellationToken);
            if (!page[0].Key.AsSpan().SequenceEqual(fence.FirstKey))
                throw new InvalidDataException("KV state index page no longer matches its opening snapshot.");
            fence.Page = new WeakReference<KvDiskIndexEntry[]>(page);
            Volatile.Write(ref _hotPage, page);
            return page;
        }
    }

    /// <summary>按页顺序枚举，只有当前页必须保持存活。</summary>
    public IEnumerator<KvDiskIndexEntry> GetEnumerator()
    {
        for (int index = 0; index < _fences.Length; index++)
            foreach (KvDiskIndexEntry entry in Page(index))
                yield return entry;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
