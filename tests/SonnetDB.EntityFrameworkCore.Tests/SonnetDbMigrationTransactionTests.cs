using System.Data.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.EntityFrameworkCore.Extensions;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

/// <summary>真实 EF 迁移的关系 DDL、历史记录与失败回滚合同。</summary>
public sealed class SonnetDbMigrationTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-ef-issue392-" + Guid.NewGuid().ToString("N"));

    /// <summary>建立本用例独占的数据库目录。</summary>
    public SonnetDbMigrationTransactionTests() => Directory.CreateDirectory(_root);

    /// <summary>仅回收本用例建立的临时数据库。</summary>
    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>历史行写入失败也必须撤销此前的整个迁移，并可在同一连接修正后重试。</summary>
    [Fact]
    public async Task Migrate_WithHistoryInsertFailure_RollsBackDdlAndAllowsRetry()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var options = new DbContextOptionsBuilder<AtomicMigrationContext>()
            .UseSonnetDB("Data Source=" + Path.Combine(_root, "history-failure"))
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new AtomicMigrationContext(options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(AtomicMigrationInitial.Id, deadline.Token);
        await context.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"__EFMigrationsHistory\" ADD CONSTRAINT \"CK_BlockMigration\" CHECK (\"MigrationId\" <> '"
            + AtomicMigrationEvolve.Id + "')", deadline.Token);

        var failure = await Assert.ThrowsAnyAsync<Exception>(
            () => migrator.MigrateAsync(AtomicMigrationEvolve.Id, deadline.Token));
        Assert.Contains("CK_BlockMigration", failure.Message, StringComparison.Ordinal);
        Assert.Equal([AtomicMigrationInitial.Id],
            (await context.Database.GetAppliedMigrationsAsync(deadline.Token)).ToArray());
        Assert.DoesNotContain("Note", await ReadFirstColumnAsync(context, "DESCRIBE TABLE \"Nodes\"", deadline.Token));
        Assert.DoesNotContain("Labels", await ReadFirstColumnAsync(context, "SHOW TABLES", deadline.Token));
        Assert.Empty(await ReadFirstColumnAsync(context, "SHOW INDEXES ON \"Nodes\"", deadline.Token));

        await context.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"__EFMigrationsHistory\" DROP CONSTRAINT \"CK_BlockMigration\"", deadline.Token);
        await migrator.MigrateAsync(AtomicMigrationEvolve.Id, deadline.Token);
        Assert.Equal([AtomicMigrationInitial.Id, AtomicMigrationEvolve.Id],
            (await context.Database.GetAppliedMigrationsAsync(deadline.Token)).ToArray());
        Assert.Contains("Note", await ReadFirstColumnAsync(context, "DESCRIBE TABLE \"Nodes\"", deadline.Token));
        Assert.Equal("标签", await ScalarAsync(context, "SELECT \"Name\" FROM \"Labels\" WHERE \"Id\" = 1", deadline.Token));
    }

    /// <summary>失败迁移回滚所有 DDL 与历史行，成功迁移及现有约束在两次重开后仍有效。</summary>
    /// <param name="protocol">嵌入式或真实远程连接协议。</param>
    /// <param name="useAsync">是否调用异步迁移入口。</param>
    [Theory]
    [InlineData("embedded", false)]
    [InlineData("embedded", true)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", true)]
    public async Task Migrate_WithLaterDdlFailure_RollsBackSchemaAndHistoryAcrossReopen(string protocol, bool useAsync)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        WebApplication? app = null;
        const string token = "issue392-admin";
        string connectionString = "Data Source=" + Path.Combine(_root, "embedded");

        async Task StartServerAsync()
        {
            app = EfTestServerHost.Build(new ServerOptions
            {
                DataRoot = Path.Combine(_root, "server"),
                AutoLoadExistingDatabases = true,
                Tokens = new Dictionary<string, string> { [token] = ServerRoles.Admin },
            }, extraArgs: protocol == "frame-http2" ? ["--Kestrel:Endpoints:Http:Protocols=Http2"] : null);
            await app.StartAsync(deadline.Token);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
            var address = new Uri(Assert.Single(addresses.Addresses));
            connectionString = $"Data Source=sonnetdb+http://{address.Authority}/migration;Token={token};Timeout=60;Protocol={protocol}";
        }

        DbContextOptions<AtomicMigrationContext> CreateOptions()
            => new DbContextOptionsBuilder<AtomicMigrationContext>()
                .UseSonnetDB(connectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;

        async Task MigrateAsync(AtomicMigrationContext context, string target)
        {
            if (useAsync)
                await context.GetService<IMigrator>().MigrateAsync(target, deadline.Token);
            else
                context.GetService<IMigrator>().Migrate(target);
        }

        try
        {
            if (protocol != "embedded")
                await StartServerAsync();

            await using (var context = new AtomicMigrationContext(CreateOptions()))
            {
                context.Database.SetCommandTimeout(60);
                await MigrateAsync(context, AtomicMigrationInitial.Id);
                await context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"Nodes\" (\"Id\", \"ParentId\", \"Name\") VALUES (1, 1, '重复'), (2, 1, '重复')", deadline.Token);
                await context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"Children\" (\"Id\", \"NodeId\") VALUES (10, 2)", deadline.Token);
                await MigrateAsync(context, AtomicMigrationEvolve.Id);

                var failure = await Assert.ThrowsAnyAsync<Exception>(() => MigrateAsync(context, AtomicMigrationFail.Id));
                Assert.Contains("UQ_Nodes_Name", failure.Message, StringComparison.Ordinal);
                Assert.Null(context.Database.CurrentTransaction);
                await AssertCommittedStateAsync(context, deadline.Token);
            }

            // 固定两次关闭/重开；每次均验证持久 schema、数据、历史与真实约束。
            for (var reopen = 0; reopen < 2; reopen++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (app is not null)
                {
                    await app.StopAsync(deadline.Token);
                    await app.DisposeAsync();
                    app = null;
                    await StartServerAsync();
                }

                await using var reopened = new AtomicMigrationContext(CreateOptions());
                await AssertCommittedStateAsync(reopened, deadline.Token);
            }
        }
        finally
        {
            if (app is not null)
            {
                using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await app.StopAsync(cleanupDeadline.Token); }
                finally { await app.DisposeAsync(); }
            }
        }
    }

    private static async Task AssertCommittedStateAsync(AtomicMigrationContext context, CancellationToken cancellationToken)
    {
        Assert.Equal([AtomicMigrationInitial.Id, AtomicMigrationEvolve.Id],
            (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray());
        Assert.Equal(2L, await ScalarAsync(context, "SELECT COUNT(*) FROM \"Nodes\"", cancellationToken));
        Assert.Equal(1L, await ScalarAsync(context, "SELECT COUNT(*) FROM \"Children\"", cancellationToken));
        Assert.Equal("标签", await ScalarAsync(context, "SELECT \"Name\" FROM \"Labels\" WHERE \"Id\" = 1", cancellationToken));

        var columns = await ReadFirstColumnAsync(context, "DESCRIBE TABLE \"Nodes\"", cancellationToken);
        Assert.Contains("Note", columns);
        Assert.DoesNotContain("Transient", columns);
        var indexes = await ReadFirstColumnAsync(context, "SHOW INDEXES ON \"Nodes\"", cancellationToken);
        Assert.Contains("IX_Nodes_Name", indexes);
        Assert.DoesNotContain("UQ_Nodes_Name", indexes);

        // 失败迁移的第一条 DROP CONSTRAINT 必须被撤销；自引用与外部引用都仍受保护。
        await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Nodes\" (\"Id\", \"ParentId\", \"Name\") VALUES (3, 999, 'orphan')", cancellationToken));
        await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Children\" (\"Id\", \"NodeId\") VALUES (11, 999)", cancellationToken));
        await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Nodes\" (\"Id\", \"ParentId\", \"Name\") VALUES (3, 1, '')", cancellationToken));
        Assert.Equal(2L, await ScalarAsync(context, "SELECT COUNT(*) FROM \"Nodes\"", cancellationToken));
    }

    private static async Task<object?> ScalarAsync(DbContext context, string sql, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using DbCommand command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(cancellationToken);
        }
        finally { await context.Database.CloseConnectionAsync(); }
    }

    private static async Task<List<string>> ReadFirstColumnAsync(DbContext context, string sql, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using DbCommand command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var values = new List<string>();
            for (var count = 0; count < 32 && await reader.ReadAsync(cancellationToken); count++)
                values.Add(reader.GetString(0));
            Assert.False(await reader.ReadAsync(cancellationToken));
            return values;
        }
        finally { await context.Database.CloseConnectionAsync(); }
    }
}

/// <summary>将原子迁移与其它测试迁移隔离的上下文。</summary>
/// <param name="options">数据库连接及 Provider 配置。</param>
public sealed class AtomicMigrationContext(DbContextOptions<AtomicMigrationContext> options) : DbContext(options);

/// <summary>建立自引用表及外部引用表。</summary>
[DbContext(typeof(AtomicMigrationContext))]
[Migration(Id)]
public sealed class AtomicMigrationInitial : Migration
{
    internal const string Id = "20261105000100_AtomicInitial";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("Nodes", table => new
        {
            Id = table.Column<long>(type: "INT", nullable: false),
            ParentId = table.Column<long>(type: "INT", nullable: true),
            Name = table.Column<string>(type: "STRING", nullable: false),
        }, constraints: table =>
        {
            table.PrimaryKey("PK_Nodes", row => row.Id);
            table.ForeignKey("FK_Nodes_Parent", row => row.ParentId, "Nodes", "Id");
        });
        migrationBuilder.CreateTable("Children", table => new
        {
            Id = table.Column<long>(type: "INT", nullable: false),
            NodeId = table.Column<long>(type: "INT", nullable: false),
        }, constraints: table =>
        {
            table.PrimaryKey("PK_Children", row => row.Id);
            table.ForeignKey("FK_Children_Node", row => row.NodeId, "Nodes", "Id");
        });
    }
}

/// <summary>在同一迁移中提交已有表演进、索引、约束、新表及数据。</summary>
[DbContext(typeof(AtomicMigrationContext))]
[Migration(Id)]
public sealed class AtomicMigrationEvolve : Migration
{
    internal const string Id = "20261105000200_AtomicEvolve";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("Note", "Nodes", type: "STRING", nullable: true);
        migrationBuilder.CreateIndex("IX_Nodes_Name", "Nodes", "Name");
        migrationBuilder.AddCheckConstraint("CK_Nodes_Name", "Nodes", "\"Name\" <> ''");
        migrationBuilder.CreateTable("Labels", table => new
        {
            Id = table.Column<long>(type: "INT", nullable: false),
            Name = table.Column<string>(type: "STRING", nullable: false),
        }, constraints: table => table.PrimaryKey("PK_Labels", row => row.Id));
        migrationBuilder.InsertData("Labels", ["Id", "Name"], ["INT", "STRING"], new object[,] { { 1L, "标签" } });
    }
}

/// <summary>在已执行两条 DDL 后以真实唯一冲突使整个迁移失败。</summary>
[DbContext(typeof(AtomicMigrationContext))]
[Migration(Id)]
public sealed class AtomicMigrationFail : Migration
{
    internal const string Id = "20261105000300_AtomicFail";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey("FK_Nodes_Parent", "Nodes");
        migrationBuilder.AddColumn<string>("Transient", "Nodes", type: "STRING", nullable: true);
        migrationBuilder.CreateIndex("UQ_Nodes_Name", "Nodes", "Name", unique: true);
    }
}
