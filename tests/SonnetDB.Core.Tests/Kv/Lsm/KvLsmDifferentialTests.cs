using System.Text;
using SonnetDB.Kv.Lsm;
using Xunit;

namespace SonnetDB.Core.Tests.Kv.Lsm;

/// <summary>
/// 多数据源（活跃/不可变 memtable、L0×2、L1 SST）合并后的快照视图与参考实现逐项对拍。
/// </summary>
public sealed class KvLsmDifferentialTests : IDisposable
{
    private static readonly long[] ExpiryChoices = [0, 0, 0, 100, 200, 300];
    private static readonly long[] ReadTimes = [50, 150, 250, 350];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-kv-diff-" + Guid.NewGuid().ToString("N"));

    public KvLsmDifferentialTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void MergedSnapshotView_MatchesReferenceForScansSeeksAndRandomWalks(int seed)
    {
        var random = new Random(seed);
        string[] pool = BuildKeyPool(random);
        var entries = new List<Entry>();
        for (long sequence = 1; sequence <= 4_000; sequence++)
        {
            byte[] userKey = Encoding.UTF8.GetBytes(pool[random.Next(pool.Length)]);
            if (random.Next(4) == 0)
            {
                entries.Add(new Entry(userKey, sequence, KvEntryKind.Delete, 0, []));
            }
            else
            {
                byte[] value = Encoding.UTF8.GetBytes($"v{sequence}");
                entries.Add(new Entry(userKey, sequence, KvEntryKind.Put, ExpiryChoices[random.Next(ExpiryChoices.Length)], value));
            }
        }

        using KvSstReader l1 = WriteSst("l1", entries.Where(static e => e.Sequence <= 1_000));
        using KvSstReader l0Old = WriteSst("l0a", entries.Where(static e => e.Sequence is > 1_000 and <= 2_000));
        var l0NewEntries = entries.Where(static e => e.Sequence is > 2_000 and <= 3_000).ToList();
        using KvSstReader l0New = WriteSst("l0b", l0NewEntries);
        var immutable = new KvMemTable();
        foreach (Entry entry in entries.Where(static e => e.Sequence is > 3_000 and <= 3_500))
            entry.AddTo(immutable);
        // 模拟 WAL 回放与已刷盘 SST 重叠：同一 internal key 同时出现在两个数据源中。
        foreach (Entry entry in l0NewEntries.Where((_, i) => i % 3 == 0))
            entry.AddTo(immutable);
        var active = new KvMemTable();
        foreach (Entry entry in entries.Where(static e => e.Sequence > 3_500))
            entry.AddTo(active);

        long steps = 0;
        for (int trial = 0; trial < 60; trial++)
        {
            long snapshot = trial == 0 ? 4_000 : random.Next(0, 4_001);
            long readTime = ReadTimes[random.Next(ReadTimes.Length)];
            byte[]? lower = random.Next(3) == 0 ? Encoding.UTF8.GetBytes(pool[random.Next(pool.Length)]) : null;
            byte[]? upper = random.Next(3) == 0 ? Encoding.UTF8.GetBytes(pool[random.Next(pool.Length)]) : null;
            List<Entry> expected = Visible(entries, snapshot, readTime, lower, upper);

            using var view = new KvSnapshotIterator(
                new KvMergingIterator([active.CreateIterator(), immutable.CreateIterator(), l0New.CreateIterator(), l0Old.CreateIterator(), l1.CreateIterator()]),
                snapshot, readTime, lower, upper);

            // 全量正向 / 反向
            int index = 0;
            for (view.SeekToFirst(); view.Valid; view.MoveNext(), steps++)
                AssertAt(expected, index++, view);
            Assert.Equal(expected.Count, index);
            for (view.SeekToLast(); view.Valid; view.MovePrev(), steps++)
                AssertAt(expected, --index, view);
            Assert.Equal(0, index);

            // 随机定位 + 随机方向游走
            for (int walk = 0; walk < 40; walk++)
            {
                byte[] target = Encoding.UTF8.GetBytes(pool[random.Next(pool.Length)] + (random.Next(2) == 0 ? "" : "~"));
                int position;
                switch (random.Next(4))
                {
                    case 0:
                        view.Seek(target);
                        position = expected.FindIndex(e => CompareUser(e.UserKey, target) >= 0);
                        break;
                    case 1:
                        view.SeekForPrev(target);
                        position = expected.FindLastIndex(e => CompareUser(e.UserKey, target) <= 0);
                        break;
                    case 2:
                        view.SeekToFirst();
                        position = expected.Count > 0 ? 0 : -1;
                        break;
                    default:
                        view.SeekToLast();
                        position = expected.Count - 1;
                        break;
                }

                for (int step = 0; step < 30; step++, steps++)
                {
                    if (position < 0 || position >= expected.Count)
                    {
                        Assert.False(view.Valid);
                        break;
                    }
                    AssertAt(expected, position, view);
                    if (random.Next(2) == 0)
                    {
                        view.MoveNext();
                        position++;
                    }
                    else
                    {
                        view.MovePrev();
                        position--;
                    }
                }
            }
        }

        Assert.True(steps > 10_000, $"steps: {steps}");
    }

    [Fact]
    public void MergingIterator_DeduplicatesIdenticalInternalKeysInBothDirections()
    {
        var a = new KvMemTable();
        var b = new KvMemTable();
        byte[][] keys =
        [
            KvInternalKey.Encode("a"u8, 3, KvEntryKind.Put),
            KvInternalKey.Encode("a"u8, 1, KvEntryKind.Put),
            KvInternalKey.Encode("b"u8, 2, KvEntryKind.Put),
            KvInternalKey.Encode("c"u8, 4, KvEntryKind.Delete),
        ];
        foreach (byte[] key in keys)
        {
            byte[] value = KvInternalKey.Kind(key) == KvEntryKind.Put ? "x"u8.ToArray() : [];
            a.Add(key, value);
            b.Add(key, value);
        }
        b.Add(KvInternalKey.Encode("b"u8, 1, KvEntryKind.Put), "only-b"u8);

        using var merged = new KvMergingIterator([a.CreateIterator(), b.CreateIterator()]);
        var forward = new List<string>();
        for (merged.SeekToFirst(); merged.Valid; merged.MoveNext())
            forward.Add(Describe(merged.Key));
        Assert.Equal(["a/3", "a/1", "b/2", "b/1", "c/4"], forward);

        var backward = new List<string>();
        for (merged.SeekToLast(); merged.Valid; merged.MovePrev())
            backward.Add(Describe(merged.Key));
        forward.Reverse();
        Assert.Equal(forward, backward);

        merged.Seek(KvInternalKey.EncodeSeekKey("b"u8, 5));
        Assert.Equal("b/2", Describe(merged.Key));
        merged.MovePrev();
        Assert.Equal("a/1", Describe(merged.Key));
        merged.MoveNext();
        Assert.Equal("b/2", Describe(merged.Key));
        merged.MoveNext();
        Assert.Equal("b/1", Describe(merged.Key));
    }

    [Fact]
    public void SstIterator_ReverseTraversalAndSeekForPrevMatchReference()
    {
        var random = new Random(17);
        var keys = Enumerable.Range(0, 3_000)
            .Select(i => KvInternalKey.Encode(Encoding.UTF8.GetBytes($"k{i % 700:D4}"), i + 1, random.Next(5) == 0 ? KvEntryKind.Delete : KvEntryKind.Put))
            .OrderBy(static k => k, Comparer<byte[]>.Create(static (x, y) => KvInternalKey.Compare(x, y)))
            .ToList();
        string path = Path.Combine(_root, "reverse" + KvSstFormat.FileExtension);
        using (var writer = new KvSstWriter(path, new KvSstWriterOptions { BlockSize = 300, RestartInterval = 3, SyncDirectory = false }))
        {
            foreach (byte[] key in keys)
                writer.Add(key, KvInternalKey.Kind(key) == KvEntryKind.Put ? key.AsSpan(0, 5) : [], KvInternalKey.Kind(key) == KvEntryKind.Put ? 7 : 0);
            writer.Finish();
        }

        using KvSstReader reader = KvSstReader.Open(path);
        using KvSstIterator iterator = reader.CreateIterator();
        int index = keys.Count;
        for (iterator.SeekToLast(); iterator.Valid; iterator.MovePrev())
        {
            byte[] key = keys[--index];
            Assert.Equal(key, iterator.Key.ToArray());
            bool put = KvInternalKey.Kind(key) == KvEntryKind.Put;
            Assert.Equal(put ? key.AsSpan(0, 5).ToArray() : [], iterator.Value.ToArray());
            Assert.Equal(put ? 7 : 0, iterator.ExpiresAtUtcTicks);
        }
        Assert.Equal(0, index);

        for (int i = 0; i < 2_000; i++)
        {
            byte[] target = KvInternalKey.EncodeSeekKey(Encoding.UTF8.GetBytes($"k{random.Next(-5, 710):D4}"), random.Next(0, 3_100));
            int expected = keys.FindLastIndex(k => KvInternalKey.Compare(k, target) <= 0);
            iterator.SeekForPrev(target);
            Assert.Equal(expected >= 0, iterator.Valid);
            if (expected >= 0)
                Assert.Equal(keys[expected], iterator.Key.ToArray());
        }
    }

    private KvSstReader WriteSst(string name, IEnumerable<Entry> entries)
    {
        string path = Path.Combine(_root, name + KvSstFormat.FileExtension);
        using (var writer = new KvSstWriter(path, new KvSstWriterOptions { BlockSize = 512, RestartInterval = 4, SyncDirectory = false }))
        {
            foreach (Entry entry in entries.OrderBy(static e => e.InternalKey, Comparer<byte[]>.Create(static (x, y) => KvInternalKey.Compare(x, y))))
                writer.Add(entry.InternalKey, entry.Value, entry.Expires);
            writer.Finish();
        }
        return KvSstReader.Open(path);
    }

    private static List<Entry> Visible(List<Entry> entries, long snapshot, long readTime, byte[]? lower, byte[]? upper)
        => entries
            .Where(e => e.Sequence <= snapshot)
            .GroupBy(static e => Encoding.Latin1.GetString(e.UserKey))
            .Select(static g => g.MaxBy(static e => e.Sequence)!)
            .Where(e => e.Kind == KvEntryKind.Put && (e.Expires == 0 || e.Expires > readTime))
            .Where(e => (lower is null || CompareUser(e.UserKey, lower) >= 0) && (upper is null || CompareUser(e.UserKey, upper) < 0))
            .OrderBy(static e => e.UserKey, Comparer<byte[]>.Create(CompareUser))
            .ToList();

    private static void AssertAt(List<Entry> expected, int index, KvSnapshotIterator view)
    {
        Assert.True(view.Valid, $"expected entry #{index} but iterator is invalid");
        Entry entry = expected[index];
        Assert.Equal(Encoding.Latin1.GetString(entry.UserKey), Encoding.Latin1.GetString(view.Key));
        Assert.Equal(entry.Value, view.Value.ToArray());
        Assert.Equal(entry.Sequence, view.Sequence);
        Assert.Equal(entry.Expires, view.ExpiresAtUtcTicks);
    }

    private static string[] BuildKeyPool(Random random)
    {
        // 含互为前缀的 key 与 0x00/0xFF 边界字节，覆盖 internal key trailer 比较的边界。
        var pool = new HashSet<string> { "a", "ab", "abc", "b", "b\u0000", "bÿ", "c" };
        while (pool.Count < 250)
            pool.Add("k" + random.Next(0, 400).ToString("D3") + (random.Next(3) == 0 ? "x" : ""));
        return [.. pool];
    }

    private static int CompareUser(byte[]? left, byte[]? right) => left.AsSpan().SequenceCompareTo(right);

    private static string Describe(ReadOnlySpan<byte> internalKey)
        => Encoding.Latin1.GetString(KvInternalKey.UserKey(internalKey)) + "/" + KvInternalKey.Sequence(internalKey);

    private sealed record Entry(byte[] UserKey, long Sequence, KvEntryKind Kind, long Expires, byte[] Value)
    {
        internal byte[] InternalKey { get; } = KvInternalKey.Encode(UserKey, Sequence, Kind);

        internal void AddTo(KvMemTable table) => table.Add(InternalKey, Value, Expires);
    }
}
