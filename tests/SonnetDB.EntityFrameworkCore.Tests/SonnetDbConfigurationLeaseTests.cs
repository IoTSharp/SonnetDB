using System.Data;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.EntityFrameworkCore.Extensions;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

/// <summary>验证配置租约实际使用的 EF 谓词、setter 和事务会话影响行数。</summary>
public sealed class SonnetDbConfigurationLeaseTests
{
    /// <summary>支持边界时刻接管、按 owner 释放和跨上下文回滚读回。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task ConditionalUpdate_UsesActualSessionAndAffectedCounts(string protocol)
    {
        const string admin = "configuration-lease-test";
        string dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-ef-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        try
        {
            await using var app = EfTestServerHost.Build(new ServerOptions
            {
                DataRoot = dataRoot,
                Tokens = new Dictionary<string, string> { [admin] = ServerRoles.Admin },
            }, extraArgs: protocol == "frame-http2" ? ["--Kestrel:Endpoints:Http:Protocols=Http2"] : null);
            await app.StartAsync(token);
            try
            {
                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
                var address = new Uri(Assert.Single(addresses!.Addresses));
                var options = new DbContextOptionsBuilder<LeaseContext>()
                    .UseSonnetDB($"Data Source=sonnetdb+http://{address.Authority}/config_lease;Token={admin};Timeout=10;Protocol={protocol}")
                    .Options;
                var now = new DateTime(2026, 11, 27, 8, 0, 0, DateTimeKind.Utc).AddTicks(1234);
                var leaseId = Guid.NewGuid();
                var wrongOwner = Guid.NewGuid();
                Guid[] targets = [Guid.NewGuid(), Guid.NewGuid()];
                var deleted = Guid.NewGuid();

                await using var observer = new LeaseContext(options);
                await observer.Database.EnsureCreatedAsync(token);
                observer.AddRange(
                    new LeaseRow { GatewayId = targets[0], LeaseId = wrongOwner, ExpiresAt = now, Owner = "expired" },
                    new LeaseRow { GatewayId = targets[1] },
                    new LeaseRow { GatewayId = deleted, Deleted = true });
                await observer.SaveChangesAsync(token);

                await using var writer = new LeaseContext(options);
                await using (var rollback = await writer.Database.BeginTransactionAsync(IsolationLevel.Serializable, token))
                {
                    Assert.Equal(0, await AcquireAsync(writer, deleted, leaseId, now, token));
                    Assert.Equal(1, await AcquireAsync(writer, targets[0], leaseId, now, token));
                    Assert.Equal(0, await AcquireAsync(writer, targets[0], wrongOwner, now, token));
                    var staged = await writer.Rows.AsNoTracking().SingleAsync(row => row.GatewayId == targets[0], token);
                    Assert.Equal(leaseId, staged.LeaseId);
                    Assert.Equal(now.AddMinutes(5).Ticks, staged.ExpiresAt?.Ticks);
                    Assert.Equal("owner", staged.Owner);
                    Assert.Equal(wrongOwner, await ReadLeaseIdAsync(observer, targets[0], token));
                    Assert.Equal(0, await ReleaseAsync(writer, targets, wrongOwner, token));
                    await rollback.RollbackAsync(token);
                }
                Assert.Equal(wrongOwner, await ReadLeaseIdAsync(observer, targets[0], token));

                await using var commit = await writer.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
                Assert.Equal(1, await AcquireAsync(writer, targets[0], leaseId, now, token));
                Assert.Equal(1, await AcquireAsync(writer, targets[1], leaseId, now, token));
                Assert.Equal(2, await ReleaseAsync(writer, targets, leaseId, token));
                await commit.CommitAsync(token);
                var released = await observer.Rows.AsNoTracking().Where(row => targets.Contains(row.GatewayId))
                    .ToListAsync(token);
                Assert.Equal(2, released.Count);
                Assert.All(released, row =>
                {
                    Assert.Null(row.LeaseId);
                    Assert.Null(row.ExpiresAt);
                    Assert.Null(row.Owner);
                });
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await app.StopAsync(shutdown.Token);
            }
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static Task<int> AcquireAsync(LeaseContext context, Guid gatewayId, Guid leaseId, DateTime now,
        CancellationToken cancellationToken)
    {
        DateTime expiresAt = now.AddMinutes(5);
        return context.Rows.Where(row => row.GatewayId == gatewayId && !row.Deleted
                && (row.LeaseId == null || row.ExpiresAt == null || row.ExpiresAt <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.LeaseId, leaseId)
                .SetProperty(row => row.ExpiresAt, expiresAt)
                .SetProperty(row => row.Owner, "owner"), cancellationToken);
    }

    private static Task<int> ReleaseAsync(LeaseContext context, Guid[] targets, Guid leaseId,
        CancellationToken cancellationToken)
        => context.Rows.Where(row => targets.Contains(row.GatewayId) && row.LeaseId == leaseId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.LeaseId, (Guid?)null)
                .SetProperty(row => row.ExpiresAt, (DateTime?)null)
                .SetProperty(row => row.Owner, (string?)null), cancellationToken);

    private static Task<Guid?> ReadLeaseIdAsync(LeaseContext context, Guid gatewayId, CancellationToken cancellationToken)
        => context.Rows.Where(row => row.GatewayId == gatewayId).Select(row => row.LeaseId).SingleAsync(cancellationToken);

    private sealed class LeaseContext(DbContextOptions<LeaseContext> options) : DbContext(options)
    {
        public DbSet<LeaseRow> Rows => Set<LeaseRow>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<LeaseRow>().HasKey(row => row.GatewayId);
    }

    private sealed class LeaseRow
    {
        public Guid GatewayId { get; set; }
        public bool Deleted { get; set; }
        public Guid? LeaseId { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string? Owner { get; set; }
    }
}
