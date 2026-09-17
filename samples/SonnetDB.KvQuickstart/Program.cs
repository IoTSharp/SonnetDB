using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonnetDB.Data;
using SonnetDB.Data.Kv;
using SonnetDB.Kv;

string connection = Environment.GetEnvironmentVariable("SONNETDB_CONNECTION")
    ?? "Data Source=./kv-quickstart-data;Timeout=10";
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; deadline.Cancel(); };
CancellationToken cancellation = deadline.Token;
if (!args.Contains("--existing-database", StringComparer.Ordinal))
    await SndbResourceInitializer.EnsureDatabaseAsync(connection, "KV Quickstart", cancellation);
using var client = new SndbKvClient(connection);

const string Keyspace = "device_cache";
const string Tenant = "site_east";
const string Key = "device_001";
if (args.Contains("--verify-reopen", StringComparer.Ordinal))
{
    var recovered = await client.GetAsync(Keyspace, Tenant, Key, cancellation);
    Require(recovered is not null, "Persistent record missing after reopen.");
    var state = KvValueCodec.DecodeJson(recovered!.Value, SampleJsonContext.Default.DeviceState);
    Require(state?.Status == "ready", "Recovered value mismatch.");
    Require(recovered.ExpiresAtUtc is not null, "Recovered TTL missing.");
    Require(recovered.Version > 0, "Recovered version invalid.");
    if (Environment.GetEnvironmentVariable("SONNETDB_EXPECTED_VERSION") is { } versionText)
        Require(recovered.Version == long.Parse(versionText, CultureInfo.InvariantCulture), "Recovered version changed.");
    if (Environment.GetEnvironmentVariable("SONNETDB_EXPECTED_EXPIRY_TICKS") is { } expiryText)
        Require(recovered.ExpiresAtUtc?.UtcTicks == long.Parse(expiryText, CultureInfo.InvariantCulture), "Recovered expiry changed.");
    Require(await client.GetAsync(Keyspace, Tenant, "deleted", cancellation) is null, "Deleted record reappeared.");
    Console.WriteLine("PASS: recovered value, version, TTL and deletion.");
    return;
}

try
{
    DateTimeOffset expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10);
    byte[] initial = KvValueCodec.EncodeJson(new DeviceState("initial", "setup"), SampleJsonContext.Default.DeviceState);
    var created = await client.SetConditionalAsync(Keyspace, Tenant, Key, initial,
        KvSetCondition.IfNotExists, expiresAtUtc, cancellation);
    Console.WriteLine($"NX applied={created.Applied}, version={created.Version}");

    // CAS uses the authoritative version. A conflict ends this attempt without a hidden retry.
    var current = await client.GetAsync(Keyspace, Tenant, Key, cancellation)
        ?? throw new InvalidOperationException("Record expired before update.");
    string operationId = Guid.NewGuid().ToString("N");
    byte[] ready = KvValueCodec.EncodeJson(new DeviceState("ready", operationId), SampleJsonContext.Default.DeviceState);
    var updated = await client.CompareAndSetAsync(Keyspace, Tenant, Key, current.Version, ready, expiresAtUtc, cancellation);
    Require(updated.Succeeded, "Concurrent update: reload state and make a new application decision.");

    var temporary = await client.GetAndSetAsync(Keyspace, Tenant, "deleted", [], cancellationToken: cancellation);
    var consumed = await client.GetAndDeleteAsync(Keyspace, Tenant, "deleted", cancellation);
    Require(consumed.PreviousEntry is { Value.Length: 0 }, "An existing empty value must remain distinguishable from a missing key.");
    Require(consumed.PreviousEntry!.Version == temporary.MutationVersion, "Exchange version mismatch.");
    var repeated = await client.GetAndDeleteAsync(Keyspace, Tenant, "deleted", cancellation);
    Require(repeated.PreviousEntry is null && repeated.MutationVersion is null, "A repeated delete must be a no-op.");
    var duplicate = await client.SetConditionalAsync(Keyspace, Tenant, Key, [], KvSetCondition.IfNotExists, cancellationToken: cancellation);
    Require(!duplicate.Applied && duplicate.Version is null, "NX conflict must not write.");
    Require((await client.GetAsync(Keyspace, Tenant, Key, cancellation))?.Version == updated.NewVersion, "Unexpected mutation after NX conflict.");
    Console.WriteLine($"PASS: conditional write, CAS, exchange, empty value, repeated delete; version={updated.NewVersion}");
    Console.WriteLine(JsonSerializer.Serialize(new RecoveryExpectation(updated.NewVersion!.Value, expiresAtUtc.UtcTicks),
        SampleJsonContext.Default.RecoveryExpectation));
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled or timed out. A dispatched mutation may have committed; reconcile its operation ID and version before another attempt.");
    Environment.ExitCode = 2;
}
catch (SndbServerException error)
{
    Console.Error.WriteLine($"KV failed: code={error.Error}. Check the target, permission and server health; reconcile any dispatched mutation before retrying.");
    Environment.ExitCode = 3;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed record DeviceState(string Status, string OperationId);
internal sealed record RecoveryExpectation(long Version, long ExpiryTicks);

[JsonSerializable(typeof(DeviceState))]
[JsonSerializable(typeof(RecoveryExpectation))]
internal sealed partial class SampleJsonContext : JsonSerializerContext;
