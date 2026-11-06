using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using SonnetDB.EntityFrameworkCore.Extensions;

if (args.Length != 2 || args[0] is not ("seed" or "mutate" or "verify"))
    throw new ArgumentException("Usage: Issue394.OwnsManyApp seed|mutate|verify <absolute database directory>. Set SONNETDB_APP_CONNECTION for a remote database.");
if (!Path.IsPathFullyQualified(args[1]))
    throw new ArgumentException("Database directory must be absolute.");

var phase = args[0];
var database = Path.GetFullPath(args[1]);
var connection = Environment.GetEnvironmentVariable("SONNETDB_APP_CONNECTION") ?? "Data Source=" + database;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
var token = timeout.Token;
var assertions = new List<string>();
var capture = new CommandCapture();
var options = new DbContextOptionsBuilder<PackingContext>().UseSonnetDB(connection).AddInterceptors(capture).Options;

if (phase == "seed")
{
    await using var context = new PackingContext(options);
    await context.Database.EnsureCreatedAsync(token);
    context.AddRange(
        new Shipment { Id = 1, Stamp = "seed", Manifest = new PackingManifest { Batches = [NewBatch("duplicate"), NewBatch("duplicate")] } },
        new Shipment { Id = 2, Stamp = "seed", Manifest = new PackingManifest { Batches = [] } },
        new Shipment { Id = 3, Stamp = "seed", Manifest = new PackingManifest { Batches = null! } },
        new Shipment { Id = 4, Stamp = "seed", Manifest = null },
        new Shipment { Id = 5, Stamp = "seed", Manifest = new PackingManifest { Batches = [] } },
        new RootPackingList { Id = 1, Batches = [NewBatch("first"), NewBatch("second")] });
    Check(await context.SaveChangesAsync(token) == 6, "insert six shipment and root-array owners");
    var raw = (await ReadRawAsync(context, "Shipments394", "Manifest", 1, token))!;
    var withExternal = raw[..^1] + ",\"External\":{\"id\":9007199254740993,\"number\":1.2300e+04}}";
    Check(await context.Database.ExecuteSqlInterpolatedAsync(
        $"UPDATE \"Shipments394\" SET \"Manifest\"={withExternal} WHERE \"Id\"=1", token) == 1, "attach external metadata with bound JSON");
    var missing = "{}";
    Check(await context.Database.ExecuteSqlInterpolatedAsync(
        $"UPDATE \"Shipments394\" SET \"Manifest\"={missing} WHERE \"Id\"=5", token) == 1, "prepare missing collection member");
    await VerifyNullShapesAsync(context, token);
    var row = await context.Shipments.AsNoTracking().SingleAsync(value => value.Id == 1, token);
    Check(row.Manifest!.Batches.Count == 2 && row.Manifest.Batches.All(batch => batch.Label == "duplicate"), "equal business values remain distinct collection elements");
    Check(row.Manifest.Batches[0].Converted == "lower" && row.Manifest.Batches[0].Optional is null, "converter and nullable element materialization");
}
else if (phase == "mutate")
{
    await using (var context = new PackingContext(options))
    {
        var row = await context.Shipments.SingleAsync(value => value.Id == 1, token);
        row.Manifest!.Batches[0].Parts[0].Quantity = 9;
        var before = capture.Commands.Count;
        Check(await context.SaveChangesAsync(token) == 1, "tracked nested component mutation");
        Check(capture.Commands.Count == before + 1 && capture.Commands[^1].Sql.Contains("json_set_value(", StringComparison.Ordinal), "nested SaveChanges sends one server JSON UPDATE");
        var raw = (await ReadRawAsync(context, "Shipments394", "Manifest", 1, token))!;
        using (var json = JsonDocument.Parse(raw))
        {
            Check(json.RootElement.GetProperty(PackingContext.CollectionName)[0].GetProperty("Parts")[0].GetProperty("Quantity").GetInt32() == 9, "nested mutation persisted at the correct ordinal");
            Check(json.RootElement.GetProperty("External").GetRawText() == "{\"id\":9007199254740993,\"number\":1.2300e+04}", "element mutation preserves external JSON and numeric spelling");
        }

        row.Manifest.Batches[1].Label = "保留 O'Reilly 中文😃";
        row.Manifest.Batches[1].Optional = "bin-中文";
        row.Manifest.Batches[1].Converted = "CHANGED";
        row.Manifest.Batches[1].Token = Guid.Empty;
        Check(await context.SaveChangesAsync(token) == 1, "multiple tracked element properties and converter");
        using (var changed = JsonDocument.Parse((await ReadRawAsync(context, "Shipments394", "Manifest", 1, token))!))
        {
            var batch = changed.RootElement.GetProperty(PackingContext.CollectionName)[1];
            Check(batch.GetProperty("Converted").GetString() == "CHANGED" && batch.GetProperty("Token").GetGuid() == Guid.Empty
                && batch.GetProperty("Optional").GetString() == "bin-中文", "model JSON writer uses converter Guid and nullable scalar codecs");
        }

        row.Manifest.Batches.RemoveAt(0);
        row.Manifest.Batches.Add(NewBatch("added"));
        Check(await context.SaveChangesAsync(token) == 1, "remove first parent and append a parent with nested parts");
        var committed = (await ReadRawAsync(context, "Shipments394", "Manifest", 1, token))!;
        using (var changed = JsonDocument.Parse(committed))
        {
            var batches = changed.RootElement.GetProperty(PackingContext.CollectionName);
            Check(batches.GetArrayLength() == 2 && batches[0].GetProperty(PackingContext.LabelName).GetString() == "保留 O'Reilly 中文😃"
                && batches[1].GetProperty(PackingContext.LabelName).GetString() == "added", "parent ordinal rebase persists ordered business data");
        }

        before = capture.Commands.Count;
        row.Manifest.Batches.Clear();
        try
        {
            await context.SaveChangesAsync(token);
            throw new InvalidOperationException("Expected EF10 identifying-key rejection after nested parent ordinal rebase.");
        }
        catch (InvalidOperationException failure) when (failure.Message.Contains("is part of a key", StringComparison.Ordinal))
        {
            Check(capture.Commands.Count == before, "EF10 identifying-key rejection sends no next SQL command");
            Check(committed == await ReadRawAsync(context, "Shipments394", "Manifest", 1, token), "EF10 rejected continuation preserves committed JSON");
        }

        context.ChangeTracker.Clear();
        row = await context.Shipments.SingleAsync(value => value.Id == 1, token);
        row.Manifest!.Batches[0].Parts[0].Quantity = 7;
        Check(await context.SaveChangesAsync(token) == 1, "explicit Clear and reload permit the following nested element mutation");
        using (var reloaded = JsonDocument.Parse((await ReadRawAsync(context, "Shipments394", "Manifest", 1, token))!))
        {
            var batches = reloaded.RootElement.GetProperty(PackingContext.CollectionName);
            Check(batches[0].GetProperty("Parts")[0].GetProperty("Quantity").GetInt32() == 7
                && batches[1].GetProperty("Parts")[0].GetProperty("Quantity").GetInt32() == 1,
                "post-reload nested mutation targets the rebased first parent and preserves its sibling");
        }
        row.Manifest.Batches.Clear();
        Check(await context.SaveChangesAsync(token) == 1, "clear tracked collection after reloading persistent ordinals");
        using (var empty = JsonDocument.Parse((await ReadRawAsync(context, "Shipments394", "Manifest", 1, token))!))
            Check(empty.RootElement.GetProperty(PackingContext.CollectionName).GetArrayLength() == 0, "cleared collection persists as empty JSON array");
        row.Manifest.Batches.Add(NewBatch("final shipment"));
        Check(await context.SaveChangesAsync(token) == 1, "empty collection accepts a new nested batch");

        var root = await context.RootPackingLists.SingleAsync(value => value.Id == 1, token);
        root.Batches[1].Label = "second updated";
        Check(await context.SaveChangesAsync(token) == 1, "root OwnsMany element update");
        root.Batches.Add(NewBatch("third"));
        Check(await context.SaveChangesAsync(token) == 1, "root OwnsMany array append");
    }

    await using (var context = new PackingContext(options))
    {
        var before = await ReadRawAsync(context, "Shipments394", "Manifest", 1, token);
        await using var transaction = await context.Database.BeginTransactionAsync(token);
        var row = await context.Shipments.SingleAsync(value => value.Id == 1, token);
        row.Manifest!.Batches[0].Parts[0].Quantity = 999;
        Check(await context.SaveChangesAsync(token) == 1, "tracked nested update in explicit transaction");
        await using (var outside = new PackingContext(options))
            Check(await ReadRawAsync(outside, "Shipments394", "Manifest", 1, token) == before, "another connection cannot see uncommitted nested mutation");
        await transaction.RollbackAsync(token);
        Check(await ReadRawAsync(context, "Shipments394", "Manifest", 1, token) == before, "rollback restores the exact persistent JSON");
    }

    await using (var winner = new PackingContext(options))
    await using (var stale = new PackingContext(options))
    {
        var winning = await winner.Shipments.SingleAsync(value => value.Id == 1, token);
        var old = await stale.Shipments.SingleAsync(value => value.Id == 1, token);
        winning.Manifest!.Batches[0].Parts[0].Quantity = 8;
        winning.Stamp = "accepted";
        Check(await winner.SaveChangesAsync(token) == 1, "winning tracked update and explicit concurrency stamp");
        old.Manifest!.Batches[0].Parts[0].Quantity = 777;
        var completed = capture.CompletedUpdates.Count;
        try
        {
            await stale.SaveChangesAsync(token);
            throw new InvalidOperationException("Expected DbUpdateConcurrencyException for the stale shipment.");
        }
        catch (DbUpdateConcurrencyException failure)
        {
            Check(failure.Entries.Count > 0, "stale tracked owner raises concurrency exception");
            Check(capture.CompletedUpdates.Count == completed + 1 && capture.CompletedUpdates[^1].Affected == 0,
                "stale tracked update reports zero affected rows");
            Check(capture.CompletedUpdates[^1].Sql.Contains("\"Stamp\"", StringComparison.Ordinal), "stale update includes the explicit stamp predicate");
        }
    }
}
else
{
    await using var context = new PackingContext(options);
    var row = await context.Shipments.AsNoTracking().SingleAsync(value => value.Id == 1, token);
    Check(row.Stamp == "accepted", "reopen preserves the winning concurrency stamp");
    Check(row.Manifest!.Batches.Count == 1 && row.Manifest.Batches[0].Label == "final shipment", "reopen preserves final collection order and cardinality");
    var batch = row.Manifest.Batches[0];
    Check(batch.Parts.Count == 1 && batch.Parts[0].Quantity == 8 && batch.Parts[0].Label == "part '中文", "reopen preserves winning nested data and rejects rollback or stale values");
    Check(batch.Optional is null && batch.Converted == "lower" && batch.Token == Guid.Parse("5a11d1b1-25ad-4776-81f2-bcd293289f3a"), "reopen preserves null converter and Guid");
    using (var raw = JsonDocument.Parse((await ReadRawAsync(context, "Shipments394", "Manifest", 1, token))!))
    {
        Check(raw.RootElement.GetProperty("External").GetRawText() == "{\"id\":9007199254740993,\"number\":1.2300e+04}", "reopen preserves target-external metadata");
        Check(raw.RootElement.GetProperty(PackingContext.CollectionName)[0].GetProperty("Converted").GetString() == "LOWER", "converter persists its provider representation");
    }
    await VerifyNullShapesAsync(context, token);
    var root = await context.RootPackingLists.AsNoTracking().SingleAsync(value => value.Id == 1, token);
    Check(root.Batches.Select(value => value.Label).SequenceEqual(["first", "second updated", "third"]), "reopen preserves root-array element update and append");
    using var rootRaw = JsonDocument.Parse((await ReadRawAsync(context, "RootPackingLists394", "Batches", 1, token))!);
    Check(rootRaw.RootElement.ValueKind == JsonValueKind.Array && rootRaw.RootElement.GetArrayLength() == 3, "root OwnsMany is persisted as a JSON array");
}

using (var writer = new Utf8JsonWriter(Console.OpenStandardOutput()))
{
    writer.WriteStartObject();
    writer.WriteString("status", "PASS");
    writer.WriteString("phase", phase);
    writer.WriteNumber("processId", Environment.ProcessId);
    writer.WriteString("database", database);
    writer.WriteString("transportEvidence", "The supervising runner must independently verify REST or native HTTP/2 Frame requests.");
    writer.WriteStartArray("assertions");
    foreach (var assertion in assertions.Take(96))
        writer.WriteStringValue(assertion);
    writer.WriteEndArray();
    writer.WriteStartArray("commands");
    foreach (var command in capture.Commands.Take(128))
    {
        writer.WriteStartObject();
        writer.WriteString("sql", command.Sql);
        writer.WriteNumber("parameters", command.Parameters);
        writer.WriteEndObject();
    }
    writer.WriteEndArray();
    writer.WriteStartArray("completedUpdates");
    foreach (var command in capture.CompletedUpdates.Take(64))
    {
        writer.WriteStartObject();
        writer.WriteString("sql", command.Sql);
        writer.WriteNumber("affected", command.Affected);
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
    token.ThrowIfCancellationRequested();
    if (!condition)
        throw new InvalidOperationException("Business acceptance failed: " + operation);
    if (assertions.Count >= 96)
        throw new InvalidOperationException("Assertion budget exceeded.");
    assertions.Add(operation);
}

async Task VerifyNullShapesAsync(PackingContext context, CancellationToken cancellationToken)
{
    var rows = await context.Shipments.AsNoTracking().Where(value => value.Id >= 2 && value.Id <= 5).OrderBy(value => value.Id).ToArrayAsync(cancellationToken);
    Check(rows.Length == 4 && rows[0].Manifest!.Batches.Count == 0, "empty collection materializes empty");
    Check(rows[1].Manifest!.Batches is null, "JSON null collection materializes null");
    Check(rows[2].Manifest is null, "SQL NULL aggregate materializes null");
    Check(rows[3].Manifest!.Batches is null, "missing collection member materializes null");
    using var empty = JsonDocument.Parse((await ReadRawAsync(context, "Shipments394", "Manifest", 2, cancellationToken))!);
    using var nullCollection = JsonDocument.Parse((await ReadRawAsync(context, "Shipments394", "Manifest", 3, cancellationToken))!);
    Check(empty.RootElement.GetProperty(PackingContext.CollectionName).GetArrayLength() == 0, "empty collection persists as array");
    Check(nullCollection.RootElement.GetProperty(PackingContext.CollectionName).ValueKind == JsonValueKind.Null, "null collection persists distinctly from empty");
    Check(await ReadRawAsync(context, "Shipments394", "Manifest", 4, cancellationToken) is null, "null aggregate persists as SQL NULL");
}

static PackingBatch NewBatch(string label) => new()
{
    Label = label,
    Converted = "lower",
    Token = Guid.Parse("5a11d1b1-25ad-4776-81f2-bcd293289f3a"),
    Parts = [new PackingPart { Label = "part '中文", Quantity = 1 }],
};

static async Task<string?> ReadRawAsync(PackingContext context, string table, string column, long id, CancellationToken cancellationToken)
{
    // Only application-owned fixed identifiers reach the command text.
    if ((table, column) is not (("Shipments394", "Manifest") or ("RootPackingLists394", "Batches")))
        throw new ArgumentException("Unexpected raw-read identifier.");
    await context.Database.OpenConnectionAsync(cancellationToken);
    try
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT \"{column}\" FROM \"{table}\" WHERE \"Id\"=@id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = id;
        command.Parameters.Add(parameter);
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : (string)value;
    }
    finally { await context.Database.CloseConnectionAsync(); }
}

internal sealed class PackingContext(DbContextOptions<PackingContext> options) : DbContext(options)
{
    internal const string CollectionName = "batches.'[root] 😃";
    internal const string LabelName = "label.'[value] 中文";

    /// <summary>运单业务集合。</summary>
    public DbSet<Shipment> Shipments => Set<Shipment>();
    /// <summary>直接持久为 JSON 数组的装箱单。</summary>
    public DbSet<RootPackingList> RootPackingLists => Set<RootPackingList>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        var shipments = builder.Entity<Shipment>();
        shipments.ToTable("Shipments394");
        shipments.Property(value => value.Id).ValueGeneratedNever();
        shipments.Property(value => value.Stamp).IsConcurrencyToken().ValueGeneratedNever();
        shipments.OwnsOne(value => value.Manifest, manifest =>
        {
            manifest.ToJson("Manifest");
            manifest.OwnsMany(value => value.Batches, batches =>
            {
                batches.HasJsonPropertyName(CollectionName);
                batches.Property(value => value.Label).HasJsonPropertyName(LabelName);
                batches.Property(value => value.Converted).HasConversion(value => value.ToUpperInvariant(), value => value.ToLowerInvariant());
                batches.OwnsMany(value => value.Parts);
            });
        });
        shipments.Navigation(value => value.Manifest).IsRequired(false);
        var roots = builder.Entity<RootPackingList>();
        roots.ToTable("RootPackingLists394");
        roots.Property(value => value.Id).ValueGeneratedNever();
        roots.OwnsMany(value => value.Batches, batches =>
        {
            batches.ToJson("Batches");
            batches.Property(value => value.Label).HasJsonPropertyName(LabelName);
            batches.Property(value => value.Converted).HasConversion(value => value.ToUpperInvariant(), value => value.ToLowerInvariant());
            batches.OwnsMany(value => value.Parts);
        });
    }
}

internal sealed class Shipment
{
    /// <summary>运单主键。</summary>
    public long Id { get; set; }
    /// <summary>应用显式维护的并发标记。</summary>
    public string Stamp { get; set; } = string.Empty;
    /// <summary>可空装箱清单。</summary>
    public PackingManifest? Manifest { get; set; }
}

internal sealed class PackingManifest
{
    /// <summary>装箱清单标题。</summary>
    public string Title { get; set; } = "装箱清单";
    /// <summary>按数组位置跟踪的装箱批次。</summary>
    public List<PackingBatch> Batches { get; set; } = [];
}

internal sealed class RootPackingList
{
    /// <summary>装箱单主键。</summary>
    public long Id { get; set; }
    /// <summary>根 JSON 集合。</summary>
    public List<PackingBatch> Batches { get; set; } = [];
}

internal sealed class PackingBatch
{
    /// <summary>允许重复的业务标签。</summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>可空库位。</summary>
    public string? Optional { get; set; }
    /// <summary>使用大小写值转换器的批次描述。</summary>
    public string Converted { get; set; } = string.Empty;
    /// <summary>批次业务标识。</summary>
    public Guid Token { get; set; }
    /// <summary>随父批次序号跟踪的零件集合。</summary>
    public List<PackingPart> Parts { get; set; } = [];
}

internal sealed class PackingPart
{
    /// <summary>零件标签。</summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>装箱数量。</summary>
    public int Quantity { get; set; }
}

internal sealed class CommandCapture : DbCommandInterceptor
{
    /// <summary>有界 SQL 命令记录。</summary>
    public List<(string Sql, int Parameters)> Commands { get; } = [];
    /// <summary>实际完成的更新及其影响行数。</summary>
    public List<(string Sql, int Affected)> CompletedUpdates { get; } = [];

    private void Capture(DbCommand command)
    {
        if (Commands.Count >= 128 || command.Parameters.Count > 256)
            throw new InvalidOperationException("Command or parameter budget exceeded.");
        Commands.Add((command.CommandText, command.Parameters.Count));
    }

    private void Complete(DbCommand command, DbDataReader reader)
    {
        if (!command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal))
            return;
        if (CompletedUpdates.Count >= 64)
            throw new InvalidOperationException("Completed update budget exceeded.");
        CompletedUpdates.Add((command.CommandText, reader.RecordsAffected));
    }

    /// <summary>记录同步读取命令。</summary>
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    { Capture(command); return result; }

    /// <summary>记录异步读取命令。</summary>
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { Capture(command); return ValueTask.FromResult(result); }

    /// <summary>记录同步非查询命令。</summary>
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    { Capture(command); return result; }

    /// <summary>记录异步非查询命令。</summary>
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Capture(command); return ValueTask.FromResult(result); }

    /// <summary>记录同步更新实际影响行数。</summary>
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    { Complete(command, result); return result; }

    /// <summary>记录异步更新实际影响行数。</summary>
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    { Complete(command, result); return ValueTask.FromResult(result); }
}
