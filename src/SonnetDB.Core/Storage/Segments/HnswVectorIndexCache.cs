namespace SonnetDB.Storage.Segments;

internal readonly record struct HnswVectorIndexCacheKey(long SegmentId, int BlockIndex, uint Crc32);

internal sealed class HnswVectorIndexCache
{
    private readonly object _lock = new();
    private readonly Dictionary<HnswVectorIndexCacheKey, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _lru = new();
    private long _currentBytes;
    private long _maxBytes;
    private long _hitCount;
    private long _missCount;

    public long CurrentBytes
    {
        get
        {
            lock (_lock)
                return _currentBytes;
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
                return _entries.Count;
        }
    }

    public long HitCount
    {
        get
        {
            lock (_lock)
                return _hitCount;
        }
    }

    public long MissCount
    {
        get
        {
            lock (_lock)
                return _missCount;
        }
    }

    public long GetSegmentBytes(long segmentId)
    {
        lock (_lock)
        {
            long bytes = 0L;
            foreach (var node in _entries.Values)
            {
                if (node.Value.Key.SegmentId == segmentId)
                    bytes += node.Value.EstimatedBytes;
            }

            return bytes;
        }
    }

    public int GetSegmentCount(long segmentId)
    {
        lock (_lock)
        {
            int count = 0;
            foreach (var key in _entries.Keys)
            {
                if (key.SegmentId == segmentId)
                    count++;
            }

            return count;
        }
    }

    public bool TryGet(HnswVectorIndexCacheKey key, out IVectorIndexReader index)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                _hitCount++;
                index = node.Value.Index;
                return true;
            }

            _missCount++;
            index = null!;
            return false;
        }
    }

    public bool TryAdd(
        HnswVectorIndexCacheKey key,
        IVectorIndexReader index,
        long estimatedBytes,
        long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(index);

        if (maxBytes <= 0 || estimatedBytes <= 0 || estimatedBytes > maxBytes)
            return false;

        lock (_lock)
        {
            _maxBytes = maxBytes;

            if (_entries.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _entries.Remove(key);
                _currentBytes -= existing.Value.EstimatedBytes;
            }

            var entry = new Entry(key, index, estimatedBytes);
            var node = new LinkedListNode<Entry>(entry);
            _lru.AddFirst(node);
            _entries.Add(key, node);
            _currentBytes += estimatedBytes;

            EvictOverBudget();
            return _entries.ContainsKey(key);
        }
    }

    public void TrimToBudget(long maxBytes)
    {
        if (maxBytes <= 0)
            return;

        lock (_lock)
        {
            _maxBytes = maxBytes;
            EvictOverBudget();
        }
    }

    public void RemoveSegment(long segmentId)
    {
        lock (_lock)
        {
            var remove = new List<HnswVectorIndexCacheKey>();
            foreach (var key in _entries.Keys)
            {
                if (key.SegmentId == segmentId)
                    remove.Add(key);
            }

            foreach (var key in remove)
            {
                if (!_entries.TryGetValue(key, out var node))
                    continue;

                _lru.Remove(node);
                _entries.Remove(key);
                _currentBytes -= node.Value.EstimatedBytes;
            }
        }
    }

    private void EvictOverBudget()
    {
        while (_maxBytes > 0 && _currentBytes > _maxBytes && _lru.Last is not null)
        {
            var node = _lru.Last;
            _lru.RemoveLast();
            _entries.Remove(node.Value.Key);
            _currentBytes -= node.Value.EstimatedBytes;
        }
    }

    private sealed class Entry
    {
        public Entry(
            HnswVectorIndexCacheKey key,
            IVectorIndexReader index,
            long estimatedBytes)
        {
            Key = key;
            Index = index;
            EstimatedBytes = estimatedBytes;
        }

        public HnswVectorIndexCacheKey Key { get; }

        public IVectorIndexReader Index { get; }

        public long EstimatedBytes { get; }
    }
}
