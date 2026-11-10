using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Exceptions;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>K7：共享表事务、维护状态与行数在 WAL 恢复中的一致性。</summary>
public sealed class SharedLsmTableTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-shared-txn-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TwoTables_CommitOneWalRecordAndOneSync_WithIndexesAndReopen()
    {
        using (Tsdb db = Open())
        {
            CreateTables(db);
            KvLsmStore store = db.Tables.Open("a").SharedStore!;
            long sequence = store.Engine.LastSequence;
            int syncs = 0;
            store.Engine.WalSyncTestHook = () => syncs++;
            try
            {
                Assert.Equal(2, db.Tables.ApplyTransaction(Inserts()));
                Assert.Equal(1, syncs);
                Assert.Equal(sequence + 1, store.Engine.LastSequence);
                Assert.False(File.Exists(Path.Combine(_root, "tables", TableTransactionJournal.FileName)));
                AssertState(db, 1);
            }
            finally { store.Engine.WalSyncTestHook = null; }
        }
        using Tsdb reopened = Open();
        AssertState(reopened, 1);
        Assert.Equal(1L, Scalar(reopened, "SELECT COUNT(*) FROM a WHERE code='x'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BeforeCommitFailureOrCancellation_LeavesAllTablesUnchanged(bool cancel)
    {
        using Tsdb db = Open();
        CreateTables(db);
        KvLsmStore store = db.Tables.Open("a").SharedStore!;
        long sequence = store.Engine.LastSequence;
        db.Tables.ApplyTransactionBeforeCompleteTestHook = () =>
        {
            if (cancel) throw new OperationCanceledException();
            throw new IOException("before atomic append");
        };
        try { Assert.ThrowsAny<Exception>(() => db.Tables.ApplyTransaction(Inserts())); }
        finally { db.Tables.ApplyTransactionBeforeCompleteTestHook = null; }
        Assert.Equal(sequence, store.Engine.LastSequence);
        AssertState(db, 0);
        Assert.Equal(2, db.Tables.ApplyTransaction(Inserts()));
        AssertState(db, 1);
    }

    [Fact]
    public void LostAcknowledgement_IsUnknownAndNeverCompensated()
    {
        using (Tsdb db = Open())
        {
            CreateTables(db);
            db.Tables.ApplyTransactionAfterCompleteTestHook = () => throw new IOException("lost acknowledgement");
            Assert.Throws<TableTransactionRecoveryException>(() => db.Tables.ApplyTransaction(Inserts()));
            db.Tables.ApplyTransactionAfterCompleteTestHook = null;
            Assert.ThrowsAny<Exception>(() => db.Tables.ApplyTransaction(Inserts()));
        }
        using Tsdb reopened = Open();
        AssertState(reopened, 1);
    }

    [Fact]
    public void WalFailure_EntersSharedReadOnly_AndReopenKeepsWholeBatch()
    {
        using (Tsdb db = Open())
        {
            CreateTables(db);
            KvLsmStore store = db.Tables.Open("a").SharedStore!;
            store.Engine.WalSyncTestHook = () => throw new IOException("injected fsync failure");
            Assert.Throws<TableTransactionRecoveryException>(() => db.Tables.ApplyTransaction(Inserts()));
            store.Engine.WalSyncTestHook = null;
            Assert.NotNull(store.Engine.Fault);
            Assert.ThrowsAny<Exception>(() => db.Tables.ApplyTransaction(Inserts()));
        }
        using Tsdb reopened = Open();
        Assert.Equal(Scalar(reopened, "SELECT COUNT(*) FROM a"), Scalar(reopened, "SELECT COUNT(*) FROM b"));
    }

    [Fact]
    public void PersistedRowCount_TracksUpdatesDeletesTruncateAndOldWriterInvalidation()
    {
        using var budget = new KvDiskReadBudget(2);
        using KvLsmStore store = KvLsmStore.Open(Path.Combine(_root, "lsm"), Options, budget);
        long id = store.Create("t");
        string sidecar = Path.Combine(_root, "table");
        var schema = TableSchema.Create("t", [("id", TableColumnType.Int64, false)], ["id"]);
        using (var table = new TableStore(schema, KvKeyspace.OpenShared("t", sidecar, store, id, Options, budget)))
        {
            table.Insert([1L]);
            table.Upsert([1L]);
            table.Insert([2L]);
            Assert.Equal(2, store.ReadRowCount(id));
            table.DeleteByPrimaryKey([1L]);
            Assert.Equal(1, store.ReadRowCount(id));
            table.Truncate();
            Assert.Equal(0, store.ReadRowCount(id));
            table.Insert([3L]);
        }
        using (KvKeyspace keyspace = KvKeyspace.OpenShared("t", sidecar, store, id, Options, budget))
        {
            Assert.Equal(1, keyspace.LoadTableRowCount());
        }
        // Older writers update sq|id without updating the optional derived row count.
        store.Import(id, [KvBatchMutation.Put(TableIndexCodec.EncodePrimaryRowKey(TableKeyCodec.EncodePrimaryKeyValues(schema, [4L])), TableRowCodec.Encode(schema, [4L]))]);
        Assert.Null(store.ReadRowCount(id));
        using var recovered = new TableStore(schema, KvKeyspace.OpenShared("t", sidecar, store, id, Options, budget));
        Assert.Equal(2, recovered.RowCount);
        Assert.Equal(2, store.ReadRowCount(id));
        Assert.False(File.Exists(Path.Combine(sidecar, TableStoreMaintenanceFile.LegacyMigrationFileName)));
        Assert.False(File.Exists(Path.Combine(sidecar, TableStoreMaintenanceFile.CleanIndexesFileName)));
    }

    private static KvOptions Options => KvOptions.Default with { StorageEngine = KvStorageEngine.Lsm, AutoCheckpointEnabled = false, SyncWalOnEveryWrite = true, ExpirerEnabled = false };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyPendingJournal_IsUndoneBeforeAutoMigration_AndGenerationSurvives(bool delete)
    {
        using (Tsdb legacy = Tsdb.Open(new TsdbOptions { RootDirectory = _root, Kv = Options with { StorageEngine = KvStorageEngine.Legacy, LegacyMigration = KvLegacyMigrationMode.Disabled } }))
        {
            SqlExecutor.Execute(legacy, "CREATE TABLE source_rows(id INT,value INT,PRIMARY KEY(id))");
            SqlExecutor.Execute(legacy, "CREATE UNIQUE INDEX ix_value ON source_rows(value)");
            SqlExecutor.Execute(legacy, "CREATE TABLE audit_rows(id INT,PRIMARY KEY(id))");
            SqlExecutor.Execute(legacy, "TRUNCATE TABLE source_rows");
            SqlExecutor.Execute(legacy, "TRUNCATE TABLE audit_rows");
            SqlExecutor.Execute(legacy, "INSERT INTO source_rows(id,value) VALUES(1,10)");
            string action = delete ? "DELETE" : "UPDATE";
            SqlExecutor.Execute(legacy, $"CREATE TRIGGER track_change AFTER {action} ON source_rows FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO audit_rows(id) VALUES(OLD.id); END");
            SqlExecutor.Execute(legacy, delete ? "DELETE FROM source_rows WHERE id=1" : "UPDATE source_rows SET value=20 WHERE id=1");
        }
        string journal = Path.Combine(_root, "tables", TableTransactionJournal.FileName);
        using (var file = new FileStream(journal, FileMode.Open, FileAccess.Write)) file.SetLength(file.Length - 4);
        for (int reopen = 0; reopen < 2; reopen++)
        {
            using Tsdb db = Open();
            Assert.Equal(10L, Scalar(db, "SELECT value FROM source_rows WHERE id=1"));
            Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM audit_rows"));
            Assert.Equal(1, db.Tables.Open("source_rows").Generation);
            Assert.Equal(1, db.Tables.Open("audit_rows").Generation);
            Assert.NotNull(db.Tables.Open("source_rows").SharedStore);
            Assert.NotNull(db.Tables.Open("audit_rows").SharedStore);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaintenanceMetadata_SyncsOnce_AndSurvivesReopenWithoutChangingLogicalSequence(bool strong)
    {
        var options = Options with { SyncWalOnEveryWrite = strong };
        string path = Path.Combine(_root, "lsm");
        long id, logicalSequence;
        using (var budget = new KvDiskReadBudget(2))
        using (KvLsmStore store = KvLsmStore.Open(path, options, budget))
        {
            id = store.Create("t");
            logicalSequence = store.GetLastSequence(id);
            int syncs = 0;
            store.Engine.WalSyncTestHook = () => syncs++;
            try
            {
                store.WriteMaintenance(id, "token", [42]);
                Assert.Equal(1, syncs);
                Assert.Equal(logicalSequence, store.GetLastSequence(id));
                Assert.Equal([42], store.ReadMaintenance(id, "token"));
            }
            finally { store.Engine.WalSyncTestHook = null; }
        }
        using var reopenedBudget = new KvDiskReadBudget(2);
        using KvLsmStore reopened = KvLsmStore.Open(path, options, reopenedBudget);
        Assert.Equal([42], reopened.ReadMaintenance(id, "token"));
        Assert.Equal(logicalSequence, reopened.GetLastSequence(id));
        int deletes = 0;
        reopened.Engine.WalSyncTestHook = () => deletes++;
        try
        {
            reopened.WriteMaintenance(id, "token", null);
            Assert.Equal(1, deletes);
            Assert.Null(reopened.ReadMaintenance(id, "token"));
        }
        finally { reopened.Engine.WalSyncTestHook = null; }
    }

    [Fact]
    public void IndexRecovery_ColdLargeBlob_AllocatesOneBlockWithoutCopyingUnselectedColumn()
    {
        using var budget = new KvDiskReadBudget(2);
        using KvLsmStore store = KvLsmStore.Open(Path.Combine(_root, "lsm"), Options, budget);
        long id = store.Create("large");
        string sidecar = Path.Combine(_root, "large");
        var schema = TableSchema.Create("large",
            [("id", TableColumnType.Int64, false), ("site", TableColumnType.String, false), ("payload", TableColumnType.Blob, false)],
            ["id"], [new TableIndexDefinition("ix_site", ["site"], false, 1)]);
        const int payloadBytes = 5 * 1024 * 1024;
        using (var table = new TableStore(schema, KvKeyspace.OpenShared("large", sidecar, store, id, Options, budget)))
        {
            table.Insert([1L, "kept", new byte[payloadBytes]]);
            store.Engine.CompactAll();
        }
        store.WriteMaintenance(id, TableStoreMaintenanceFile.CleanIndexesFileName, null);
        KvKeyspace reopened = KvKeyspace.OpenShared("large", sidecar, store, id, Options, budget);
        long before = GC.GetAllocatedBytesForCurrentThread();
        using var recovered = new TableStore(schema, reopened);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(1, recovered.RowCount);
        Assert.True(allocated < payloadBytes + 1024 * 1024, $"Cold index recovery allocated {allocated:N0} bytes for {payloadBytes:N0} bytes.");
        Assert.Equal(1, store.ReadRowCount(id));
    }

    [Fact]
    public void SharedBatch_EmptyCancelledAndOversized_LeaveAllDataAndSequencesUntouched()
    {
        var options = Options with { AutoCheckpointEnabled = true, MaxWalBytes = 1024 };
        using var budget = new KvDiskReadBudget(2);
        using KvLsmStore store = KvLsmStore.Open(Path.Combine(_root, "lsm"), options, budget);
        long aId = store.Create("a"), bId = store.Create("b");
        using KvKeyspace a = KvKeyspace.OpenShared("a", Path.Combine(_root, "a"), store, aId, options, budget);
        using KvKeyspace b = KvKeyspace.OpenShared("b", Path.Combine(_root, "b"), store, bId, options, budget);
        long sequence = store.Engine.LastSequence;
        KvKeyspace.ApplySharedTransaction([], CancellationToken.None);
        var entries = new (KvKeyspace, IReadOnlyList<KvBatchMutation>, long, long)[]
        {
            (a, new[] { KvBatchMutation.Put("key"u8.ToArray(), new byte[600]) }, a.LastSequence, a.Generation),
            (b, new[] { KvBatchMutation.Put("key"u8.ToArray(), new byte[600]) }, b.LastSequence, b.Generation),
        };
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => KvKeyspace.ApplySharedTransaction(entries, cancel.Token));
        IOException error = Assert.Throws<IOException>(() => KvKeyspace.ApplySharedTransaction(entries, CancellationToken.None));
        Assert.True(KvAtomicBatchErrors.IsTooLarge(error));
        Assert.Equal(sequence, store.Engine.LastSequence);
        Assert.Null(a.Get("key"));
        Assert.Null(b.Get("key"));
        Assert.Null(store.Engine.Fault);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedLegacyOrStandaloneAndShared_KeepJournalRecovery(bool standaloneLsm)
    {
        using (Tsdb legacy = Tsdb.Open(new TsdbOptions { RootDirectory = _root, Kv = Options with { StorageEngine = KvStorageEngine.Legacy } }))
            SqlExecutor.Execute(legacy, "CREATE TABLE a(id INT,code STRING,PRIMARY KEY(id))");
        if (standaloneLsm)
        {
            string table = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "tables", "rowstore")));
            using KvKeyspace converted = KvKeyspace.Open("a", table, Options);
        }
        using (Tsdb db = Tsdb.Open(new TsdbOptions { RootDirectory = _root, Kv = Options with { LegacyMigration = KvLegacyMigrationMode.Disabled } }))
        {
            SqlExecutor.Execute(db, "CREATE TABLE b(id INT,code STRING,PRIMARY KEY(id))");
            Assert.Null(db.Tables.Open("a").SharedStore);
            Assert.NotNull(db.Tables.Open("b").SharedStore);
            db.Tables.ApplyTransactionBeforeCompleteTestHook = () => throw new IOException("mixed transaction pre-decision failure");
            Assert.Throws<IOException>(() => db.Tables.ApplyTransaction(Inserts()));
            db.Tables.ApplyTransactionBeforeCompleteTestHook = null;
            AssertState(db, 0);
            db.Tables.ApplyTransaction(Inserts());
            AssertState(db, 1);
        }
        using Tsdb reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root, Kv = Options with { LegacyMigration = KvLegacyMigrationMode.Disabled } });
        AssertState(reopened, 1);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        Kv = Options,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
    });

    private static void CreateTables(Tsdb db)
    {
        SqlExecutor.Execute(db, "CREATE TABLE a(id INT,code STRING,PRIMARY KEY(id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_code ON a(code)");
        SqlExecutor.Execute(db, "CREATE TABLE b(id INT,code STRING,PRIMARY KEY(id))");
        db.Tables.Open("a");
        db.Tables.Open("b");
    }

    private static Dictionary<string, IReadOnlyList<TableRowMutation>> Inserts() => new()
    {
        ["a"] = [new TableRowMutation(null, [1L, "x"])],
        ["b"] = [new TableRowMutation(null, [1L, "y"])],
    };

    private static object? Scalar(Tsdb db, string sql)
        => Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql)).Rows)[0];

    private static void AssertState(Tsdb db, int expected)
    {
        foreach (string name in new[] { "a", "b" })
        {
            Assert.Equal((long)expected, Scalar(db, $"SELECT COUNT(*) FROM {name}"));
            Assert.Equal(expected, db.Tables.Open(name).RowCount);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
