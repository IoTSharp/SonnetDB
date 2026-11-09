namespace SonnetDB.Kv.Lsm;

/// <summary>
/// internal key 有序数据源的统一双向迭代器（memtable、SST、合并结果）。
/// </summary>
/// <remarks>
/// <see cref="Key"/> 与 <see cref="Value"/> 返回的 span 只在下一次移动或定位之前有效。
/// <see cref="Value"/> 为用户 value（不含 TTL 前缀），墓碑为空。
/// </remarks>
internal interface IKvInternalIterator : IDisposable
{
    /// <summary>是否指向一个条目。</summary>
    bool Valid { get; }

    /// <summary>当前 internal key。</summary>
    ReadOnlySpan<byte> Key { get; }

    /// <summary>当前用户 value。</summary>
    ReadOnlySpan<byte> Value { get; }

    /// <summary>当前条目到期时间（UTC ticks），0 表示无 TTL。</summary>
    long ExpiresAtUtcTicks { get; }

    /// <summary>定位到首条目。</summary>
    void SeekToFirst();

    /// <summary>定位到最后一个条目。</summary>
    void SeekToLast();

    /// <summary>定位到首个 key ≥ target 的条目。</summary>
    void Seek(ReadOnlySpan<byte> target);

    /// <summary>定位到最后一个 key ≤ target 的条目。</summary>
    void SeekForPrev(ReadOnlySpan<byte> target);

    /// <summary>前进一条。</summary>
    void MoveNext();

    /// <summary>后退一条。</summary>
    void MovePrev();
}

/// <summary>
/// 把多个有序子迭代器合并为一个有序流；相同 internal key 只产出一次，取序号最小（最新数据源）的子迭代器。
/// </summary>
/// <remarks>
/// 子迭代器按优先级排列：下标越小代表越新的数据源（活跃 memtable、不可变 memtable、L0 新→旧、L1…）。
/// 相同 internal key 出现在多个子迭代器中（例如 WAL 回放与已刷盘 SST 重叠）时视为同一版本。
/// 方向切换按 LevelDB 方式重新定位其余子迭代器。子迭代器数量很少，最小/最大值采用线性扫描。
/// </remarks>
internal sealed class KvMergingIterator : IKvInternalIterator
{
    private readonly IKvInternalIterator[] _children;
    private IKvInternalIterator? _current;
    private bool _forward = true;
    private byte[] _keyBuffer = new byte[64];

    /// <summary>创建合并迭代器，并接管子迭代器的生命周期。</summary>
    internal KvMergingIterator(IReadOnlyList<IKvInternalIterator> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        _children = [.. children];
    }

    /// <inheritdoc />
    public bool Valid => _current is { Valid: true };

    /// <inheritdoc />
    public ReadOnlySpan<byte> Key => Valid ? _current!.Key : throw InvalidPosition();

    /// <inheritdoc />
    public ReadOnlySpan<byte> Value => Valid ? _current!.Value : throw InvalidPosition();

    /// <inheritdoc />
    public long ExpiresAtUtcTicks => Valid ? _current!.ExpiresAtUtcTicks : throw InvalidPosition();

    /// <inheritdoc />
    public void SeekToFirst()
    {
        foreach (IKvInternalIterator child in _children)
            child.SeekToFirst();
        _forward = true;
        FindSmallest();
    }

    /// <inheritdoc />
    public void SeekToLast()
    {
        foreach (IKvInternalIterator child in _children)
            child.SeekToLast();
        _forward = false;
        FindLargest();
    }

    /// <inheritdoc />
    public void Seek(ReadOnlySpan<byte> target)
    {
        foreach (IKvInternalIterator child in _children)
            child.Seek(target);
        _forward = true;
        FindSmallest();
    }

    /// <inheritdoc />
    public void SeekForPrev(ReadOnlySpan<byte> target)
    {
        foreach (IKvInternalIterator child in _children)
            child.SeekForPrev(target);
        _forward = false;
        FindLargest();
    }

    /// <inheritdoc />
    public void MoveNext()
    {
        if (!Valid)
            throw InvalidPosition();
        ReadOnlySpan<byte> key = CopyCurrentKey();

        if (!_forward)
        {
            // 反向时其余子迭代器位于 key 之前；把它们移到首个 > key 的位置。
            foreach (IKvInternalIterator child in _children)
            {
                if (ReferenceEquals(child, _current))
                    continue;
                child.Seek(key);
                if (child.Valid && KvInternalKey.Compare(child.Key, key) == 0)
                    child.MoveNext();
            }
            _forward = true;
        }
        else
        {
            // 跳过其它数据源中的同一 internal key。
            foreach (IKvInternalIterator child in _children)
            {
                if (!ReferenceEquals(child, _current) && child.Valid && KvInternalKey.Compare(child.Key, key) == 0)
                    child.MoveNext();
            }
        }

        _current!.MoveNext();
        FindSmallest();
    }

    /// <inheritdoc />
    public void MovePrev()
    {
        if (!Valid)
            throw InvalidPosition();
        ReadOnlySpan<byte> key = CopyCurrentKey();

        if (_forward)
        {
            // 正向时其余子迭代器位于 key 之后（或等于 key）；把它们移到最后一个 < key 的位置。
            foreach (IKvInternalIterator child in _children)
            {
                if (ReferenceEquals(child, _current))
                    continue;
                child.Seek(key);
                if (child.Valid)
                    child.MovePrev();
                else
                    child.SeekToLast();
            }
            _forward = false;
        }
        else
        {
            foreach (IKvInternalIterator child in _children)
            {
                if (!ReferenceEquals(child, _current) && child.Valid && KvInternalKey.Compare(child.Key, key) == 0)
                    child.MovePrev();
            }
        }

        _current!.MovePrev();
        FindLargest();
    }

    /// <summary>释放全部子迭代器。</summary>
    public void Dispose()
    {
        foreach (IKvInternalIterator child in _children)
            child.Dispose();
        _current = null;
    }

    private ReadOnlySpan<byte> CopyCurrentKey()
    {
        ReadOnlySpan<byte> key = _current!.Key;
        if (_keyBuffer.Length < key.Length)
            _keyBuffer = new byte[Math.Max(key.Length, _keyBuffer.Length * 2)];
        key.CopyTo(_keyBuffer);
        return _keyBuffer.AsSpan(0, key.Length);
    }

    private void FindSmallest()
    {
        IKvInternalIterator? smallest = null;
        foreach (IKvInternalIterator child in _children)
        {
            if (child.Valid && (smallest is null || KvInternalKey.Compare(child.Key, smallest.Key) < 0))
                smallest = child;
        }
        _current = smallest;
    }

    private void FindLargest()
    {
        IKvInternalIterator? largest = null;
        foreach (IKvInternalIterator child in _children)
        {
            if (child.Valid && (largest is null || KvInternalKey.Compare(child.Key, largest.Key) > 0))
                largest = child;
        }
        _current = largest;
    }

    private static InvalidOperationException InvalidPosition()
        => new("KV-LSM merging iterator is not positioned on an entry.");
}

/// <summary>
/// 按快照把 internal key 流还原为用户可见视图：每个 user key 只产出在 <c>snapshotSequence</c>
/// 可见的最新版本；该版本为墓碑或在 <c>readTimestampUtcTicks</c> 已到期时整个 key 不可见。
/// </summary>
/// <remarks>
/// <para>支持 <c>[lowerBound, upperBound)</c> 范围以及正/反向移动与方向切换（与 LevelDB DBIter 等价）。</para>
/// <para>正向时 <see cref="Key"/>/<see cref="Value"/> 直接引用数据源缓冲；反向时引用内部保存的副本。</para>
/// </remarks>
internal sealed class KvSnapshotIterator : IDisposable
{
    private readonly IKvInternalIterator _source;
    private readonly long _snapshotSequence;
    private readonly long _readTimestampUtcTicks;
    private readonly byte[]? _lowerBound;
    private readonly byte[]? _upperBound;
    private readonly KvRangeTombstoneSet _rangeTombstones;
    private bool _forward = true;
    private bool _valid;
    private byte[] _savedKey = new byte[64];
    private int _savedKeyLength;
    private byte[] _savedValue = new byte[64];
    private int _savedValueLength;
    private long _savedSequence;
    private long _savedExpires;

    /// <summary>创建快照迭代器，并接管 <paramref name="source"/> 的生命周期。</summary>
    /// <param name="source">internal key 有序数据源（通常为 <see cref="KvMergingIterator"/>）。</param>
    /// <param name="snapshotSequence">可见的最大 sequence。</param>
    /// <param name="readTimestampUtcTicks">判断 TTL 的固定时刻（UTC ticks）。</param>
    /// <param name="lowerBound">包含的 user key 下界；null 表示无下界。</param>
    /// <param name="upperBound">不包含的 user key 上界；null 表示无上界。</param>
    /// <param name="rangeTombstones">该快照可见的 range tombstone 分段视图；null 表示没有。</param>
    internal KvSnapshotIterator(
        IKvInternalIterator source,
        long snapshotSequence,
        long readTimestampUtcTicks,
        byte[]? lowerBound = null,
        byte[]? upperBound = null,
        KvRangeTombstoneSet? rangeTombstones = null)
    {
        _rangeTombstones = rangeTombstones ?? KvRangeTombstoneSet.Empty;
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotSequence);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(snapshotSequence, KvInternalKey.MaxSequence);
        _source = source;
        _snapshotSequence = snapshotSequence;
        _readTimestampUtcTicks = readTimestampUtcTicks;
        _lowerBound = lowerBound;
        _upperBound = upperBound;
    }

    /// <summary>是否指向一个可见条目。</summary>
    internal bool Valid => _valid;

    /// <summary>当前 user key。</summary>
    internal ReadOnlySpan<byte> Key
        => !_valid ? throw InvalidPosition()
            : _forward ? KvInternalKey.UserKey(_source.Key) : _savedKey.AsSpan(0, _savedKeyLength);

    /// <summary>当前 value。</summary>
    internal ReadOnlySpan<byte> Value
        => !_valid ? throw InvalidPosition()
            : _forward ? _source.Value : _savedValue.AsSpan(0, _savedValueLength);

    /// <summary>当前条目的版本（sequence）。</summary>
    internal long Sequence
        => !_valid ? throw InvalidPosition()
            : _forward ? KvInternalKey.Sequence(_source.Key) : _savedSequence;

    /// <summary>当前条目到期时间（UTC ticks），0 表示无 TTL。</summary>
    internal long ExpiresAtUtcTicks
        => !_valid ? throw InvalidPosition()
            : _forward ? _source.ExpiresAtUtcTicks : _savedExpires;

    /// <summary>定位到范围内第一个可见 key。</summary>
    internal void SeekToFirst()
    {
        if (_lowerBound is not null)
        {
            Seek(_lowerBound);
            return;
        }
        _forward = true;
        _source.SeekToFirst();
        FindNextUserEntry(skipping: false);
    }

    /// <summary>定位到范围内最后一个可见 key。</summary>
    internal void SeekToLast()
    {
        _forward = false;
        if (_upperBound is not null)
            _source.SeekForPrev(KvInternalKey.EncodeSeekKey(_upperBound, KvInternalKey.MaxSequence));
        else
            _source.SeekToLast();
        FindPrevUserEntry();
    }

    /// <summary>定位到首个 ≥ <paramref name="userKey"/> 的可见 key（受下界约束）。</summary>
    internal void Seek(ReadOnlySpan<byte> userKey)
    {
        if (_lowerBound is not null && userKey.SequenceCompareTo(_lowerBound) < 0)
            userKey = _lowerBound;
        _forward = true;
        _source.Seek(KvInternalKey.EncodeSeekKey(userKey, _snapshotSequence));
        FindNextUserEntry(skipping: false);
    }

    /// <summary>定位到最后一个 ≤ <paramref name="userKey"/> 的可见 key（受上界约束）。</summary>
    internal void SeekForPrev(ReadOnlySpan<byte> userKey)
    {
        if (_upperBound is not null && userKey.SequenceCompareTo(_upperBound) >= 0)
        {
            SeekToLast();
            return;
        }
        _forward = false;
        _source.SeekForPrev(KvInternalKey.EncodeLastKey(userKey));
        FindPrevUserEntry();
    }

    /// <summary>移动到下一个可见 key。</summary>
    internal void MoveNext()
    {
        if (!_valid)
            throw InvalidPosition();

        if (!_forward)
        {
            // 数据源位于当前 key 的全部版本之前：前移进入这些版本，再按跳过规则越过它们。
            _forward = true;
            if (!_source.Valid)
                _source.SeekToFirst();
            else
                _source.MoveNext();
        }
        else
        {
            SaveKey(KvInternalKey.UserKey(_source.Key));
            _source.MoveNext();
        }

        FindNextUserEntry(skipping: true);
    }

    /// <summary>移动到上一个可见 key。</summary>
    internal void MovePrev()
    {
        if (!_valid)
            throw InvalidPosition();

        if (_forward)
        {
            // 数据源位于当前 key 的可见版本：后退到所有版本之前。
            SaveKey(KvInternalKey.UserKey(_source.Key));
            while (true)
            {
                _source.MovePrev();
                if (!_source.Valid)
                {
                    _valid = false;
                    _savedKeyLength = 0;
                    return;
                }
                if (KvInternalKey.UserKey(_source.Key).SequenceCompareTo(SavedKey) < 0)
                    break;
            }
            _forward = false;
        }

        FindPrevUserEntry();
    }

    /// <summary>释放数据源。</summary>
    public void Dispose() => _source.Dispose();

    private ReadOnlySpan<byte> SavedKey => _savedKey.AsSpan(0, _savedKeyLength);

    private void FindNextUserEntry(bool skipping)
    {
        // skipping 为 true 时，≤ SavedKey 的 user key 都已处理（可见或被墓碑/到期遮蔽），直接跳过。
        while (_source.Valid)
        {
            ReadOnlySpan<byte> key = _source.Key;
            ReadOnlySpan<byte> userKey = KvInternalKey.UserKey(key);
            if (_upperBound is not null && userKey.SequenceCompareTo(_upperBound) >= 0)
                break;

            if (KvInternalKey.Sequence(key) <= _snapshotSequence
                && !(skipping && userKey.SequenceCompareTo(SavedKey) <= 0))
            {
                if (IsLivePut(key, _source.ExpiresAtUtcTicks))
                {
                    _valid = true;
                    _savedKeyLength = 0;
                    return;
                }

                SaveKey(userKey);
                skipping = true;
            }
            _source.MoveNext();
        }

        _valid = false;
        _savedKeyLength = 0;
    }

    private void FindPrevUserEntry()
    {
        bool found = false;
        while (_source.Valid)
        {
            ReadOnlySpan<byte> key = _source.Key;
            ReadOnlySpan<byte> userKey = KvInternalKey.UserKey(key);
            if (_lowerBound is not null && userKey.SequenceCompareTo(_lowerBound) < 0)
                break;

            if (KvInternalKey.Sequence(key) <= _snapshotSequence)
            {
                // 反向遍历同一 user key 时版本从旧到新；越过候选 key 后候选即为最终结果。
                if (found && userKey.SequenceCompareTo(SavedKey) < 0)
                    break;

                if (IsLivePut(key, _source.ExpiresAtUtcTicks))
                {
                    found = true;
                    SaveKey(userKey);
                    SaveValue(_source.Value);
                    _savedSequence = KvInternalKey.Sequence(key);
                    _savedExpires = _source.ExpiresAtUtcTicks;
                }
                else
                {
                    found = false;
                    _savedKeyLength = 0;
                    _savedValueLength = 0;
                }
            }
            _source.MovePrev();
        }

        if (!found)
        {
            _valid = false;
            _savedKeyLength = 0;
            _savedValueLength = 0;
            _forward = true;
        }
        else
        {
            _valid = true;
        }
    }

    private bool IsLivePut(ReadOnlySpan<byte> internalKey, long expiresAtUtcTicks)
        => KvInternalKey.Kind(internalKey) == KvEntryKind.Put
            && (expiresAtUtcTicks == 0 || expiresAtUtcTicks > _readTimestampUtcTicks)
            && (_rangeTombstones.IsEmpty
                || _rangeTombstones.MaxCovering(KvInternalKey.UserKey(internalKey)) <= KvInternalKey.Sequence(internalKey));

    private void SaveKey(ReadOnlySpan<byte> userKey)
    {
        if (_savedKey.Length < userKey.Length)
            _savedKey = new byte[Math.Max(userKey.Length, _savedKey.Length * 2)];
        userKey.CopyTo(_savedKey);
        _savedKeyLength = userKey.Length;
    }

    private void SaveValue(ReadOnlySpan<byte> value)
    {
        if (_savedValue.Length < value.Length)
            _savedValue = new byte[Math.Max(value.Length, _savedValue.Length * 2)];
        value.CopyTo(_savedValue);
        _savedValueLength = value.Length;
    }

    private static InvalidOperationException InvalidPosition()
        => new("KV-LSM snapshot iterator is not positioned on a visible entry.");
}
