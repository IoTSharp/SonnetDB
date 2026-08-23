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

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
