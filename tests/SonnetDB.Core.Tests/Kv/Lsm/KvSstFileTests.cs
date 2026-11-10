using System.Text;
using SonnetDB.Kv;
using SonnetDB.Kv.Lsm;
using Xunit;

namespace SonnetDB.Core.Tests.Kv.Lsm;

public sealed class KvSstFileTests : IDisposable
{
    private static readonly KvSstWriterOptions SmallBlocks = new() { BlockSize = 256, RestartInterval = 4, SyncDirectory = false };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-kv-sst-" + Guid.NewGuid().ToString("N"));

    public KvSstFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void WriteThenRead_MultiBlockFile_RoundTripsEveryEntryAndProperties()
    {
        var expected = BuildEntries(seed: 7, userKeys: 800, maxVersions: 3);
        string path = WriteFile("multi", expected, SmallBlocks with { Generation = 5 });

        using KvSstReader reader = KvSstReader.Open(path);
        reader.VerifyAll();

        KvSstProperties properties = reader.Properties;
        Assert.Equal(expected.Count, properties.EntryCount);
        Assert.Equal(expected.Count(static e => e.Kind == KvEntryKind.Delete), properties.DeletionCount);
        Assert.True(properties.DataBlockCount > 10);
        Assert.Equal(expected.Min(static e => e.Sequence), properties.MinSequence);
        Assert.Equal(expected.Max(static e => e.Sequence), properties.MaxSequence);
        Assert.Equal(expected.Where(static e => e.Expires != 0).Min(static e => e.Expires), properties.MinExpiresAtUtcTicks);
        Assert.Equal(5, properties.Generation);
        Assert.Equal(expected[0].InternalKey, properties.SmallestKey);
        Assert.Equal(expected[^1].InternalKey, properties.LargestKey);

        using KvSstIterator iterator = reader.CreateIterator();
        int index = 0;
        for (iterator.SeekToFirst(); iterator.Valid; iterator.MoveNext())
        {
            KvSstEntry actual = iterator.Current;
            TestEntry wanted = expected[index++];
            Assert.Equal(wanted.InternalKey, actual.InternalKey);
            Assert.Equal(wanted.Kind, actual.Kind);
            Assert.Equal(wanted.Sequence, actual.Sequence);
            Assert.Equal(wanted.Expires, actual.ExpiresAtUtcTicks);
            Assert.Equal(wanted.Value, actual.Value.ToArray());
        }
        Assert.Equal(expected.Count, index);
    }

    [Fact]
    public void TryGet_ReturnsNewestVersionVisibleAtSnapshot_IncludingTombstones()
    {
        var entries = new List<TestEntry>
        {
            Put("alpha", 5, "a5"),
            Put("key", 30, "v30"),
            Delete("key", 20),
            Put("key", 10, "v10"),
            Put("zulu", 1, "z1"),
        };
        string path = WriteFile("versions", entries, SmallBlocks);
        using KvSstReader reader = KvSstReader.Open(path);

        Assert.True(reader.TryGet("key"u8, 35, out KvSstEntry latest));
        Assert.Equal(KvEntryKind.Put, latest.Kind);
        Assert.Equal("v30", Encoding.UTF8.GetString(latest.Value.Span));

        Assert.True(reader.TryGet("key"u8, 25, out KvSstEntry tombstone));
        Assert.Equal(KvEntryKind.Delete, tombstone.Kind);
        Assert.Equal(20, tombstone.Sequence);

        Assert.True(reader.TryGet("key"u8, 10, out KvSstEntry oldest));
        Assert.Equal("v10", Encoding.UTF8.GetString(oldest.Value.Span));

        Assert.False(reader.TryGet("key"u8, 9, out _));
        Assert.False(reader.TryGet("missing"u8, 100, out _));
        Assert.False(reader.TryGet("aaa"u8, 100, out _));
        Assert.False(reader.TryGet("zzzz"u8, 100, out _));
    }

    [Fact]
    public void TryGet_FindsVersionsThatSpanDataBlockBoundaries()
    {
        var entries = new List<TestEntry>();
        for (long sequence = 400; sequence >= 1; sequence--)
            entries.Add(Put("hot", sequence, "value-" + sequence));
        entries.Add(Put("later", 1, "x"));
        string path = WriteFile("span", entries, SmallBlocks);

        using KvSstReader reader = KvSstReader.Open(path);
        Assert.True(reader.Properties.DataBlockCount > 5);
        foreach (long snapshot in new long[] { 400, 333, 200, 57, 2, 1 })
        {
            Assert.True(reader.TryGet("hot"u8, snapshot, out KvSstEntry entry));
            Assert.Equal(snapshot, entry.Sequence);
            Assert.Equal("value-" + snapshot, Encoding.UTF8.GetString(entry.Value.Span));
        }
        Assert.True(reader.TryGet("later"u8, 1_000, out _));
    }

    [Fact]
    public void Seek_MatchesReferenceLowerBoundForRandomTargets()
    {
        var expected = BuildEntries(seed: 11, userKeys: 500, maxVersions: 2);
        string path = WriteFile("seek", expected, SmallBlocks);
        using KvSstReader reader = KvSstReader.Open(path);
        using KvSstIterator iterator = reader.CreateIterator();
        var random = new Random(19);

        for (int i = 0; i < 2_000; i++)
        {
            byte[] target = KvInternalKey.EncodeSeekKey(
                Encoding.UTF8.GetBytes($"user:{random.Next(0, 1_100):D5}"),
                random.Next(0, 4_000));
            int reference = expected.FindIndex(e => KvInternalKey.Compare(e.InternalKey, target) >= 0);

            iterator.Seek(target);
            if (reference < 0)
            {
                Assert.False(iterator.Valid);
                continue;
            }
            Assert.True(iterator.Valid);
            Assert.Equal(expected[reference].InternalKey, iterator.Key.ToArray());
        }
    }

    [Fact]
    public void SingleEntryFile_RoundTripsWithEmptyUserKeyAndEmptyValue()
    {
        string path = WriteFile("single", [new TestEntry(KvInternalKey.Encode([], 0, KvEntryKind.Put), KvEntryKind.Put, 0, 0, [])], SmallBlocks);

        using KvSstReader reader = KvSstReader.Open(path);
        reader.VerifyAll();
        Assert.Equal(1, reader.Properties.DataBlockCount);
        Assert.True(reader.TryGet([], 0, out KvSstEntry entry));
        Assert.True(entry.Value.IsEmpty);
    }

    [Fact]
    public void LargeValue_SpanningMoreThanOneBlockTarget_RoundTrips()
    {
        byte[] value = new byte[1024 * 1024];
        new Random(3).NextBytes(value);
        string path = WriteFile("large", [Put("big", 1, value)], SmallBlocks);

        using KvSstReader reader = KvSstReader.Open(path);
        Assert.True(reader.TryGet("big"u8, 1, out KvSstEntry entry));
        Assert.Equal(value, entry.Value.ToArray());
    }

    [Fact]
    public void Writer_RejectsInvalidInputAndLeavesNoTemporaryFile()
    {
        string path = Path.Combine(_root, "invalid" + KvSstFormat.FileExtension);
        using (var writer = new KvSstWriter(path, SmallBlocks))
        {
            writer.Add(KvInternalKey.Encode("b"u8, 5, KvEntryKind.Put), "x"u8);
            Assert.Throws<ArgumentException>(() => writer.Add(KvInternalKey.Encode("b"u8, 5, KvEntryKind.Put), "x"u8));
            Assert.Throws<ArgumentException>(() => writer.Add(KvInternalKey.Encode("a"u8, 9, KvEntryKind.Put), "x"u8));
            Assert.Throws<ArgumentException>(() => writer.Add(KvInternalKey.Encode("c"u8, 1, KvEntryKind.Delete), "x"u8));
            Assert.Throws<ArgumentException>(() => writer.Add(KvInternalKey.Encode("c"u8, 1, KvEntryKind.Delete), [], expiresAtUtcTicks: 5));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.Add(KvInternalKey.Encode("c"u8, 1, KvEntryKind.Put), "x"u8, -1));
            Assert.Throws<ArgumentException>(() => writer.Add(KvInternalKey.EncodeSeekKey("c"u8, 1), []));
        }

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));

        using (var empty = new KvSstWriter(path, SmallBlocks))
            Assert.Throws<InvalidOperationException>(() => empty.Finish());
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Writer_RefusesToOverwriteAndCannotBeReused()
    {
        string path = WriteFile("existing", [Put("k", 1, "v")], SmallBlocks);
        Assert.Throws<IOException>(() => new KvSstWriter(path, SmallBlocks));

        string second = Path.Combine(_root, "reuse" + KvSstFormat.FileExtension);
        using var writer = new KvSstWriter(second, SmallBlocks);
        writer.Add(KvInternalKey.Encode("k"u8, 1, KvEntryKind.Put), "v"u8);
        writer.Finish();
        Assert.Throws<InvalidOperationException>(() => writer.Add(KvInternalKey.Encode("l"u8, 1, KvEntryKind.Put), "v"u8));
        Assert.Throws<InvalidOperationException>(() => writer.Finish());
    }

    [Fact]
    public void Open_RejectsTruncatedFiles()
    {
        string path = WriteFile("truncated", BuildEntries(seed: 3, userKeys: 50, maxVersions: 1), SmallBlocks);
        byte[] original = File.ReadAllBytes(path);

        foreach (int length in new[] { 0, 10, KvSstFooter.Size - 1, original.Length - 1, original.Length / 2 })
        {
            File.WriteAllBytes(path, original.AsSpan(0, length).ToArray());
            var exception = Assert.Throws<InvalidDataException>(() => KvSstReader.Open(path).Dispose());
            Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnySingleByteFlip_IsDetectedByOpenOrFullVerification()
    {
        string path = WriteFile("flip", BuildEntries(seed: 5, userKeys: 120, maxVersions: 2), SmallBlocks);
        byte[] original = File.ReadAllBytes(path);
        var random = new Random(23);
        var offsets = Enumerable.Range(0, 300).Select(_ => random.Next(original.Length))
            .Concat([0, original.Length - 1, original.Length - KvSstFooter.Size])
            .Distinct();

        foreach (int offset in offsets)
        {
            byte[] corrupted = (byte[])original.Clone();
            corrupted[offset] ^= 0x20;
            File.WriteAllBytes(path, corrupted);

            var exception = Assert.Throws<InvalidDataException>(() =>
            {
                using KvSstReader reader = KvSstReader.Open(path);
                reader.VerifyAll();
            });
            Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DataBlockCorruption_SurfacesFromPointReadWithFilePath()
    {
        var entries = BuildEntries(seed: 9, userKeys: 200, maxVersions: 1);
        string path = WriteFile("point-corrupt", entries, SmallBlocks);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[10] ^= 0x01; // 第一个数据块内部
        File.WriteAllBytes(path, bytes);

        using KvSstReader reader = KvSstReader.Open(path);
        var exception = Assert.Throws<InvalidDataException>(() =>
            reader.TryGet(KvInternalKey.UserKey(entries[0].InternalKey), long.MaxValue >> 9, out _));
        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        Assert.Contains("CRC", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_AreAccountedInDiskReadBudget_AndHonorCancellation()
    {
        string path = WriteFile("budget", BuildEntries(seed: 13, userKeys: 300, maxVersions: 1), SmallBlocks);
        using var budget = new KvDiskReadBudget(maxConcurrentReads: 2);

        using KvSstReader reader = KvSstReader.Open(path, budget);
        long afterOpen = budget.CompletedReads;
        Assert.Equal(4, afterOpen); // footer + properties + index + filter
        reader.VerifyAll();
        Assert.Equal(afterOpen + reader.Properties.DataBlockCount, budget.CompletedReads);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => reader.VerifyAll(cancellation.Token));
    }

    /// <summary>单条 ≥ 块大小的条目独占数据块，读取相邻小 key 时不读入大 value。</summary>
    [Fact]
    public void LargeEntry_IsIsolatedInItsOwnBlock_SoNeighbourReadsStaySmall()
    {
        byte[] large = new byte[64 * 1024];
        new Random(17).NextBytes(large);
        var entries = new List<TestEntry>
        {
            Put("a", 1, "small-a"),
            Put("b", 2, "small-b"),
            Put("c", 3, large),
            Put("d", 4, "small-d"),
        };
        string path = WriteFile("isolated", entries, new KvSstWriterOptions { BlockSize = 4096, SyncDirectory = false });
        using var budget = new KvDiskReadBudget(maxConcurrentReads: 2);

        using KvSstReader reader = KvSstReader.Open(path, budget);
        reader.VerifyAll();
        Assert.Equal(3, reader.Properties.DataBlockCount); // [a,b] [c] [d]

        foreach (string key in new[] { "b", "d" })
        {
            long before = budget.CompletedBytes;
            Assert.True(reader.TryGet(Encoding.UTF8.GetBytes(key), long.MaxValue >> 9, out KvSstEntry entry));
            Assert.Equal("small-" + key, Encoding.UTF8.GetString(entry.Value.Span));
            Assert.InRange(budget.CompletedBytes - before, 1, 1024);
        }
        Assert.True(reader.TryGet("c"u8, long.MaxValue >> 9, out KvSstEntry largeEntry));
        Assert.Equal(large, largeEntry.Value.ToArray());
    }

    [Fact]
    public void DisposedReader_RejectsNewIterators()
    {
        string path = WriteFile("disposed", [Put("k", 1, "v")], SmallBlocks);
        KvSstReader reader = KvSstReader.Open(path);
        reader.Dispose();
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.CreateIterator());
    }

    private string WriteFile(string name, IReadOnlyList<TestEntry> entries, KvSstWriterOptions options)
    {
        string path = Path.Combine(_root, name + KvSstFormat.FileExtension);
        using var writer = new KvSstWriter(path, options);
        foreach (TestEntry entry in entries)
            writer.Add(entry.InternalKey, entry.Value, entry.Expires);
        writer.Finish();
        Assert.False(File.Exists(path + ".tmp"));
        return path;
    }

    private static List<TestEntry> BuildEntries(int seed, int userKeys, int maxVersions)
    {
        var random = new Random(seed);
        var entries = new List<TestEntry>();
        long sequence = 1;
        for (int i = 0; i < userKeys; i++)
        {
            byte[] userKey = Encoding.UTF8.GetBytes($"user:{i * 2:D5}");
            int versions = random.Next(1, maxVersions + 1);
            for (int v = 0; v < versions; v++)
            {
                long seq = sequence++;
                if (random.Next(5) == 0)
                {
                    entries.Add(new TestEntry(KvInternalKey.Encode(userKey, seq, KvEntryKind.Delete), KvEntryKind.Delete, seq, 0, []));
                }
                else
                {
                    byte[] value = new byte[random.Next(0, 64)];
                    random.NextBytes(value);
                    long expires = random.Next(3) == 0 ? 638_000_000_000_000_000 + random.Next(1, 1_000_000) : 0;
                    entries.Add(new TestEntry(KvInternalKey.Encode(userKey, seq, KvEntryKind.Put), KvEntryKind.Put, seq, expires, value));
                }
            }
        }

        entries.Sort(static (left, right) => KvInternalKey.Compare(left.InternalKey, right.InternalKey));
        return entries;
    }

    private static TestEntry Put(string key, long sequence, string value) => Put(key, sequence, Encoding.UTF8.GetBytes(value));

    private static TestEntry Put(string key, long sequence, byte[] value)
        => new(KvInternalKey.Encode(Encoding.UTF8.GetBytes(key), sequence, KvEntryKind.Put), KvEntryKind.Put, sequence, 0, value);

    private static TestEntry Delete(string key, long sequence)
        => new(KvInternalKey.Encode(Encoding.UTF8.GetBytes(key), sequence, KvEntryKind.Delete), KvEntryKind.Delete, sequence, 0, []);

    private sealed record TestEntry(byte[] InternalKey, KvEntryKind Kind, long Sequence, long Expires, byte[] Value);
}
