using SonnetDB.Engine;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>验证唯一索引点读的有界分配、原子可见性及原有约束和恢复语义。</summary>
public sealed class TableUniqueIndexPointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-unique-point-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>为本测试建立独占数据库目录，结束时仅回收此目录。</summary>
    public TableUniqueIndexPointTests() => Directory.CreateDirectory(_root);

    /// <summary>微试完整唯一键的命中与未命中都不构建全表快照。</summary>
    [Fact]
    public void UniquePoint_HitAndMiss_DoNotBuildSnapshot()
    {
        TableSchema schema = CreateSchema();
        using var keyspace = KvKeyspace.Open("point", _root, Options());
        using var store = new TableStore(schema, keyspace);
        store.Insert([1L, "north", "camera-1", "payload"]);
        long builds = keyspace.SnapshotOverlayCacheBuildCount;
        store.ReadSnapshotAcquiredTestHook = static () => throw new InvalidOperationException("unique point unexpectedly acquired a snapshot");

        Assert.Equal(1L, Assert.Single(store.GetByIndex(schema.Indexes[0], ["camera-1"])).Values[0]);
        Assert.Empty(store.GetByIndex(schema.Indexes[0], ["missing"]));
        Assert.Single(store.GetByIndexPrefix(schema.Indexes[0], ["camera-1"]));
        Assert.Single(store.EnumerateByIndexPrefix(schema.Indexes[0], ["camera-1"]));
        Assert.Equal(builds, keyspace.SnapshotOverlayCacheBuildCount);
        store.ReadSnapshotAcquiredTestHook = null;
        Assert.Empty(store.GetByIndex(schema.Indexes[0], ["camera-1"], limit: 0));
    }

    /// <summary>持续写入使快照缓存失效时，点读的分配仍与大型覆盖层规模无关。</summary>
    [Fact]
    public void UniquePoint_AfterInterleavedWrites_HasBoundedAllocation()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        TableSchema schema = CreateSchema();
        using var keyspace = KvKeyspace.Open("large-point", _root, Options());
        using var store = new TableStore(schema, keyspace);
        var seed = new List<IReadOnlyList<object?>>(4096);
        for (int position = 0; position < 4096; position++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            seed.Add(new object?[] { (long)position, "north", $"camera-{position}", "payload" });
        }
        store.InsertMany(seed);
        _ = store.GetByIndex(schema.Indexes[0], ["camera-1"]);
        long builds = keyspace.SnapshotOverlayCacheBuildCount;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int position = 0; position < 64; position++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            store.Upsert([1L, "north", "camera-1", $"payload-{position}"]);
            Assert.Equal($"payload-{position}", Assert.Single(store.GetByIndex(schema.Indexes[0], ["camera-1"])).Values[3]);
            Assert.Empty(store.GetByIndex(schema.Indexes[0], ["missing"]));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(builds, keyspace.SnapshotOverlayCacheBuildCount);
        Assert.True(allocated < 1024 * 1024, $"64 writes and 128 point reads allocated {allocated:N0} bytes.");
    }

    /// <summary>覆盖点读不回表、不构建快照，并保留取消和零行上限。</summary>
    [Fact]
    public void CoveredUniquePoint_DecodesOnlyIndexAndHonorsCancellation()
    {
        TableSchema schema = CreateSchema();
        using var keyspace = KvKeyspace.Open("covered-point", _root, Options());
        using var store = new TableStore(schema, keyspace);
        store.Insert([1L, "north", "camera-1", new string('x', 1024 * 1024)]);
        long builds = keyspace.SnapshotOverlayCacheBuildCount;
        store.RowDecodedTestHook = static _ => throw new InvalidOperationException("covered point unexpectedly decoded a base row");
        TableRow row = Assert.Single(store.EnumerateCoveredIndex(schema.Indexes[0], ["camera-1"]));
        Assert.Equal("camera-1", row.Values[2]);
        Assert.Null(row.Values[3]);
        Assert.Empty(store.EnumerateCoveredIndex(schema.Indexes[0], ["missing"]));
        Assert.Empty(store.EnumerateCoveredIndex(schema.Indexes[0], ["camera-1"], limit: 0));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.EnumerateCoveredIndex(schema.Indexes[0], ["camera-1"], cancellationToken: canceled.Token).ToArray());
        Assert.Equal(builds, keyspace.SnapshotOverlayCacheBuildCount);
    }

    /// <summary>批量完整唯一键保持输入顺序、未命中跳过和行数上限。</summary>
    [Fact]
    public void EncodedUniquePoints_KeepOrderAndLimitWithoutSnapshot()
    {
        TableSchema schema = CreateSchema();
        using var keyspace = KvKeyspace.Open("batch-point", _root, Options());
        using var store = new TableStore(schema, keyspace);
        store.Insert([1L, "north", "camera-1", "one"]);
        store.Insert([2L, "north", "camera-2", "two"]);
        TableIndex index = schema.Indexes[0];
        byte[][] keys = [TableIndexCodec.EncodeLookupPrefix(index, ["missing"], schema)!,
            TableIndexCodec.EncodeLookupPrefix(index, ["camera-2"], schema)!, TableIndexCodec.EncodeLookupPrefix(index, ["camera-1"], schema)!];
        long builds = keyspace.SnapshotOverlayCacheBuildCount;
        Assert.Equal(new object?[] { 2L, 1L }, store.GetByEncodedIndexPrefixes(index, keys, uniquePointLookup: true).Select(static row => row.Values[0]));
        Assert.Equal(2L, Assert.Single(store.GetByEncodedIndexPrefixes(index, keys, uniquePointLookup: true, limit: 1)).Values[0]);
        Assert.Empty(store.GetByEncodedIndexPrefixes(index, keys, uniquePointLookup: true, limit: 0));
        Assert.Equal(builds, keyspace.SnapshotOverlayCacheBuildCount);
    }

    /// <summary>精确唯一性校验仍拒绝持久或批内重复，并在失败前保持 WAL 和原行不变。</summary>
    [Fact]
    public void ExactUniqueValidation_PreservesConflictsAndAtomicBatch()
    {
        TableSchema schema = CreateSchema();
        using var keyspace = KvKeyspace.Open("validate-point", _root, Options());
        using var store = new TableStore(schema, keyspace);
        store.Insert([1L, "north", "camera-1", "one"]);
        store.Insert([2L, "north", "camera-2", "two"]);
        long sequence = keyspace.LastSequence;
        long scans = store.UniqueIndexValidationScanCount;
        Assert.Throws<TableConstraintException>(() => store.Insert([3L, "north", "camera-1", "duplicate"]));
        Assert.Throws<TableConstraintException>(() => store.ApplyBatch([
            new TableRowMutation([1L], [1L, "north", "new-camera", "changed"]),
            new TableRowMutation([2L], [2L, "north", "new-camera", "changed"])]));
        Assert.Equal(sequence, keyspace.LastSequence);
        Assert.Equal("one", Assert.Single(store.GetByIndex(schema.Indexes[0], ["camera-1"])).Values[3]);
        store.Upsert([1L, "north", "renamed-camera", "updated"]);
        Assert.Empty(store.GetByIndex(schema.Indexes[0], ["camera-1"]));
        Assert.Equal(1L, Assert.Single(store.GetByIndex(schema.Indexes[0], ["renamed-camera"])).Values[0]);
        Assert.Equal(scans, store.UniqueIndexValidationScanCount);
    }

    /// <summary>复合唯一键允许空值，部分前缀继续返回多行并使用稳定快照。</summary>
    [Fact]
    public void NullableAndPartialUniqueKeys_RetainFallbackSemantics()
    {
        TableSchema schema = CreateSchema(composite: true, nullable: true);
        using var keyspace = KvKeyspace.Open("partial-point", _root, Options());
        using var store = new TableStore(schema, keyspace);
        store.InsertMany([[1L, "north", "one", "one"], [2L, "north", "two", "two"],
            [3L, "north", null, "null-one"], [4L, "north", null, "null-two"]]);
        int snapshots = 0;
        store.ReadSnapshotAcquiredTestHook = () => snapshots++;
        Assert.Single(store.GetByIndex(schema.Indexes[0], ["north", "one"]));
        Assert.Equal(0, snapshots);
        Assert.Equal(2, store.GetByIndexPrefix(schema.Indexes[0], ["north"]).Count);
        Assert.Empty(store.GetByIndex(schema.Indexes[0], ["north", null]));
        Assert.Equal(2, snapshots);
        Assert.Equal(4, store.RowCount);
    }

    /// <summary>普通非唯一索引继续返回全部命中，并保留原来的快照读取路径。</summary>
    [Fact]
    public void NonUniquePoint_RetainsMultipleRowsAndSnapshot()
    {
        TableSchema schema = CreateSchema(unique: false);
        using var keyspace = KvKeyspace.Open("nonunique-point", _root, Options());
        using var store = new TableStore(schema, keyspace);
        store.InsertMany([[1L, "north", "shared", "one"], [2L, "north", "shared", "two"]]);
        int snapshots = 0;
        store.ReadSnapshotAcquiredTestHook = () => snapshots++;
        Assert.Equal(2, store.GetByIndex(schema.Indexes[0], ["shared"]).Count);
        Assert.Equal(1, snapshots);
    }

    /// <summary>唯一 JSON path 保持原来的读取路径、空路径规则与重复值约束。</summary>
    [Fact]
    public void JsonUniqueIndex_RetainsLookupAndConstraintSemantics()
    {
        TableSchema schema = TableSchema.Create("json_devices", [("id", TableColumnType.Int64, false), ("document", TableColumnType.Json, true)],
            ["id"], [new TableIndexDefinition("ux_json_name", ["document"], true, JsonPath: "$.name")]);
        using var keyspace = KvKeyspace.Open("json-point", _root, Options());
        using var store = new TableStore(schema, keyspace);
        store.Insert([1L, "{\"name\":\"one\"}"]);
        Assert.Throws<TableConstraintException>(() => store.Insert([2L, "{\"name\":\"one\"}"]));
        store.Insert([3L, "{}"]);
        store.Insert([4L, "{}"]);
        int snapshots = 0;
        store.ReadSnapshotAcquiredTestHook = () => snapshots++;
        Assert.Single(store.GetByIndex(schema.Indexes[0], ["one"]));
        Assert.Equal(1, snapshots);
    }

    /// <summary>SQL 普通、覆盖及 IN 唯一键查询都走精确读取，并在重开后返回已提交值。</summary>
    [Fact]
    public void SqlUniquePoint_AndReopen_PreserveCommittedRows()
    {
        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root, Kv = Options() }))
        {
            SqlExecutor.Execute(database, "CREATE TABLE devices (id INT, name STRING, payload STRING, PRIMARY KEY (id))");
            SqlExecutor.Execute(database, "CREATE UNIQUE INDEX ux_name ON devices (name)");
            SqlExecutor.Execute(database, "INSERT INTO devices (id, name, payload) VALUES (1, 'one', 'payload'), (2, 'two', 'second')");
            TableStore store = database.Tables.Open("devices");
            store.ReadSnapshotAcquiredTestHook = static () => throw new InvalidOperationException("SQL unique point unexpectedly acquired a snapshot");
            Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, "SELECT payload FROM devices WHERE name = 'one' LIMIT 1")).Rows);
            Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, "SELECT name FROM devices WHERE name = 'one' LIMIT 1")).Rows);
            Assert.Equal(2, Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, "SELECT payload FROM devices WHERE name IN ('two', 'one')")).Rows.Count);
        }
        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root, Kv = Options() });
        Assert.Equal("payload", Assert.Single(reopened.Tables.Open("devices").GetByIndex(reopened.Tables.Catalog.TryGet("devices")!.Indexes[0], ["one"])).Values[2]);
        Assert.Throws<TableConstraintException>(() => reopened.Tables.Open("devices").Insert([3L, "one", "duplicate"]));
    }

    /// <summary>磁盘索引与主行之间暂停时，更新不能穿过点读窗口产生索引与主行不一致。</summary>
    [Fact]
    public async Task UniquePoint_ConcurrentIndexChange_HasAtomicVisibility()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        TableSchema schema = CreateSchema();
        using var keyspace = KvKeyspace.Open("concurrent-point", _root, Options());
        using var store = new TableStore(schema, keyspace);
        store.Insert([1L, "north", "before", "payload"]);
        keyspace.Compact();
        using var readStarted = new ManualResetEventSlim();
        using var releaseRead = new ManualResetEventSlim();
        using var writeStarted = new ManualResetEventSlim();
        int reads = 0;
        keyspace.ConfigureDiskReadTestHook(() =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                readStarted.Set();
                if (!releaseRead.Wait(TimeSpan.FromSeconds(10), deadline.Token))
                    throw new TimeoutException("test did not release the unique index disk read");
            }
        });
        Task<IReadOnlyList<TableRow>> reader = Task.Run(() => store.GetByIndex(schema.Indexes[0], ["before"]), deadline.Token);
        Task? writer = null;
        try
        {
            Assert.True(readStarted.Wait(TimeSpan.FromSeconds(10), deadline.Token));
            writer = Task.Run(() =>
            {
                writeStarted.Set();
                store.Upsert([1L, "north", "after", "updated"]);
            }, deadline.Token);
            Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(10), deadline.Token));
            Assert.NotSame(writer, await Task.WhenAny(writer, Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token)));
            releaseRead.Set();
            TableRow row = Assert.Single(await reader.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("before", row.Values[2]);
            Assert.Equal("payload", row.Values[3]);
            await writer.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(store.GetByIndex(schema.Indexes[0], ["before"]));
            Assert.Equal("updated", Assert.Single(store.GetByIndex(schema.Indexes[0], ["after"])).Values[3]);
        }
        finally
        {
            releaseRead.Set();
            await reader.WaitAsync(TimeSpan.FromSeconds(10));
            if (writer is not null)
                await writer.WaitAsync(TimeSpan.FromSeconds(10));
            keyspace.ConfigureDiskReadTestHook(null);
        }
    }

    /// <summary>创建可切换复合、空值和唯一性条件的最小相机表。</summary>
    private static TableSchema CreateSchema(bool unique = true, bool composite = false, bool nullable = false)
        => TableSchema.Create("devices", [("id", TableColumnType.Int64, false), ("region", TableColumnType.String, false),
            ("name", TableColumnType.String, nullable), ("payload", TableColumnType.String, false)],
            ["id"], [new TableIndexDefinition("ix_name", composite ? ["region", "name"] : ["name"], unique)]);

    /// <summary>本地回归关闭后台维护，避免引入非本测试的线程和落盘噪声。</summary>
    private static KvOptions Options() => KvOptions.Default with
    {
        AutoCheckpointEnabled = false,
        SyncWalOnEveryWrite = false,
        ExpirerEnabled = false,
        CleanupEnabled = false,
    };

    /// <summary>回收以本测试唯一随机身份创建的临时数据库，不触碰其它测试或用户目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
