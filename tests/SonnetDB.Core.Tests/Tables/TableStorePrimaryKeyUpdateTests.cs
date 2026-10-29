using SonnetDB.Kv;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>验证主键迁移以单个 WAL 批次发布，并保留旧读快照。</summary>
public sealed class TableStorePrimaryKeyUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-store-pk312-" + Guid.NewGuid().ToString("N"));

    /// <summary>主行与两个索引的迁移只有一次 WAL 同步；旧快照始终看到完整旧行。</summary>
    [Fact]
    public async Task ApplyBatch_PrimaryKeyChange_PublishesOneWalBatchAndPreservesSnapshot()
    {
        var schema = TableSchema.Create("Items",
            [("Id", TableColumnType.Int64, false), ("Name", TableColumnType.String, false),
                ("GroupId", TableColumnType.Int64, false)], ["Id"],
            [new TableIndexDefinition("ux_name", ["Name"], IsUnique: true),
                new TableIndexDefinition("ix_group", ["GroupId"], IsUnique: false)]);
        using var keyspace = KvKeyspace.Open("table.Items", _root,
            KvOptions.Default with { AutoCheckpointEnabled = false, ExpirerEnabled = false });
        using var store = new TableStore(schema, keyspace);
        store.Upsert([1L, "a", 9L]);
        long beforeSequence = keyspace.LastSequence;
        int syncs = 0;
        keyspace.WalSyncTestHook = () => syncs++;
        using var captured = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.ReadSnapshotAcquiredTestHook = () =>
        {
            captured.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("snapshot wait timed out");
        };
        // 普通非唯一索引保留稳定快照同步点；唯一点读的无快照路径由专门测试验证。
        Task<IReadOnlyList<TableRow>> oldRead = Task.Run(() => store.GetByIndex(schema.Indexes[1], [9L]));
        try
        {
            Assert.True(captured.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, store.ApplyBatch([new TableRowMutation([1L], [101L, "a", 9L])]));
            Assert.Equal(beforeSequence + 1, keyspace.LastSequence);
            Assert.Equal(1, syncs);
            KvWalRecord record = KvWalFile.Replay(KvKeyspace.ActiveWalPath(_root)).Last();
            Assert.Equal(KvWalRecordKind.MutationBatch, record.Kind);
            Assert.Equal(5, KvWalFile.DecodeMutationBatch(record).Count);
            release.Set();
            Assert.Equal(1L, Assert.Single(await oldRead.WaitAsync(TimeSpan.FromSeconds(10))).Values[0]);
            store.ReadSnapshotAcquiredTestHook = null;
            Assert.Null(store.GetByPrimaryKey([1L]));
            Assert.Equal("a", store.GetByPrimaryKey([101L])!.Values[1]);
            Assert.Equal(101L, Assert.Single(store.GetByIndex(schema.Indexes[0], ["a"])).Values[0]);
            Assert.Equal(101L, Assert.Single(store.GetByIndex(schema.Indexes[1], [9L])).Values[0]);
            Assert.Equal(1, store.RowCount);
        }
        finally
        {
            release.Set();
            await oldRead.WaitAsync(TimeSpan.FromSeconds(10));
            store.ReadSnapshotAcquiredTestHook = null;
            keyspace.WalSyncTestHook = null;
        }
    }

    /// <summary>回收本夹具创建的唯一目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
