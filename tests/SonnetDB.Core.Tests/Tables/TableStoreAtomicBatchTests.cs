using SonnetDB.Kv;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

public sealed class TableStoreAtomicBatchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-table-atomic-batch-tests",
        Guid.NewGuid().ToString("N"));

    public TableStoreAtomicBatchTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ApplyBatch_UnchangedSecondaryIndex_AppendsRowsOnlyAndSyncsOnce()
    {
        TableSchema schema = TableSchema.Create(
            "devices",
            [
                ("id", TableColumnType.Int64, false),
                ("site", TableColumnType.String, false),
                ("status", TableColumnType.String, false),
            ],
            ["id"],
            [new TableIndexDefinition("idx_site", ["site"], IsUnique: true, CreatedAtUtcTicks: 1)]);
        var options = KvOptions.Default with
        {
            AutoCheckpointEnabled = false,
            ExpirerEnabled = false,
        };
        var keyspace = KvKeyspace.Open("table.devices", _root, options);
        using var store = new TableStore(schema, keyspace);
        store.Upsert([1L, "north", "idle"]);
        store.Upsert([2L, "south", "idle"]);
        long previousSequence = keyspace.LastSequence;
        long previousUniqueScans = store.UniqueIndexValidationScanCount;
        int syncCount = 0;
        keyspace.WalSyncTestHook = () => syncCount++;

        int affected = store.ApplyBatch(
        [
            new TableRowMutation([1L], [1L, "north", "running"]),
            new TableRowMutation([2L], [2L, "south", "stopped"]),
        ]);

        Assert.Equal(2, affected);
        Assert.Equal(previousSequence + 1, keyspace.LastSequence);
        Assert.Equal(1, syncCount);
        Assert.Equal(previousUniqueScans, store.UniqueIndexValidationScanCount);
        KvWalRecord record = KvWalFile.Replay(KvKeyspace.ActiveWalPath(_root)).Last();
        Assert.Equal(KvWalRecordKind.MutationBatch, record.Kind);
        IReadOnlyList<KvBatchMutation> mutations = KvWalFile.DecodeMutationBatch(record);
        Assert.Equal(2, mutations.Count);
        Assert.All(mutations, mutation => Assert.Equal((byte)'r', mutation.Key[0]));

        Assert.Equal("running", Assert.Single(store.GetByIndex(schema.Indexes[0], ["north"])).Values[2]);
        Assert.Equal("stopped", Assert.Single(store.GetByIndex(schema.Indexes[0], ["south"])).Values[2]);
        keyspace.WalSyncTestHook = null;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
