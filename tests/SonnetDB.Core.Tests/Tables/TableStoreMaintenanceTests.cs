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

    /// <summary>验证分页 key 扫描不会为了列表预分配而全量统计 KV。</summary>
    [Fact]
    public void ScanKeysPrefixAfter_WithLimit_DoesNotCountAllVisibleEntries()
    {
        string path = Path.Combine(_root, "bounded-key-scan");
        using var keyspace = KvKeyspace.Open("bounded-key-scan", path, KvOptions.Default);
        for (var index = 0; index < 1024; index++)
            keyspace.Put($"row:{index:D4}", "value"u8);

        var countVisibleCalls = 0;
        keyspace.CountVisibleTestHook = () => countVisibleCalls++;

        var keys = keyspace.ScanKeysPrefixAfter("row:"u8, ReadOnlySpan<byte>.Empty, limit: 16);

        Assert.Equal(16, keys.Count);
        Assert.Equal(0, countVisibleCalls);
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

    /// <summary>验证稳定前缀扫描跨多页时只创建一次底层枚举，并保持每页内存有界。</summary>
    [Fact]
    public void ReadStablePrefixPages_OverOnePage_EnumeratesOnce()
    {
        string path = Path.Combine(_root, "stable-prefix-pages");
        var options = KvOptions.Default with
        {
            AutoCheckpointEnabled = false,
            SyncWalOnEveryWrite = false,
        };
        using var keyspace = KvKeyspace.Open("stable-prefix-pages", path, options);
        for (var index = 0; index < 600; index++)
            keyspace.Put($"row:{index:D4}", "value"u8);

        int enumerations = 0;
        var pageCounts = new List<int>();
        keyspace.StablePrefixScanTestHook = prefix =>
        {
            if (prefix.Span.SequenceEqual("row:"u8))
                Interlocked.Increment(ref enumerations);
        };

        keyspace.ReadStablePrefixPages("row:"u8, 256, page => pageCounts.Add(page.Count));

        Assert.Equal(1, Volatile.Read(ref enumerations));
        Assert.Equal([256, 256, 88], pageCounts);
    }

    /// <summary>验证异常退出 using scope 后 DeleteOnClose 临时 spool 不会残留。</summary>
    [Fact]
    public void IndexRepairSpool_ExceptionalScope_DeletesTemporaryFile()
    {
        string? temporaryPath = null;
        Action exceptionalScope = () =>
        {
            using var spool = new TableIndexRepairSpool();
            temporaryPath = spool.TemporaryPath;
            spool.AppendPut("index"u8.ToArray(), "primary"u8.ToArray(), uniqueIndexOrdinal: -1);
            throw new InvalidOperationException("injected spool consumer failure");
        };

        Assert.Throws<InvalidOperationException>(exceptionalScope);

        Assert.NotNull(temporaryPath);
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public void SchemaFingerprint_WithColumnDefault_RemainsIndexCompatible()
    {
        var withoutDefault = TableSchema.Create(
            "devices",
            [("id", TableColumnType.Int64, false), ("site", TableColumnType.String, true)],
            ["id"],
            createdAtUtcTicks: 1234);
        var withDefault = TableSchema.CreateWithDefaults(
            "devices",
            [("id", TableColumnType.Int64, false), ("site", TableColumnType.String, true)],
            ["id"],
            indexes: null,
            foreignKeys: null,
            rowVersionColumns: null,
            createdAtUtcTicks: 1234,
            checkConstraints: null,
            columnDefaults: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["site"] = "'north'",
            });

        Assert.Equal(
            TableStoreMaintenanceFile.ComputeSchemaFingerprint(withoutDefault),
            TableStoreMaintenanceFile.ComputeSchemaFingerprint(withDefault));
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

    /// <summary>验证缺少 clean token 但索引完全一致时不追加 WAL，也不调度后台检查点。</summary>
    [Fact]
    public void MissingCleanToken_ConsistentIndexes_ProducesNoWritesOrCheckpoint()
    {
        string path = Path.Combine(_root, "consistent-indexes");
        var schema = IndexedSchema("consistent_indexes");
        var options = KvOptions.Default with
        {
            MaxWalBytes = long.MaxValue,
            MaxOverlayEntries = int.MaxValue,
        };
        var preparationKeyspace = KvKeyspace.Open("table.consistent_indexes", path, options);
        var preparationStore = new TableStore(schema, preparationKeyspace);
        preparationStore.Insert([1L, "north"]);
        preparationStore.Dispose();

        File.Delete(Path.Combine(path, TableStoreMaintenanceFile.CleanIndexesFileName));
        var recoveryKeyspace = KvKeyspace.Open("table.consistent_indexes", path, options);
        long sequenceBeforeOpen = recoveryKeyspace.LastSequence;
        long schedulesBeforeOpen = recoveryKeyspace.AutoCheckpointScheduleCount;
        var recoveryStore = new TableStore(schema, recoveryKeyspace);

        Assert.Equal(sequenceBeforeOpen, recoveryKeyspace.LastSequence);
        Assert.Equal(schedulesBeforeOpen, recoveryKeyspace.AutoCheckpointScheduleCount);
        Assert.Single(recoveryStore.GetByIndex(schema.Indexes[0], ["north"]));
        recoveryStore.Dispose();
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

    /// <summary>验证差量恢复会修正错值，并删除 stale、orphan 与未知索引键。</summary>
    [Fact]
    public void MissingCleanToken_DifferentialRepair_FixesWrongValueAndRemovesUnexpectedIndexes()
    {
        string path = Path.Combine(_root, "differential-index-repair");
        var schema = IndexedSchema("differential_index_repair");
        object?[] north = [1L, "north"];
        object?[] south = [2L, "south"];
        byte[] northPrimaryKey = TableKeyCodec.EncodePrimaryKey(schema, north);
        byte[] southPrimaryKey = TableKeyCodec.EncodePrimaryKey(schema, south);
        byte[] northIndexKey = TableIndexCodec.EncodeIndexEntryKey(
            schema.Indexes[0], north, schema, northPrimaryKey);
        byte[] staleIndexKey = TableIndexCodec.EncodeIndexEntryKey(
            schema.Indexes[0], [1L, "obsolete"], schema, northPrimaryKey);
        byte[] orphanPrimaryKey = TableKeyCodec.EncodePrimaryKeyValues(schema, [999L]);
        byte[] orphanIndexKey = TableIndexCodec.EncodeIndexEntryKey(
            schema.Indexes[0], [999L, "orphan"], schema, orphanPrimaryKey);
        byte[] unknownIndexKey = [(byte)'i', 0, 7, .. "removed"u8];

        var preparationKeyspace = KvKeyspace.Open("table.differential_index_repair", path, KvOptions.Default);
        var preparationStore = new TableStore(schema, preparationKeyspace);
        preparationStore.Insert(north);
        preparationStore.Insert(south);
        preparationStore.Dispose();

        var corruptedKeyspace = KvKeyspace.Open("table.differential_index_repair", path, KvOptions.Default);
        corruptedKeyspace.Put(northIndexKey, southPrimaryKey);
        corruptedKeyspace.Put(staleIndexKey, northPrimaryKey);
        corruptedKeyspace.Put(orphanIndexKey, orphanPrimaryKey);
        corruptedKeyspace.Put(unknownIndexKey, northPrimaryKey);
        corruptedKeyspace.Dispose();

        var recoveryKeyspace = KvKeyspace.Open("table.differential_index_repair", path, KvOptions.Default);
        var recoveryStore = new TableStore(schema, recoveryKeyspace);

        Assert.Equal(northPrimaryKey, recoveryKeyspace.Get(northIndexKey));
        Assert.Null(recoveryKeyspace.Get(staleIndexKey));
        Assert.Null(recoveryKeyspace.Get(orphanIndexKey));
        Assert.Null(recoveryKeyspace.Get(unknownIndexKey));
        Assert.Single(recoveryStore.GetByIndex(schema.Indexes[0], ["north"]));
        Assert.Single(recoveryStore.GetByIndex(schema.Indexes[0], ["south"]));
        recoveryStore.Dispose();
    }

    /// <summary>验证 mixed schema 中非唯一索引错值不会被误判成唯一约束冲突。</summary>
    [Fact]
    public void MissingCleanToken_MixedIndexes_RepairsNonUniqueWrongValue()
    {
        string path = Path.Combine(_root, "mixed-index-repair");
        var schema = MixedIndexedSchema("mixed_index_repair");
        var nonUniqueIndex = schema.Indexes.Single(index => index.Name == "idx_id");
        object?[] north = [1L, "north"];
        object?[] south = [2L, "south"];
        byte[] northPrimaryKey = TableKeyCodec.EncodePrimaryKey(schema, north);
        byte[] southPrimaryKey = TableKeyCodec.EncodePrimaryKey(schema, south);
        byte[] northIndexKey = TableIndexCodec.EncodeIndexEntryKey(
            nonUniqueIndex, north, schema, northPrimaryKey);

        var preparationKeyspace = KvKeyspace.Open("table.mixed_index_repair", path, KvOptions.Default);
        var preparationStore = new TableStore(schema, preparationKeyspace);
        preparationStore.Insert(north);
        preparationStore.Insert(south);
        preparationStore.Dispose();

        var corruptedKeyspace = KvKeyspace.Open("table.mixed_index_repair", path, KvOptions.Default);
        corruptedKeyspace.Put(northIndexKey, southPrimaryKey);
        corruptedKeyspace.Dispose();

        var recoveryKeyspace = KvKeyspace.Open("table.mixed_index_repair", path, KvOptions.Default);
        var recoveryStore = new TableStore(schema, recoveryKeyspace);

        Assert.Equal(northPrimaryKey, recoveryKeyspace.Get(northIndexKey));
        Assert.Single(recoveryStore.GetByIndex(nonUniqueIndex, [1L]));
        recoveryStore.Dispose();
    }

    /// <summary>验证唯一索引的 orphan 指针和已不匹配行值可以差量覆盖，而不会误报冲突。</summary>
    [Fact]
    public void MissingCleanToken_UniqueOrphanAndStalePointers_AreRepaired()
    {
        string path = Path.Combine(_root, "unique-stale-repair");
        var schema = UniqueIndexedSchema("unique_stale_repair");
        object?[] north = [1L, "north"];
        object?[] south = [2L, "south"];
        byte[] northPrimaryKey = TableKeyCodec.EncodePrimaryKey(schema, north);
        byte[] southPrimaryKey = TableKeyCodec.EncodePrimaryKey(schema, south);
        byte[] missingPrimaryKey = TableKeyCodec.EncodePrimaryKeyValues(schema, [999L]);
        byte[] northIndexKey = TableIndexCodec.EncodeIndexEntryKey(
            schema.Indexes[0], north, schema, northPrimaryKey);
        byte[] southIndexKey = TableIndexCodec.EncodeIndexEntryKey(
            schema.Indexes[0], south, schema, southPrimaryKey);

        var preparationKeyspace = KvKeyspace.Open("table.unique_stale_repair", path, KvOptions.Default);
        var preparationStore = new TableStore(schema, preparationKeyspace);
        preparationStore.Insert(north);
        preparationStore.Insert(south);
        preparationStore.Dispose();

        var corruptedKeyspace = KvKeyspace.Open("table.unique_stale_repair", path, KvOptions.Default);
        corruptedKeyspace.Put(northIndexKey, missingPrimaryKey);
        corruptedKeyspace.Put(southIndexKey, northPrimaryKey);
        corruptedKeyspace.Dispose();

        var recoveryKeyspace = KvKeyspace.Open("table.unique_stale_repair", path, KvOptions.Default);
        var recoveryStore = new TableStore(schema, recoveryKeyspace);

        Assert.Equal(northPrimaryKey, recoveryKeyspace.Get(northIndexKey));
        Assert.Equal(southPrimaryKey, recoveryKeyspace.Get(southIndexKey));
        Assert.Single(recoveryStore.GetByIndex(schema.Indexes[0], ["north"]));
        Assert.Single(recoveryStore.GetByIndex(schema.Indexes[0], ["south"]));
        recoveryStore.Dispose();
    }

    /// <summary>验证另一条存活主行仍产生同一唯一键时，差量恢复继续报告唯一约束冲突。</summary>
    [Fact]
    public void MissingCleanToken_LiveUniqueConflict_StopsRecovery()
    {
        string path = Path.Combine(_root, "unique-live-conflict");
        var schema = UniqueIndexedSchema("unique_live_conflict");
        object?[] north = [1L, "north"];
        object?[] south = [2L, "south"];
        byte[] northPrimaryKey = TableKeyCodec.EncodePrimaryKey(schema, north);
        byte[] southPrimaryKey = TableKeyCodec.EncodePrimaryKey(schema, south);
        byte[] northIndexKey = TableIndexCodec.EncodeIndexEntryKey(
            schema.Indexes[0], north, schema, northPrimaryKey);
        byte[] southRowKey = TableIndexCodec.EncodePrimaryRowKey(southPrimaryKey);

        var preparationKeyspace = KvKeyspace.Open("table.unique_live_conflict", path, KvOptions.Default);
        var preparationStore = new TableStore(schema, preparationKeyspace);
        preparationStore.Insert(north);
        preparationStore.Insert(south);
        preparationStore.Dispose();

        var corruptedKeyspace = KvKeyspace.Open("table.unique_live_conflict", path, KvOptions.Default);
        corruptedKeyspace.Put(southRowKey, TableRowCodec.Encode(schema, [2L, "north"]));
        corruptedKeyspace.Put(northIndexKey, southPrimaryKey);
        corruptedKeyspace.Dispose();

        using var recoveryKeyspace = KvKeyspace.Open("table.unique_live_conflict", path, KvOptions.Default);
        TableConstraintException error = Assert.Throws<TableConstraintException>(
            () => new TableStore(schema, recoveryKeyspace));

        Assert.Equal(TableConstraintException.UniqueViolation, error.ErrorCode);
    }

    /// <summary>验证唯一键缺失且冲突行跨过多个行扫描页边界时，spool 回放仍能发现存活冲突。</summary>
    [Fact]
    public void MissingCleanToken_CrossPageMissingUniqueKey_StopsRecovery()
    {
        string path = Path.Combine(_root, "cross-page-unique-conflict");
        var schema = UniqueIndexedSchema("cross_page_unique_conflict");
        var preparationKeyspace = KvKeyspace.Open(
            "table.cross_page_unique_conflict",
            path,
            KvOptions.Default with { SyncWalOnEveryWrite = false });
        var preparationStore = new TableStore(schema, preparationKeyspace);
        for (var index = 0; index < 257; index++)
            preparationStore.Insert([Convert.ToInt64(index), $"site-{index:D4}"]);
        preparationStore.Dispose();

        var corruptedKeyspace = KvKeyspace.Open(
            "table.cross_page_unique_conflict",
            path,
            KvOptions.Default with { SyncWalOnEveryWrite = false });
        byte[] conflictingPrimaryKey = TableKeyCodec.EncodePrimaryKeyValues(schema, [256L]);
        byte[] conflictingRowKey = TableIndexCodec.EncodePrimaryRowKey(conflictingPrimaryKey);
        corruptedKeyspace.Put(
            conflictingRowKey,
            TableRowCodec.Encode(schema, [256L, "site-0000"]));
        var indexKeys = corruptedKeyspace.ScanKeysPrefixAfter(
            [(byte)'i'],
            ReadOnlySpan<byte>.Empty,
            limit: 300);
        Assert.Equal(257, corruptedKeyspace.DeleteMany(indexKeys));
        corruptedKeyspace.Dispose();

        using var recoveryKeyspace = KvKeyspace.Open(
            "table.cross_page_unique_conflict",
            path,
            KvOptions.Default with { SyncWalOnEveryWrite = false });
        TableConstraintException error = Assert.Throws<TableConstraintException>(
            () => new TableStore(schema, recoveryKeyspace));

        Assert.Equal(TableConstraintException.UniqueViolation, error.ErrorCode);
    }

    /// <summary>验证超过一页的 stale 索引只扫描一次，并按两个有界原子批次清理。</summary>
    [Fact]
    public void MissingCleanToken_MoreThanOnePageOfStaleIndexes_ScansOnceAndCleansAll()
    {
        string path = Path.Combine(_root, "multi-page-stale-cleanup");
        var schema = IndexedSchema("multi_page_stale_cleanup");
        var options = KvOptions.Default with
        {
            AutoCheckpointEnabled = false,
            SyncWalOnEveryWrite = false,
        };
        var preparationKeyspace = KvKeyspace.Open("table.multi_page_stale_cleanup", path, options);
        var preparationStore = new TableStore(schema, preparationKeyspace);
        for (var index = 0; index < 600; index++)
            preparationStore.Insert([Convert.ToInt64(index), $"site-{index:D4}"]);
        preparationStore.Dispose();

        var corruptedKeyspace = KvKeyspace.Open("table.multi_page_stale_cleanup", path, options);
        byte[] existingPrimaryKey = TableKeyCodec.EncodePrimaryKeyValues(schema, [0L]);
        var staleKeys = new List<byte[]>(300);
        for (var index = 0; index < 300; index++)
        {
            byte[] staleKey = System.Text.Encoding.UTF8.GetBytes($"i:removed:{index:D4}");
            corruptedKeyspace.Put(staleKey, existingPrimaryKey);
            staleKeys.Add(staleKey);
        }
        corruptedKeyspace.Dispose();

        var recoveryKeyspace = KvKeyspace.Open("table.multi_page_stale_cleanup", path, options);
        int rowScans = 0;
        int indexScans = 0;
        recoveryKeyspace.StablePrefixScanTestHook = prefix =>
        {
            if (prefix.Span.SequenceEqual([(byte)'r']))
                Interlocked.Increment(ref rowScans);
            else if (prefix.Span.SequenceEqual([(byte)'i']))
                Interlocked.Increment(ref indexScans);
        };
        long sequenceBeforeRecovery = recoveryKeyspace.LastSequence;
        var recoveryStore = new TableStore(schema, recoveryKeyspace);

        Assert.Equal(1, Volatile.Read(ref rowScans));
        Assert.Equal(1, Volatile.Read(ref indexScans));
        Assert.Equal(sequenceBeforeRecovery + 2, recoveryKeyspace.LastSequence);
        foreach (byte[] staleKey in staleKeys)
            Assert.Null(recoveryKeyspace.Get(staleKey));
        Assert.Single(recoveryStore.GetByIndex(schema.Indexes[0], ["site-0000"]));
        Assert.Single(recoveryStore.GetByIndex(schema.Indexes[0], ["site-0599"]));
        recoveryStore.Dispose();
    }

    /// <summary>验证恢复覆盖层预算低于默认回放页时会自动缩页，并通过检查点持续推进。</summary>
    [Fact]
    public void MissingCleanToken_IndexRebuildOverlayBudgetBelowPreferredPage_Completes()
    {
        string path = Path.Combine(_root, "small-index-rebuild-overlay-budget");
        var schema = IndexedSchema("small_index_rebuild_overlay_budget");
        var preparationOptions = KvOptions.Default with { SyncWalOnEveryWrite = false };
        var preparationKeyspace = KvKeyspace.Open(
            "table.small_index_rebuild_overlay_budget",
            path,
            preparationOptions);
        var preparationStore = new TableStore(schema, preparationKeyspace);
        for (var index = 0; index < 5; index++)
            preparationStore.Insert([Convert.ToInt64(index), $"site-{index:D4}"]);
        preparationKeyspace.Compact();
        var preparedIndexKeys = preparationKeyspace.ScanKeysPrefixAfter(
            [(byte)'i'],
            ReadOnlySpan<byte>.Empty,
            limit: 10);
        Assert.Equal(5, preparationKeyspace.DeleteMany(preparedIndexKeys));
        preparationKeyspace.Compact();
        preparationStore.Dispose();

        File.Delete(Path.Combine(path, TableStoreMaintenanceFile.CleanIndexesFileName));
        var recoveryOptions = KvOptions.Default with
        {
            SyncWalOnEveryWrite = false,
            MaxWalBytes = long.MaxValue,
            MaxOverlayEntries = 1,
            IndexRebuildMaxWalBytes = long.MaxValue,
            IndexRebuildMaxOverlayEntries = 1,
            CheckpointWriteBackpressureTimeout = TimeSpan.FromSeconds(5),
        };
        var recoveryKeyspace = KvKeyspace.Open(
            "table.small_index_rebuild_overlay_budget",
            path,
            recoveryOptions);
        var recoveryStore = new TableStore(schema, recoveryKeyspace);

        Assert.Equal(5, recoveryStore.RowCount);
        Assert.True(recoveryKeyspace.AutoCheckpointScheduleCount > 0);
        Assert.Single(recoveryStore.GetByIndex(schema.Indexes[0], ["site-0000"]));
        Assert.Single(recoveryStore.GetByIndex(schema.Indexes[0], ["site-0004"]));
        recoveryStore.Dispose();
    }

    /// <summary>验证单条索引 mutation 超出恢复 WAL 预算时在追加前给出明确错误。</summary>
    [Fact]
    public void MissingCleanToken_SingleIndexMutationExceedsWalBudget_FailsBeforeWrite()
    {
        string path = Path.Combine(_root, "index-rebuild-mutation-over-wal-budget");
        var schema = IndexedSchema("index_rebuild_mutation_over_wal_budget");
        var preparationOptions = KvOptions.Default with { SyncWalOnEveryWrite = false };
        var preparationKeyspace = KvKeyspace.Open(
            "table.index_rebuild_mutation_over_wal_budget",
            path,
            preparationOptions);
        var preparationStore = new TableStore(schema, preparationKeyspace);
        preparationStore.Insert([1L, "north"]);
        preparationKeyspace.Compact();
        var preparedIndexKeys = preparationKeyspace.ScanKeysPrefixAfter(
            [(byte)'i'],
            ReadOnlySpan<byte>.Empty,
            limit: 2);
        Assert.Single(preparedIndexKeys);
        Assert.Equal(1, preparationKeyspace.DeleteMany(preparedIndexKeys));
        preparationKeyspace.Compact();
        preparationStore.Dispose();

        File.Delete(Path.Combine(path, TableStoreMaintenanceFile.CleanIndexesFileName));
        var recoveryOptions = KvOptions.Default with
        {
            SyncWalOnEveryWrite = false,
            IndexRebuildMaxWalBytes = KvWalFile.HeaderSize,
            IndexRebuildMaxOverlayEntries = 1,
        };
        using var recoveryKeyspace = KvKeyspace.Open(
            "table.index_rebuild_mutation_over_wal_budget",
            path,
            recoveryOptions);
        long sequenceBeforeRecovery = recoveryKeyspace.LastSequence;

        IOException error = Assert.Throws<IOException>(() => new TableStore(schema, recoveryKeyspace));

        Assert.True(KvAtomicBatchErrors.IsTooLarge(error));
        Assert.Contains("batch itself exceeds the fresh checkpoint budget", error.Message, StringComparison.Ordinal);
        Assert.Equal(sequenceBeforeRecovery, recoveryKeyspace.LastSequence);
    }

    /// <summary>验证恢复 WAL 预算可容纳单条但不足整页时，会有界拆分并完成全部索引补写。</summary>
    [Fact]
    public void MissingCleanToken_IndexRepairPageExceedsWalBudget_SplitsAndCompletes()
    {
        string path = Path.Combine(_root, "index-rebuild-page-over-wal-budget");
        var schema = IndexedSchema("index_rebuild_page_over_wal_budget");
        object?[][] rows = Enumerable.Range(0, 5)
            .Select(index => new object?[] { Convert.ToInt64(index), $"site-{index:D4}" })
            .ToArray();
        var repairMutations = new List<KvBatchMutation>(rows.Length);
        foreach (object?[] row in rows)
        {
            byte[] primaryKey = TableKeyCodec.EncodePrimaryKey(schema, row);
            byte[] indexKey = TableIndexCodec.EncodeIndexEntryKey(
                schema.Indexes[0],
                row,
                schema,
                primaryKey);
            repairMutations.Add(KvBatchMutation.Put(
                indexKey,
                TableIndexCodec.EncodeIndexEntryValue(primaryKey)));
        }

        long singleMutationWalBytes = repairMutations.Max(mutation =>
            KvWalFile.CalculateMutationBatchRecordBytes([mutation]));
        long rebuildWalBudget = checked(KvWalFile.HeaderSize + singleMutationWalBytes);
        Assert.True(
            KvWalFile.HeaderSize + KvWalFile.CalculateMutationBatchRecordBytes(repairMutations)
                > rebuildWalBudget);

        var preparationOptions = KvOptions.Default with { SyncWalOnEveryWrite = false };
        var preparationKeyspace = KvKeyspace.Open(
            "table.index_rebuild_page_over_wal_budget",
            path,
            preparationOptions);
        var preparationStore = new TableStore(schema, preparationKeyspace);
        foreach (object?[] row in rows)
            preparationStore.Insert(row);
        preparationKeyspace.Compact();
        var preparedIndexKeys = preparationKeyspace.ScanKeysPrefixAfter(
            [(byte)'i'],
            ReadOnlySpan<byte>.Empty,
            limit: 10);
        Assert.Equal(rows.Length, preparationKeyspace.DeleteMany(preparedIndexKeys));
        preparationKeyspace.Compact();
        preparationStore.Dispose();

        File.Delete(Path.Combine(path, TableStoreMaintenanceFile.CleanIndexesFileName));
        var recoveryOptions = KvOptions.Default with
        {
            SyncWalOnEveryWrite = false,
            IndexRebuildMaxWalBytes = rebuildWalBudget,
            IndexRebuildMaxOverlayEntries = 100,
            CheckpointWriteBackpressureTimeout = TimeSpan.FromSeconds(5),
        };
        var recoveryKeyspace = KvKeyspace.Open(
            "table.index_rebuild_page_over_wal_budget",
            path,
            recoveryOptions);
        long sequenceBeforeRecovery = recoveryKeyspace.LastSequence;
        var recoveryStore = new TableStore(schema, recoveryKeyspace);

        Assert.Equal(sequenceBeforeRecovery + rows.Length, recoveryKeyspace.LastSequence);
        Assert.Equal(rows.Length, recoveryStore.RowCount);
        Assert.Single(recoveryStore.GetByIndex(schema.Indexes[0], ["site-0000"]));
        Assert.Single(recoveryStore.GetByIndex(schema.Indexes[0], ["site-0004"]));
        recoveryStore.Dispose();
    }

    /// <summary>验证大索引重建跨越当前检查点预算时会等待落盘并继续，而不是让表打开失败。</summary>
    [Fact]
    public void MissingCleanToken_IndexRebuildCrossesCheckpointBudget_Completes()
    {
        string path = Path.Combine(_root, "budgeted-index-rebuild");
        var schema = IndexedSchema("budgeted_index_rebuild");
        var preparationOptions = KvOptions.Default with { SyncWalOnEveryWrite = false };
        var preparationKeyspace = KvKeyspace.Open("table.budgeted_index_rebuild", path, preparationOptions);
        var preparationStore = new TableStore(schema, preparationKeyspace);
        for (var index = 0; index < 2_300; index++)
            preparationStore.Insert([Convert.ToInt64(index), $"site-{index:D4}"]);
        preparationKeyspace.Compact();
        var preparedIndexKeys = preparationKeyspace.ScanKeysPrefixAfter(
            [(byte)'i'],
            ReadOnlySpan<byte>.Empty,
            limit: 2_500);
        Assert.Equal(2_300, preparationKeyspace.DeleteMany(preparedIndexKeys));
        preparationKeyspace.Compact();
        preparationStore.Dispose();

        File.Delete(Path.Combine(path, TableStoreMaintenanceFile.CleanIndexesFileName));
        var recoveryOptions = KvOptions.Default with
        {
            SyncWalOnEveryWrite = false,
            MaxOverlayEntries = 1_100,
            IndexRebuildMaxWalBytes = long.MaxValue,
            IndexRebuildMaxOverlayEntries = 1_100,
            CheckpointWriteBackpressureTimeout = TimeSpan.FromMilliseconds(1),
        };
        var recoveryKeyspace = KvKeyspace.Open("table.budgeted_index_rebuild", path, recoveryOptions);
        var checkpointCount = 0;
        recoveryKeyspace.CheckpointTestHook = phase =>
        {
            if (phase == KvCheckpointPhase.AfterFreeze)
            {
                Interlocked.Increment(ref checkpointCount);
                Thread.Sleep(100);
            }
        };
        var recoveryStore = new TableStore(schema, recoveryKeyspace);

        Assert.Equal(2_300, recoveryStore.RowCount);
        Assert.True(Volatile.Read(ref checkpointCount) > 0);
        Assert.Single(recoveryStore.GetByIndex(schema.Indexes[0], ["site-0000"]));
        Assert.Single(recoveryStore.GetByIndex(schema.Indexes[0], ["site-2299"]));
        recoveryStore.Dispose();
    }

    /// <summary>验证索引维护遇到真实检查点写盘失败时保留原异常并终止打开。</summary>
    [Fact]
    public void MissingCleanToken_CheckpointIoFailure_PropagatesWithoutRetryLoop()
    {
        string path = Path.Combine(_root, "failed-index-checkpoint");
        var schema = IndexedSchema("failed_index_checkpoint");
        var preparationOptions = KvOptions.Default with { SyncWalOnEveryWrite = false };
        var preparationKeyspace = KvKeyspace.Open("table.failed_index_checkpoint", path, preparationOptions);
        var preparationStore = new TableStore(schema, preparationKeyspace);
        for (var index = 0; index < 1_100; index++)
            preparationStore.Insert([Convert.ToInt64(index), $"site-{index:D4}"]);
        preparationKeyspace.Compact();
        var preparedIndexKeys = preparationKeyspace.ScanKeysPrefixAfter(
            [(byte)'i'],
            ReadOnlySpan<byte>.Empty,
            limit: 1_200);
        Assert.Equal(1_100, preparationKeyspace.DeleteMany(preparedIndexKeys));
        preparationKeyspace.Compact();
        preparationStore.Dispose();

        File.Delete(Path.Combine(path, TableStoreMaintenanceFile.CleanIndexesFileName));
        var recoveryOptions = KvOptions.Default with
        {
            SyncWalOnEveryWrite = false,
            MaxOverlayEntries = 1_050,
            IndexRebuildMaxWalBytes = long.MaxValue,
            IndexRebuildMaxOverlayEntries = 1_050,
        };
        using var recoveryKeyspace = KvKeyspace.Open("table.failed_index_checkpoint", path, recoveryOptions);
        recoveryKeyspace.CheckpointTestHook = phase =>
        {
            if (phase == KvCheckpointPhase.BeforeStateDirectoryFsync)
                throw new UnauthorizedAccessException("injected index maintenance checkpoint failure");
        };

        IOException error = Assert.Throws<IOException>(() => new TableStore(schema, recoveryKeyspace));

        var checkpointFailure = Assert.IsType<UnauthorizedAccessException>(error.InnerException);
        Assert.Contains("injected index maintenance checkpoint failure", checkpointFailure.Message, StringComparison.Ordinal);
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

    private static TableSchema UniqueIndexedSchema(string name)
        => TableSchema.Create(
            name,
            [("id", TableColumnType.Int64, false), ("site", TableColumnType.String, false)],
            ["id"],
            [new TableIndexDefinition("ux_site", ["site"], IsUnique: true, CreatedAtUtcTicks: 1)]);

    private static TableSchema MixedIndexedSchema(string name)
        => TableSchema.Create(
            name,
            [("id", TableColumnType.Int64, false), ("site", TableColumnType.String, false)],
            ["id"],
            [
                new TableIndexDefinition("ux_site", ["site"], IsUnique: true, CreatedAtUtcTicks: 1),
                new TableIndexDefinition("idx_id", ["id"], IsUnique: false, CreatedAtUtcTicks: 2),
            ]);
}
