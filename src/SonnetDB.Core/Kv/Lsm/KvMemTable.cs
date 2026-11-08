namespace SonnetDB.Kv.Lsm;

/// <summary>
/// KV-LSM 内存表：按 internal key 排序的并发跳表，单写者、多读者无锁读取。
/// </summary>
/// <remarks>
/// <para>
/// Safe-only arena 实现：节点记录、next 指针和 key/value 字节分别存放在分块数组中，
/// 节点之间用 <see cref="int"/> 下标互指（0 为头节点，同时表示空指针），避免每个条目分配多个托管对象。
/// </para>
/// <para>
/// 发布顺序：写者先完整写好节点记录、key/value 与新节点自身的 next 槽位，再从第 0 层起用
/// <see cref="Volatile.Write(ref int, int)"/> 把新节点挂到前驱上；读者用 <see cref="Volatile.Read(ref int)"/>
/// 读取 next 槽位，因此只要看到节点下标，就一定看到其完整内容。分块目录扩容时先发布新目录再链接节点。
/// </para>
/// <para>条目只增不改不删；同一 internal key 只能插入一次。</para>
/// </remarks>
internal sealed class KvMemTable
{
    /// <summary>跳表最大高度。</summary>
    internal const int MaxHeight = 12;

    private const int NodeChunkShift = 12;
    private const int NodeChunkSize = 1 << NodeChunkShift;
    private const int NextChunkSize = 16 * 1024;
    private const int DataChunkSize = 1024 * 1024;
    private const int LargeEntryThreshold = 256 * 1024;
    private const int NodeRecordBytes = 40;

    private readonly object _writeSync = new();
    private NodeRecord[][] _nodes = new NodeRecord[4][];
    private int[][] _next = new int[4][];
    private byte[][] _data = new byte[4][];
    private int _nodeCount;
    private int _nextChunkCount;
    private int _nextPosition;
    private int _dataChunkCount;
    private int _dataPosition;
    private int _maxHeight = 1;
    private uint _random = 0x9E3779B9;
    private long _entryCount;
    private long _approximateBytes;
    private long _minSequence = long.MaxValue;
    private long _maxSequence = long.MinValue;

    /// <summary>创建空内存表。</summary>
    internal KvMemTable()
    {
        _nodes[0] = new NodeRecord[NodeChunkSize];
        Interlocked.Add(ref _approximateBytes, (long)NodeChunkSize * NodeRecordBytes);
        (int nextChunk, int nextOffset) = AllocateNextSlots(MaxHeight);
        _nodes[0][0] = new NodeRecord { Height = MaxHeight, NextChunk = nextChunk, NextOffset = nextOffset };
        _nodeCount = 1;
    }

    /// <summary>条目数。</summary>
    internal long EntryCount => Volatile.Read(ref _entryCount);

    /// <summary>已分配的 arena 字节数（近似内存占用）。</summary>
    internal long ApproximateMemoryUsage => Volatile.Read(ref _approximateBytes);

    /// <summary>最小 sequence；空表为 <see cref="long.MaxValue"/>。</summary>
    internal long MinSequence => Volatile.Read(ref _minSequence);

    /// <summary>最大 sequence；空表为 <see cref="long.MinValue"/>。</summary>
    internal long MaxSequence => Volatile.Read(ref _maxSequence);

    /// <summary>插入一个条目；可与读者并发，多个写者之间由内部锁串行化。</summary>
    /// <exception cref="ArgumentException">internal key 无效、重复，或墓碑带值/TTL。</exception>
    internal void Add(ReadOnlySpan<byte> internalKey, ReadOnlySpan<byte> value, long expiresAtUtcTicks = 0)
    {
        try
        {
            KvInternalKey.Validate(internalKey);
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(exception.Message, nameof(internalKey), exception);
        }
        ArgumentOutOfRangeException.ThrowIfNegative(expiresAtUtcTicks);
        if (KvInternalKey.Kind(internalKey) == KvEntryKind.Delete && (!value.IsEmpty || expiresAtUtcTicks != 0))
            throw new ArgumentException("KV-LSM delete tombstones cannot carry a value or TTL.", nameof(value));

        lock (_writeSync)
        {
            Span<int> previous = stackalloc int[MaxHeight];
            int found = FindGreaterOrEqual(internalKey, previous);
            if (found != 0 && KvInternalKey.Compare(NodeKey(found), internalKey) == 0)
                throw new ArgumentException("KV-LSM memtable already contains this internal key.", nameof(internalKey));

            int height = RandomHeight();
            int currentMax = _maxHeight;
            if (height > currentMax)
            {
                for (int level = currentMax; level < height; level++)
                    previous[level] = 0;
                // 读者看到更高的高度时，头节点在新层上的指针仍为空，会直接下降，因此可以先发布高度。
                Volatile.Write(ref _maxHeight, height);
            }

            (int dataChunk, int dataOffset) = AllocateData(internalKey.Length + value.Length);
            byte[] data = _data[dataChunk];
            internalKey.CopyTo(data.AsSpan(dataOffset));
            value.CopyTo(data.AsSpan(dataOffset + internalKey.Length));

            (int nextChunk, int nextOffset) = AllocateNextSlots(height);
            int[] nextSlots = _next[nextChunk];
            for (int level = 0; level < height; level++)
                nextSlots[nextOffset + level] = NextSlot(previous[level], level);

            int node = AllocateNode();
            _nodes[node >> NodeChunkShift][node & (NodeChunkSize - 1)] = new NodeRecord
            {
                DataChunk = dataChunk,
                DataOffset = dataOffset,
                KeyLength = internalKey.Length,
                ValueLength = value.Length,
                ExpiresAtUtcTicks = expiresAtUtcTicks,
                Height = height,
                NextChunk = nextChunk,
                NextOffset = nextOffset,
            };

            for (int level = 0; level < height; level++)
            {
                ref NodeRecord predecessor = ref Node(previous[level]);
                Volatile.Write(ref _next[predecessor.NextChunk][predecessor.NextOffset + level], node);
            }

            long sequence = KvInternalKey.Sequence(internalKey);
            if (sequence < _minSequence)
                Volatile.Write(ref _minSequence, sequence);
            if (sequence > _maxSequence)
                Volatile.Write(ref _maxSequence, sequence);
            Interlocked.Increment(ref _entryCount);
        }
    }

    /// <summary>
    /// 查找 <paramref name="userKey"/> 在 <paramref name="snapshotSequence"/> 可见的最新版本（可能是墓碑）。
    /// </summary>
    internal bool TryGet(ReadOnlySpan<byte> userKey, long snapshotSequence, out KvSstEntry entry)
    {
        int node = FindGreaterOrEqual(KvInternalKey.EncodeSeekKey(userKey, snapshotSequence), previous: default);
        if (node == 0 || !KvInternalKey.UserKey(NodeKey(node)).SequenceEqual(userKey))
        {
            entry = default;
            return false;
        }

        ReadOnlySpan<byte> key = NodeKey(node);
        ref NodeRecord record = ref Node(node);
        entry = new KvSstEntry(
            key.ToArray(),
            KvInternalKey.Kind(key),
            KvInternalKey.Sequence(key),
            record.ExpiresAtUtcTicks,
            NodeValue(node).ToArray());
        return true;
    }

    /// <summary>创建双向迭代器（创建后需定位）。</summary>
    internal KvMemTableIterator CreateIterator() => new(this);

    internal ReadOnlySpan<byte> NodeKey(int node)
    {
        ref NodeRecord record = ref Node(node);
        return Volatile.Read(ref _data)[record.DataChunk].AsSpan(record.DataOffset, record.KeyLength);
    }

    internal ReadOnlySpan<byte> NodeValue(int node)
    {
        ref NodeRecord record = ref Node(node);
        return Volatile.Read(ref _data)[record.DataChunk].AsSpan(record.DataOffset + record.KeyLength, record.ValueLength);
    }

    internal long NodeExpires(int node) => Node(node).ExpiresAtUtcTicks;

    internal int NextNode(int node, int level)
    {
        ref NodeRecord record = ref Node(node);
        return Volatile.Read(ref Volatile.Read(ref _next)[record.NextChunk][record.NextOffset + level]);
    }

    /// <summary>首个 key ≥ target 的节点；不存在返回 0。<paramref name="previous"/> 非空时记录每层前驱。</summary>
    internal int FindGreaterOrEqual(ReadOnlySpan<byte> target, Span<int> previous)
    {
        int node = 0;
        int level = Volatile.Read(ref _maxHeight) - 1;
        while (true)
        {
            int next = NextNode(node, level);
            if (next != 0 && KvInternalKey.Compare(NodeKey(next), target) < 0)
            {
                node = next;
                continue;
            }

            if (!previous.IsEmpty)
                previous[level] = node;
            if (level == 0)
                return next;
            level--;
        }
    }

    /// <summary>最后一个 key &lt; target 的节点；不存在返回 0。</summary>
    internal int FindLessThan(ReadOnlySpan<byte> target)
    {
        int node = 0;
        int level = Volatile.Read(ref _maxHeight) - 1;
        while (true)
        {
            int next = NextNode(node, level);
            if (next != 0 && KvInternalKey.Compare(NodeKey(next), target) < 0)
            {
                node = next;
                continue;
            }
            if (level == 0)
                return node;
            level--;
        }
    }

    /// <summary>最后一个节点；空表返回 0。</summary>
    internal int FindLast()
    {
        int node = 0;
        int level = Volatile.Read(ref _maxHeight) - 1;
        while (true)
        {
            int next = NextNode(node, level);
            if (next != 0)
            {
                node = next;
                continue;
            }
            if (level == 0)
                return node;
            level--;
        }
    }

    private ref NodeRecord Node(int node)
        => ref Volatile.Read(ref _nodes)[node >> NodeChunkShift][node & (NodeChunkSize - 1)];

    private int NextSlot(int node, int level)
    {
        ref NodeRecord record = ref Node(node);
        return _next[record.NextChunk][record.NextOffset + level];
    }

    private int RandomHeight()
    {
        int height = 1;
        while (height < MaxHeight)
        {
            _random ^= _random << 13;
            _random ^= _random >> 17;
            _random ^= _random << 5;
            if ((_random & 3) != 0)
                break;
            height++;
        }
        return height;
    }

    private int AllocateNode()
    {
        int node = _nodeCount;
        int chunk = node >> NodeChunkShift;
        if ((node & (NodeChunkSize - 1)) == 0)
        {
            if (chunk == _nodes.Length)
            {
                var grown = new NodeRecord[_nodes.Length * 2][];
                Array.Copy(_nodes, grown, _nodes.Length);
                Volatile.Write(ref _nodes, grown);
            }
            _nodes[chunk] = new NodeRecord[NodeChunkSize];
            Interlocked.Add(ref _approximateBytes, (long)NodeChunkSize * NodeRecordBytes);
        }
        _nodeCount = checked(node + 1);
        return node;
    }

    private (int Chunk, int Offset) AllocateNextSlots(int count)
    {
        if (_nextChunkCount == 0 || _nextPosition + count > NextChunkSize)
        {
            if (_nextChunkCount == _next.Length)
            {
                var grown = new int[_next.Length * 2][];
                Array.Copy(_next, grown, _next.Length);
                Volatile.Write(ref _next, grown);
            }
            _next[_nextChunkCount++] = new int[NextChunkSize];
            _nextPosition = 0;
            Interlocked.Add(ref _approximateBytes, (long)NextChunkSize * sizeof(int));
        }
        int offset = _nextPosition;
        _nextPosition += count;
        return (_nextChunkCount - 1, offset);
    }

    private (int Chunk, int Offset) AllocateData(int length)
    {
        if (length >= LargeEntryThreshold)
            return (AddDataChunk(new byte[length]), 0);
        if (_dataChunkCount == 0 || _dataPosition + length > _data[_dataChunkCount - 1].Length || _data[_dataChunkCount - 1].Length != DataChunkSize)
        {
            AddDataChunk(new byte[DataChunkSize]);
            _dataPosition = 0;
        }
        int offset = _dataPosition;
        _dataPosition += length;
        return (_dataChunkCount - 1, offset);
    }

    private int AddDataChunk(byte[] chunk)
    {
        if (_dataChunkCount == _data.Length)
        {
            var grown = new byte[_data.Length * 2][];
            Array.Copy(_data, grown, _data.Length);
            Volatile.Write(ref _data, grown);
        }
        _data[_dataChunkCount] = chunk;
        Interlocked.Add(ref _approximateBytes, chunk.Length);
        return _dataChunkCount++;
    }

    private struct NodeRecord
    {
        public int DataChunk;
        public int DataOffset;
        public int KeyLength;
        public int ValueLength;
        public long ExpiresAtUtcTicks;
        public int Height;
        public int NextChunk;
        public int NextOffset;
    }
}

/// <summary>
/// <see cref="KvMemTable"/> 的双向迭代器；可与写者并发，看到迭代过程中新插入的条目是允许的
/// （上层通过 sequence 过滤保证快照语义）。
/// </summary>
internal sealed class KvMemTableIterator : IKvInternalIterator
{
    private readonly KvMemTable _table;
    private int _node;

    internal KvMemTableIterator(KvMemTable table) => _table = table;

    /// <inheritdoc />
    public bool Valid => _node != 0;

    /// <inheritdoc />
    public ReadOnlySpan<byte> Key => Valid ? _table.NodeKey(_node) : throw InvalidPosition();

    /// <inheritdoc />
    public ReadOnlySpan<byte> Value => Valid ? _table.NodeValue(_node) : throw InvalidPosition();

    /// <inheritdoc />
    public long ExpiresAtUtcTicks => Valid ? _table.NodeExpires(_node) : throw InvalidPosition();

    /// <inheritdoc />
    public void SeekToFirst() => _node = _table.NextNode(0, 0);

    /// <inheritdoc />
    public void SeekToLast() => _node = _table.FindLast();

    /// <inheritdoc />
    public void Seek(ReadOnlySpan<byte> target) => _node = _table.FindGreaterOrEqual(target, previous: default);

    /// <inheritdoc />
    public void SeekForPrev(ReadOnlySpan<byte> target)
    {
        Seek(target);
        if (!Valid)
            SeekToLast();
        else if (KvInternalKey.Compare(Key, target) > 0)
            MovePrev();
    }

    /// <inheritdoc />
    public void MoveNext()
    {
        if (!Valid)
            throw InvalidPosition();
        _node = _table.NextNode(_node, 0);
    }

    /// <inheritdoc />
    public void MovePrev()
    {
        if (!Valid)
            throw InvalidPosition();
        _node = _table.FindLessThan(_table.NodeKey(_node));
    }

    /// <inheritdoc />
    public void Dispose() => _node = 0;

    private static InvalidOperationException InvalidPosition()
        => new("KV-LSM memtable iterator is not positioned on an entry.");
}
