using System.Buffers.Binary;
using SonnetDB.Catalog;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Graphs;
using SonnetDB.Graphs.Storage;
using SonnetDB.Kv;
using SonnetDB.Model;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Format;
using SonnetDB.Tables;
using SonnetDB.Views;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class GraphManagerTests : IDisposable
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-graph-manager-" + Guid.NewGuid().ToString("N"));

    public GraphManagerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void CreateOpenDropAndReopen_PreservesStableDefinitionAndLifecycle()
    {
        string root = Path.Combine(_root, "lifecycle");
        Guid storageId;
        string storeDirectory;
        using (var manager = OpenManager(root))
        {
            GraphStore created = manager.Create("social");
            storageId = created.StorageId;
            storeDirectory = created.RootDirectory;

            Assert.Equal("social", created.Name);
            Assert.Equal(GraphDefinition.CurrentRecordFormatVersion, created.Version);
            Assert.Same(created, manager.Open("social"));
            Assert.Null(manager.TryOpen("missing"));
            Assert.Equal(1, manager.Catalog.Revision);
            Assert.True(File.Exists(created.MarkerPath));

            GraphStore reopenedStore = manager.Reopen("social");
            Assert.NotSame(created, reopenedStore);
            Assert.True(created.IsDisposed);
            Assert.Equal(storageId, reopenedStore.StorageId);
            Assert.Equal(["social"], manager.CheckpointAll());
        }

        using (var reopenedManager = OpenManager(root))
        {
            GraphStore reopened = reopenedManager.Open("social");
            Assert.Equal(storageId, reopened.StorageId);
            Assert.Equal(1, reopenedManager.Catalog.Revision);
            Assert.True(reopenedManager.Drop("social"));
            Assert.False(reopenedManager.Drop("social"));
            Assert.Equal(2, reopenedManager.Catalog.Revision);
            Assert.False(Directory.Exists(storeDirectory));
        }

        using var afterDrop = OpenManager(root);
        Assert.Equal(2, afterDrop.Catalog.Revision);
        Assert.Empty(afterDrop.Catalog.Snapshot());
        Assert.Null(afterDrop.TryOpen("social"));
    }

    [Fact]
    public void Open_MissingCorruptAndFutureMarker_RejectsBeforeKvOpen()
    {
        string missingRoot = Path.Combine(_root, "missing-marker");
        string missingMarkerPath;
        using (var manager = OpenManager(missingRoot))
            missingMarkerPath = manager.Create("missing_marker").MarkerPath;
        File.Delete(missingMarkerPath);
        using (var manager = OpenManager(missingRoot))
        {
            Assert.Contains(
                "marker 缺失",
                Assert.Throws<InvalidDataException>(() => manager.Open("missing_marker")).Message,
                StringComparison.Ordinal);
        }

        string corruptRoot = Path.Combine(_root, "corrupt-marker");
        string corruptMarkerPath;
        using (var manager = OpenManager(corruptRoot))
            corruptMarkerPath = manager.Create("corrupt_marker").MarkerPath;
        byte[] corrupt = File.ReadAllBytes(corruptMarkerPath);
        corrupt[32] ^= 0x01;
        File.WriteAllBytes(corruptMarkerPath, corrupt);
        using (var manager = OpenManager(corruptRoot))
        {
            Assert.Contains(
                "CRC32",
                Assert.Throws<InvalidDataException>(() => manager.Open("corrupt_marker")).Message,
                StringComparison.Ordinal);
        }

        string futureRoot = Path.Combine(_root, "future-marker");
        string futureMarkerPath;
        using (var manager = OpenManager(futureRoot))
            futureMarkerPath = manager.Create("future_marker").MarkerPath;
        byte[] future = File.ReadAllBytes(futureMarkerPath);
        BinaryPrimitives.WriteInt32LittleEndian(future.AsSpan(8, 4), 2);
        File.WriteAllBytes(futureMarkerPath, future);
        using var futureManager = OpenManager(futureRoot);
        Assert.Contains(
            "版本不受支持",
            Assert.Throws<InvalidDataException>(() => futureManager.Open("future_marker")).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Create_CandidatePersistedBeforePublish_ExposesOnlyOldSnapshot()
    {
        string root = Path.Combine(_root, "persist-before-publish");
        using var manager = OpenManager(root);
        var hookInvoked = false;
        manager.AfterCatalogPersistedBeforePublishTestHook = () =>
        {
            hookInvoked = true;
            Assert.Equal(0, manager.Catalog.Revision);
            Assert.Null(manager.Catalog.TryGet("social"));
            Assert.Single(GraphCatalogCodec.Load(manager.CatalogPath).Definitions);
        };

        GraphStore created = manager.Create("social");

        Assert.True(hookInvoked);
        Assert.Equal("social", created.Name);
        Assert.Equal(1, manager.Catalog.Revision);
    }

    [Fact]
    public void Create_PublishFailure_RollsBackCatalogAndCandidateStore()
    {
        string root = Path.Combine(_root, "publish-failure");
        using (var manager = OpenManager(root))
        {
            manager.AfterCatalogPersistedBeforePublishTestHook = static () =>
                throw new InvalidOperationException("injected graph publication failure");

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => manager.Create("social"));
            Assert.Contains("injected graph publication failure", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, manager.Catalog.Revision);
            Assert.Null(manager.Catalog.TryGet("social"));
            Assert.Empty(Directory.EnumerateDirectories(manager.StoresDirectory));
        }

        using var reopened = OpenManager(root);
        Assert.Equal(0, reopened.Catalog.Revision);
        Assert.Empty(reopened.Catalog.Snapshot());
    }

    [Theory]
    [InlineData((int)GraphCatalogSavePhase.AfterReplaceBeforeDirectoryFlush)]
    [InlineData((int)GraphCatalogSavePhase.AfterDirectoryFlush)]
    public void Create_CatalogSaveFailureAfterReplace_PreservesCompleteStoreAndFaultsManager(
        int failurePhaseValue)
    {
        var failurePhase = (GraphCatalogSavePhase)failurePhaseValue;
        string root = Path.Combine(_root, "create-save-failure-" + failurePhase);
        Guid storageId;
        string storeDirectory;
        using (var manager = OpenManager(root))
        {
            manager.CatalogSavePhaseTestHook = phase =>
            {
                if (phase == failurePhase)
                    throw new IOException($"injected {phase} failure");
            };

            IOException error = Assert.Throws<IOException>(() => manager.Create("social"));

            Assert.Contains("持久化结果未知", error.Message, StringComparison.Ordinal);
            GraphDefinition durable = Assert.Single(GraphCatalogCodec.Load(manager.CatalogPath).Definitions);
            storageId = durable.StorageId;
            storeDirectory = Path.Combine(manager.StoresDirectory, storageId.ToString("N"));
            Assert.Equal("social", durable.Name);
            Assert.True(File.Exists(Path.Combine(storeDirectory, GraphStore.MarkerFileName)));
            Assert.Empty(manager.Catalog.Snapshot());
            AssertManagerIsCatalogFaulted(manager);
        }

        using var reopened = OpenManager(root);
        Assert.Equal(storageId, reopened.Open("social").StorageId);
        Assert.True(Directory.Exists(storeDirectory));
    }

    [Fact]
    public void Create_PublishFailureAndRollbackSaveFailure_PreservesCandidateStoreForReopen()
    {
        string root = Path.Combine(_root, "create-rollback-save-failure");
        string storeDirectory;
        using (var manager = OpenManager(root))
        {
            var saveAttempt = 0;
            manager.CatalogSavePhaseTestHook = phase =>
            {
                if (phase == GraphCatalogSavePhase.BeforeReplace
                    && ++saveAttempt == 2)
                {
                    throw new IOException("injected catalog rollback failure");
                }
            };
            manager.AfterCatalogPersistedBeforePublishTestHook = static () =>
                throw new InvalidOperationException("injected graph publication failure");

            IOException error = Assert.Throws<IOException>(() => manager.Create("social"));

            Assert.Contains("持久化结果未知", error.Message, StringComparison.Ordinal);
            Assert.Contains("catalog rollback failure", error.ToString(), StringComparison.Ordinal);
            GraphDefinition durable = Assert.Single(GraphCatalogCodec.Load(manager.CatalogPath).Definitions);
            storeDirectory = Path.Combine(manager.StoresDirectory, durable.StorageId.ToString("N"));
            Assert.True(File.Exists(Path.Combine(storeDirectory, GraphStore.MarkerFileName)));
            Assert.Empty(manager.Catalog.Snapshot());
            AssertManagerIsCatalogFaulted(manager);
        }

        using var reopened = OpenManager(root);
        Assert.Equal("social", reopened.Open("social").Name);
        Assert.True(Directory.Exists(storeDirectory));
    }

    [Theory]
    [InlineData((int)GraphCatalogSavePhase.BeforeReplace, true)]
    [InlineData((int)GraphCatalogSavePhase.AfterReplaceBeforeDirectoryFlush, false)]
    public void Drop_CatalogSaveFailureBeforeOrAfterReplace_PreservesStoreAndReopensDurableState(
        int failurePhaseValue,
        bool graphRemainsDurable)
    {
        var failurePhase = (GraphCatalogSavePhase)failurePhaseValue;
        string root = Path.Combine(_root, "drop-save-failure-" + failurePhase);
        string storeDirectory;
        using (var manager = OpenManager(root))
        {
            GraphStore store = manager.Create("social");
            storeDirectory = store.RootDirectory;
            manager.CatalogSavePhaseTestHook = phase =>
            {
                if (phase == failurePhase)
                    throw new IOException($"injected {phase} failure");
            };

            IOException error = Assert.Throws<IOException>(() => manager.Drop("social"));

            Assert.Contains("持久化结果未知", error.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(storeDirectory));
            Assert.NotNull(manager.Catalog.TryGet("social"));
            Assert.Equal(
                graphRemainsDurable,
                GraphCatalogCodec.Load(manager.CatalogPath).Definitions.Count == 1);
            AssertManagerIsCatalogFaulted(manager);
        }

        using var reopened = OpenManager(root);
        if (graphRemainsDurable)
            Assert.Equal("social", reopened.Open("social").Name);
        else
            Assert.Null(reopened.TryOpen("social"));
        Assert.True(Directory.Exists(storeDirectory));
    }

    [Fact]
    public void ManagedCatalog_DirectMutation_IsRejectedWithoutDiskChange()
    {
        string root = Path.Combine(_root, "managed-catalog");
        using var manager = OpenManager(root);
        GraphDefinition bypass = GraphDefinition.Create("bypass");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => manager.Catalog.Add(bypass));

        Assert.Contains("GraphManager", error.Message, StringComparison.Ordinal);
        Assert.Empty(manager.Catalog.Snapshot());
        Assert.False(File.Exists(manager.CatalogPath));
    }

    [Fact]
    public void Tsdb_GraphNameConflictsWithEveryBaseObjectInBothDirections()
    {
        string root = Path.Combine(_root, "name-conflicts");
        using var database = OpenDatabase(root);
        database.Tables.Create(CreateTable("view_source"));

        database.Graphs.Create("graph_first_table");
        Assert.Throws<InvalidOperationException>(() =>
            database.Tables.Create(CreateTable("graph_first_table")));
        database.Graphs.Create("graph_first_document");
        Assert.Throws<InvalidOperationException>(() =>
            database.Documents.Create(DocumentCollectionSchema.Create("graph_first_document")));
        database.Graphs.Create("graph_first_measurement");
        Assert.Throws<InvalidOperationException>(() =>
            database.CreateMeasurement(CreateMeasurement("graph_first_measurement")));
        database.Graphs.Create("graph_first_view");
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            database,
            "CREATE VIEW graph_first_view AS SELECT * FROM view_source"));
        database.Graphs.Create("graph_first_materialized");
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            database,
            "CREATE MATERIALIZED VIEW graph_first_materialized AS SELECT * FROM view_source"));

        database.Tables.Create(CreateTable("table_first"));
        Assert.Throws<InvalidOperationException>(() => database.Graphs.Create("table_first"));
        database.Documents.Create(DocumentCollectionSchema.Create("document_first"));
        Assert.Throws<InvalidOperationException>(() => database.Graphs.Create("document_first"));
        database.CreateMeasurement(CreateMeasurement("measurement_first"));
        Assert.Throws<InvalidOperationException>(() => database.Graphs.Create("measurement_first"));
        SqlExecutor.Execute(database, "CREATE VIEW view_first AS SELECT * FROM view_source");
        Assert.Throws<InvalidOperationException>(() => database.Graphs.Create("view_first"));
        SqlExecutor.Execute(
            database,
            "CREATE MATERIALIZED VIEW materialized_first AS SELECT * FROM view_source");
        Assert.Throws<InvalidOperationException>(() => database.Graphs.Create("materialized_first"));
    }

    [Fact]
    public void Tsdb_GraphNameConflictsWithDirectViewAndImplicitMeasurementApis()
    {
        using Tsdb database = OpenDatabase(Path.Combine(_root, "direct-name-conflicts"));

        database.Graphs.Create("graph_first_view_sdk");
        Assert.Throws<InvalidOperationException>(() => database.Views.Create(
            ViewDefinition.Create("graph_first_view_sdk", "SELECT 1 AS value")));
        database.Graphs.Create("graph_first_materialized_sdk");
        Assert.Throws<InvalidOperationException>(() => database.MaterializedViews.Create(
            MaterializedViewDefinition.Create("graph_first_materialized_sdk", "SELECT 1 AS value")));
        database.Graphs.Create("graph_first_write");
        Assert.Throws<InvalidOperationException>(() => database.Write(CreatePoint("graph_first_write")));
        database.Graphs.Create("graph_first_write_many");
        Assert.Throws<InvalidOperationException>(() => database.WriteMany(
            new[] { CreatePoint("graph_first_write_many") }));

        database.Views.Create(ViewDefinition.Create("view_sdk_first", "SELECT 1 AS value"));
        Assert.Throws<InvalidOperationException>(() => database.Graphs.Create("view_sdk_first"));
        database.MaterializedViews.Create(MaterializedViewDefinition.Create(
            "materialized_sdk_first",
            "SELECT 1 AS value"));
        Assert.Throws<InvalidOperationException>(() => database.Graphs.Create("materialized_sdk_first"));
        database.Write(CreatePoint("implicit_write_first"));
        Assert.Throws<InvalidOperationException>(() => database.Graphs.Create("implicit_write_first"));
        database.WriteMany(new[] { CreatePoint("implicit_write_many_first") });
        Assert.Throws<InvalidOperationException>(() => database.Graphs.Create("implicit_write_many_first"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GraphCreate_ConcurrentImplicitMeasurementWrite_SerializesAndGraphWins(bool writeMany)
    {
        string name = writeMany ? "shared_many" : "shared_one";
        using Tsdb database = OpenDatabase(Path.Combine(_root, "concurrent-implicit-" + name));
        using var graphCatalogPersisted = new ManualResetEventSlim();
        using var releaseGraphCreate = new ManualResetEventSlim();
        database.Graphs.AfterCatalogPersistedBeforePublishTestHook = () =>
        {
            graphCatalogPersisted.Set();
            if (!releaseGraphCreate.Wait(OperationTimeout))
                throw new TimeoutException("测试未能释放 graph CREATE。");
        };

        Task<GraphStore> graphTask = Task.Run(() => database.Graphs.Create(name));
        Assert.True(graphCatalogPersisted.Wait(OperationTimeout));
        Task writeTask = Task.Run(() =>
        {
            if (writeMany)
                database.WriteMany(new[] { CreatePoint(name) });
            else
                database.Write(CreatePoint(name));
        });
        try
        {
            Task first = await Task.WhenAny(writeTask, Task.Delay(TimeSpan.FromMilliseconds(250)));
            Assert.NotSame(writeTask, first);
        }
        finally
        {
            releaseGraphCreate.Set();
        }

        GraphStore graph = await graphTask.WaitAsync(OperationTimeout);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writeTask.WaitAsync(OperationTimeout));
        Assert.Contains("graph", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(name, graph.Name);
        Assert.False(database.Measurements.Contains(name));
    }

    [Fact]
    public void GraphManager_SameRootHasSingleLiveOwnerAndCanReopenAfterDispose()
    {
        string root = Path.Combine(_root, "single-owner");
        var first = OpenManager(root);
        try
        {
            Assert.Throws<InvalidOperationException>(() => OpenManager(root));
        }
        finally
        {
            first.Dispose();
        }

        using GraphManager reopened = OpenManager(root);
        Assert.Empty(reopened.Catalog.Snapshot());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Constructor_KvCapacityBelowGraphV1Minimum_FailsBeforeOwningRoot(bool limitKey)
    {
        string root = Path.Combine(_root, limitKey ? "small-key-capacity" : "small-value-capacity");
        KvOptions options = limitKey
            ? StableKvOptions() with { MaxKeyBytes = GraphKeyCodec.MaxEncodedKeyBytes - 1 }
            : StableKvOptions() with { MaxValueBytes = GraphRecordEnvelopeCodec.MaxEncodedRecordBytes - 1 };

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new GraphManager(root, options));

        Assert.Contains(
            limitKey ? nameof(KvOptions.MaxKeyBytes) : nameof(KvOptions.MaxValueBytes),
            error.Message,
            StringComparison.Ordinal);
        using GraphManager reopened = OpenManager(root);
        Assert.Empty(reopened.Catalog.Snapshot());
    }

    [Fact]
    public void Dispose_WhenMultipleStoresFail_ClosesAllStoresAndReleasesRootOwner()
    {
        string root = Path.Combine(_root, "dispose-aggregate");
        GraphManager manager = OpenManager(root);
        GraphStore first = manager.Create("first");
        GraphStore second = manager.Create("second");
        first.Keyspace.WalDisposeFlushTestHook = () => throw new IOException("first dispose failure");
        second.Keyspace.WalDisposeFlushTestHook = () => throw new IOException("second dispose failure");

        AggregateException error = Assert.Throws<AggregateException>(manager.Dispose);

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Contains(error.InnerExceptions, static exception =>
            exception.Message.Contains("first dispose failure", StringComparison.Ordinal));
        Assert.Contains(error.InnerExceptions, static exception =>
            exception.Message.Contains("second dispose failure", StringComparison.Ordinal));
        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
        manager.Dispose();
        using GraphManager reopened = OpenManager(root);
        Assert.Equal(2, reopened.Catalog.Snapshot().Count);
    }

    [Fact]
    public void Constructor_WithDurableNameConflict_FailsAndReleasesRootOwner()
    {
        string root = Path.Combine(_root, "durable-name-conflict");
        using (GraphManager seed = OpenManager(root))
            _ = seed.Create("shared");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new GraphManager(
                root,
                KvOptions.Default,
                static (name, _) => throw new InvalidOperationException($"durable conflict: {name}"),
                dependencyGuard: null,
                synchronizationRoot: new object()));

        Assert.Contains("durable conflict: shared", error.Message, StringComparison.Ordinal);
        using GraphManager reopened = OpenManager(root);
        Assert.NotNull(reopened.Catalog.TryGet("shared"));
    }

    [Fact]
    public void TsdbOpen_WithDurableGraphAndMeasurementNameConflict_FailsFastAndReleasesGraphOwner()
    {
        string databaseRoot = Path.Combine(_root, "durable-cross-model-conflict");
        using (Tsdb database = OpenDatabase(databaseRoot))
            database.Write(CreatePoint("shared"));
        string graphRoot = TsdbPaths.GraphsDir(databaseRoot);
        using (GraphManager manager = OpenManager(graphRoot))
            _ = manager.Create("shared");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            OpenDatabase(databaseRoot));

        Assert.Contains("同名 measurement", error.Message, StringComparison.Ordinal);
        using GraphManager reopened = OpenManager(graphRoot);
        Assert.NotNull(reopened.Catalog.TryGet("shared"));
    }

    [Fact]
    public void Graph_PhaseZero_IsNotKnownSqlOrViewSource()
    {
        using Tsdb database = OpenDatabase(Path.Combine(_root, "not-a-source"));
        database.Graphs.Create("social");

        Assert.False(SqlExecutor.IsKnownViewSource(database, "social"));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "CREATE VIEW graph_view AS SELECT * FROM social"));
        Assert.Contains("不存在的数据源 'social'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CrashClose_AfterGraphCreate_ReopensCatalogAndStore()
    {
        string root = Path.Combine(_root, "crash-reopen");
        Tsdb database = OpenDatabase(root);
        Guid storageId = database.Graphs.Create("social").StorageId;

        database.CrashSimulationCloseWal();

        using Tsdb reopened = OpenDatabase(root);
        Assert.Equal(storageId, reopened.Graphs.Open("social").StorageId);
    }

    [Fact]
    public async Task GraphCreate_ConcurrentTableCreate_SerializesAndOnlyGraphCommits()
    {
        using Tsdb database = OpenDatabase(Path.Combine(_root, "concurrent-name"));
        using var graphCatalogPersisted = new ManualResetEventSlim();
        using var releaseGraphCreate = new ManualResetEventSlim();
        database.Graphs.AfterCatalogPersistedBeforePublishTestHook = () =>
        {
            graphCatalogPersisted.Set();
            if (!releaseGraphCreate.Wait(OperationTimeout))
                throw new TimeoutException("测试未能释放 graph CREATE。");
        };

        Task<GraphStore> graphTask = Task.Run(() => database.Graphs.Create("shared"));
        Assert.True(graphCatalogPersisted.Wait(OperationTimeout));
        Task tableTask = Task.Run(() => database.Tables.Create(CreateTable("shared")));
        try
        {
            Task first = await Task.WhenAny(tableTask, Task.Delay(TimeSpan.FromMilliseconds(250)));
            Assert.NotSame(tableTask, first);
        }
        finally
        {
            releaseGraphCreate.Set();
        }

        GraphStore graph = await graphTask.WaitAsync(OperationTimeout);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await tableTask.WaitAsync(OperationTimeout));
        Assert.Contains("graph", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("shared", graph.Name);
        Assert.Null(database.Tables.Catalog.TryGet("shared"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static GraphManager OpenManager(string rootDirectory)
        => new(rootDirectory, StableKvOptions());

    private static void AssertManagerIsCatalogFaulted(GraphManager manager)
    {
        AssertCatalogFault(() => manager.Create("another"));
        AssertCatalogFault(() => manager.Open("social"));
        AssertCatalogFault(() => manager.Drop("social"));
        AssertCatalogFault(() => manager.CheckpointAll());

        var backupCallbackInvoked = false;
        AssertCatalogFault(() => manager.ExecuteConsistentBackup(() =>
        {
            backupCallbackInvoked = true;
            return 0;
        }));
        Assert.False(backupCallbackInvoked);
    }

    private static void AssertCatalogFault(Action action)
    {
        IOException error = Assert.Throws<IOException>(action);
        Assert.Contains("GraphManager 已停用", error.Message, StringComparison.Ordinal);
    }

    private static Tsdb OpenDatabase(string rootDirectory)
        => Tsdb.Open(new TsdbOptions
        {
            RootDirectory = rootDirectory,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
            Kv = StableKvOptions(),
        });

    private static KvOptions StableKvOptions()
        => new()
        {
            AutoCheckpointEnabled = false,
            ExpirerEnabled = false,
            CleanupEnabled = false,
        };

    private static TableSchema CreateTable(string name)
        => TableSchema.Create(
            name,
            [("id", TableColumnType.Int64, false)],
            ["id"]);

    private static Point CreatePoint(string measurement)
        => Point.Create(
            measurement,
            1,
            new Dictionary<string, string> { ["host"] = "h1" },
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromLong(1) });

    private static MeasurementSchema CreateMeasurement(string name)
        => MeasurementSchema.Create(
            name,
            [new MeasurementColumn("value", MeasurementColumnRole.Field, FieldType.Int64)]);
}
