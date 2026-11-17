namespace SonnetDB.FullText.Storage;

/// <summary>
/// 持久全文 segment block 的缓存键。
/// </summary>
/// <param name="SegmentId">segment 标识。</param>
/// <param name="Offset">block 在 segment 文件中的字节偏移。</param>
/// <param name="Length">block 的字节长度。</param>
/// <param name="Crc32">block 内容校验值；格式没有校验值时使用 0。</param>
internal readonly record struct FullTextBlockCacheKey(
    long SegmentId,
    long Offset,
    int Length,
    uint Crc32);

/// <summary>
/// 全文 block 缓存及物理读取的累计诊断快照。
/// </summary>
/// <param name="PhysicalReadCount">成功完成文件读取的 posting block 数量。</param>
/// <param name="PhysicalReadBytes">成功完成文件读取的 posting block 字节数。</param>
/// <param name="CacheHitCount">缓存命中次数。</param>
/// <param name="CacheMissCount">缓存未命中次数。</param>
/// <param name="CacheEvictionCount">因字节预算淘汰的条目数。</param>
/// <param name="CachedBytes">当前缓存占用字节数。</param>
/// <param name="CachedBlockCount">当前缓存条目数。</param>
internal readonly record struct FullTextBlockReadStatistics(
    long PhysicalReadCount,
    long PhysicalReadBytes,
    long CacheHitCount,
    long CacheMissCount,
    long CacheEvictionCount,
    long CachedBytes,
    int CachedBlockCount);

/// <summary>
/// 按字节预算的全文 block LRU 缓存。
/// </summary>
/// <remarks>
/// <para>缓存只保存已经完成校验的不可变字节快照；调用方必须把返回数组当作只读数据使用。</para>
/// <para>所有操作都是短时同步操作，不执行文件 I/O、不等待外部任务，也不拥有取消令牌。</para>
/// <para>调用方在开始磁盘读取前负责检查取消和查询预算；缓存本身只负责有界内存与低基数统计。</para>
/// </remarks>
internal sealed class FullTextBlockCache
{
    private readonly object _sync = new();
    private readonly long _maxBytes;
    private readonly Dictionary<FullTextBlockCacheKey, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _lru = new();
    private long _currentBytes;
    private long _hitCount;
    private long _missCount;
    private long _evictionCount;

    /// <summary>
    /// 创建全文 block 缓存。
    /// </summary>
    /// <param name="maxBytes">最大缓存字节数；0 表示禁用缓存。</param>
    public FullTextBlockCache(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        _maxBytes = maxBytes;
    }

    /// <summary>最大缓存字节数。</summary>
    public long MaxBytes => _maxBytes;

    /// <summary>当前缓存字节数。</summary>
    public long CurrentBytes
    {
        get
        {
            lock (_sync)
                return _currentBytes;
        }
    }

    /// <summary>当前缓存条目数。</summary>
    public int Count
    {
        get
        {
            lock (_sync)
                return _entries.Count;
        }
    }

    /// <summary>缓存命中次数。</summary>
    public long HitCount
    {
        get
        {
            lock (_sync)
                return _hitCount;
        }
    }

    /// <summary>缓存未命中次数。</summary>
    public long MissCount
    {
        get
        {
            lock (_sync)
                return _missCount;
        }
    }

    /// <summary>因字节预算淘汰的条目数。</summary>
    public long EvictionCount
    {
        get
        {
            lock (_sync)
                return _evictionCount;
        }
    }

    /// <summary>
    /// 原子读取当前缓存统计，供端到端全文读路径基准使用。
    /// </summary>
    public (long HitCount, long MissCount, long EvictionCount, long CurrentBytes, int Count) GetStatistics()
    {
        lock (_sync)
        {
            return (_hitCount, _missCount, _evictionCount, _currentBytes, _entries.Count);
        }
    }

    /// <summary>
    /// 判断给定大小的 block 是否可以进入缓存。
    /// </summary>
    public bool CanStore(long estimatedBytes)
        => estimatedBytes > 0 && _maxBytes > 0 && estimatedBytes <= _maxBytes;

    /// <summary>
    /// 尝试读取缓存 block，并将命中条目提升为最近使用。
    /// </summary>
    public bool TryGet(FullTextBlockCacheKey key, out byte[] bytes)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                _hitCount++;
                bytes = node.Value.Bytes;
                return true;
            }

            _missCount++;
            bytes = [];
            return false;
        }
    }

    /// <summary>
    /// 尝试加入一个已经完成读取和校验的 block。
    /// </summary>
    /// <param name="key">block 键。</param>
    /// <param name="bytes">不可变 block 字节快照。</param>
    /// <param name="estimatedBytes">计入缓存预算的大小；为空时使用 <paramref name="bytes"/> 长度。</param>
    /// <returns>条目最终留在缓存中时返回 <see langword="true"/>；缓存禁用或条目过大时返回 <see langword="false"/>。</returns>
    public bool TryAdd(FullTextBlockCacheKey key, byte[] bytes, long? estimatedBytes = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        long size = estimatedBytes ?? bytes.LongLength;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        if (!CanStore(size))
            return false;

        lock (_sync)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                _lru.Remove(existing);
                _entries.Remove(key);
                _currentBytes -= existing.Value.EstimatedBytes;
            }

            Entry entry = new(key, bytes, size);
            LinkedListNode<Entry> node = new(entry);
            _lru.AddFirst(node);
            _entries.Add(key, node);
            _currentBytes = checked(_currentBytes + size);
            EvictOverBudget();
            return _entries.ContainsKey(key);
        }
    }

    /// <summary>
    /// 清空缓存条目；累计命中、未命中和淘汰统计保持不变。
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _lru.Clear();
            _currentBytes = 0;
        }
    }

    private void EvictOverBudget()
    {
        while (_currentBytes > _maxBytes && _lru.Last is not null)
        {
            LinkedListNode<Entry> node = _lru.Last;
            _lru.RemoveLast();
            _entries.Remove(node.Value.Key);
            _currentBytes -= node.Value.EstimatedBytes;
            _evictionCount++;
        }
    }

    private sealed class Entry
    {
        public Entry(FullTextBlockCacheKey key, byte[] bytes, long estimatedBytes)
        {
            Key = key;
            Bytes = bytes;
            EstimatedBytes = estimatedBytes;
        }

        public FullTextBlockCacheKey Key { get; }

        public byte[] Bytes { get; }

        public long EstimatedBytes { get; }
    }
}
