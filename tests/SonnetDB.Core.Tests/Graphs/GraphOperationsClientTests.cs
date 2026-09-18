using SonnetDB.Data.Graphs;
using SonnetDB.Graphs;
using Xunit;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class GraphOperationsClientTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-graph-operations-client-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EmbeddedOperations_ExposeSharedContractsAndPersistentApprovalAudit()
    {
        string connection = $"Data Source={_root};Mode=Embedded";
        using (var client = new SndbGraphClient(connection))
        {
            await client.CreateGraphAsync("plant");
            await client.UpsertVertexAsync(
                "plant",
                new GraphUpsertVertexRequest { Id = 1, RequestId = Guid.NewGuid(), Labels = [7] });

            GraphOperationsOverviewDto overview = await client.GetOperationsOverviewAsync("plant");
            Assert.Equal(1, overview.VertexCount);
            Assert.False(overview.Capabilities.SlowTraversalDiagnostics);
            Assert.Equal("not_available_embedded", overview.SlowTraversalSource);

            GraphVisualizationDto visualization = await client.GetVisualizationAsync("plant", limit: 10);
            Assert.False(visualization.Truncated);
            Assert.Equal(1, Assert.Single(visualization.Vertices).Id);

            GraphMaintenanceApprovalDto staged = await client.StageMaintenanceAsync(
                "plant",
                new GraphMaintenanceStageRequest { Action = GraphMaintenanceAction.Checkpoint });
            Assert.Equal("staged", staged.State);
            GraphMaintenanceApprovalDto completed = await client.ApproveMaintenanceAsync("plant", staged.ApprovalId);
            Assert.Equal("completed", completed.State);
            Assert.Contains(
                await client.ListMaintenanceAuditAsync("plant"),
                entry => entry.ApprovalId == staged.ApprovalId && entry.State == "completed");
        }

        using var reopened = new SndbGraphClient(connection);
        Assert.Contains(
            await reopened.ListMaintenanceAuditAsync("plant"),
            entry => entry.State == "completed");
        Assert.Equal(1, (await reopened.GetOperationsOverviewAsync("plant")).VertexCount);
    }

    [Fact]
    public async Task GraphClient_CancelledOperationsStopBeforeOpeningSnapshot()
    {
        using var client = new SndbGraphClient($"Data Source={_root}-cancel");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.ListGraphsAsync(cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (GraphExpansion _ in client.ExpandAsync(
                "missing",
                new GraphExpandRequest { VertexId = 1 },
                cancellation.Token))
            {
            }
        });
    }

    [Fact]
    public async Task GraphClient_PageAdaptersKeepResultsBoundedAndPreserveOrder()
    {
        using var client = new SndbGraphClient($"Data Source={_root}-pages");
        await client.CreateGraphAsync("plant");
        for (long id = 1; id <= 4; id++)
        {
            await client.UpsertVertexAsync(
                "plant",
                new GraphUpsertVertexRequest { Id = id, RequestId = Guid.NewGuid(), Labels = [7] });
        }
        for (long id = 1; id <= 3; id++)
        {
            await client.UpsertEdgeAsync(
                "plant",
                new GraphUpsertEdgeRequest
                {
                    Id = 100 + id,
                    RequestId = Guid.NewGuid(),
                    SourceId = 1,
                    TargetId = id + 1,
                    LabelId = 8,
                });
        }

        var pages = new List<IReadOnlyList<GraphExpansion>>();
        await foreach (IReadOnlyList<GraphExpansion> page in client.ExpandPagesAsync(
            "plant",
            new GraphExpandRequest { VertexId = 1, PageSize = 2 }))
        {
            pages.Add(page);
        }

        Assert.Equal(2, pages.Count);
        Assert.All(pages, page => Assert.InRange(page.Count, 1, 2));
        Assert.Equal([2L, 3L, 4L], pages.SelectMany(static page => page).Select(static item => item.NeighborId.Value).ToArray());
    }

    [Fact]
    public async Task GraphClient_ImportValidationReportsAllItemErrorsWithRequestId()
    {
        using var client = new SndbGraphClient($"Data Source={_root}-batch-errors");
        await client.CreateGraphAsync("plant");
        Guid requestId = Guid.NewGuid();

        SndbGraphBatchException exception = await Assert.ThrowsAsync<SndbGraphBatchException>(
            () => client.ImportAsync(
                "plant",
                new GraphImportRequest
                {
                    RequestId = requestId,
                    Vertices =
                    [
                        new GraphImportVertexDto { Id = 0 },
                        new GraphImportVertexDto { Id = 1 },
                        new GraphImportVertexDto { Id = 1 },
                    ],
                    Edges =
                    [
                        new GraphImportEdgeDto { Id = 10, SourceId = 0, TargetId = 1, LabelId = 2 },
                    ],
                }));

        Assert.Equal(requestId, exception.RequestId);
        Assert.True(exception.Errors.Count >= 3);
        Assert.Contains(exception.Errors, error => error.Code == "duplicate_id" && error.ElementId == 1);
        Assert.Contains(exception.Errors, error => error.Code == "invalid_item" && error.ElementKind == "edge");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
