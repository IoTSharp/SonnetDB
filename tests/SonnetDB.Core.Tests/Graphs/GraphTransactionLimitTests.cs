using SonnetDB.Graphs;
using SonnetDB.Graphs.Storage;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class GraphTransactionLimitTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-graph-transaction-limit-tests",
        Guid.NewGuid().ToString("N"));
    private GraphManager? _manager;

    public GraphTransactionLimitTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Commit_EncodedPlanExceedsByteLimit_FailsBeforeWalAppend()
    {
        GraphStore store = CreateStore("encoded-plan-limit");
        var vertexId = new GraphElementId(1);
        LabelId[] labels = [new LabelId(1)];
        GraphPropertyEntry[] properties =
        [
            new GraphPropertyEntry(1, GraphPropertyValue.FromString("bounded")),
        ];
        byte[] encodedRecord = GraphElementRecordCodec.EncodeVertex(
            new GraphVertexRecord(vertexId, 1, labels, properties));
        Guid requestId = Guid.NewGuid();
        long initialWalLength = store.Keyspace.ActiveWalLength;
        long initialSequence = store.Keyspace.LastSequence;
        GraphTransaction transaction = store.BeginTransaction(
            requestId,
            new GraphTransactionLimits
            {
                MaxKvMutations = 128,
                MaxEncodedBytes = encodedRecord.Length,
            });

        transaction.UpsertVertex(vertexId, 0, labels, properties);
        Assert.Throws<GraphTransactionLimitExceededException>(() => transaction.Commit());

        Assert.Equal(initialWalLength, store.Keyspace.ActiveWalLength);
        Assert.Equal(initialSequence, store.Keyspace.LastSequence);
        Assert.Null(store.Keyspace.Get(GraphKeyCodec.EncodeVertexRecord(vertexId)));
        Assert.Null(store.Keyspace.Get(GraphKeyCodec.EncodeTransactionRequest(requestId)));
    }

    [Fact]
    public void UpsertVertex_InfiniteProperties_StopsAtConstructionLimit()
    {
        GraphStore store = CreateStore("infinite-properties");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        int enumerated = 0;

        Assert.Throws<ArgumentOutOfRangeException>(() => transaction.UpsertVertex(
            new GraphElementId(1),
            0,
            [],
            InfiniteProperties(() => enumerated++)));

        Assert.Equal(16_385, enumerated);
    }

    public void Dispose()
    {
        _manager?.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private GraphStore CreateStore(string name)
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
        return _manager.Create(name);
    }

    private static IEnumerable<GraphPropertyEntry> InfiniteProperties(Action onEnumerated)
    {
        int propertyId = 1;
        while (true)
        {
            onEnumerated();
            yield return new GraphPropertyEntry(
                propertyId,
                GraphPropertyValue.FromInt64(propertyId));
            propertyId++;
        }
    }
}
