using System.Text.Json;
using SonnetDB.Graphs;
using SonnetDB.Json;
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

    [Fact]
    public void Reopen_WithUnterminatedTail_RepairsOnlyTornRecord()
    {
        var store = new FileGraphMaintenanceAuditStore(_root);
        GraphMaintenanceApprovalDto staged = new GraphMaintenanceApprovalService(
            store,
            TimeProvider.System).Stage(
                "factory",
                "topology",
                new GraphMaintenanceStageRequest { Action = GraphMaintenanceAction.Checkpoint },
                "operator");
        string path = Path.Combine(_root, "graph-maintenance-audit.ndjson");
        long validLength = new FileInfo(path).Length;
        File.AppendAllText(path, "{\"ApprovalId\":\"");

        var reopened = new FileGraphMaintenanceAuditStore(_root);

        Assert.Equal("staged", reopened.GetLatest(staged.ApprovalId)?.State);
        Assert.Equal(validLength, new FileInfo(path).Length);
    }

    [Fact]
    public void Reopen_WithApplyingTail_RecordsInterruptedTerminalState()
    {
        var store = new FileGraphMaintenanceAuditStore(_root);
        GraphMaintenanceApprovalDto staged = new GraphMaintenanceApprovalService(
            store,
            TimeProvider.System).Stage(
                "factory",
                "topology",
                new GraphMaintenanceStageRequest { Action = GraphMaintenanceAction.Checkpoint },
                "operator");
        string path = Path.Combine(_root, "graph-maintenance-audit.ndjson");
        GraphMaintenanceApprovalDto applying = staged with
        {
            State = "applying",
            OccurredAtUtc = DateTimeOffset.UtcNow,
        };
        using var json = new MemoryStream();
        JsonSerializer.Serialize(json, applying, ServerJsonContext.Default.GraphMaintenanceApprovalDto);
        File.AppendAllText(path, System.Text.Encoding.UTF8.GetString(json.ToArray()) + Environment.NewLine);

        var reopened = new FileGraphMaintenanceAuditStore(_root);

        GraphMaintenanceApprovalDto? latest = reopened.GetLatest(staged.ApprovalId);
        Assert.NotNull(latest);
        Assert.Equal("interrupted", latest.State);
        Assert.Equal("graph_maintenance_interrupted", latest.ErrorCode);
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
