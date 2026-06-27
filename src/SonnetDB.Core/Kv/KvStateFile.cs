using System.Buffers.Binary;
using System.IO.Hashing;

namespace SonnetDB.Kv;

internal static class KvStateFile
{
    private const int HeaderSize = 64;
    private const int EntryPrefixBytesV1 = 16;
    private const int EntryPrefixBytesV2 = 24;
    private const int CurrentVersion = 3;

    private static ReadOnlySpan<byte> SnapshotMagic => "SDBKVSNP"u8;
    private static ReadOnlySpan<byte> SegmentMagic => "SDBKVSEG"u8;

    public static void SaveSnapshot(
        string path,
        long sequence,
        IReadOnlyDictionary<byte[], KvValueEntry> values)
        => SaveSnapshot(path, sequence, values.OrderBy(static x => x.Key, KvKeyComparer.Instance), values.Count);

    public static void SaveSnapshot(
        string path,
        long sequence,
        IEnumerable<KeyValuePair<byte[], KvValueEntry>> orderedValues,
        int count)
        => Save(path, SnapshotMagic, sequence, orderedValues, count);

    public static void SaveSegment(
        string path,
        long sequence,
        IReadOnlyDictionary<byte[], KvValueEntry> values)
        => SaveSegment(path, sequence, values.OrderBy(static x => x.Key, KvKeyComparer.Instance), values.Count);

    public static void SaveSegment(
        string path,
        long sequence,
        IEnumerable<KeyValuePair<byte[], KvValueEntry>> orderedValues,
        int count)
        => Save(path, SegmentMagic, sequence, orderedValues, count);

    public static KvDiskState OpenDiskState(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = ReadHeader(fs);
        int entryPrefixBytes = header.Version >= 2 ? EntryPrefixBytesV2 : EntryPrefixBytesV1;
        byte[] prefixBuffer = new byte[entryPrefixBytes];
        byte[] crcBuffer = new byte[4];
        var entries = new List<KvDiskIndexEntry>(header.Count);

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
            ValidateEntryHeader(keyLength, valueLength, expiresAtUtcTicks);

            long payloadOffset = fs.Position;
            byte[] key = new byte[keyLength];
            if (ReadExact(fs, key) < keyLength)
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
                expectedCrc));
        }

        return new KvDiskState(path, header.Sequence, entries);
    }

    public static KvStateSnapshot Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = ReadHeader(fs);
        var values = new Dictionary<byte[], KvValueEntry>(header.Count, KvKeyComparer.Instance);
        int entryPrefixBytes = header.Version >= 2 ? EntryPrefixBytesV2 : EntryPrefixBytesV1;
        byte[] prefixBuffer = new byte[entryPrefixBytes];
        byte[] crcBuffer = new byte[4];
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
            ValidateEntryHeader(keyLength, valueLength, expiresAtUtcTicks);

            byte[] payload = new byte[keyLength + valueLength];
            if (ReadExact(fs, payload) < payload.Length)
                throw new InvalidDataException("KV state entry payload is truncated.");

            if (ReadExact(fs, crcBuffer) < crcBuffer.Length)
                throw new InvalidDataException("KV state entry CRC is truncated.");

            uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBuffer);
            uint actualCrc = Crc32.HashToUInt32(payload);
            if (expectedCrc != actualCrc)
                throw new InvalidDataException("KV state entry CRC mismatch.");

            byte[] key = payload.AsSpan(0, keyLength).ToArray();
            byte[] value = payload.AsSpan(keyLength, valueLength).ToArray();
            DateTimeOffset? expiresAtUtc = expiresAtUtcTicks > 0
                ? new DateTimeOffset(expiresAtUtcTicks, TimeSpan.Zero)
                : null;
            values[key] = new KvValueEntry(value, entryVersion, expiresAtUtc);
        }

        return new KvStateSnapshot(header.Sequence, values, diskState: null);
    }

    private static void Save(
        string path,
        ReadOnlySpan<byte> magic,
        long sequence,
        IEnumerable<KeyValuePair<byte[], KvValueEntry>> orderedValues,
        int count)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(orderedValues);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

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
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(60, 4), Crc32.HashToUInt32(header[..60]));
            fs.Write(header);

            byte[] prefixBuffer = new byte[EntryPrefixBytesV2];
            byte[] crcBuffer = new byte[4];
            int written = 0;
            foreach (var pair in orderedValues)
            {
                if (pair.Value.IsDeleted)
                    continue;

                Span<byte> prefix = prefixBuffer;
                BinaryPrimitives.WriteInt32LittleEndian(prefix[..4], pair.Key.Length);
                BinaryPrimitives.WriteInt32LittleEndian(prefix.Slice(4, 4), pair.Value.Value.Length);
                BinaryPrimitives.WriteInt64LittleEndian(prefix.Slice(8, 8), pair.Value.Version);
                BinaryPrimitives.WriteInt64LittleEndian(prefix.Slice(16, 8), pair.Value.ExpiresAtUtc?.UtcTicks ?? 0);
                fs.Write(prefix);
                fs.Write(pair.Key);
                fs.Write(pair.Value.Value);

                uint crc = ComputeEntryCrc(pair.Key, pair.Value.Value);
                BinaryPrimitives.WriteUInt32LittleEndian(crcBuffer, crc);
                fs.Write(crcBuffer);
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
        if (sequence < 0 || count < 0)
            throw new InvalidDataException("KV state header contains invalid counters.");

        return new KvStateHeader(version, sequence, count);
    }

    private static void ValidateEntryHeader(int keyLength, int valueLength, long expiresAtUtcTicks)
    {
        if (keyLength <= 0 || valueLength < 0)
            throw new InvalidDataException("KV state entry length is invalid.");
        if (expiresAtUtcTicks < 0)
            throw new InvalidDataException("KV state entry expires-at is invalid.");
    }

    private static uint ComputeEntryCrc(byte[] key, byte[] value)
    {
        byte[] payload = new byte[key.Length + value.Length];
        key.CopyTo(payload.AsSpan(0, key.Length));
        value.CopyTo(payload.AsSpan(key.Length, value.Length));
        return Crc32.HashToUInt32(payload);
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

    private readonly record struct KvStateHeader(int Version, long Sequence, int Count);
}

internal sealed class KvStateSnapshot
{
    public KvStateSnapshot(long sequence, Dictionary<byte[], KvValueEntry> values, KvDiskState? diskState)
    {
        Sequence = sequence;
        Values = values;
        DiskState = diskState;
    }

    public long Sequence { get; }

    public Dictionary<byte[], KvValueEntry> Values { get; }

    public KvDiskState? DiskState { get; }
}

internal sealed class KvDiskState : IDisposable
{
    private readonly object _sync = new();
    private readonly KvDiskIndexEntry[] _entries;
    private readonly FileStream _stream;
    private bool _disposed;

    public KvDiskState(string path, long sequence, IReadOnlyList<KvDiskIndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(entries);
        Path = path;
        Sequence = sequence;
        _entries = entries.ToArray();
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public string Path { get; }

    public long Sequence { get; }

    public int Count => _entries.Length;

    public bool Contains(ReadOnlySpan<byte> key) => FindIndex(key) >= 0;

    public KvValueEntry? Get(ReadOnlySpan<byte> key)
    {
        int index = FindIndex(key);
        if (index < 0)
            return null;

        return Read(_entries[index]);
    }

    public IEnumerable<KvDiskIndexEntry> ScanPrefixAfter(byte[] prefix, byte[]? afterKey)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        for (int i = 0; i < _entries.Length; i++)
        {
            var entry = _entries[i];
            if (!entry.Key.AsSpan().StartsWith(prefix))
                continue;
            if (afterKey is not null && KvKeyComparer.Instance.Compare(entry.Key, afterKey) <= 0)
                continue;

            yield return entry;
        }
    }

    public KvValueEntry Read(KvDiskIndexEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] payload = new byte[entry.Key.Length + entry.ValueLength];
        entry.Key.CopyTo(payload.AsSpan(0, entry.Key.Length));

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stream.Position = entry.ValueOffset;
            if (ReadExact(_stream, payload.AsSpan(entry.Key.Length, entry.ValueLength)) < entry.ValueLength)
                throw new InvalidDataException("KV state entry value is truncated.");
        }

        uint actualCrc = Crc32.HashToUInt32(payload);
        if (actualCrc != entry.PayloadCrc)
            throw new InvalidDataException("KV state entry CRC mismatch.");

        byte[] value = payload.AsSpan(entry.Key.Length, entry.ValueLength).ToArray();
        return new KvValueEntry(value, entry.Version, entry.ExpiresAtUtc);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _stream.Dispose();
        }
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

internal sealed class KvDiskIndexEntry
{
    public KvDiskIndexEntry(
        byte[] key,
        int valueLength,
        long version,
        DateTimeOffset? expiresAtUtc,
        long prefixOffset,
        long payloadOffset,
        uint payloadCrc)
    {
        Key = key;
        ValueLength = valueLength;
        Version = version;
        ExpiresAtUtc = expiresAtUtc;
        PrefixOffset = prefixOffset;
        PayloadOffset = payloadOffset;
        PayloadCrc = payloadCrc;
    }

    public byte[] Key { get; }

    public int ValueLength { get; }

    public long Version { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    public long PrefixOffset { get; }

    public long PayloadOffset { get; }

    public long ValueOffset => PayloadOffset + Key.Length;

    public uint PayloadCrc { get; }

    public KvValueEntry ToValueEntry() => new([], Version, ExpiresAtUtc);
}
