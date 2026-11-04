using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using Microsoft.Win32.SafeHandles;
using SonnetDB.Diagnostics;

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
        IEnumerable<KeyValuePair<byte[], KvValueEntry>> orderedValues,
        int count,
        long generation = 0)
        => Save(path, SnapshotMagic, sequence, orderedValues, count, generation);

    public static void SaveSegment(
        string path,
        long sequence,
        IEnumerable<KeyValuePair<byte[], KvValueEntry>> orderedValues,
        int count,
        long generation = 0)
        => Save(path, SegmentMagic, sequence, orderedValues, count, generation);

    public static KvDiskState OpenDiskState(string path, KvDiskReadBudget? readBudget = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = ReadHeader(fs);
        // 冷打开顺序构造稀疏目录，复用邻近元数据；逐条设置 FileStream.Position 会反复丢弃读取缓冲。
        // 用显式逻辑偏移跳过值，64 KiB 仅为本次打开的固定缓冲，不改变后续点读的 4 KiB 合同。
        using var indexReader = new KvIndexPageReader(fs.SafeFileHandle, CancellationToken.None, 64 * 1024);
        long position = HeaderSize;
        long fileLength = fs.Length;
        int entryPrefixBytes = GetEntryPrefixBytes(header.Version);
        byte[] prefixBuffer = new byte[entryPrefixBytes];
        byte[] crcBuffer = new byte[4];
        bool paged = header.Count > KvPagedDiskIndex.EagerIndexThreshold;
        var entries = paged ? null : new List<KvDiskIndexEntry>(header.Count);
        var fences = paged ? new KvDiskIndexFence[(header.Count - 1) / KvPagedDiskIndex.PageSize + 1] : [];
        byte[] keyBuffer = ArrayPool<byte>.Shared.Rent(256);
        int previousKeyLength = 0;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            for (int i = 0; i < header.Count; i++)
            {
                // 打开时仅保存稀疏边界并复用键缓冲；大表不再为每个键创建常驻对象。
                if ((i & 1023) == 0 && System.Diagnostics.Stopwatch.GetElapsedTime(started) > TimeSpan.FromMinutes(15))
                    throw new TimeoutException("KV state index opening exceeded its time budget.");
                Span<byte> prefix = prefixBuffer;
                long prefixOffset = position;
                if (indexReader.Read(prefix, position) < entryPrefixBytes)
                    throw new InvalidDataException("KV state entry prefix is truncated.");
                position = checked(position + entryPrefixBytes);

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
                    previousKeyLength,
                    header.Version >= 5 && i % KeyPrefixRestartInterval == 0);

                long payloadOffset = position;
                if (keyLength > keyBuffer.Length)
                {
                    byte[] replacement = ArrayPool<byte>.Shared.Rent(keyLength);
                    keyBuffer.AsSpan(0, sharedKeyBytes).CopyTo(replacement);
                    ArrayPool<byte>.Shared.Return(keyBuffer);
                    keyBuffer = replacement;
                }
                if (indexReader.Read(keyBuffer.AsSpan(sharedKeyBytes, storedKeyBytes), position) < storedKeyBytes)
                    throw new InvalidDataException("KV state entry key is truncated.");

                position = checked(position + storedKeyBytes + valueLength);
                if (position > fileLength)
                    throw new InvalidDataException("KV state entry value is truncated.");

                if (indexReader.Read(crcBuffer, position) < crcBuffer.Length)
                    throw new InvalidDataException("KV state entry CRC is truncated.");
                position = checked(position + crcBuffer.Length);

                uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBuffer);
                DateTimeOffset? expiresAtUtc = expiresAtUtcTicks > 0
                    ? new DateTimeOffset(expiresAtUtcTicks, TimeSpan.Zero)
                    : null;
                if (paged)
                {
                    if (i % KvPagedDiskIndex.PageSize == 0)
                        fences[i / KvPagedDiskIndex.PageSize] = new KvDiskIndexFence(keyBuffer.AsSpan(0, keyLength).ToArray(), prefixOffset);
                }
                else entries!.Add(new KvDiskIndexEntry(
                    keyBuffer.AsSpan(0, keyLength).ToArray(),
                    valueLength,
                    entryVersion,
                    expiresAtUtc,
                    prefixOffset,
                    payloadOffset,
                    expectedCrc,
                    storedKeyBytes));
                previousKeyLength = keyLength;
            }

            if (position != fileLength)
                throw new InvalidDataException("KV state file contains trailing data.");

            return paged
                ? new KvDiskState(path, header.Sequence, header.Generation, header.Version, header.Count, fences, readBudget)
                : new KvDiskState(path, header.Sequence, header.Generation, entries!, readBudget);
        }
        finally { ArrayPool<byte>.Shared.Return(keyBuffer); }
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

    internal static int GetEntryPrefixBytes(int version)
        => version switch
        {
            1 => EntryPrefixBytesV1,
            >= 2 and <= 4 => EntryPrefixBytesV2,
            5 => EntryPrefixBytesV5,
            _ => throw new InvalidDataException("KV state entry version is unsupported."),
        };

    internal static void ValidateEntryHeader(
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
    private readonly IReadOnlyList<KvDiskIndexEntry> _entries;
    private readonly FileStream _stream;
    private readonly SafeFileHandle _readHandle;
    private readonly KvDiskReadBudget _readBudget;
    private readonly bool _ownsReadBudget;
    private int _referenceCount = 1;
    private bool _ownerReleased;
    private bool _disposed;

    /// <summary>测试扫描时记录实际检查的磁盘索引位置。</summary>
    internal Action<int>? ScanIndexVisitedTestHook { get; set; }

    /// <summary>测试范围扫描底层枚举器实际开始执行的次数。</summary>
    internal Action? ScanStartedTestHook { get; set; }

    /// <summary>测试并发按位置读取进入实际 I/O 前的时机。</summary>
    internal Action? ReadStartedTestHook { get; set; }

    public KvDiskState(
        string path,
        long sequence,
        long generation,
        IReadOnlyList<KvDiskIndexEntry> entries,
        KvDiskReadBudget? readBudget = null)
        : this(path, sequence, generation, readBudget)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries.ToArray();
    }

    /// <summary>大状态文件只常驻稀疏目录；页面按查询需要从原始不可变文件读取。</summary>
    internal KvDiskState(string path, long sequence, long generation, int version, int count,
        KvDiskIndexFence[] fences, KvDiskReadBudget? readBudget = null)
        : this(path, sequence, generation, readBudget)
    {
        _entries = new KvPagedDiskIndex(count, fences,
            (offset, first, take, token) => ReadIndexPage(version, offset, first, take, token));
    }

    /// <summary>为值读取和索引页读取共享同一文件句柄、读预算和生命周期。</summary>
    private KvDiskState(string path, long sequence, long generation, KvDiskReadBudget? readBudget)
    {
        ArgumentNullException.ThrowIfNull(path);
        Path = path;
        Sequence = sequence;
        Generation = generation;
        _entries = [];
        _ownsReadBudget = readBudget is null;
        _readBudget = readBudget ?? new KvDiskReadBudget(KvDiskReadBudget.DefaultMaxConcurrentReads);
        _readBudget.AddStateReference();
        try
        {
            _stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            // 构造时只暴露一次句柄，避免每次点读触发 FileStream 的 Flush/Seek 兼容逻辑。
            _readHandle = _stream.SafeFileHandle;
        }
        catch
        {
            _readBudget.ReleaseStateReference();
            if (_ownsReadBudget)
                _readBudget.Dispose();
            throw;
        }
    }

    public string Path { get; }

    public long Sequence { get; }

    public long Generation { get; }

    public int Count => _entries.Count;

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

    public bool Contains(ReadOnlySpan<byte> key)
    {
        AddReadReference();
        try { return FindIndex(key) >= 0; }
        finally { ReleaseLease(); }
    }

    public KvValueEntry? Get(ReadOnlySpan<byte> key)
        => Get(key, CancellationToken.None);

    public KvValueEntry? Get(ReadOnlySpan<byte> key, CancellationToken cancellationToken)
    {
        AddReadReference();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = FindIndex(key, cancellationToken);
            if (index < 0)
                return null;

            return Read(_entries[index], cancellationToken);
        }
        finally { ReleaseLease(); }
    }

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
            && startIndex < _entries.Count
            && Compare(_entries[startIndex].Key, lowerBound) == 0)
        {
            startIndex++;
        }

        for (int i = startIndex; i < _entries.Count; i++)
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
            ? _entries.Count - 1
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

    /// <summary>按需要解码一页索引；值正文仍留在文件中，且与普通读取共享取消和物理 I/O 调度。</summary>
    private KvDiskIndexEntry[] ReadIndexPage(int version, long offset, int firstIndex, int count,
        CancellationToken cancellationToken)
    {
        AddReadReference();
        try
        {
            using KvDiskReadBudget.ReadLease lease = _readBudget.Acquire(cancellationToken, maintenanceRead: false);
            int prefixBytes = KvStateFile.GetEntryPrefixBytes(version);
            byte[] prefix = new byte[prefixBytes];
            byte[] crc = new byte[4];
            byte[] previous = [];
            var entries = new KvDiskIndexEntry[count];
            using var reader = new KvIndexPageReader(_readHandle, cancellationToken);
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(30))
                    throw new TimeoutException("KV index page read exceeded its time budget.");
                long prefixOffset = offset;
                if (reader.Read(prefix, offset) != prefixBytes)
                    throw new InvalidDataException("KV state entry prefix is truncated.");
                int keyLength = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                int valueLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4));
                long entryVersion = BinaryPrimitives.ReadInt64LittleEndian(prefix.AsSpan(8));
                long expiry = prefixBytes >= 24 ? BinaryPrimitives.ReadInt64LittleEndian(prefix.AsSpan(16)) : 0;
                int shared = version >= 5 ? BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(24)) : 0;
                int stored = version >= 5 ? BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(28)) : keyLength;
                KvStateFile.ValidateEntryHeader(keyLength, valueLength, expiry, shared, stored, previous.Length,
                    version >= 5 && (firstIndex + i) % 16 == 0);
                long payloadOffset = checked(offset + prefixBytes);
                byte[] key = new byte[keyLength];
                previous.AsSpan(0, shared).CopyTo(key);
                if (reader.Read(key.AsSpan(shared, stored), payloadOffset) != stored)
                    throw new InvalidDataException("KV state entry key is truncated.");
                long crcOffset = checked(payloadOffset + stored + valueLength);
                if (reader.Read(crc, crcOffset) != 4)
                    throw new InvalidDataException("KV state entry CRC is truncated.");
                entries[i] = new KvDiskIndexEntry(key, valueLength, entryVersion,
                    expiry > 0 ? new DateTimeOffset(expiry, TimeSpan.Zero) : null,
                    prefixOffset, payloadOffset, BinaryPrimitives.ReadUInt32LittleEndian(crc), stored);
                previous = key;
                offset = checked(crcOffset + 4);
            }
            lease.RecordRead(checked((int)reader.BytesRead));
            return entries;
        }
        finally { ReleaseLease(); }
    }

    public KvValueEntry Read(KvDiskIndexEntry entry)
        => Read(entry, CancellationToken.None);

    /// <summary>按位置读取并校验 payload；维护读取使用独立预算。</summary>
    public KvValueEntry Read(KvDiskIndexEntry entry, CancellationToken cancellationToken, bool maintenanceRead = false)
    {
        AddReadReference();
        try
        {
            byte[] value;
            int bytesRead;
            using (KvDiskReadBudget.ReadLease readLease = _readBudget.Acquire(cancellationToken, maintenanceRead))
            {
                value = new byte[entry.ValueLength];
                ReadStartedTestHook?.Invoke();
                long readStarted = SonnetDbMeter.StartKvStateReadTiming();
                bytesRead = ReadExactAt(value, entry.ValueOffset);
                readLease.RecordRead(bytesRead);
                SonnetDbMeter.RecordKvStateRead(readStarted, bytesRead);
            }
            // CRC 计算保留 state 生命周期，但不占用稀缺物理读许可。
            if (bytesRead < entry.ValueLength)
                throw new InvalidDataException("KV state entry value is truncated.");

            var crc = new Crc32();
            crc.Append(entry.Key);
            crc.Append(value);
            uint actualCrc = crc.GetCurrentHashAsUInt32();
            if (actualCrc != entry.PayloadCrc)
                throw new InvalidDataException("KV state entry CRC mismatch.");

            return new KvValueEntry(value, entry.Version, entry.ExpiresAtUtc);
        }
        finally
        {
            ReleaseLease();
        }
    }

    /// <summary>为一次锁外按位置读取增加临时引用，避免 checkpoint 替换期间关闭共享句柄。</summary>
    private void AddReadReference()
    {
        lock (_sync)
        {
            // owner 释放后，既有快照 lease 仍必须可读；只有引用归零并真正关闭后才拒绝。
            ObjectDisposedException.ThrowIf(_disposed, this);
            _referenceCount = checked(_referenceCount + 1);
        }
    }

    /// <summary>从明确文件偏移读取完整 payload；不同线程不共享或修改 FileStream.Position。</summary>
    private int ReadExactAt(Span<byte> destination, long fileOffset)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = RandomAccess.Read(
                _readHandle,
                destination[total..],
                checked(fileOffset + total));
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }

    /// <summary>通过独立文件偏移校验全部正文，不在状态锁内等待索引页或物理读取许可。</summary>
    public void ValidateAllEntries()
    {
        AddReadReference();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var crc = new Crc32();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (KvDiskIndexEntry entry in _entries)
            {
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > TimeSpan.FromMinutes(15))
                    throw new TimeoutException("KV state validation exceeded its time budget.");
                crc.Reset();
                crc.Append(entry.Key);
                long offset = entry.ValueOffset;
                int remaining = entry.ValueLength;
                // 新状态文件校验沿用独立顺序读取，避免重复占用生成 checkpoint 的维护许可。
                {
                    for (int block = 0; remaining > 0 && block <= entry.ValueLength / buffer.Length; block++)
                    {
                        int take = Math.Min(buffer.Length, remaining);
                        int read = ReadExactAt(buffer.AsSpan(0, take), offset);
                        if (read != take) throw new InvalidDataException("KV state entry value is truncated.");
                        crc.Append(buffer.AsSpan(0, read));
                        offset += read;
                        remaining -= read;
                    }
                }
                if (remaining != 0 || crc.GetCurrentHashAsUInt32() != entry.PayloadCrc)
                    throw new InvalidDataException("KV state entry CRC mismatch.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ReleaseLease();
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
        try
        {
            _stream.Dispose();
        }
        finally
        {
            _readBudget.ReleaseStateReference();
            if (_ownsReadBudget)
                _readBudget.Dispose();
        }
    }

    private int FindIndex(ReadOnlySpan<byte> key, CancellationToken cancellationToken = default)
    {
        if (_entries is KvPagedDiskIndex paged)
        {
            int index = paged.LowerBound(key, cancellationToken);
            return index < Count && Compare(_entries[index].Key, key) == 0 ? index : -1;
        }
        int lo = 0;
        int hi = _entries.Count - 1;
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
        if (_entries is KvPagedDiskIndex paged) return paged.LowerBound(key);
        int lo = 0;
        int hi = _entries.Count;
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
