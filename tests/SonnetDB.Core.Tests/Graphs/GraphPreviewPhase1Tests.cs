using SonnetDB.Data.Graphs;
using SonnetDB.Graphs;
using SonnetDB.Graphs.Storage;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class GraphPreviewPhase1Tests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-graph-preview-phase1-tests",
        Guid.NewGuid().ToString("N"));
    private readonly GraphManager _manager;

    public GraphPreviewPhase1Tests()
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
    public void Crud_AtomicIndexesAndReopen_PreservesNativeGraph()
    {
        GraphStore store = _manager.Create("preview");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertVertex(
            new GraphElementId(1),
            0,
            [new LabelId(1)],
            [new GraphProperty(7, GraphPropertyValue.FromString("alpha"))],
            uniquePropertyIds: [7]);
        transaction.UpsertVertex(
            new GraphElementId(2),
            0,
            [new LabelId(1)],
            [new GraphProperty(7, GraphPropertyValue.FromString("beta"))],
            uniquePropertyIds: [7]);
        transaction.UpsertEdge(
            new GraphElementId(10),
            0,
            new GraphElementId(1),
            new GraphElementId(2),
            new LabelId(2),
            [new GraphProperty(8, GraphPropertyValue.FromInt64(1))]);
        transaction.Commit();

        Assert.Throws<GraphUniqueConstraintException>(() =>
        {
            GraphTransaction conflict = store.BeginTransaction(Guid.NewGuid());
            conflict.UpsertVertex(
                new GraphElementId(3),
                0,
                [new LabelId(1)],
                [new GraphProperty(7, GraphPropertyValue.FromString("alpha"))],
                uniquePropertyIds: [7]);
            conflict.Commit();
        });

        using (GraphReadSession read = store.BeginRead())
        {
            GraphVertex vertex = Assert.IsType<GraphVertex>(read.GetVertex(new GraphElementId(1)));
            Assert.Equal("alpha", vertex.Properties.Single().Value.AsString());
            using GraphCursor<GraphExpansion> cursor = read.Expand(
                new GraphElementId(1),
                GraphDirection.Both,
                options: new GraphCursorOptions { PageSize = 1 });
            GraphExpansion expansion = Assert.Single(cursor.ReadNextPage());
            Assert.Equal(new GraphElementId(2), expansion.NeighborId);
            Assert.Empty(cursor.ReadNextPage());

            GraphVertex indexed = Assert.Single(read.SeekVertices(
                new LabelId(1),
                7,
                GraphPropertyValue.FromString("alpha"),
                new GraphCursorOptions { PageSize = 2 }).ReadNextPage());
            Assert.Equal(new GraphElementId(1), indexed.Id);
        }

        store = _manager.Reopen("preview");
        using GraphReadSession reopened = store.BeginRead();
        Assert.NotNull(reopened.GetEdge(new GraphElementId(10)));
        Assert.Equal(GraphAccessPath.NativeAdjacency,
            reopened.ExplainExpand(new GraphElementId(1)).AccessPath);
    }

    [Fact]
    public void PublicGraphModels_RejectDefaultIdsAndInvalidStatisticKeys()
    {
        GraphEdge edge = new(
            new GraphElementId(10),
            1,
            new GraphElementId(1),
            new GraphElementId(2),
            new LabelId(2),
            []);

        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphExpansion(
            default,
            new GraphElementId(2),
            GraphDirection.Outgoing,
            edge));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphExpansion(
            new GraphElementId(1),
            default,
            GraphDirection.Outgoing,
            edge));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphIndexStatisticKey(
            (GraphElementType)99,
            new LabelId(1),
            1,
            GraphPropertyKind.String));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphIndexStatisticKey(
            GraphElementType.Vertex,
            default,
            1,
            GraphPropertyKind.String));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphIndexStatisticKey(
            GraphElementType.Vertex,
            new LabelId(1),
            1,
            (GraphPropertyKind)99));
        Assert.Throws<ArgumentException>(() => new GraphVertexPredicate());
        Assert.Throws<ArgumentException>(() => new GraphVertexPredicate(propertyId: 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphVertexPredicate(labelId: default(LabelId)));
    }

    [Fact]
    public void Expand_WithTargetLabelAndProperty_OnSupernodeRemainsPagedAndBounded()
    {
        GraphStore store = _manager.Create("filtered-supernode");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertVertex(new GraphElementId(1), 0, [new LabelId(1)], []);
        for (long id = 2; id <= 1_001; id++)
        {
            bool matches = id % 100 == 0;
            transaction.UpsertVertex(
                new GraphElementId(id),
                0,
                [new LabelId(matches ? 20 : 10)],
                [new GraphProperty(7, GraphPropertyValue.FromString(matches ? "match" : "skip"))]);
            transaction.UpsertEdge(
                new GraphElementId(10_000 + id),
                0,
                new GraphElementId(1),
                new GraphElementId(id),
                new LabelId(30),
                []);
        }
        transaction.Commit();

        using GraphReadSession read = store.BeginRead();
        using GraphCursor<GraphExpansion> cursor = read.Expand(
            new GraphElementId(1),
            new GraphVertexPredicate(
                new LabelId(20),
                7,
                GraphPropertyValue.FromString("match")),
            GraphDirection.Outgoing,
            new LabelId(30),
            new GraphCursorOptions
            {
                PageSize = 3,
                MaxPageBytes = 4 * 1024,
                MaxResults = 10,
            });

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var pages = new List<int>();
        var neighborIds = new List<long>();
        while (true)
        {
            IReadOnlyList<GraphExpansion> page = cursor.ReadNextPage();
            if (page.Count == 0)
                break;
            pages.Add(page.Count);
            neighborIds.AddRange(page.Select(static item => item.NeighborId.Value));
        }
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal([3, 3, 3, 1], pages);
        Assert.Equal(Enumerable.Range(1, 10).Select(static value => (long)value * 100), neighborIds);
        Assert.True(allocatedBytes < 32L * 1024 * 1024, $"filtered expand allocated {allocatedBytes} bytes");
        Assert.True(cursor.IsExhausted);
    }

    [Fact]
    public void Traversal_BfsDfsAndShortestPath_RespectCyclesAndDepthBudget()
    {
        GraphStore store = _manager.Create("paths");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        for (int id = 1; id <= 4; id++)
            transaction.UpsertVertex(new GraphElementId(id), 0, [new LabelId(1)], []);
        transaction.UpsertEdge(new GraphElementId(10), 0, new GraphElementId(1), new GraphElementId(2), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(11), 0, new GraphElementId(2), new GraphElementId(3), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(12), 0, new GraphElementId(3), new GraphElementId(4), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(13), 0, new GraphElementId(4), new GraphElementId(1), new LabelId(2), []);
        transaction.Commit();

        using GraphReadSession read = store.BeginRead();
        GraphTraversalOptions options = new() { MaxDepth = 3, MaxFrontier = 32, MaxPaths = 32, PageSize = 8 };
        using GraphCursor<GraphPath> bfs = read.Bfs(new GraphElementId(1), options: options);
        IReadOnlyList<GraphPath> paths = bfs.ReadNextPage();
        Assert.Equal(4, paths.Count);
        Assert.Equal(0, paths[0].Depth);
        Assert.Equal(3, paths[^1].Depth);

        GraphPath? shortest = read.ShortestPath(new GraphElementId(1), new GraphElementId(4), options: options);
        Assert.NotNull(shortest);
        Assert.Equal(3, shortest!.Depth);

        using GraphCursor<GraphPath> dfs = read.Paths(
            new GraphElementId(1),
            minDepth: 2,
            maxDepth: 2,
            options: options);
        Assert.All(dfs.ReadNextPage(), static path => Assert.Equal(2, path.Depth));
    }

    [Fact]
    public void Expand_IncomingBothSelfLoopAndParallelEdges_ReturnsCompletePagedResults()
    {
        GraphStore store = _manager.Create("directions");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        for (int id = 1; id <= 4; id++)
            transaction.UpsertVertex(new GraphElementId(id), 0, [new LabelId(1)], []);
        transaction.UpsertEdge(new GraphElementId(10), 0, new GraphElementId(2), new GraphElementId(1), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(11), 0, new GraphElementId(2), new GraphElementId(1), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(12), 0, new GraphElementId(3), new GraphElementId(1), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(13), 0, new GraphElementId(1), new GraphElementId(4), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(14), 0, new GraphElementId(1), new GraphElementId(1), new LabelId(2), []);
        transaction.Commit();

        using GraphReadSession read = store.BeginRead();
        GraphExpansion[] incoming = ReadAll(read.Expand(
            new GraphElementId(1),
            GraphDirection.Incoming,
            options: new GraphCursorOptions { PageSize = 3 }));
        Assert.Equal([10L, 11L, 12L, 14L], incoming.Select(static item => item.Edge.Id.Value).Order().ToArray());
        Assert.All(incoming, static item => Assert.Equal(GraphDirection.Incoming, item.Direction));

        GraphExpansion[] outgoing = ReadAll(read.Expand(
            new GraphElementId(1),
            GraphDirection.Outgoing,
            options: new GraphCursorOptions { PageSize = 3 }));
        Assert.Equal([13L, 14L], outgoing.Select(static item => item.Edge.Id.Value).Order().ToArray());
        Assert.All(outgoing, static item => Assert.Equal(GraphDirection.Outgoing, item.Direction));

        GraphExpansion[] both = ReadAll(read.Expand(
            new GraphElementId(1),
            GraphDirection.Both,
            options: new GraphCursorOptions { PageSize = 3 }));
        Assert.Equal([10L, 11L, 12L, 13L, 14L], both.Select(static item => item.Edge.Id.Value).Order().ToArray());
        Assert.Single(both, static item => item.Edge.Id.Value == 14);

        GraphPath[] traversed = ReadAll(read.Bfs(
            new GraphElementId(1),
            GraphDirection.Both,
            options: new GraphTraversalOptions
            {
                MaxDepth = 1,
                MaxFrontier = 8,
                MaxPaths = 8,
                PageSize = 3,
                PathUniqueness = GraphPathUniqueness.Edge,
            }));
        Assert.Equal(6, traversed.Length);
        Assert.Equal(
            [10L, 11L, 12L, 13L, 14L],
            traversed.Where(static path => path.Depth == 1).Select(static path => path.EdgeIds[0].Value).Order().ToArray());
    }

    [Fact]
    public void ShortestPath_MaxPathsExhausted_ThrowsInsteadOfReturningNull()
    {
        GraphStore store = _manager.Create("shortest-path-budget");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        for (int id = 1; id <= 4; id++)
            transaction.UpsertVertex(new GraphElementId(id), 0, [new LabelId(1)], []);
        transaction.UpsertEdge(new GraphElementId(10), 0, new GraphElementId(1), new GraphElementId(2), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(11), 0, new GraphElementId(1), new GraphElementId(3), new LabelId(2), []);
        transaction.Commit();

        using GraphReadSession read = store.BeginRead();
        GraphTraversalOptions exhausted = new()
        {
            MaxDepth = 1,
            MaxFrontier = 8,
            MaxPaths = 2,
            PageSize = 8,
        };
        GraphTraversalLimitExceededException error = Assert.Throws<GraphTraversalLimitExceededException>(
            () => read.ShortestPath(new GraphElementId(1), new GraphElementId(3), options: exhausted));
        Assert.Contains("2", error.Message, StringComparison.Ordinal);

        GraphPath withinBudget = Assert.IsType<GraphPath>(
            read.ShortestPath(new GraphElementId(1), new GraphElementId(2), options: exhausted));
        Assert.Equal([1L, 2L], withinBudget.VertexIds.Select(static id => id.Value).ToArray());

        GraphPath? unreachable = read.ShortestPath(
            new GraphElementId(1),
            new GraphElementId(4),
            options: exhausted with { MaxPaths = 3 });
        Assert.Null(unreachable);
    }

    [Fact]
    public void Traversal_MaxExpandedEdges_StopsBeforeScanningPastBudget()
    {
        GraphStore store = _manager.Create("expanded-edge-budget");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        for (int id = 1; id <= 3; id++)
            transaction.UpsertVertex(new GraphElementId(id), 0, [new LabelId(1)], []);
        transaction.UpsertEdge(new GraphElementId(10), 0, new GraphElementId(1), new GraphElementId(2), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(11), 0, new GraphElementId(1), new GraphElementId(3), new LabelId(2), []);
        transaction.Commit();

        using GraphReadSession read = store.BeginRead();
        GraphTraversalOptions options = new()
        {
            MaxDepth = 1,
            MaxFrontier = 8,
            MaxPaths = 8,
            MaxExpandedEdges = 2,
            PageSize = 128,
            PathUniqueness = GraphPathUniqueness.Edge,
        };
        Assert.Equal(2, ReadAll(read.Paths(new GraphElementId(1), 1, 1, options: options)).Length);

        using GraphCursor<GraphPath> limited = read.Paths(
            new GraphElementId(1),
            1,
            1,
            options: options with { MaxExpandedEdges = 1 });
        GraphTraversalLimitExceededException error = Assert.Throws<GraphTraversalLimitExceededException>(
            () => limited.ReadNextPage());
        Assert.Contains("1", error.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentOutOfRangeException>(() => read.Bfs(
            new GraphElementId(1),
            options: options with { MaxExpandedEdges = 0 }));
    }

    [Fact]
    public void Traversal_SmallPagesTerminalFrontierAndUniqueness_DoNotLosePaths()
    {
        GraphStore store = _manager.Create("paged-paths");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        for (int id = 1; id <= 7; id++)
            transaction.UpsertVertex(new GraphElementId(id), 0, [new LabelId(1)], []);
        transaction.UpsertEdge(new GraphElementId(10), 0, new GraphElementId(1), new GraphElementId(2), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(11), 0, new GraphElementId(1), new GraphElementId(3), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(12), 0, new GraphElementId(1), new GraphElementId(4), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(13), 0, new GraphElementId(2), new GraphElementId(5), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(14), 0, new GraphElementId(3), new GraphElementId(6), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(15), 0, new GraphElementId(4), new GraphElementId(7), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(16), 0, new GraphElementId(1), new GraphElementId(1), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(17), 0, new GraphElementId(1), new GraphElementId(2), new LabelId(2), []);
        transaction.Commit();

        using GraphReadSession read = store.BeginRead();
        GraphPath[] breadthFirst = ReadAll(read.Bfs(
            new GraphElementId(1),
            options: new GraphTraversalOptions
            {
                MaxDepth = 2,
                MaxFrontier = 8,
                MaxPaths = 32,
                PageSize = 2,
            }));
        Assert.Equal([0, 1, 1, 1, 2, 2, 2], breadthFirst.Select(static path => path.Depth).ToArray());

        GraphPath[] terminal = ReadAll(read.Paths(
            new GraphElementId(1),
            minDepth: 1,
            maxDepth: 1,
            options: new GraphTraversalOptions
            {
                MaxDepth = 1,
                MaxFrontier = 1,
                MaxPaths = 32,
                PageSize = 1,
                PathUniqueness = GraphPathUniqueness.Edge,
            }));
        Assert.Equal(5, terminal.Length);
        Assert.Equal(2, terminal.Count(static path => path.VertexIds[^1] == new GraphElementId(2)));
        Assert.Single(terminal, static path => path.VertexIds[^1] == new GraphElementId(1));

        using GraphCursor<GraphPath> cancelled = read.Bfs(new GraphElementId(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => cancelled.ReadNextPage(cancellation.Token));
        Assert.Throws<InvalidOperationException>(() => cancelled.ReadNextPage());
    }

    [Fact]
    public void UniqueIndexes_BatchUpdateDeleteAndReopen_RemainConsistent()
    {
        GraphStore store = _manager.Create("unique-lifecycle");
        GraphTransaction duplicateBatch = store.BeginTransaction(Guid.NewGuid());
        duplicateBatch.UpsertVertex(
            new GraphElementId(1),
            0,
            [new LabelId(1)],
            [new GraphProperty(7, GraphPropertyValue.FromString("same"))],
            [7]);
        duplicateBatch.UpsertVertex(
            new GraphElementId(2),
            0,
            [new LabelId(1)],
            [new GraphProperty(7, GraphPropertyValue.FromString("same"))],
            [7]);
        Assert.Throws<GraphUniqueConstraintException>(() => duplicateBatch.Commit());
        using (GraphReadSession empty = store.BeginRead())
        {
            Assert.Null(empty.GetVertex(new GraphElementId(1)));
            Assert.Null(empty.GetVertex(new GraphElementId(2)));
        }

        GraphTransaction create = store.BeginTransaction(Guid.NewGuid());
        create.UpsertVertex(
            new GraphElementId(1),
            0,
            [new LabelId(1)],
            [new GraphProperty(7, GraphPropertyValue.FromString("alpha"))],
            [7]);
        create.Commit();

        GraphTransaction update = store.BeginTransaction(Guid.NewGuid());
        update.UpsertVertex(
            new GraphElementId(1),
            1,
            [new LabelId(1)],
            [new GraphProperty(7, GraphPropertyValue.FromString("gamma"))],
            [7]);
        update.Commit();

        GraphTransaction reuseOldValue = store.BeginTransaction(Guid.NewGuid());
        reuseOldValue.UpsertVertex(
            new GraphElementId(2),
            0,
            [new LabelId(1)],
            [new GraphProperty(7, GraphPropertyValue.FromString("alpha"))],
            [7]);
        reuseOldValue.Commit();

        GraphTransaction conflict = store.BeginTransaction(Guid.NewGuid());
        conflict.UpsertVertex(
            new GraphElementId(3),
            0,
            [new LabelId(1)],
            [new GraphProperty(7, GraphPropertyValue.FromString("gamma"))],
            [7]);
        Assert.Throws<GraphUniqueConstraintException>(() => conflict.Commit());

        GraphTransaction delete = store.BeginTransaction(Guid.NewGuid());
        delete.DeleteVertex(new GraphElementId(1), 2);
        delete.Commit();
        GraphTransaction reuseDeletedValue = store.BeginTransaction(Guid.NewGuid());
        reuseDeletedValue.UpsertVertex(
            new GraphElementId(3),
            0,
            [new LabelId(1)],
            [new GraphProperty(7, GraphPropertyValue.FromString("gamma"))],
            [7]);
        reuseDeletedValue.Commit();

        store = _manager.Reopen("unique-lifecycle");
        Assert.True(GraphInvariantChecker.Check(store).IsComplete);
        GraphTransaction reopenedConflict = store.BeginTransaction(Guid.NewGuid());
        reopenedConflict.UpsertVertex(
            new GraphElementId(4),
            0,
            [new LabelId(1)],
            [new GraphProperty(7, GraphPropertyValue.FromString("gamma"))],
            [7]);
        Assert.Throws<GraphUniqueConstraintException>(() => reopenedConflict.Commit());
    }

    [Fact]
    public void Statistics_RefreshAndExplain_ReportNativeCardinality()
    {
        GraphStore store = _manager.Create("stats");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertVertex(new GraphElementId(1), 0, [new LabelId(3)], [new GraphProperty(9, GraphPropertyValue.FromString("same"))]);
        transaction.UpsertVertex(new GraphElementId(2), 0, [new LabelId(3)], []);
        transaction.UpsertEdge(new GraphElementId(4), 0, new GraphElementId(1), new GraphElementId(2), new LabelId(5), []);
        transaction.Commit();

        using GraphReadSession read = store.BeginRead();
        GraphStatistics statistics = read.RefreshStatistics();
        Assert.Equal(2, statistics.VertexCount);
        Assert.Equal(1, statistics.EdgeCount);
        Assert.Equal(2, statistics.LabelCardinality[new LabelId(3)]);
        Assert.Equal(1, statistics.LabelCardinality[new LabelId(5)]);
        Assert.Equal(1, statistics.DegreeHistogram[1]);
        Assert.Equal(1, statistics.DegreeHistogram[0]);
        GraphExplain missing = read.ExplainVertexSeek(
            new LabelId(3),
            9,
            GraphPropertyValue.FromString("same"));
        Assert.Null(missing.EstimatedRows);
        Assert.Equal("statistics_missing", missing.EstimateSource);
        Assert.Null(missing.StatisticsSequence);
        GraphExplain explain = read.ExplainVertexSeek(
            new LabelId(3),
            9,
            GraphPropertyValue.FromString("same"),
            statistics);
        Assert.Equal(GraphAccessPath.NativeIndexSeek, explain.AccessPath);
        Assert.Equal(1, explain.EstimatedRows);
        Assert.Equal("refreshed", explain.EstimateSource);

        GraphTransaction next = store.BeginTransaction(Guid.NewGuid());
        next.UpsertVertex(
            new GraphElementId(3),
            0,
            [new LabelId(3)],
            [new GraphProperty(9, GraphPropertyValue.FromString("same"))]);
        next.Commit();
        using GraphReadSession newer = store.BeginRead();
        GraphExplain stale = newer.ExplainVertexSeek(
            new LabelId(3),
            9,
            GraphPropertyValue.FromString("same"),
            statistics);
        Assert.Equal("stale", stale.EstimateSource);
        Assert.Equal(1, stale.EstimatedRows);
        Assert.Equal(statistics.Sequence, stale.StatisticsSequence);
    }

    [Fact]
    public void RebuildIndexes_RemovesOrphansAndRestoresMissingDerivedEntries()
    {
        GraphStore store = _manager.Create("repair");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertVertex(
            new GraphElementId(1),
            0,
            [new LabelId(1)],
            [new GraphProperty(7, GraphPropertyValue.FromString("repair"))]);
        transaction.UpsertVertex(new GraphElementId(2), 0, [new LabelId(1)], []);
        transaction.UpsertEdge(
            new GraphElementId(10),
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
            new GraphElementId(10))));
        Assert.True(store.Keyspace.Delete(GraphKeyCodec.EncodePropertyIndex(
            GraphElementKind.Vertex,
            new LabelId(1),
            7,
            GraphPropertyValue.FromString("repair"),
            new GraphElementId(1))));
        _ = store.Keyspace.Put(
            GraphKeyCodec.EncodeLabelMembership(GraphElementKind.Vertex, new LabelId(99), new GraphElementId(999)), []);

        GraphIndexRebuildResult result = store.RebuildIndexes();

        Assert.True(result.ScannedRecords >= 3);
        Assert.True(result.RepairedEntries >= 2);
        Assert.True(result.RemovedEntries >= 1);
        using GraphReadSession read = store.BeginRead();
        GraphVertex[] repairedVertices = read.SeekVertices(
            new LabelId(1),
            7,
            GraphPropertyValue.FromString("repair")).ReadNextPage().ToArray();
        Assert.Single(repairedVertices);
        GraphExpansion[] repairedExpansions = read.Expand(new GraphElementId(2), GraphDirection.Incoming).ReadNextPage().ToArray();
        Assert.Single(repairedExpansions);
        Assert.True(GraphInvariantChecker.Check(store).IsValid);
    }

    [Fact]
    public void RebuildIndexes_WithSuppliedUniqueDeclaration_RestoresMissingUniqueKey()
    {
        GraphStore store = _manager.Create("repair-unique");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertVertex(
            new GraphElementId(1),
            0,
            [new LabelId(1)],
            [new GraphProperty(7, GraphPropertyValue.FromString("only"))],
            [7]);
        transaction.Commit();
        Assert.True(store.Keyspace.Delete(GraphKeyCodec.EncodeUniqueProperty(
            GraphElementKind.Vertex,
            new LabelId(1),
            7,
            GraphPropertyValue.FromString("only"))));

        GraphIndexRebuildResult result = store.RebuildIndexes(new GraphIndexRebuildOptions
        {
            UniqueIndexes = [new GraphUniqueIndexDefinition(GraphElementType.Vertex, new LabelId(1), 7)],
        });

        Assert.True(result.UniqueDeclarationsWereSupplied);
        GraphTransaction conflict = store.BeginTransaction(Guid.NewGuid());
        conflict.UpsertVertex(
            new GraphElementId(2),
            0,
            [new LabelId(1)],
            [new GraphProperty(7, GraphPropertyValue.FromString("only"))],
            [7]);
        Assert.Throws<GraphUniqueConstraintException>(() => conflict.Commit());
        Assert.True(GraphInvariantChecker.Check(store).IsValid);
    }

    [Fact]
    public async Task TypedClient_EmbeddedJsonImporterAndNdjsonExpand_UseSameCoreSemantics()
    {
        string clientRoot = Path.Combine(_root, "client");
        using var client = new SndbGraphClient($"Data Source={clientRoot}");
        await client.CreateGraphAsync("code");
        string json = """
            {
              "requestId": "00000000-0000-0000-0000-000000000001",
              "vertices": [{"id":1,"labels":[1]}, {"id":2,"labels":[1]}],
              "edges": [{"id":3,"sourceId":1,"targetId":2,"labelId":2}]
            }
            """;
        await using var source = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        SndbGraphImportReport report = await SndbGraphImporter.ImportJsonAsync(
            client,
            "code",
            source,
            new SndbGraphImportOptions { RequestId = Guid.Parse("00000000-0000-0000-0000-000000000010") });
        Assert.Equal((2, 1), (report.VertexCount, report.EdgeCount));
        GraphVertex? vertex = await client.GetVertexAsync("code", new GraphElementId(1));
        Assert.NotNull(vertex);
        List<GraphExpansion> expansions = [];
        await foreach (GraphExpansion expansion in client.ExpandAsync(
            "code",
            new GraphExpandRequest { VertexId = 1, Direction = GraphDirection.Outgoing }))
        {
            expansions.Add(expansion);
        }
        Assert.Equal(new GraphElementId(2), Assert.Single(expansions).NeighborId);
    }

    [Fact]
    public async Task JsonImporter_NormalizedGraphJson_ReordersBatchesPreservesMetadataAndResumesAfterReopen()
    {
        string clientRoot = Path.Combine(_root, "graph-json-resume");
        const string Json = """
            {
              "relationships": [
                {
                  "id": "rel:1",
                  "source": "symbol:a",
                  "target": "symbol:b",
                  "type": "CALLS",
                  "properties": { "line": 42 },
                  "provenance": "fixture.cs",
                  "confidence": 0.95
                }
              ],
              "nodes": [
                {
                  "id": "symbol:a",
                  "labels": ["Method"],
                  "properties": { "name": "A" },
                  "provenance": "fixture.cs",
                  "confidence": 0.99
                },
                {
                  "id": "symbol:b",
                  "type": "Method",
                  "properties": { "name": "B" }
                }
              ]
            }
            """;
        Guid importId = Guid.Parse("35100000-0000-0000-0000-000000000001");
        var options = new SndbGraphImportOptions { RequestId = importId, BatchSize = 1 };

        using (var first = new SndbGraphClient($"Data Source={clientRoot}"))
        {
            await first.CreateGraphAsync("code");
            await using var source = new ChunkedReadStream(System.Text.Encoding.UTF8.GetBytes(Json), 7);
            SndbGraphImportReport report = await SndbGraphImporter.ImportJsonAsync(first, "code", source, options);
            Assert.Equal((2, 1, 3), (report.VertexCount, report.EdgeCount, report.BatchCount));
        }

        using var reopened = new SndbGraphClient($"Data Source={clientRoot}");
        await using (var retrySource = new ChunkedReadStream(System.Text.Encoding.UTF8.GetBytes(Json), 11))
        {
            SndbGraphImportReport retry = await SndbGraphImporter.ImportJsonAsync(reopened, "code", retrySource, options);
            Assert.Equal((2, 1, 3), (retry.VertexCount, retry.EdgeCount, retry.BatchCount));
        }

        GraphElementId firstId = SndbGraphImporter.GetStableElementId("symbol:a");
        GraphVertex vertex = Assert.IsType<GraphVertex>(await reopened.GetVertexAsync("code", firstId));
        Assert.Contains(SndbGraphImporter.GetStableLabelId("Method"), vertex.Labels);
        Assert.Equal(
            "fixture.cs",
            vertex.Properties.Single(property => property.PropertyId == SndbGraphImporter.GetStablePropertyId("provenance"))
                .Value.AsString());
        Assert.Equal(
            0.99,
            vertex.Properties.Single(property => property.PropertyId == SndbGraphImporter.GetStablePropertyId("confidence"))
                .Value.AsFloat64());

        var expansions = new List<GraphExpansion>();
        await foreach (GraphExpansion expansion in reopened.ExpandAsync(
            "code",
            new GraphExpandRequest { VertexId = firstId.Value }))
        {
            expansions.Add(expansion);
        }
        GraphExpansion call = Assert.Single(expansions);
        Assert.Equal(SndbGraphImporter.GetStableElementId("symbol:b"), call.NeighborId);
        Assert.Equal(SndbGraphImporter.GetStableLabelId("CALLS"), call.Edge.LabelId);
        Assert.Equal(
            "fixture.cs",
            call.Edge.Properties.Single(property => property.PropertyId == SndbGraphImporter.GetStablePropertyId("provenance"))
                .Value.AsString());
    }

    [Fact]
    public async Task CsvImporter_BatchesQuotedLabelsAndRetriesIdempotently()
    {
        string clientRoot = Path.Combine(_root, "csv-import");
        Guid importId = Guid.Parse("35100000-0000-0000-0000-000000000002");
        var options = new SndbGraphImportOptions { RequestId = importId, BatchSize = 1 };

        using (var first = new SndbGraphClient($"Data Source={clientRoot}"))
        {
            await first.CreateGraphAsync("code");
            await using var vertices = new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes("id,labels\n1,\"1;2\"\n2,1\n"));
            await using var edges = new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes("id,sourceId,targetId,labelId\n10,1,2,3\n"));
            SndbGraphImportReport report = await SndbGraphImporter.ImportCsvAsync(
                first,
                "code",
                vertices,
                edges,
                options);
            Assert.Equal((2, 1, 3), (report.VertexCount, report.EdgeCount, report.BatchCount));
        }

        using var reopened = new SndbGraphClient($"Data Source={clientRoot}");
        await using (var retryVertices = new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes("id,labels\n1,\"1;2\"\n2,1\n")))
        await using (var retryEdges = new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes("id,sourceId,targetId,labelId\n10,1,2,3\n")))
        {
            SndbGraphImportReport retry = await SndbGraphImporter.ImportCsvAsync(
                reopened,
                "code",
                retryVertices,
                retryEdges,
                options);
            Assert.Equal((2, 1, 3), (retry.VertexCount, retry.EdgeCount, retry.BatchCount));
        }

        GraphVertex vertex = Assert.IsType<GraphVertex>(
            await reopened.GetVertexAsync("code", new GraphElementId(1)));
        Assert.Equal([new LabelId(1), new LabelId(2)], vertex.Labels);
        Assert.NotNull(await reopened.GetEdgeAsync("code", new GraphElementId(10)));
    }

    [Fact]
    public async Task CsvImporter_WithByteBudget_SplitsBatchesBeforeElementCountLimit()
    {
        string clientRoot = Path.Combine(_root, "csv-byte-batches");
        using var client = new SndbGraphClient($"Data Source={clientRoot}");
        await client.CreateGraphAsync("code");
        string rows = "id,labels\n" + string.Join('\n', Enumerable.Range(1, 12).Select(static id => $"{id},1")) + "\n";
        await using var vertices = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rows));
        await using var edges = new MemoryStream();

        SndbGraphImportReport report = await SndbGraphImporter.ImportCsvAsync(
            client,
            "code",
            vertices,
            edges,
            new SndbGraphImportOptions
            {
                RequestId = Guid.Parse("35100000-0000-0000-0000-000000000004"),
                BatchSize = 100,
                MaxBatchBytes = 512,
                MaxCsvLineBytes = 128,
            });

        Assert.Equal(12, report.VertexCount);
        Assert.True(report.BatchCount > 1);
        Assert.NotNull(await client.GetVertexAsync("code", new GraphElementId(12)));
    }

    [Fact]
    public async Task CsvImporter_WithOversizeLaterLine_RejectsBeforePublishingAnyBatch()
    {
        string clientRoot = Path.Combine(_root, "csv-line-limit");
        using var client = new SndbGraphClient($"Data Source={clientRoot}");
        await client.CreateGraphAsync("code");
        string rows = "id,labels\n1,1\n2," + new string('9', 40) + "\n";
        await using var vertices = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rows));
        await using var edges = new MemoryStream();

        GraphImportLimitExceededException error = await Assert.ThrowsAsync<GraphImportLimitExceededException>(
            () => SndbGraphImporter.ImportCsvAsync(
                client,
                "code",
                vertices,
                edges,
                new SndbGraphImportOptions
                {
                    RequestId = Guid.Parse("35100000-0000-0000-0000-000000000005"),
                    MaxBatchBytes = 512,
                    MaxCsvLineBytes = 32,
                }));

        Assert.Equal("csv_line", error.LimitName);
        Assert.Equal(32, error.MaximumBytes);
        Assert.Null(await client.GetVertexAsync("code", new GraphElementId(1)));
    }

    [Fact]
    public async Task JsonImporter_TruncatedDocument_RejectsInputWithoutPublishingPartialBatch()
    {
        string clientRoot = Path.Combine(_root, "json-invalid");
        using var client = new SndbGraphClient($"Data Source={clientRoot}");
        await client.CreateGraphAsync("code");
        await using var source = new ChunkedReadStream(
            System.Text.Encoding.UTF8.GetBytes("{\"nodes\":[{\"id\":\"symbol:a\"}"),
            3);

        await Assert.ThrowsAsync<InvalidDataException>(() => SndbGraphImporter.ImportJsonAsync(
            client,
            "code",
            source,
            new SndbGraphImportOptions
            {
                RequestId = Guid.Parse("35100000-0000-0000-0000-000000000003"),
            }));
        Assert.Null(await client.GetVertexAsync("code", SndbGraphImporter.GetStableElementId("symbol:a")));
    }

    public void Dispose()
    {
        _manager.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static T[] ReadAll<T>(GraphCursor<T> cursor) where T : class
    {
        using (cursor)
        {
            var result = new List<T>();
            while (true)
            {
                IReadOnlyList<T> page = cursor.ReadNextPage();
                if (page.Count == 0)
                    return result.ToArray();
                result.AddRange(page);
            }
        }
    }

    private sealed class ChunkedReadStream(byte[] value, int maximumRead) : Stream
    {
        private readonly MemoryStream _inner = new(value, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
            => _inner.Read(buffer, offset, Math.Min(count, maximumRead));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer[..Math.Min(buffer.Length, maximumRead)], cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
