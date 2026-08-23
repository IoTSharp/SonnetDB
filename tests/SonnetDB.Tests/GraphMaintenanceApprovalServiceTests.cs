using SonnetDB.Graphs;
using SonnetDB.Server.Graphs;
using Xunit;

namespace SonnetDB.Tests;

public sealed class GraphMaintenanceApprovalServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-graph-approval-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Approve_AfterExpiry_PersistsExpiredAuditEvent()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-23T00:00:00Z"));
        var store = new FileGraphMaintenanceAuditStore(_root);
        var service = new GraphMaintenanceApprovalService(store, time);
        GraphMaintenanceApprovalDto staged = service.Stage(
            "factory",
            "topology",
            new GraphMaintenanceStageRequest { Action = GraphMaintenanceAction.Checkpoint },
            "operator");

        time.Advance(TimeSpan.FromMinutes(11));

        GraphMaintenanceApprovalException exception = Assert.Throws<GraphMaintenanceApprovalException>(() =>
            service.Approve("factory", "topology", staged.ApprovalId, "approver", store: null!, CancellationToken.None));
        Assert.Equal("graph_maintenance_approval_expired", exception.Code);
        GraphMaintenanceApprovalDto expired = Assert.Single(
            service.List("factory", "topology", 10),
            entry => entry.State == "expired");
        Assert.True(expired.OccurredAtUtc > expired.ExpiresAtUtc);

        var reopened = new FileGraphMaintenanceAuditStore(_root);
        Assert.Equal("expired", reopened.GetLatest(staged.ApprovalId)?.State);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
