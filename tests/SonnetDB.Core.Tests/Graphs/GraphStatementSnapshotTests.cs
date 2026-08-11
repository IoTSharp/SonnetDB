using SonnetDB.Graphs;
using SonnetDB.Kv;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class GraphStatementSnapshotTests : IDisposable
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-graph-statement-snapshot-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<GraphManager> _managers = [];

    [Fact]
    public async Task Bfs_PagedDuringConcurrentReparent_PreservesStatementSnapshotAndDoesNotBlockWriter()
    {
        GraphStore store = CreateStore("long-traversal");
        GraphTransaction seed = store.BeginTransaction(Guid.NewGuid());
        for (long id = 1; id <= 3; id++)
            seed.UpsertVertex(new GraphElementId(id), 0, [new LabelId(1)], []);
        seed.UpsertEdge(
            new GraphElementId(10),
            0,
            new GraphElementId(1),
            new GraphElementId(2),
            new LabelId(2),
            []);
        seed.UpsertEdge(
            new GraphElementId(11),
            0,
            new GraphElementId(2),
            new GraphElementId(3),
            new LabelId(2),
            []);
        seed.Commit();

        using GraphReadSession statement = store.BeginRead();
        using GraphCursor<GraphPath> cursor = statement.Bfs(
            new GraphElementId(1),
            options: new GraphTraversalOptions
            {
                MaxDepth = 2,
                MaxFrontier = 16,
                MaxPaths = 16,
                PageSize = 1,
            });
        GraphPath first = Assert.Single(cursor.ReadNextPage());
        Assert.Equal(0, first.Depth);
        Assert.Equal(statement.Sequence, cursor.SnapshotSequence);

        Task<GraphCommitResult> writer = Task.Run(() =>
        {
            GraphTransaction reparent = store.BeginTransaction(Guid.NewGuid());
            reparent.DeleteEdge(new GraphElementId(10), 1);
            reparent.UpsertEdge(
                new GraphElementId(12),
                0,
                new GraphElementId(1),
                new GraphElementId(3),
                new LabelId(2),
                []);
            return reparent.Commit();
        });
        GraphCommitResult committed = await writer.WaitAsync(OperationTimeout);

        GraphPath[] oldPaths = [first, .. ReadAll(cursor)];
        Assert.True(committed.Sequence > statement.Sequence);
        Assert.Equal([1L, 2L, 3L], oldPaths.Select(static path => path.VertexIds[^1].Value).ToArray());
        Assert.NotNull(statement.GetEdge(new GraphElementId(10)));
        Assert.Null(statement.GetEdge(new GraphElementId(12)));

        using GraphReadSession nextStatement = store.BeginRead();
        using GraphCursor<GraphPath> nextCursor = nextStatement.Bfs(
            new GraphElementId(1),
            options: new GraphTraversalOptions
            {
                MaxDepth = 2,
                MaxFrontier = 16,
                MaxPaths = 16,
                PageSize = 1,
            });
        GraphPath[] newPaths = ReadAll(nextCursor);
        Assert.Equal([1L, 3L], newPaths.Select(static path => path.VertexIds[^1].Value).ToArray());
        Assert.Null(nextStatement.GetEdge(new GraphElementId(10)));
        Assert.NotNull(nextStatement.GetEdge(new GraphElementId(12)));
        Assert.True(nextStatement.Sequence > statement.Sequence);
        Assert.True(GraphInvariantChecker.Check(store).IsValid);
    }

    [Fact]
    public void ScanVertices_CursorOpenedAfterConcurrentCommit_StillUsesSessionSnapshot()
    {
        GraphStore store = CreateStore("late-cursor");
        GraphTransaction seed = store.BeginTransaction(Guid.NewGuid());
        seed.UpsertVertex(new GraphElementId(1), 0, [new LabelId(1)], []);
        seed.UpsertVertex(new GraphElementId(2), 0, [new LabelId(1)], []);
        seed.Commit();

        using GraphReadSession statement = store.BeginRead();
        using GraphCursor<GraphVertex> beforeCommit = statement.ScanVertices();
        GraphTransaction writer = store.BeginTransaction(Guid.NewGuid());
        writer.UpsertVertex(new GraphElementId(3), 0, [new LabelId(1)], []);
        GraphCommitResult committed = writer.Commit();
        using GraphCursor<GraphVertex> afterCommit = statement.ScanVertices();

        Assert.True(committed.Sequence > statement.Sequence);
        Assert.Equal(statement.Sequence, beforeCommit.SnapshotSequence);
        Assert.Equal(statement.Sequence, afterCommit.SnapshotSequence);
        Assert.Equal([1L, 2L], ReadAll(beforeCommit).Select(static vertex => vertex.Id.Value).ToArray());
        Assert.Equal([1L, 2L], ReadAll(afterCommit).Select(static vertex => vertex.Id.Value).ToArray());

        using GraphReadSession nextStatement = store.BeginRead();
        using GraphCursor<GraphVertex> nextCursor = nextStatement.ScanVertices();
        Assert.Equal([1L, 2L, 3L], ReadAll(nextCursor).Select(static vertex => vertex.Id.Value).ToArray());
    }

    [Fact]
    public async Task Commit_DisjointVertexUpdates_BothSucceedWithoutDeadlockOrStarvation()
    {
        GraphStore store = CreateStore("disjoint-writes");
        GraphTransaction seed = store.BeginTransaction(Guid.NewGuid());
        seed.UpsertVertex(
            new GraphElementId(1),
            0,
            [new LabelId(1)],
            [new GraphProperty(1, GraphPropertyValue.FromString("before-first"))]);
        seed.UpsertVertex(
            new GraphElementId(2),
            0,
            [new LabelId(1)],
            [new GraphProperty(1, GraphPropertyValue.FromString("before-second"))]);
        seed.Commit();
        using var barrier = new Barrier(2);
        store.BeforeTransactionConditionalCommitTestHook = () => SignalAndWait(barrier);
        try
        {
            GraphTransaction first = store.BeginTransaction(Guid.NewGuid());
            first.UpsertVertex(
                new GraphElementId(1),
                1,
                [new LabelId(1)],
                [new GraphProperty(1, GraphPropertyValue.FromString("first"))]);
            GraphTransaction second = store.BeginTransaction(Guid.NewGuid());
            second.UpsertVertex(
                new GraphElementId(2),
                1,
                [new LabelId(1)],
                [new GraphProperty(1, GraphPropertyValue.FromString("second"))]);

            Exception?[] results = await CommitConcurrently(first, second);

            Assert.All(results, static exception => Assert.Null(exception));
        }
        finally
        {
            store.BeforeTransactionConditionalCommitTestHook = null;
        }

        using GraphReadSession read = store.BeginRead();
        Assert.Equal(2, read.GetVertex(new GraphElementId(1))!.ElementVersion);
        Assert.Equal(2, read.GetVertex(new GraphElementId(2))!.ElementVersion);
        Assert.True(GraphInvariantChecker.Check(store).IsValid);
    }

    [Fact]
    public async Task Commit_ConcurrentUniquePropertyClaim_ExactlyOneWinsAndPreservesOwner()
    {
        GraphStore store = CreateStore("unique-claim");
        GraphTransaction seed = store.BeginTransaction(Guid.NewGuid());
        seed.UpsertVertex(
            new GraphElementId(1),
            0,
            [new LabelId(1)],
            [new GraphProperty(7, GraphPropertyValue.FromString("before-first"))]);
        seed.UpsertVertex(
            new GraphElementId(2),
            0,
            [new LabelId(1)],
            [new GraphProperty(7, GraphPropertyValue.FromString("before-second"))]);
        seed.Commit();
        using var barrier = new Barrier(2);
        store.BeforeTransactionConditionalCommitTestHook = () => SignalAndWait(barrier);
        try
        {
            GraphProperty shared = new(7, GraphPropertyValue.FromString("shared"));
            GraphTransaction first = store.BeginTransaction(Guid.NewGuid());
            first.UpsertVertex(new GraphElementId(1), 1, [new LabelId(1)], [shared], [7]);
            GraphTransaction second = store.BeginTransaction(Guid.NewGuid());
            second.UpsertVertex(new GraphElementId(2), 1, [new LabelId(1)], [shared], [7]);

            Exception?[] results = await CommitConcurrently(first, second);

            Assert.Single(results, static exception => exception is null);
            Assert.Single(results, static exception => exception is GraphConcurrencyException);
        }
        finally
        {
            store.BeforeTransactionConditionalCommitTestHook = null;
        }

        using GraphReadSession read = store.BeginRead();
        GraphVertex[] vertices =
        [
            read.GetVertex(new GraphElementId(1))!,
            read.GetVertex(new GraphElementId(2))!,
        ];
        GraphVertex owner = Assert.Single(
            vertices,
            static vertex => vertex.Properties.Single().Value.AsString() == "shared");
        GraphVertex unmodified = Assert.Single(
            vertices,
            static vertex => vertex.Properties.Single().Value.AsString() != "shared");
        Assert.Equal(2, owner.ElementVersion);
        Assert.Equal(1, unmodified.ElementVersion);
        Assert.True(GraphInvariantChecker.Check(store).IsValid);
    }

    [Fact]
    public async Task Commit_ConcurrentEndpointDeleteAndEdgeInsert_ExactlyOneWinsWithoutOrphan()
    {
        GraphStore store = CreateStore("endpoint-delete");
        CreateVertices(store, 1, 2);
        using var barrier = new Barrier(2);
        store.BeforeTransactionConditionalCommitTestHook = () => SignalAndWait(barrier);
        try
        {
            GraphTransaction insertEdge = store.BeginTransaction(Guid.NewGuid());
            insertEdge.UpsertEdge(
                new GraphElementId(10),
                0,
                new GraphElementId(1),
                new GraphElementId(2),
                new LabelId(2),
                []);
            GraphTransaction deleteEndpoint = store.BeginTransaction(Guid.NewGuid());
            deleteEndpoint.DeleteVertex(new GraphElementId(2), 1);

            Exception?[] results = await CommitConcurrently(insertEdge, deleteEndpoint);

            Assert.Single(results, static exception => exception is null);
            Exception loser = Assert.Single(results, static exception => exception is not null)!;
            Assert.True(
                loser is GraphConcurrencyException or GraphVertexDeleteRestrictedException,
                $"Unexpected conflict type: {loser.GetType().FullName}");
        }
        finally
        {
            store.BeforeTransactionConditionalCommitTestHook = null;
        }

        using GraphReadSession read = store.BeginRead();
        bool endpointExists = read.GetVertex(new GraphElementId(2)) is not null;
        bool edgeExists = read.GetEdge(new GraphElementId(10)) is not null;
        Assert.Equal(endpointExists, edgeExists);
        Assert.True(GraphInvariantChecker.Check(store).IsValid);
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

    private static void CreateVertices(GraphStore store, params long[] ids)
    {
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        foreach (long id in ids)
            transaction.UpsertVertex(new GraphElementId(id), 0, [new LabelId(1)], []);
        transaction.Commit();
    }

    private static async Task<Exception?[]> CommitConcurrently(
        GraphTransaction first,
        GraphTransaction second)
    {
        Task<Exception?> firstResult = Task.Run<Exception?>(() => Record.Exception(() => first.Commit()));
        Task<Exception?> secondResult = Task.Run<Exception?>(() => Record.Exception(() => second.Commit()));
        return await Task.WhenAll(firstResult, secondResult).WaitAsync(OperationTimeout);
    }

    private static void SignalAndWait(Barrier barrier)
    {
        if (!barrier.SignalAndWait(OperationTimeout))
            throw new TimeoutException("并发 Graph transaction 未在期限内到达提交屏障。");
    }

    private static T[] ReadAll<T>(GraphCursor<T> cursor) where T : class
    {
        var result = new List<T>();
        while (true)
        {
            IReadOnlyList<T> page = cursor.ReadNextPage();
            if (page.Count == 0)
                return [.. result];
            result.AddRange(page);
        }
    }
}
