using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
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
        Assert.Contains("DROP TABLE \"Devices\"", downSql.CommandText, StringComparison.Ordinal);
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
