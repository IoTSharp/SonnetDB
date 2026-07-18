using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvStateFileTests : IDisposable
{
    private const int HeaderSize = 64;
    private const int EntryPrefixSize = 24;
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

    private static byte[] CreateLargeValue()
    {
        byte[] value = new byte[LargeValueBytes];
        for (int i = 0; i < value.Length; i++)
            value[i] = (byte)(i * 31);
        return value;
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
