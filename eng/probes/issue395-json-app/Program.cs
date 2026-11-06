using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using SonnetDB.Data;
using SonnetDB.EntityFrameworkCore.Extensions;

if (args.Length != 2 || args[0] is not ("seed" or "update" or "reject" or "verify" or "compete" or "verify-concurrency" or "verify-rejected"))
    throw new ArgumentException("Usage: Issue395.JsonApp seed|update|reject|verify|compete|verify-concurrency|verify-rejected <absolute database directory>. Set SONNETDB_APP_CONNECTION for a remote database.");
var phase = args[0];
var database = Path.GetFullPath(args[1]);
if (!Path.IsPathFullyQualified(args[1]))
    throw new ArgumentException("Database directory must be absolute.");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
var token = timeout.Token;
var capture = new CommandCapture();
var checks = new List<string>();
var rejections = new List<(string Operation, string Type, string Message, string? ServerError, string? ServerMessage, int SqlCommands)>();
int[] concurrentOutcomes = [];
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
            OnHand = 12, Labels = ["industrial", "旧标签"], OptionalCommandId = Guid.Parse("98df7bba-33b7-4db3-82f6-c7f3401b0395"),
            Replenishment = new ReplenishmentPolicy { Minimum = 5, Target = 20, Reason = "original" },
            Offers = [new SupplierOffer { Vendor = "原供应商", Price = 11.25m, ContractTag = "original" }],
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
    var policy = new ReplenishmentPolicy { Minimum = 99, Target = 50, Reason = "补货 O'Reilly 中文😃" };
    var labels = new List<string> { "preferred", "自动补货", "quote'\\\"" };
    var offer = new SupplierOffer { Vendor = "新供应商", Price = 99m, ContractTag = "replacement" };
    var id = Guid.Parse("98df7bba-33b7-4db3-82f6-c7f3401b0395");
    var when = new DateTimeOffset(2026, 11, 6, 14, 15, 16, TimeSpan.FromHours(5.5)).AddTicks(1234567);
    var before = capture.Commands.Count;
    Check(await context.Products.Where(product => product.Sku == "PUMP-395" && product.RowVersion == 0)
        .ExecuteUpdateAsync(setters => setters
            .SetProperty(product => product.Attributes.Replenishment, policy)
            .SetProperty(product => product.Attributes.Labels, labels)
            .SetProperty(product => product.Attributes.Labels[0], "preferred-reviewed")
            .SetProperty(product => product.Attributes.Offers[0], offer)
            .SetProperty(product => product.Attributes.Offers[0].Price, 10.125m)
            .SetProperty(product => product.Attributes.Offers[0].ContractTag, "MiXeD")
            .SetProperty(product => product.Attributes.OnHand, product => product.Attributes.OnHand + 2)
            .SetProperty(product => product.Attributes.Replenishment.Minimum, product => product.Attributes.OnHand + 3)
            .SetProperty(product => product.Attributes.Replenishment.Reason, policy.Reason)
            .SetProperty(product => product.Attributes.PriorVendor, product => product.Attributes.Offers[0].Vendor)
            .SetProperty(product => product.Attributes.CommandId, id)
            .SetProperty(product => product.Attributes.OptionalCommandId, (Guid?)null)
            .SetProperty(product => product.Attributes.UpdatedAt, when)
            .SetProperty(product => product.RowVersion, product => product.RowVersion + 1), token) == 1, "business update affected row");
    Check(capture.Commands.Count - before == 1, "exactly one SQL command, no SELECT/read-modify-write");
    Check(capture.Commands[^1].Sql.StartsWith("UPDATE", StringComparison.Ordinal), "server UPDATE");
    Check(capture.Commands[^1].Parameters > 0, "parameterized update");
    Check(!capture.Commands[^1].Sql.Contains(policy.Reason, StringComparison.Ordinal), "value is not SQL text");
    Check(capture.Commands[^1].StringParameters.Any(value => value.StartsWith("$", StringComparison.Ordinal) && value.Contains("[0]", StringComparison.Ordinal)), "fixed collection path is a parameter");
    Check(capture.Commands[^1].StringParameters.Any(value => value.StartsWith("$", StringComparison.Ordinal) && value.Contains("with.dot", StringComparison.Ordinal)), "escaped model JSON property path is a parameter");
    Check(!capture.Commands[^1].Sql.Contains("$.Offers", StringComparison.Ordinal) && !capture.Commands[^1].Sql.Contains("with.dot", StringComparison.Ordinal), "JSON path is not SQL text");
    Check(await context.Products.Where(product => product.Id == 1 && product.RowVersion == 0)
        .ExecuteUpdateAsync(setters => setters.SetProperty(product => product.Attributes.OnHand, 999), token) == 0, "stale command rejected by explicit rowversion");
    await using var transaction = await context.Database.BeginTransactionAsync(token);
    Check(await context.Products.Where(product => product.Id == 1).ExecuteUpdateAsync(setters => setters
        .SetProperty(product => product.Attributes.Replenishment,
            new ReplenishmentPolicy { Minimum = 999, Target = 999, Reason = "rollback" }), token) == 1, "transaction update");
    await transaction.RollbackAsync(token);
}
else if (phase == "reject")
{
    var original = await ReadRawAsync(context, token);
    var index = 0;
    await ExpectRejectedAsync(() => context.Products.Where(product => product.Id == 1).ExecuteUpdateAsync(setters => setters
        .SetProperty(product => product.Attributes.Offers[index].Vendor, "dynamic"), token), "dynamic index", false);
    await ExpectRejectedAsync(() => context.Products.Where(product => product.Id == 1).ExecuteUpdateAsync(setters => setters
        .SetProperty(product => product.Attributes.Offers.ElementAt(-1).Vendor, "negative"), token), "negative index", false);
    await ExpectRejectedAsync(() => context.Products.Where(product => product.Id == 1).ExecuteUpdateAsync(setters => setters
        .SetProperty(product => product.Attributes.Offers[0].ContractTag, product => product.Attributes.Offers[0].ContractTag), token), "converter row RHS", false);
    await ExpectRejectedAsync(() => context.Products.Where(product => product.Id == 1).ExecuteUpdateAsync(setters => setters
        .SetProperty(product => product.Attributes.OnHand, 999)
        .SetProperty(product => product.Attributes.Offers[9].Vendor, "out-of-range"), token), "out-of-range statement", true);
    Check(await ReadRawAsync(context, token) == original, "all rejected commands preserved the complete original JSON");
    Check(await context.Products.Where(product => product.Id == 1).Select(product => product.RowVersion).SingleAsync(token) == 1, "rejections preserved explicit rowversion");
}
else if (phase == "compete")
{
    var firstCapture = new CommandCapture();
    var secondCapture = new CommandCapture();
    await using var first = new ProductContext(new DbContextOptionsBuilder<ProductContext>().UseSonnetDB(connection).AddInterceptors(firstCapture).Options);
    await using var second = new ProductContext(new DbContextOptionsBuilder<ProductContext>().UseSonnetDB(connection).AddInterceptors(secondCapture).Options);
    using var gate = new Barrier(2);
    Task<int> CompeteAsync(ProductContext contender, string reason) => Task.Run(async () =>
    {
        if (!gate.SignalAndWait(TimeSpan.FromSeconds(10), token))
            throw new TimeoutException("Both independent business contenders must reach the start barrier.");
        return await contender.Products.Where(product => product.Id == 1 && product.RowVersion == 1)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(product => product.Attributes.Replenishment, new ReplenishmentPolicy { Minimum = 18, Target = 70, Reason = reason })
                .SetProperty(product => product.RowVersion, product => product.RowVersion + 1), token);
    }, token);
    concurrentOutcomes = await Task.WhenAll(CompeteAsync(first, "winner-a"), CompeteAsync(second, "winner-b"));
    Check(concurrentOutcomes.Order().SequenceEqual([0, 1]), "two concurrent explicit-version commands have exactly one winner");
    Check(firstCapture.Commands.Count == 1 && secondCapture.Commands.Count == 1, "each contender sent exactly one UPDATE without a read");
    capture.Commands.AddRange(firstCapture.Commands);
    capture.Commands.AddRange(secondCapture.Commands);
    var winner = await context.Products.AsNoTracking().SingleAsync(product => product.Id == 1, token);
    Check(winner.RowVersion == 2 && winner.Attributes.Replenishment.Reason == (concurrentOutcomes[0] == 1 ? "winner-a" : "winner-b"), "persisted strategy belongs to the sole winning command");
}
else if (phase == "verify-concurrency")
{
    var product = await context.Products.AsNoTracking().SingleAsync(product => product.Id == 1, token);
    Check(product.RowVersion == 2 && product.Attributes.Replenishment.Reason is "winner-a" or "winner-b", "winning version and strategy survive independent process reopen");
    Check(product.Attributes.Replenishment.Minimum == 18 && product.Attributes.Replenishment.Target == 70, "winning strategy is complete after reopen");
    Check(product.Attributes.OnHand == 14 && product.Attributes.PriorVendor == "原供应商", "concurrent strategy update preserved stock and prior vendor");
    using var json = JsonDocument.Parse(await ReadRawAsync(context, token));
    Check(json.RootElement.GetProperty("Unmanaged").GetRawText() == "{\"externalId\":9007199254740993,\"calibration\":1.2300e+04,\"offset\":\"+05:30\",\"escaped\":\"\\u4e2d\"}", "unknown external JSON preserved through concurrent update and process reopen");
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
    Check(product.Attributes.OnHand == 14, "stock adjustment persisted");
    Check(product.Attributes.Replenishment.Minimum == 15 && product.Attributes.Replenishment.Target == 50, "RHS used old stock 12 rather than new stock 14 or replacement minimum 99");
    Check(product.Attributes.PriorVendor == "原供应商", "collection RHS used old vendor before element replacement");
    Check(product.Attributes.Replenishment.Reason == "补货 O'Reilly 中文😃", "model JSON name and Unicode");
    Check(product.Attributes.Labels.SequenceEqual(["preferred-reviewed", "自动补货", "quote'\\\""]), "array replacement followed by fixed primitive element update");
    Check(product.Attributes.Offers[0].Vendor == "新供应商" && product.Attributes.Offers[0].Price == 10.125m, "supplier scalar and decimal JSON codec");
    Check(product.Attributes.CommandId == Guid.Parse("98df7bba-33b7-4db3-82f6-c7f3401b0395"), "Guid codec");
    Check(product.Attributes.OptionalCommandId is null, "nullable Guid encoded as JSON null");
    Check(product.Attributes.Offers[0].ContractTag == "mixed", "fixed owned element converter decoded on read");
    Check(product.Attributes.UpdatedAt == new DateTimeOffset(2026, 11, 6, 14, 15, 16, TimeSpan.FromHours(5.5)).AddTicks(1234567)
        && product.Attributes.UpdatedAt.Offset == TimeSpan.FromHours(5.5), "offset/ticks codec");
    using var json = JsonDocument.Parse(await ReadRawAsync(context, token));
    Check(json.RootElement.GetProperty("Offers")[0].GetProperty("ContractTag").GetString() == "MIXED", "converter encoded its provider JSON representation");
    Check(json.RootElement.GetProperty("Unmanaged").GetRawText() == "{\"externalId\":9007199254740993,\"calibration\":1.2300e+04,\"offset\":\"+05:30\",\"escaped\":\"\\u4e2d\"}", "unknown external JSON preserved exactly");
}

using (var writer = new Utf8JsonWriter(Console.OpenStandardOutput()))
{
    writer.WriteStartObject();
    writer.WriteString("status", "PASS");
    writer.WriteString("phase", phase);
    writer.WriteNumber("capturedCommands", capture.Commands.Count);
    writer.WriteStartArray("checks");
    foreach (var check in checks.Take(96))
        writer.WriteStringValue(check);
    writer.WriteEndArray();
    writer.WriteStartArray("concurrentAffectedRows");
    foreach (var outcome in concurrentOutcomes.Take(2))
        writer.WriteNumberValue(outcome);
    writer.WriteEndArray();
    writer.WriteStartArray("commands");
    foreach (var command in capture.Commands.Take(64))
    {
        writer.WriteStartObject();
        writer.WriteString("sql", command.Sql);
        writer.WriteNumber("parameters", command.Parameters);
        writer.WriteStartArray("stringParameters");
        foreach (var parameter in command.StringParameters.Take(128))
            writer.WriteStringValue(parameter);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
    writer.WriteEndArray();
    writer.WriteStartArray("rejections");
    foreach (var rejection in rejections.Take(4))
    {
        writer.WriteStartObject();
        writer.WriteString("operation", rejection.Operation);
        writer.WriteString("exceptionType", rejection.Type);
        writer.WriteString("message", rejection.Message);
        writer.WriteString("serverError", rejection.ServerError);
        writer.WriteString("serverMessage", rejection.ServerMessage);
        writer.WriteNumber("sqlCommands", rejection.SqlCommands);
        writer.WriteEndObject();
    }
    writer.WriteEndArray();
    writer.WriteStartArray("assemblies");
    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
        .Where(value => value.GetName().Name?.StartsWith("SonnetDB", StringComparison.Ordinal) == true)
        .OrderBy(value => value.GetName().Name, StringComparer.Ordinal).Take(16))
    {
        writer.WriteStartObject();
        writer.WriteString("name", assembly.GetName().Name);
        writer.WriteString("version", assembly.GetName().Version?.ToString());
        writer.WriteString("path", Path.GetFullPath(assembly.Location));
        using var file = File.OpenRead(assembly.Location);
        writer.WriteString("sha256", Convert.ToHexStringLower(SHA256.HashData(file)));
        writer.WriteEndObject();
    }
    writer.WriteEndArray();
    writer.WriteEndObject();
}

void Check(bool condition, string operation)
{
    if (!condition)
        throw new InvalidOperationException("Business acceptance failed: " + operation);
    if (checks.Count >= 96)
        throw new InvalidOperationException("Acceptance check budget exceeded.");
    checks.Add(operation);
}

async Task ExpectRejectedAsync(Func<Task<int>> action, string operation, bool sentSql)
{
    var before = capture.Commands.Count;
    Exception? rejected = null;
    try { await action(); }
    catch (NotSupportedException exception) when (!sentSql && exception.Message.Contains("This JSON operation is not supported", StringComparison.Ordinal)) { rejected = exception; }
    catch (InvalidOperationException exception) when (sentSql && IsOutOfRangeJsonFailure(exception.Message)) { rejected = exception; }
    catch (SndbServerException exception) when (sentSql && exception.Error == "sql_error" && IsOutOfRangeJsonFailure(exception.ServerMessage)) { rejected = exception; }
    Check(rejected is not null, operation + " rejected");
    Check(capture.Commands.Count - before == (sentSql ? 1 : 0), operation + (sentSql ? " sent one atomic statement" : " rejected before SQL"));
    Check(rejections.Count < 4, "rejection receipt budget");
    var actual = rejected!;
    var server = actual as SndbServerException;
    rejections.Add((operation, actual.GetType().FullName ?? actual.GetType().Name, actual.Message,
        server?.Error, server?.ServerMessage, capture.Commands.Count - before));
}

static bool IsOutOfRangeJsonFailure(string message) => message.StartsWith("json_set_value:", StringComparison.Ordinal)
    && message.Contains("数组路径必须指向现有数组中未越界的元素。", StringComparison.Ordinal);

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
            attributes.OwnsMany(value => value.Offers, offers => offers.Property(value => value.ContractTag)
                .HasConversion(value => value.ToUpperInvariant(), value => value.ToLowerInvariant()));
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
    public Guid? OptionalCommandId { get; set; }
    public string PriorVendor { get; set; } = string.Empty;
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
    public string ContractTag { get; set; } = string.Empty;
}

internal sealed class CommandCapture : DbCommandInterceptor
{
    public List<(string Sql, int Parameters, string[] StringParameters)> Commands { get; } = [];

    private void Capture(DbCommand command)
    {
        if (Commands.Count >= 64)
            throw new InvalidOperationException("Command budget exceeded.");
        Commands.Add((command.CommandText, command.Parameters.Count,
            command.Parameters.Cast<DbParameter>().Take(128).Select(parameter => parameter.Value).OfType<string>().ToArray()));
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
