namespace SonnetDB.Kv.Lsm;

/// <summary>
/// 已校验 SST 数据块的分片 LRU 缓存，按块字节数计费，可在多个 SST/引擎之间共享。
/// </summary>
/// <remarks>
/// 缓存键为 <c>(readerId, blockOffset)</c>，其中 readerId 在每次打开 SST 时唯一分配；
/// 文件被删除或读取器关闭后，残留条目无法再被命中，随 LRU 自然淘汰。
/// 单块大于分片容量时不缓存。
/// </remarks>
internal sealed class KvBlockCache
{
    private const int ShardCount = 16;
    private readonly Shard[] _shards;
    private long _hits;
    private long _misses;
    private long _evictions;

    /// <summary>创建缓存。</summary>
    /// <param name="capacityBytes">总容量（字节），必须为正数。</param>
    internal KvBlockCache(long capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        CapacityBytes = capacityBytes;
        long perShard = Math.Max(1, capacityBytes / ShardCount);
        _shards = [.. Enumerable.Range(0, ShardCount).Select(_ => new Shard(perShard))];
    }

    /// <summary>总容量（字节）。</summary>
    internal long CapacityBytes { get; }

    /// <summary>命中次数。</summary>
    internal long Hits => Interlocked.Read(ref _hits);

    /// <summary>未命中次数。</summary>
    internal long Misses => Interlocked.Read(ref _misses);

    /// <summary>淘汰次数。</summary>
    internal long Evictions => Interlocked.Read(ref _evictions);

    /// <summary>当前占用字节数。</summary>
    internal long UsageBytes => _shards.Sum(static shard => shard.Usage);

    /// <summary>查找块；命中时移到 LRU 头部。</summary>
    internal bool TryGet(long readerId, long offset, out KvSstBlock block)
    {
        bool hit = ShardFor(readerId, offset).TryGet((readerId, offset), out block);
        Interlocked.Increment(ref hit ? ref _hits : ref _misses);
        return hit;
    }

    /// <summary>插入块（已存在时保留原条目）。</summary>
    internal void Insert(long readerId, long offset, KvSstBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        int evicted = ShardFor(readerId, offset).Insert((readerId, offset), block);
        if (evicted > 0)
            Interlocked.Add(ref _evictions, evicted);
    }

    private Shard ShardFor(long readerId, long offset)
        => _shards[(int)((uint)HashCode.Combine(readerId, offset) % ShardCount)];

    private sealed class Shard(long capacity)
    {
        private readonly object _sync = new();
        private readonly Dictionary<(long, long), LinkedListNode<(long, long, KvSstBlock)>> _map = [];
        private readonly LinkedList<(long, long, KvSstBlock)> _lru = new();
        private long _usage;

        internal long Usage { get { lock (_sync) return _usage; } }

        internal bool TryGet((long, long) key, out KvSstBlock block)
        {
            lock (_sync)
            {
                if (_map.TryGetValue(key, out LinkedListNode<(long, long, KvSstBlock)>? node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    block = node.Value.Item3;
                    return true;
                }
            }
            block = null!;
            return false;
        }

        internal int Insert((long, long) key, KvSstBlock block)
        {
            long charge = block.Size;
            if (charge > capacity)
                return 0;
            lock (_sync)
            {
                if (_map.ContainsKey(key))
                    return 0;
                _map[key] = _lru.AddFirst((key.Item1, key.Item2, block));
                _usage += charge;
                int evicted = 0;
                while (_usage > capacity && _lru.Last is { } last)
                {
                    _lru.RemoveLast();
                    _map.Remove((last.Value.Item1, last.Value.Item2));
                    _usage -= last.Value.Item3.Size;
                    evicted++;
                }
                return evicted;
            }
        }
    }
}
