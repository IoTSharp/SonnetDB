using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using SonnetDB.EntityFrameworkCore.Extensions;

if (args.Length != 2 || args[0] is not ("seed" or "update" or "verify" or "verify-rejected"))
    throw new ArgumentException("Usage: Issue395.JsonApp seed|update|verify|verify-rejected <absolute database directory>. Set SONNETDB_APP_CONNECTION for a remote database.");
var phase = args[0];
var database = Path.GetFullPath(args[1]);
if (!Path.IsPathFullyQualified(args[1]))
    throw new ArgumentException("Database directory must be absolute.");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
var token = timeout.Token;
var capture = new CommandCapture();
var connection = Environment.GetEnvironmentVariable("SONNETDB_APP_CONNECTION") ?? "Data Source=" + database;
var options = new DbContextOptionsBuilder<ProductContext>().UseSonnetDB(connection).AddInterceptors(capture).Options;
await using var context = new ProductContext(options);
if (phase == "seed")
{
    await context.Database.EnsureCreatedAsync(token);
    context.Add(new Product
    {
        Id = 1, Sku = "PUMP-395", RowVersion = 0,
        Attributes = new ProductAttributes
        {
            OnHand = 12, Labels = ["industrial", "旧标签"],
            Replenishment = new ReplenishmentPolicy { Minimum = 5, Target = 20, Reason = "original" },
            Offers = [new SupplierOffer { Vendor = "原供应商", Price = 11.25m }],
        },
    });
    Check(await context.SaveChangesAsync(token) == 1, "seed row count");
    var raw = await ReadRawAsync(context, token);
    var withUnknown = raw[..^1] + ",\"Unmanaged\":{\"externalId\":9007199254740993,\"calibration\":1.2300e+04,\"offset\":\"+05:30\",\"escaped\":\"\\u4e2d\"}}";
    Check(await context.Database.ExecuteSqlInterpolatedAsync(
        $"UPDATE \"ProductProfiles\" SET \"Attributes\"={withUnknown} WHERE \"Id\"=1", token) == 1, "seed unmanaged data");
}
else if (phase == "update")
{
    // The business command reads no entity before updating: version and SKU are explicit inputs.
    var policy = new ReplenishmentPolicy { Minimum = 15, Target = 50, Reason = "补货 O'Reilly 中文😃" };
    var labels = new List<string> { "preferred", "自动补货", "quote'\\\"" };
    var id = Guid.Parse("98df7bba-33b7-4db3-82f6-c7f3401b0395");
    var when = new DateTimeOffset(2026, 11, 6, 14, 15, 16, TimeSpan.FromHours(5.5)).AddTicks(1234567);
    var before = capture.Commands.Count;
    Check(await context.Products.Where(product => product.Sku == "PUMP-395" && product.RowVersion == 0)
        .ExecuteUpdateAsync(setters => setters
            .SetProperty(product => product.Attributes.Replenishment, policy)
            .SetProperty(product => product.Attributes.Labels, labels)
            .SetProperty(product => product.Attributes.Offers[0].Vendor, "新供应商")
            .SetProperty(product => product.Attributes.Offers[0].Price, 10.125m)
            .SetProperty(product => product.Attributes.Replenishment.Minimum, product => product.Attributes.OnHand + 3)
            .SetProperty(product => product.Attributes.CommandId, id)
            .SetProperty(product => product.Attributes.UpdatedAt, when)
            .SetProperty(product => product.RowVersion, product => product.RowVersion + 1), token) == 1, "business update affected row");
    Check(capture.Commands.Count - before == 1, "exactly one SQL command, no SELECT/read-modify-write");
    Check(capture.Commands[^1].Sql.StartsWith("UPDATE", StringComparison.Ordinal), "server UPDATE");
    Check(capture.Commands[^1].Parameters > 0, "parameterized update");
    Check(!capture.Commands[^1].Sql.Contains(policy.Reason, StringComparison.Ordinal), "value is not SQL text");
    Check(await context.Products.Where(product => product.Id == 1 && product.RowVersion == 0)
        .ExecuteUpdateAsync(setters => setters.SetProperty(product => product.Attributes.OnHand, 999), token) == 0, "stale command rejected by explicit rowversion");
    await using var transaction = await context.Database.BeginTransactionAsync(token);
    Check(await context.Products.Where(product => product.Id == 1).ExecuteUpdateAsync(setters => setters
        .SetProperty(product => product.Attributes.Replenishment,
            new ReplenishmentPolicy { Minimum = 999, Target = 999, Reason = "rollback" }), token) == 1, "transaction update");
    await transaction.RollbackAsync(token);
}
else if (phase == "verify-rejected")
{
    var product = await context.Products.AsNoTracking().SingleAsync(product => product.Id == 1, token);
    Check(product.RowVersion == 0 && product.Attributes.OnHand == 12, "rejected old-provider command did not update the row");
    Check(product.Attributes.Replenishment.Minimum == 5 && product.Attributes.Replenishment.Target == 20, "rejected command preserved policy");
    Check(product.Attributes.Labels.SequenceEqual(["industrial", "旧标签"]), "rejected command preserved labels");
    Check(product.Attributes.Offers[0].Vendor == "原供应商" && product.Attributes.Offers[0].Price == 11.25m, "rejected command preserved offers");
}
else
{
    var product = await context.Products.AsNoTracking().SingleAsync(product => product.Id == 1, token);
    Check(product.RowVersion == 1, "persisted explicit rowversion");
    Check(product.Attributes.OnHand == 12, "preserved stock");
    Check(product.Attributes.Replenishment.Minimum == 15 && product.Attributes.Replenishment.Target == 50, "replenishment policy after rollback and reopen");
    Check(product.Attributes.Replenishment.Reason == "补货 O'Reilly 中文😃", "model JSON name and Unicode");
    Check(product.Attributes.Labels.SequenceEqual(["preferred", "自动补货", "quote'\\\""]), "array replacement");
    Check(product.Attributes.Offers[0].Vendor == "新供应商" && product.Attributes.Offers[0].Price == 10.125m, "supplier scalar and decimal JSON codec");
    Check(product.Attributes.CommandId == Guid.Parse("98df7bba-33b7-4db3-82f6-c7f3401b0395"), "Guid codec");
    Check(product.Attributes.UpdatedAt == new DateTimeOffset(2026, 11, 6, 14, 15, 16, TimeSpan.FromHours(5.5)).AddTicks(1234567), "offset/ticks codec");
    using var json = JsonDocument.Parse(await ReadRawAsync(context, token));
    Check(json.RootElement.GetProperty("Unmanaged").GetRawText() == "{\"externalId\":9007199254740993,\"calibration\":1.2300e+04,\"offset\":\"+05:30\",\"escaped\":\"\\u4e2d\"}", "unknown external JSON preserved exactly");
}

using (var writer = new Utf8JsonWriter(Console.OpenStandardOutput()))
{
    writer.WriteStartObject();
    writer.WriteString("status", "PASS");
    writer.WriteString("phase", phase);
    writer.WriteNumber("capturedCommands", capture.Commands.Count);
    writer.WriteStartArray("commands");
    foreach (var command in capture.Commands.Take(64))
    {
        writer.WriteStartObject();
        writer.WriteString("sql", command.Sql);
        writer.WriteNumber("parameters", command.Parameters);
        writer.WriteEndObject();
    }
    writer.WriteEndArray();
    writer.WriteStartArray("assemblies");
    foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "SonnetDB*.dll").Take(16))
    {
        writer.WriteStartObject();
        writer.WriteString("path", Path.GetFullPath(path));
        using var file = File.OpenRead(path);
        writer.WriteString("sha256", Convert.ToHexStringLower(SHA256.HashData(file)));
        writer.WriteEndObject();
    }
    writer.WriteEndArray();
    writer.WriteEndObject();
}

static void Check(bool condition, string operation)
{
    if (!condition)
        throw new InvalidOperationException("Business acceptance failed: " + operation);
}

static async Task<string> ReadRawAsync(ProductContext context, CancellationToken token)
{
    await context.Database.OpenConnectionAsync(token);
    try
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT \"Attributes\" FROM \"ProductProfiles\" WHERE \"Id\"=1";
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        return (string)(await command.ExecuteScalarAsync(token))!;
    }
    finally { await context.Database.CloseConnectionAsync(); }
}

internal sealed class ProductContext(DbContextOptions<ProductContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var products = modelBuilder.Entity<Product>();
        products.ToTable("ProductProfiles");
        products.Property(product => product.Id).ValueGeneratedNever();
        products.Property(product => product.RowVersion).IsConcurrencyToken().ValueGeneratedNever();
        products.OwnsOne(product => product.Attributes, attributes =>
        {
            attributes.ToJson("Attributes");
            attributes.Property(value => value.Labels);
            attributes.OwnsOne(value => value.Replenishment, policy =>
                policy.Property(value => value.Reason).HasJsonPropertyName("原因'.with.dot 😃"));
            attributes.OwnsMany(value => value.Offers);
        });
    }
}

internal sealed class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = string.Empty;
    public long RowVersion { get; set; }
    public ProductAttributes Attributes { get; set; } = new();
}

internal sealed class ProductAttributes
{
    public int OnHand { get; set; }
    public Guid CommandId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<string> Labels { get; set; } = [];
    public ReplenishmentPolicy Replenishment { get; set; } = new();
    public List<SupplierOffer> Offers { get; set; } = [];
}

internal sealed class ReplenishmentPolicy
{
    public int Minimum { get; set; }
    public int Target { get; set; }
    public string Reason { get; set; } = string.Empty;
}

internal sealed class SupplierOffer
{
    public string Vendor { get; set; } = string.Empty;
    public decimal Price { get; set; }
}

internal sealed class CommandCapture : DbCommandInterceptor
{
    public List<(string Sql, int Parameters)> Commands { get; } = [];

    private void Capture(DbCommand command)
    {
        if (Commands.Count >= 64)
            throw new InvalidOperationException("Command budget exceeded.");
        Commands.Add((command.CommandText, command.Parameters.Count));
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    { Capture(command); return result; }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Capture(command); return ValueTask.FromResult(result); }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { Capture(command); return ValueTask.FromResult(result); }
}
