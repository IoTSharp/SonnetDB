using System.Buffers.Binary;
using System.IO.Hashing;

namespace SonnetDB.Kv;

internal static class KvStateFile
{
    private const int HeaderSize = 64;
    private const int EntryPrefixBytes = 16;

    private static ReadOnlySpan<byte> SnapshotMagic => "SDBKVSNP"u8;
    private static ReadOnlySpan<byte> SegmentMagic => "SDBKVSEG"u8;

    public static void SaveSnapshot(
        string path,
        long sequence,
        IReadOnlyDictionary<byte[], KvValueEntry> values)
        => Save(path, SnapshotMagic, sequence, values);

    public static void SaveSegment(
        string path,
        long sequence,
        IReadOnlyDictionary<byte[], KvValueEntry> values)
        => Save(path, SegmentMagic, sequence, values);

    public static KvStateSnapshot Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[HeaderSize];
        if (ReadExact(fs, header) < HeaderSize)
            throw new InvalidDataException("KV state header is truncated.");

        bool isSnapshot = header[..8].SequenceEqual(SnapshotMagic);
        bool isSegment = header[..8].SequenceEqual(SegmentMagic);
        uint expectedHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(60, 4));
        uint actualHeaderCrc = Crc32.HashToUInt32(header[..60]);
        if ((!isSnapshot && !isSegment) ||
            BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4)) != 1 ||
            BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4)) != HeaderSize ||
            expectedHeaderCrc != actualHeaderCrc)
        {
            throw new InvalidDataException("KV state header is invalid.");
        }

        long sequence = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(24, 8));
        int count = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(32, 4));
        if (sequence < 0 || count < 0)
            throw new InvalidDataException("KV state header contains invalid counters.");

        var values = new Dictionary<byte[], KvValueEntry>(count, KvKeyComparer.Instance);
        byte[] prefixBuffer = new byte[EntryPrefixBytes];
        byte[] crcBuffer = new byte[4];
        for (int i = 0; i < count; i++)
        {
            Span<byte> prefix = prefixBuffer;
            if (ReadExact(fs, prefix) < EntryPrefixBytes)
                throw new InvalidDataException("KV state entry prefix is truncated.");

            int keyLength = BinaryPrimitives.ReadInt32LittleEndian(prefix[..4]);
            int valueLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.Slice(4, 4));
            long version = BinaryPrimitives.ReadInt64LittleEndian(prefix.Slice(8, 8));
            if (keyLength <= 0 || valueLength < 0)
                throw new InvalidDataException("KV state entry length is invalid.");

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
            values[key] = new KvValueEntry(value, version);
        }

        return new KvStateSnapshot(sequence, values);
    }

    private static void Save(
        string path,
        ReadOnlySpan<byte> magic,
        long sequence,
        IReadOnlyDictionary<byte[], KvValueEntry> values)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string tempPath = path + ".tmp";

        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            magic.CopyTo(header[..8]);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12, 4), HeaderSize);
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(16, 8), DateTime.UtcNow.Ticks);
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(24, 8), sequence);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(32, 4), values.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(60, 4), Crc32.HashToUInt32(header[..60]));
            fs.Write(header);

            var ordered = values.OrderBy(static x => x.Key, KvKeyComparer.Instance);
            byte[] prefixBuffer = new byte[EntryPrefixBytes];
            byte[] crcBuffer = new byte[4];
            foreach (var pair in ordered)
            {
                Span<byte> prefix = prefixBuffer;
                BinaryPrimitives.WriteInt32LittleEndian(prefix[..4], pair.Key.Length);
                BinaryPrimitives.WriteInt32LittleEndian(prefix.Slice(4, 4), pair.Value.Value.Length);
                BinaryPrimitives.WriteInt64LittleEndian(prefix.Slice(8, 8), pair.Value.Version);
                fs.Write(prefix);
                fs.Write(pair.Key);
                fs.Write(pair.Value.Value);

                uint crc = ComputeEntryCrc(pair.Key, pair.Value.Value);
                BinaryPrimitives.WriteUInt32LittleEndian(crcBuffer, crc);
                fs.Write(crcBuffer);
            }

            fs.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
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
}

internal sealed class KvStateSnapshot
{
    public KvStateSnapshot(long sequence, Dictionary<byte[], KvValueEntry> values)
    {
        Sequence = sequence;
        Values = values;
    }

    public long Sequence { get; }

    public Dictionary<byte[], KvValueEntry> Values { get; }
}
