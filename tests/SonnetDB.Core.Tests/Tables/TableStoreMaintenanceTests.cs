using SonnetDB.Kv;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

public sealed class TableStoreMaintenanceTests : IDisposable
{
    private readonly string _root;

    public TableStoreMaintenanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-table-maintenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ScanKeysPrefixAfter_LargeValue_DoesNotCopyValue()
    {
        string path = Path.Combine(_root, "key-scan");
        using var keyspace = KvKeyspace.Open("large-values", path, KvOptions.Default);
        byte[] value = new byte[8 * 1024 * 1024];
        keyspace.Put("row:1", value);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var keys = keyspace.ScanKeysPrefixAfter("row:"u8, ReadOnlySpan<byte>.Empty, limit: 16);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Single(keys);
        Assert.Equal("row:1"u8.ToArray(), keys[0]);
        Assert.True(
            allocated < 1024 * 1024,
            $"Key-only scan allocated {allocated:N0} bytes for an {value.Length:N0}-byte value.");
    }

    [Fact]
    public void Open_ModernLargeBlobWithoutIndexes_DoesNotMaterializeRowValue()
    {
        string path = Path.Combine(_root, "large-table");
        var schema = BlobSchema("large_table");
        var keyspace = KvKeyspace.Open("table.large_table", path, KvOptions.Default);
        byte[] blob = new byte[8 * 1024 * 1024];
        byte[] primaryKey = TableKeyCodec.EncodePrimaryKeyValues(schema, [1L]);
        keyspace.Put(
            TableIndexCodec.EncodePrimaryRowKey(primaryKey),
            TableRowCodec.Encode(schema, [1L, blob]));

        TableStore? store = null;
        try
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            store = new TableStore(schema, keyspace);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(1, store.RowCount);
            Assert.True(
                allocated < 1024 * 1024,
                $"Table open allocated {allocated:N0} bytes for an {blob.Length:N0}-byte modern row.");
        }
        finally
        {
            if (store is not null)
                store.Dispose();
            else
                keyspace.Dispose();
        }
    }

    [Fact]
    public void LegacyMigration_WithBufferedWal_PublishesDurableMarkerOnce()
    {
        string path = Path.Combine(_root, "legacy-table");
        var schema = SimpleSchema("legacy_table");
        var options = KvOptions.Default with { SyncWalOnEveryWrite = false };
        var keyspace = KvKeyspace.Open("table.legacy_table", path, options);
        byte[] primaryKey = TableKeyCodec.EncodePrimaryKeyValues(schema, [7L]);
        byte[] payload = TableRowCodec.Encode(schema, [7L, "legacy"]);
        keyspace.Put(primaryKey, payload);

        var store = new TableStore(schema, keyspace);
        Assert.Null(keyspace.Get(primaryKey));
        Assert.Equal(payload, keyspace.Get(TableIndexCodec.EncodePrimaryRowKey(primaryKey)));
        Assert.True(File.Exists(Path.Combine(path, TableStoreMaintenanceFile.LegacyMigrationFileName)));

        string walPath = Path.Combine(path, "wal", "active.SDBKVWAL");
        Assert.Equal(keyspace.ActiveWalLength, new FileInfo(walPath).Length);
        long migratedSequence = keyspace.LastSequence;
        store.Dispose();

        var reopenedKeyspace = KvKeyspace.Open("table.legacy_table", path, options);
        var reopenedStore = new TableStore(schema, reopenedKeyspace);
        Assert.Equal(migratedSequence, reopenedKeyspace.LastSequence);
        Assert.Equal("legacy", reopenedStore.GetByPrimaryKey([7L])!.Values[1]);
        reopenedStore.Dispose();
    }

    [Fact]
    public void LegacyMigration_AfterCrashBetweenPutAndDelete_IsIdempotent()
    {
        string path = Path.Combine(_root, "interrupted-legacy-table");
        var schema = SimpleSchema("interrupted_legacy_table");
        var keyspace = KvKeyspace.Open("table.interrupted_legacy_table", path, KvOptions.Default);
        byte[] primaryKey = TableKeyCodec.EncodePrimaryKeyValues(schema, [9L]);
        byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(primaryKey);
        keyspace.Put(primaryKey, TableRowCodec.Encode(schema, [9L, "stale-legacy"]));
        keyspace.Put(rowKey, TableRowCodec.Encode(schema, [9L, "already-migrated"]));

        var store = new TableStore(schema, keyspace);

        Assert.Null(keyspace.Get(primaryKey));
        Assert.Equal("already-migrated", store.GetByPrimaryKey([9L])!.Values[1]);
        Assert.True(File.Exists(Path.Combine(path, TableStoreMaintenanceFile.LegacyMigrationFileName)));
        store.Dispose();
    }

    [Fact]
    public void MissingCleanToken_AfterInterruptedIndexMutation_RebuildsIndexes()
    {
        string path = Path.Combine(_root, "indexed-table");
        var schema = IndexedSchema("indexed_table");
        object?[] values = [1L, "north"];
        byte[] primaryKey = TableKeyCodec.EncodePrimaryKey(schema, values);
        byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(primaryKey);
        byte[] indexKey = TableIndexCodec.EncodeIndexEntryKey(schema.Indexes[0], values, schema, primaryKey);

        var firstKeyspace = KvKeyspace.Open("table.indexed_table", path, KvOptions.Default);
        firstKeyspace.Put(rowKey, TableRowCodec.Encode(schema, values));
        var firstStore = new TableStore(schema, firstKeyspace);
        Assert.NotNull(firstKeyspace.Get(indexKey));
        firstStore.Dispose();

        var interruptedKeyspace = KvKeyspace.Open("table.indexed_table", path, KvOptions.Default);
        long beforeCleanOpen = interruptedKeyspace.LastSequence;
        var interruptedStore = new TableStore(schema, interruptedKeyspace);
        long cleanOpenSequence = interruptedKeyspace.LastSequence;
        Assert.Equal(beforeCleanOpen, cleanOpenSequence);
        Assert.Single(interruptedStore.GetByIndex(schema.Indexes[0], ["north"]));
        Assert.True(interruptedKeyspace.Delete(indexKey));
        interruptedKeyspace.Dispose();
        GC.KeepAlive(interruptedStore);

        var recoveredKeyspace = KvKeyspace.Open("table.indexed_table", path, KvOptions.Default);
        long beforeRebuild = recoveredKeyspace.LastSequence;
        var recoveredStore = new TableStore(schema, recoveredKeyspace);

        Assert.Equal(cleanOpenSequence + 1, beforeRebuild);
        Assert.True(recoveredKeyspace.LastSequence > beforeRebuild);
        Assert.Single(recoveredStore.GetByIndex(schema.Indexes[0], ["north"]));
        recoveredStore.Dispose();
    }

    [Fact]
    public void Dispose_WhenWalFlushFails_DoesNotPublishCleanIndexToken()
    {
        string path = Path.Combine(_root, "failed-dispose");
        var schema = SimpleSchema("failed_dispose");
        var keyspace = KvKeyspace.Open("table.failed_dispose", path, KvOptions.Default);
        var store = new TableStore(schema, keyspace);
        keyspace.WalDisposeFlushTestHook = () => throw new IOException("injected WAL flush failure");

        IOException error = Assert.Throws<IOException>(() => store.Dispose());

        Assert.Contains("injected WAL flush failure", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(path, TableStoreMaintenanceFile.CleanIndexesFileName)));
    }

    private static TableSchema SimpleSchema(string name)
        => TableSchema.Create(
            name,
            [("id", TableColumnType.Int64, false), ("name", TableColumnType.String, false)],
            ["id"]);

    private static TableSchema BlobSchema(string name)
        => TableSchema.Create(
            name,
            [("id", TableColumnType.Int64, false), ("payload", TableColumnType.Blob, false)],
            ["id"]);

    private static TableSchema IndexedSchema(string name)
        => TableSchema.Create(
            name,
            [("id", TableColumnType.Int64, false), ("site", TableColumnType.String, false)],
            ["id"],
            [new TableIndexDefinition("idx_site", ["site"], IsUnique: false, CreatedAtUtcTicks: 1)]);
}
