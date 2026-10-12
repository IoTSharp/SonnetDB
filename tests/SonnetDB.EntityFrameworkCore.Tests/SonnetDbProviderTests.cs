using System.Collections.Concurrent;
using System.Data.Common;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.EntityFrameworkCore.Extensions;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

public sealed class SonnetDbProviderTests : IDisposable
{
    private readonly string _root;

    public SonnetDbProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-ef-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void UseSonnetDB_RegistersProviderServices()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());

        Assert.Equal("SonnetDB.EntityFrameworkCore", context.Database.ProviderName);
        Assert.IsAssignableFrom<IRelationalTypeMappingSource>(context.GetService<IRelationalTypeMappingSource>());
        Assert.IsAssignableFrom<IQuerySqlGeneratorFactory>(context.GetService<IQuerySqlGeneratorFactory>());
        Assert.IsAssignableFrom<IMigrationsSqlGenerator>(context.GetService<IMigrationsSqlGenerator>());
        Assert.IsAssignableFrom<IHistoryRepository>(context.GetService<IHistoryRepository>());
    }

    /// <summary>EF 的命令超时配置必须写入真正执行的 SndbCommand。</summary>
    [Fact]
    public async Task SetCommandTimeout_PropagatesToExecutedSndbCommand()
    {
        var interceptor = new CommandTimeoutCaptureInterceptor();
        var options = new DbContextOptionsBuilder<DeviceContext>()
            .UseSonnetDB($"Data Source={_root}")
            .AddInterceptors(interceptor)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        using var context = new DeviceContext(options);
        context.Database.SetCommandTimeout(5);

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE command_timeout_probe (id INT, PRIMARY KEY (id))");

        var command = Assert.IsType<SndbCommand>(interceptor.LastCommand);
        Assert.Equal(5, command.CommandTimeout);
    }

    [Fact]
    public async Task SaveChanges_WithMinimalDbContext_PerformsCrud()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");

        context.Devices.Add(new Device { Id = 1, Name = "pump", Enabled = true });
        await context.SaveChangesAsync();

        var device = await context.Devices.SingleAsync(item => item.Id == 1);
        Assert.Equal("pump", device.Name);
        Assert.True(device.Enabled);

        device.Name = "pump-2";
        await context.SaveChangesAsync();

        Assert.Equal("pump-2", await context.Devices.Where(item => item.Enabled).Select(item => item.Name).SingleAsync());

        context.Devices.Remove(device);
        await context.SaveChangesAsync();

        Assert.Empty(await context.Devices.ToListAsync());
    }

    /// <summary>嵌入式命名外键的建表、删除、替换、回退与关闭重开保持真实约束。</summary>
    [Fact]
    public async Task NamedForeignKeyMigrations_Embedded_PreserveSchemaAndConstraintAcrossReopen()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var options = CreateNamedForeignKeyOptions($"Data Source={Path.Combine(_root, "named-foreign-key")}");

        await AssertNamedForeignKeyInitialAndReplacementAsync(options, deadline.Token);
        await AssertNamedForeignKeyReopenAndRollbackAsync(options, deadline.Token);
    }

    /// <summary>真实 REST 与 HTTP/2 Frame 迁移在 Server 重开后保持命名外键和约束行为。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task NamedForeignKeyMigrations_Remote_PreserveSchemaAndConstraintAcrossServerReopen(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        const string token = "issue-272-admin";
        const string database = "fk";
        var dataRoot = Path.GetFullPath(Path.Combine(_root, "fk"));
        var owner = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, ".issue-272-owner"), owner);
        var requests = new ConcurrentQueue<(string Path, string Protocol)>();
        WebApplication? app = null;

        async Task<DbContextOptions<NamedForeignKeyMigrationContext>> StartServerAsync()
        {
            app = EfTestServerHost.Build(
                new ServerOptions
                {
                    DataRoot = dataRoot,
                    AutoLoadExistingDatabases = true,
                    AllowAnonymousProbes = true,
                    Tokens = new Dictionary<string, string> { [token] = ServerRoles.Admin },
                },
                extraArgs: protocol == "frame-http2" ? ["--Kestrel:Endpoints:Http:Protocols=Http2"] : null);
            app.Use(async (httpContext, next) =>
            {
                requests.Enqueue((httpContext.Request.Path.Value ?? string.Empty, httpContext.Request.Protocol));
                await next(httpContext);
            });
            await app.StartAsync(deadline.Token);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
            var address = new Uri(Assert.Single(addresses.Addresses));
            return CreateNamedForeignKeyOptions(
                $"Data Source=sonnetdb+http://{address.Authority}/{database};Token={token};Timeout=30" +
                (protocol == "frame-http2" ? ";Protocol=frame-http2" : string.Empty));
        }

        try
        {
            var options = await StartServerAsync();
            await AssertNamedForeignKeyInitialAndReplacementAsync(options, deadline.Token);
            await app!.StopAsync();
            await app.DisposeAsync();
            app = null;

            options = await StartServerAsync();
            await AssertNamedForeignKeyReopenAndRollbackAsync(options, deadline.Token);

            var observed = requests.ToArray();
            Assert.Contains(observed, request => request.Path.StartsWith($"/v1/db/{database}/", StringComparison.Ordinal));
            Assert.All(observed, request => Assert.Equal(protocol == "frame-http2" ? "HTTP/2" : "HTTP/1.1", request.Protocol));
        }
        finally
        {
            try
            {
                if (app is not null)
                {
                    try { await app.StopAsync(); }
                    finally { await app.DisposeAsync(); }
                }
            }
            finally
            {
                var expected = Path.GetFullPath(Path.Combine(_root, "fk"));
                if (!string.Equals(dataRoot, expected, StringComparison.Ordinal) ||
                    File.GetAttributes(dataRoot).HasFlag(FileAttributes.ReparsePoint) ||
                    File.ReadAllText(Path.Combine(dataRoot, ".issue-272-owner")) != owner)
                    throw new InvalidOperationException("拒绝清理所有权不一致的命名外键测试目录。");
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private static DbContextOptions<NamedForeignKeyMigrationContext> CreateNamedForeignKeyOptions(string connectionString)
        => new DbContextOptionsBuilder<NamedForeignKeyMigrationContext>()
            .UseSonnetDB(connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

    private static async Task AssertNamedForeignKeyInitialAndReplacementAsync(
        DbContextOptions<NamedForeignKeyMigrationContext> options, CancellationToken cancellationToken)
    {
        await using var context = new NamedForeignKeyMigrationContext(options);
        var createScript = context.Database.GenerateCreateScript();
        Assert.Contains("CONSTRAINT \"FK_书\"\"籍_作者\" FOREIGN KEY", createScript, StringComparison.Ordinal);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(NamedForeignKeyInitial.MigrationId, cancellationToken);
        await AssertNamedForeignKeySchemaAsync(context, NamedForeignKeyNames.OriginalConstraint, NamedForeignKeyNames.Authors, cancellationToken);

        context.Authors.AddRange(new NamedForeignKeyAuthor { Id = 1 }, new NamedForeignKeyAuthor { Id = 2 });
        context.ReplacementAuthors.AddRange(new NamedForeignKeyReplacementAuthor { Id = 1 }, new NamedForeignKeyReplacementAuthor { Id = 3 });
        context.Books.Add(new NamedForeignKeyBook { Id = 1, AuthorId = 1 });
        await context.SaveChangesAsync(cancellationToken);
        await AssertNamedForeignKeyViolationAsync(context, 90, 3, NamedForeignKeyNames.OriginalConstraint, cancellationToken);

        var noOpDrop = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate(
            [new DropForeignKeyOperation { Table = NamedForeignKeyNames.Books, Name = "Missing_约束" }]));
        Assert.Equal(0, await context.Database.ExecuteSqlRawAsync(noOpDrop.CommandText, cancellationToken));
        await AssertNamedForeignKeySchemaAsync(context, NamedForeignKeyNames.OriginalConstraint, NamedForeignKeyNames.Authors, cancellationToken);
        await AssertNamedForeignKeyViolationAsync(context, 91, 3, NamedForeignKeyNames.OriginalConstraint, cancellationToken);

        await migrator.MigrateAsync(NamedForeignKeyRemoved.MigrationId, cancellationToken);
        await AssertNamedForeignKeySchemaAsync(context, null, null, cancellationToken);
        context.Books.Add(new NamedForeignKeyBook { Id = 2, AuthorId = 3 });
        await context.SaveChangesAsync(cancellationToken);
        Assert.Equal(3, (await context.Books.AsNoTracking().SingleAsync(book => book.Id == 2, cancellationToken)).AuthorId);

        await migrator.MigrateAsync(NamedForeignKeyReplaced.MigrationId, cancellationToken);
        await AssertNamedForeignKeySchemaAsync(context, NamedForeignKeyNames.ReplacementConstraint, NamedForeignKeyNames.ReplacementAuthors, cancellationToken);
        await AssertNamedForeignKeyViolationAsync(context, 92, 2, NamedForeignKeyNames.ReplacementConstraint, cancellationToken);
        context.Books.Add(new NamedForeignKeyBook { Id = 3, AuthorId = 3 });
        await context.SaveChangesAsync(cancellationToken);
        context.ReplacementAuthors.Remove(await context.ReplacementAuthors.SingleAsync(author => author.Id == 3, cancellationToken));
        await context.SaveChangesAsync(cancellationToken);
        var remainingBookIds = await context.Books.AsNoTracking().OrderBy(book => book.Id).Select(book => book.Id).ToArrayAsync(cancellationToken);
        Assert.Equal([1L], remainingBookIds);
        Assert.Equal(
            [NamedForeignKeyInitial.MigrationId, NamedForeignKeyRemoved.MigrationId, NamedForeignKeyReplaced.MigrationId],
            (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray());
    }

    private static async Task AssertNamedForeignKeyReopenAndRollbackAsync(
        DbContextOptions<NamedForeignKeyMigrationContext> options, CancellationToken cancellationToken)
    {
        await using var context = new NamedForeignKeyMigrationContext(options);
        await AssertNamedForeignKeySchemaAsync(context, NamedForeignKeyNames.ReplacementConstraint, NamedForeignKeyNames.ReplacementAuthors, cancellationToken);
        Assert.Equal(1, await context.Books.CountAsync(cancellationToken));
        await AssertNamedForeignKeyViolationAsync(context, 93, 2, NamedForeignKeyNames.ReplacementConstraint, cancellationToken);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(NamedForeignKeyRemoved.MigrationId, cancellationToken);
        await AssertNamedForeignKeySchemaAsync(context, null, null, cancellationToken);
        var orphan = new NamedForeignKeyBook { Id = 4, AuthorId = 99 };
        context.Books.Add(orphan);
        await context.SaveChangesAsync(cancellationToken);
        Assert.Equal(99, (await context.Books.AsNoTracking().SingleAsync(book => book.Id == 4, cancellationToken)).AuthorId);
        context.Books.Remove(orphan);
        await context.SaveChangesAsync(cancellationToken);

        await migrator.MigrateAsync(NamedForeignKeyInitial.MigrationId, cancellationToken);
        await AssertNamedForeignKeySchemaAsync(context, NamedForeignKeyNames.OriginalConstraint, NamedForeignKeyNames.Authors, cancellationToken);
        await AssertNamedForeignKeyViolationAsync(context, 94, 3, NamedForeignKeyNames.OriginalConstraint, cancellationToken);
        await migrator.MigrateAsync(Migration.InitialDatabase, cancellationToken);
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var tables = context.Database.GetDbConnection().GetSchema("Tables", [null, null, NamedForeignKeyNames.Books, null]);
            Assert.Empty(tables.Rows.Cast<System.Data.DataRow>());
        }
        finally { await context.Database.CloseConnectionAsync(); }
        Assert.Empty(await context.Database.GetAppliedMigrationsAsync(cancellationToken));

        await migrator.MigrateAsync(NamedForeignKeyInitial.MigrationId, cancellationToken);
        await AssertNamedForeignKeySchemaAsync(context, NamedForeignKeyNames.OriginalConstraint, NamedForeignKeyNames.Authors, cancellationToken);
        await AssertNamedForeignKeyViolationAsync(context, 95, 1, NamedForeignKeyNames.OriginalConstraint, cancellationToken);
        context.Authors.Add(new NamedForeignKeyAuthor { Id = 10 });
        context.Books.Add(new NamedForeignKeyBook { Id = 10, AuthorId = 10 });
        await context.SaveChangesAsync(cancellationToken);
        Assert.Equal(10, (await context.Books.AsNoTracking().SingleAsync(book => book.Id == 10, cancellationToken)).AuthorId);
        Assert.Equal([NamedForeignKeyInitial.MigrationId], (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray());
    }

    private static async Task AssertNamedForeignKeySchemaAsync(
        NamedForeignKeyMigrationContext context, string? expectedName, string? expectedPrincipal, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var constraints = context.Database.GetDbConnection().GetSchema("ForeignKeys", [null, null, NamedForeignKeyNames.Books, null]);
            var rows = constraints.Rows.Cast<System.Data.DataRow>().ToArray();
            if (expectedName is null)
            {
                Assert.Empty(rows);
                return;
            }
            var row = Assert.Single(rows);
            Assert.Equal(expectedName, row["CONSTRAINT_NAME"]);
            Assert.Equal(NamedForeignKeyNames.Books, row["TABLE_NAME"]);
            Assert.Equal(NamedForeignKeyNames.AuthorId, row["COLUMN_NAME"]);
            Assert.Equal(expectedPrincipal, row["PRINCIPAL_TABLE_NAME"]);
            Assert.Equal(NamedForeignKeyNames.Id, row["PRINCIPAL_COLUMN_NAME"]);
            Assert.Equal("Cascade", row["ON_DELETE"]);
        }
        finally { await context.Database.CloseConnectionAsync(); }
    }

    private static async Task AssertNamedForeignKeyViolationAsync(
        NamedForeignKeyMigrationContext context, long bookId, long authorId, string expectedConstraint, CancellationToken cancellationToken)
    {
        context.Books.Add(new NamedForeignKeyBook { Id = bookId, AuthorId = authorId });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(cancellationToken));
        Assert.Contains(expectedConstraint, error.ToString(), StringComparison.Ordinal);
        context.ChangeTracker.Clear();
        Assert.False(await context.Books.AnyAsync(book => book.Id == bookId, cancellationToken));
    }

    /// <summary>普通跟踪 UPDATE 实际命中零行时必须按 EF Core 契约报告乐观并发冲突。</summary>
    [Fact]
    public void SaveChanges_TrackedUpdateMatchesNoRows_ThrowsConcurrencyException()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());
        context.Database.ExecuteSqlRaw(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");

        var missing = new Device { Id = 404, Name = "missing", Enabled = true };
        context.Attach(missing);
        context.Entry(missing).Property(item => item.Name).IsModified = true;

        var exception = Assert.Throws<DbUpdateConcurrencyException>(() => context.SaveChanges());
        Assert.Same(missing, Assert.Single(exception.Entries).Entity);
    }

    /// <summary>并发令牌已被其他上下文更新后，陈旧实体的异步保存必须可靠检测冲突。</summary>
    [Fact]
    public async Task SaveChangesAsync_StaleConcurrencyToken_ThrowsConcurrencyException()
    {
        var options = CreateOptions<IdentitySubsetContext>();
        await using (var setup = new IdentitySubsetContext(options))
        {
            await setup.Database.ExecuteSqlRawAsync(
                "CREATE TABLE \"AspNetUsers\" (\"Id\" STRING NOT NULL, \"UserName\" STRING NULL, \"NormalizedUserName\" STRING NULL, \"EmailConfirmed\" BOOL NOT NULL, \"ConcurrencyStamp\" STRING NULL, PRIMARY KEY (\"Id\"))");
            setup.Users.Add(new IdentityUserSubset
            {
                Id = "user-stale",
                UserName = "original",
                ConcurrencyStamp = "stamp-1",
            });
            await setup.SaveChangesAsync();
        }

        await using var staleContext = new IdentitySubsetContext(options);
        var stale = await staleContext.Users.SingleAsync(item => item.Id == "user-stale");

        await using (var winnerContext = new IdentitySubsetContext(options))
        {
            var winner = await winnerContext.Users.SingleAsync(item => item.Id == "user-stale");
            winner.UserName = "winner";
            winner.ConcurrencyStamp = "stamp-2";
            await winnerContext.SaveChangesAsync();
        }

        stale.UserName = "stale";
        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => staleContext.SaveChangesAsync());
        Assert.Same(stale, Assert.Single(exception.Entries).Entity);
    }

    /// <summary>普通跟踪 DELETE 实际命中零行时，异步保存必须报告乐观并发冲突。</summary>
    [Fact]
    public async Task SaveChangesAsync_TrackedDeleteMatchesNoRows_ThrowsConcurrencyException()
    {
        await using var context = new DeviceContext(CreateOptions<DeviceContext>());
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");

        var missing = new Device { Id = 405, Name = "missing", Enabled = false };
        context.Attach(missing);
        context.Remove(missing);

        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync());
        Assert.Same(missing, Assert.Single(exception.Entries).Entity);
    }

    /// <summary>
    /// 验证 EF Core ExecuteUpdateAsync 生成的目标表别名可由 SonnetDB 完整解析并执行。
    /// </summary>
    [Fact]
    public async Task ExecuteUpdateAsync_WithTargetAlias_PerformsConditionalUpdate()
    {
        var interceptor = new CommandTimeoutCaptureInterceptor();
        var options = new DbContextOptionsBuilder<DeviceContext>()
            .UseSonnetDB($"Data Source={_root}")
            .AddInterceptors(interceptor)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        using var context = new DeviceContext(options);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");
        context.Devices.AddRange(
            new Device { Id = 1, Name = "pump", Enabled = true },
            new Device { Id = 2, Name = "fan", Enabled = false });
        await context.SaveChangesAsync();

        var id = 1L;
        var changed = await context.Devices
            .Where(item => item.Id == id && item.Enabled)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Enabled, false)
                .SetProperty(item => item.Name, "pump-offline"));

        Assert.Equal(1, changed);
        Assert.Contains("UPDATE \"Devices\" AS ", interceptor.LastCommand!.CommandText, StringComparison.Ordinal);
        context.ChangeTracker.Clear();
        var updated = await context.Devices.SingleAsync(item => item.Id == id);
        Assert.False(updated.Enabled);
        Assert.Equal("pump-offline", updated.Name);
        Assert.True(await context.Devices.Where(item => item.Id == 2).Select(item => item.Enabled).SingleAsync() is false);

        Assert.Equal(
            0,
            await context.Devices
                .Where(item => item.Id == 404)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Enabled, true)));
        Assert.Equal(
            2,
            await context.Devices
                .Where(item => !item.Enabled)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Enabled, true)));
    }

    /// <summary>ExecuteDeleteAsync 返回命中、未命中和多行删除的真实行数。</summary>
    [Fact]
    public async Task ExecuteDeleteAsync_ReturnsActualRowCount()
    {
        await using var context = new DeviceContext(CreateOptions<DeviceContext>());
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");
        context.Devices.AddRange(
            new Device { Id = 1, Name = "pump", Enabled = false },
            new Device { Id = 2, Name = "fan", Enabled = true },
            new Device { Id = 3, Name = "valve", Enabled = true });
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.Devices.Where(item => item.Id == 1).ExecuteDeleteAsync());
        Assert.Equal(0, await context.Devices.Where(item => item.Id == 404).ExecuteDeleteAsync());
        Assert.Equal(2, await context.Devices.Where(item => item.Enabled).ExecuteDeleteAsync());
        Assert.Empty(await context.Devices.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// 验证 EF Core 的参数化 AnyAsync 生成 EXISTS，并在同一查询形状下保持命中、未命中和 NULL 语义。
    /// </summary>
    [Fact]
    public async Task AnyAsync_WithIndexedKeyAndResidualPredicates_ExecutesParameterizedExists()
    {
        var interceptor = new ReaderCommandCaptureInterceptor();
        var options = new DbContextOptionsBuilder<NullableDeviceContext>()
            .UseSonnetDB($"Data Source={_root}")
            .AddInterceptors(interceptor)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        using var context = new NullableDeviceContext(options);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");
        await context.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX \"UX_Devices_Name\" ON \"Devices\" (\"Name\")");
        context.Devices.AddRange(
            new Device { Id = 1, Name = "pump-001", Enabled = false },
            new Device { Id = 2, Name = "pump-002", Enabled = true });
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Devices\" (\"Id\", \"Name\", \"Enabled\") VALUES (3, NULL, TRUE)");

        string? name = "pump-002";
        bool enabled = true;
        long minimumId = 2;
        var query = context.Devices.Where(item =>
            item.Name == name && item.Enabled == enabled && item.Id >= minimumId);

        interceptor.Reset();
        Assert.True(await query.AnyAsync());
        Assert.Contains("EXISTS", interceptor.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(interceptor.ParameterValues, static value => Equals(value, "pump-002"));
        Assert.Contains(interceptor.ParameterValues, static value => Equals(value, 2L));

        name = "missing";
        interceptor.Reset();
        Assert.False(await query.AnyAsync());
        Assert.Contains(interceptor.ParameterValues, static value => Equals(value, "missing"));

        name = null;
        interceptor.Reset();
        Assert.True(await query.AnyAsync());
        Assert.Contains("EXISTS", interceptor.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IS NULL", interceptor.CommandText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateCreateScript_WithIntegerValueGeneratedOnAdd_EmitsAutoIncrement()
    {
        using var context = new GeneratedKeysContext(CreateOptions<GeneratedKeysContext>());

        var script = context.Database.GenerateCreateScript();

        Assert.Contains("CREATE TABLE \"GeneratedInts\"", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"GeneratedLongs\"", script, StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Matches(script, "\"Id\" INT AUTO_INCREMENT NOT NULL", RegexOptions.IgnoreCase).Count);
    }

    [Fact]
    public void GenerateCreateScript_WithValueGeneratedNever_DoesNotEmitAutoIncrement()
    {
        using var context = new ManualKeyContext(CreateOptions<ManualKeyContext>());

        var script = context.Database.GenerateCreateScript();

        Assert.Contains("\"Id\" INT NOT NULL", script, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTO_INCREMENT", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveChanges_WithValueGeneratedNever_PreservesClientKey()
    {
        using var context = new ManualKeyContext(CreateOptions<ManualKeyContext>());
        await context.Database.EnsureCreatedAsync();
        var entity = new ManualKeyEntity { Id = 42, Name = "pump" };

        var entry = context.Entities.Add(entity);

        Assert.False(entry.Property(item => item.Id).IsTemporary);

        await context.SaveChangesAsync();

        Assert.Equal(42L, entity.Id);
        context.ChangeTracker.Clear();
        Assert.Equal("pump", await context.Entities
            .Where(item => item.Id == entity.Id)
            .Select(item => item.Name)
            .SingleAsync());
    }

    [Fact]
    public async Task SaveChanges_WithValueGeneratedOnAddInteger_UsesDatabaseGeneratedKey()
    {
        using var context = new GeneratedKeysContext(CreateOptions<GeneratedKeysContext>());
        await context.Database.EnsureCreatedAsync();
        var entity = new GeneratedIntEntity { Name = "pump" };

        var entry = context.GeneratedInts.Add(entity);

        var idEntry = entry.Property(item => item.Id);
        Assert.Equal(ValueGenerated.OnAdd, idEntry.Metadata.ValueGenerated);
        Assert.True(idEntry.IsTemporary);

        await context.SaveChangesAsync();

        Assert.Equal(1, entity.Id);
        Assert.False(entry.Property(item => item.Id).IsTemporary);
        context.ChangeTracker.Clear();
        Assert.Equal("pump", await context.GeneratedInts
            .Where(item => item.Id == entity.Id)
            .Select(item => item.Name)
            .SingleAsync());
    }

    [Fact]
    public async Task SaveChanges_WithMultipleGeneratedKeys_PropagatesReturnedValues()
    {
        using var context = new GeneratedKeysContext(CreateOptions<GeneratedKeysContext>());
        await context.Database.EnsureCreatedAsync();
        var first = new GeneratedLongEntity { Name = "pump" };
        var second = new GeneratedLongEntity { Name = "fan" };

        context.GeneratedLongs.AddRange(first, second);

        Assert.True(context.Entry(first).Property(item => item.Id).IsTemporary);
        Assert.True(context.Entry(second).Property(item => item.Id).IsTemporary);

        await context.SaveChangesAsync();

        Assert.Equal([1L, 2L], [first.Id, second.Id]);
        Assert.False(context.Entry(first).Property(item => item.Id).IsTemporary);
        Assert.False(context.Entry(second).Property(item => item.Id).IsTemporary);
        context.ChangeTracker.Clear();
        var storedIds = await context.GeneratedLongs
            .OrderBy(item => item.Id)
            .Select(item => item.Id)
            .ToArrayAsync();
        Assert.Equal([1L, 2L], storedIds);
    }

    [Fact]
    public async Task SaveChanges_WithGeneratedParentKey_PropagatesForeignKeyToChild()
    {
        using var context = new GeneratedRelationshipContext(CreateOptions<GeneratedRelationshipContext>());
        await context.Database.EnsureCreatedAsync();
        var parent = new GeneratedParent
        {
            Name = "plant",
            Children =
            [
                new GeneratedChild { Id = 100, Name = "pump" },
            ],
        };

        context.Parents.Add(parent);

        Assert.True(context.Entry(parent).Property(item => item.Id).IsTemporary);

        await context.SaveChangesAsync();

        var child = Assert.Single(parent.Children);
        Assert.Equal(1L, parent.Id);
        Assert.Equal(parent.Id, child.ParentId);
        context.ChangeTracker.Clear();

        var storedChild = await context.Children
            .Include(item => item.Parent)
            .SingleAsync(item => item.Id == child.Id);
        Assert.Equal(parent.Id, storedChild.ParentId);
        Assert.Equal(parent.Id, storedChild.Parent.Id);
    }

    [Fact]
    public async Task RemoteSaveChanges_WithMultipleGeneratedKeys_UsesDatabaseGeneratedValues()
    {
        const string token = "ef-remote-generated-keys";
        var database = "ef_remote_generated_keys_" + Guid.NewGuid().ToString("N");
        var dataRoot = Path.Combine(Path.GetTempPath(), "sndb-ef-remote-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);

        await using var app = EfTestServerHost.Build(new ServerOptions
        {
            DataRoot = dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [token] = ServerRoles.Admin,
            },
        });

        try
        {
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
            var baseUrl = addresses.Addresses.First();
            var connectionString =
                $"Data Source=sonnetdb+http://{new Uri(baseUrl).Authority}/{database};Token={token};Timeout=30";
            using var context = new GeneratedKeysContext(
                new DbContextOptionsBuilder<GeneratedKeysContext>()
                    .UseSonnetDB(connectionString)
                    .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                    .Options);
            await context.Database.EnsureCreatedAsync();
            var first = new GeneratedLongEntity { Name = "pump" };
            var second = new GeneratedLongEntity { Name = "fan" };

            context.GeneratedLongs.AddRange(first, second);
            Assert.True(context.Entry(first).Property(item => item.Id).IsTemporary);
            Assert.True(context.Entry(second).Property(item => item.Id).IsTemporary);

            await context.SaveChangesAsync();

            Assert.Equal([1L, 2L], [first.Id, second.Id]);
            context.ChangeTracker.Clear();
            var storedIds = await context.GeneratedLongs
                .OrderBy(item => item.Id)
                .Select(item => item.Id)
                .ToArrayAsync();
            Assert.Equal([1L, 2L], storedIds);
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(dataRoot))
            {
                try { Directory.Delete(dataRoot, recursive: true); } catch { /* best-effort */ }
            }
        }
    }

    [Fact]
    public async Task DateTimeParameter_WithRangePredicate_FiltersDateTimeColumn()
    {
        using var context = new EventContext(CreateOptions<EventContext>());
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Events\" (\"Id\" INT NOT NULL, \"OccurredAt\" DATETIME NOT NULL, PRIMARY KEY (\"Id\"))");
        var first = new DateTime(2026, 7, 10, 15, 48, 56, DateTimeKind.Utc);
        context.Events.AddRange(
            new EventRecord { Id = 1, OccurredAt = first },
            new EventRecord { Id = 2, OccurredAt = first.AddSeconds(5) });
        await context.SaveChangesAsync();

        var from = first.AddSeconds(-1);
        var to = first.AddSeconds(1);
        var ids = await context.Events
            .Where(item => item.OccurredAt >= from && item.OccurredAt <= to)
            .Select(item => item.Id)
            .ToListAsync();

        Assert.Equal([1L], ids);
    }

    [Fact]
    public async Task Query_DateTimeNowDate_TranslatesAndExecutes()
    {
        using var context = new EventContext(CreateOptions<EventContext>());
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Events\" (\"Id\" INT NOT NULL, \"OccurredAt\" DATETIME NOT NULL, PRIMARY KEY (\"Id\"))");
        var today = DateTime.Now.Date;
        context.Events.AddRange(
            new EventRecord { Id = 1, OccurredAt = today },
            new EventRecord { Id = 2, OccurredAt = today.AddDays(-1) });
        await context.SaveChangesAsync();

        var query = context.Events.Where(item =>
            DateTime.Now.Date >= item.OccurredAt
            && DateTime.Now.Date <= item.OccurredAt);
        var sql = query.ToQueryString();
        var todaySql = context.Events
            .Where(item => item.OccurredAt == DateTime.Today)
            .ToQueryString();
        var ids = await query.Select(item => item.Id).ToArrayAsync();

        Assert.Contains("CURRENT_DATETIME", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATE_ONLY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CURRENT_DATETIME", todaySql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATE_ONLY", todaySql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([1L], ids);
    }

    [Fact]
    public async Task Query_DateMembersAndAddMethods_TranslateAndExecute()
    {
        using var context = new EventContext(CreateOptions<EventContext>());
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Events\" (\"Id\" INT NOT NULL, \"OccurredAt\" DATETIME NOT NULL, PRIMARY KEY (\"Id\"))");
        context.Events.AddRange(
            new EventRecord
            {
                Id = 1,
                OccurredAt = new DateTime(2026, 7, 10, 15, 48, 56, 123, DateTimeKind.Utc)
            },
            new EventRecord
            {
                Id = 2,
                OccurredAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        await context.SaveChangesAsync();

        var expected = new DateTime(2026, 7, 11, 15, 48, 56, 123, DateTimeKind.Utc);
        var query = context.Events.Where(item =>
            item.OccurredAt.Year == 2026
            && item.OccurredAt.Month == 7
            && item.OccurredAt.Day == 10
            && item.OccurredAt.Millisecond == 123
            && item.OccurredAt.AddDays(1) == expected);
        var sql = query.ToQueryString();
        var ids = await query.Select(item => item.Id).ToArrayAsync();
        var projection = await context.Events
            .Where(item => item.Id == 1)
            .Select(item => new
            {
                Date = item.OccurredAt.Date,
                NextDay = item.OccurredAt.AddDays(1)
            })
            .SingleAsync();

        Assert.Contains("DATE_PART", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATE_ADD_DATETIME", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([1L], ids);
        Assert.Equal(new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc), projection.Date);
        Assert.Equal(expected, projection.NextDay);
    }

    [Fact]
    public async Task Query_DateTimeOffsetMembersAndMethods_TranslateAndExecute()
    {
        using var context = new OffsetEventContext(CreateOptions<OffsetEventContext>());
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"OffsetEvents\" (\"Id\" INT NOT NULL, \"OccurredAt\" DATETIME NOT NULL, PRIMARY KEY (\"Id\"))");
        var occurredAt = new DateTimeOffset(2026, 7, 10, 15, 48, 56, TimeSpan.Zero);
        context.Events.Add(new OffsetEventRecord { Id = 1, OccurredAt = occurredAt });
        await context.SaveChangesAsync();

        var materialized = await context.Events.AsNoTracking().SingleAsync(item => item.Id == 1);
        Assert.Equal(occurredAt, materialized.OccurredAt);

        var expected = occurredAt.AddHours(2);
        var unixMilliseconds = occurredAt.ToUnixTimeMilliseconds();
        var query = context.Events.Where(item =>
            item.OccurredAt.Year == 2026
            && item.OccurredAt.AddHours(2) == expected
            && item.OccurredAt.ToUnixTimeMilliseconds() == unixMilliseconds
            && item.OccurredAt <= DateTimeOffset.UtcNow);
        var sql = query.ToQueryString();
        var projection = await query
            .Select(item => new
            {
                Date = item.OccurredAt.Date,
                Adjusted = item.OccurredAt.AddHours(2),
                DateTime = item.OccurredAt.DateTime,
                UtcDateTime = item.OccurredAt.UtcDateTime,
                LocalDateTime = item.OccurredAt.LocalDateTime
            })
            .SingleAsync();

        Assert.Contains("DATE_PART", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATE_ADD_DATETIME_OFFSET", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TO_UNIX_MILLISECONDS", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CURRENT_UTC_DATETIME_OFFSET", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(occurredAt.Date, projection.Date);
        Assert.Equal(expected, projection.Adjusted);
        Assert.Equal(occurredAt.DateTime, projection.DateTime);
        Assert.Equal(occurredAt.UtcDateTime, projection.UtcDateTime);
        Assert.Equal(occurredAt.LocalDateTime, projection.LocalDateTime);
    }

    [Fact]
    public async Task UseSonnetDB_WithExistingConnection_PerformsCrud()
    {
        await using var connection = new SndbConnection($"Data Source={_root}");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DeviceContext>()
            .UseSonnetDB(connection)
            .Options;

        using var context = new DeviceContext(options);

        Assert.Same(connection, context.Database.GetDbConnection());
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");

        context.Devices.Add(new Device { Id = 10, Name = "gateway", Enabled = true });
        await context.SaveChangesAsync();

        Assert.Equal("gateway", await context.Devices.Where(item => item.Enabled).Select(item => item.Name).SingleAsync());
    }

    [Fact]
    public async Task SaveChanges_WithIdentitySubset_HandlesCommonIdentityColumns()
    {
        using var context = new IdentitySubsetContext(CreateOptions<IdentitySubsetContext>());

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"AspNetUsers\" (\"Id\" STRING NOT NULL, \"UserName\" STRING NULL, \"NormalizedUserName\" STRING NULL, \"EmailConfirmed\" BOOL NOT NULL, \"ConcurrencyStamp\" STRING NULL, PRIMARY KEY (\"Id\"))");

        context.Users.Add(new IdentityUserSubset
        {
            Id = "user-1",
            UserName = "alice",
            NormalizedUserName = "ALICE",
            EmailConfirmed = true,
            ConcurrencyStamp = "stamp-1"
        });
        await context.SaveChangesAsync();

        var user = await context.Users.SingleAsync(item => item.NormalizedUserName == "ALICE");
        Assert.Equal("alice", user.UserName);
        Assert.True(user.EmailConfirmed);

        user.ConcurrencyStamp = "stamp-2";
        await context.SaveChangesAsync();

        Assert.Equal("stamp-2", await context.Users.Select(item => item.ConcurrencyStamp).SingleAsync());
    }

    [Fact]
    public void QueryTranslation_ToQueryString_UsesSonnetDbSql()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());

        var sql = context.Devices
            .Where(item => item.Enabled && item.Id > 10)
            .OrderBy(item => item.Name)
            .Select(item => new { item.Id, item.Name })
            .ToQueryString();

        Assert.Contains("SELECT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"Devices\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"Enabled\"", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QueryTranslation_StringPatterns_UseLike()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());

        var sql = context.Devices
            .Where(item => item.Name.StartsWith("pump")
                || item.Name.EndsWith("001")
                || item.Name.Contains("mp-0"))
            .ToQueryString();

        Assert.Contains("LIKE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("starts_with", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ends_with", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contains(", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QueryTranslation_RegexIsMatch_UsesRegexpLike()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());

        var sql = context.Devices
            .Where(item => Regex.IsMatch(item.Name, "^pump-[0-9]+$", RegexOptions.IgnoreCase))
            .ToQueryString();

        Assert.Contains("REGEXP_LIKE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'i'", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Query_RegexIsMatch_FiltersRows()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");
        context.Devices.AddRange(
            new Device { Id = 1, Name = "Pump-001", Enabled = true },
            new Device { Id = 2, Name = "pump-A", Enabled = true },
            new Device { Id = 3, Name = "fan-001", Enabled = true });
        await context.SaveChangesAsync();

        var ids = await context.Devices
            .Where(item => Regex.IsMatch(item.Name, "^pump-[0-9]+$", RegexOptions.IgnoreCase))
            .Select(item => item.Id)
            .ToArrayAsync();

        Assert.Equal([1L], ids);
    }

    [Fact]
    public async Task Query_StringPatterns_FilterRows()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");

        context.Devices.AddRange(
            new Device { Id = 1, Name = "pump-001", Enabled = true },
            new Device { Id = 2, Name = "pump-002", Enabled = true },
            new Device { Id = 3, Name = "fan-001", Enabled = true },
            new Device { Id = 4, Name = "valve-003", Enabled = true });
        await context.SaveChangesAsync();

        Assert.Equal(new long[] { 1L, 2L }, await context.Devices
            .Where(item => item.Name.StartsWith("pump"))
            .OrderBy(item => item.Id)
            .Select(item => item.Id)
            .ToArrayAsync());

        Assert.Equal(new long[] { 1L, 3L }, await context.Devices
            .Where(item => item.Name.EndsWith("001"))
            .OrderBy(item => item.Id)
            .Select(item => item.Id)
            .ToArrayAsync());

        Assert.Equal(new long[] { 1L, 2L }, await context.Devices
            .Where(item => item.Name.Contains("mp-0"))
            .OrderBy(item => item.Id)
            .Select(item => item.Id)
            .ToArrayAsync());
    }

    /// <summary>以原问题三字符及组合、NULL 小样验证参数转义与实际列表/计数。</summary>
    [Theory]
    [InlineData("%", 1L)]
    [InlineData("_", 3L)]
    [InlineData("\\", 5L)]
    [InlineData("\\%_", 5L)]
    [InlineData(null, 0L)]
    public async Task Query_StringPatternEscapingSmoke_PreservesLiteralAndNullParameters(string? pattern, long expectedId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var interceptor = new ReaderCommandCaptureInterceptor();
        using var context = new NullableDeviceContext(
            new DbContextOptionsBuilder<NullableDeviceContext>()
                .UseSonnetDB($"Data Source={_root}")
                .AddInterceptors(interceptor)
                .Options);
        await context.Database.EnsureCreatedAsync(deadline.Token);
        Device[] devices =
        [
            new() { Id = 1, Name = "100% Real" },
            new() { Id = 2, Name = "100X Real" },
            new() { Id = 3, Name = "A_B" },
            new() { Id = 4, Name = "AXB" },
            new() { Id = 5, Name = pattern == "\\%_" ? "\\%_" : "path\\name" },
        ];
        context.Devices.AddRange(devices);
        await context.SaveChangesAsync(deadline.Token);
        context.ChangeTracker.Clear();

        var query = context.Devices.Where(item => item.Name.Contains(pattern!));
        var ids = await query.OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync(deadline.Token);

        Assert.Equal(expectedId == 0 ? [] : new[] { expectedId }, ids);
        Assert.Equal(ids.Length, await query.CountAsync(deadline.Token));
        if (pattern is not null)
        {
            Assert.Contains(interceptor.ParameterValues, value => Equals(value, pattern));
        }
    }

    /// <summary>实际执行三种方法的常量/参数、字面特殊字符、Unicode、空串和 NULL。</summary>
    [Theory]
    [InlineData(nameof(string.Contains), "%", false)]
    [InlineData(nameof(string.Contains), "%", true)]
    [InlineData(nameof(string.Contains), "_", false)]
    [InlineData(nameof(string.Contains), "_", true)]
    [InlineData(nameof(string.Contains), "\\", false)]
    [InlineData(nameof(string.Contains), "\\", true)]
    [InlineData(nameof(string.Contains), "\\%_", false)]
    [InlineData(nameof(string.Contains), "\\%_", true)]
    [InlineData(nameof(string.Contains), "泵_%\\😀设备", false)]
    [InlineData(nameof(string.Contains), "泵_%\\😀设备", true)]
    [InlineData(nameof(string.Contains), "", false)]
    [InlineData(nameof(string.Contains), "", true)]
    [InlineData(nameof(string.Contains), null, false)]
    [InlineData(nameof(string.Contains), null, true)]
    [InlineData(nameof(string.StartsWith), "%", false)]
    [InlineData(nameof(string.StartsWith), "%", true)]
    [InlineData(nameof(string.StartsWith), "_", false)]
    [InlineData(nameof(string.StartsWith), "_", true)]
    [InlineData(nameof(string.StartsWith), "\\", false)]
    [InlineData(nameof(string.StartsWith), "\\", true)]
    [InlineData(nameof(string.StartsWith), "\\%_", false)]
    [InlineData(nameof(string.StartsWith), "\\%_", true)]
    [InlineData(nameof(string.StartsWith), "泵_%\\😀设备", false)]
    [InlineData(nameof(string.StartsWith), "泵_%\\😀设备", true)]
    [InlineData(nameof(string.StartsWith), "", false)]
    [InlineData(nameof(string.StartsWith), "", true)]
    [InlineData(nameof(string.StartsWith), null, false)]
    [InlineData(nameof(string.StartsWith), null, true)]
    [InlineData(nameof(string.EndsWith), "%", false)]
    [InlineData(nameof(string.EndsWith), "%", true)]
    [InlineData(nameof(string.EndsWith), "_", false)]
    [InlineData(nameof(string.EndsWith), "_", true)]
    [InlineData(nameof(string.EndsWith), "\\", false)]
    [InlineData(nameof(string.EndsWith), "\\", true)]
    [InlineData(nameof(string.EndsWith), "\\%_", false)]
    [InlineData(nameof(string.EndsWith), "\\%_", true)]
    [InlineData(nameof(string.EndsWith), "泵_%\\😀设备", false)]
    [InlineData(nameof(string.EndsWith), "泵_%\\😀设备", true)]
    [InlineData(nameof(string.EndsWith), "", false)]
    [InlineData(nameof(string.EndsWith), "", true)]
    [InlineData(nameof(string.EndsWith), null, false)]
    [InlineData(nameof(string.EndsWith), null, true)]
    public async Task Query_StringPatterns_LiteralSemanticsMatchListCountAndPage(
        string method, string? pattern, bool constant)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var interceptor = new ReaderCommandCaptureInterceptor();
        using var context = new NullableDeviceContext(
            new DbContextOptionsBuilder<NullableDeviceContext>()
                .UseSonnetDB($"Data Source={_root}")
                .AddInterceptors(interceptor)
                .Options);
        await context.Database.EnsureCreatedAsync(deadline.Token);
        var devices = CreateLiteralPatternDevices();
        context.Devices.AddRange(devices);
        await context.SaveChangesAsync(deadline.Token);
        context.ChangeTracker.Clear();

        await AssertLiteralPatternQueryAsync(
            context.Devices, devices, method, pattern, constant, interceptor, deadline.Token);
    }

    /// <summary>通过真实 Kestrel REST/Frame HTTP2 执行三种方法的特殊字符列表、计数和分页。</summary>
    [Theory]
    [InlineData(nameof(string.Contains), "%", "rest")]
    [InlineData(nameof(string.Contains), "%", "frame-http2")]
    [InlineData(nameof(string.Contains), "_", "rest")]
    [InlineData(nameof(string.Contains), "_", "frame-http2")]
    [InlineData(nameof(string.Contains), "\\", "rest")]
    [InlineData(nameof(string.Contains), "\\", "frame-http2")]
    [InlineData(nameof(string.StartsWith), "%", "rest")]
    [InlineData(nameof(string.StartsWith), "%", "frame-http2")]
    [InlineData(nameof(string.StartsWith), "_", "rest")]
    [InlineData(nameof(string.StartsWith), "_", "frame-http2")]
    [InlineData(nameof(string.StartsWith), "\\", "rest")]
    [InlineData(nameof(string.StartsWith), "\\", "frame-http2")]
    [InlineData(nameof(string.EndsWith), "%", "rest")]
    [InlineData(nameof(string.EndsWith), "%", "frame-http2")]
    [InlineData(nameof(string.EndsWith), "_", "rest")]
    [InlineData(nameof(string.EndsWith), "_", "frame-http2")]
    [InlineData(nameof(string.EndsWith), "\\", "rest")]
    [InlineData(nameof(string.EndsWith), "\\", "frame-http2")]
    public async Task RemoteQuery_StringPatterns_LiteralSemanticsMatchListCountAndPage(
        string method, string pattern, string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var dataRoot = Path.Combine(_root, "remote-patterns");
        var requests = new ConcurrentQueue<(string Method, string Protocol)>();
        var frameHttp2 = protocol == "frame-http2";
        await using var app = EfTestServerHost.Build(
            new ServerOptions
            {
                DataRoot = dataRoot,
                AutoLoadExistingDatabases = true,
                AllowAnonymousProbes = true,
                Tokens = new Dictionary<string, string> { ["ef-patterns-admin"] = ServerRoles.Admin },
            },
            extraArgs: frameHttp2 ? ["--Kestrel:Endpoints:Http:Protocols=Http2"] : null);
        app.Use(async (httpContext, next) =>
        {
            requests.Enqueue((httpContext.Request.Method, httpContext.Request.Protocol));
            await next(httpContext);
        });

        try
        {
            await app.StartAsync(deadline.Token);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
            var baseUrl = Assert.Single(addresses.Addresses);
            var interceptor = new ReaderCommandCaptureInterceptor();
            using var context = new NullableDeviceContext(
                new DbContextOptionsBuilder<NullableDeviceContext>()
                    .UseSonnetDB(
                        $"Data Source=sonnetdb+http://{new Uri(baseUrl).Authority}/ef_patterns;" +
                        "Token=ef-patterns-admin;Timeout=15" +
                        (frameHttp2 ? ";Protocol=frame-http2" : string.Empty))
                    .AddInterceptors(interceptor)
                    .Options);
            await context.Database.EnsureCreatedAsync(deadline.Token);
            var devices = CreateLiteralPatternDevices();
            context.Devices.AddRange(devices);
            await context.SaveChangesAsync(deadline.Token);
            context.ChangeTracker.Clear();
            requests.Clear();

            await AssertLiteralPatternQueryAsync(
                context.Devices, devices, method, pattern, constant: false, interceptor, deadline.Token);

            var observed = requests.ToArray();
            Assert.NotEmpty(observed);
            Assert.All(observed, request =>
            {
                Assert.Equal("POST", request.Method);
                Assert.Equal(frameHttp2 ? "HTTP/2" : "HTTP/1.1", request.Protocol);
            });
        }
        finally
        {
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(cleanupDeadline.Token);
        }
    }

    /// <summary>真实 REST 与 HTTP2 对捕获的 NULL 谓词及显式线上 NULL 参数都返回空列表、零计数和空分页。</summary>
    /// <param name="protocol">真实连接协议。</param>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task RemoteQuery_CapturedNullStringParameter_ReturnsEmptyAndPreservesWireNull(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var requests = new ConcurrentQueue<(string Method, string Protocol)>();
        var frameHttp2 = protocol == "frame-http2";
        await using var app = EfTestServerHost.Build(
            new ServerOptions
            {
                DataRoot = Path.Combine(_root, "remote-null-patterns"),
                AutoLoadExistingDatabases = true,
                AllowAnonymousProbes = true,
                Tokens = new Dictionary<string, string> { ["ef-null-patterns-admin"] = ServerRoles.Admin },
            },
            extraArgs: frameHttp2 ? ["--Kestrel:Endpoints:Http:Protocols=Http2"] : null);
        app.Use(async (httpContext, next) =>
        {
            requests.Enqueue((httpContext.Request.Method, httpContext.Request.Protocol));
            await next(httpContext);
        });

        try
        {
            await app.StartAsync(deadline.Token);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
            var address = new Uri(Assert.Single(addresses.Addresses));
            var interceptor = new ReaderCommandCaptureInterceptor();
            using var context = new NullableDeviceContext(
                new DbContextOptionsBuilder<NullableDeviceContext>()
                    .UseSonnetDB(
                        $"Data Source=sonnetdb+http://{address.Authority}/ef_null_patterns;" +
                        "Token=ef-null-patterns-admin;Timeout=15" +
                        (frameHttp2 ? ";Protocol=frame-http2" : string.Empty))
                    .AddInterceptors(interceptor)
                    .Options);
            await context.Database.EnsureCreatedAsync(deadline.Token);
            var devices = CreateLiteralPatternDevices();
            context.Devices.AddRange(devices);
            await context.SaveChangesAsync(deadline.Token);
            context.ChangeTracker.Clear();
            requests.Clear();

            // EF 可合法将捕获的 NULL 谓词折叠为 FALSE；这里只验证实际查询结果，不伪称参数必然上网。
            string? capturedPattern = null;
            foreach (string method in new[] { nameof(string.Contains), nameof(string.StartsWith), nameof(string.EndsWith) })
            {
                await AssertLiteralPatternQueryAsync(
                    context.Devices, devices, method, capturedPattern, constant: false, interceptor, deadline.Token);
            }

            // 独立 raw wire 合同：保留相同 NULL 守卫，以显式类型参数确认真实传输 NULL。
            using var parameterFactory = context.Database.GetDbConnection().CreateCommand();
            var parameter = parameterFactory.CreateParameter();
            parameter.ParameterName = "nullPattern";
            parameter.DbType = System.Data.DbType.String;
            parameter.Value = DBNull.Value;
            var wireQuery = context.Devices.FromSqlRaw(
                "SELECT \"Id\", \"Name\", \"Enabled\" FROM \"Devices\" " +
                "WHERE @nullPattern IS NOT NULL AND \"Name\" LIKE @nullPattern",
                parameter);
            interceptor.Reset();
            Assert.Empty(await wireQuery.OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync(deadline.Token));
            Assert.Contains(interceptor.ParameterValues, static value => value is null);
            Assert.Equal(0, await wireQuery.CountAsync(deadline.Token));
            Assert.Contains(interceptor.ParameterValues, static value => value is null);
            Assert.Empty(await wireQuery.OrderBy(item => item.Id).Skip(1).Take(2)
                .Select(item => item.Id).ToArrayAsync(deadline.Token));
            Assert.Contains(interceptor.ParameterValues, static value => value is null);

            var observed = requests.ToArray();
            Assert.NotEmpty(observed);
            Assert.All(observed, request =>
            {
                Assert.Equal("POST", request.Method);
                Assert.Equal(frameHttp2 ? "HTTP/2" : "HTTP/1.1", request.Protocol);
            });
        }
        finally
        {
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(cleanupDeadline.Token);
        }
    }

    private static Device[] CreateLiteralPatternDevices() =>
    [
        new() { Id = 1, Name = "100% Real" },
        new() { Id = 2, Name = "100X Real" },
        new() { Id = 3, Name = "A_B" },
        new() { Id = 4, Name = "AXB" },
        new() { Id = 5, Name = "path\\name" },
        new() { Id = 6, Name = "%leading" },
        new() { Id = 7, Name = "trailing%" },
        new() { Id = 8, Name = "_leading" },
        new() { Id = 9, Name = "trailing_" },
        new() { Id = 10, Name = "\\leading" },
        new() { Id = 11, Name = "trailing\\" },
        new() { Id = 12, Name = "\\%_" },
        new() { Id = 13, Name = "泵_%\\😀设备" },
        new() { Id = 14, Name = string.Empty },
        new() { Id = 15, Name = null! },
    ];

    private static IQueryable<Device> CreateLiteralPatternQuery(
        IQueryable<Device> source, string method, string? pattern, bool constant)
    {
        if (!constant)
        {
            return method switch
            {
                nameof(string.Contains) => source.Where(item => item.Name.Contains(pattern!)),
                nameof(string.StartsWith) => source.Where(item => item.Name.StartsWith(pattern!)),
                nameof(string.EndsWith) => source.Where(item => item.Name.EndsWith(pattern!)),
                _ => throw new ArgumentOutOfRangeException(nameof(method)),
            };
        }

        var item = Expression.Parameter(typeof(Device), "item");
        var predicate = Expression.Call(
            Expression.Property(item, nameof(Device.Name)),
            typeof(string).GetMethod(method, [typeof(string)])!,
            Expression.Constant(pattern, typeof(string)));
        return source.Where(Expression.Lambda<Func<Device, bool>>(predicate, item));
    }

    private static async Task AssertLiteralPatternQueryAsync(
        IQueryable<Device> source,
        Device[] devices,
        string method,
        string? pattern,
        bool constant,
        ReaderCommandCaptureInterceptor interceptor,
        CancellationToken cancellationToken)
    {
        var expected = devices.Where(item => item.Name is not null && pattern is not null && (method switch
        {
            nameof(string.Contains) => item.Name.Contains(pattern, StringComparison.Ordinal),
            nameof(string.StartsWith) => item.Name.StartsWith(pattern, StringComparison.Ordinal),
            nameof(string.EndsWith) => item.Name.EndsWith(pattern, StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        })).OrderBy(item => item.Id).Select(item => item.Id).ToArray();
        var query = CreateLiteralPatternQuery(source, method, pattern, constant);
        interceptor.Reset();
        var actual = await query.OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync(cancellationToken);

        Assert.Equal(expected, actual);
        if (!constant && !string.IsNullOrEmpty(pattern))
        {
            Assert.Contains(interceptor.ParameterValues, value => Equals(value, pattern));
        }

        Assert.Equal(expected.Length, await query.CountAsync(cancellationToken));
        Assert.Equal(
            expected.Skip(1).Take(2),
            await query.OrderBy(item => item.Id).Skip(1).Take(2).Select(item => item.Id).ToArrayAsync(cancellationToken));
    }

    /// <summary>
    /// 验证 C# 字符串加法使用 concat 生成并可由 SonnetDB 正常执行。
    /// </summary>
    [Fact]
    public async Task Query_StringConcatenation_UsesConcatAndExecutes()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");
        context.Devices.Add(new Device { Id = 1, Name = "pump", Enabled = true });
        await context.SaveChangesAsync();

        var query = context.Devices.Select(item => "device:" + item.Name + ":online");
        var sql = query.ToQueryString();
        var label = await query.SingleAsync();

        Assert.Contains("concat(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" + ", sql, StringComparison.Ordinal);
        Assert.Equal("device:pump:online", label);
    }

    [Fact]
    public async Task Query_SkipTakeProjection_Executes()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");

        context.Devices.AddRange(
            new Device { Id = 1, Name = "pump-001", Enabled = true },
            new Device { Id = 2, Name = "pump-002", Enabled = true },
            new Device { Id = 3, Name = "fan-001", Enabled = false });
        await context.SaveChangesAsync();

        var page = await context.Devices
            .Where(item => item.Enabled)
            .Skip(0)
            .Take(10)
            .Select(item => new { item.Id, item.Name })
            .ToArrayAsync();

        Assert.Equal([1L, 2L], page.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task Query_ConditionalProjection_UsesCaseExpressionAndExecutes()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");

        context.Devices.AddRange(
            new Device { Id = 1, Name = "pump-001", Enabled = true },
            new Device { Id = 2, Name = "pump-002", Enabled = false });
        await context.SaveChangesAsync();

        var sql = context.Devices
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                VisibleName = item.Enabled ? string.Empty : item.Name
            })
            .ToQueryString();

        Assert.Contains("CASE", sql, StringComparison.OrdinalIgnoreCase);

        var rows = await context.Devices
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                VisibleName = item.Enabled ? string.Empty : item.Name
            })
            .ToArrayAsync();

        Assert.Equal(string.Empty, rows[0].VisibleName);
        Assert.Equal("pump-002", rows[1].VisibleName);
    }

    [Fact]
    public async Task Query_SkipTakeProjection_WithNonZeroOffset_Executes()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");

        context.Devices.AddRange(
            new Device { Id = 1, Name = "pump-001", Enabled = true },
            new Device { Id = 2, Name = "pump-002", Enabled = true },
            new Device { Id = 3, Name = "fan-001", Enabled = true },
            new Device { Id = 4, Name = "meter-001", Enabled = false });
        await context.SaveChangesAsync();

        var sql = context.Devices
            .Where(item => item.Enabled)
            .OrderBy(item => item.Id)
            .Skip(1)
            .Take(1)
            .Select(item => new { item.Id, item.Name })
            .ToQueryString();

        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET", sql, StringComparison.OrdinalIgnoreCase);

        var page = await context.Devices
            .Where(item => item.Enabled)
            .OrderBy(item => item.Id)
            .Skip(1)
            .Take(1)
            .Select(item => new { item.Id, item.Name })
            .ToArrayAsync();

        var row = Assert.Single(page);
        Assert.Equal(2, row.Id);
        Assert.Equal("pump-002", row.Name);
    }

    [Fact]
    public async Task Query_ConstantFalseWhere_UsesBooleanLiteralAndExecutes()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");

        context.Devices.Add(new Device { Id = 1, Name = "pump-001", Enabled = true });
        await context.SaveChangesAsync();

        var sql = context.Devices
            .Where(_ => false)
            .ToQueryString();

        Assert.Contains("FALSE", sql, StringComparison.OrdinalIgnoreCase);

        var rows = await context.Devices
            .Where(_ => false)
            .ToArrayAsync();

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Query_StringToLower_UsesLowerFunctionAndExecutes()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");

        context.Devices.AddRange(
            new Device { Id = 1, Name = "Pump-001", Enabled = true },
            new Device { Id = 2, Name = "fan-001", Enabled = true });
        await context.SaveChangesAsync();

        var sql = context.Devices
            .Where(item => item.Name.ToLower() == "pump-001")
            .ToQueryString();

        Assert.Contains("LOWER", sql, StringComparison.OrdinalIgnoreCase);

        var device = await context.Devices.SingleAsync(item => item.Name.ToLower() == "pump-001");
        Assert.Equal(1, device.Id);
    }

    [Fact]
    public async Task Query_MultiColumnOrderByWithSkipTake_Executes()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");

        context.Devices.AddRange(
            new Device { Id = 1, Name = "pump", Enabled = true },
            new Device { Id = 2, Name = "fan", Enabled = true },
            new Device { Id = 3, Name = "pump", Enabled = true },
            new Device { Id = 4, Name = "meter", Enabled = false });
        await context.SaveChangesAsync();

        var page = await context.Devices
            .Where(item => item.Enabled)
            .OrderBy(item => item.Name)
            .ThenByDescending(item => item.Id)
            .Skip(0)
            .Take(10)
            .Select(item => new { item.Id, item.Name })
            .ToArrayAsync();

        Assert.Equal([2L, 3L, 1L], page.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task Query_FilteredIncludeProjectionWithSplitQuery_Executes()
    {
        using var context = new ProduceContext(
            new DbContextOptionsBuilder<ProduceContext>()
                .UseSonnetDB($"Data Source={_root}", options => options.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options);

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Produces\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Deleted\" BOOL NOT NULL, \"CustomerId\" INT NULL, \"TenantId\" INT NULL, PRIMARY KEY (\"Id\"))");
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Deleted\" BOOL NOT NULL, \"ProduceId\" INT NULL, PRIMARY KEY (\"Id\"), FOREIGN KEY (\"ProduceId\") REFERENCES \"Produces\" (\"Id\"))");

        context.Produces.AddRange(
            new Produce { Id = 1, Name = "alpha", Deleted = false },
            new Produce { Id = 2, Name = "beta", Deleted = false });
        context.Devices.AddRange(
            new ProduceDevice { Id = 10, Name = "a-1", Deleted = false, ProduceId = 1 },
            new ProduceDevice { Id = 11, Name = "a-2", Deleted = true, ProduceId = 1 },
            new ProduceDevice { Id = 20, Name = "b-1", Deleted = false, ProduceId = 2 });
        await context.SaveChangesAsync();

        var page = await context.Produces
            .Include(item => item.Devices.Where(device => !device.Deleted))
            .Where(item => !item.Deleted)
            .OrderBy(item => item.Name)
            .Skip(0)
            .Take(10)
            .Select(item => new ProduceListItem
            {
                Id = item.Id,
                Name = item.Name,
                Devices = item.Devices
            })
            .ToListAsync();

        Assert.Equal([1L, 2L], page.Select(item => item.Id).ToArray());
        Assert.Contains(page[0].Devices, device => device.Name == "a-1");
        Assert.Contains(page[1].Devices, device => device.Name == "b-1");
    }

    [Fact]
    public async Task Query_IoTSharpProduceListShape_Executes()
    {
        using var context = new ProduceContext(
            new DbContextOptionsBuilder<ProduceContext>()
                .UseSonnetDB($"Data Source={_root}", options => options.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options);

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Customers\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, PRIMARY KEY (\"Id\"))");
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Tenants\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, PRIMARY KEY (\"Id\"))");
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "Produces" (
                "Id" INT NOT NULL,
                "Name" STRING NOT NULL,
                "Deleted" BOOL NOT NULL,
                "CustomerId" INT NULL,
                "TenantId" INT NULL,
                PRIMARY KEY ("Id"),
                FOREIGN KEY ("CustomerId") REFERENCES "Customers" ("Id"),
                FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id"))
            """);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Deleted\" BOOL NOT NULL, \"ProduceId\" INT NULL, PRIMARY KEY (\"Id\"), FOREIGN KEY (\"ProduceId\") REFERENCES \"Produces\" (\"Id\"))");

        context.Customers.Add(new ProduceCustomer { Id = 100, Name = "customer" });
        context.Tenants.Add(new ProduceTenant { Id = 200, Name = "tenant" });
        context.Produces.AddRange(
            new Produce { Id = 1, Name = "alpha", Deleted = false, CustomerId = 100, TenantId = 200 },
            new Produce { Id = 2, Name = "beta", Deleted = false, CustomerId = 100, TenantId = 200 },
            new Produce { Id = 3, Name = "deleted", Deleted = true, CustomerId = 100, TenantId = 200 });
        context.Devices.AddRange(
            new ProduceDevice { Id = 10, Name = "a-1", Deleted = false, ProduceId = 1 },
            new ProduceDevice { Id = 11, Name = "a-2", Deleted = true, ProduceId = 1 },
            new ProduceDevice { Id = 20, Name = "b-1", Deleted = false, ProduceId = 2 });
        await context.SaveChangesAsync();

        var query = context.Produces.Include(item => item.Devices.Where(device => !device.Deleted));
        var total = await query.CountAsync(item =>
            item.Customer!.Id == 100 &&
            item.Tenant!.Id == 200 &&
            !item.Deleted);
        var page = await query
            .Where(item => item.Customer!.Id == 100 && item.Tenant!.Id == 200 && !item.Deleted)
            .Skip(0)
            .Take(20)
            .Select(item => new ProduceListItem
            {
                Id = item.Id,
                Name = item.Name,
                Devices = item.Devices
            })
            .ToListAsync();

        Assert.Equal(2, total);
        Assert.Equal([1L, 2L], page.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void MigrationsSqlGenerator_CreateAndRollback_GeneratesSonnetDbDdl()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var create = new CreateTableOperation
        {
            Name = "Devices",
            Columns =
            {
                new AddColumnOperation
                {
                    Table = "Devices",
                    Name = "Id",
                    ClrType = typeof(long),
                    ColumnType = "INT",
                    IsNullable = false
                },
                new AddColumnOperation
                {
                    Table = "Devices",
                    Name = "Name",
                    ClrType = typeof(string),
                    ColumnType = "STRING",
                    IsNullable = false
                }
            },
            PrimaryKey = new AddPrimaryKeyOperation
            {
                Table = "Devices",
                Columns = ["Id"]
            }
        };
        create.CheckConstraints.Add(new AddCheckConstraintOperation
        {
            Name = "CK_Devices_Name",
            Table = "Devices",
            Sql = "Name IN ('pump', 'fan')"
        });
        var drop = new DropTableOperation { Name = "Devices" };

        var upSql = Assert.Single(generator.Generate([create]));
        var downSql = Assert.Single(generator.Generate([drop]));

        Assert.Contains("CREATE TABLE \"Devices\"", upSql.CommandText, StringComparison.Ordinal);
        Assert.Contains("\"Id\" INT NOT NULL", upSql.CommandText, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (\"Id\")", upSql.CommandText, StringComparison.Ordinal);
        Assert.Contains(
            "CONSTRAINT \"CK_Devices_Name\" CHECK (Name IN ('pump', 'fan'))",
            upSql.CommandText,
            StringComparison.Ordinal);
        Assert.Contains("DROP TABLE IF EXISTS \"Devices\"", downSql.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationsSqlGenerator_DdlCommandsSuppressTransactionsButInsertDataDoesNot()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var createTable = new CreateTableOperation
        {
            Name = "Devices",
            Columns =
            {
                new AddColumnOperation
                {
                    Table = "Devices",
                    Name = "Id",
                    ClrType = typeof(long),
                    ColumnType = "INT",
                    IsNullable = false
                }
            }
        };
        var addColumn = new AddColumnOperation
        {
            Table = "Devices",
            Name = "Name",
            ClrType = typeof(string),
            ColumnType = "STRING",
            IsNullable = true
        };
        var alterColumn = new AlterColumnOperation
        {
            Table = "Devices",
            Name = "Name",
            ClrType = typeof(string),
            ColumnType = "STRING",
            IsNullable = false,
            DefaultValue = "unknown",
            OldColumn = new AddColumnOperation
            {
                Table = "Devices",
                Name = "Name",
                ClrType = typeof(string),
                ColumnType = "STRING",
                IsNullable = true
            }
        };
        var insertData = new InsertDataOperation
        {
            Table = "Devices",
            Columns = ["Id"],
            ColumnTypes = ["INT"],
            Values = new object[,] { { 1L } }
        };

        var commands = generator.Generate([createTable, addColumn, alterColumn, insertData]);

        Assert.Collection(
            commands,
            command =>
            {
                Assert.Contains("CREATE TABLE", command.CommandText, StringComparison.Ordinal);
                Assert.True(command.TransactionSuppressed);
            },
            command =>
            {
                Assert.Contains("ADD COLUMN", command.CommandText, StringComparison.Ordinal);
                Assert.True(command.TransactionSuppressed);
            },
            command =>
            {
                Assert.Contains("ALTER COLUMN", command.CommandText, StringComparison.Ordinal);
                Assert.True(command.TransactionSuppressed);
            },
            command =>
            {
                Assert.Contains("INSERT INTO", command.CommandText, StringComparison.Ordinal);
                Assert.False(command.TransactionSuppressed);
            });
    }

    [Fact]
    public void MigrationsSqlGenerator_CreateIndex_UsesIdempotentSonnetDbDdl()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var createIndex = new CreateIndexOperation
        {
            Name = "IX_Devices_Name",
            Table = "Devices",
            Columns = ["Name"]
        };

        var sql = Assert.Single(generator.Generate([createIndex]));

        Assert.Contains("CREATE INDEX IF NOT EXISTS \"IX_Devices_Name\" ON \"Devices\" (\"Name\")", sql.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationsSqlGenerator_DropForeignKey_GeneratesSonnetDbAlterTable()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var dropForeignKey = new DropForeignKeyOperation
        {
            Name = "FK_Device_AuthorizedKeys_AuthorizedKeyId",
            Table = "Device"
        };

        var sql = Assert.Single(generator.Generate([dropForeignKey]));

        Assert.Contains(
            "ALTER TABLE \"Device\" DROP CONSTRAINT \"FK_Device_AuthorizedKeys_AuthorizedKeyId\"",
            sql.CommandText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationsSqlGenerator_AddForeignKey_GeneratesSonnetDbAlterTable()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var addForeignKey = new AddForeignKeyOperation
        {
            Name = "FK_Device_AuthorizedKeys_AuthorizedKeyId",
            Table = "Device",
            Columns = ["AuthorizedKeyId"],
            PrincipalTable = "AuthorizedKeys",
            PrincipalColumns = ["Id"]
        };

        var sql = Assert.Single(generator.Generate([addForeignKey]));

        Assert.Contains(
            "ALTER TABLE \"Device\" ADD CONSTRAINT \"FK_Device_AuthorizedKeys_AuthorizedKeyId\" FOREIGN KEY (\"AuthorizedKeyId\") REFERENCES \"AuthorizedKeys\" (\"Id\")",
            sql.CommandText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationsSqlGenerator_AddDropCheckConstraint_GeneratesSonnetDbAlterTable()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var add = new AddCheckConstraintOperation
        {
            Name = "CK_Devices_Name",
            Table = "Devices",
            Sql = "Name IN ('pump', 'fan')"
        };
        var drop = new DropCheckConstraintOperation
        {
            Name = "CK_Devices_Name",
            Table = "Devices"
        };

        var addSql = Assert.Single(generator.Generate([add]));
        var dropSql = Assert.Single(generator.Generate([drop]));

        Assert.Contains(
            "ALTER TABLE \"Devices\" ADD CONSTRAINT \"CK_Devices_Name\" CHECK (Name IN ('pump', 'fan'))",
            addSql.CommandText,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TABLE \"Devices\" DROP CONSTRAINT \"CK_Devices_Name\"",
            dropSql.CommandText,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReferentialAction.Cascade, " ON DELETE CASCADE")]
    [InlineData(ReferentialAction.SetNull, " ON DELETE SET NULL")]
    [InlineData(ReferentialAction.Restrict, "")]
    [InlineData(ReferentialAction.NoAction, "")]
    public void MigrationsSqlGenerator_AddForeignKey_EmitsOnDeleteClause(
        ReferentialAction onDelete,
        string expectedClause)
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var addForeignKey = new AddForeignKeyOperation
        {
            Name = "FK_orders_customers",
            Table = "orders",
            Columns = ["customer_id"],
            PrincipalTable = "customers",
            PrincipalColumns = ["id"],
            OnDelete = onDelete
        };

        var sql = Assert.Single(generator.Generate([addForeignKey]));

        Assert.Contains(
            "ALTER TABLE \"orders\" ADD CONSTRAINT \"FK_orders_customers\" FOREIGN KEY (\"customer_id\") REFERENCES \"customers\" (\"id\")" + expectedClause,
            sql.CommandText,
            StringComparison.Ordinal);
        if (expectedClause.Length == 0)
            Assert.DoesNotContain("ON DELETE", sql.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationsSqlGenerator_DropIndex_IncludesTableName()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var dropIndex = new DropIndexOperation
        {
            Name = "IX_Device_AuthorizedKeyId",
            Table = "Device"
        };

        var sql = Assert.Single(generator.Generate([dropIndex]));

        Assert.Contains(
            "DROP INDEX \"IX_Device_AuthorizedKeyId\" ON \"Device\"",
            sql.CommandText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationsSqlGenerator_DropColumn_UsesIdempotentSonnetDbDdl()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var dropColumn = new DropColumnOperation
        {
            Name = "AuthorizedKeyId",
            Table = "Device"
        };

        var sql = Assert.Single(generator.Generate([dropColumn]));

        Assert.Contains(
            "ALTER TABLE \"Device\" DROP COLUMN IF EXISTS \"AuthorizedKeyId\"",
            sql.CommandText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationsSqlGenerator_AlterColumn_GeneratesTypeNullabilityAndDefault()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var alterColumn = new AlterColumnOperation
        {
            Table = "Devices",
            Name = "LegacyCode",
            ClrType = typeof(string),
            ColumnType = "STRING",
            IsNullable = true,
            DefaultValue = "unknown",
            OldColumn = new AddColumnOperation
            {
                Table = "Devices",
                Name = "LegacyCode",
                ClrType = typeof(long),
                ColumnType = "INT",
                IsNullable = false
            }
        };

        var sql = Assert.Single(generator.Generate([alterColumn]));

        Assert.Contains(
            "ALTER TABLE \"Devices\" ALTER COLUMN \"LegacyCode\" TYPE STRING NULL SET DEFAULT 'unknown'",
            sql.CommandText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationsSqlGenerator_AlterColumnWithoutDefault_GeneratesDropDefault()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var alterColumn = new AlterColumnOperation
        {
            Table = "Devices",
            Name = "Name",
            ClrType = typeof(string),
            ColumnType = "STRING",
            IsNullable = false,
            OldColumn = new AddColumnOperation
            {
                Table = "Devices",
                Name = "Name",
                ClrType = typeof(string),
                ColumnType = "STRING",
                IsNullable = true,
                DefaultValue = "legacy"
            }
        };

        var sql = Assert.Single(generator.Generate([alterColumn]));

        Assert.Contains(
            "ALTER TABLE \"Devices\" ALTER COLUMN \"Name\" TYPE STRING NOT NULL DROP DEFAULT",
            sql.CommandText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationsSqlGenerator_AlterColumnWithSqlDefault_EmitsRawDefaultExpression()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var alterColumn = new AlterColumnOperation
        {
            Table = "Devices",
            Name = "Retries",
            ClrType = typeof(long),
            ColumnType = "INT",
            IsNullable = false,
            DefaultValueSql = "42",
            OldColumn = new AddColumnOperation
            {
                Table = "Devices",
                Name = "Retries",
                ClrType = typeof(long),
                ColumnType = "INT",
                IsNullable = false,
                DefaultValue = 0L
            }
        };

        var sql = Assert.Single(generator.Generate([alterColumn]));

        Assert.Contains(
            "ALTER TABLE \"Devices\" ALTER COLUMN \"Retries\" TYPE INT NOT NULL SET DEFAULT 42",
            sql.CommandText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationsSqlGenerator_AlterComputedColumn_ThrowsNotSupported()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var alterColumn = new AlterColumnOperation
        {
            Table = "Devices",
            Name = "DisplayName",
            ClrType = typeof(string),
            ColumnType = "STRING",
            IsNullable = true,
            ComputedColumnSql = "Name"
        };

        var exception = Assert.Throws<NotSupportedException>(() => generator.Generate([alterColumn]));

        Assert.Contains("计算列", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DatabaseMigrate_WithHistoryTable_InitializesUpgradesRollsBackAndIsIdempotent()
    {
        using var context = new MigrationDeviceContext(CreateOptions<MigrationDeviceContext>());
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync("20260613000200_AddDeviceEnabled");

        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Devices\" (\"Id\", \"Name\", \"Enabled\") VALUES (1, 'pump', TRUE)");
        await context.Database.MigrateAsync();

        Assert.True(await HistoryTableExistsAsync(context, "__EFMigrationsHistory"));
        Assert.Equal(
            [
                "20260613000100_InitialDevices",
                "20260613000200_AddDeviceEnabled",
                "20260613000300_AddAndAlterDeviceRetryCount",
            ],
            (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.True(await ColumnExistsAsync(context, "Devices", "Enabled"));
        Assert.Equal(
            ("float64", true, "2.5"),
            await ReadColumnDefinitionAsync(context, "Devices", "RetryCount"));
        Assert.Equal(0d, await ReadDoubleAsync(context, "Devices", "RetryCount", 1));

        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Devices\" (\"Id\", \"Name\", \"Enabled\") VALUES (2, 'fan', FALSE)");
        Assert.Equal(2.5d, await ReadDoubleAsync(context, "Devices", "RetryCount", 2));

        await context.Database.MigrateAsync();
        Assert.Equal(3, await CountRowsAsync(context, "__EFMigrationsHistory"));

        await context.Database.ExecuteSqlRawAsync("DELETE FROM \"Devices\" WHERE \"Id\" >= 0");
        await migrator.MigrateAsync("20260613000100_InitialDevices");

        Assert.Equal(["20260613000100_InitialDevices"], (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.False(await ColumnExistsAsync(context, "Devices", "Enabled"));
        Assert.False(await ColumnExistsAsync(context, "Devices", "RetryCount"));

        await context.Database.MigrateAsync();
        Assert.Equal(
            [
                "20260613000100_InitialDevices",
                "20260613000200_AddDeviceEnabled",
                "20260613000300_AddAndAlterDeviceRetryCount",
            ],
            (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.True(await ColumnExistsAsync(context, "Devices", "Enabled"));
        Assert.Equal(
            ("float64", true, "2.5"),
            await ReadColumnDefinitionAsync(context, "Devices", "RetryCount"));
    }

    [Fact]
    public async Task DatabaseMigrate_WithConfiguredHistoryTable_UsesCustomHistoryTable()
    {
        using var context = new MigrationDeviceContext(
            new DbContextOptionsBuilder<MigrationDeviceContext>()
                .UseSonnetDB(
                    $"Data Source={_root}",
                    options => options.MigrationsHistoryTable("__SonnetHistory"))
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options);

        await context.Database.MigrateAsync();

        Assert.True(await HistoryTableExistsAsync(context, "__SonnetHistory"));
        Assert.False(await HistoryTableExistsAsync(context, "__EFMigrationsHistory"));
        Assert.Equal(3, await CountRowsAsync(context, "__SonnetHistory"));
    }

    [Fact]
    public async Task AdoSchemaMetadata_AfterEfDdl_ReportsTablesColumnsAndIndexes()
    {
        using var context = new DeviceContext(CreateOptions<DeviceContext>());

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");
        await context.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX \"UX_Devices_Name\" ON \"Devices\" (\"Name\")");

        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync();
        try
        {
            var tables = connection.GetSchema("Tables");
            Assert.Contains(
                tables.Rows.Cast<System.Data.DataRow>(),
                row => string.Equals((string)row["TABLE_NAME"], "Devices", StringComparison.Ordinal));

            var columns = connection.GetSchema("Columns", [null, null, "Devices", null]);
            Assert.Equal(["Id", "Name", "Enabled"], columns.Rows.Cast<System.Data.DataRow>().Select(row => (string)row["COLUMN_NAME"]).ToArray());

            var indexes = connection.GetSchema("Indexes", [null, null, "Devices", "UX_Devices_Name"]);
            var index = Assert.Single(indexes.Rows.Cast<System.Data.DataRow>());
            Assert.True((bool)index["IS_UNIQUE"]);
            Assert.Equal("Name", index["COLUMN_NAME"]);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task RemoteDatabaseCreator_CreateDeleteExists_UsesServerControlPlane()
    {
        const string token = "ef-remote-admin";
        const string database = "ef_remote_lifecycle";
        var dataRoot = Path.Combine(Path.GetTempPath(), "sndb-ef-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);

        await using var app = EfTestServerHost.Build(new ServerOptions
        {
            DataRoot = dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [token] = ServerRoles.Admin,
            },
        });

        try
        {
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
            var baseUrl = addresses.Addresses.First();
            var connectionString = $"Data Source=sonnetdb+http://{new Uri(baseUrl).Authority}/{database};Token={token};Timeout=30";

            using var context = new DeviceContext(
                new DbContextOptionsBuilder<DeviceContext>()
                    .UseSonnetDB(connectionString)
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                    .Options);

            var creator = context.Database.GetService<IRelationalDatabaseCreator>();
            Assert.False(await creator.ExistsAsync());

            await creator.CreateAsync();
            Assert.True(await creator.ExistsAsync());

            await context.Database.ExecuteSqlRawAsync(
                "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");
            Assert.True(await creator.HasTablesAsync());

            await creator.DeleteAsync();
            Assert.False(await creator.ExistsAsync());
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(dataRoot))
            {
                try { Directory.Delete(dataRoot, recursive: true); } catch { /* best-effort */ }
            }
        }
    }

    [Fact]
    public async Task RemoteDatabaseCreator_FrameHttp2_UsesExactHttp2ForServerControlPlane()
    {
        const string token = "ef-remote-h2-admin";
        const string database = "ef_remote_h2_lifecycle";
        var dataRoot = Path.Combine(Path.GetTempPath(), "sndb-ef-remote-h2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        var requests = new ConcurrentQueue<(string Method, string Path, string Protocol)>();

        await using var app = EfTestServerHost.Build(
            new ServerOptions
            {
                DataRoot = dataRoot,
                AutoLoadExistingDatabases = true,
                AllowAnonymousProbes = true,
                Tokens = new Dictionary<string, string>
                {
                    [token] = ServerRoles.Admin,
                },
            },
            extraArgs: ["--Kestrel:Endpoints:Http:Protocols=Http2"]);
        app.Use(async (context, next) =>
        {
            requests.Enqueue((
                context.Request.Method,
                context.Request.Path.Value ?? string.Empty,
                context.Request.Protocol));
            await next(context);
        });

        try
        {
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
            var baseUrl = Assert.Single(addresses.Addresses);
            var connectionString =
                $"Data Source=sonnetdb+http://{new Uri(baseUrl).Authority}/{database};" +
                $"Token={token};Timeout=30;Protocol=frame-http2";

            using var context = new DeviceContext(
                new DbContextOptionsBuilder<DeviceContext>()
                    .UseSonnetDB(connectionString)
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                    .Options);

            var creator = context.Database.GetService<IRelationalDatabaseCreator>();
            Assert.False(await creator.ExistsAsync());
            await creator.CreateAsync();
            Assert.True(await creator.ExistsAsync());
            await creator.DeleteAsync();
            Assert.False(await creator.ExistsAsync());

            var observed = requests.ToArray();
            Assert.Collection(
                observed,
                request => Assert.Equal(("GET", $"/v1/db/{database}/schema", "HTTP/2"), request),
                request => Assert.Equal(("POST", "/v1/db", "HTTP/2"), request),
                request => Assert.Equal(("GET", $"/v1/db/{database}/schema", "HTTP/2"), request),
                request => Assert.Equal(("DELETE", $"/v1/db/{database}", "HTTP/2"), request),
                request => Assert.Equal(("GET", $"/v1/db/{database}/schema", "HTTP/2"), request));
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(dataRoot))
            {
                try { Directory.Delete(dataRoot, recursive: true); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>
    /// Frame HTTP/2 远程 EF 在事务内外均使用真实影响行数，并对零行 UPDATE/DELETE 抛并发异常。
    /// </summary>
    [Fact]
    public async Task RemoteSaveChanges_FrameHttp2_UsesActualRecordsAffectedInsideAndOutsideTransaction()
    {
        const string token = "ef-remote-h2-concurrency";
        var database = "ef_remote_h2_concurrency_" + Guid.NewGuid().ToString("N");
        var dataRoot = Path.Combine(Path.GetTempPath(), "sndb-ef-remote-h2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        var requests = new ConcurrentQueue<(string Path, string Protocol)>();

        await using var app = EfTestServerHost.Build(
            new ServerOptions
            {
                DataRoot = dataRoot,
                AutoLoadExistingDatabases = true,
                AllowAnonymousProbes = true,
                Tokens = new Dictionary<string, string> { [token] = ServerRoles.Admin },
            },
            extraArgs: ["--Kestrel:Endpoints:Http:Protocols=Http2"]);
        app.Use(async (httpContext, next) =>
        {
            requests.Enqueue((httpContext.Request.Path.Value ?? string.Empty, httpContext.Request.Protocol));
            await next(httpContext);
        });

        try
        {
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
            var baseUrl = Assert.Single(addresses.Addresses);
            var options = new DbContextOptionsBuilder<DeviceContext>()
                .UseSonnetDB(
                    $"Data Source=sonnetdb+http://{new Uri(baseUrl).Authority}/{database};" +
                    $"Token={token};Timeout=30;Protocol=frame-http2")
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;

            await using var context = new DeviceContext(options);
            var creator = context.Database.GetService<IRelationalDatabaseCreator>();
            await creator.CreateAsync();
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, \"Enabled\" BOOL NOT NULL, PRIMARY KEY (\"Id\"))");

            var device = new Device { Id = 1, Name = "pump", Enabled = true };
            context.Devices.Add(device);
            Assert.Equal(1, context.SaveChanges());
            device.Name = "pump-outside";
            Assert.Equal(1, await context.SaveChangesAsync());

            var missing = new Device { Id = 404, Name = "missing", Enabled = false };
            context.Attach(missing);
            context.Remove(missing);
            Assert.Throws<DbUpdateConcurrencyException>(() => context.SaveChanges());
            context.ChangeTracker.Clear();

            await using var transaction = await context.Database.BeginTransactionAsync();
            device = await context.Devices.SingleAsync(item => item.Id == 1);
            device.Name = "pump-transaction";
            Assert.Equal(1, await context.SaveChangesAsync());

            var stale = new Device { Id = 405, Name = "stale", Enabled = false };
            context.Attach(stale);
            context.Entry(stale).Property(item => item.Name).IsModified = true;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync());
            await transaction.RollbackAsync();

            await using var verification = new DeviceContext(options);
            Assert.Equal("pump-outside", (await verification.Devices.SingleAsync(item => item.Id == 1)).Name);
            Assert.False(await verification.Devices.AnyAsync(item => item.Id == 404 || item.Id == 405));

            var observed = requests.ToArray();
            Assert.Contains(observed, request => request.Path == $"/v1/db/{database}/sql");
            var transactionPath = $"/v1/db/{database}/sql/transactions";
            Assert.Contains(observed, request => request.Path == transactionPath);
            var transactionSql = observed.Where(request =>
                request.Path.StartsWith(transactionPath + "/", StringComparison.Ordinal)
                && request.Path.EndsWith("/sql", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(transactionSql);
            var sessionPath = Assert.Single(transactionSql.Select(request => request.Path[..^4]).Distinct());
            Assert.Contains(observed, request => request.Path == sessionPath + "/rollback");
            Assert.All(observed, request => Assert.Equal("HTTP/2", request.Protocol));
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(dataRoot))
            {
                try { Directory.Delete(dataRoot, recursive: true); } catch { /* best-effort */ }
            }
        }
    }

    [Fact]
    public async Task RemoteDatabaseMigrate_WithMissingDatabase_CreatesDatabaseAndAppliesMigrations()
    {
        const string token = "ef-remote-admin";
        var database = "ef_remote_new_migration_" + Guid.NewGuid().ToString("N");
        var dataRoot = Path.Combine(Path.GetTempPath(), "sndb-ef-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);

        await using var app = EfTestServerHost.Build(new ServerOptions
        {
            DataRoot = dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [token] = ServerRoles.Admin,
            },
        });

        try
        {
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
            var baseUrl = addresses.Addresses.First();
            var connectionString = $"Data Source=sonnetdb+http://{new Uri(baseUrl).Authority}/{database};Token={token};Timeout=30";

            using var context = new MigrationDeviceContext(
                new DbContextOptionsBuilder<MigrationDeviceContext>()
                    .UseSonnetDB(connectionString)
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                    .Options);

            Assert.Equal(
                [
                    "20260613000100_InitialDevices",
                    "20260613000200_AddDeviceEnabled",
                    "20260613000300_AddAndAlterDeviceRetryCount",
                ],
                (await context.Database.GetPendingMigrationsAsync()).ToArray());

            await context.Database.MigrateAsync();

            var creator = context.Database.GetService<IRelationalDatabaseCreator>();
            Assert.True(await creator.ExistsAsync());
            Assert.True(await ColumnExistsAsync(context, "Devices", "Enabled"));
            Assert.Equal(
                ("float64", true, "2.5"),
                await ReadColumnDefinitionAsync(context, "Devices", "RetryCount"));
            Assert.Equal(3, await CountRowsAsync(context, "__EFMigrationsHistory"));

            await using var transaction = await context.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable);
            Assert.False(transaction.SupportsSavepoints);
            context.Devices.Add(new MigrationDevice { Id = 1, Name = "pump", Enabled = true });
            await context.SaveChangesAsync();
            context.Devices.Add(new MigrationDevice { Id = 2, Name = "fan", Enabled = false });
            await context.SaveChangesAsync();
            await transaction.CommitAsync();

            Assert.Equal(2, await context.Devices.CountAsync());
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(dataRoot))
            {
                try { Directory.Delete(dataRoot, recursive: true); } catch { /* best-effort */ }
            }
        }
    }

    [Fact]
    public async Task RemoteDatabaseMigrate_WithExistingHistoryOverFrameHttp2_SkipsAlreadyAppliedMigration()
    {
        const string token = "ef-remote-admin";
        var database = "ef_remote_migration_history_" + Guid.NewGuid().ToString("N");
        var dataRoot = Path.Combine(Path.GetTempPath(), "sndb-ef-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        var requests = new ConcurrentQueue<(string Method, string Path, string Protocol)>();

        await using var app = EfTestServerHost.Build(
            new ServerOptions
            {
                DataRoot = dataRoot,
                AutoLoadExistingDatabases = true,
                AllowAnonymousProbes = true,
                Tokens = new Dictionary<string, string>
                {
                    [token] = ServerRoles.Admin,
                },
            },
            extraArgs: ["--Kestrel:Endpoints:Http:Protocols=Http2"]);
        app.Use(async (context, next) =>
        {
            requests.Enqueue((
                context.Request.Method,
                context.Request.Path.Value ?? string.Empty,
                context.Request.Protocol));
            await next(context);
        });

        try
        {
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
            var baseUrl = addresses.Addresses.First();
            var connectionString =
                $"Data Source=sonnetdb+http://{new Uri(baseUrl).Authority}/{database};" +
                $"Token={token};Timeout=30;Protocol=frame-http2";

            using var context = new MigrationDeviceContext(
                new DbContextOptionsBuilder<MigrationDeviceContext>()
                    .UseSonnetDB(connectionString)
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                    .Options);

            var creator = context.Database.GetService<IRelationalDatabaseCreator>();
            await creator.CreateAsync();
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TABLE \"Devices\" (\"Id\" INT NOT NULL, \"Name\" STRING NOT NULL, PRIMARY KEY (\"Id\"))");
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" STRING NOT NULL, \"ProductVersion\" STRING NOT NULL, PRIMARY KEY (\"MigrationId\"))");
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20260613000100_InitialDevices', '10.0.9')");

            Assert.Equal(
                ["20260613000100_InitialDevices"],
                (await context.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Equal(
                [
                    "20260613000200_AddDeviceEnabled",
                    "20260613000300_AddAndAlterDeviceRetryCount",
                ],
                (await context.Database.GetPendingMigrationsAsync()).ToArray());

            await context.Database.MigrateAsync();

            Assert.True(await ColumnExistsAsync(context, "Devices", "Enabled"));
            Assert.Equal(
                ("float64", true, "2.5"),
                await ReadColumnDefinitionAsync(context, "Devices", "RetryCount"));
            Assert.Equal(3, await CountRowsAsync(context, "__EFMigrationsHistory"));

            var observed = requests.ToArray();
            Assert.Contains(observed, request => request.Method == "POST" && request.Path == "/v1/db");
            Assert.Contains(observed, request => request.Path == $"/v1/db/{database}/sql");
            Assert.All(observed, request => Assert.Equal("HTTP/2", request.Protocol));
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(dataRoot))
            {
                try { Directory.Delete(dataRoot, recursive: true); } catch { /* best-effort */ }
            }
        }
    }

    private DbContextOptions<TContext> CreateOptions<TContext>()
        where TContext : DbContext
        => new DbContextOptionsBuilder<TContext>()
            .UseSonnetDB($"Data Source={_root}")
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

    private static async Task<bool> HistoryTableExistsAsync(DbContext context, string tableName)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SHOW TABLES";
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(0), tableName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> ColumnExistsAsync(DbContext context, string tableName, string columnName)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"DESCRIBE TABLE \"{tableName}\"";
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(0), columnName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task<(string DataType, bool IsNullable, string? DefaultExpressionSql)> ReadColumnDefinitionAsync(
        DbContext context,
        string tableName,
        string columnName)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"DESCRIBE TABLE \"{tableName}\"";
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!string.Equals(reader.GetString(0), columnName, StringComparison.Ordinal))
                    continue;

                return (
                    reader.GetString(1),
                    reader.GetBoolean(2),
                    reader.IsDBNull(5) ? null : reader.GetString(5));
            }

            throw new InvalidOperationException($"table '{tableName}' 中不存在列 '{columnName}'。");
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task<double> ReadDoubleAsync(
        DbContext context,
        string tableName,
        string columnName,
        long id)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT \"{columnName}\" FROM \"{tableName}\" WHERE \"Id\" = {id}";
        await context.Database.OpenConnectionAsync();
        try
        {
            object? value = await command.ExecuteScalarAsync();
            return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task<int> CountRowsAsync(DbContext context, string tableName)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT \"MigrationId\" FROM \"{tableName}\"";
        await context.Database.OpenConnectionAsync();
        try
        {
            var count = 0;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                count++;
            }

            return count;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private sealed class GeneratedKeysContext(DbContextOptions<GeneratedKeysContext> options) : DbContext(options)
    {
        public DbSet<GeneratedIntEntity> GeneratedInts => Set<GeneratedIntEntity>();

        public DbSet<GeneratedLongEntity> GeneratedLongs => Set<GeneratedLongEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<GeneratedIntEntity>(entity =>
            {
                entity.ToTable("GeneratedInts");
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id).HasColumnType("INT");
                entity.Property(item => item.Name).HasColumnType("STRING").IsRequired();
            });

            modelBuilder.Entity<GeneratedLongEntity>(entity =>
            {
                entity.ToTable("GeneratedLongs");
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id).HasColumnType("INT").ValueGeneratedOnAdd();
                entity.Property(item => item.Name).HasColumnType("STRING").IsRequired();
            });
        }
    }

    private sealed class GeneratedIntEntity
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class GeneratedLongEntity
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class ManualKeyContext(DbContextOptions<ManualKeyContext> options) : DbContext(options)
    {
        public DbSet<ManualKeyEntity> Entities => Set<ManualKeyEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ManualKeyEntity>(entity =>
            {
                entity.ToTable("ManualKeys");
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id).HasColumnType("INT").ValueGeneratedNever();
                entity.Property(item => item.Name).HasColumnType("STRING").IsRequired();
            });
        }
    }

    private sealed class ManualKeyEntity
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class GeneratedRelationshipContext(DbContextOptions<GeneratedRelationshipContext> options)
        : DbContext(options)
    {
        public DbSet<GeneratedParent> Parents => Set<GeneratedParent>();

        public DbSet<GeneratedChild> Children => Set<GeneratedChild>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<GeneratedParent>(entity =>
            {
                entity.ToTable("GeneratedParents");
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id).HasColumnType("INT").ValueGeneratedOnAdd();
                entity.Property(item => item.Name).HasColumnType("STRING").IsRequired();
                entity.HasMany(item => item.Children)
                    .WithOne(item => item.Parent)
                    .HasForeignKey(item => item.ParentId)
                    .IsRequired();
            });

            modelBuilder.Entity<GeneratedChild>(entity =>
            {
                entity.ToTable("GeneratedChildren");
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id).HasColumnType("INT").ValueGeneratedNever();
                entity.Property(item => item.ParentId).HasColumnType("INT");
                entity.Property(item => item.Name).HasColumnType("STRING").IsRequired();
            });
        }
    }

    private sealed class GeneratedParent
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public List<GeneratedChild> Children { get; set; } = [];
    }

    private sealed class GeneratedChild
    {
        public long Id { get; set; }

        public long ParentId { get; set; }

        public GeneratedParent Parent { get; set; } = null!;

        public string Name { get; set; } = string.Empty;
    }

    private sealed class DeviceContext(DbContextOptions<DeviceContext> options) : DbContext(options)
    {
        public DbSet<Device> Devices => Set<Device>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Device>(entity =>
            {
                entity.ToTable("Devices");
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id).HasColumnType("INT").ValueGeneratedNever();
                entity.Property(item => item.Name).HasColumnType("STRING").IsRequired();
                entity.Property(item => item.Enabled).HasColumnType("BOOL");
            });
        }
    }

    /// <summary>为 AnyAsync NULL 回归提供可空索引键，而不改变通用 DeviceContext 合同。</summary>
    private sealed class NullableDeviceContext(DbContextOptions<NullableDeviceContext> options) : DbContext(options)
    {
        public DbSet<Device> Devices => Set<Device>();

        /// <summary>配置与 DeviceContext 相同的表，并仅把 Name 调整为可空列。</summary>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Device>(entity =>
            {
                entity.ToTable("Devices");
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id).HasColumnType("INT").ValueGeneratedNever();
                entity.Property(item => item.Name).HasColumnType("STRING").IsRequired(false);
                entity.Property(item => item.Enabled).HasColumnType("BOOL");
            });
        }
    }

    /// <summary>捕获 EF 实际交给 ADO.NET provider 的非查询命令。</summary>
    private sealed class CommandTimeoutCaptureInterceptor : DbCommandInterceptor
    {
        public DbCommand? LastCommand { get; private set; }

        /// <summary>捕获同步非查询命令。</summary>
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            LastCommand = command;
            return result;
        }

        /// <summary>捕获异步非查询命令。</summary>
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>捕获 EF 实际执行的查询命令及其参数快照。</summary>
    private sealed class ReaderCommandCaptureInterceptor : DbCommandInterceptor
    {
        public string CommandText { get; private set; } = string.Empty;

        public IReadOnlyList<object?> ParameterValues { get; private set; } = [];

        /// <summary>清空上一次命令快照，避免把准备数据阶段的命令误当成被测查询。</summary>
        public void Reset()
        {
            CommandText = string.Empty;
            ParameterValues = [];
        }

        /// <summary>捕获同步 reader 查询。</summary>
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Capture(command);
            return result;
        }

        /// <summary>捕获异步 reader 查询。</summary>
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(result);
        }

        /// <summary>捕获同步 scalar 查询，兼容 provider 对布尔终结操作的执行选择。</summary>
        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            Capture(command);
            return result;
        }

        /// <summary>捕获异步 scalar 查询，兼容 provider 对布尔终结操作的执行选择。</summary>
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(result);
        }

        /// <summary>复制命令文本和参数值，避免命令释放后测试再读取可变集合。</summary>
        private void Capture(DbCommand command)
        {
            CommandText = command.CommandText;
            ParameterValues = command.Parameters
                .Cast<DbParameter>()
                .Select(static parameter => parameter.Value is DBNull ? null : parameter.Value)
                .ToArray();
        }
    }

    private sealed class EventContext(DbContextOptions<EventContext> options) : DbContext(options)
    {
        public DbSet<EventRecord> Events => Set<EventRecord>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EventRecord>(entity =>
            {
                entity.ToTable("Events");
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id).HasColumnType("INT").ValueGeneratedNever();
                entity.Property(item => item.OccurredAt).HasColumnType("DATETIME");
            });
        }
    }

    private sealed class EventRecord
    {
        public long Id { get; set; }

        public DateTime OccurredAt { get; set; }
    }

    private sealed class OffsetEventContext(DbContextOptions<OffsetEventContext> options) : DbContext(options)
    {
        public DbSet<OffsetEventRecord> Events => Set<OffsetEventRecord>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<OffsetEventRecord>(entity =>
            {
                entity.ToTable("OffsetEvents");
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id).HasColumnType("INT").ValueGeneratedNever();
                entity.Property(item => item.OccurredAt).HasColumnType("DATETIME");
            });
        }
    }

    private sealed class OffsetEventRecord
    {
        public long Id { get; set; }

        public DateTimeOffset OccurredAt { get; set; }
    }

    private sealed class Device
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool Enabled { get; set; }
    }

    private sealed class ProduceContext(DbContextOptions<ProduceContext> options) : DbContext(options)
    {
        public DbSet<Produce> Produces => Set<Produce>();

        public DbSet<ProduceDevice> Devices => Set<ProduceDevice>();

        public DbSet<ProduceCustomer> Customers => Set<ProduceCustomer>();

        public DbSet<ProduceTenant> Tenants => Set<ProduceTenant>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ProduceCustomer>(entity =>
            {
                entity.ToTable("Customers");
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id).HasColumnType("INT").ValueGeneratedNever();
                entity.Property(item => item.Name).HasColumnType("STRING").IsRequired();
            });

            modelBuilder.Entity<ProduceTenant>(entity =>
            {
                entity.ToTable("Tenants");
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id).HasColumnType("INT").ValueGeneratedNever();
                entity.Property(item => item.Name).HasColumnType("STRING").IsRequired();
            });

            modelBuilder.Entity<Produce>(entity =>
            {
                entity.ToTable("Produces");
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id).HasColumnType("INT").ValueGeneratedNever();
                entity.Property(item => item.Name).HasColumnType("STRING").IsRequired();
                entity.Property(item => item.Deleted).HasColumnType("BOOL");
                entity.Property(item => item.CustomerId).HasColumnType("INT");
                entity.Property(item => item.TenantId).HasColumnType("INT");
                entity.HasMany(item => item.Devices)
                    .WithOne()
                    .HasForeignKey(item => item.ProduceId);
                entity.HasOne(item => item.Customer)
                    .WithMany()
                    .HasForeignKey(item => item.CustomerId);
                entity.HasOne(item => item.Tenant)
                    .WithMany()
                    .HasForeignKey(item => item.TenantId);
            });

            modelBuilder.Entity<ProduceDevice>(entity =>
            {
                entity.ToTable("Devices");
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id).HasColumnType("INT").ValueGeneratedNever();
                entity.Property(item => item.Name).HasColumnType("STRING").IsRequired();
                entity.Property(item => item.Deleted).HasColumnType("BOOL");
                entity.Property(item => item.ProduceId).HasColumnType("INT");
            });
        }
    }

    private sealed class Produce
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool Deleted { get; set; }

        public long? CustomerId { get; set; }

        public ProduceCustomer? Customer { get; set; }

        public long? TenantId { get; set; }

        public ProduceTenant? Tenant { get; set; }

        public List<ProduceDevice> Devices { get; set; } = [];
    }

    private sealed class ProduceCustomer
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class ProduceTenant
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class ProduceDevice
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool Deleted { get; set; }

        public long? ProduceId { get; set; }
    }

    private sealed class ProduceListItem
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public List<ProduceDevice> Devices { get; set; } = [];
    }

    private sealed class IdentitySubsetContext(DbContextOptions<IdentitySubsetContext> options) : DbContext(options)
    {
        public DbSet<IdentityUserSubset> Users => Set<IdentityUserSubset>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<IdentityUserSubset>(entity =>
            {
                entity.ToTable("AspNetUsers");
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id).HasColumnType("STRING").ValueGeneratedNever();
                entity.Property(item => item.UserName).HasColumnType("STRING");
                entity.Property(item => item.NormalizedUserName).HasColumnType("STRING");
                entity.Property(item => item.EmailConfirmed).HasColumnType("BOOL");
                entity.Property(item => item.ConcurrencyStamp).HasColumnType("STRING").IsConcurrencyToken();
            });
        }
    }

    private sealed class IdentityUserSubset
    {
        public string Id { get; set; } = string.Empty;

        public string? UserName { get; set; }

        public string? NormalizedUserName { get; set; }

        public bool EmailConfirmed { get; set; }

        public string? ConcurrencyStamp { get; set; }
    }
}

public sealed class MigrationDeviceContext(DbContextOptions<MigrationDeviceContext> options) : DbContext(options)
{
    public DbSet<MigrationDevice> Devices => Set<MigrationDevice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MigrationDevice>(entity =>
        {
            entity.ToTable("Devices");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnType("INT").ValueGeneratedNever();
            entity.Property(item => item.Name).HasColumnType("STRING").IsRequired();
            entity.Property(item => item.Enabled).HasColumnType("BOOL");
            entity.Property(item => item.RetryCount).HasColumnType("FLOAT");
        });
    }
}

public sealed class MigrationDevice
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public double? RetryCount { get; set; }
}

[DbContext(typeof(MigrationDeviceContext))]
[Migration("20260613000100_InitialDevices")]
public sealed class InitialDevices : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Devices",
            columns: table => new
            {
                Id = table.Column<long>(type: "INT", nullable: false),
                Name = table.Column<string>(type: "STRING", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Devices", x => x.Id));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable("Devices");
}

[DbContext(typeof(MigrationDeviceContext))]
[Migration("20260613000200_AddDeviceEnabled")]
public sealed class AddDeviceEnabled : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<bool>(
            name: "Enabled",
            table: "Devices",
            type: "BOOL",
            nullable: false,
            defaultValue: false);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn("Enabled", "Devices");
}

[DbContext(typeof(MigrationDeviceContext))]
[Migration("20260613000300_AddAndAlterDeviceRetryCount")]
public sealed class AddAndAlterDeviceRetryCount : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "RetryCount",
            table: "Devices",
            type: "INT",
            nullable: false,
            defaultValue: 0L);
        migrationBuilder.AlterColumn<double>(
            name: "RetryCount",
            table: "Devices",
            type: "FLOAT",
            nullable: true,
            defaultValue: 2.5d,
            oldClrType: typeof(long),
            oldType: "INT",
            oldNullable: false,
            oldDefaultValue: 0L);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<long>(
            name: "RetryCount",
            table: "Devices",
            type: "INT",
            nullable: false,
            defaultValue: 0L,
            oldClrType: typeof(double),
            oldType: "FLOAT",
            oldNullable: true,
            oldDefaultValue: 2.5d);
        migrationBuilder.DropColumn("RetryCount", "Devices");
    }
}

internal static class NamedForeignKeyNames
{
    internal const string Books = "A06 书\"籍";
    internal const string Authors = "A06 作\"者";
    internal const string ReplacementAuthors = "A06 替\"代作者";
    internal const string OriginalConstraint = "FK_书\"籍_作者";
    internal const string ReplacementConstraint = "FK_替\"代_作者";
    internal const string Id = "Id";
    internal const string AuthorId = "Author ID";
}

internal sealed class NamedForeignKeyMigrationContext(DbContextOptions<NamedForeignKeyMigrationContext> options) : DbContext(options)
{
    internal DbSet<NamedForeignKeyAuthor> Authors => Set<NamedForeignKeyAuthor>();
    internal DbSet<NamedForeignKeyReplacementAuthor> ReplacementAuthors => Set<NamedForeignKeyReplacementAuthor>();
    internal DbSet<NamedForeignKeyBook> Books => Set<NamedForeignKeyBook>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NamedForeignKeyAuthor>(entity =>
        {
            entity.ToTable(NamedForeignKeyNames.Authors);
            entity.HasKey(author => author.Id);
            entity.Property(author => author.Id).HasColumnType("INT").ValueGeneratedNever();
        });
        modelBuilder.Entity<NamedForeignKeyReplacementAuthor>(entity =>
        {
            entity.ToTable(NamedForeignKeyNames.ReplacementAuthors);
            entity.HasKey(author => author.Id);
            entity.Property(author => author.Id).HasColumnType("INT").ValueGeneratedNever();
        });
        modelBuilder.Entity<NamedForeignKeyBook>(entity =>
        {
            entity.ToTable(NamedForeignKeyNames.Books);
            entity.HasKey(book => book.Id);
            entity.Property(book => book.Id).HasColumnType("INT").ValueGeneratedNever();
            entity.Property(book => book.AuthorId).HasColumnName(NamedForeignKeyNames.AuthorId).HasColumnType("INT");
            entity.HasOne<NamedForeignKeyAuthor>().WithMany().HasForeignKey(book => book.AuthorId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName(NamedForeignKeyNames.OriginalConstraint);
        });
    }
}

internal sealed class NamedForeignKeyAuthor
{
    public long Id { get; set; }
}

internal sealed class NamedForeignKeyReplacementAuthor
{
    public long Id { get; set; }
}

internal sealed class NamedForeignKeyBook
{
    public long Id { get; set; }
    public long AuthorId { get; set; }
}

/// <summary>创建带命名外键的初始书籍及两种作者表。</summary>
[DbContext(typeof(NamedForeignKeyMigrationContext))]
[Migration(MigrationId)]
public sealed class NamedForeignKeyInitial : Migration
{
    internal const string MigrationId = "20261012010100_NamedForeignKeyInitial";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: NamedForeignKeyNames.Authors,
            columns: table => new { Id = table.Column<long>(type: "INT", nullable: false) },
            constraints: table => table.PrimaryKey("PK_A06Authors", row => row.Id));
        migrationBuilder.CreateTable(
            name: NamedForeignKeyNames.ReplacementAuthors,
            columns: table => new { Id = table.Column<long>(type: "INT", nullable: false) },
            constraints: table => table.PrimaryKey("PK_A06ReplacementAuthors", row => row.Id));
        migrationBuilder.CreateTable(
            name: NamedForeignKeyNames.Books,
            columns: table => new
            {
                Id = table.Column<long>(type: "INT", nullable: false),
                AuthorId = table.Column<long>(name: NamedForeignKeyNames.AuthorId, type: "INT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_A06Books", row => row.Id);
                table.ForeignKey(
                    name: NamedForeignKeyNames.OriginalConstraint,
                    column: row => row.AuthorId,
                    principalTable: NamedForeignKeyNames.Authors,
                    principalColumn: NamedForeignKeyNames.Id,
                    onDelete: ReferentialAction.Cascade);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(NamedForeignKeyNames.Books);
        migrationBuilder.DropTable(NamedForeignKeyNames.ReplacementAuthors);
        migrationBuilder.DropTable(NamedForeignKeyNames.Authors);
    }
}

/// <summary>按模型原名删除外键，回退时恢复原关系。</summary>
[DbContext(typeof(NamedForeignKeyMigrationContext))]
[Migration(MigrationId)]
public sealed class NamedForeignKeyRemoved : Migration
{
    internal const string MigrationId = "20261012010200_NamedForeignKeyRemoved";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropForeignKey(NamedForeignKeyNames.OriginalConstraint, NamedForeignKeyNames.Books);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddForeignKey(
            name: NamedForeignKeyNames.OriginalConstraint,
            table: NamedForeignKeyNames.Books,
            column: NamedForeignKeyNames.AuthorId,
            principalTable: NamedForeignKeyNames.Authors,
            principalColumn: NamedForeignKeyNames.Id,
            onDelete: ReferentialAction.Cascade);
}

/// <summary>以新名称及新引用表替换关系，回退时删除替换约束。</summary>
[DbContext(typeof(NamedForeignKeyMigrationContext))]
[Migration(MigrationId)]
public sealed class NamedForeignKeyReplaced : Migration
{
    internal const string MigrationId = "20261012010300_NamedForeignKeyReplaced";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddForeignKey(
            name: NamedForeignKeyNames.ReplacementConstraint,
            table: NamedForeignKeyNames.Books,
            column: NamedForeignKeyNames.AuthorId,
            principalTable: NamedForeignKeyNames.ReplacementAuthors,
            principalColumn: NamedForeignKeyNames.Id,
            onDelete: ReferentialAction.Cascade);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropForeignKey(NamedForeignKeyNames.ReplacementConstraint, NamedForeignKeyNames.Books);
}
