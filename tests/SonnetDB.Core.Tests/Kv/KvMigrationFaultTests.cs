using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Kv.Lsm;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

/// <summary>G12：隔离空间与真实临时文件 I/O 故障，重复重开对账不依赖填满宿主磁盘。</summary>
[Collection(nameof(KvLegacyMigrationTests))]
public sealed class KvMigrationFaultTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sndb-kv-migration-io-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        KvMigrationFileIo.TestHook = null;
        KvMigrationFileIo.AvailableSpaceTestHook = null;
        KvLegacyMigration.PhaseTestHook = null;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Standalone_InsufficientSpace_UsesIntactLegacyThenMigratesOnRecovery()
    {
        string path = Path.Combine(_root, "standalone-space");
        Expected expected = Seed(path);
        KvMigrationFileIo.AvailableSpaceTestHook = _ => 0;
        using (KvKeyspace fallback = KvKeyspace.Open("m", path, Options()))
        {
            Assert.Equal(KvStorageEngine.Legacy, fallback.StorageEngine);
            Verify(fallback, expected, preserveVersions: true);
            Assert.False(Directory.Exists(Path.Combine(path, KvLegacyMigration.StagingDirectoryName)));
        }
        KvMigrationFileIo.AvailableSpaceTestHook = null;
        ReopenAndVerify(path, expected, KvStorageEngine.Lsm);
    }

    [Fact]
    public void Shared_InsufficientSpace_DoesNotCreateAnImportedKeyspace()
    {
        string path = Path.Combine(_root, "shared-space");
        Expected expected = Seed(path);
        using var budget = new KvDiskReadBudget(2);
        using KvLsmStore store = KvLsmStore.Open(Path.Combine(_root, "lsm"), Options(), budget);
        KvMigrationFileIo.AvailableSpaceTestHook = _ => 0;
        Assert.Null(KvLegacyMigration.TryMigrateIntoShared("m", path, store, Options(), budget, out string? reason));
        Assert.Contains("migration skipped", reason, StringComparison.Ordinal);
        Assert.Empty(store.List());
        using (KvKeyspace fallback = KvKeyspace.Open("m", path, Options(KvStorageEngine.Legacy)))
            Verify(fallback, expected, preserveVersions: true);
        KvMigrationFileIo.AvailableSpaceTestHook = null;
        long id = KvLegacyMigration.TryMigrateIntoShared("m", path, store, Options(), budget, out _)!.Value;
        using KvKeyspace migrated = KvKeyspace.OpenShared("m", path, store, id, Options(), budget);
        Verify(migrated, expected, preserveVersions: false);
    }

    [Fact]
    public void MigratedStandalone_WalSyncUnknown_SealsWritesAndReconcilesTheCompleteEntry()
    {
        string path = Path.Combine(_root, "migration-then-unknown");
        Expected expected = Seed(path);
        DateTimeOffset expiry = DateTimeOffset.UtcNow.AddHours(3);
        byte[] value = Enumerable.Range(0, 337).Select(index => (byte)(index ^ 0x5A)).ToArray();
        using (KvKeyspace migrated = KvKeyspace.Open("m", path, Options()))
        {
            migrated.WalSyncTestHook = () => throw new IOException("isolated WAL sync failure after a complete appended record");
            IOException unknown = Assert.Throws<IOException>(() => migrated.Put("unknown", value, expiry));
            Assert.True(migrated.IsWriteCommitOutcomeUnknown(unknown));
            migrated.WalSyncTestHook = null;
            IOException sealedWrite = Assert.Throws<IOException>(() => migrated.Put("must-not-commit", [99]));
            Assert.True(migrated.IsWriteCommitOutcomeUnknown(sealedWrite));
            Assert.Contains("read-only", sealedWrite.Message, StringComparison.Ordinal);
        }
        var entries = new Dictionary<string, KvEntry>(expected.Entries, StringComparer.Ordinal)
        {
            ["unknown"] = new KvEntry("unknown"u8.ToArray(), value, expected.Sequence + 1, expiry),
        };
        ReopenAndVerify(path, expected with { Sequence = expected.Sequence + 1, Entries = entries }, KvStorageEngine.Lsm);
        using KvKeyspace reconciled = KvKeyspace.Open("m", path, Options());
        Assert.Null(reconciled.Get("must-not-commit"));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("space")]
    [InlineData("sync")]
    [InlineData("unknown")]
    public void Standalone_StagingSstIoFailure_PreservesLegacyAndReopensThreeTimes(string fault)
    {
        string path = Path.Combine(_root, "staging-sst-" + fault);
        Expected expected = Seed(path);
        string staging = Path.Combine(path, KvLegacyMigration.StagingDirectoryName);
        string? faultPath = null;
        byte[]? writtenPrefix = null;
        int triggered = 0;
        KvMigrationIoPhase target = fault switch
        {
            "partial" or "space" => KvMigrationIoPhase.BeforeWrite,
            "sync" => KvMigrationIoPhase.BeforeSync,
            "unknown" => KvMigrationIoPhase.AfterReplace,
            _ => throw new InvalidOperationException("Unknown SST fixture fault."),
        };
        KvMigrationFileIo.TestHook = (phase, file, stream, content) =>
        {
            if (phase != target || !file.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !file.EndsWith(KvSstFormat.FileExtension + (phase == KvMigrationIoPhase.AfterReplace ? "" : ".tmp"), StringComparison.Ordinal))
                return;
            Assert.Equal(1, ++triggered);
            faultPath = file;
            if (fault is "partial" or "space")
            {
                Assert.NotNull(stream);
                Assert.True(content.Length > 2);
                writtenPrefix = content[..(fault == "space" ? 1 : content.Length / 2)].ToArray();
                stream.Write(writtenPrefix);
                stream.Flush(flushToDisk: true);
                stream.Dispose();
                Assert.Equal(writtenPrefix, File.ReadAllBytes(file));
                Assert.True(writtenPrefix.Length < content.Length, "SST fixture must fail before the entire payload is written.");
            }
            else if (fault == "sync")
            {
                Assert.NotNull(stream);
                stream.Flush(flushToDisk: false);
                stream.Dispose();
                using KvSstReader reader = KvSstReader.Open(file);
                Assert.Equal(expected.Entries.Count, reader.Properties.EntryCount);
            }
            else
            {
                Assert.Null(stream);
                Assert.True(File.Exists(file));
                Assert.False(File.Exists(file + ".tmp"));
                using KvSstReader reader = KvSstReader.Open(file);
                Assert.Equal(expected.Entries.Count, reader.Properties.EntryCount);
            }
            throw fault == "space"
                ? new IOException("isolated staging SST disk-full fixture", unchecked((int)0x80070070))
                : new IOException("isolated staging SST " + fault + " fixture");
        };
        IOException error;
        try
        {
            error = Assert.Throws<IOException>(() => KvKeyspace.Open("m", path, Options()).Dispose());
        }
        finally
        {
            KvMigrationFileIo.TestHook = null;
        }
        Assert.Equal(1, triggered);
        Assert.NotNull(faultPath);
        Assert.Contains("staging SST", error.Message, StringComparison.Ordinal);
        if (fault == "space")
            Assert.Equal(unchecked((int)0x80070070), error.HResult);
        if (fault is "partial" or "space")
            Assert.NotNull(writtenPrefix);
        Assert.Equal(fault == "unknown", File.Exists(faultPath));
        Assert.False(File.Exists(Path.Combine(staging, KvLegacyMigration.SwitchingFileName)));
        Assert.False(File.Exists(KvLsmPaths.CurrentPath(path)));
        Assert.Empty(Directory.GetDirectories(path, KvLegacyMigration.RetainedPrefix + "*"));
        using (KvKeyspace legacy = KvKeyspace.Open("m", path, Options(KvStorageEngine.Legacy)))
        {
            Assert.Equal(KvStorageEngine.Legacy, legacy.StorageEngine);
            Verify(legacy, expected, preserveVersions: true);
        }
        Assert.False(Directory.Exists(staging));
        ReopenAndVerify(path, expected, KvStorageEngine.Lsm);
    }

    [Theory]
    [InlineData("switching", "partial")]
    [InlineData("switching", "sync")]
    [InlineData("switching", "unknown")]
    [InlineData("generation.meta", "partial")]
    [InlineData("generation.meta", "sync")]
    [InlineData("generation.meta", "unknown")]
    [InlineData("migration.info", "partial")]
    [InlineData("migration.info", "sync")]
    [InlineData("migration.info", "unknown")]
    public void Standalone_MigrationIoFailure_ReopensIdempotentlyWithFullValuesVersionsAndTtl(string file, string fault)
    {
        string path = Path.Combine(_root, "standalone-" + file + "-" + fault);
        Expected expected = Seed(path);
        using var injected = InjectFileFault(path, file, fault);
        Assert.Throws<IOException>(() => KvKeyspace.Open("m", path, Options()).Dispose());
        injected.AssertTriggeredAndStop();
        ReopenAndVerify(path, expected, KvStorageEngine.Lsm);
    }

    [Theory]
    [InlineData("lsm-migration.pending", "partial")]
    [InlineData("lsm-migration.pending", "sync")]
    [InlineData("lsm-migration.pending", "unknown")]
    [InlineData("generation.meta", "partial")]
    [InlineData("generation.meta", "sync")]
    [InlineData("generation.meta", "unknown")]
    [InlineData("migration.info", "partial")]
    [InlineData("migration.info", "sync")]
    [InlineData("migration.info", "unknown")]
    public void Shared_MigrationIoFailure_ReconcilesPublishedAndUnpublishedImports(string file, string fault)
    {
        string databasePath = Path.Combine(_root, "shared-db-" + file + "-" + fault);
        using (Tsdb legacy = Tsdb.Open(DatabaseOptions(databasePath, KvStorageEngine.Legacy)))
        {
            SqlExecutor.Execute(legacy, "CREATE TABLE items(id INT, code STRING, payload STRING, PRIMARY KEY(id))");
            SqlExecutor.Execute(legacy, "CREATE INDEX ix_code ON items(code)");
            SqlExecutor.Execute(legacy, "TRUNCATE TABLE items");
            var rows = new IReadOnlyList<object?>[65];
            for (int index = 0; index < rows.Length; index++)
                rows[index] = new object?[] { (long)index, "group-" + index % 7, new string((char)('a' + index % 20), 33 + index) };
            legacy.Tables.Open("items").InsertMany(rows);
            legacy.Tables.CheckpointAll();
        }
        string path = Assert.Single(Directory.GetDirectories(Path.Combine(databasePath, "tables", "rowstore")));
        using (var injected = InjectFileFault(path, file, fault))
        {
            Assert.Throws<IOException>(() =>
            {
                using Tsdb migrating = Tsdb.Open(DatabaseOptions(databasePath));
                _ = migrating.Tables.Open("items").RowCount;
            });
            injected.AssertTriggeredAndStop();
        }
        var clock = Stopwatch.StartNew();
        for (int reopen = 0; reopen < 3 && clock.Elapsed < TimeSpan.FromSeconds(30); reopen++)
        {
            using Tsdb database = Tsdb.Open(DatabaseOptions(databasePath));
            var table = database.Tables.Open("items");
            Assert.Equal(1, table.Generation);
            Assert.Equal(65, table.RowCount);
            for (int index = 0; index < 65; index++)
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "Full table recovery comparison timed out.");
                var row = table.GetByPrimaryKey(new object?[] { (long)index });
                Assert.NotNull(row);
                Assert.Equal((long)index, row.Values[0]);
                Assert.Equal("group-" + index % 7, row.Values[1]);
                Assert.Equal(new string((char)('a' + index % 20), 33 + index), row.Values[2]);
            }
            var indexed = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
                "SELECT id, code FROM items WHERE code='group-3' ORDER BY id"));
            Assert.Equal(Enumerable.Range(0, 65).Where(index => index % 7 == 3).Select(index => (long)index),
                indexed.Rows.Select(row => (long)row[0]!));
            Assert.All(indexed.Rows, row => Assert.Equal("group-3", row[1]));
            Assert.False(File.Exists(Path.Combine(path, KvLegacyMigration.PendingFileName)));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "Repeated recovery exceeded its deadline.");
        }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "Repeated recovery did not finish all three reopens.");
        using var budget = new KvDiskReadBudget(2);
        using KvLsmStore store = KvLsmStore.Open(Path.Combine(databasePath, "tables", "lsm"), Options(), budget);
        Assert.Single(store.List());
    }

    [Theory]
    [InlineData("generation.meta", "partial")]
    [InlineData("generation.meta", "sync")]
    [InlineData("generation.meta", "unknown")]
    [InlineData("lsm-rollback.pending", "partial")]
    [InlineData("lsm-rollback.pending", "sync")]
    [InlineData("lsm-rollback.pending", "unknown")]
    [InlineData("isolated", "partial")]
    [InlineData("isolated", "sync")]
    [InlineData("isolated", "unknown")]
    public void Standalone_RollbackIoFailure_ResumesWithoutDuplicatingOrLosingEntries(string file, string fault)
    {
        string path = Path.Combine(_root, "rollback-" + file + "-" + fault);
        Expected expected = Seed(path);
        using (KvKeyspace.Open("m", path, Options())) { }
        using var injected = InjectFileFault(path, file, fault);
        Assert.Throws<IOException>(() => KvLegacyMigration.RollbackStandalone(path));
        injected.AssertTriggeredAndStop();
        KvLegacyMigration.RollbackStandalone(path);
        ReopenAndVerify(path, expected, KvStorageEngine.Legacy);
        Assert.False(File.Exists(Path.Combine(path, KvLegacyMigration.RollbackPendingFileName)));
    }

    [Theory]
    [InlineData("generation.meta", "partial")]
    [InlineData("generation.meta", "sync")]
    [InlineData("generation.meta", "unknown")]
    [InlineData("lsm-rollback.pending", "partial")]
    [InlineData("lsm-rollback.pending", "sync")]
    [InlineData("lsm-rollback.pending", "unknown")]
    [InlineData("isolated", "partial")]
    [InlineData("isolated", "sync")]
    [InlineData("isolated", "unknown")]
    public void Shared_RollbackIoFailure_ResumesWithoutLeavingAnImportedKeyspace(string file, string fault)
    {
        string path = Path.Combine(_root, "shared-rollback-" + file + "-" + fault);
        Expected expected = Seed(path);
        using var budget = new KvDiskReadBudget(2);
        using KvLsmStore store = KvLsmStore.Open(Path.Combine(_root, "lsm"), Options(), budget);
        long id = KvLegacyMigration.TryMigrateIntoShared("m", path, store, Options(), budget, out _)!.Value;
        using var injected = InjectFileFault(path, file, fault);
        Assert.Throws<IOException>(() => KvLegacyMigration.RollbackShared(path, store));
        injected.AssertTriggeredAndStop();
        KvLegacyMigration.RollbackShared(path, store);
        ReopenAndVerify(path, expected, KvStorageEngine.Legacy);
        Assert.False(store.Contains(id));
        Assert.Empty(store.List());
        Assert.False(File.Exists(Path.Combine(path, KvLegacyMigration.RollbackPendingFileName)));
    }

    private static KvOptions Options(KvStorageEngine engine = KvStorageEngine.Lsm) => KvOptions.Default with
    {
        StorageEngine = engine,
        LegacyMigration = engine == KvStorageEngine.Legacy ? KvLegacyMigrationMode.Disabled : KvLegacyMigrationMode.Auto,
        SyncWalOnEveryWrite = true,
        AutoCheckpointEnabled = false,
        CleanupEnabled = false,
        ExpirerEnabled = false,
    };

    private static TsdbOptions DatabaseOptions(string path, KvStorageEngine engine = KvStorageEngine.Lsm) => new()
    {
        RootDirectory = path,
        Kv = Options(engine),
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
    };

    private static Expected Seed(string path)
    {
        using KvKeyspace legacy = KvKeyspace.Open("m", path, Options(KvStorageEngine.Legacy));
        legacy.Put("removed", [17]);
        legacy.Clear();
        DateTimeOffset expires = DateTimeOffset.UtcNow.AddHours(2);
        var entries = new Dictionary<string, KvEntry>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        for (int index = 0; index < 65 && clock.Elapsed < TimeSpan.FromSeconds(15); index++)
        {
            string key = $"key-{index:D4}";
            byte[] value = Enumerable.Range(0, 33 + index).Select(value => (byte)(value ^ index)).ToArray();
            legacy.Put(key, value, index % 3 == 0 ? expires : null);
            entries.Add(key, legacy.GetEntry(key)!);
            if (index == 31)
                legacy.CreateSnapshot();
        }
        Assert.Equal(65, entries.Count);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), "Seed exceeded its deadline.");
        return new Expected(legacy.LastSequence, legacy.Generation, entries);
    }

    private static void ReopenAndVerify(string path, Expected expected, KvStorageEngine engine)
    {
        var clock = Stopwatch.StartNew();
        for (int reopen = 0; reopen < 3 && clock.Elapsed < TimeSpan.FromSeconds(30); reopen++)
        {
            using KvKeyspace store = KvKeyspace.Open("m", path, Options(engine));
            Assert.Equal(engine, store.StorageEngine);
            Verify(store, expected, preserveVersions: true);
        }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "Recovery did not finish all three reopens.");
    }

    private static void Verify(KvKeyspace store, Expected expected, bool preserveVersions)
    {
        Assert.Equal(expected.Generation, store.Generation);
        if (preserveVersions)
            Assert.Equal(expected.Sequence, store.LastSequence);
        Assert.Equal(expected.Entries.Count, store.Count);
        Assert.Null(store.Get("removed"));
        var clock = Stopwatch.StartNew();
        foreach ((string key, KvEntry original) in expected.Entries)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "Full-value comparison exceeded its deadline.");
            KvEntry actual = Assert.IsType<KvEntry>(store.GetEntry(key));
            Assert.Equal(original.Key.ToArray(), actual.Key.ToArray());
            Assert.Equal(original.Value.ToArray(), actual.Value.ToArray());
            Assert.Equal(original.ExpiresAtUtc, actual.ExpiresAtUtc);
            if (preserveVersions)
                Assert.Equal(original.Version, actual.Version);
            else
                Assert.InRange(actual.Version, 1, store.LastSequence);
        }
    }

    private static FileFault InjectFileFault(string root, string file, string fault) => new(root, file, fault);

    private sealed record Expected(long Sequence, long Generation, IReadOnlyDictionary<string, KvEntry> Entries);

    private sealed class FileFault : IDisposable
    {
        private readonly string _root;
        private readonly string _file;
        private readonly string _fault;
        private string? _partialPath;
        private byte[]? _partial;
        private int _triggered;

        internal FileFault(string root, string file, string fault)
        {
            _root = root + Path.DirectorySeparatorChar;
            _file = file;
            _fault = fault;
            KvMigrationFileIo.TestHook = Inject;
        }

        private void Inject(KvMigrationIoPhase phase, string path, FileStream? stream, ReadOnlySpan<byte> content)
        {
            KvMigrationIoPhase target = _fault switch
            {
                "partial" => KvMigrationIoPhase.BeforeWrite,
                "sync" => KvMigrationIoPhase.BeforeSync,
                "unknown" => KvMigrationIoPhase.AfterReplace,
                _ => throw new InvalidOperationException("Unknown fault fixture."),
            };
            if (phase != target || !path.StartsWith(_root, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(path), _file + (phase == KvMigrationIoPhase.AfterReplace ? "" : ".tmp"), StringComparison.Ordinal))
                return;
            Assert.Equal(1, Interlocked.Increment(ref _triggered));
            if (_fault == "partial")
            {
                _partialPath = path;
                _partial = content[..Math.Max(1, content.Length / 2)].ToArray();
                stream!.Write(_partial);
                stream.Flush(flushToDisk: true);
            }
            throw new IOException("isolated migration " + _fault + " I/O failure: " + _file);
        }

        internal void AssertTriggeredAndStop()
        {
            KvMigrationFileIo.TestHook = null;
            Assert.Equal(1, _triggered);
            if (_partialPath is not null)
                Assert.Equal(_partial, File.ReadAllBytes(_partialPath));
        }

        public void Dispose() => KvMigrationFileIo.TestHook = null;
    }
}
