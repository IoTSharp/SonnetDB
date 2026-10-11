using SonnetDB.Kv;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>验证大字段投影恢复的分配预算及 JSON 索引一致性。</summary>
public sealed class TableStoreRecoveryProjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-recovery-projection-" + Guid.NewGuid().ToString("N"));

    /// <summary>回收本测试专用存储目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>验证冷开索引恢复仅分配磁盘原始行缓冲，不复制或解码无关大字段。</summary>
    [Fact]
    public void MissingCleanToken_DiskRowsWithLargeUnindexedPayload_AllocatesOneRawBufferPerRow()
    {
        var schema = TableSchema.Create(
            "large_recovery",
            [("id", TableColumnType.Int64, false), ("site", TableColumnType.String, false),
                ("audit", TableColumnType.Json, false), ("media", TableColumnType.Blob, false)],
            ["id"],
            [new TableIndexDefinition("idx_site", ["site"], false, 1)]);
        var options = KvOptions.Default with { AutoCheckpointEnabled = false, SyncWalOnEveryWrite = false };
        string audit = "{\"value\":\"" + new string('x', 1024 * 1024) + "\"}";
        byte[] media = new byte[2 * 1024 * 1024];
        long rawRowBytes = 0;
        using (var preparationKeyspace = KvKeyspace.Open("table.large_recovery", _root, options))
        using (var preparationStore = new TableStore(schema, preparationKeyspace))
        {
            // 五行跨过恢复默认的四行页，验证跨页时也不重复保留大行副本。
            for (int index = 0; index < 5; index++)
            {
                object?[] values = [Convert.ToInt64(index), "north", audit, media];
                rawRowBytes += TableRowCodec.Encode(schema, values).Length;
                preparationStore.Insert(values);
            }
            preparationKeyspace.Compact();
        }

        File.Delete(Path.Combine(_root, TableStoreMaintenanceFile.CleanIndexesFileName));
        using var recoveryKeyspace = KvKeyspace.Open("table.large_recovery", _root, options);
        long sequence = recoveryKeyspace.LastSequence;
        long before = GC.GetAllocatedBytesForCurrentThread();
        using var recoveryStore = new TableStore(schema, recoveryKeyspace);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(5, recoveryStore.RowCount);
        Assert.Equal(sequence, recoveryKeyspace.LastSequence);
        Assert.True(allocated < rawRowBytes + 1024 * 1024,
            $"恢复分配 {allocated:N0} 字节，原始行总量 {rawRowBytes:N0} 字节；无关大字段应不再复制或解码。");
        Assert.Equal(5, recoveryStore.GetByIndex(schema.Indexes[0], ["north"]).Count);
    }

    /// <summary>验证 JSON path 索引依赖列继续解码，并正确补齐缺失项、清理 stale 项。</summary>
    [Fact]
    public void MissingCleanToken_JsonPathAndUnindexedPayload_RepairsExpectedAndStaleIndexes()
    {
        var schema = TableSchema.Create(
            "json_recovery",
            [("id", TableColumnType.Int64, false), ("indexed_json", TableColumnType.Json, false),
                ("audit", TableColumnType.String, false)],
            ["id"],
            [new TableIndexDefinition("idx_site", ["indexed_json"], false, 1, "$.site")]);
        var options = KvOptions.Default with { AutoCheckpointEnabled = false, SyncWalOnEveryWrite = false };
        object?[] values = [1L, "{\"site\":\"north\"}", new string('x', 4096)];
        byte[] primaryKey = TableKeyCodec.EncodePrimaryKey(schema, values);
        byte[] indexKey = TableIndexCodec.EncodeIndexEntryKey(schema.Indexes[0], values, schema, primaryKey);
        byte[] staleIndexKey = TableIndexCodec.EncodeIndexEntryKey(
            schema.Indexes[0], [1L, "{\"site\":\"obsolete\"}", ""], schema, primaryKey);
        using (var preparationKeyspace = KvKeyspace.Open("table.json_recovery", _root, options))
        using (var preparationStore = new TableStore(schema, preparationKeyspace))
            preparationStore.Insert(values);

        using (var corruptedKeyspace = KvKeyspace.Open("table.json_recovery", _root, options))
        {
            Assert.True(corruptedKeyspace.Delete(indexKey));
            corruptedKeyspace.Put(staleIndexKey, primaryKey);
        }

        using var recoveryKeyspace = KvKeyspace.Open("table.json_recovery", _root, options);
        using var recoveryStore = new TableStore(schema, recoveryKeyspace);
        Assert.Equal(primaryKey, recoveryKeyspace.Get(indexKey));
        Assert.Null(recoveryKeyspace.Get(staleIndexKey));
        TableRow recovered = Assert.Single(recoveryStore.GetByIndex(schema.Indexes[0], ["north"]));
        Assert.Equal(values, recovered.Values);
    }
}
