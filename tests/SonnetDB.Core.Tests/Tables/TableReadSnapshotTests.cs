using SonnetDB.Kv;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

public sealed class TableReadSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-table-read-snapshot-tests",
        Guid.NewGuid().ToString("N"));

    public TableReadSnapshotTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task GetByIndex_BlocksAfterSnapshotCapture_AllowsConcurrentWriteAndKeepsViewStable()
    {
        TableSchema schema = CreateSchema();
        using var keyspace = KvKeyspace.Open("table.devices", _root, Options());
        using var store = new TableStore(schema, keyspace);
        store.Upsert([1L, "north", "idle"]);

        using var snapshotCaptured = new ManualResetEventSlim();
        using var releaseRead = new ManualResetEventSlim();
        store.ReadSnapshotAcquiredTestHook = () =>
        {
            snapshotCaptured.Set();
            if (!releaseRead.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("test did not release the table read snapshot");
        };

        Task<IReadOnlyList<TableRow>> read = Task.Run(() =>
            store.GetByIndex(schema.Indexes[0], ["north"]));
        try
        {
            Assert.True(snapshotCaptured.Wait(TimeSpan.FromSeconds(10)));

            Task write = Task.Run(() => store.Upsert([2L, "north", "running"]));
            await write.WaitAsync(TimeSpan.FromSeconds(10));

            releaseRead.Set();
            IReadOnlyList<TableRow> rows = await read.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Single(rows);
            Assert.Equal(1L, rows[0].Values[0]);
            Assert.Equal(2, store.RowCount);
        }
        finally
        {
            releaseRead.Set();
            await read.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task GetByIndexRangeThroughValueGroup_BlocksAfterSnapshotCapture_AllowsConcurrentWrite()
    {
        TableSchema schema = TableSchema.Create(
            "events",
            [
                ("id", TableColumnType.Int64, false),
                ("tenant", TableColumnType.String, false),
                ("occurred", TableColumnType.Int64, false),
            ],
            ["id"],
            [new TableIndexDefinition("ix_tenant_occurred", ["tenant", "occurred"], IsUnique: false)]);
        using var keyspace = KvKeyspace.Open("table.events", _root, Options());
        using var store = new TableStore(schema, keyspace);
        store.Upsert([1L, "north", 10L]);

        using var snapshotCaptured = new ManualResetEventSlim();
        using var releaseRead = new ManualResetEventSlim();
        store.ReadSnapshotAcquiredTestHook = () =>
        {
            snapshotCaptured.Set();
            if (!releaseRead.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("test did not release the table read snapshot");
        };

        Task<IReadOnlyList<TableRow>> read = Task.Run(() =>
            store.GetByIndexRangeThroughValueGroup(
                schema.Indexes[0],
                ["north"],
                new TableIndexRange(
                    schema.TryGetColumn("occurred")!,
                    new TableIndexRangeBound(0, Inclusive: true),
                new TableIndexRangeBound(100, Inclusive: true)),
                candidateLimit: 1));
        try
        {
            Assert.True(snapshotCaptured.Wait(TimeSpan.FromSeconds(10)));

            Task write = Task.Run(() => store.Upsert([2L, "north", 20L]));
            await write.WaitAsync(TimeSpan.FromSeconds(10));

            releaseRead.Set();
            IReadOnlyList<TableRow> rows = await read.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Single(rows);
            Assert.Equal(1L, rows[0].Values[0]);
            Assert.Equal(2, store.RowCount);
        }
        finally
        {
            releaseRead.Set();
            await read.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void AcquireTableReadSnapshot_ExceptionFromObserver_ReleasesSnapshotLease()
    {
        TableSchema schema = CreateSchema();
        using var keyspace = KvKeyspace.Open("table.release", _root, Options());
        using var store = new TableStore(schema, keyspace);
        store.Upsert([1L, "north", "idle"]);
        store.ReadSnapshotAcquiredTestHook = static () => throw new InvalidOperationException("injected read failure");

        Assert.Throws<InvalidOperationException>(store.AcquireTableReadSnapshot);

        store.ReadSnapshotAcquiredTestHook = null;
        Assert.Single(store.GetByIndex(schema.Indexes[0], ["north"]));
        keyspace.Compact();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static TableSchema CreateSchema()
        => TableSchema.Create(
            "devices",
            [
                ("id", TableColumnType.Int64, false),
                ("site", TableColumnType.String, false),
                ("status", TableColumnType.String, false),
            ],
            ["id"],
            [new TableIndexDefinition("ix_site", ["site"], IsUnique: false)]);

    private static KvOptions Options()
        => KvOptions.Default with
        {
            AutoCheckpointEnabled = false,
            SyncWalOnEveryWrite = false,
            ExpirerEnabled = false,
            CleanupEnabled = false,
        };
}
