using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Hosting;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>验证关系表启动预热状态能够可靠控制 readiness。</summary>
public sealed class RelationalTableWarmupTests
{
    /// <summary>已有数据库中的全部关系表必须由启动服务主动冷开并计入完成状态。</summary>
    [Fact]
    public async Task Service_WithExistingDatabase_WarmsEveryRelationalTable()
    {
        string root = Path.Combine(Path.GetTempPath(), "sndb-server-warmup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var preparation = new TsdbRegistry(root))
            {
                Assert.True(preparation.TryCreate("production", out var database));
                database.Tables.Create(CreateSchema("weights"));
                database.Tables.Create(CreateSchema("captures"));
            }

            using var registry = new TsdbRegistry(root);
            registry.LoadExisting();
            var state = new RelationalTableWarmupState();
            var service = new RelationalTableWarmupService(
                registry,
                state,
                Options.Create(new ServerOptions { RelationalTableWarmupConcurrency = 2 }),
                NullLogger<RelationalTableWarmupService>.Instance);

            await service.StartAsync(CancellationToken.None);

            RelationalTableWarmupSnapshot snapshot = state.Read();
            Assert.Equal(RelationalTableWarmupPhase.Completed, snapshot.Phase);
            Assert.Equal(1, snapshot.DatabaseCount);
            Assert.Equal(2, snapshot.TableCount);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>预热未开始时必须阻断 readiness，不能提前接入业务流量。</summary>
    [Fact]
    public async Task HealthCheck_BeforeWarmup_ReturnsUnhealthy()
    {
        var state = new RelationalTableWarmupState();
        var healthCheck = new RelationalTableWarmupHealthCheck(state);

        HealthCheckResult result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("关系表启动预热尚未开始。", result.Description);
    }

    /// <summary>预热完成后 readiness 必须公开数据库和关系表数量。</summary>
    [Fact]
    public async Task HealthCheck_AfterWarmup_ReturnsHealthyCounts()
    {
        var state = new RelationalTableWarmupState();
        state.MarkCompleted(databaseCount: 1, tableCount: 7);
        var healthCheck = new RelationalTableWarmupHealthCheck(state);

        HealthCheckResult result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("已预热 1 个数据库中的 7 张关系表。", result.Description);
    }

    /// <summary>任一关系表冷开失败时必须保留原因并阻断 readiness。</summary>
    [Fact]
    public async Task HealthCheck_AfterWarmupFailure_ReturnsUnhealthyReason()
    {
        var state = new RelationalTableWarmupState();
        state.MarkFailed(new IOException("索引恢复失败"));
        var healthCheck = new RelationalTableWarmupHealthCheck(state);

        HealthCheckResult result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("关系表启动预热失败：索引恢复失败", result.Description);
    }

    /// <summary>创建用于启动预热验证的最小关系表 schema。</summary>
    private static TableSchema CreateSchema(string name)
        => TableSchema.Create(
            name,
            [("id", TableColumnType.Int64, false), ("value", TableColumnType.String, false)],
            ["id"]);
}
