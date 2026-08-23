using SonnetDB.Graphs;
using SonnetDB.Kv;
using SonnetDB.Tables;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class GraphOfflineAlgorithmTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-graph-offline-algorithm-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void RunOfflineAlgorithms_TableOutputResumesAndPublishesVersionedResults()
    {
        using var graphs = CreateGraphManager();
        using var tables = CreateTableManager();
        GraphStore store = graphs.Create("analytics");
        SeedTwoTrianglesAndIsolatedVertex(store);
        TableSchema schema = GraphOfflineAlgorithmTable.CreateSchema("graph_algorithm_results");
        tables.Create(schema);
        TableStore table = tables.Open(schema.Name);
        Guid operationId = Guid.NewGuid();
        var request = new GraphOfflineAlgorithmRequest(
            operationId,
            new GraphOfflineAlgorithmTableOutput(table));
        var options = new GraphOfflineAlgorithmOptions
        {
            PageSize = 2,
            MaxWorkUnits = 1,
            MaxMemoryBytes = 256 * 1024,
            OutputBatchSize = 2,
        };

        GraphOfflineAlgorithmResult result = RunToCompletion(store, request, options);

        Assert.True(result.IsComplete);
        Assert.True(result.WasResumed);
        Assert.Equal(GraphOfflineAlgorithmPhase.Completed, result.Phase);
        Assert.Equal(7, result.VertexCount);
        Assert.Equal(6, result.EdgeCount);
        Assert.Equal(7, result.PublishedVertices);
        Assert.True(result.PageRankConverged);
        Assert.True(result.CommunityConverged);
        Assert.True(result.SpillBytes > 0);
        Assert.Equal(256 * 1024, result.MemoryBudgetBytes);

        TableRow[] rows = table.Scan()
            .OrderBy(static row => (long)row.Values[1]!)
            .ToArray();
        Assert.Equal(7, rows.Length);
        Assert.All(rows, row =>
        {
            Assert.Equal(operationId.ToString("D"), row.Values[0]);
            Assert.Equal(result.SourceSequence, row.Values[2]);
        });
        Assert.Equal([1L, 1L, 1L, 4L, 4L, 4L, 7L],
            rows.Select(static row => (long)row.Values[3]!).ToArray());
        Assert.Equal([1L, 1L, 1L, 1L, 1L, 1L, 0L],
            rows.Select(static row => (long)row.Values[5]!).ToArray());
        Assert.Equal([1L, 1L, 1L, 1L, 1L, 1L, 0L],
            rows.Select(static row => (long)row.Values[6]!).ToArray());
        Assert.Equal([2L, 2L, 2L, 2L, 2L, 2L, 0L],
            rows.Select(static row => (long)row.Values[7]!).ToArray());
        Assert.Equal([1L, 1L, 1L, 4L, 4L, 4L, 7L],
            rows.Select(static row => (long)row.Values[8]!).ToArray());
        Assert.InRange(
            rows.Sum(static row => (double)row.Values[4]!),
            0.999_999_999,
            1.000_000_001);

        string workspace = Path.Combine(
            store.OfflineAlgorithmRootDirectory,
            operationId.ToString("N"));
        string manifest = Assert.Single(Directory.GetFiles(workspace));
        Assert.Equal(GraphOfflineAlgorithmManifestCodec.FileName, Path.GetFileName(manifest));
        Assert.Empty(Directory.GetDirectories(workspace));
    }

    [Fact]
    public void RunOfflineAlgorithms_GraphOutputPreservesPropertiesAndIsIdempotentAfterReopen()
    {
        GraphOfflineAlgorithmResult result;
        Guid operationId = Guid.NewGuid();
        var output = new GraphOfflineAlgorithmGraphOutput(100, 101, 102, 103, 104, 105, 106)
        {
            UniqueIndexes =
            [
                new GraphUniqueIndexDefinition(
                    GraphElementType.Vertex,
                    new LabelId(1),
                    9),
            ],
        };
        var request = new GraphOfflineAlgorithmRequest(operationId, output);
        var options = new GraphOfflineAlgorithmOptions
        {
            PageSize = 1,
            MaxWorkUnits = 2,
            MaxMemoryBytes = 256 * 1024,
            OutputBatchSize = 1,
            MaxCommunityIterations = 4,
        };

        using (GraphManager manager = CreateGraphManager())
        {
            GraphStore store = manager.Create("property-output");
            GraphTransaction vertices = store.BeginTransaction(Guid.NewGuid());
            vertices.UpsertVertex(
                new GraphElementId(1),
                0,
                [new LabelId(1)],
                [new GraphProperty(9, GraphPropertyValue.FromString("one"))],
                [9]);
            vertices.UpsertVertex(
                new GraphElementId(2),
                0,
                [new LabelId(1)],
                [new GraphProperty(9, GraphPropertyValue.FromString("two"))],
                [9]);
            _ = vertices.Commit();
            GraphTransaction edge = store.BeginTransaction(Guid.NewGuid());
            edge.UpsertEdge(
                new GraphElementId(10),
                0,
                new GraphElementId(1),
                new GraphElementId(2),
                new LabelId(2),
                []);
            _ = edge.Commit();

            result = RunToCompletion(store, request, options);
            AssertGraphOutput(store, result.ResultVersion);
            using GraphReadSession read = store.BeginRead();
            Assert.Equal(2, read.GetVertex(new GraphElementId(1))!.ElementVersion);
            Assert.Equal(2, read.GetVertex(new GraphElementId(2))!.ElementVersion);
        }

        using (GraphManager reopened = CreateGraphManager())
        {
            GraphStore store = reopened.Open("property-output");
            GraphOfflineAlgorithmResult resumed = store.RunOfflineAlgorithms(request, options);
            Assert.True(resumed.IsComplete);
            Assert.True(resumed.WasResumed);
            Assert.Equal(result.ResultVersion, resumed.ResultVersion);
            AssertGraphOutput(store, result.ResultVersion);
            using GraphReadSession read = store.BeginRead();
            Assert.Equal(2, read.GetVertex(new GraphElementId(1))!.ElementVersion);
            Assert.Equal(2, read.GetVertex(new GraphElementId(2))!.ElementVersion);
        }
    }

    [Fact]
    public void RunOfflineAlgorithms_SourceChangesDuringSnapshotCollection_RejectsResume()
    {
        using GraphManager manager = CreateGraphManager();
        GraphStore store = manager.Create("source-change");
        CreateVertices(store, 1, 2, 3);
        using var tables = CreateTableManager();
        TableSchema schema = GraphOfflineAlgorithmTable.CreateSchema("source_change_results");
        tables.Create(schema);
        var request = new GraphOfflineAlgorithmRequest(
            Guid.NewGuid(),
            new GraphOfflineAlgorithmTableOutput(tables.Open(schema.Name)));
        var options = new GraphOfflineAlgorithmOptions
        {
            PageSize = 1,
            MaxWorkUnits = 1,
            MaxMemoryBytes = 256 * 1024,
        };

        GraphOfflineAlgorithmResult first = store.RunOfflineAlgorithms(request, options);
        Assert.Equal(GraphOfflineAlgorithmPhase.ScanVertices, first.Phase);
        Assert.Equal(1, first.VertexCount);
        CreateVertices(store, 4);

        GraphOfflineAlgorithmSourceChangedException exception = Assert.Throws<GraphOfflineAlgorithmSourceChangedException>(
            () => store.RunOfflineAlgorithms(request, options));
        Assert.Equal(first.SourceSequence, exception.ExpectedSequence);
        Assert.True(exception.ActualSequence > exception.ExpectedSequence);
    }

    [Fact]
    public void RunOfflineAlgorithms_PreCanceledOperationCanResumeFromDurableManifest()
    {
        using GraphManager manager = CreateGraphManager();
        GraphStore store = manager.Create("canceled");
        CreateVertices(store, 1);
        using var tables = CreateTableManager();
        TableSchema schema = GraphOfflineAlgorithmTable.CreateSchema("canceled_results");
        tables.Create(schema);
        var request = new GraphOfflineAlgorithmRequest(
            Guid.NewGuid(),
            new GraphOfflineAlgorithmTableOutput(tables.Open(schema.Name)));
        var options = new GraphOfflineAlgorithmOptions
        {
            MaxWorkUnits = 1,
            MaxMemoryBytes = 256 * 1024,
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => store.RunOfflineAlgorithms(request, options, cancellation.Token));
        GraphOfflineAlgorithmResult resumed = store.RunOfflineAlgorithms(request, options);

        Assert.True(resumed.WasResumed);
        Assert.True(resumed.WorkUnits > 0);
    }

    [Fact]
    public void SpillVector_ExceedsMemoryBudget_RoundTripsThroughFileBackedStorage()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "spill-vector.bin");
        const int Count = 40_000;
        const int MemoryBudget = 256 * 1024;
        using (GraphAlgorithmLongVector vector = GraphAlgorithmLongVector.Create(path, Count, MemoryBudget))
        {
            vector.Set(0, long.MinValue);
            vector.Set(Count / 2, 42);
            vector.Set(Count - 1, long.MaxValue);
            vector.Flush();
        }

        Assert.Equal(Count * sizeof(long), new FileInfo(path).Length);
        using GraphAlgorithmLongVector reopened = GraphAlgorithmLongVector.Open(path, Count, MemoryBudget);
        Assert.Equal(long.MinValue, reopened.Get(0));
        Assert.Equal(42, reopened.Get(Count / 2));
        Assert.Equal(long.MaxValue, reopened.Get(Count - 1));
    }

    [Fact]
    public void RunOfflineAlgorithms_CorruptedManifest_RejectsResume()
    {
        using GraphManager manager = CreateGraphManager();
        GraphStore store = manager.Create("corrupt-manifest");
        CreateVertices(store, 1, 2);
        using var tables = CreateTableManager();
        TableSchema schema = GraphOfflineAlgorithmTable.CreateSchema("corrupt_manifest_results");
        tables.Create(schema);
        Guid operationId = Guid.NewGuid();
        var request = new GraphOfflineAlgorithmRequest(
            operationId,
            new GraphOfflineAlgorithmTableOutput(tables.Open(schema.Name)));
        var options = new GraphOfflineAlgorithmOptions
        {
            MaxWorkUnits = 1,
            MaxMemoryBytes = 256 * 1024,
        };
        _ = store.RunOfflineAlgorithms(request, options);
        string manifestPath = Path.Combine(
            store.OfflineAlgorithmRootDirectory,
            operationId.ToString("N"),
            GraphOfflineAlgorithmManifestCodec.FileName);
        byte[] manifest = File.ReadAllBytes(manifestPath);
        manifest[64] ^= 0x5A;
        File.WriteAllBytes(manifestPath, manifest);

        Assert.Throws<InvalidDataException>(() => store.RunOfflineAlgorithms(request, options));
    }

    private GraphManager CreateGraphManager()
        => new(
            Path.Combine(_root, "graphs"),
            KvOptions.Default with
            {
                AutoCheckpointEnabled = false,
                SyncWalOnEveryWrite = false,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            });

    private TableManager CreateTableManager()
        => new(
            Path.Combine(_root, "tables"),
            KvOptions.Default with
            {
                AutoCheckpointEnabled = false,
                SyncWalOnEveryWrite = false,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            });

    private static GraphOfflineAlgorithmResult RunToCompletion(
        GraphStore store,
        GraphOfflineAlgorithmRequest request,
        GraphOfflineAlgorithmOptions options)
    {
        GraphOfflineAlgorithmResult? result = null;
        for (int attempt = 0; attempt < 200; attempt++)
        {
            result = store.RunOfflineAlgorithms(request, options);
            if (result.IsComplete)
                return result;
        }
        throw new TimeoutException($"Graph offline algorithm 未在 200 次续作内完成，当前阶段 {result?.Phase}。");
    }

    private static void AssertGraphOutput(GraphStore store, string resultVersion)
    {
        using GraphReadSession read = store.BeginRead();
        GraphVertex first = Assert.IsType<GraphVertex>(read.GetVertex(new GraphElementId(1)));
        GraphVertex second = Assert.IsType<GraphVertex>(read.GetVertex(new GraphElementId(2)));
        Assert.Equal("one", GetProperty(first, 9).AsString());
        Assert.Equal("two", GetProperty(second, 9).AsString());
        Assert.Equal(1, GetProperty(first, 103).AsInt64());
        Assert.Equal(0, GetProperty(first, 102).AsInt64());
        Assert.Equal(0, GetProperty(second, 103).AsInt64());
        Assert.Equal(1, GetProperty(second, 102).AsInt64());
        Assert.Equal(resultVersion, GetProperty(first, 106).AsString());
        Assert.Equal(resultVersion, GetProperty(second, 106).AsString());
    }

    private static GraphPropertyValue GetProperty(GraphVertex vertex, int propertyId)
        => Assert.Single(vertex.Properties, property => property.PropertyId == propertyId).Value;

    private static void SeedTwoTrianglesAndIsolatedVertex(GraphStore store)
    {
        CreateVertices(store, 1, 2, 3, 4, 5, 6, 7);
        GraphTransaction edges = store.BeginTransaction(Guid.NewGuid());
        AddEdge(edges, 101, 1, 2);
        AddEdge(edges, 102, 2, 3);
        AddEdge(edges, 103, 3, 1);
        AddEdge(edges, 104, 4, 5);
        AddEdge(edges, 105, 5, 6);
        AddEdge(edges, 106, 6, 4);
        _ = edges.Commit();
    }

    private static void CreateVertices(GraphStore store, params long[] ids)
    {
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        foreach (long id in ids)
        {
            transaction.UpsertVertex(
                new GraphElementId(id),
                0,
                [new LabelId(1)],
                []);
        }
        _ = transaction.Commit();
    }

    private static void AddEdge(
        GraphTransaction transaction,
        long edgeId,
        long sourceId,
        long targetId)
        => transaction.UpsertEdge(
            new GraphElementId(edgeId),
            0,
            new GraphElementId(sourceId),
            new GraphElementId(targetId),
            new LabelId(2),
            []);

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
}
