using System.Text;
using SonnetDB.Kv;
using SonnetDB.Kv.Lsm;
using Xunit;

namespace SonnetDB.Core.Tests.Kv.Lsm;

public sealed class KvLsmRangeDeletionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-kv-range-" + Guid.NewGuid().ToString("N"));

    public KvLsmRangeDeletionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void WalRangedBatch_RoundTripsAndRejectsInvalidInput()
    {
        string path = Path.Combine(_root, "wal", "000000000001.SDBKVWAL");
        using (KvWalFile wal = KvWalFile.Open(path, 1, 4096))
        {
            Assert.Equal(1, wal.AppendMutationBatch([KvBatchMutation.Put(Bytes("plain"), Bytes("v"))]));
            Assert.Equal(2, wal.AppendMutationBatchWithRanges(
                [KvBatchMutation.Put(Bytes("k"), Bytes("v"), new DateTimeOffset(5, TimeSpan.Zero)), KvBatchMutation.Delete(Bytes("d"))],
                [new KvRangeDeletion([], Bytes("b")), new KvRangeDeletion(Bytes("x"), Bytes("z"))]));
            Assert.Equal(3, wal.AppendMutationBatchWithRanges([], [new KvRangeDeletion(Bytes("m"), Bytes("n"))]));

            Assert.Throws<ArgumentException>(() => wal.AppendMutationBatchWithRanges([], []));
            Assert.Throws<ArgumentException>(() => wal.AppendMutationBatchWithRanges([], [new KvRangeDeletion(Bytes("b"), Bytes("a"))]));
            Assert.Throws<ArgumentException>(() => wal.AppendMutationBatchWithRanges([], [new KvRangeDeletion(Bytes("a"), [])]));
            wal.Sync();
        }

        var records = new List<KvWalRecord>();
        using (KvWalFile.Open(path, 1, 4096, records.Add)) { }
        Assert.Equal([1L, 2L, 3L], records.Select(static r => r.Sequence));
        Assert.Equal(KvWalRecordKind.MutationBatch, records[0].Kind);

        (IReadOnlyList<KvBatchMutation> mutations, IReadOnlyList<KvRangeDeletion> ranges) = KvWalFile.DecodeMutationBatchWithRanges(records[1]);
        Assert.Equal(["k", "d"], mutations.Select(static m => Text(m.Key)));
        Assert.Equal(5, mutations[0].ExpiresAtUtc!.Value.UtcTicks);
        Assert.Null(mutations[1].Value);
        Assert.Equal(["[,b)", "[x,z)"], ranges.Select(static r => $"[{Text(r.StartInclusive)},{Text(r.EndExclusive)})"));
        Assert.Empty(KvWalFile.DecodeMutationBatchWithRanges(records[2]).Mutations);
    }

    [Fact]
    public void SstWithRangeTombstones_UsesV2AndRoundTrips_WhilePointOnlyFilesStayV1()
    {
        string ranged = WriteSst("ranged", points: ["a", "c", "q"], ranges: [("b", "d", 9), ("p", "r", 7)]);
        string rangeOnly = WriteSst("range-only", points: [], ranges: [("k", "m", 4)]);
        string pointOnly = WriteSst("points", points: ["a"], ranges: []);

        Assert.Equal(KvSstFormat.RangeDeletionVersion, FooterVersion(ranged));
        Assert.Equal(KvSstFormat.RangeDeletionVersion, FooterVersion(rangeOnly));
        Assert.Equal(KvSstFormat.CurrentVersion, FooterVersion(pointOnly));

        using (KvSstReader reader = KvSstReader.Open(ranged))
        {
            reader.VerifyAll();
            Assert.Equal(2, reader.Properties.RangeDeletionCount);
            Assert.Equal(["[b,d)@9", "[p,r)@7"], reader.RangeTombstones.Select(Describe));
            Assert.Equal(KvInternalKey.EncodeRangeEnd(Bytes("r")), reader.Properties.LargestKey);
            Assert.True(reader.TryGet(Bytes("c"), 100, out _));
        }

        using (KvSstReader reader = KvSstReader.Open(rangeOnly))
        {
            reader.VerifyAll();
            Assert.Equal(0, reader.Properties.EntryCount);
            Assert.Equal(0, reader.Properties.DataBlockCount);
            Assert.Equal(["[k,m)@4"], reader.RangeTombstones.Select(Describe));
            Assert.False(reader.TryGet(Bytes("l"), 100, out _));
            using KvSstIterator iterator = reader.CreateIterator();
            iterator.SeekToFirst();
            Assert.False(iterator.Valid);
            iterator.SeekToLast();
            Assert.False(iterator.Valid);
        }

        byte[] original = File.ReadAllBytes(ranged);
        long footerStart = original.Length - KvSstFooter.Size;
        for (int offset = (int)footerStart - 20; offset < footerStart; offset++)
        {
            byte[] corrupted = (byte[])original.Clone();
            corrupted[offset] ^= 0x10;
            File.WriteAllBytes(ranged, corrupted);
            Assert.Throws<InvalidDataException>(() => KvSstReader.Open(ranged).Dispose());
        }
    }

    [Fact]
    public void RangeTombstoneSet_MatchesBruteForce()
    {
        var random = new Random(4);
        for (int trial = 0; trial < 200; trial++)
        {
            var tombstones = Enumerable.Range(0, random.Next(0, 8)).Select(_ =>
            {
                int a = random.Next(0, 30);
                int b = random.Next(a + 1, 32);
                return new KvRangeTombstone(Bytes($"{a:D2}"), Bytes($"{b:D2}"), random.Next(1, 20));
            }).ToList();
            long snapshot = random.Next(0, 21);
            KvRangeTombstoneSet set = KvRangeTombstoneSet.Build(tombstones, snapshot);
            for (int key = 0; key < 33; key++)
            {
                foreach (string probe in new[] { $"{key:D2}", $"{key:D2}x" })
                    Assert.Equal(KvRangeTombstone.MaxCovering(tombstones, Bytes(probe), snapshot), set.MaxCovering(Bytes(probe)));
            }
        }
    }

    [Fact]
    public void DeleteRange_HidesOlderKeys_KeepsSameBatchWrites_AndRespectsSnapshots()
    {
        using KvLsmEngine engine = KvLsmEngine.Open(Path.Combine(_root, "engine"), Options(background: false));
        foreach (string key in new[] { "a", "b", "c", "d", "e" })
            engine.Apply([Put(key, "old")]);
        using KvLsmSnapshot before = engine.AcquireSnapshot();

        engine.Apply([Put("c", "same-batch")], [new KvRangeDeletion(Bytes("b"), Bytes("e"))]);
        engine.Apply([Put("d", "after")]);

        Assert.Equal(["a=old", "c=same-batch", "d=after", "e=old"], Scan(engine));
        Assert.Equal(["e=old", "d=after", "c=same-batch", "a=old"], ScanReverse(engine));
        Assert.Null(engine.Get(Bytes("b")));
        Assert.Equal(["a=old", "b=old", "c=old", "d=old", "e=old"], Scan(before));

        engine.Flush();
        Assert.Equal(["a=old", "c=same-batch", "d=after", "e=old"], Scan(engine));
        engine.CompactAll();
        Assert.Equal(["a=old", "c=same-batch", "d=after", "e=old"], Scan(engine));
        Assert.Throws<ArgumentException>(() => engine.DeleteRange(Bytes("z"), Bytes("a")));
        Assert.False(engine.IsFaulted);
    }

    [Fact]
    public void RangeOnlyMemtable_FlushesAndSurvivesWalReplayAndReopen()
    {
        string root = Path.Combine(_root, "replay");
        using (KvLsmEngine engine = KvLsmEngine.Open(root, Options(background: false)))
        {
            for (int i = 0; i < 100; i++)
                engine.Apply([Put($"k{i:D3}", "v")]);
            engine.Flush();
            engine.DeleteRange(Bytes("k010"), Bytes("k090"));
        }

        using (KvLsmEngine reopened = KvLsmEngine.Open(root, Options(background: false)))
        {
            Assert.Equal(20, Scan(reopened).Count);
            Assert.Null(reopened.Get(Bytes("k050")));
            Assert.NotNull(reopened.Get(Bytes("k090")));
            reopened.DeleteRange(Bytes("k000"), Bytes("k005"));
            reopened.Flush();
        }

        using KvLsmEngine third = KvLsmEngine.Open(root, Options(background: false));
        Assert.Equal(15, Scan(third).Count);
        Assert.Null(third.Get(Bytes("k003")));
    }

    [Fact]
    public void Compaction_DropsCoveredDataAtBaseLevel_AndKeepsTombstonesAboveOlderData()
    {
        var options = Options(background: false);
        using KvLsmEngine engine = KvLsmEngine.Open(Path.Combine(_root, "gc"), options);
        for (int i = 0; i < 2_000; i++)
            engine.Apply([Put($"k{i:D4}", "v")]);
        engine.CompactAll();

        engine.DeleteRange(Bytes("k0500"), Bytes("k1500"));
        for (int i = 0; i < options.Compaction.Level0CompactionTrigger; i++)
        {
            engine.Apply([Put($"z{i}", "f")]);
            engine.Flush();
        }
        (long entries, long deletions, long ranges) = Count(engine);
        Assert.True(ranges >= 1, "tombstone must stay above the L6 data it covers");
        Assert.Null(engine.Get(Bytes("k1000")));
        Assert.Equal(1_000 + options.Compaction.Level0CompactionTrigger, Scan(engine).Count);

        engine.CompactAll();
        (entries, deletions, ranges) = Count(engine);
        Assert.Equal(1_000 + options.Compaction.Level0CompactionTrigger, entries);
        Assert.Equal(0, deletions);
        Assert.Equal(0, ranges);
    }

    [Fact]
    public void Compaction_ClipsTombstonesAtOutputCuts_KeepingLevelsNonOverlapping()
    {
        var options = Options(background: false) with
        {
            Compaction = Options(false).Compaction with { TargetFileSizeBytes = 2 * 1024 },
        };
        using KvLsmEngine engine = KvLsmEngine.Open(Path.Combine(_root, "clip"), options);
        for (int i = 0; i < 1_500; i++)
            engine.Apply([Put($"k{i:D4}", new string('o', 30))]);
        engine.CompactAll();

        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < 1_500; i++)
            expected[$"k{i:D4}"] = new string('o', 30);
        for (int round = 0; round < options.Compaction.Level0CompactionTrigger; round++)
        {
            for (int i = round; i < 1_500; i += 7)
            {
                engine.Apply([Put($"k{i:D4}", $"n{round}")]);
                expected[$"k{i:D4}"] = $"n{round}";
            }
            int start = 200 * round;
            engine.DeleteRange(Bytes($"k{start:D4}"), Bytes($"k{start + 150:D4}"));
            foreach (string key in expected.Keys.Where(k => string.CompareOrdinal(k, $"k{start:D4}") >= 0 && string.CompareOrdinal(k, $"k{start + 150:D4}") < 0).ToList())
                expected.Remove(key);
            engine.Flush();
        }

        Assert.False(engine.IsFaulted);
        KvVersion version = engine.AcquireCurrentVersion();
        try
        {
            Assert.True(version.Files(1).Count > 1, $"L1 files: {version.Files(1).Count}");
            int filesWithPieces = version.Files(1).Count(file =>
            {
                using KvSstReader reader = KvSstReader.Open(KvLsmPaths.SstPath(engine.RootDirectory, file.FileNumber));
                return reader.Properties.RangeDeletionCount > 0;
            });
            Assert.True(filesWithPieces >= 2, $"tombstone pieces should span several L1 files, got {filesWithPieces}");
            for (int level = 1; level < KvVersion.LevelCount; level++)
            {
                IReadOnlyList<KvLsmFileMetadata> files = version.Files(level);
                for (int i = 1; i < files.Count; i++)
                    Assert.True(KvInternalKey.Compare(files[i - 1].LargestKey, files[i].SmallestKey) < 0);
            }
        }
        finally
        {
            version.Release();
        }
        Assert.Equal(expected.Select(static p => p.Key + "=" + p.Value), Scan(engine));
        foreach ((string key, string value) in expected.Where((_, i) => i % 17 == 0))
            Assert.Equal(value, Text(engine.Get(Bytes(key))!.Value));
        Assert.Null(engine.Get(Bytes("k0011"))); // 第 0 轮范围内且之后各轮都不再更新（11 mod 7 = 4）
    }

    [Theory]
    [InlineData(21, false)]
    [InlineData(22, true)]
    [InlineData(23, false)]
    public void RandomWorkloadWithRangesCompactionAndReopen_MatchesModel(int seed, bool background)
    {
        var random = new Random(seed);
        var model = new Model();
        string root = Path.Combine(_root, "random");
        var options = Options(background);
        KvLsmEngine engine = KvLsmEngine.Open(root, options);
        try
        {
            for (int step = 0; step < 2_500; step++)
            {
                int action = random.Next(100);
                if (action < 75)
                {
                    var batch = new List<KvBatchMutation>();
                    for (int i = random.Next(1, 4); i > 0; i--)
                    {
                        string key = "k" + random.Next(0, 400).ToString("D3");
                        batch.Add(random.Next(6) == 0 ? KvBatchMutation.Delete(Bytes(key)) : Put(key, $"v{step}.{i}"));
                    }
                    var ranges = new List<KvRangeDeletion>();
                    if (random.Next(4) == 0)
                    {
                        int a = random.Next(0, 400);
                        int b = Math.Min(400, a + random.Next(1, 60));
                        ranges.Add(new KvRangeDeletion(Bytes($"k{a:D3}"), Bytes($"k{b:D3}")));
                    }
                    long sequence = engine.Apply(batch, ranges);
                    model.Apply(sequence, batch, ranges);
                }
                else if (action < 85)
                {
                    engine.Flush();
                }
                else if (action < 88)
                {
                    engine.CompactAll();
                }
                else if (action < 90)
                {
                    engine.Dispose();
                    engine = KvLsmEngine.Open(root, options);
                }

                if (step % 125 == 0 || action is >= 85 and < 90)
                {
                    using KvLsmSnapshot snapshot = engine.AcquireSnapshot();
                    List<string> expected = model.Visible(snapshot.Sequence);
                    Assert.Equal(expected, Scan(snapshot));
                    List<string> reverse = ScanReverse(snapshot);
                    reverse.Reverse();
                    Assert.Equal(expected, reverse);
                    for (int probe = 0; probe < 40; probe++)
                    {
                        string key = "k" + random.Next(0, 400).ToString("D3");
                        string? want = expected.FirstOrDefault(e => e.StartsWith(key + "=", StringComparison.Ordinal));
                        KvEntry? got = snapshot.Get(Bytes(key));
                        Assert.Equal(want, got is null ? null : key + "=" + Text(got.Value));
                    }
                }
            }
            Assert.False(engine.IsFaulted);
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public void Clear_DropsEverythingAdvancesGenerationAndKeepsOldSnapshotsReadable()
    {
        string root = Path.Combine(_root, "clear");
        using (KvLsmEngine engine = KvLsmEngine.Open(root, Options(background: true) with { Generation = 3 }))
        {
            for (int i = 0; i < 3_000; i++)
                engine.Apply([Put($"k{i:D4}", new string('v', 40))]);
            engine.Flush();
            engine.Apply([Put("unflushed", "x")]);
            using KvLsmSnapshot before = engine.AcquireSnapshot();

            Assert.Equal(4, engine.Clear());
            Assert.Equal(4, engine.Generation);
            Assert.Empty(Scan(engine));
            Assert.Null(engine.Get(Bytes("unflushed")));
            Assert.Equal(3_001, Scan(before).Count);

            long next = engine.Apply([Put("after", "clear")]);
            Assert.True(next > before.Sequence);
            Assert.Equal(["after=clear"], Scan(engine));
        }

        using KvLsmEngine reopened = KvLsmEngine.Open(root, Options(background: false));
        Assert.Equal(4, reopened.Generation);
        Assert.Equal(["after=clear"], Scan(reopened));
        Assert.All(Directory.GetFiles(KvLsmPaths.SstDirectory(root)), file =>
        {
            using KvSstReader reader = KvSstReader.Open(file);
            Assert.Equal(4, reader.Properties.Generation);
        });
    }

    private string WriteSst(string name, string[] points, (string Start, string End, long Sequence)[] ranges)
    {
        string path = Path.Combine(_root, name + KvSstFormat.FileExtension);
        using var writer = new KvSstWriter(path, new KvSstWriterOptions { SyncDirectory = false });
        foreach (string point in points)
            writer.Add(KvInternalKey.Encode(Bytes(point), 5, KvEntryKind.Put), "v"u8);
        foreach ((string start, string end, long sequence) in ranges)
            writer.AddRangeTombstone(new KvRangeTombstone(Bytes(start), Bytes(end), sequence));
        writer.Finish();
        return path;
    }

    private static int FooterVersion(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return KvSstFooter.Read(bytes.AsSpan(bytes.Length - KvSstFooter.Size)).FormatVersion;
    }

    private (long Entries, long Deletions, long Ranges) Count(KvLsmEngine engine)
    {
        KvVersion version = engine.AcquireCurrentVersion();
        try
        {
            long entries = 0, deletions = 0, ranges = 0;
            foreach (KvLsmFileMetadata file in version.AllFiles)
            {
                using KvSstReader reader = KvSstReader.Open(KvLsmPaths.SstPath(engine.RootDirectory, file.FileNumber));
                entries += reader.Properties.EntryCount;
                deletions += reader.Properties.DeletionCount;
                ranges += reader.Properties.RangeDeletionCount;
            }
            return (entries, deletions, ranges);
        }
        finally
        {
            version.Release();
        }
    }

    private static KvLsmEngineOptions Options(bool background)
        => new()
        {
            BackgroundFlush = background,
            WriteBufferBytes = 64 * 1024,
            SyncWalOnEveryWrite = false,
            MaxImmutableMemTables = 4,
            Level0StopWritesTrigger = 12,
            UtcNowTicks = static () => 0,
            SstWriterOptions = new KvSstWriterOptions { BlockSize = 1024, SyncDirectory = false },
            Compaction = new KvCompactionOptions
            {
                Level0CompactionTrigger = 4,
                MaxBytesForLevelBase = 128 * 1024,
                LevelSizeMultiplier = 4,
                TargetFileSizeBytes = 32 * 1024,
            },
        };

    private static List<string> Scan(KvLsmEngine engine)
    {
        using KvLsmSnapshot snapshot = engine.AcquireSnapshot();
        return Scan(snapshot);
    }

    private static List<string> ScanReverse(KvLsmEngine engine)
    {
        using KvLsmSnapshot snapshot = engine.AcquireSnapshot();
        return ScanReverse(snapshot);
    }

    private static List<string> Scan(KvLsmSnapshot snapshot)
    {
        var result = new List<string>();
        using KvSnapshotIterator iterator = snapshot.CreateIterator();
        for (iterator.SeekToFirst(); iterator.Valid; iterator.MoveNext())
            result.Add(Text(iterator.Key) + "=" + Text(iterator.Value));
        return result;
    }

    private static List<string> ScanReverse(KvLsmSnapshot snapshot)
    {
        var result = new List<string>();
        using KvSnapshotIterator iterator = snapshot.CreateIterator();
        for (iterator.SeekToLast(); iterator.Valid; iterator.MovePrev())
            result.Add(Text(iterator.Key) + "=" + Text(iterator.Value));
        return result;
    }

    private static string Describe(KvRangeTombstone tombstone)
        => $"[{Text(tombstone.Start)},{Text(tombstone.End)})@{tombstone.Sequence}";

    private static KvBatchMutation Put(string key, string value) => KvBatchMutation.Put(Bytes(key), Bytes(value));

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static string Text(ReadOnlySpan<byte> value) => Encoding.UTF8.GetString(value);

    private static string Text(ReadOnlyMemory<byte> value) => Encoding.UTF8.GetString(value.Span);

    private static string Text(byte[] value) => Encoding.UTF8.GetString(value);

    private sealed class Model
    {
        private readonly List<(long Sequence, string Key, string? Value)> _points = [];
        private readonly List<(long Sequence, string Start, string End)> _ranges = [];

        internal void Apply(long sequence, IReadOnlyList<KvBatchMutation> batch, IReadOnlyList<KvRangeDeletion> ranges)
        {
            foreach (KvRangeDeletion range in ranges)
                _ranges.Add((sequence, Text(range.StartInclusive), Text(range.EndExclusive)));
            var last = new Dictionary<string, KvBatchMutation>(StringComparer.Ordinal);
            foreach (KvBatchMutation mutation in batch)
                last[Text(mutation.Key)] = mutation;
            foreach ((string key, KvBatchMutation mutation) in last)
                _points.Add((sequence, key, mutation.Value is null ? null : Text(mutation.Value)));
        }

        internal List<string> Visible(long snapshot)
            => _points
                .Where(p => p.Sequence <= snapshot)
                .GroupBy(static p => p.Key)
                .Select(static g => g.MaxBy(static p => p.Sequence))
                .Where(p => p.Value is not null && !_ranges.Any(r =>
                    r.Sequence <= snapshot
                    && r.Sequence > p.Sequence
                    && string.CompareOrdinal(p.Key, r.Start) >= 0
                    && string.CompareOrdinal(p.Key, r.End) < 0))
                .OrderBy(static p => p.Key, StringComparer.Ordinal)
                .Select(static p => p.Key + "=" + p.Value)
                .ToList();
    }
}
