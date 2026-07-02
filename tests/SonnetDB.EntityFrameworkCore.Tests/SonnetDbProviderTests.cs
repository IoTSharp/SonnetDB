using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
        var drop = new DropTableOperation { Name = "Devices" };

        var upSql = Assert.Single(generator.Generate([create]));
        var downSql = Assert.Single(generator.Generate([drop]));

        Assert.Contains("CREATE TABLE \"Devices\"", upSql.CommandText, StringComparison.Ordinal);
        Assert.Contains("\"Id\" INT NOT NULL", upSql.CommandText, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (\"Id\")", upSql.CommandText, StringComparison.Ordinal);
        Assert.Contains("DROP TABLE IF EXISTS \"Devices\"", downSql.CommandText, StringComparison.Ordinal);
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
    public async Task DatabaseMigrate_WithHistoryTable_InitializesUpgradesRollsBackAndIsIdempotent()
    {
        using var context = new MigrationDeviceContext(CreateOptions<MigrationDeviceContext>());

        await context.Database.MigrateAsync();

        Assert.True(await HistoryTableExistsAsync(context, "__EFMigrationsHistory"));
        Assert.Equal(
            ["20260613000100_InitialDevices", "20260613000200_AddDeviceEnabled"],
            (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.True(await ColumnExistsAsync(context, "Devices", "Enabled"));

        await context.Database.MigrateAsync();
        Assert.Equal(2, await CountRowsAsync(context, "__EFMigrationsHistory"));

        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260613000100_InitialDevices");

        Assert.Equal(["20260613000100_InitialDevices"], (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.False(await ColumnExistsAsync(context, "Devices", "Enabled"));

        await context.Database.MigrateAsync();
        Assert.Equal(
            ["20260613000100_InitialDevices", "20260613000200_AddDeviceEnabled"],
            (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.True(await ColumnExistsAsync(context, "Devices", "Enabled"));
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
        Assert.Equal(2, await CountRowsAsync(context, "__SonnetHistory"));
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
    public async Task RemoteDatabaseMigrate_WithExistingHistorySkipsAlreadyAppliedMigration()
    {
        const string token = "ef-remote-admin";
        var database = "ef_remote_migration_history_" + Guid.NewGuid().ToString("N");
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
                ["20260613000200_AddDeviceEnabled"],
                (await context.Database.GetPendingMigrationsAsync()).ToArray());

            await context.Database.MigrateAsync();

            Assert.True(await ColumnExistsAsync(context, "Devices", "Enabled"));
            Assert.Equal(2, await CountRowsAsync(context, "__EFMigrationsHistory"));
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
                entity.Property(item => item.ConcurrencyStamp).HasColumnType("STRING");
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
        });
    }
}

public sealed class MigrationDevice
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; }
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
