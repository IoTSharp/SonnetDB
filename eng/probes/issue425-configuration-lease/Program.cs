using System.Data;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SonnetDB.Data;
using SonnetDB.EntityFrameworkCore.Extensions;

const string SerializationConflict = "table_serialization_conflict";
var settings = new SndbConnectionStringBuilder(
    Environment.GetEnvironmentVariable("SONNETDB_LEASE_PROBE_CONNECTION")
        ?? throw new InvalidOperationException("Set SONNETDB_LEASE_PROBE_CONNECTION for an isolated test database."));
Require(settings.Database?.StartsWith("issue425_", StringComparison.Ordinal) == true
    && settings.Host is "127.0.0.1" or "localhost"
    && string.Equals(settings.Protocol?.ToString(), "Rest", StringComparison.OrdinalIgnoreCase),
    "Use REST and a local, disposable issue425_ database.");

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    deadline.Cancel();
};
var cancellation = deadline.Token;
using var client = new ProbeContext(new DbContextOptionsBuilder<ProbeContext>()
    .UseSonnetDB(settings.ConnectionString).Options);

switch (args)
{
    case ["setup"]:
        WriteAssembly(typeof(SndbConnection).Assembly);
        WriteAssembly(client.GetServiceProviderAssembly());
        Require(await client.Database.EnsureCreatedAsync(cancellation), "Expected a fresh database.");
        client.Anchors.AddRange(
            new LeaseAnchor { GatewayId = FixtureId(1) },
            new LeaseAnchor { GatewayId = FixtureId(2), OperationId = FixtureId(20), Owner = "busy", ExpiresAt = DateTime.UtcNow.AddHours(1) },
            new LeaseAnchor { GatewayId = FixtureId(3), OperationId = FixtureId(30), Owner = "expired", ExpiresAt = DateTime.UtcNow.AddHours(-1) },
            new LeaseAnchor { GatewayId = FixtureId(4), Deleted = true });
        await client.SaveChangesAsync(cancellation);
        break;
    case ["verify"]:
        await VerifyAsync(client, cancellation);
        break;
    case ["race"]:
        await RaceAsync(client, cancellation);
        break;
    case ["readback"]:
        await ReadbackAsync(client, cancellation);
        break;
    case ["worker", var operationIdArgument]:
        await WorkerAsync(client, Guid.Parse(operationIdArgument), cancellation);
        break;
    default:
        throw new ArgumentException("Expected setup, verify, race or readback.");
}

static async Task VerifyAsync(ProbeContext client, CancellationToken cancellation)
{
    var now = DateTime.UtcNow;
    var freeGateway = FixtureId(1);
    await using (var transaction = await client.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellation))
    {
        Require(await AcquireAsync(client, FixtureId(1), FixtureId(10), now, cancellation) == 1, "Free anchor must affect one row.");
        Require(await AcquireAsync(client, FixtureId(2), FixtureId(10), now, cancellation) == 0, "Busy anchor must affect zero rows.");
        Require(await AcquireAsync(client, FixtureId(4), FixtureId(10), now, cancellation) == 0, "Deleted anchor must affect zero rows.");
        Require(await AcquireAsync(client, FixtureId(5), FixtureId(10), now, cancellation) == 0, "Missing anchor must affect zero rows.");
        Require(await ReleaseAsync(client, new[] { FixtureId(1) }, FixtureId(99), cancellation) == 0, "Wrong owner must affect zero rows.");
        client.Receipts.Add(new OperationReceipt { OperationId = FixtureId(10), GatewayId = FixtureId(1) });
        await client.SaveChangesAsync(cancellation);
        await transaction.RollbackAsync(cancellation);
    }

    client.ChangeTracker.Clear();
    Require(!await client.Receipts.AnyAsync(cancellation), "Rolled back receipt was persisted.");
    Require((await client.Anchors.AsNoTracking().SingleAsync(row => row.GatewayId == freeGateway, cancellation)).OperationId is null,
        "Rolled back first acquisition was persisted.");

    await using (var transaction = await client.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellation))
    {
        Require(await AcquireAsync(client, FixtureId(1), FixtureId(11), now, cancellation) == 1, "First batch acquisition failed.");
        Require(await AcquireAsync(client, FixtureId(3), FixtureId(11), now, cancellation) == 1, "Expired lease could not be recovered.");
        IReadOnlyList<Guid> ids = new[] { FixtureId(1), FixtureId(3) };
        Require(await ReleaseAsync(client, ids, FixtureId(11), cancellation) == ids.Count, "Batch release did not affect two rows.");
        await transaction.CommitAsync(cancellation);
    }
    Require(await client.Anchors.AsNoTracking().CountAsync(row => row.OperationId == null && !row.Deleted, cancellation) == 2,
        "Batch released rows must have null operation IDs.");
    Require(await client.Anchors.AsNoTracking().CountAsync(row => row.Owner == null && row.ExpiresAt == null && !row.Deleted, cancellation) == 2,
        "All release setters must clear their columns.");
    Console.WriteLine("PASS affected counts, wrong owner, batch rollback, expired lease and batch release");
}

static async Task RaceAsync(ProbeContext client, CancellationToken cancellation)
{
    using var first = StartWorker(FixtureId(61));
    try
    {
        using var second = StartWorker(FixtureId(62));
        try
        {
            Console.WriteLine($"Workers {first.Id}, {second.Id} (parent {Environment.ProcessId})");
            var firstError = first.StandardError.ReadToEndAsync(cancellation);
            var secondError = second.StandardError.ReadToEndAsync(cancellation);
            await Task.WhenAll(RequireLineAsync(first, "ready", cancellation), RequireLineAsync(second, "ready", cancellation));
            await Task.WhenAll(SendGateAsync(first, cancellation), SendGateAsync(second, cancellation));
            await Task.WhenAll(RequireLineAsync(first, "affected=1", cancellation), RequireLineAsync(second, "affected=1", cancellation));
            await Task.WhenAll(SendGateAsync(first, cancellation), SendGateAsync(second, cancellation));
            var results = await Task.WhenAll(first.StandardOutput.ReadLineAsync(cancellation).AsTask(), second.StandardOutput.ReadLineAsync(cancellation).AsTask());
            await Task.WhenAll(first.WaitForExitAsync(cancellation), second.WaitForExitAsync(cancellation));
            Require(first.ExitCode == 0 && second.ExitCode == 0,
                $"Worker failure. First: {await firstError} Second: {await secondError}");
            Array.Sort(results, StringComparer.Ordinal);
            Require(results.SequenceEqual(new[] { "committed", "conflict" }), $"Expected one commit and one conflict, got {string.Join(", ", results)}.");
        }
        finally
        {
            await StopWorkerAsync(second);
        }
    }
    finally
    {
        await StopWorkerAsync(first);
    }

    await ReadbackAsync(client, cancellation);
    Console.WriteLine("PASS both independent transactions saw affected=1, one commit succeeded and one conflicted");
}

static async Task ReadbackAsync(ProbeContext client, CancellationToken cancellation)
{
    var gateway = FixtureId(1);
    var anchor = await client.Anchors.AsNoTracking().SingleAsync(row => row.GatewayId == gateway, cancellation);
    var receipts = await client.Receipts.AsNoTracking().ToArrayAsync(cancellation);
    Require(receipts.Length == 1
        && (receipts[0].OperationId == FixtureId(61) || receipts[0].OperationId == FixtureId(62))
        && receipts[0].GatewayId == anchor.GatewayId
        && anchor.ActiveVersion == receipts[0].OperationId
        && anchor.OperationId is null && anchor.Owner is null && anchor.ExpiresAt is null,
        "Expected only the winning receipt and matching assignment, with no dangling lease.");
    Console.WriteLine($"PASS consistent committed assignment and receipt {receipts[0].OperationId}");
}

static async Task WorkerAsync(ProbeContext client, Guid operationId, CancellationToken cancellation)
{
    await using var transaction = await client.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
    Console.WriteLine("ready");
    Require(await Console.In.ReadLineAsync(cancellation) == "continue", "Missing acquisition gate.");
    var gateway = FixtureId(1);
    int affected = await AcquireAsync(client, gateway, operationId, DateTime.UtcNow, cancellation);
    Console.WriteLine($"affected={affected}");
    Require(affected == 1, "Unexpected acquisition result.");
    Require(await Console.In.ReadLineAsync(cancellation) == "continue", "Missing commit gate.");

    var assignment = await client.Anchors.SingleAsync(row => row.GatewayId == gateway, cancellation);
    assignment.ActiveVersion = operationId;
    client.Receipts.Add(new OperationReceipt { OperationId = operationId, GatewayId = gateway });
    await client.SaveChangesAsync(cancellation);
    Require(await ReleaseAsync(client, new[] { gateway }, operationId, cancellation) == 1, "Worker lost the lease before commit.");
    try
    {
        await transaction.CommitAsync(cancellation);
        Console.WriteLine("committed");
    }
    catch (SndbServerException ex) when (ex.Error == SerializationConflict)
    {
        Console.WriteLine("conflict");
    }
}

static Task<int> AcquireAsync(ProbeContext client, Guid gateway, Guid operation, DateTime now, CancellationToken cancellation)
    => client.Anchors
        .Where(row => row.GatewayId == gateway && !row.Deleted
            && (row.OperationId == null || row.ExpiresAt == null || row.ExpiresAt <= now))
        .ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.OperationId, operation)
            .SetProperty(row => row.ExpiresAt, now.AddMinutes(5))
            .SetProperty(row => row.Owner, "probe"), cancellation);

static Task<int> ReleaseAsync(ProbeContext client, IReadOnlyList<Guid> gateways, Guid operation, CancellationToken cancellation)
    => client.Anchors
        .Where(row => gateways.Contains(row.GatewayId) && row.OperationId == operation)
        .ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.OperationId, (Guid?)null)
            .SetProperty(row => row.ExpiresAt, (DateTime?)null)
            .SetProperty(row => row.Owner, (string?)null), cancellation);

static Process StartWorker(Guid operationId)
{
    string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate dotnet.");
    Require(Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase),
        "Launch the probe by passing its DLL to dotnet.");
    var options = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    options.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    options.ArgumentList.Add("worker");
    options.ArgumentList.Add(operationId.ToString("D"));
    var process = Process.Start(options) ?? throw new InvalidOperationException("Could not start a lease worker.");
    Console.WriteLine($"Started pid={process.Id} parent={Environment.ProcessId} at={process.StartTime:O} executable={executable} dll={options.ArgumentList[0]} worker={operationId:D}");
    return process;
}

static async Task StopWorkerAsync(Process worker)
{
    if (!worker.HasExited)
    {
        worker.StandardInput.Close();
        using var gracefulDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await worker.WaitForExitAsync(gracefulDeadline.Token);
        }
        catch (OperationCanceledException)
        {
            worker.Kill(entireProcessTree: true);
            using var forcedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await worker.WaitForExitAsync(forcedDeadline.Token);
        }
    }
}

static async Task RequireLineAsync(Process worker, string expected, CancellationToken cancellation)
{
    var actual = await worker.StandardOutput.ReadLineAsync(cancellation);
    Require(actual == expected, $"Worker {worker.Id} expected '{expected}', received '{actual}'.");
}

static async Task SendGateAsync(Process worker, CancellationToken cancellation)
{
    await worker.StandardInput.WriteLineAsync("continue".AsMemory(), cancellation);
    await worker.StandardInput.FlushAsync(cancellation);
}

static Guid FixtureId(int index) => new(index, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void WriteAssembly(Assembly assembly)
{
    using var dll = File.OpenRead(assembly.Location);
    var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    Console.WriteLine($"{assembly.GetName().Name} {version} SHA256={Convert.ToHexString(SHA256.HashData(dll))}");
}

sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options)
{
    internal DbSet<LeaseAnchor> Anchors => Set<LeaseAnchor>();
    internal DbSet<OperationReceipt> Receipts => Set<OperationReceipt>();

    internal Assembly GetServiceProviderAssembly()
        => Assembly.Load(new AssemblyName(Database.ProviderName
            ?? throw new InvalidOperationException("Provider name is unavailable.")));

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<LeaseAnchor>(anchor =>
        {
            anchor.ToTable("LeaseAnchor");
            anchor.HasKey(row => row.GatewayId);
            anchor.Property(row => row.GatewayId).ValueGeneratedNever();
        });
        model.Entity<OperationReceipt>(receipt =>
        {
            receipt.ToTable("OperationReceipt");
            receipt.HasKey(row => row.OperationId);
            receipt.Property(row => row.OperationId).ValueGeneratedNever();
        });
    }
}

sealed class LeaseAnchor
{
    public Guid GatewayId { get; set; }
    public Guid? OperationId { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? Owner { get; set; }
    public bool Deleted { get; set; }
    public Guid? ActiveVersion { get; set; }
}

sealed class OperationReceipt
{
    public Guid OperationId { get; set; }
    public Guid GatewayId { get; set; }
}
