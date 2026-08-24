using SonnetDB.Graphs;
using SonnetDB.Kv;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class GraphWeightedShortestPathTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-graph-weighted-path-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<GraphManager> _managers = [];

    [Fact]
    public void WeightedShortestPath_DijkstraUsesTotalWeightAndStablePath()
    {
        GraphStore store = CreateStore("dijkstra");
        SeedWeightedGraph(store);

        using GraphReadSession read = store.BeginRead();
        GraphWeightedPath result = Assert.IsType<GraphWeightedPath>(read.WeightedShortestPath(
            new GraphElementId(1),
            new GraphElementId(4),
            GraphWeightedShortestPathOptions.ForProperty(1)));

        Assert.Equal(3d, result.TotalWeight);
        Assert.Equal([1L, 3L, 2L, 4L], result.VertexIds.Select(static id => id.Value).ToArray());
        Assert.Equal([12L, 13L, 14L], result.EdgeIds.Select(static id => id.Value).ToArray());
        Assert.Equal(GraphWeightedShortestPathAlgorithm.Dijkstra, result.Algorithm);
        Assert.Equal(read.Sequence, result.SnapshotSequence);

        GraphWeightedPath zeroLength = Assert.IsType<GraphWeightedPath>(read.WeightedShortestPath(
            new GraphElementId(1),
            new GraphElementId(1),
            GraphWeightedShortestPathOptions.ForProperty(1)));
        Assert.Equal(0, zeroLength.Depth);
        Assert.Equal(0d, zeroLength.TotalWeight);
    }

    [Theory]
    [InlineData(GraphWeightedShortestPathAlgorithm.AStar)]
    [InlineData(GraphWeightedShortestPathAlgorithm.BidirectionalDijkstra)]
    public void WeightedShortestPath_AdvancedAlgorithmsMatchDijkstra(GraphWeightedShortestPathAlgorithm algorithm)
    {
        GraphStore store = CreateStore("advanced-" + algorithm);
        SeedWeightedGraph(store);
        using GraphReadSession read = store.BeginRead();
        GraphWeightedShortestPathOptions options = GraphWeightedShortestPathOptions.ForProperty(1) with
        {
            Algorithm = algorithm,
            Heuristic = algorithm == GraphWeightedShortestPathAlgorithm.AStar
                ? static _ => 0d
                : null,
        };

        GraphWeightedPath result = Assert.IsType<GraphWeightedPath>(read.WeightedShortestPath(
            new GraphElementId(1), new GraphElementId(4), options));

        Assert.Equal(3d, result.TotalWeight);
        Assert.Equal([1L, 3L, 2L, 4L], result.VertexIds.Select(static id => id.Value).ToArray());
        Assert.Equal(algorithm, result.Algorithm);
    }

    [Fact]
    public void WeightedShortestPath_IncomingDirectionBuildsForwardPath()
    {
        GraphStore store = CreateStore("incoming");
        SeedWeightedGraph(store);
        using GraphReadSession read = store.BeginRead();

        foreach (GraphWeightedShortestPathAlgorithm algorithm in new[]
        {
            GraphWeightedShortestPathAlgorithm.Dijkstra,
            GraphWeightedShortestPathAlgorithm.BidirectionalDijkstra,
        })
        {
            GraphWeightedPath result = Assert.IsType<GraphWeightedPath>(read.WeightedShortestPath(
                new GraphElementId(4),
                new GraphElementId(1),
                GraphWeightedShortestPathOptions.ForProperty(1) with
                {
                    Algorithm = algorithm,
                    Direction = GraphDirection.Incoming,
                }));

            Assert.Equal(3d, result.TotalWeight);
            Assert.Equal([4L, 2L, 3L, 1L], result.VertexIds.Select(static id => id.Value).ToArray());
        }
    }

    [Theory]
    [InlineData(GraphWeightedShortestPathAlgorithm.Dijkstra)]
    [InlineData(GraphWeightedShortestPathAlgorithm.BidirectionalDijkstra)]
    public void WeightedShortestPath_DepthBudgetKeepsSeparateVertexDepthStates(GraphWeightedShortestPathAlgorithm algorithm)
    {
        GraphStore store = CreateStore("depth-state-" + algorithm);
        CreateVertices(store, 1, 2, 3, 4);
        AddEdge(store, 10, 1, 2, GraphPropertyValue.FromInt64(5));
        AddEdge(store, 11, 1, 3, GraphPropertyValue.FromInt64(1));
        AddEdge(store, 12, 3, 2, GraphPropertyValue.FromInt64(1));
        AddEdge(store, 13, 2, 4, GraphPropertyValue.FromInt64(1));
        using GraphReadSession read = store.BeginRead();

        GraphWeightedPath result = Assert.IsType<GraphWeightedPath>(read.WeightedShortestPath(
            new GraphElementId(1),
            new GraphElementId(4),
            GraphWeightedShortestPathOptions.ForProperty(1) with
            {
                Algorithm = algorithm,
                MaxDepth = 2,
            }));

        Assert.Equal(6d, result.TotalWeight);
        Assert.Equal([1L, 2L, 4L], result.VertexIds.Select(static id => id.Value).ToArray());
    }

    [Fact]
    public void WeightedShortestPath_RejectsNegativeMissingAndUnsupportedWeights()
    {
        GraphStore store = CreateStore("invalid-weight");
        CreateVertices(store, 1, 2, 3, 4);
        AddEdge(store, 10, 1, 2, GraphPropertyValue.FromInt64(-1));
        AddEdge(store, 11, 2, 3, GraphPropertyValue.FromInt64(1));
        AddEdge(store, 12, 3, 4, GraphPropertyValue.FromString("not-a-number"));
        AddEdgeWithoutWeight(store, 13, 2, 4);
        using GraphReadSession read = store.BeginRead();

        Assert.Throws<GraphNegativeWeightException>(() => read.WeightedShortestPath(
            new GraphElementId(1), new GraphElementId(2), GraphWeightedShortestPathOptions.ForProperty(1)));
        Assert.Throws<GraphMissingWeightException>(() => read.WeightedShortestPath(
            new GraphElementId(2), new GraphElementId(4), GraphWeightedShortestPathOptions.ForProperty(1)));
        Assert.Throws<GraphWeightTypeException>(() => read.WeightedShortestPath(
            new GraphElementId(3), new GraphElementId(4), GraphWeightedShortestPathOptions.ForProperty(1)));
    }

    [Fact]
    public void WeightedShortestPath_RejectsAccumulationOverflowAndHonorsCancellation()
    {
        GraphStore store = CreateStore("overflow-cancel");
        CreateVertices(store, 1, 2, 3);
        AddEdge(store, 10, 1, 2, GraphPropertyValue.FromFloat64(double.MaxValue));
        AddEdge(store, 11, 2, 3, GraphPropertyValue.FromFloat64(double.MaxValue));
        using GraphReadSession read = store.BeginRead();

        Assert.Throws<GraphWeightOverflowException>(() => read.WeightedShortestPath(
            new GraphElementId(1), new GraphElementId(3), GraphWeightedShortestPathOptions.ForProperty(1)));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => read.WeightedShortestPath(
            new GraphElementId(1), new GraphElementId(3),
            GraphWeightedShortestPathOptions.ForProperty(1), canceled.Token));
    }

    [Fact]
    public void WeightedShortestPath_EnforcesVisitedFrontierExpansionAndBatchBudgets()
    {
        GraphStore store = CreateStore("budgets");
        SeedWeightedGraph(store);
        using GraphReadSession read = store.BeginRead();

        Assert.Throws<GraphWeightedPathLimitExceededException>(() => read.WeightedShortestPath(
            new GraphElementId(1),
            new GraphElementId(4),
            GraphWeightedShortestPathOptions.ForProperty(1) with { MaxVisitedVertices = 1 }));
        Assert.Throws<GraphWeightedPathLimitExceededException>(() => read.WeightedShortestPath(
            new GraphElementId(1),
            new GraphElementId(4),
            GraphWeightedShortestPathOptions.ForProperty(1) with { MaxFrontier = 1 }));
        Assert.Throws<GraphWeightedPathLimitExceededException>(() => read.WeightedShortestPath(
            new GraphElementId(1),
            new GraphElementId(4),
            GraphWeightedShortestPathOptions.ForProperty(1) with { MaxExpandedEdges = 1 }));
        Assert.Throws<GraphWeightedPathLimitExceededException>(() =>
            GraphAlgorithmExecutor.ExecuteShortestPaths(
                read,
                [
                    new GraphWeightedPathQuery(new GraphElementId(1), new GraphElementId(4)),
                    new GraphWeightedPathQuery(new GraphElementId(1), new GraphElementId(2)),
                ],
                GraphWeightedShortestPathOptions.ForProperty(1),
                new GraphAlgorithmBatchOptions { MaxQueries = 1, MaxResults = 1 }));
    }

    [Fact]
    public void BatchExecutor_PreservesInputOrderAndUsesOneReadSnapshot()
    {
        GraphStore store = CreateStore("batch");
        SeedWeightedGraph(store);
        using GraphReadSession read = store.BeginRead();
        AddEdge(store, 16, 1, 4, GraphPropertyValue.FromInt64(0));
        GraphWeightedPathBatchResult result = GraphAlgorithmExecutor.ExecuteShortestPaths(
            read,
            [
                new GraphWeightedPathQuery(new GraphElementId(1), new GraphElementId(4)),
                new GraphWeightedPathQuery(new GraphElementId(4), new GraphElementId(1)),
                new GraphWeightedPathQuery(new GraphElementId(1), new GraphElementId(99)),
            ],
            GraphWeightedShortestPathOptions.ForProperty(1),
            new GraphAlgorithmBatchOptions { MaxQueries = 3, MaxResults = 3 });

        Assert.Equal(3, result.ProcessedQueries);
        Assert.Equal(3, result.Paths.Count);
        Assert.Equal(3d, result.Paths[0]!.TotalWeight);
        Assert.Null(result.Paths[1]);
        Assert.Null(result.Paths[2]);

        using GraphReadSession nextRead = store.BeginRead();
        GraphWeightedPath nextPath = Assert.IsType<GraphWeightedPath>(nextRead.Dijkstra(
            new GraphElementId(1), new GraphElementId(4), 1));
        Assert.Equal(0d, nextPath.TotalWeight);
    }

    [Fact]
    public void WeightedShortestPath_DijkstraAndBidirectionalMatchBoundedExhaustiveSearch()
    {
        GraphStore store = CreateStore("bounded-oracle");
        const int vertexCount = 8;
        const int maxDepth = 4;
        var random = new Random(362);
        var edges = new List<(long Id, long Source, long Target, long Weight)>();
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        for (long id = 1; id <= vertexCount; id++)
            transaction.UpsertVertex(new GraphElementId(id), 0, [new LabelId(1)], []);
        for (long edgeId = 1; edgeId <= 32; edgeId++)
        {
            long source = random.Next(1, vertexCount + 1);
            long target = random.Next(1, vertexCount + 1);
            long weight = random.Next(0, 10);
            edges.Add((edgeId, source, target, weight));
            transaction.UpsertEdge(
                new GraphElementId(edgeId),
                0,
                new GraphElementId(source),
                new GraphElementId(target),
                new LabelId(2),
                [new GraphProperty(1, GraphPropertyValue.FromInt64(weight))]);
        }
        transaction.Commit();

        using GraphReadSession read = store.BeginRead();
        for (long start = 1; start <= vertexCount; start++)
            for (long target = 1; target <= vertexCount; target++)
            {
                double? expected = ExhaustiveShortestPath(edges, start, target, maxDepth);
                foreach (GraphWeightedShortestPathAlgorithm algorithm in new[]
                {
                GraphWeightedShortestPathAlgorithm.Dijkstra,
                GraphWeightedShortestPathAlgorithm.BidirectionalDijkstra,
            })
                {
                    GraphWeightedPath? actual = read.WeightedShortestPath(
                        new GraphElementId(start),
                        new GraphElementId(target),
                        GraphWeightedShortestPathOptions.ForProperty(1) with
                        {
                            Algorithm = algorithm,
                            MaxDepth = maxDepth,
                            MaxFrontier = 100_000,
                        });
                    Assert.Equal(expected.HasValue, actual is not null);
                    if (expected.HasValue)
                        Assert.Equal(expected.Value, actual!.TotalWeight);
                }
            }
    }

    public void Dispose()
    {
        foreach (GraphManager manager in _managers)
            manager.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private GraphStore CreateStore(string name)
    {
        var manager = new GraphManager(
            Path.Combine(_root, name),
            KvOptions.Default with
            {
                AutoCheckpointEnabled = false,
                SyncWalOnEveryWrite = false,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            });
        _managers.Add(manager);
        return manager.Create(name);
    }

    private static void SeedWeightedGraph(GraphStore store)
    {
        CreateVertices(store, 1, 2, 3, 4);
        AddEdge(store, 11, 1, 2, GraphPropertyValue.FromInt64(10));
        AddEdge(store, 12, 1, 3, GraphPropertyValue.FromInt64(1));
        AddEdge(store, 13, 3, 2, GraphPropertyValue.FromInt64(1));
        AddEdge(store, 14, 2, 4, GraphPropertyValue.FromInt64(1));
        AddEdge(store, 15, 3, 4, GraphPropertyValue.FromInt64(20));
    }

    private static void CreateVertices(GraphStore store, params long[] ids)
    {
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        foreach (long id in ids)
            transaction.UpsertVertex(new GraphElementId(id), 0, [new LabelId(1)], []);
        transaction.Commit();
    }

    private static void AddEdge(GraphStore store, long id, long source, long target, GraphPropertyValue weight)
    {
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertEdge(
            new GraphElementId(id),
            0,
            new GraphElementId(source),
            new GraphElementId(target),
            new LabelId(2),
            [new GraphProperty(1, weight)]);
        transaction.Commit();
    }

    private static void AddEdgeWithoutWeight(GraphStore store, long id, long source, long target)
    {
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertEdge(
            new GraphElementId(id),
            0,
            new GraphElementId(source),
            new GraphElementId(target),
            new LabelId(2),
            []);
        transaction.Commit();
    }

    private static double? ExhaustiveShortestPath(
        IReadOnlyList<(long Id, long Source, long Target, long Weight)> edges,
        long start,
        long target,
        int maxDepth)
    {
        double best = double.PositiveInfinity;
        Search(start, 0, 0);
        return double.IsFinite(best) ? best : null;

        void Search(long vertex, int depth, double weight)
        {
            if (vertex == target)
                best = Math.Min(best, weight);
            if (depth >= maxDepth || weight >= best)
                return;
            foreach ((long _, long source, long neighbor, long edgeWeight) in edges)
            {
                if (source == vertex)
                    Search(neighbor, depth + 1, weight + edgeWeight);
            }
        }
    }
}
