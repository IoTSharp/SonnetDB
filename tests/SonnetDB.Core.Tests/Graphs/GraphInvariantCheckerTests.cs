using SonnetDB.Graphs;
using SonnetDB.Graphs.Storage;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class GraphInvariantCheckerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-graph-invariant-tests",
        Guid.NewGuid().ToString("N"));

    public GraphInvariantCheckerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Check_AtomicEdgeFixture_ReturnsCompleteValidReport()
    {
        using var manager = CreateManager("valid");
        GraphStore store = manager.Create("social");
        WriteFixture(store);

        GraphInvariantReport report = GraphInvariantChecker.Check(store);

        Assert.True(report.IsComplete);
        Assert.True(report.IsValid, FormatIssues(report));
        Assert.Equal(2, report.VertexCount);
        Assert.Equal(1, report.EdgeCount);
        Assert.Equal(1, report.OutgoingAdjacencyCount);
        Assert.Equal(1, report.IncomingAdjacencyCount);
        Assert.Equal(3, report.LabelIndexCount);
        Assert.Equal(3, report.PropertyIndexCount);
        Assert.Equal(2, report.HighWater.VertexId);
        Assert.Equal(10, report.HighWater.EdgeId);
        Assert.Equal(3, report.HighWater.LabelId);
        Assert.Equal(3, report.HighWater.PropertyId);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Check_OrphanAndMismatchedProjection_ReportsEachInvariant()
    {
        using var manager = CreateManager("projection");
        GraphStore store = manager.Create("topology");
        WriteFixture(store);

        Assert.True(store.Keyspace.Delete(GraphKeyCodec.EncodeIncomingAdjacency(
            new GraphElementId(2),
            new LabelId(3),
            new GraphElementId(1),
            new GraphElementId(10))));
        Assert.True(store.Keyspace.Delete(GraphKeyCodec.EncodePropertyIndex(
            GraphElementKind.Edge,
            new LabelId(3),
            3,
            GraphPropertyValue.FromString("calls"),
            new GraphElementId(10))));
        store.Keyspace.Put(
            GraphKeyCodec.EncodeOutgoingAdjacency(
                new GraphElementId(1),
                new LabelId(3),
                new GraphElementId(2),
                new GraphElementId(99)),
            []);
        store.Keyspace.Put(
            GraphKeyCodec.EncodePropertyIndex(
                GraphElementKind.Edge,
                new LabelId(3),
                3,
                GraphPropertyValue.FromString("wrong"),
                new GraphElementId(10)),
            []);

        GraphInvariantReport report = GraphInvariantChecker.Check(store);

        Assert.False(report.IsValid);
        Assert.True(report.IsComplete);
        Assert.Contains(report.Issues, static issue =>
            issue.Kind == GraphInvariantIssueKind.MissingIncomingAdjacency);
        Assert.Contains(report.Issues, static issue =>
            issue.Kind == GraphInvariantIssueKind.OrphanOutgoingAdjacency);
        Assert.Contains(report.Issues, static issue =>
            issue.Kind == GraphInvariantIssueKind.MissingPropertyIndex);
        Assert.Contains(report.Issues, static issue =>
            issue.Kind == GraphInvariantIssueKind.OrphanPropertyIndex);
    }

    [Fact]
    public void Check_MissingEndpointAndStaleHighWater_ReportsCorruption()
    {
        using var manager = CreateManager("endpoint");
        GraphStore store = manager.Create("evidence");
        WriteFixture(store);

        Assert.True(store.Keyspace.Delete(GraphKeyCodec.EncodeVertexRecord(new GraphElementId(2))));
        store.Keyspace.Put(
            GraphKeyCodec.EncodeMetadata((byte)GraphHighWaterKind.EdgeId),
            GraphHighWaterCodec.Encode(GraphHighWaterKind.EdgeId, 5));

        GraphInvariantReport report = GraphInvariantChecker.Check(store);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, static issue =>
            issue.Kind == GraphInvariantIssueKind.MissingVertexEndpoint);
        Assert.Contains(report.Issues, static issue =>
            issue.Kind == GraphInvariantIssueKind.HighWaterBehind);
        Assert.Contains(report.Issues, static issue =>
            issue.Kind == GraphInvariantIssueKind.OrphanLabelIndex);
    }

    [Fact]
    public void Check_CorruptKeyAndRecord_ReportsBoundedDiagnostics()
    {
        using var manager = CreateManager("corrupt");
        GraphStore store = manager.Create("corrupt");
        WriteFixture(store);

        byte[] corruptKey = GraphKeyCodec.EncodeVertexRecord(new GraphElementId(20));
        corruptKey[^1] ^= 0x7F;
        store.Keyspace.Put(corruptKey, [0x01]);
        store.Keyspace.Put(
            GraphKeyCodec.EncodeEdgeRecord(new GraphElementId(30)),
            [0x01, 0x02, 0x03]);

        GraphInvariantReport report = GraphInvariantChecker.Check(store, new GraphInvariantCheckOptions
        {
            MaxIssues = 2,
        });

        Assert.False(report.IsValid);
        Assert.Equal(2, report.Issues.Count);
        Assert.True(report.TotalIssueCount >= report.Issues.Count);
        Assert.Equal(report.TotalIssueCount - report.Issues.Count, report.SuppressedIssueCount);
        Assert.All(report.Issues, static issue => Assert.True(issue.Key.Length <= 80));
        Assert.Contains(report.Issues, static issue => issue.Kind == GraphInvariantIssueKind.MalformedKey);
        Assert.Contains(report.Issues, static issue => issue.Kind == GraphInvariantIssueKind.MalformedRecord);
    }

    [Fact]
    public void Check_NonEmptyProjectionAndCorruptRequestMarker_ReportsMalformedRecord()
    {
        using var manager = CreateManager("projection-value");
        GraphStore store = manager.Create("projection-value");
        WriteFixture(store);

        store.Keyspace.Put(
            GraphKeyCodec.EncodeLabelMembership(
                GraphElementKind.Vertex,
                new LabelId(1),
                new GraphElementId(1)),
            [0x01]);
        store.Keyspace.Put(
            GraphKeyCodec.EncodeTransactionRequest(Guid.Parse("34600000-0000-0000-0000-000000000002")),
            [0x01, 0x02]);

        GraphInvariantReport report = GraphInvariantChecker.Check(store, new GraphInvariantCheckOptions
        {
            MaxIssues = 20,
        });

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, static issue =>
            issue.Kind == GraphInvariantIssueKind.MalformedRecord
            && issue.Message.Contains("projection key", StringComparison.Ordinal));
        Assert.Contains(report.Issues, static issue =>
            issue.Kind == GraphInvariantIssueKind.MalformedRecord
            && issue.Message.Contains("request marker", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_ScanLimit_ReturnsIncompleteInvalidReport()
    {
        using var manager = CreateManager("limit");
        GraphStore store = manager.Create("bounded");
        WriteFixture(store);

        GraphInvariantReport report = GraphInvariantChecker.Check(store, new GraphInvariantCheckOptions
        {
            PageSize = 2,
            MaxScannedEntries = 1,
            MaxIssues = 1,
        });

        Assert.False(report.IsComplete);
        Assert.False(report.IsValid);
        Assert.Equal(1, report.ScannedEntries);
        GraphInvariantIssue issue = Assert.Single(report.Issues);
        Assert.Equal(GraphInvariantIssueKind.ScanLimitExceeded, issue.Kind);
    }

    [Fact]
    public void Check_PointLookupLimit_ReturnsIncompleteInvalidReport()
    {
        using var manager = CreateManager("lookup-limit");
        GraphStore store = manager.Create("bounded");
        WriteFixture(store);

        GraphInvariantReport report = GraphInvariantChecker.Check(store, new GraphInvariantCheckOptions
        {
            MaxPointLookups = 1,
            MaxIssues = 1,
        });

        Assert.False(report.IsComplete);
        Assert.False(report.IsValid);
        Assert.Equal(1, report.PointLookupCount);
        GraphInvariantIssue issue = Assert.Single(report.Issues);
        Assert.Equal(GraphInvariantIssueKind.PointLookupLimitExceeded, issue.Kind);
    }

    [Fact]
    public void Check_DefaultBudgets_CoverMoreThanLegacyTenMillionEntryLimit()
    {
        const long GateVertexCount = 1_000_000;
        const long GateEdgeCount = 10_000_000;
        const long EstimatedGateEntries = (GateVertexCount * 3) + (GateEdgeCount * 5);
        const long EstimatedGatePointLookups = (GateVertexCount * 4) + (GateEdgeCount * 10);
        var options = new GraphInvariantCheckOptions();

        Assert.True(options.MaxScannedEntries > 10_000_000);
        Assert.True(options.MaxScannedEntries >= EstimatedGateEntries);
        Assert.True(options.MaxPointLookups >= EstimatedGatePointLookups);
    }

    [Fact]
    public void Check_UniquePropertyOrphanAndCollision_ReportsBoth()
    {
        using var manager = CreateManager("unique");
        GraphStore store = manager.Create("unique");
        WriteFixture(store);
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertVertex(
            new GraphElementId(3),
            expectedElementVersion: 0,
            [new LabelId(1)],
            [new GraphProperty(1, GraphPropertyValue.FromString("source"))]);
        transaction.Commit();
        store.Keyspace.Put(
            GraphKeyCodec.EncodeUniqueProperty(
                GraphElementKind.Vertex,
                new LabelId(1),
                1,
                GraphPropertyValue.FromString("source")),
            GraphUniquePropertyOwnerCodec.Encode(
                GraphElementKind.Vertex,
                new GraphElementId(2)));
        store.Keyspace.Put(
            GraphKeyCodec.EncodeUniqueProperty(
                GraphElementKind.Vertex,
                new LabelId(1),
                1,
                GraphPropertyValue.FromString("missing")),
            GraphUniquePropertyOwnerCodec.Encode(
                GraphElementKind.Vertex,
                new GraphElementId(1)));

        GraphInvariantReport report = GraphInvariantChecker.Check(store);

        Assert.False(report.IsValid);
        Assert.True(report.IsComplete);
        Assert.Contains(report.Issues, static issue =>
            issue.Kind == GraphInvariantIssueKind.OrphanUniquePropertyIndex);
        Assert.Contains(report.Issues, static issue =>
            issue.Kind == GraphInvariantIssueKind.UniquePropertyCollision);
        Assert.Contains(report.Issues, static issue =>
            issue.Kind == GraphInvariantIssueKind.OrphanUniquePropertyIndex
            && issue.Message.Contains("owner=2", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_UniquePropertyWithMalformedOrWrongOwner_ReportsCorruption()
    {
        using var manager = CreateManager("unique-owner");
        GraphStore store = manager.Create("unique-owner");
        WriteFixture(store);
        store.Keyspace.Put(
            GraphKeyCodec.EncodeUniqueProperty(
                GraphElementKind.Vertex,
                new LabelId(2),
                2,
                GraphPropertyValue.FromString("target")),
            GraphUniquePropertyOwnerCodec.Encode(
                GraphElementKind.Vertex,
                new GraphElementId(1)));
        store.Keyspace.Put(
            GraphKeyCodec.EncodeUniqueProperty(
                GraphElementKind.Vertex,
                new LabelId(1),
                1,
                GraphPropertyValue.FromString("malformed")),
            []);

        GraphInvariantReport report = GraphInvariantChecker.Check(store);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, static issue =>
            issue.Kind == GraphInvariantIssueKind.OrphanUniquePropertyIndex
            && issue.Message.Contains("owner=1", StringComparison.Ordinal));
        Assert.Contains(report.Issues, static issue =>
            issue.Kind == GraphInvariantIssueKind.MalformedRecord
            && issue.Message.Contains("unique property owner", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_ModerateGraphWithSmallPages_ReturnsCompleteValidReport()
    {
        const int VertexCount = 2_000;
        const int BatchSize = 200;
        using var manager = CreateManager("moderate");
        GraphStore store = manager.Create("moderate");
        for (int start = 1; start <= VertexCount; start += BatchSize)
        {
            GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
            for (int id = start; id < start + BatchSize; id++)
            {
                transaction.UpsertVertex(
                    new GraphElementId(id),
                    expectedElementVersion: 0,
                    [new LabelId(1)],
                    [new GraphProperty(1, GraphPropertyValue.FromInt64(id))]);
            }
            transaction.Commit();
        }
        store.Keyspace.Compact();

        GraphInvariantReport report = GraphInvariantChecker.Check(store, new GraphInvariantCheckOptions
        {
            PageSize = 8,
        });

        Assert.True(report.IsComplete);
        Assert.True(report.IsValid, FormatIssues(report));
        Assert.Equal(VertexCount, report.VertexCount);
        Assert.True(report.ScannedEntries > 8);
        Assert.Equal(VertexCount * 4L, report.PointLookupCount);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private GraphManager CreateManager(string name)
        => new(Path.Combine(_root, name), Options());

    private static void WriteFixture(GraphStore store)
    {
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertVertex(
            new GraphElementId(1),
            expectedElementVersion: 0,
            [new LabelId(1)],
            [new GraphProperty(1, GraphPropertyValue.FromString("source"))]);
        transaction.UpsertVertex(
            new GraphElementId(2),
            expectedElementVersion: 0,
            [new LabelId(2)],
            [new GraphProperty(2, GraphPropertyValue.FromString("target"))]);
        transaction.UpsertEdge(
            new GraphElementId(10),
            expectedElementVersion: 0,
            new GraphElementId(1),
            new GraphElementId(2),
            new LabelId(3),
            [new GraphProperty(3, GraphPropertyValue.FromString("calls"))]);
        GraphCommitResult result = transaction.Commit();
        Assert.False(result.IsDuplicate);
    }

    private static KvOptions Options()
        => KvOptions.Default with
        {
            AutoCheckpointEnabled = false,
            SyncWalOnEveryWrite = true,
            ExpirerEnabled = false,
            CleanupEnabled = false,
        };

    private static string FormatIssues(GraphInvariantReport report)
        => string.Join(Environment.NewLine, report.Issues.Select(static issue => issue.Message));
}
