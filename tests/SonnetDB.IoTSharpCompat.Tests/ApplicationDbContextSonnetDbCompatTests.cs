namespace SonnetDB.IoTSharpCompat.Tests;

using IoTSharp.Contracts;
using IoTSharp.Data;
using IoTSharp.Data.SonnetDB;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.EntityFrameworkCore.Extensions;
using Xunit;

public sealed class ApplicationDbContextSonnetDbCompatTests : IDisposable
{
    private readonly string _root;
    private readonly ServiceProvider _provider;

    public ApplicationDbContextSonnetDbCompatTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-iotsharp-" + Guid.NewGuid().ToString("N"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDataBaseModelBuilderOptions>(static _ => new SonnetDbModelBuilderOptions());
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSonnetDB($"Data Source={_root}", static builder =>
                builder.MigrationsAssembly("IoTSharp.Data.SonnetDB")));
        services.AddIdentity<IdentityUser, IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        _provider = services.BuildServiceProvider(validateScopes: true);
    }

    public void Dispose()
    {
        _provider.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore test cleanup failures */ }
    }

    [Fact]
    public async Task EnsureCreated_WithApplicationDbContext_CreatesIoTSharpSchema()
    {
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Assert.True(await context.Database.EnsureCreatedAsync());

        var tableNames = await ReadTableNamesAsync(context);
        Assert.Contains("AspNetUsers", tableNames);
        Assert.Contains("Tenant", tableNames);
        Assert.Contains("Customer", tableNames);
        Assert.Contains("Device", tableNames);
        Assert.Contains("Assets", tableNames);
        Assert.Contains("FlowRules", tableNames);
        Assert.Contains("DeviceRules", tableNames);
    }

    [Fact]
    public async Task MigrationsHistory_WithApplicationDbContext_SupportsDefaultHistoryTableCreationAndIdempotency()
    {
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var history = context.GetService<Microsoft.EntityFrameworkCore.Migrations.IHistoryRepository>();

        Assert.False(await history.ExistsAsync());
        await history.CreateIfNotExistsAsync();
        Assert.True(await history.ExistsAsync());
        Assert.Empty(await history.GetAppliedMigrationsAsync());

        await history.CreateIfNotExistsAsync();
        var tableNames = await ReadTableNamesAsync(context);
        Assert.Contains("__EFMigrationsHistory", tableNames);
        Assert.Equal(1, tableNames.Count(item => item == "__EFMigrationsHistory"));
    }

    [Fact]
    public async Task Migrate_WithSonnetDbBaseline_CreatesIoTSharpSchemaAndRecordsMigration()
    {
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await context.Database.MigrateAsync();

        var tableNames = await ReadTableNamesAsync(context);
        Assert.Contains("__EFMigrationsHistory", tableNames);
        Assert.Contains("AspNetUsers", tableNames);
        Assert.Contains("Tenant", tableNames);
        Assert.Contains("Customer", tableNames);
        Assert.Contains("Device", tableNames);
        Assert.Contains("Assets", tableNames);
        Assert.Contains("FlowRules", tableNames);

        var migrations = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(
            [
                "20260613145712_InitialSonnetDbApplicationDbContext",
                "20260702090000_AddDeviceLatestQueryIndexes"
            ],
            migrations);

        await context.Database.MigrateAsync();
        Assert.Equal(migrations, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
    }

    [Fact]
    public async Task Identity_WithApplicationDbContext_CanCreateUserAndVerifyPassword()
    {
        await EnsureSchemaAsync();
        await using var scope = _provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        var user = new IdentityUser
        {
            UserName = "alice",
            Email = "alice@example.test"
        };

        var create = await users.CreateAsync(user, "P@ssword1!");
        Assert.True(create.Succeeded, string.Join("; ", create.Errors.Select(static x => x.Description)));

        var found = await users.FindByNameAsync("alice");
        Assert.NotNull(found);
        Assert.True(await users.CheckPasswordAsync(found, "P@ssword1!"));
        Assert.False(await users.CheckPasswordAsync(found, "wrong-password"));
    }

    [Fact]
    public async Task SaveChanges_WithApplicationRelationship_GeneratesGuidPrimaryKey()
    {
        await EnsureSchemaAsync();
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "tenant-generated",
            Email = "tenant-generated@example.test"
        };
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Name = "customer-generated",
            Email = "customer-generated@example.test",
            Tenant = tenant
        };
        context.AddRange(tenant, customer);
        await context.SaveChangesAsync();

        var user = new IdentityUser
        {
            UserName = "relationship@example.test",
            Email = "relationship@example.test"
        };
        var created = await users.CreateAsync(user, "P@ssword1!");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(static item => item.Description)));

        var relationship = new Relationship
        {
            IdentityUser = user,
            Tenant = tenant,
            Customer = customer
        };

        context.Add(relationship);
        await context.SaveChangesAsync();

        Assert.NotEqual(Guid.Empty, relationship.Id);
        Assert.Equal(relationship.Id, await context.Relationship.Select(item => item.Id).SingleAsync());
    }

    [Fact]
    public async Task ControlPlaneCrud_WithApplicationDbContext_SupportsIncludesPagingQueriesAndDelete()
    {
        await EnsureSchemaAsync();
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "tenant-a",
            Email = "tenant@example.test",
            Phone = "10086",
            Country = "CN",
            Province = "JS",
            City = "Suzhou",
            Street = "Industry",
            Address = "No.1",
            ZipCode = 215000
        };
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Name = "customer-a",
            Email = "customer@example.test",
            Phone = "10010",
            Country = "CN",
            Province = "JS",
            City = "Suzhou",
            Street = "Science",
            Address = "No.2",
            ZipCode = 215000,
            Tenant = tenant
        };
        var device = new Device
        {
            Id = Guid.NewGuid(),
            Name = "pump-001",
            DeviceType = DeviceType.Device,
            Timeout = 60,
            Tenant = tenant,
            Customer = customer,
            DeviceIdentity = new DeviceIdentity
            {
                Id = Guid.NewGuid(),
                IdentityType = IdentityType.AccessToken,
                IdentityId = "token-pump-001"
            }
        };
        var asset = new Asset
        {
            Id = Guid.NewGuid(),
            Name = "line-a",
            Description = "production line",
            AssetType = "line",
            Tenant = tenant,
            Customer = customer
        };
        var rule = new FlowRule
        {
            RuleId = Guid.NewGuid(),
            RuleType = RuleType.RuleNode,
            Name = "high-temp",
            Describes = "alarm",
            Runner = "builtin",
            ExecutableCode = "{}",
            Creator = "tester",
            RuleDesc = "temperature alarm",
            CreatTime = DateTime.UtcNow,
            DefinitionsXml = "<rule />",
            Version = 1,
            SubVersion = 0,
            MountType = EventType.Telemetry,
            Tenant = tenant,
            Customer = customer
        };
        var deviceRule = new DeviceRule
        {
            DeviceRuleId = Guid.NewGuid(),
            Device = device,
            FlowRule = rule,
            ConfigUser = Guid.NewGuid(),
            ConfigDateTime = DateTime.UtcNow,
            EnableTrace = 1
        };

        context.AddRange(tenant, customer, device, asset, rule, deviceRule);
        await context.SaveChangesAsync();

        var deviceQuery = context.Device
            .Include(item => item.Tenant)
            .Include(item => item.Customer)
            .Include(item => item.DeviceIdentity)
            .Where(item => item.Name == "pump-001");
        var loaded = await deviceQuery.SingleAsync();

        Assert.Equal("tenant-a", loaded.Tenant.Name);
        Assert.Equal("customer-a", loaded.Customer.Name);
        Assert.Equal("token-pump-001", loaded.DeviceIdentity.IdentityId);

        var paged = await context.Device
            .OrderBy(item => item.Name)
            .Skip(0)
            .Take(10)
            .Select(item => item.Name)
            .ToListAsync();
        Assert.Equal(["pump-001"], paged);

        var deviceListProjection = await context.Device
            .Include(item => item.DeviceIdentity)
            .Where(item => item.Customer.Id == customer.Id && !item.Deleted && item.Tenant.Id == tenant.Id)
            .Skip(0)
            .Take(10)
            .Select(item => new
            {
                item.Id,
                item.Name,
                IdentityId = item.DeviceIdentity.IdentityId,
                IdentityType = item.DeviceIdentity.IdentityType,
                TenantId = item.Tenant.Id,
                TenantName = item.Tenant.Name,
                CustomerId = item.Customer.Id,
                CustomerName = item.Customer.Name,
                item.Timeout
            })
            .ToListAsync();
        var projected = Assert.Single(deviceListProjection);
        Assert.Equal("pump-001", projected.Name);
        Assert.Equal("token-pump-001", projected.IdentityId);
        Assert.Equal("tenant-a", projected.TenantName);
        Assert.Equal("customer-a", projected.CustomerName);

        var assetQuery = context.Assets.Where(item => item.Tenant == tenant && item.Name == "line-a");
        Assert.True(await assetQuery.AnyAsync());
        Assert.Equal(1, await context.FlowRules.CountAsync(item => item.MountType == EventType.Telemetry));
        Assert.Equal(1, await context.DeviceRules.Include(item => item.FlowRule).CountAsync(item => item.FlowRule.Name == "high-temp"));

        loaded.Name = "pump-001-renamed";
        await context.SaveChangesAsync();
        Assert.Equal("pump-001-renamed", await context.Device.Select(item => item.Name).SingleAsync());

        context.Remove(deviceRule);
        context.Remove(asset);
        await context.SaveChangesAsync();

        Assert.Empty(await context.DeviceRules.ToListAsync());
        Assert.Empty(await context.Assets.ToListAsync());
    }

    [Fact]
    public async Task SaveChanges_WhenUniqueIdentityConstraintFails_RollsBackAllPendingChanges()
    {
        await EnsureSchemaAsync();
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        context.Users.AddRange(
            new IdentityUser
            {
                Id = Guid.NewGuid().ToString("N"),
                UserName = "duplicate-a",
                NormalizedUserName = "DUPLICATE",
                Email = "a@example.test",
                NormalizedEmail = "A@EXAMPLE.TEST",
                SecurityStamp = Guid.NewGuid().ToString("N")
            },
            new IdentityUser
            {
                Id = Guid.NewGuid().ToString("N"),
                UserName = "duplicate-b",
                NormalizedUserName = "DUPLICATE",
                Email = "b@example.test",
                NormalizedEmail = "B@EXAMPLE.TEST",
                SecurityStamp = Guid.NewGuid().ToString("N")
            });

        await Assert.ThrowsAnyAsync<Exception>(() => context.SaveChangesAsync());

        context.ChangeTracker.Clear();
        Assert.Equal(0, await context.Users.CountAsync());
    }

    [Fact]
    [Trait("Category", "Documentation")]
    public void UnsupportedList_ForApplicationDbContextCompat_DocumentsCurrentBoundaries()
    {
        // 文档表一致性检查；不运行 SonnetDB。
        var unsupported = IoTSharpCompatMatrix.RelationalSonnetDbUnsupported;

        Assert.Contains(unsupported, item => item.Contains("HealthChecks UI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(unsupported, item => item.Contains("migrations history has no", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(unsupported, item => item.Contains("distributed cross-process migration locking", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(unsupported, item => item.Contains("production migration baseline", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(unsupported, item => item.Contains("StartsWith", StringComparison.OrdinalIgnoreCase));
    }

    private async Task EnsureSchemaAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
    }

    private static async Task<HashSet<string>> ReadTableNamesAsync(ApplicationDbContext context)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SHOW TABLES";
        await context.Database.OpenConnectionAsync();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        await context.Database.CloseConnectionAsync();
        return names;
    }
}
