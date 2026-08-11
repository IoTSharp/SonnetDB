using SonnetDB.Graphs;
using SonnetDB.Graphs.Storage;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class GraphTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-graph-transaction-tests",
        Guid.NewGuid().ToString("N"));
    private readonly List<GraphManager> _managers = [];

    public GraphTransactionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Commit_EdgeFixture_PublishesRecordAdjacencyIndexesAndHighWaterAtOneSequence()
    {
        GraphStore store = CreateStore("atomic-edge");
        CreateEndpoints(store);
        var edgeId = new GraphElementId(10);
        var sourceId = new GraphElementId(1);
        var targetId = new GraphElementId(2);
        var labelId = new LabelId(7);
        var property = new GraphProperty(9, GraphPropertyValue.FromString("calls"));

        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertEdge(edgeId, 0, sourceId, targetId, labelId, [property]);
        GraphCommitResult result = transaction.Commit();

        byte[][] keys =
        [
            GraphKeyCodec.EncodeEdgeRecord(edgeId),
            GraphKeyCodec.EncodeOutgoingAdjacency(sourceId, labelId, targetId, edgeId),
            GraphKeyCodec.EncodeIncomingAdjacency(targetId, labelId, sourceId, edgeId),
            GraphKeyCodec.EncodeLabelMembership(GraphElementKind.Edge, labelId, edgeId),
            GraphKeyCodec.EncodePropertyIndex(
                GraphElementKind.Edge,
                labelId,
                property.PropertyId,
                property.Value,
                edgeId),
        ];
        foreach (byte[] key in keys)
        {
            KvEntry entry = Assert.IsType<KvEntry>(store.Keyspace.GetEntry(key));
            Assert.Equal(result.Sequence, entry.Version);
        }

        GraphEdgeRecord edge = GraphElementRecordCodec.DecodeEdge(store.Keyspace.Get(keys[0])!);
        Assert.Equal((sourceId, targetId, edgeId, labelId),
            (edge.SourceId, edge.TargetId, edge.Id, edge.LabelId));
        Assert.Equal(2, ReadHighWater(store, GraphHighWaterKind.VertexId));
        Assert.Equal(10, ReadHighWater(store, GraphHighWaterKind.EdgeId));
        Assert.Equal(7, ReadHighWater(store, GraphHighWaterKind.LabelId));
        Assert.Equal(9, ReadHighWater(store, GraphHighWaterKind.PropertyId));
    }

    [Fact]
    public async Task Commit_ConcurrentElementVersion_ExactlyOneUpdateWins()
    {
        GraphStore store = CreateStore("concurrent-version");
        GraphTransaction create = store.BeginTransaction(Guid.NewGuid());
        create.UpsertVertex(new GraphElementId(1), 0, [new LabelId(1)], []);
        create.Commit();

        using var barrier = new Barrier(2);
        store.BeforeTransactionConditionalCommitTestHook = () => barrier.SignalAndWait();
        GraphTransaction first = store.BeginTransaction(Guid.NewGuid());
        first.UpsertVertex(
            new GraphElementId(1),
            1,
            [new LabelId(1)],
            [new GraphProperty(1, GraphPropertyValue.FromString("first"))]);
        GraphTransaction second = store.BeginTransaction(Guid.NewGuid());
        second.UpsertVertex(
            new GraphElementId(1),
            1,
            [new LabelId(1)],
            [new GraphProperty(1, GraphPropertyValue.FromString("second"))]);

        Task<Exception?> firstResult = Task.Run<Exception?>(() => Record.Exception(() => first.Commit()));
        Task<Exception?> secondResult = Task.Run<Exception?>(() => Record.Exception(() => second.Commit()));
        Exception?[] exceptions = await Task.WhenAll(firstResult, secondResult);
        store.BeforeTransactionConditionalCommitTestHook = null;

        Assert.Single(exceptions, static exception => exception is null);
        Assert.Single(exceptions, static exception => exception is GraphConcurrencyException);
        GraphVertexRecord persisted = GraphElementRecordCodec.DecodeVertex(
            store.Keyspace.Get(GraphKeyCodec.EncodeVertexRecord(new GraphElementId(1)))!);
        Assert.Equal(2, persisted.ElementVersion);
    }

    [Fact]
    public void Commit_DuplicateRequest_ReturnsOriginalSequenceAndRejectsDifferentContent()
    {
        GraphStore store = CreateStore("duplicate-request");
        Guid requestId = Guid.NewGuid();
        GraphTransaction first = store.BeginTransaction(requestId);
        first.UpsertVertex(new GraphElementId(1), 0, [new LabelId(1)], []);
        GraphCommitResult original = first.Commit();

        GraphTransaction retry = store.BeginTransaction(requestId);
        retry.UpsertVertex(new GraphElementId(1), 0, [new LabelId(1)], []);
        GraphCommitResult duplicate = retry.Commit();

        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(original.Sequence, duplicate.Sequence);
        GraphTransaction conflicting = store.BeginTransaction(requestId);
        conflicting.UpsertVertex(new GraphElementId(1), 0, [new LabelId(2)], []);
        Assert.Throws<GraphRequestConflictException>(() => conflicting.Commit());
    }

    [Fact]
    public void Commit_CancellationAndExplicitLimit_FailBeforeWalAppend()
    {
        GraphStore store = CreateStore("bounded-cancel");
        long initialWalLength = store.Keyspace.ActiveWalLength;
        using var cancellation = new CancellationTokenSource();
        GraphTransaction canceled = store.BeginTransaction(Guid.NewGuid());
        canceled.UpsertVertex(new GraphElementId(1), 0, [new LabelId(1)], []);
        store.BeforeTransactionConditionalCommitTestHook = cancellation.Cancel;

        Assert.Throws<OperationCanceledException>(() => canceled.Commit(cancellation.Token));
        store.BeforeTransactionConditionalCommitTestHook = null;
        Assert.Equal(initialWalLength, store.Keyspace.ActiveWalLength);
        Assert.Null(store.Keyspace.Get(GraphKeyCodec.EncodeVertexRecord(new GraphElementId(1))));

        GraphTransaction limited = store.BeginTransaction(
            Guid.NewGuid(),
            new GraphTransactionLimits { MaxKvMutations = 1, MaxEncodedBytes = 1024 * 1024 });
        limited.UpsertVertex(new GraphElementId(2), 0, [new LabelId(1)], []);
        Assert.Throws<GraphTransactionLimitExceededException>(() =>
            limited.UpsertVertex(new GraphElementId(3), 0, [new LabelId(1)], []));
        Assert.Throws<GraphTransactionLimitExceededException>(() => limited.Commit());
        Assert.Equal(initialWalLength, store.Keyspace.ActiveWalLength);
    }

    [Fact]
    public void UpsertVertex_InfiniteLabels_StopsAtConstructionLimit()
    {
        GraphStore store = CreateStore("bounded-enumerable");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());

        Assert.Throws<ArgumentOutOfRangeException>(() => transaction.UpsertVertex(
            new GraphElementId(1),
            0,
            InfiniteLabels(),
            []));
    }

    [Fact]
    public void DeleteVertex_WithIncidentEdge_RestrictsUntilEdgeIsDeleted()
    {
        GraphStore store = CreateStore("delete-restrict");
        CreateEndpoints(store);
        var edgeId = new GraphElementId(3);
        var sourceId = new GraphElementId(1);
        var targetId = new GraphElementId(2);
        var labelId = new LabelId(2);
        GraphTransaction createEdge = store.BeginTransaction(Guid.NewGuid());
        createEdge.UpsertEdge(edgeId, 0, sourceId, targetId, labelId, []);
        createEdge.Commit();

        GraphTransaction restricted = store.BeginTransaction(Guid.NewGuid());
        restricted.DeleteVertex(sourceId, 1);
        Assert.Throws<GraphVertexDeleteRestrictedException>(() => restricted.Commit());
        Assert.NotNull(store.Keyspace.Get(GraphKeyCodec.EncodeVertexRecord(sourceId)));
        Assert.NotNull(store.Keyspace.Get(GraphKeyCodec.EncodeEdgeRecord(edgeId)));

        GraphTransaction deleteEdge = store.BeginTransaction(Guid.NewGuid());
        deleteEdge.DeleteEdge(edgeId, 1);
        deleteEdge.Commit();
        GraphTransaction deleteVertex = store.BeginTransaction(Guid.NewGuid());
        deleteVertex.DeleteVertex(sourceId, 1);
        deleteVertex.Commit();

        Assert.Null(store.Keyspace.Get(GraphKeyCodec.EncodeVertexRecord(sourceId)));
        Assert.Null(store.Keyspace.Get(GraphKeyCodec.EncodeEdgeRecord(edgeId)));
    }

    [Fact]
    public void Commit_WalSyncFailure_ReopenAndRetryObservesWholeEdgeBatch()
    {
        GraphManager manager = CreateManager("unknown", syncWal: true);
        GraphStore store = manager.Create("unknown");
        CreateEndpoints(store);
        Guid requestId = Guid.NewGuid();
        var edgeId = new GraphElementId(4);
        var sourceId = new GraphElementId(1);
        var targetId = new GraphElementId(2);
        var labelId = new LabelId(5);
        GraphTransaction transaction = store.BeginTransaction(requestId);
        transaction.UpsertEdge(
            edgeId,
            0,
            sourceId,
            targetId,
            labelId,
            [new GraphProperty(8, GraphPropertyValue.FromInt64(42))]);
        store.Keyspace.WalSyncTestHook = static () =>
            throw new InvalidOperationException("simulated fsync failure");

        GraphCommitOutcomeUnknownException exception = Assert.Throws<GraphCommitOutcomeUnknownException>(
            () => transaction.Commit());
        Assert.Equal(requestId, exception.RequestId);
        store.Keyspace.WalSyncTestHook = null;

        store = manager.Reopen("unknown");
        GraphTransaction retry = store.BeginTransaction(requestId);
        retry.UpsertEdge(
            edgeId,
            0,
            sourceId,
            targetId,
            labelId,
            [new GraphProperty(8, GraphPropertyValue.FromInt64(42))]);
        GraphCommitResult resolved = retry.Commit();

        Assert.True(resolved.IsDuplicate);
        Assert.NotNull(store.Keyspace.Get(GraphKeyCodec.EncodeEdgeRecord(edgeId)));
        Assert.NotNull(store.Keyspace.Get(
            GraphKeyCodec.EncodeOutgoingAdjacency(sourceId, labelId, targetId, edgeId)));
        Assert.NotNull(store.Keyspace.Get(
            GraphKeyCodec.EncodeIncomingAdjacency(targetId, labelId, sourceId, edgeId)));
        Assert.NotNull(store.Keyspace.Get(
            GraphKeyCodec.EncodeLabelMembership(GraphElementKind.Edge, labelId, edgeId)));
        Assert.NotNull(store.Keyspace.Get(GraphKeyCodec.EncodePropertyIndex(
            GraphElementKind.Edge,
            labelId,
            8,
            GraphPropertyValue.FromInt64(42),
            edgeId)));
    }

    public void Dispose()
    {
        foreach (GraphManager manager in _managers)
            manager.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private GraphStore CreateStore(string name)
        => CreateManager(name, syncWal: false).Create(name);

    private GraphManager CreateManager(string name, bool syncWal)
    {
        var manager = new GraphManager(
            Path.Combine(_root, name),
            KvOptions.Default with
            {
                AutoCheckpointEnabled = false,
                SyncWalOnEveryWrite = syncWal,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            });
        _managers.Add(manager);
        return manager;
    }

    private static void CreateEndpoints(GraphStore store)
    {
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertVertex(new GraphElementId(1), 0, [new LabelId(1)], []);
        transaction.UpsertVertex(new GraphElementId(2), 0, [new LabelId(1)], []);
        transaction.Commit();
    }

    private static long ReadHighWater(GraphStore store, GraphHighWaterKind kind)
        => GraphHighWaterCodec.Decode(
            store.Keyspace.Get(GraphKeyCodec.EncodeMetadata((byte)kind))!,
            kind);

    private static IEnumerable<LabelId> InfiniteLabels()
    {
        int value = 1;
        while (true)
            yield return new LabelId(value++);
    }
}
