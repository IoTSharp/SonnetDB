using System.Buffers.Binary;
using SonnetDB.Graphs;
using SonnetDB.Graphs.Storage;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class GraphMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-graph-maintenance-tests",
        Guid.NewGuid().ToString("N"));
    private readonly GraphManager _manager;

    public GraphMaintenanceTests()
    {
        _manager = new GraphManager(
            _root,
            KvOptions.Default with
            {
                AutoCheckpointEnabled = false,
                SyncWalOnEveryWrite = false,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            });
    }

    [Fact]
    public void Maintenance_CancelAndReopen_ResumesFromDurablePageAndKeepsUniqueSource()
    {
        GraphStore store = _manager.Create("repairable");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertVertex(
            new GraphElementId(1),
            0,
            [new LabelId(1)],
            [new GraphProperty(7, GraphPropertyValue.FromString("alpha"))],
            uniquePropertyIds: [7]);
        transaction.UpsertVertex(new GraphElementId(2), 0, [new LabelId(1)], []);
        transaction.UpsertEdge(
            new GraphElementId(3),
            0,
            new GraphElementId(1),
            new GraphElementId(2),
            new LabelId(2),
            []);
        transaction.Commit();

        Assert.True(store.Keyspace.Delete(GraphKeyCodec.EncodeIncomingAdjacency(
            new GraphElementId(2),
            new LabelId(2),
            new GraphElementId(1),
            new GraphElementId(3))));
        Assert.True(store.Keyspace.Delete(GraphKeyCodec.EncodeUniqueProperty(
            GraphElementKind.Vertex,
            new LabelId(1),
            7,
            GraphPropertyValue.FromString("alpha"))));

        GraphUniqueIndexDefinition unique = new(GraphElementType.Vertex, new LabelId(1), 7);
        GraphMaintenanceOptions options = new()
        {
            UniqueIndexes = [unique],
            PageSize = 1,
            MaxWorkUnits = 3,
            CheckpointEveryWorkUnits = 1,
        };
        GraphMaintenanceResult first = store.RunMaintenance(options);
        Assert.False(first.IsComplete);
        Assert.True(first.OperationId != Guid.Empty);
        Assert.True(File.Exists(store.MaintenanceManifestPath));

        store = _manager.Reopen("repairable");
        GraphMaintenanceResult current = first;
        for (int attempt = 0; attempt < 200 && !current.IsComplete; attempt++)
        {
            current = store.RunMaintenance(options with { MaxWorkUnits = 8 });
        }

        Assert.True(current.IsComplete);
        Assert.True(current.WasResumed);
        Assert.Equal(first.OperationId, current.OperationId);
        Assert.False(File.Exists(store.MaintenanceManifestPath));
        Assert.True(GraphInvariantChecker.Check(store).IsValid);

        GraphTransaction conflict = store.BeginTransaction(Guid.NewGuid());
        conflict.UpsertVertex(
            new GraphElementId(4),
            0,
            [new LabelId(1)],
            [new GraphProperty(7, GraphPropertyValue.FromString("alpha"))],
            uniquePropertyIds: [7]);
        Assert.Throws<GraphUniqueConstraintException>(() => conflict.Commit());
    }

    [Fact]
    public void Maintenance_CorruptManifest_RejectsResumeWithoutStartingOver()
    {
        GraphStore store = _manager.Create("corrupt-maintenance");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertVertex(new GraphElementId(1), 0, [new LabelId(1)], []);
        transaction.Commit();

        _ = store.RunMaintenance(new GraphMaintenanceOptions
        {
            MaxWorkUnits = 1,
            PageSize = 1,
        });
        byte[] manifest = File.ReadAllBytes(store.MaintenanceManifestPath);
        manifest[20] ^= 0x7F;
        File.WriteAllBytes(store.MaintenanceManifestPath, manifest);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => store.RunMaintenance(new GraphMaintenanceOptions { MaxWorkUnits = 1 }));
        Assert.Contains("manifest", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(manifest, File.ReadAllBytes(store.MaintenanceManifestPath));
    }

    [Fact]
    public async Task Maintenance_BetweenWorkUnits_DoesNotBlockBusinessWriter()
    {
        GraphStore store = _manager.Create("maintenance-writer");
        for (int index = 1; index <= 8; index++)
        {
            GraphTransaction seed = store.BeginTransaction(Guid.NewGuid());
            seed.UpsertVertex(new GraphElementId(index), 0, [new LabelId(1)], []);
            seed.Commit();
        }

        using var workUnitFinished = new ManualResetEventSlim();
        using var releaseMaintenance = new ManualResetEventSlim();
        int hookCalls = 0;
        store.AfterMaintenanceWorkUnitTestHook = (_, _) =>
        {
            if (Interlocked.Increment(ref hookCalls) != 1)
                return;
            workUnitFinished.Set();
            Assert.True(releaseMaintenance.Wait(TimeSpan.FromSeconds(10)));
        };

        Task<GraphMaintenanceResult> maintenance = Task.Run(() => store.RunMaintenance(
            new GraphMaintenanceOptions
            {
                PageSize = 1,
                MaxWorkUnits = 8,
                CheckpointEveryWorkUnits = 0,
            }));
        Assert.True(workUnitFinished.Wait(TimeSpan.FromSeconds(10)));

        Task writer = Task.Run(() =>
        {
            GraphTransaction write = store.BeginTransaction(Guid.NewGuid());
            write.UpsertVertex(new GraphElementId(99), 0, [new LabelId(1)], []);
            write.Commit();
        });
        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        releaseMaintenance.Set();
        _ = await maintenance.WaitAsync(TimeSpan.FromSeconds(10));
        store.AfterMaintenanceWorkUnitTestHook = null;

        GraphMaintenanceResult? result = null;
        for (int attempt = 0; attempt < 100 && (result is null || !result.IsComplete); attempt++)
            result = store.RunMaintenance(new GraphMaintenanceOptions { MaxWorkUnits = 16 });
        Assert.NotNull(result);
        Assert.True(result.IsComplete);
        Assert.True(GraphInvariantChecker.Check(store).IsValid);
    }

    [Fact]
    public void Statistics_SupernodeDegree_IsStreamedWithExplicitGroupBudget()
    {
        GraphStore store = _manager.Create("streamed-stats");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertVertex(new GraphElementId(1), 0, [new LabelId(1)], []);
        transaction.UpsertVertex(new GraphElementId(2), 0, [new LabelId(1)], []);
        transaction.UpsertEdge(
            new GraphElementId(3),
            0,
            new GraphElementId(1),
            new GraphElementId(2),
            new LabelId(2),
            []);
        transaction.Commit();

        using GraphReadSession read = store.BeginRead();
        GraphStatistics statistics = read.RefreshStatistics(new GraphStatisticsRefreshOptions
        {
            PageSize = 1,
            MaxStatisticGroups = 16,
        });
        Assert.Equal(1, statistics.DegreeHistogram[1]);
        Assert.Equal(1, statistics.DegreeHistogram[0]);
        Assert.Throws<GraphStatisticsLimitExceededException>(() => read.RefreshStatistics(
            new GraphStatisticsRefreshOptions
            {
                PageSize = 1,
                MaxStatisticGroups = 1,
            }));
    }

    [Fact]
    public void Maintenance_RecordProjectionExceedsMutationBudget_FailsBeforeExpansion()
    {
        GraphStore store = _manager.Create("mutation-budget");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertVertex(
            new GraphElementId(1),
            0,
            [new LabelId(1), new LabelId(2)],
            [
                new GraphProperty(1, GraphPropertyValue.FromInt64(1)),
                new GraphProperty(2, GraphPropertyValue.FromInt64(2)),
            ]);
        transaction.Commit();

        Assert.Throws<GraphMaintenanceLimitExceededException>(() => store.RunMaintenance(
            new GraphMaintenanceOptions
            {
                MaxMutationsPerWorkUnit = 5,
                MaxWorkUnits = 1,
            }));
        GraphMaintenanceResult? result = null;
        for (int attempt = 0; attempt < 20 && (result is null || !result.IsComplete); attempt++)
        {
            result = store.RunMaintenance(new GraphMaintenanceOptions
            {
                MaxMutationsPerWorkUnit = 16,
                MaxWorkUnits = 16,
            });
        }
        Assert.NotNull(result);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void Maintenance_RemovalPages_RespectMutationBudget()
    {
        GraphStore store = _manager.Create("removal-budget");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertVertex(new GraphElementId(1), 0, [new LabelId(1)], []);
        transaction.Commit();
        for (int index = 0; index < 3; index++)
        {
            store.Keyspace.Put(
                GraphKeyCodec.EncodeLabelMembership(
                    GraphElementKind.Vertex,
                    new LabelId(2),
                    new GraphElementId(100 + index)),
                []);
        }

        GraphMaintenanceResult? result = null;
        long previousRemovedEntries = 0;
        for (int attempt = 0; attempt < 100 && (result is null || !result.IsComplete); attempt++)
        {
            result = store.RunMaintenance(new GraphMaintenanceOptions
            {
                PageSize = 16,
                MaxMutationsPerWorkUnit = 1,
                MaxWorkUnits = 1,
                CheckpointEveryWorkUnits = 0,
            });
            Assert.InRange(result.RemovedEntries - previousRemovedEntries, 0, 1);
            previousRemovedEntries = result.RemovedEntries;
        }

        Assert.NotNull(result);
        Assert.True(result.IsComplete);
        Assert.Equal(3, result.RemovedEntries);
        Assert.True(GraphInvariantChecker.Check(store).IsValid);
    }

    [Fact]
    public void Maintenance_UniqueRepairPages_RespectMutationBudget()
    {
        GraphStore store = _manager.Create("unique-repair-budget");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        for (int index = 1; index <= 3; index++)
        {
            transaction.UpsertVertex(
                new GraphElementId(index),
                0,
                [new LabelId(1)],
                [new GraphProperty(7, GraphPropertyValue.FromString($"value-{index}"))],
                uniquePropertyIds: [7]);
        }
        transaction.Commit();
        for (int index = 1; index <= 3; index++)
        {
            Assert.True(store.Keyspace.Delete(GraphKeyCodec.EncodeUniqueProperty(
                GraphElementKind.Vertex,
                new LabelId(1),
                7,
                GraphPropertyValue.FromString($"value-{index}"))));
        }

        GraphUniqueIndexDefinition definition = new(GraphElementType.Vertex, new LabelId(1), 7);
        GraphMaintenanceResult? result = null;
        long previousRepairedEntries = 0;
        for (int attempt = 0; attempt < 200 && (result is null || !result.IsComplete); attempt++)
        {
            result = store.RunMaintenance(new GraphMaintenanceOptions
            {
                UniqueIndexes = [definition],
                PageSize = 16,
                MaxMutationsPerWorkUnit = 2,
                MaxWorkUnits = 1,
                CheckpointEveryWorkUnits = 0,
            });
            Assert.InRange(result.RepairedEntries - previousRepairedEntries, 0, 2);
            previousRepairedEntries = result.RepairedEntries;
        }

        Assert.NotNull(result);
        Assert.True(result.IsComplete);
        Assert.Equal(3, result.RepairedEntries);
        Assert.True(GraphInvariantChecker.Check(store).IsValid);
    }

    [Fact]
    public void MaintenanceManifest_ValidCrcWithInvalidContinuation_IsRejected()
    {
        string path = Path.Combine(_root, GraphMaintenanceManifestCodec.FileName);
        Guid storageId = Guid.NewGuid();
        GraphMaintenanceManifestCodec.Save(path, new GraphMaintenanceState
        {
            StorageId = storageId,
            OperationId = Guid.NewGuid(),
            Phase = GraphMaintenancePhase.Checkpoint,
            SourceSequence = 1,
            LastSequence = 1,
            AfterKey = [1],
            UniqueDefinitions = [],
            MaxUniqueIndexDefinitions = 1,
            CompactOnCompletion = false,
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => GraphMaintenanceManifestCodec.Load(path, storageId));
        Assert.Contains("continuation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdjacencyCheckpoint_UsesRestartedPrefixCompression_AndRoundTrips()
    {
        string path = Path.Combine(_root, "adjacency.SDBKVSEG");
        const int EntryCount = 4_096;
        GraphElementId anchor = new(1);
        var entries = new List<KeyValuePair<byte[], KvValueEntry>>(EntryCount);
        for (int index = 0; index < EntryCount; index++)
        {
            GraphElementId neighbor = new(index + 2);
            GraphElementId edge = new(index + 10_000);
            entries.Add(new KeyValuePair<byte[], KvValueEntry>(
                GraphKeyCodec.EncodeOutgoingAdjacency(anchor, new LabelId(2), neighbor, edge),
                new KvValueEntry([], index + 1)));
        }

        KvStateFile.SaveSegment(path, EntryCount, entries, EntryCount);
        byte[] encoded = File.ReadAllBytes(path);
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(8, 4)));
        int uncompressedBytes = 64 + EntryCount * (32 + entries[0].Key.Length + sizeof(uint));
        Assert.True(encoded.Length < uncompressedBytes * 0.7, $"compressed={encoded.Length}, baseline={uncompressedBytes}");

        using KvDiskState state = KvStateFile.OpenDiskState(path);
        Assert.Equal(EntryCount, state.Count);
        KvValueEntry? value = state.Get(entries[^1].Key);
        Assert.NotNull(value);
        Assert.Equal(entries[^1].Value.Version, value.Version);
    }

    [Fact]
    public void SupernodeExpansion_OneHundredThousandEdges_ReturnsBoundedPages()
    {
        string root = Path.Combine(_root, "supernode");
        using var manager = new GraphManager(
            root,
            KvOptions.Default with
            {
                AutoCheckpointEnabled = false,
                SyncWalOnEveryWrite = false,
                MaxSnapshotOverlayEntries = 250_000,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            });
        GraphStore store = manager.Create("hub");
        GraphTransaction vertex = store.BeginTransaction(Guid.NewGuid());
        vertex.UpsertVertex(new GraphElementId(1), 0, [new LabelId(1)], []);
        vertex.Commit();

        const int EdgeCount = 100_000;
        var mutations = new List<KvBatchMutation>(2_000);
        for (int index = 0; index < EdgeCount; index++)
        {
            GraphElementId edgeId = new(index + 2);
            GraphElementId targetId = new(index + 200_000);
            var record = new GraphEdgeRecord(
                edgeId,
                1,
                new GraphElementId(1),
                targetId,
                new LabelId(2),
                []);
            mutations.Add(KvBatchMutation.Put(
                GraphKeyCodec.EncodeEdgeRecord(edgeId),
                GraphElementRecordCodec.EncodeEdge(record)));
            mutations.Add(KvBatchMutation.Put(
                GraphKeyCodec.EncodeOutgoingAdjacency(new GraphElementId(1), new LabelId(2), targetId, edgeId),
                []));
            if (mutations.Count == 2_000)
            {
                store.Keyspace.ApplyIndexRebuildBatch(mutations);
                mutations.Clear();
            }
        }
        if (mutations.Count > 0)
            store.Keyspace.ApplyIndexRebuildBatch(mutations);
        store.Compact();

        using GraphReadSession read = store.BeginRead();
        using GraphCursor<GraphExpansion> cursor = read.Expand(
            new GraphElementId(1),
            GraphDirection.Outgoing,
            new LabelId(2),
            new GraphCursorOptions
            {
                PageSize = 64,
                MaxResults = 256,
                MaxPageBytes = 16 * 1024,
            });
        IReadOnlyList<GraphExpansion> page = cursor.ReadNextPage();
        Assert.Equal(64, page.Count);
        Assert.False(cursor.IsExhausted);
        Assert.Equal(64, cursor.ReadNextPage().Count);
        Assert.Equal(128, ReadCount(cursor));
    }

    private static int ReadCount(GraphCursor<GraphExpansion> cursor)
    {
        int total = 0;
        while (true)
        {
            IReadOnlyList<GraphExpansion> page = cursor.ReadNextPage();
            if (page.Count == 0)
                return total;
            total += page.Count;
        }
    }

    public void Dispose()
    {
        _manager.Dispose();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}
