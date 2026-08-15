namespace SonnetDB.Kv;

/// <summary>
/// KV keyspace 的稳定只读快照租约。
/// </summary>
/// <remarks>
/// 快照固定创建时可见的 key/value、版本与 TTL 判断时刻。释放快照后不能再创建游标，
/// 但已经创建的游标持有独立租约，可以继续读取到末尾。
/// </remarks>
public sealed class KvReadSnapshot : IDisposable
{
    private readonly object _sync = new();
    private KvReadSnapshotState? _state;

    internal KvReadSnapshot(KvReadSnapshotState state)
    {
        _state = state;
        Sequence = state.Sequence;
        ReadTimestampUtc = state.ReadTimestampUtc;
    }

    /// <summary>创建快照时 keyspace 已应用的最后一个单调版本号。</summary>
    public long Sequence { get; }

    /// <summary>快照用于判断 TTL 可见性的固定 UTC 时刻。</summary>
    public DateTimeOffset ReadTimestampUtc { get; }

    /// <summary>
    /// 创建一个按 key 字节序升序或降序读取当前快照的范围游标。
    /// </summary>
    /// <param name="options">范围、页条目数与页字节预算；为 null 时扫描全部 key。</param>
    /// <returns>持有独立快照租约的范围游标。</returns>
    /// <exception cref="ArgumentOutOfRangeException">页条目数或页字节预算不是正数。</exception>
    /// <exception cref="ObjectDisposedException">快照已经释放。</exception>
    public KvRangeCursor OpenRangeCursor(KvRangeScanOptions? options = null)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_state is null, this);
            return _state.OpenRangeCursor(options ?? new KvRangeScanOptions());
        }
    }

    /// <summary>
    /// 在当前稳定快照内读取精确 key，并返回独立拥有的 key/value 副本。
    /// </summary>
    internal KvEntry? GetEntry(ReadOnlySpan<byte> key)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_state is null, this);
            return _state.GetEntry(key);
        }
    }

    /// <summary>为同一稳定可见视图取得独立生命周期租约。</summary>
    internal KvReadSnapshot AcquireLease()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_state is null, this);
            _state.AddReference();
            return new KvReadSnapshot(_state);
        }
    }

    /// <summary>释放快照自身持有的状态租约。</summary>
    public void Dispose()
    {
        KvReadSnapshotState? state;
        lock (_sync)
        {
            state = _state;
            _state = null;
        }

        state?.Release();
    }
}

internal sealed class KvReadSnapshotState
{
    private readonly object _sync = new();
    private readonly KeyValuePair<byte[], KvValueEntry>[] _mutableValues;
    private readonly KeyValuePair<byte[], KvValueEntry>[] _frozenValues;
    private KvDiskStateLease? _diskLease;
    private int _referenceCount = 1;
    private bool _released;

    public KvReadSnapshotState(
        KeyValuePair<byte[], KvValueEntry>[] mutableValues,
        KeyValuePair<byte[], KvValueEntry>[] frozenValues,
        KvDiskStateLease? diskLease,
        long sequence,
        DateTimeOffset readTimestampUtc)
    {
        _mutableValues = mutableValues;
        _frozenValues = frozenValues;
        _diskLease = diskLease;
        Sequence = sequence;
        ReadTimestampUtc = readTimestampUtc;
    }

    public long Sequence { get; }

    public DateTimeOffset ReadTimestampUtc { get; }

    public KvRangeCursor OpenRangeCursor(KvRangeScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.PageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxPageBytes);

        AddReference();
        try
        {
            return new KvRangeCursor(this, options);
        }
        catch
        {
            Release();
            throw;
        }
    }

    public IEnumerator<KeyValuePair<byte[], KvValueEntry>> CreateEnumerator(KvRangeScanOptions options)
    {
        byte[] prefix = options.Prefix.ToArray();
        byte[]? startInclusive = ToOptionalArray(options.StartInclusive);
        byte[]? endExclusive = ToOptionalArray(options.EndExclusive);
        byte[]? afterKey = ToOptionalArray(options.AfterKey);
        KvDiskState? diskState = _diskLease?.State;

        return EnumerateVisibleEntries(
                _mutableValues,
                _frozenValues,
                diskState,
                prefix,
                afterKey,
                startInclusive,
                endExclusive,
                options.Descending)
            .GetEnumerator();
    }

    public KvEntry? GetEntry(ReadOnlySpan<byte> key)
    {
        byte[] lookup = key.ToArray();
        KvValueEntry? entry;
        int mutableIndex = FindIndex(_mutableValues, lookup);
        if (mutableIndex >= 0)
            entry = _mutableValues[mutableIndex].Value;
        else
        {
            int frozenIndex = FindIndex(_frozenValues, lookup);
            entry = frozenIndex >= 0
                ? _frozenValues[frozenIndex].Value
                : _diskLease?.State.Get(key);
        }

        if (entry is null || entry.IsDeleted || entry.IsExpired(ReadTimestampUtc))
            return null;
        return new KvEntry(
            lookup,
            entry.Value.ToArray(),
            entry.Version,
            entry.ExpiresAtUtc);
    }

    private static IEnumerable<KeyValuePair<byte[], KvValueEntry>> EnumerateVisibleEntries(
        KeyValuePair<byte[], KvValueEntry>[] mutableValues,
        KeyValuePair<byte[], KvValueEntry>[] frozenValues,
        KvDiskState? diskState,
        byte[] prefix,
        byte[]? afterKey,
        byte[]? startInclusive,
        byte[]? endExclusive,
        bool descending)
    {
        IEnumerable<KeyValuePair<byte[], KvValueEntry>> lowerLayer = EnumerateOverlayAndDisk(
            frozenValues,
            diskState,
            prefix,
            afterKey,
            startInclusive,
            endExclusive,
            descending);
        foreach (KeyValuePair<byte[], KvValueEntry> pair in MergeOverlayAndLowerLayer(
            mutableValues,
            lowerLayer,
            prefix,
            afterKey,
            startInclusive,
            endExclusive,
            descending))
        {
            yield return pair;
        }
    }

    private static IEnumerable<KeyValuePair<byte[], KvValueEntry>> MergeOverlayAndLowerLayer(
        KeyValuePair<byte[], KvValueEntry>[] overlay,
        IEnumerable<KeyValuePair<byte[], KvValueEntry>> lowerLayer,
        byte[] prefix,
        byte[]? afterKey,
        byte[]? startInclusive,
        byte[]? endExclusive,
        bool descending)
    {
        using IEnumerator<KeyValuePair<byte[], KvValueEntry>> memory = EnumerateOverlayRange(
            overlay,
            prefix,
            afterKey,
            startInclusive,
            endExclusive,
            descending).GetEnumerator();
        using IEnumerator<KeyValuePair<byte[], KvValueEntry>> lower = lowerLayer.GetEnumerator();

        bool hasMemory = memory.MoveNext();
        bool hasLower = lower.MoveNext();
        while (hasMemory || hasLower)
        {
            if (!hasLower)
            {
                if (!memory.Current.Value.IsDeleted)
                    yield return memory.Current;
                hasMemory = memory.MoveNext();
                continue;
            }

            if (!hasMemory)
            {
                yield return lower.Current;
                hasLower = lower.MoveNext();
                continue;
            }

            int comparison = CompareInDirection(memory.Current.Key, lower.Current.Key, descending);
            if (comparison < 0)
            {
                if (!memory.Current.Value.IsDeleted)
                    yield return memory.Current;
                hasMemory = memory.MoveNext();
                continue;
            }

            if (comparison == 0)
            {
                if (!memory.Current.Value.IsDeleted)
                    yield return memory.Current;
                hasMemory = memory.MoveNext();
                hasLower = lower.MoveNext();
                continue;
            }

            yield return lower.Current;
            hasLower = lower.MoveNext();
        }
    }

    private static IEnumerable<KeyValuePair<byte[], KvValueEntry>> EnumerateOverlayAndDisk(
        KeyValuePair<byte[], KvValueEntry>[] overlay,
        KvDiskState? diskState,
        byte[] prefix,
        byte[]? afterKey,
        byte[]? startInclusive,
        byte[]? endExclusive,
        bool descending)
    {
        using IEnumerator<KeyValuePair<byte[], KvValueEntry>> memory = EnumerateOverlayRange(
            overlay,
            prefix,
            afterKey,
            startInclusive,
            endExclusive,
            descending).GetEnumerator();
        IEnumerable<KvDiskIndexEntry> diskEntries = diskState is null
            ? Array.Empty<KvDiskIndexEntry>()
            : diskState.ScanRange(prefix, startInclusive, endExclusive, afterKey, descending);
        using IEnumerator<KvDiskIndexEntry> disk = diskEntries.GetEnumerator();

        bool hasMemory = memory.MoveNext();
        bool hasDisk = disk.MoveNext();
        while (hasMemory || hasDisk)
        {
            if (!hasDisk)
            {
                if (!memory.Current.Value.IsDeleted)
                    yield return memory.Current;
                hasMemory = memory.MoveNext();
                continue;
            }

            if (!hasMemory)
            {
                KvDiskIndexEntry diskEntry = disk.Current;
                yield return new KeyValuePair<byte[], KvValueEntry>(
                    diskEntry.Key,
                    diskState!.Read(diskEntry));
                hasDisk = disk.MoveNext();
                continue;
            }

            int comparison = CompareInDirection(memory.Current.Key, disk.Current.Key, descending);
            if (comparison < 0)
            {
                if (!memory.Current.Value.IsDeleted)
                    yield return memory.Current;
                hasMemory = memory.MoveNext();
                continue;
            }

            if (comparison == 0)
            {
                if (!memory.Current.Value.IsDeleted)
                    yield return memory.Current;
                hasMemory = memory.MoveNext();
                hasDisk = disk.MoveNext();
                continue;
            }

            KvDiskIndexEntry currentDisk = disk.Current;
            yield return new KeyValuePair<byte[], KvValueEntry>(
                currentDisk.Key,
                diskState!.Read(currentDisk));
            hasDisk = disk.MoveNext();
        }
    }

    private static IEnumerable<KeyValuePair<byte[], KvValueEntry>> EnumerateOverlayRange(
        KeyValuePair<byte[], KvValueEntry>[] entries,
        byte[] prefix,
        byte[]? afterKey,
        byte[]? startInclusive,
        byte[]? endExclusive,
        bool descending)
    {
        if (descending)
        {
            foreach (var entry in EnumerateOverlayRangeDescending(
                entries,
                prefix,
                afterKey,
                startInclusive,
                endExclusive))
            {
                yield return entry;
            }
            yield break;
        }

        byte[] lowerBound = prefix;
        bool lowerBoundExclusive = false;
        if (startInclusive is not null
            && KvKeyComparer.Instance.Compare(startInclusive, lowerBound) > 0)
        {
            lowerBound = startInclusive;
        }
        if (afterKey is not null
            && KvKeyComparer.Instance.Compare(afterKey, lowerBound) >= 0)
        {
            lowerBound = afterKey;
            lowerBoundExclusive = true;
        }

        int startIndex = LowerBound(entries, lowerBound);
        if (lowerBoundExclusive
            && startIndex < entries.Length
            && KvKeyComparer.Instance.Compare(entries[startIndex].Key, lowerBound) == 0)
        {
            startIndex++;
        }

        for (int i = startIndex; i < entries.Length; i++)
        {
            KeyValuePair<byte[], KvValueEntry> entry = entries[i];
            if (!entry.Key.AsSpan().StartsWith(prefix))
                yield break;
            if (endExclusive is not null
                && KvKeyComparer.Instance.Compare(entry.Key, endExclusive) >= 0)
            {
                yield break;
            }

            yield return entry;
        }
    }

    /// <summary>按 key 字节序降序枚举内存层的指定前缀与半开区间。</summary>
    private static IEnumerable<KeyValuePair<byte[], KvValueEntry>> EnumerateOverlayRangeDescending(
        KeyValuePair<byte[], KvValueEntry>[] entries,
        byte[] prefix,
        byte[]? afterKey,
        byte[]? startInclusive,
        byte[]? endExclusive)
    {
        byte[] lowerBound = prefix;
        if (startInclusive is not null
            && KvKeyComparer.Instance.Compare(startInclusive, lowerBound) > 0)
        {
            lowerBound = startInclusive;
        }

        byte[]? upperExclusive = endExclusive;
        byte[]? prefixUpper = GetPrefixUpperBound(prefix);
        if (prefixUpper is not null
            && (upperExclusive is null
                || KvKeyComparer.Instance.Compare(prefixUpper, upperExclusive) < 0))
        {
            upperExclusive = prefixUpper;
        }
        if (afterKey is not null
            && (upperExclusive is null
                || KvKeyComparer.Instance.Compare(afterKey, upperExclusive) < 0))
        {
            upperExclusive = afterKey;
        }

        int startIndex = upperExclusive is null
            ? entries.Length - 1
            : LowerBound(entries, upperExclusive) - 1;
        for (int i = startIndex; i >= 0; i--)
        {
            KeyValuePair<byte[], KvValueEntry> entry = entries[i];
            if (KvKeyComparer.Instance.Compare(entry.Key, lowerBound) < 0)
                yield break;
            if (!entry.Key.AsSpan().StartsWith(prefix))
                yield break;
            yield return entry;
        }
    }

    /// <summary>按扫描方向比较两个 key，使较早返回的 key 排在前面。</summary>
    private static int CompareInDirection(byte[] left, byte[] right, bool descending)
    {
        int comparison = KvKeyComparer.Instance.Compare(left, right);
        return descending ? -comparison : comparison;
    }

    /// <summary>计算覆盖指定前缀的最小排他上界；空前缀或全 0xFF 前缀没有有限上界。</summary>
    private static byte[]? GetPrefixUpperBound(ReadOnlySpan<byte> prefix)
    {
        if (prefix.IsEmpty)
            return null;

        byte[] upper = prefix.ToArray();
        for (int i = upper.Length - 1; i >= 0; i--)
        {
            if (upper[i] == byte.MaxValue)
                continue;
            upper[i]++;
            return upper[..(i + 1)];
        }
        return null;
    }

    private static int FindIndex(
        KeyValuePair<byte[], KvValueEntry>[] entries,
        byte[] key)
    {
        int lo = 0;
        int hi = entries.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            int comparison = KvKeyComparer.Instance.Compare(entries[mid].Key, key);
            if (comparison == 0)
                return mid;
            if (comparison < 0)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        return -1;
    }

    private static int LowerBound(
        KeyValuePair<byte[], KvValueEntry>[] entries,
        byte[] key)
    {
        int lo = 0;
        int hi = entries.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (KvKeyComparer.Instance.Compare(entries[mid].Key, key) < 0)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    public void Release()
    {
        KvDiskStateLease? diskLease = null;
        lock (_sync)
        {
            if (_referenceCount <= 0)
                throw new InvalidOperationException("KV read snapshot reference count is invalid.");

            _referenceCount--;
            if (_referenceCount != 0)
                return;

            _released = true;
            diskLease = _diskLease;
            _diskLease = null;
        }

        diskLease?.Dispose();
    }

    internal void AddReference()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_released, this);
            _referenceCount = checked(_referenceCount + 1);
        }
    }

    private static byte[]? ToOptionalArray(ReadOnlyMemory<byte> value)
        => value.IsEmpty ? null : value.ToArray();
}
