using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;

namespace SonnetDB.Kv;

internal static class KvStateFile
{
    private const int HeaderSize = 64;
    private const int EntryPrefixBytesV1 = 16;
    private const int EntryPrefixBytesV2 = 24;
    private const int EntryPrefixBytesV5 = 32;
    private const int KeyPrefixRestartInterval = 16;
    private const int CurrentVersion = 5;

    private static ReadOnlySpan<byte> SnapshotMagic => "SDBKVSNP"u8;
    private static ReadOnlySpan<byte> SegmentMagic => "SDBKVSEG"u8;

    public static void SaveSnapshot(
        string path,
        long sequence,
        IReadOnlyDictionary<byte[], KvValueEntry> values,
        long generation = 0)
        => SaveSnapshot(path, sequence, values.OrderBy(static x => x.Key, KvKeyComparer.Instance), values.Count, generation);

    public static void SaveSnapshot(
        string path,
        long sequence,
        IEnumerable<KeyValuePair<byte[], KvValueEntry>> orderedValues,
        int count,
        long generation = 0)
        => Save(path, SnapshotMagic, sequence, orderedValues, count, generation);

    public static void SaveSegment(
        string path,
        long sequence,
        IReadOnlyDictionary<byte[], KvValueEntry> values,
        long generation = 0)
        => SaveSegment(path, sequence, values.OrderBy(static x => x.Key, KvKeyComparer.Instance), values.Count, generation);

    public static void SaveSegment(
        string path,
        long sequence,
        IEnumerable<KeyValuePair<byte[], KvValueEntry>> orderedValues,
        int count,
        long generation = 0)
        => Save(path, SegmentMagic, sequence, orderedValues, count, generation);

    public static KvDiskState OpenDiskState(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = ReadHeader(fs);
        int entryPrefixBytes = GetEntryPrefixBytes(header.Version);
        byte[] prefixBuffer = new byte[entryPrefixBytes];
        byte[] crcBuffer = new byte[4];
        var entries = new List<KvDiskIndexEntry>(header.Count);
        byte[] previousKey = [];

        for (int i = 0; i < header.Count; i++)
        {
            Span<byte> prefix = prefixBuffer;
            long prefixOffset = fs.Position;
            if (ReadExact(fs, prefix) < entryPrefixBytes)
                throw new InvalidDataException("KV state entry prefix is truncated.");

            int keyLength = BinaryPrimitives.ReadInt32LittleEndian(prefix[..4]);
            int valueLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.Slice(4, 4));
            long entryVersion = BinaryPrimitives.ReadInt64LittleEndian(prefix.Slice(8, 8));
            long expiresAtUtcTicks = entryPrefixBytes >= EntryPrefixBytesV2
                ? BinaryPrimitives.ReadInt64LittleEndian(prefix.Slice(16, 8))
                : 0;
            int sharedKeyBytes = header.Version >= 5
                ? BinaryPrimitives.ReadInt32LittleEndian(prefix.Slice(24, 4))
                : 0;
            int storedKeyBytes = header.Version >= 5
                ? BinaryPrimitives.ReadInt32LittleEndian(prefix.Slice(28, 4))
                : keyLength;
            ValidateEntryHeader(
                keyLength,
                valueLength,
                expiresAtUtcTicks,
                sharedKeyBytes,
                storedKeyBytes,
                previousKey.Length,
                header.Version >= 5 && i % KeyPrefixRestartInterval == 0);

            long payloadOffset = fs.Position;
            byte[] key = new byte[keyLength];
            previousKey.AsSpan(0, sharedKeyBytes).CopyTo(key);
            if (ReadExact(fs, key.AsSpan(sharedKeyBytes, storedKeyBytes)) < storedKeyBytes)
                throw new InvalidDataException("KV state entry key is truncated.");

            fs.Position += valueLength;
            if (fs.Position > fs.Length)
                throw new InvalidDataException("KV state entry value is truncated.");

            if (ReadExact(fs, crcBuffer) < crcBuffer.Length)
                throw new InvalidDataException("KV state entry CRC is truncated.");

            uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBuffer);
            DateTimeOffset? expiresAtUtc = expiresAtUtcTicks > 0
                ? new DateTimeOffset(expiresAtUtcTicks, TimeSpan.Zero)
                : null;
            entries.Add(new KvDiskIndexEntry(
                key,
                valueLength,
                entryVersion,
                expiresAtUtc,
                prefixOffset,
                payloadOffset,
                expectedCrc,
                storedKeyBytes));
            previousKey = key;
        }

        if (fs.Position != fs.Length)
            throw new InvalidDataException("KV state file contains trailing data.");

        return new KvDiskState(path, header.Sequence, header.Generation, entries);
    }

    public static KvStateSnapshot Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = ReadHeader(fs);
        var values = new Dictionary<byte[], KvValueEntry>(header.Count, KvKeyComparer.Instance);
        int entryPrefixBytes = GetEntryPrefixBytes(header.Version);
        byte[] prefixBuffer = new byte[entryPrefixBytes];
        byte[] crcBuffer = new byte[4];
        byte[] previousKey = [];
        for (int i = 0; i < header.Count; i++)
        {
            Span<byte> prefix = prefixBuffer;
            if (ReadExact(fs, prefix) < entryPrefixBytes)
                throw new InvalidDataException("KV state entry prefix is truncated.");

            int keyLength = BinaryPrimitives.ReadInt32LittleEndian(prefix[..4]);
            int valueLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.Slice(4, 4));
            long entryVersion = BinaryPrimitives.ReadInt64LittleEndian(prefix.Slice(8, 8));
            long expiresAtUtcTicks = entryPrefixBytes >= EntryPrefixBytesV2
                ? BinaryPrimitives.ReadInt64LittleEndian(prefix.Slice(16, 8))
                : 0;
            int sharedKeyBytes = header.Version >= 5
                ? BinaryPrimitives.ReadInt32LittleEndian(prefix.Slice(24, 4))
                : 0;
            int storedKeyBytes = header.Version >= 5
                ? BinaryPrimitives.ReadInt32LittleEndian(prefix.Slice(28, 4))
                : keyLength;
            ValidateEntryHeader(
                keyLength,
                valueLength,
                expiresAtUtcTicks,
                sharedKeyBytes,
                storedKeyBytes,
                previousKey.Length,
                header.Version >= 5 && i % KeyPrefixRestartInterval == 0);

            byte[] key = new byte[keyLength];
            previousKey.AsSpan(0, sharedKeyBytes).CopyTo(key);
            if (ReadExact(fs, key.AsSpan(sharedKeyBytes, storedKeyBytes)) < storedKeyBytes)
                throw new InvalidDataException("KV state entry key is truncated.");
            byte[] value = new byte[valueLength];
            if (ReadExact(fs, value) < valueLength)
                throw new InvalidDataException("KV state entry value is truncated.");

            if (ReadExact(fs, crcBuffer) < crcBuffer.Length)
                throw new InvalidDataException("KV state entry CRC is truncated.");

            uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBuffer);
            uint actualCrc = ComputeEntryCrc(key, value);
            if (expectedCrc != actualCrc)
                throw new InvalidDataException("KV state entry CRC mismatch.");

            DateTimeOffset? expiresAtUtc = expiresAtUtcTicks > 0
                ? new DateTimeOffset(expiresAtUtcTicks, TimeSpan.Zero)
                : null;
            values[key] = new KvValueEntry(value, entryVersion, expiresAtUtc);
            previousKey = key;
        }

        if (fs.Position != fs.Length)
            throw new InvalidDataException("KV state file contains trailing data.");

        return new KvStateSnapshot(header.Sequence, header.Generation, values, diskState: null);
    }

    private static void Save(
        string path,
        ReadOnlySpan<byte> magic,
        long sequence,
        IEnumerable<KeyValuePair<byte[], KvValueEntry>> orderedValues,
        int count,
        long generation)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(orderedValues);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string tempPath = path + ".tmp";

        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            magic.CopyTo(header[..8]);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), CurrentVersion);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12, 4), HeaderSize);
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(16, 8), DateTime.UtcNow.Ticks);
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(24, 8), sequence);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(32, 4), count);
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(36, 8), generation);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(60, 4), Crc32.HashToUInt32(header[..60]));
            fs.Write(header);

            byte[] prefixBuffer = new byte[EntryPrefixBytesV5];
            byte[] crcBuffer = new byte[4];
            byte[] previousKey = [];
            int written = 0;
            foreach (var pair in orderedValues)
            {
                if (pair.Value.IsDeleted)
                    continue;
                int sharedKeyBytes = written % KeyPrefixRestartInterval == 0
                    ? 0
                    : CommonPrefixLength(previousKey, pair.Key);
                int storedKeyBytes = pair.Key.Length - sharedKeyBytes;
                Span<byte> prefix = prefixBuffer;
                prefix.Clear();
                BinaryPrimitives.WriteInt32LittleEndian(prefix[..4], pair.Key.Length);
                BinaryPrimitives.WriteInt32LittleEndian(prefix.Slice(4, 4), pair.Value.Value.Length);
                BinaryPrimitives.WriteInt64LittleEndian(prefix.Slice(8, 8), pair.Value.Version);
                BinaryPrimitives.WriteInt64LittleEndian(prefix.Slice(16, 8), pair.Value.ExpiresAtUtc?.UtcTicks ?? 0);
                BinaryPrimitives.WriteInt32LittleEndian(prefix.Slice(24, 4), sharedKeyBytes);
                BinaryPrimitives.WriteInt32LittleEndian(prefix.Slice(28, 4), storedKeyBytes);
                fs.Write(prefix);
                fs.Write(pair.Key.AsSpan(sharedKeyBytes));
                fs.Write(pair.Value.Value);

                uint crc = ComputeEntryCrc(pair.Key, pair.Value.Value);
                BinaryPrimitives.WriteUInt32LittleEndian(crcBuffer, crc);
                fs.Write(crcBuffer);
                previousKey = pair.Key;
                written++;
            }

            if (written != count)
                throw new InvalidDataException("KV state entry count changed while saving.");

            fs.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private static KvStateHeader ReadHeader(FileStream fs)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        if (ReadExact(fs, header) < HeaderSize)
            throw new InvalidDataException("KV state header is truncated.");

        bool isSnapshot = header[..8].SequenceEqual(SnapshotMagic);
        bool isSegment = header[..8].SequenceEqual(SegmentMagic);
        int version = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4));
        uint expectedHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(60, 4));
        uint actualHeaderCrc = Crc32.HashToUInt32(header[..60]);
        if ((!isSnapshot && !isSegment) ||
            version is < 1 or > CurrentVersion ||
            BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4)) != HeaderSize ||
            expectedHeaderCrc != actualHeaderCrc)
        {
            throw new InvalidDataException("KV state header is invalid.");
        }

        long sequence = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(24, 8));
        int count = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(32, 4));
        long generation = version >= 4
            ? BinaryPrimitives.ReadInt64LittleEndian(header.Slice(36, 8))
            : 0;
        if (sequence < 0 || count < 0 || generation < 0)
            throw new InvalidDataException("KV state header contains invalid counters.");

        return new KvStateHeader(version, sequence, count, generation);
    }

    private static int GetEntryPrefixBytes(int version)
        => version switch
        {
            1 => EntryPrefixBytesV1,
            >= 2 and <= 4 => EntryPrefixBytesV2,
            5 => EntryPrefixBytesV5,
            _ => throw new InvalidDataException("KV state entry version is unsupported."),
        };

    private static void ValidateEntryHeader(
        int keyLength,
        int valueLength,
        long expiresAtUtcTicks,
        int sharedKeyBytes,
        int storedKeyBytes,
        int previousKeyLength,
        bool requiresRestart)
    {
        if (keyLength <= 0 || valueLength < 0)
            throw new InvalidDataException("KV state entry length is invalid.");
        if (expiresAtUtcTicks < 0)
            throw new InvalidDataException("KV state entry expires-at is invalid.");
        if (sharedKeyBytes < 0
            || storedKeyBytes < 0
            || sharedKeyBytes > previousKeyLength
            || sharedKeyBytes + storedKeyBytes != keyLength
            || requiresRestart && sharedKeyBytes != 0)
        {
            throw new InvalidDataException("KV state entry key prefix encoding is invalid.");
        }
    }

    private static int CommonPrefixLength(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int maximum = Math.Min(left.Length, right.Length);
        int length = 0;
        while (length < maximum && left[length] == right[length])
            length++;
        return length;
    }

    private static uint ComputeEntryCrc(byte[] key, byte[] value)
    {
        var crc = new Crc32();
        crc.Append(key);
        crc.Append(value);
        return crc.GetCurrentHashAsUInt32();
    }

    private static int ReadExact(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }

    private readonly record struct KvStateHeader(int Version, long Sequence, int Count, long Generation);
}

internal sealed class KvStateSnapshot
{
    public KvStateSnapshot(
        long sequence,
        long generation,
        Dictionary<byte[], KvValueEntry> values,
        KvDiskState? diskState)
    {
        Sequence = sequence;
        Generation = generation;
        Values = values;
        DiskState = diskState;
    }

    public long Sequence { get; }

    public long Generation { get; set; }

    public Dictionary<byte[], KvValueEntry> Values { get; }

    public KvDiskState? DiskState { get; set; }
}

internal sealed class KvDiskState : IDisposable
{
    private readonly object _sync = new();
    private readonly KvDiskIndexEntry[] _entries;
    private readonly FileStream _stream;
    private int _referenceCount = 1;
    private bool _ownerReleased;
    private bool _disposed;

    /// <summary>测试扫描时记录实际检查的磁盘索引位置。</summary>
    internal Action<int>? ScanIndexVisitedTestHook { get; set; }

    /// <summary>测试范围扫描底层枚举器实际开始执行的次数。</summary>
    internal Action? ScanStartedTestHook { get; set; }

    public KvDiskState(string path, long sequence, long generation, IReadOnlyList<KvDiskIndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(entries);
        Path = path;
        Sequence = sequence;
        Generation = generation;
        _entries = entries.ToArray();
        _stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
    }

    public string Path { get; }

    public long Sequence { get; }

    public long Generation { get; }

    public int Count => _entries.Length;

    /// <summary>取得共享当前不可变磁盘状态的读租约。</summary>
    public KvDiskStateLease AcquireLease()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_ownerReleased || _disposed, this);
            _referenceCount = checked(_referenceCount + 1);
            return new KvDiskStateLease(this);
        }
    }

    public bool Contains(ReadOnlySpan<byte> key) => FindIndex(key) >= 0;

    public KvValueEntry? Get(ReadOnlySpan<byte> key)
    {
        int index = FindIndex(key);
        if (index < 0)
            return null;

        return Read(_entries[index]);
    }

    /// <summary>
    /// 兼容旧前缀分页扫描，并严格从指定 key 之后继续读取。
    /// </summary>
    public IEnumerable<KvDiskIndexEntry> ScanPrefixAfter(byte[] prefix, byte[]? afterKey)
        => ScanRange(prefix, startInclusive: null, endExclusive: null, afterKey);

    /// <summary>
    /// 按前缀和半开区间扫描磁盘索引，并严格排除 continuation key。
    /// </summary>
    /// <param name="prefix">必须匹配的 key 前缀。</param>
    /// <param name="startInclusive">包含的起始 key；null 表示无显式下界。</param>
    /// <param name="endExclusive">不包含的结束 key；null 表示无显式上界。</param>
    /// <param name="afterKey">上一页最后一个 key；null 表示无 continuation。</param>
    /// <returns>按 key 字节序升序排列的磁盘索引项。</returns>
    public IEnumerable<KvDiskIndexEntry> ScanRange(
        byte[] prefix,
        byte[]? startInclusive,
        byte[]? endExclusive,
        byte[]? afterKey,
        bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ScanStartedTestHook?.Invoke();

        if (descending)
        {
            foreach (var entry in ScanRangeDescending(prefix, startInclusive, endExclusive, afterKey))
                yield return entry;
            yield break;
        }

        byte[] lowerBound = prefix;
        bool lowerBoundExclusive = false;
        if (startInclusive is not null && Compare(startInclusive, lowerBound) > 0)
            lowerBound = startInclusive;
        if (afterKey is not null && Compare(afterKey, lowerBound) >= 0)
        {
            lowerBound = afterKey;
            lowerBoundExclusive = true;
        }

        int startIndex = LowerBound(lowerBound);
        if (lowerBoundExclusive
            && startIndex < _entries.Length
            && Compare(_entries[startIndex].Key, lowerBound) == 0)
        {
            startIndex++;
        }

        for (int i = startIndex; i < _entries.Length; i++)
        {
            var entry = _entries[i];
            ScanIndexVisitedTestHook?.Invoke(i);
            if (!entry.Key.AsSpan().StartsWith(prefix))
                yield break;
            if (endExclusive is not null && Compare(entry.Key, endExclusive) >= 0)
                yield break;

            yield return entry;
        }
    }

    /// <summary>按前缀和半开区间从上界向下扫描磁盘索引。</summary>
    private IEnumerable<KvDiskIndexEntry> ScanRangeDescending(
        byte[] prefix,
        byte[]? startInclusive,
        byte[]? endExclusive,
        byte[]? afterKey)
    {
        byte[] lowerBound = prefix;
        if (startInclusive is not null && Compare(startInclusive, lowerBound) > 0)
            lowerBound = startInclusive;

        byte[]? upperExclusive = endExclusive;
        byte[]? prefixUpper = GetPrefixUpperBound(prefix);
        if (prefixUpper is not null
            && (upperExclusive is null || Compare(prefixUpper, upperExclusive) < 0))
        {
            upperExclusive = prefixUpper;
        }
        if (afterKey is not null
            && (upperExclusive is null || Compare(afterKey, upperExclusive) < 0))
        {
            upperExclusive = afterKey;
        }

        int startIndex = upperExclusive is null
            ? _entries.Length - 1
            : LowerBound(upperExclusive) - 1;
        for (int i = startIndex; i >= 0; i--)
        {
            KvDiskIndexEntry entry = _entries[i];
            ScanIndexVisitedTestHook?.Invoke(i);
            if (Compare(entry.Key, lowerBound) < 0)
                yield break;
            if (!entry.Key.AsSpan().StartsWith(prefix))
                yield break;
            yield return entry;
        }
    }

    /// <summary>计算指定前缀的最小排他上界。</summary>
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

    public KvValueEntry Read(KvDiskIndexEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] value = new byte[entry.ValueLength];

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stream.Position = entry.ValueOffset;
            if (ReadExact(_stream, value) < entry.ValueLength)
                throw new InvalidDataException("KV state entry value is truncated.");
        }

        var crc = new Crc32();
        crc.Append(entry.Key);
        crc.Append(value);
        uint actualCrc = crc.GetCurrentHashAsUInt32();
        if (actualCrc != entry.PayloadCrc)
            throw new InvalidDataException("KV state entry CRC mismatch.");

        return new KvValueEntry(value, entry.Version, entry.ExpiresAtUtc);
    }

    public void ValidateAllEntries()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var crc = new Crc32();
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                foreach (KvDiskIndexEntry entry in _entries)
                {
                    crc.Reset();
                    crc.Append(entry.Key);
                    _stream.Position = entry.ValueOffset;
                    int remaining = entry.ValueLength;
                    while (remaining > 0)
                    {
                        int read = _stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                        if (read == 0)
                            throw new InvalidDataException("KV state entry value is truncated.");
                        crc.Append(buffer.AsSpan(0, read));
                        remaining -= read;
                    }

                    if (crc.GetCurrentHashAsUInt32() != entry.PayloadCrc)
                        throw new InvalidDataException("KV state entry CRC mismatch.");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_ownerReleased)
                return;

            _ownerReleased = true;
            ReleaseReferenceLocked();
        }
    }

    internal void ReleaseLease()
    {
        lock (_sync)
            ReleaseReferenceLocked();
    }

    private void ReleaseReferenceLocked()
    {
        if (_referenceCount <= 0)
            throw new InvalidOperationException("KV disk state reference count is invalid.");

        _referenceCount--;
        if (_referenceCount != 0)
            return;

        _disposed = true;
        _stream.Dispose();
    }

    private int FindIndex(ReadOnlySpan<byte> key)
    {
        int lo = 0;
        int hi = _entries.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            int comparison = Compare(_entries[mid].Key, key);
            if (comparison == 0)
                return mid;
            if (comparison < 0)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        return -1;
    }

    /// <summary>
    /// 使用二分查找返回第一个大于等于目标 key 的磁盘索引位置。
    /// </summary>
    private int LowerBound(ReadOnlySpan<byte> key)
    {
        int lo = 0;
        int hi = _entries.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (Compare(_entries[mid].Key, key) < 0)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private static int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int min = Math.Min(left.Length, right.Length);
        for (int i = 0; i < min; i++)
        {
            int comparison = left[i].CompareTo(right[i]);
            if (comparison != 0)
                return comparison;
        }

        return left.Length.CompareTo(right.Length);
    }

    private static int ReadExact(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }
}

internal sealed class KvDiskStateLease : IDisposable
{
    private KvDiskState? _owner;

    public KvDiskStateLease(KvDiskState state)
    {
        State = state;
        _owner = state;
    }

    public KvDiskState State { get; }

    public void Dispose()
        => Interlocked.Exchange(ref _owner, null)?.ReleaseLease();
}

internal sealed class KvDiskIndexEntry
{
    public KvDiskIndexEntry(
        byte[] key,
        int valueLength,
        long version,
        DateTimeOffset? expiresAtUtc,
        long prefixOffset,
        long payloadOffset,
        uint payloadCrc,
        int? storedKeyLength = null)
    {
        Key = key;
        ValueLength = valueLength;
        Version = version;
        ExpiresAtUtc = expiresAtUtc;
        PrefixOffset = prefixOffset;
        PayloadOffset = payloadOffset;
        PayloadCrc = payloadCrc;
        StoredKeyLength = storedKeyLength ?? key.Length;
    }

    public byte[] Key { get; }

    public int ValueLength { get; }

    public long Version { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    public long PrefixOffset { get; }

    public long PayloadOffset { get; }

    public int StoredKeyLength { get; }

    public long ValueOffset => PayloadOffset + StoredKeyLength;

    public uint PayloadCrc { get; }

    public KvValueEntry ToValueEntry() => new([], Version, ExpiresAtUtc);
}
