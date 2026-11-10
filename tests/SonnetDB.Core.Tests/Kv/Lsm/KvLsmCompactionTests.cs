using System.Text;
using SonnetDB.Kv;
using SonnetDB.Kv.Lsm;
using Xunit;

namespace SonnetDB.Core.Tests.Kv.Lsm;

public sealed class KvLsmCompactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-kv-compact-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticCompaction_BoundsLevel0_KeepsLevelInvariants_AndPreservesData(bool background)
    {
        var options = SmallOptions(background);
        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var random = new Random(3);
        using (KvLsmEngine engine = KvLsmEngine.Open(_root, options))
        {
            for (int i = 0; i < 25_000; i++)
            {
                string key = $"k{random.Next(0, 6_000):D5}";
                if (random.Next(8) == 0)
                {
                    engine.Apply([KvBatchMutation.Delete(Bytes(key))]);
                    expected.Remove(key);
                }
                else
                {
                    string value = $"v{i}" + new string('x', 60);
                    engine.Apply([Put(key, value)]);
                    expected[key] = value;
                }
            }
            engine.Flush();
            WaitForCompactionIdle(engine, options);

            KvLsmEngineStats stats = engine.GetStats();
            Assert.True(stats.CompactionCount > 0);
            Assert.True(stats.FileCountByLevel[0] < options.Compaction.Level0CompactionTrigger, $"L0={stats.FileCountByLevel[0]}");
            Assert.True(stats.FileCountByLevel.Skip(1).Sum() > 1, "expected multiple files below L0");
            AssertLevelInvariants(engine);
            AssertMatches(engine, expected);
        }

        using KvLsmEngine reopened = KvLsmEngine.Open(_root, options);
        AssertLevelInvariants(reopened);
        AssertMatches(reopened, expected);
    }

    /// <summary>
    /// 请求读取预算占满且没有排队名额时，flush 后的新表打开与 compaction 输入读取都走维护通道：
    /// 请求过载不能让后台维护失败并把引擎置为 faulted（只读）。
    /// </summary>
    [Fact]
    public void CompactAll_WhileRequestReadBudgetSaturated_UsesMaintenanceLane()
    {
        using var budget = new KvDiskReadBudget(maxConcurrentReads: 1, maxQueuedReads: 0, readWaitTimeout: TimeSpan.Zero);
        using KvLsmEngine engine = KvLsmEngine.Open(_root, SmallOptions(background: false) with { ReadBudget = budget });
        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < 500; i++)
                engine.Apply([Put($"k{i:D4}", $"r{round}")]);
            engine.Flush();
        }

        using (budget.Acquire(CancellationToken.None))
        {
            Assert.Throws<SonnetDB.Exceptions.KvReadOverloadedException>(() => budget.Acquire(CancellationToken.None).Dispose());
            for (int i = 0; i < 500; i++)
                engine.Apply([Put($"k{i:D4}", "r3")]);
            engine.Flush();
            engine.CompactAll();
            Assert.Null(engine.Fault);
            Assert.Equal(0, budget.MaintenanceActiveReads);
        }

        Assert.Equal(0, engine.GetStats().FileCountByLevel.Take(KvVersion.LevelCount - 1).Sum());
        Assert.Equal("r3", Text(engine.Get(Bytes("k0499"))!.Value));
    }

    [Fact]
    public void CompactAll_KeepsOnlyNewestLiveVersions_AndDropsTombstones()
    {
        using KvLsmEngine engine = KvLsmEngine.Open(_root, SmallOptions(background: false));
        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < 2_000; i++)
                engine.Apply([Put($"k{i:D4}", $"r{round}")]);
            engine.Flush();
        }
        for (int i = 0; i < 2_000; i += 2)
            engine.Apply([KvBatchMutation.Delete(Bytes($"k{i:D4}"))]);

        engine.CompactAll();

        (long entries, long deletions) = CountEntries(engine);
        Assert.Equal(1_000, entries);
        Assert.Equal(0, deletions);
        KvLsmEngineStats stats = engine.GetStats();
        Assert.Equal(0, stats.FileCountByLevel.Take(KvVersion.LevelCount - 1).Sum());
        Assert.Null(engine.Get(Bytes("k0000")));
        Assert.Equal("r2", Text(engine.Get(Bytes("k0001"))!.Value));
    }

    [Fact]
    public void TombstoneAboveOlderData_IsKeptUntilItReachesTheBaseLevel()
    {
        var options = SmallOptions(background: false);
        using KvLsmEngine engine = KvLsmEngine.Open(_root, options);
        engine.Apply([Put("victim", "old"), Put("other", "x")]);
        engine.CompactAll(); // 数据进入 L6

        engine.Apply([KvBatchMutation.Delete(Bytes("victim"))]);
        for (int i = 0; i < options.Compaction.Level0CompactionTrigger; i++)
        {
            engine.Apply([Put($"filler{i}", "f")]);
            engine.Flush(); // 触发 L0→L1：victim 仍在 L6，墓碑必须保留
        }

        Assert.Null(engine.Get(Bytes("victim")));
        Assert.True(CountEntries(engine).Deletions >= 1);

        engine.CompactAll();
        Assert.Null(engine.Get(Bytes("victim")));
        Assert.Equal(0, CountEntries(engine).Deletions);
    }

    [Fact]
    public void ExpiredEntries_AreDroppedOnlyAfterTheirDeadline_AndPinnedSnapshotsStillSeeThem()
    {
        long clock = 50;
        var options = SmallOptions(background: false) with { UtcNowTicks = () => Volatile.Read(ref clock) };
        using KvLsmEngine engine = KvLsmEngine.Open(_root, options);
        engine.Apply([Put("ttl", "soon", expires: 100), Put("forever", "x")]);

        engine.CompactAll();
        Assert.Equal(2, CountEntries(engine).Entries);

        using KvLsmSnapshot pinned = engine.AcquireSnapshot(new DateTimeOffset(60, TimeSpan.Zero));
        Volatile.Write(ref clock, 200);
        engine.CompactAll();
        Assert.Equal(1, CountEntries(engine).Entries);

        Assert.Equal("soon", Text(pinned.Get(Bytes("ttl"))!.Value));
        using KvLsmSnapshot later = engine.AcquireSnapshot(new DateTimeOffset(200, TimeSpan.Zero));
        Assert.Null(later.Get(Bytes("ttl")));
    }

    [Fact]
    public void SnapshotPinsInputFiles_UntilReleased()
    {
        using KvLsmEngine engine = KvLsmEngine.Open(_root, SmallOptions(background: false));
        for (int i = 0; i < 500; i++)
            engine.Apply([Put($"k{i:D3}", "before")]);
        engine.Flush();
        string[] before = SstFiles();

        KvLsmSnapshot snapshot = engine.AcquireSnapshot();
        for (int i = 0; i < 500; i++)
            engine.Apply([Put($"k{i:D3}", "after")]);
        engine.CompactAll();

        Assert.All(before, file => Assert.True(File.Exists(file), file));
        Assert.Equal("before", Text(snapshot.Get(Bytes("k123"))!.Value));
        using (KvSnapshotIterator iterator = snapshot.CreateIterator())
        {
            snapshot.Dispose();
            iterator.SeekToFirst();
            Assert.Equal("before", Text(iterator.Value));
        }

        engine.Apply([Put("trigger", "x")]);
        engine.Flush();
        Assert.All(before, file => Assert.False(File.Exists(file), file));
        Assert.Equal("after", Text(engine.Get(Bytes("k123"))!.Value));
    }

    [Theory]
    [InlineData((int)KvLsmEnginePhase.CompactionAfterOutputsWritten)]
    [InlineData((int)KvLsmEnginePhase.CompactionAfterManifestCommit)]
    public void CrashDuringCompaction_ReopenKeepsDataAndRemovesLeftovers(int phaseValue)
    {
        var phase = (KvLsmEnginePhase)phaseValue;
        bool armed = false;
        var options = SmallOptions(background: false) with
        {
            TestHook = p => { if (armed && p == phase) throw new IOException("injected crash"); },
        };
        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using (KvLsmEngine engine = KvLsmEngine.Open(_root, options))
        {
            for (int round = 0; round < 3; round++)
            {
                for (int i = 0; i < 800; i++)
                {
                    engine.Apply([Put($"k{i:D3}", $"r{round}")]);
                    expected[$"k{i:D3}"] = $"r{round}";
                }
                engine.Flush();
            }
            armed = true;
            Assert.Throws<IOException>(() => engine.CompactAll());
            Assert.True(engine.IsFaulted);
        }

        using KvLsmEngine reopened = KvLsmEngine.Open(_root, SmallOptions(background: false));
        AssertMatches(reopened, expected);
        using var lease = new VersionLease(reopened);
        var live = lease.Version.AllFiles.Select(static f => f.FileNumber).ToHashSet();
        Assert.All(SstFiles(), file =>
        {
            Assert.True(KvLsmPaths.TryParseSstFileName(Path.GetFileName(file), out long number));
            Assert.Contains(number, live);
        });
    }

    [Fact]
    public void BlockCache_ServesRepeatedReadsAndRespectsCapacity()
    {
        var cache = new KvBlockCache(64 * 1024);
        using (KvLsmEngine engine = KvLsmEngine.Open(_root, SmallOptions(background: false) with { BlockCache = cache }))
        {
            for (int i = 0; i < 3_000; i++)
                engine.Apply([Put($"k{i:D4}", new string('v', 100))]);
            engine.CompactAll();

            long missesBefore = cache.Misses;
            for (int i = 0; i < 50; i++)
                Assert.NotNull(engine.Get(Bytes("k1500")));
            Assert.True(cache.Hits >= 49, $"hits={cache.Hits}");
            Assert.True(cache.Misses - missesBefore <= 1);

            using KvLsmSnapshot snapshot = engine.AcquireSnapshot();
            using KvSnapshotIterator iterator = snapshot.CreateIterator();
            int count = 0;
            for (iterator.SeekToFirst(); iterator.Valid; iterator.MoveNext())
                count++;
            Assert.Equal(3_000, count);
        }

        Assert.True(cache.UsageBytes <= cache.CapacityBytes);
        Assert.True(cache.Evictions > 0);
    }

    [Fact]
    public void LevelIterator_TraversesManyFilesInBothDirections()
    {
        var options = SmallOptions(background: false) with
        {
            Compaction = SmallOptions(false).Compaction with { TargetFileSizeBytes = 4 * 1024 },
        };
        using KvLsmEngine engine = KvLsmEngine.Open(_root, options);
        var keys = Enumerable.Range(0, 2_000).Select(static i => $"k{i:D4}").ToList();
        foreach (string key in keys)
            engine.Apply([Put(key, key)]);
        engine.CompactAll();
        Assert.True(engine.GetStats().FileCountByLevel[KvVersion.LevelCount - 1] > 5);

        using KvLsmSnapshot snapshot = engine.AcquireSnapshot();
        using KvSnapshotIterator iterator = snapshot.CreateIterator();
        var forward = new List<string>();
        for (iterator.SeekToFirst(); iterator.Valid; iterator.MoveNext())
            forward.Add(Text(iterator.Key));
        Assert.Equal(keys, forward);
        var backward = new List<string>();
        for (iterator.SeekToLast(); iterator.Valid; iterator.MovePrev())
            backward.Add(Text(iterator.Key));
        backward.Reverse();
        Assert.Equal(keys, backward);

        iterator.SeekForPrev(Bytes("k1000~"));
        Assert.Equal("k1000", Text(iterator.Key));
        iterator.MoveNext();
        Assert.Equal("k1001", Text(iterator.Key));
        iterator.Seek(Bytes("k0999~"));
        Assert.Equal("k1000", Text(iterator.Key));
        iterator.MovePrev();
        Assert.Equal("k0999", Text(iterator.Key));
    }

    private static KvLsmEngineOptions SmallOptions(bool background)
        => new()
        {
            BackgroundFlush = background,
            WriteBufferBytes = 64 * 1024,
            SyncWalOnEveryWrite = false,
            MaxImmutableMemTables = 4,
            Level0StopWritesTrigger = 12,
            SstWriterOptions = new KvSstWriterOptions { BlockSize = 1024, SyncDirectory = false },
            Compaction = new KvCompactionOptions
            {
                Level0CompactionTrigger = 4,
                MaxBytesForLevelBase = 256 * 1024,
                LevelSizeMultiplier = 4,
                TargetFileSizeBytes = 64 * 1024,
            },
        };

    private static void WaitForCompactionIdle(KvLsmEngine engine, KvLsmEngineOptions options)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            KvLsmEngineStats stats = engine.GetStats();
            bool levelsOk = Enumerable.Range(1, KvVersion.LevelCount - 2)
                .All(level => stats.BytesByLevel[level] <= options.Compaction.TargetBytes(level));
            if (stats.ImmutableMemTables == 0 && stats.FileCountByLevel[0] < options.Compaction.Level0CompactionTrigger && levelsOk)
                return;
            Thread.Sleep(20);
        }
        Assert.Fail("background compaction did not settle in time");
    }

    private static void AssertLevelInvariants(KvLsmEngine engine)
    {
        KvVersion version = engine.AcquireCurrentVersion();
        try
        {
            for (int level = 1; level < KvVersion.LevelCount; level++)
            {
                IReadOnlyList<KvLsmFileMetadata> files = version.Files(level);
                for (int i = 1; i < files.Count; i++)
                {
                    Assert.True(
                        KvInternalKey.UserKey(files[i - 1].LargestKey).SequenceCompareTo(KvInternalKey.UserKey(files[i].SmallestKey)) < 0,
                        $"L{level} files #{files[i - 1].FileNumber} and #{files[i].FileNumber} share or overlap user keys");
                }
            }
        }
        finally
        {
            version.Release();
        }
    }

    private static void AssertMatches(KvLsmEngine engine, SortedDictionary<string, string> expected)
    {
        using KvLsmSnapshot snapshot = engine.AcquireSnapshot();
        var actual = new List<string>();
        using (KvSnapshotIterator iterator = snapshot.CreateIterator())
        {
            for (iterator.SeekToFirst(); iterator.Valid; iterator.MoveNext())
                actual.Add(Text(iterator.Key) + "=" + Text(iterator.Value));
        }
        Assert.Equal(expected.Select(static p => p.Key + "=" + p.Value), actual);
        foreach ((string key, string value) in expected.Take(300))
            Assert.Equal(value, Text(snapshot.Get(Bytes(key))!.Value));
    }

    private (long Entries, long Deletions) CountEntries(KvLsmEngine engine)
    {
        KvVersion version = engine.AcquireCurrentVersion();
        try
        {
            long entries = 0;
            long deletions = 0;
            foreach (KvLsmFileMetadata file in version.AllFiles)
            {
                using KvSstReader reader = KvSstReader.Open(KvLsmPaths.SstPath(_root, file.FileNumber));
                entries += reader.Properties.EntryCount;
                deletions += reader.Properties.DeletionCount;
            }
            return (entries, deletions);
        }
        finally
        {
            version.Release();
        }
    }

    private string[] SstFiles() => Directory.GetFiles(KvLsmPaths.SstDirectory(_root), "*" + KvSstFormat.FileExtension);

    private static KvBatchMutation Put(string key, string value, long? expires = null)
        => new(Bytes(key), Bytes(value), expires is null ? null : new DateTimeOffset(expires.Value, TimeSpan.Zero));

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static string Text(ReadOnlySpan<byte> value) => Encoding.UTF8.GetString(value);

    private static string Text(ReadOnlyMemory<byte> value) => Encoding.UTF8.GetString(value.Span);

    private sealed class VersionLease(KvLsmEngine engine) : IDisposable
    {
        internal KvVersion Version { get; } = engine.AcquireCurrentVersion();

        public void Dispose() => Version.Release();
    }
}
