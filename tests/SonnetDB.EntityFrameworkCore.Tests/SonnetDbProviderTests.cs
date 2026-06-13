using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
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

    private DbContextOptions<TContext> CreateOptions<TContext>()
        where TContext : DbContext
        => new DbContextOptionsBuilder<TContext>()
            .UseSonnetDB($"Data Source={_root}")
            .Options;

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
