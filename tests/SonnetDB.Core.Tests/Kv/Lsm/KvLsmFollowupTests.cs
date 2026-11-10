using System.Buffers.Binary;
using System.Diagnostics;
using SonnetDB.Backup;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Kv.Lsm;
using Xunit;

namespace SonnetDB.Core.Tests.Kv.Lsm;

public sealed class KvLsmFollowupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-lsm-followup-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedSstV3_PrefixBloomAndStorageRange_RespectRangeTombstones(bool ranged)
    {
        string path = Path.Combine(_root, "prefix" + KvSstFormat.FileExtension);
        using (var writer = new KvSstWriter(path, new KvSstWriterOptions { SharedPrefixes = true, SyncDirectory = false }))
        {
            writer.Add(KvInternalKey.Encode(Key(1, "a"u8), 1, KvEntryKind.Put), [1]);
            writer.Add(KvInternalKey.Encode(Key(100, "a"u8), 2, KvEntryKind.Put), [2]);
            if (ranged) writer.AddRangeTombstone(new KvRangeTombstone(KvLsmStore.Prefix(40), KvLsmStore.Prefix(60), 3));
            writer.Finish();
        }
        using KvSstReader reader = KvSstReader.Open(path);
        Assert.True(reader.Properties.SharedPrefixes);
        Assert.Equal(1, reader.Properties.MinStorageId);
        Assert.Equal(100, reader.Properties.MaxStorageId);
        Assert.True(reader.MayContainStoragePrefix(KvLsmStore.Prefix(1)));
        Assert.True(reader.MayContainStoragePrefix(KvLsmStore.Prefix(100)));
        if (ranged) Assert.True(reader.MayContainStoragePrefix(KvLsmStore.Prefix(50)));
        int rejected = 0;
        for (int id = 2; id < 100; id++)
            if (!reader.MayContainStoragePrefix(KvLsmStore.Prefix(id))) rejected++;
        Assert.True(rejected >= (ranged ? 60 : 80), $"Only {rejected} unrelated prefixes were rejected.");
        Assert.False(reader.MayContainStoragePrefix(KvLsmStore.Prefix(101)));
        reader.VerifyAll();
        byte[] footer = File.ReadAllBytes(path)[^KvSstFooter.Size..];
        Assert.Equal(KvSstFormat.SharedPrefixVersion, BinaryPrimitives.ReadInt32LittleEndian(footer.AsSpan(8)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PinnedBackup_SurvivesCompactionOrClear_AndExcludesLaterWalBytes(bool clear)
    {
        string source = Path.Combine(_root, "source", "engine");
        string target = Path.Combine(_root, "backup");
        using KvLsmEngine engine = KvLsmEngine.Open(source, new KvLsmEngineOptions { BackgroundFlush = false, SyncWalOnEveryWrite = false });
        engine.Apply([KvBatchMutation.Put("a"u8.ToArray(), [1])]);
        engine.Flush();
        engine.Apply([KvBatchMutation.Put("tail"u8.ToArray(), [2])]);
        using var backup = new KvLsmBackupSet();
        backup.Engines.Add(engine.AcquireBackup());
        if (clear) engine.Clear();
        engine.Apply([KvBatchMutation.Put("a"u8.ToArray(), [3]), KvBatchMutation.Put("late"u8.ToArray(), [4])]);
        engine.CompactAll();
        IReadOnlyList<string> copied = backup.CopyTo(Path.Combine(_root, "source"), target, null);
        Assert.Contains(Path.Combine("engine", "CURRENT"), copied);
        using KvLsmEngine restored = KvLsmEngine.Open(Path.Combine(target, "engine"), new KvLsmEngineOptions { BackgroundFlush = false });
        Assert.Equal([1], restored.Get("a"u8)!.Value.ToArray());
        Assert.Equal([2], restored.Get("tail"u8)!.Value.ToArray());
        Assert.Null(restored.Get("late"u8));
    }

    [Theory]
    [InlineData((int)KvCleanupThrottleReason.CpuPressure)]
    [InlineData((int)KvCleanupThrottleReason.MemoryPressure)]
    [InlineData((int)KvCleanupThrottleReason.ActiveQueries)]
    public void BackgroundCompaction_PausesAndResumes_WhileFlushContinues(int reason)
    {
        using var scheduler = new KvLsmBackgroundScheduler(1, 1, "Followup");
        int paused = 1;
        using KvLsmEngine engine = KvLsmEngine.Open(_root, new KvLsmEngineOptions
        {
            Scheduler = scheduler,
            SyncWalOnEveryWrite = false,
            Compaction = new KvCompactionOptions { Level0CompactionTrigger = 2 },
            Level0StopWritesTrigger = 5,
            CompactionPauseReason = () => Volatile.Read(ref paused) == 1 ? (KvCleanupThrottleReason)reason : KvCleanupThrottleReason.None,
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        for (int batch = 0; batch < 2; batch++)
        {
            engine.Apply([KvBatchMutation.Put([(byte)batch], [1])], deadline.Token);
            engine.Flush(deadline.Token);
        }
        Wait(() => engine.CompactionPauseCount > 0);
        Assert.Equal(2, engine.GetStats().FileCountByLevel[0]);
        Volatile.Write(ref paused, 0);
        Wait(() => engine.GetStats().CompactionCount > 0);
        Assert.Null(engine.Fault);
    }

    [Fact]
    public void ProjectedRead_LargeUnmaterializedValue_IsBorrowedAndDoesNotWrite()
    {
        using KvKeyspace keyspace = KvKeyspace.Open("large", _root,
            KvOptions.Default with { StorageEngine = KvStorageEngine.Lsm, SyncWalOnEveryWrite = false });
        byte[] value = new byte[3 * 1024 * 1024];
        value[0] = 42;
        keyspace.Put("r1", value);
        long sequence = keyspace.LastSequence;
        long before = GC.GetAllocatedBytesForCurrentThread();
        keyspace.ReadStablePrefixProjectedPages("r"u8, 4, static (_, payload) => (int)payload[0], page => Assert.Equal([42], page));
        Assert.Equal("42", keyspace.ReadProjectedValue("r1"u8.ToArray(), static payload => payload[0].ToString()));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024 * 1024, $"Borrowed memtable projections allocated {allocated} bytes.");
        Assert.Equal(sequence, keyspace.LastSequence);
    }

    private static byte[] Key(long id, ReadOnlySpan<byte> suffix) => [.. KvLsmStore.Prefix(id), .. suffix];

    [Fact]
    public void PinnedBackup_DisposedAfterEngineClose_ReleasesSstReaderHandles()
    {
        using KvLsmEngine engine = KvLsmEngine.Open(_root, new KvLsmEngineOptions { BackgroundFlush = false });
        engine.Apply([KvBatchMutation.Put("a"u8.ToArray(), [1])]);
        engine.Flush();
        using KvLsmEngineBackup backup = engine.AcquireBackup();
        string file = Assert.Single(Directory.GetFiles(Path.Combine(_root, "sst"), "*.SDBKVSST"));
        engine.Dispose();
        backup.Dispose();
        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    [Fact]
    public void BackupService_ConcurrentRawKvCompaction_DoesNotChangePinnedBackup()
    {
        string database = Path.Combine(_root, "database");
        using Tsdb db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = database,
            Kv = KvOptions.Default with { StorageEngine = KvStorageEngine.Lsm },
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        });
        KvKeyspace keyspace = db.Keyspaces.Open("pinned");
        keyspace.Put("original", [1]);
        int changed = 0;
        var service = new BackupService();
        service.AfterFileCopiedTestHook = _ =>
        {
            if (Interlocked.Exchange(ref changed, 1) != 0) return;
            keyspace.Put("original", [2]);
            keyspace.Put("late", [3]);
            keyspace.Compact();
        };
        string destination = Path.Combine(_root, "backup-service");
        service.Create(db, new BackupCreateOptions { DestinationDirectory = destination });
        Assert.Equal(1, changed);
        Assert.True(service.Verify(destination).IsValid);
        using Tsdb restored = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = destination,
            Kv = KvOptions.Default with { StorageEngine = KvStorageEngine.Lsm },
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        });
        Assert.Equal([1], restored.Keyspaces.Open("pinned").Get("original"));
        Assert.Null(restored.Keyspaces.Open("pinned").Get("late"));
    }

    private static void Wait(Func<bool> predicate)
    {
        var clock = Stopwatch.StartNew();
        for (int poll = 0; poll < 500 && clock.Elapsed < TimeSpan.FromSeconds(5); poll++)
        {
            if (predicate()) return;
            Thread.Sleep(10);
        }
        Assert.True(predicate(), "Bounded background maintenance wait expired.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
