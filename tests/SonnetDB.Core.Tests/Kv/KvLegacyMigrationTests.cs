using System.Text;
using SonnetDB.Engine;
using SonnetDB.Kv;
using SonnetDB.Kv.Lsm;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

/// <summary>K7：打开时把 legacy keyspace 与关系表迁移到 LSM（决策 D2），含崩溃注入、回滚与保留期清理。</summary>
[Collection(nameof(KvLegacyMigrationTests))]
public sealed class KvLegacyMigrationTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sndb-kv-migration-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        KvLegacyMigration.PhaseTestHook = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>独立 keyspace 迁移保留数据、版本、TTL、LastSequence 与 generation；旧文件移入保留目录，新目录带 v3 标记。</summary>
    [Fact]
    public void Standalone_MigratesOnOpen_PreservingVersionsTtlSequenceAndGeneration()
    {
        string path = Path.Combine(_root, "standalone");
        DateTimeOffset expires = DateTimeOffset.UtcNow.AddHours(1);
        long versionA, versionB, sequence, generation;
        using (KvKeyspace legacy = KvKeyspace.Open("m", path, Options(KvStorageEngine.Legacy)))
        {
            legacy.Put("old", [9]);
            legacy.Clear();
            legacy.Put("a", [1]);
            legacy.CreateSnapshot();
            legacy.Put("b", [2], expires);
            versionA = legacy.GetEntry("a"u8)!.Version;
            versionB = legacy.GetEntry("b"u8)!.Version;
            sequence = legacy.LastSequence;
            generation = legacy.Generation;
        }

        using (KvKeyspace migrated = KvKeyspace.Open("m", path, Options()))
        {
            Assert.Equal(KvStorageEngine.Lsm, migrated.StorageEngine);
            Assert.Equal(sequence, migrated.LastSequence);
            Assert.Equal(generation, migrated.Generation);
            Assert.Null(migrated.Get("old"));
            KvEntry a = migrated.GetEntry("a"u8)!;
            KvEntry b = migrated.GetEntry("b"u8)!;
            Assert.Equal(versionA, a.Version);
            Assert.Equal(versionB, b.Version);
            Assert.Equal(expires.UtcTicks, b.ExpiresAtUtc!.Value.UtcTicks);
            migrated.Put("c", [3]);
        }

        Assert.True(KvLsmLayoutMarkerFile.TryRead(path, out KvLsmLayoutMarker marker));
        Assert.Equal(KvLsmLayoutKind.Standalone, marker.Kind);
        Assert.False(Directory.Exists(KvKeyspace.SnapshotsDirectory(path)));
        Assert.False(Directory.Exists(Path.Combine(path, KvLegacyMigration.StagingDirectoryName)));
        string retained = Assert.Single(Directory.GetDirectories(path, KvLegacyMigration.RetainedPrefix + "*"));
        Assert.True(File.Exists(Path.Combine(retained, KvLegacyMigration.InfoFileName)));

        // 保留期为零：下一次按 LSM 打开时删除保留目录。
        using (KvKeyspace reopened = KvKeyspace.Open("m", path, Options() with { LegacyMigrationRetention = TimeSpan.Zero }))
            Assert.Equal([3], reopened.Get("c"));
        Assert.Empty(Directory.GetDirectories(path, KvLegacyMigration.RetainedPrefix + "*"));
    }

    /// <summary>在每个切换阶段崩溃后重新打开：切换开始前回到 legacy 再迁移，切换开始后向前补完，数据都完整。</summary>
    [Theory]
    [InlineData("standalone-built")]
    [InlineData("standalone-switching")]
    [InlineData("standalone-legacy-moved")]
    [InlineData("standalone-current-moved")]
    public void Standalone_CrashAtEachPhase_ReopenCompletesWithIntactData(string phase)
    {
        string path = Path.Combine(_root, "crash-" + phase);
        using (KvKeyspace legacy = KvKeyspace.Open("c", path, Options(KvStorageEngine.Legacy)))
        {
            for (int i = 0; i < 50; i++)
                legacy.Put("k" + i.ToString("D3"), Encoding.UTF8.GetBytes("v" + i));
            legacy.CreateSnapshot();
            legacy.Put("tail", [7]);
        }

        KvLegacyMigration.PhaseTestHook = (current, directory) =>
        {
            if (current == phase && directory.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("injected migration crash at " + phase);
        };
        Assert.Throws<IOException>(() => KvKeyspace.Open("c", path, Options()).Dispose());
        KvLegacyMigration.PhaseTestHook = null;

        using KvKeyspace reopened = KvKeyspace.Open("c", path, Options());
        Assert.Equal(KvStorageEngine.Lsm, reopened.StorageEngine);
        Assert.Equal(51, reopened.Count);
        Assert.Equal("v49", Encoding.UTF8.GetString(reopened.Get("k049")!));
        Assert.Equal([7], reopened.Get("tail"));
        Assert.False(Directory.Exists(Path.Combine(path, KvLegacyMigration.StagingDirectoryName)));
    }

    /// <summary>迁移后没有新写入时可以回滚到 legacy；有新写入时拒绝回滚。</summary>
    [Fact]
    public void Standalone_Rollback_AllowedOnlyWithoutNewWrites()
    {
        string clean = Path.Combine(_root, "rollback-clean");
        string written = Path.Combine(_root, "rollback-written");
        foreach (string path in new[] { clean, written })
        {
            using KvKeyspace legacy = KvKeyspace.Open("r", path, Options(KvStorageEngine.Legacy));
            legacy.Put("a", [1]);
        }
        using (KvKeyspace.Open("r", clean, Options())) { }
        using (KvKeyspace migrated = KvKeyspace.Open("r", written, Options()))
            migrated.Put("new", [2]);

        KvLegacyMigration.RollbackStandalone(clean);
        Assert.False(File.Exists(KvLsmPaths.CurrentPath(clean)));
        using (KvKeyspace rolledBack = KvKeyspace.Open("r", clean, Options(KvStorageEngine.Legacy)))
        {
            Assert.Equal(KvStorageEngine.Legacy, rolledBack.StorageEngine);
            Assert.Equal([1], rolledBack.Get("a"));
        }

        Assert.Throws<InvalidOperationException>(() => KvLegacyMigration.RollbackStandalone(written));
        using KvKeyspace still = KvKeyspace.Open("r", written, Options());
        Assert.Equal([2], still.Get("new"));
    }

    /// <summary>legacy 关系表在选择 LSM 后打开时导入共享实例：行、索引与 SQL 查询保持；表目录改为共享标记。</summary>
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(true, 0)]
    public void Shared_MigrationPreservesGeneration_AndEmptyMigrationCanRollBack(bool empty, int generation)
    {
        string table = Path.Combine(_root, "generation-table");
        using (KvKeyspace legacy = KvKeyspace.Open("t", table, Options(KvStorageEngine.Legacy)))
        {
            for (int clear = 0; clear < generation; clear++)
            {
                legacy.Put("removed", [9]);
                legacy.Clear();
            }
            if (!empty) legacy.Put("kept", [42]);
            Assert.Equal(generation, legacy.Generation);
        }
        using var budget = new KvDiskReadBudget(2);
        using KvLsmStore store = KvLsmStore.Open(Path.Combine(_root, "generation-lsm"), Options(), budget);
        long id = KvLegacyMigration.TryMigrateIntoShared("t", table, store, Options(), budget, out _)!.Value;
        using (KvKeyspace shared = KvKeyspace.OpenShared("t", table, store, id, Options(), budget))
        {
            Assert.Equal(generation, shared.Generation);
            Assert.Equal(empty ? 0 : 1, shared.Count);
        }
        KvLegacyMigration.RollbackShared(table, store);
        using KvKeyspace restored = KvKeyspace.Open("t", table, Options(KvStorageEngine.Legacy));
        Assert.Equal(generation, restored.Generation);
        Assert.Equal(empty ? null : new byte[] { 42 }, restored.Get("kept"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shared_OldEmptyImportWithZeroSequence_RollsBackOnlyBeforeAnyWrite(bool written)
    {
        string table = Path.Combine(_root, "old-empty");
        using (KvKeyspace.Open("e", table, Options(KvStorageEngine.Legacy))) { }
        using var budget = new KvDiskReadBudget(2);
        using KvLsmStore store = KvLsmStore.Open(Path.Combine(_root, "old-empty-lsm"), Options(), budget);
        long id = KvLegacyMigration.TryMigrateIntoShared("e", table, store, Options(), budget, out _)!.Value;
        string retained = Assert.Single(Directory.GetDirectories(table, KvLegacyMigration.RetainedPrefix + "*"));
        string info = Path.Combine(retained, KvLegacyMigration.InfoFileName);
        string[] lines = File.ReadAllLines(info);
        File.WriteAllLines(info, lines.Select(static line => line.StartsWith("importedSequence=", StringComparison.Ordinal) ? "importedSequence=0" : line));
        if (written)
        {
            using (KvKeyspace shared = KvKeyspace.OpenShared("e", table, store, id, Options(), budget)) shared.Put("new", [42]);
            Assert.Throws<InvalidOperationException>(() => KvLegacyMigration.RollbackShared(table, store));
            using KvKeyspace still = KvKeyspace.OpenShared("e", table, store, id, Options(), budget);
            Assert.Equal([42], still.Get("new"));
        }
        else
        {
            KvLegacyMigration.RollbackShared(table, store);
            using KvKeyspace restored = KvKeyspace.Open("e", table, Options(KvStorageEngine.Legacy));
            Assert.Equal(0, restored.Count);
            Assert.False(store.Contains(id));
        }
    }

    /// <summary>legacy 关系表在选择 LSM 后打开时导入共享实例：行、索引与 SQL 查询保持；表目录改为共享标记。</summary>
    [Fact]
    public void Tables_MigrateIntoSharedStorageOnOpen()
    {
        using (Tsdb legacy = OpenDatabase(KvStorageEngine.Legacy))
        {
            SqlExecutor.Execute(legacy, "CREATE TABLE items(id INT, code VARCHAR, PRIMARY KEY(id))");
            SqlExecutor.Execute(legacy, "CREATE INDEX ix_code ON items(code)");
            SqlExecutor.Execute(legacy, "INSERT INTO items(id, code) VALUES (1,'a'),(2,'b'),(3,'b')");
        }

        using (Tsdb migrated = OpenDatabase(KvStorageEngine.Lsm))
        {
            Assert.Equal(3L, Scalar(migrated, "SELECT COUNT(*) FROM items"));
            Assert.Equal(2L, Scalar(migrated, "SELECT COUNT(*) FROM items WHERE code = 'b'"));
            SqlExecutor.Execute(migrated, "INSERT INTO items(id, code) VALUES (4,'c')");
        }

        string tableDirectory = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "db", "tables", "rowstore")));
        Assert.True(KvLsmLayoutMarkerFile.TryRead(tableDirectory, out KvLsmLayoutMarker marker));
        Assert.Equal(KvLsmLayoutKind.Shared, marker.Kind);
        Assert.Single(Directory.GetDirectories(tableDirectory, KvLegacyMigration.RetainedPrefix + "*"));
        Assert.True(File.Exists(KvLsmPaths.CurrentPath(Path.Combine(_root, "db", "tables", "lsm"))));

        using Tsdb reopened = OpenDatabase(KvStorageEngine.Lsm);
        Assert.Equal(4L, Scalar(reopened, "SELECT COUNT(*) FROM items"));
    }

    /// <summary>表迁移在导入后、待处理日志后、移动旧文件后崩溃：重新打开后数据完整，且共享实例中没有残留的孤立编号。</summary>
    [Theory]
    [InlineData("shared-imported")]
    [InlineData("shared-pending")]
    [InlineData("shared-legacy-moved")]
    public void Tables_CrashAtEachPhase_ReopenCompletesWithoutOrphans(string phase)
    {
        using (Tsdb legacy = OpenDatabase(KvStorageEngine.Legacy))
        {
            SqlExecutor.Execute(legacy, "CREATE TABLE items(id INT, code VARCHAR, PRIMARY KEY(id))");
            SqlExecutor.Execute(legacy, "INSERT INTO items(id, code) VALUES (1,'a'),(2,'b')");
        }

        KvLegacyMigration.PhaseTestHook = (current, directory) =>
        {
            if (current == phase && directory.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("injected table migration crash at " + phase);
        };
        Assert.ThrowsAny<Exception>(() =>
        {
            using Tsdb crashing = OpenDatabase(KvStorageEngine.Lsm);
            SqlExecutor.Execute(crashing, "SELECT COUNT(*) FROM items");
        });
        KvLegacyMigration.PhaseTestHook = null;

        using (Tsdb reopened = OpenDatabase(KvStorageEngine.Lsm))
        {
            Assert.Equal(2L, Scalar(reopened, "SELECT COUNT(*) FROM items"));
            Assert.Equal("b", Scalar(reopened, "SELECT code FROM items WHERE id = 2"));
        }

        using var budget = new KvDiskReadBudget(2);
        using KvLsmStore store = KvLsmStore.Open(Path.Combine(_root, "db", "tables", "lsm"), Options(), budget);
        Assert.Single(store.List());
    }

    /// <summary>表迁移回滚：导入后没有新提交时 legacy 文件移回、共享数据删除。</summary>
    [Fact]
    public void Shared_Rollback_RestoresLegacyAndDropsImportedData()
    {
        string table = Path.Combine(_root, "table");
        using (KvKeyspace legacy = KvKeyspace.Open("t", table, Options(KvStorageEngine.Legacy)))
        {
            legacy.Put("r1", [1]);
            legacy.Put("r2", [2]);
        }

        using var budget = new KvDiskReadBudget(2);
        using KvLsmStore store = KvLsmStore.Open(Path.Combine(_root, "lsm"), Options(), budget);
        long? id = KvLegacyMigration.TryMigrateIntoShared("t", table, store, Options(), budget, out _);
        Assert.NotNull(id);
        using (KvKeyspace shared = KvKeyspace.OpenShared("t", table, store, id.Value, Options(), budget))
            Assert.Equal([2], shared.Get("r2"));

        KvLegacyMigration.RollbackShared(table, store);
        Assert.False(store.Contains(id.Value));
        Assert.False(KvLsmLayoutMarkerFile.TryRead(table, out _));
        using KvKeyspace restored = KvKeyspace.Open("t", table, Options(KvStorageEngine.Legacy));
        Assert.Equal(KvStorageEngine.Legacy, restored.StorageEngine);
        Assert.Equal([1], restored.Get("r1"));
    }

    private Tsdb OpenDatabase(KvStorageEngine engine) => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = Path.Combine(_root, "db"),
        Kv = Options(engine),
    });

    private static object? Scalar(Tsdb db, string sql)
        => Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql)).Rows)[0];

    private static KvOptions Options(KvStorageEngine engine = KvStorageEngine.Lsm) => KvOptions.Default with
    {
        StorageEngine = engine,
        SyncWalOnEveryWrite = false,
        ExpirerEnabled = false,
        CleanupEnabled = false,
    };
}
