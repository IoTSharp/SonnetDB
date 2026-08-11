using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvStateFileTests : IDisposable
{
    private const int HeaderSize = 64;
    private const int EntryPrefixSize = 32;
    private const int LargeValueBytes = 4 * 1024 * 1024;
    private const long AllocationSlackBytes = 256 * 1024;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-kv-state-file-" + Guid.NewGuid().ToString("N"));

    public KvStateFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void SaveSnapshot_CrcCoversKeyThenValue_AndDiskReadRoundTripsMetadata()
    {
        string path = Path.Combine(_root, "roundtrip.SDBKVSNP");
        byte[] key = Encoding.UTF8.GetBytes("capture:107:42");
        byte[] value = Enumerable.Range(0, 257).Select(static i => (byte)i).ToArray();
        var expiresAtUtc = new DateTimeOffset(638_900_000_000_000_000, TimeSpan.Zero);
        KeyValuePair<byte[], KvValueEntry>[] entries =
        [
            new(key, new KvValueEntry(value, version: 42, expiresAtUtc)),
        ];

        KvStateFile.SaveSnapshot(path, sequence: 42, entries, count: 1, generation: 7);

        byte[] file = File.ReadAllBytes(path);
        int payloadOffset = HeaderSize + EntryPrefixSize;
        int crcOffset = payloadOffset + key.Length + value.Length;
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(crcOffset, sizeof(uint)));
        byte[] contiguousPayload = new byte[key.Length + value.Length];
        key.CopyTo(contiguousPayload, 0);
        value.CopyTo(contiguousPayload, key.Length);
        Assert.Equal(Crc32.HashToUInt32(contiguousPayload), storedCrc);

        using KvDiskState state = KvStateFile.OpenDiskState(path);
        KvValueEntry? restored = state.Get(key);

        Assert.NotNull(restored);
        Assert.Equal(42, state.Sequence);
        Assert.Equal(7, state.Generation);
        Assert.Equal(1, state.Count);
        Assert.Equal(42, restored.Version);
        Assert.Equal(expiresAtUtc, restored.ExpiresAtUtc);
        Assert.Equal(value, restored.Value);
    }

    /// <summary>
    /// 验证磁盘范围扫描从 lower-bound 开始，并在上界或离开前缀时立即停止。
    /// </summary>
    [Fact]
    public void DiskScanRange_UsesLowerBoundAndStopsAtEndOrPrefixBoundary()
    {
        string path = Path.Combine(_root, "range.SDBKVSNP");
        string[] keys =
        [
            "aaa:00",
            "idx:00",
            "idx:01",
            "idx:02",
            "idx:03",
            "idx:04",
            "idx:05",
            "idx2:00",
            "zzz:00",
        ];
        KeyValuePair<byte[], KvValueEntry>[] entries = keys
            .Select(static (key, index) => new KeyValuePair<byte[], KvValueEntry>(
                Encoding.UTF8.GetBytes(key),
                new KvValueEntry([(byte)index], version: index + 1)))
            .ToArray();
        KvStateFile.SaveSnapshot(path, sequence: entries.Length, entries, entries.Length);

        using KvDiskState state = KvStateFile.OpenDiskState(path);
        var visited = new List<int>();
        state.ScanIndexVisitedTestHook = visited.Add;

        var page = state.ScanRange(
                Encoding.UTF8.GetBytes("idx:"),
                Encoding.UTF8.GetBytes("idx:02"),
                Encoding.UTF8.GetBytes("idx:05"),
                Encoding.UTF8.GetBytes("idx:03"))
            .ToArray();

        Assert.Equal(
            ["idx:04"],
            page.Select(static entry => Encoding.UTF8.GetString(entry.Key)).ToArray());
        Assert.Equal([5, 6], visited);

        visited.Clear();
        var startWins = state.ScanRange(
                Encoding.UTF8.GetBytes("idx:"),
                Encoding.UTF8.GetBytes("idx:02"),
                Encoding.UTF8.GetBytes("idx:04"),
                Encoding.UTF8.GetBytes("idx:01"))
            .ToArray();

        Assert.Equal(
            ["idx:02", "idx:03"],
            startWins.Select(static entry => Encoding.UTF8.GetString(entry.Key)).ToArray());
        Assert.Equal([3, 4, 5], visited);

        visited.Clear();
        var allPrefixRows = state.ScanRange(
                Encoding.UTF8.GetBytes("idx:"),
                startInclusive: null,
                endExclusive: null,
                afterKey: null)
            .ToArray();

        Assert.Equal(
            ["idx:00", "idx:01", "idx:02", "idx:03", "idx:04", "idx:05"],
            allPrefixRows.Select(static entry => Encoding.UTF8.GetString(entry.Key)).ToArray());
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], visited);
    }

    [Fact]
    public void SaveSnapshot_LargeValue_DoesNotAllocateCombinedCrcPayload()
    {
        string path = Path.Combine(_root, "large-save.SDBKVSNP");
        byte[] key = Encoding.UTF8.GetBytes("capture:large");
        byte[] value = CreateLargeValue();
        KeyValuePair<byte[], KvValueEntry>[] entries =
        [
            new(key, new KvValueEntry(value, version: 1)),
        ];

        long allocated = MeasureAllocatedBytes(
            () => KvStateFile.SaveSnapshot(path, sequence: 1, entries, count: 1));

        Assert.True(
            allocated < AllocationSlackBytes,
            $"Saving should hash key/value incrementally without a payload-sized copy. Allocated={allocated:N0} bytes.");
    }

    [Fact]
    public void DiskRead_LargeValue_AllocatesOnlyReturnedValueBuffer()
    {
        string path = Path.Combine(_root, "large-read.SDBKVSNP");
        byte[] key = Encoding.UTF8.GetBytes("capture:large");
        byte[] expected = CreateLargeValue();
        KeyValuePair<byte[], KvValueEntry>[] entries =
        [
            new(key, new KvValueEntry(expected, version: 1)),
        ];
        KvStateFile.SaveSnapshot(path, sequence: 1, entries, count: 1);
        using KvDiskState state = KvStateFile.OpenDiskState(path);
        KvValueEntry? restored = null;

        long allocated = MeasureAllocatedBytes(() => restored = state.Get(key));

        Assert.NotNull(restored);
        Assert.Equal(expected, restored.Value);
        Assert.True(
            allocated < LargeValueBytes + AllocationSlackBytes,
            $"Reading should allocate the returned value once without a payload copy. Allocated={allocated:N0} bytes.");
    }

    [Fact]
    public void DiskRead_CorruptValue_ThrowsCrcMismatch()
    {
        string path = Path.Combine(_root, "corrupt.SDBKVSNP");
        byte[] key = Encoding.UTF8.GetBytes("capture:corrupt");
        byte[] value = [1, 2, 3, 4, 5];
        KeyValuePair<byte[], KvValueEntry>[] entries =
        [
            new(key, new KvValueEntry(value, version: 1)),
        ];
        KvStateFile.SaveSnapshot(path, sequence: 1, entries, count: 1);

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = HeaderSize + EntryPrefixSize + key.Length + 2;
            stream.WriteByte(0xFF);
        }

        using KvDiskState state = KvStateFile.OpenDiskState(path);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => state.Get(key));
        Assert.Equal("KV state entry CRC mismatch.", error.Message);
    }

    [Fact]
    public void DiskRead_TruncatedValue_Throws()
    {
        string path = Path.Combine(_root, "truncated-value.bin");
        File.WriteAllBytes(path, [0xAA, 0xBB]);
        byte[] key = [0x01];
        var entry = new KvDiskIndexEntry(
            key,
            valueLength: 2,
            version: 1,
            expiresAtUtc: null,
            prefixOffset: 0,
            payloadOffset: 0,
            payloadCrc: 0);
        using var state = new KvDiskState(path, sequence: 1, generation: 0, [entry]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => state.Read(entry));
        Assert.Equal("KV state entry value is truncated.", error.Message);
    }

    [Fact]
    public void OpenDiskState_V4UncompressedFile_RemainsReadable()
    {
        string path = Path.Combine(_root, "legacy-v4.SDBKVSNP");
        byte[] key = Encoding.UTF8.GetBytes("legacy:key");
        byte[] value = [4, 3, 2, 1];
        WriteLegacyV4(path, key, value, version: 9, generation: 3);

        using KvDiskState state = KvStateFile.OpenDiskState(path);
        KvValueEntry? restored = state.Get(key);

        Assert.NotNull(restored);
        Assert.Equal(9, state.Sequence);
        Assert.Equal(3, state.Generation);
        Assert.Equal(9, restored.Version);
        Assert.Equal(value, restored.Value);
    }

    [Fact]
    public void OpenDiskState_V5RestartWithSharedPrefix_IsRejected()
    {
        string path = Path.Combine(_root, "invalid-restart.SDBKVSNP");
        KeyValuePair<byte[], KvValueEntry>[] entries = Enumerable.Range(0, 17)
            .Select(static index => new KeyValuePair<byte[], KvValueEntry>(
                Encoding.UTF8.GetBytes($"restart:{index:D2}"),
                new KvValueEntry([], index + 1)))
            .ToArray();
        KvStateFile.SaveSnapshot(path, entries.Length, entries, entries.Length);
        byte[] encoded = File.ReadAllBytes(path);
        int restartOffset = FindV5EntryPrefixOffset(encoded, 16);
        int keyLength = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(restartOffset, 4));
        BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(restartOffset + 24, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(restartOffset + 28, 4), keyLength - 1);
        File.WriteAllBytes(path, encoded);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => KvStateFile.OpenDiskState(path));
        Assert.Contains("prefix encoding", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAllEntries_V5CompressedKeyDamage_ThrowsCrcMismatch()
    {
        string path = Path.Combine(_root, "corrupt-compressed-key.SDBKVSNP");
        KeyValuePair<byte[], KvValueEntry>[] entries =
        [
            new(Encoding.UTF8.GetBytes("shared-prefix:a"), new KvValueEntry([1], 1)),
            new(Encoding.UTF8.GetBytes("shared-prefix:b"), new KvValueEntry([2], 2)),
        ];
        KvStateFile.SaveSnapshot(path, entries.Length, entries, entries.Length);
        byte[] encoded = File.ReadAllBytes(path);
        int secondPrefixOffset = FindV5EntryPrefixOffset(encoded, 1);
        int secondSuffixOffset = secondPrefixOffset + EntryPrefixSize;
        encoded[secondSuffixOffset] ^= 0x01;
        File.WriteAllBytes(path, encoded);

        using KvDiskState state = KvStateFile.OpenDiskState(path);
        InvalidDataException error = Assert.Throws<InvalidDataException>(state.ValidateAllEntries);
        Assert.Equal("KV state entry CRC mismatch.", error.Message);
    }

    [Fact]
    public void OpenDiskState_V5TrailingData_IsRejected()
    {
        string path = Path.Combine(_root, "trailing-data.SDBKVSNP");
        KeyValuePair<byte[], KvValueEntry>[] entries =
        [
            new(Encoding.UTF8.GetBytes("key"), new KvValueEntry([], 1)),
        ];
        KvStateFile.SaveSnapshot(path, 1, entries, 1);
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
            stream.WriteByte(0xFF);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => KvStateFile.OpenDiskState(path));
        Assert.Contains("trailing data", error.Message, StringComparison.Ordinal);
    }

    private static byte[] CreateLargeValue()
    {
        byte[] value = new byte[LargeValueBytes];
        for (int i = 0; i < value.Length; i++)
            value[i] = (byte)(i * 31);
        return value;
    }

    private static void WriteLegacyV4(
        string path,
        byte[] key,
        byte[] value,
        long version,
        long generation)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        Span<byte> header = stackalloc byte[HeaderSize];
        "SDBKVSNP"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], 4);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], HeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(header[16..], DateTime.UtcNow.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(header[24..], version);
        BinaryPrimitives.WriteInt32LittleEndian(header[32..], 1);
        BinaryPrimitives.WriteInt64LittleEndian(header[36..], generation);
        BinaryPrimitives.WriteUInt32LittleEndian(header[60..], Crc32.HashToUInt32(header[..60]));
        stream.Write(header);

        Span<byte> prefix = stackalloc byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, key.Length);
        BinaryPrimitives.WriteInt32LittleEndian(prefix[4..], value.Length);
        BinaryPrimitives.WriteInt64LittleEndian(prefix[8..], version);
        stream.Write(prefix);
        stream.Write(key);
        stream.Write(value);
        var crc = new Crc32();
        crc.Append(key);
        crc.Append(value);
        Span<byte> checksum = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(checksum, crc.GetCurrentHashAsUInt32());
        stream.Write(checksum);
    }

    private static int FindV5EntryPrefixOffset(byte[] encoded, int targetIndex)
    {
        int offset = HeaderSize;
        for (int index = 0; index < targetIndex; index++)
        {
            int valueLength = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(offset + 4, 4));
            int storedKeyLength = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(offset + 28, 4));
            offset = checked(offset + EntryPrefixSize + storedKeyLength + valueLength + sizeof(uint));
        }
        return offset;
    }

    private static long MeasureAllocatedBytes(Action action)
    {
        action();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
