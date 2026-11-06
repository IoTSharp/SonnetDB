using System.Security.Cryptography;
using System.Text.Json;
using SonnetDB.Client;
using SonnetDB.Protocol;

if (args.Length != 2 || args[0] is not ("seed" or "exchange" or "verify-deleted"))
    throw new ArgumentException("Usage: Issue397.KvApp seed|exchange|verify-deleted <absolute evidence directory>.");
if (!Path.IsPathFullyQualified(args[1]))
    throw new ArgumentException("Evidence directory must be absolute.");
var endpointText = Environment.GetEnvironmentVariable("SONNETDB_FRAME_ENDPOINT");
if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")
    || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
    throw new ArgumentException("SONNETDB_FRAME_ENDPOINT must identify the test Server without embedded credentials.");
var configuredToken = Environment.GetEnvironmentVariable("SONNETDB_FRAME_TOKEN");
if (configuredToken?.Length == 0) configuredToken = null;
var phase = args[0];
var evidenceDirectory = Path.GetFullPath(args[1]);
var receiptPath = Path.Combine(evidenceDirectory, phase + ".json");
if (File.Exists(receiptPath))
    throw new InvalidOperationException("This phase already has a receipt; reconcile it before any further remote operation.");
Directory.CreateDirectory(evidenceDirectory);

const string Database = "issue397_sdk";
const string Keyspace = "get_and_delete";
byte[] prefix = [0x39, 0x37, 0xff];
byte[] mainKey = [.. prefix, 0];
byte[] emptyKey = [.. prefix, 1];
byte[] raceKey = [.. prefix, 2];
byte[] survivorKey = [.. prefix, 3];
byte[] missingKey = [.. prefix, 4];
byte[] mainValue = [0, 1, 127, 128, 255];
byte[] raceValue = [255, 0, 0x39, 0x37];
byte[] survivorValue = [42, 0, 255];
var expiry = new DateTimeOffset(2035, 11, 7, 1, 2, 3, TimeSpan.FromHours(8)).AddTicks(7654321);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
var token = timeout.Token;
var checks = new List<string>();
var exchanges = new List<(string Operation, KvDataFrameExchangeResult Result)>();
int[] concurrentFound = [];
using var client = new SndbFrameClient(endpoint, configuredToken, timeout: TimeSpan.FromSeconds(20));

if (phase == "seed")
{
    Check((await client.ScanKvPageAsync(Database, Keyspace, prefix, limit: 8, cancellationToken: token)).Count == 0, "fresh bounded prefix before seed");
    await SeedAsync(mainKey, mainValue, expiry, "main");
    await SeedAsync(emptyKey, [], null, "empty");
    await SeedAsync(raceKey, raceValue, expiry, "race");
    await SeedAsync(survivorKey, survivorValue, null, "survivor");
    Check((await client.ScanKvPageAsync(Database, Keyspace, prefix, limit: 8, cancellationToken: token)).Count == 4, "one scan page contains all four seeded keys");
}
else if (phase == "exchange")
{
    var before = await RequireValueAsync(client, mainKey, mainValue, expiry, "main before exchange");
    var deleted = await client.GetAndDeleteKvAsync(Database, Keyspace, mainKey, cancellationToken: token);
    VerifyDeletedRecord(deleted, before, "binary old record");
    exchanges.Add(("main", deleted));

    var emptyBefore = await RequireValueAsync(client, emptyKey, [], null, "empty before exchange");
    var empty = await client.GetAndDeleteKvAsync(Database, Keyspace, emptyKey, cancellationToken: token);
    VerifyDeletedRecord(empty, emptyBefore, "empty old record");
    Check(empty.PreviousValue is not null && empty.PreviousValue.Value.Length == 0, "empty old value remains a present record");
    exchanges.Add(("empty", empty));

    var missing = await client.GetAndDeleteKvAsync(Database, Keyspace, missingKey, cancellationToken: token);
    Check(missing.PreviousValue is null && missing.MutationVersion is null, "missing exchange has neither old record nor mutation version");
    exchanges.Add(("missing", missing));

    var raceBefore = await RequireValueAsync(client, raceKey, raceValue, expiry, "race before exchange");
    using var first = new SndbFrameClient(endpoint, configuredToken, timeout: TimeSpan.FromSeconds(20));
    using var second = new SndbFrameClient(endpoint, configuredToken, timeout: TimeSpan.FromSeconds(20));
    using var gate = new Barrier(2);
    Task<KvDataFrameExchangeResult> CompeteAsync(SndbFrameClient contender) => Task.Run(async () =>
    {
        if (!gate.SignalAndWait(TimeSpan.FromSeconds(10), token))
            throw new TimeoutException("Both independent SDK clients must reach the start barrier.");
        return await contender.GetAndDeleteKvAsync(Database, Keyspace, raceKey, cancellationToken: token);
    }, token);
    var results = await Task.WhenAll(CompeteAsync(first), CompeteAsync(second));
    concurrentFound = results.Select(result => result.PreviousValue is null ? 0 : 1).ToArray();
    Check(concurrentFound.Order().SequenceEqual([0, 1]), "two independent clients receive exactly one old record");
    var winner = results.Single(result => result.PreviousValue is not null);
    var loser = results.Single(result => result.PreviousValue is null);
    VerifyDeletedRecord(winner, raceBefore, "race winning old record");
    Check(loser.MutationVersion is null, "race losing client reports no mutation");
    exchanges.Add(("race-first", results[0]));
    exchanges.Add(("race-second", results[1]));
    await VerifyRemainingAsync();
}
else
{
    await VerifyRemainingAsync();
}

var assemblies = AppDomain.CurrentDomain.GetAssemblies()
    .Where(value => value.GetName().Name is "SonnetDB.Client" or "SonnetDB.Protocol")
    .OrderBy(value => value.GetName().Name, StringComparer.Ordinal).Take(3).ToArray();
Check(assemblies.Length == 2, "actual Client and Protocol assemblies are loaded");
using var receipt = new MemoryStream();
using (var writer = new Utf8JsonWriter(receipt))
{
    writer.WriteStartObject();
    writer.WriteString("status", "PASS");
    writer.WriteString("phase", phase);
    writer.WriteNumber("processId", Environment.ProcessId);
    writer.WriteString("database", Database);
    writer.WriteString("keyspace", Keyspace);
    writer.WriteString("endpoint", endpoint.AbsoluteUri);
    writer.WriteString("transportEvidence", "The supervising runner must independently verify HTTP/2 and /v1/frame requests.");
    writer.WriteStartArray("checks");
    foreach (var check in checks.Take(64)) writer.WriteStringValue(check);
    writer.WriteEndArray();
    writer.WriteStartArray("concurrentFound");
    foreach (var found in concurrentFound.Take(2)) writer.WriteNumberValue(found);
    writer.WriteEndArray();
    writer.WriteStartArray("exchanges");
    foreach (var exchange in exchanges.Take(5))
    {
        writer.WriteStartObject();
        writer.WriteString("operation", exchange.Operation);
        if (exchange.Result.MutationVersion is { } mutation) writer.WriteNumber("mutationVersion", mutation);
        else writer.WriteNull("mutationVersion");
        if (exchange.Result.PreviousValue is { } previous)
        {
            writer.WriteStartObject("previousValue");
            writer.WriteNumber("version", previous.Version);
            writer.WriteString("valueHex", Convert.ToHexStringLower(previous.Value));
            if (previous.ExpiresAtUtc is { } expires) writer.WriteNumber("expiresAtUtcTicks", expires.UtcTicks);
            else writer.WriteNull("expiresAtUtcTicks");
            writer.WriteEndObject();
        }
        else writer.WriteNull("previousValue");
        writer.WriteEndObject();
    }
    writer.WriteEndArray();
    writer.WriteStartArray("assemblies");
    foreach (var assembly in assemblies.Take(2))
    {
        writer.WriteStartObject();
        writer.WriteString("name", assembly.GetName().Name);
        writer.WriteString("version", assembly.GetName().Version?.ToString());
        writer.WriteString("path", Path.GetFullPath(assembly.Location));
        await using var file = File.OpenRead(assembly.Location);
        writer.WriteString("sha256", Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token)));
        writer.WriteEndObject();
    }
    writer.WriteEndArray();
    writer.WriteEndObject();
}
await using (var output = new FileStream(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    await output.WriteAsync(receipt.ToArray(), token);
await Console.OpenStandardOutput().WriteAsync(receipt.ToArray(), token);

void Check(bool condition, string operation)
{
    token.ThrowIfCancellationRequested();
    if (!condition) throw new InvalidOperationException("SDK acceptance failed: " + operation);
    if (checks.Count >= 64) throw new InvalidOperationException("Acceptance check budget exceeded.");
    checks.Add(operation);
}

async Task SeedAsync(byte[] key, byte[] value, DateTimeOffset? expires, string name)
{
    long version = await client.PutKvAsync(Database, Keyspace, key, value, expires, cancellationToken: token);
    Check(version > 0, name + " Put returned positive version");
    var actual = await RequireValueAsync(client, key, value, expires, name + " seeded read");
    Check(actual.Version == version, name + " Get retained the Put version");
}

async Task<KvDataFrameValue> RequireValueAsync(SndbFrameClient reader, byte[] key, byte[] expected, DateTimeOffset? expires, string name)
{
    var actual = await reader.GetKvAsync(Database, Keyspace, key, cancellationToken: token)
        ?? throw new InvalidOperationException("SDK acceptance missing record: " + name);
    Check(actual.Value.AsSpan().SequenceEqual(expected), name + " exact binary value");
    Check(actual.Version > 0, name + " positive old version");
    Check(actual.ExpiresAtUtc?.UtcTicks == expires?.UtcTicks
        && (actual.ExpiresAtUtc is null || actual.ExpiresAtUtc.Value.Offset == TimeSpan.Zero), name + " complete UTC ticks and expiry presence");
    return actual;
}

void VerifyDeletedRecord(KvDataFrameExchangeResult result, KvDataFrameValue expected, string name)
{
    var previous = result.PreviousValue ?? throw new InvalidOperationException("SDK acceptance missing old record: " + name);
    Check(previous.Value.AsSpan().SequenceEqual(expected.Value), name + " exact old binary value");
    Check(previous.Version == expected.Version, name + " exact old version");
    Check(previous.ExpiresAtUtc?.UtcTicks == expected.ExpiresAtUtc?.UtcTicks
        && (previous.ExpiresAtUtc is null || previous.ExpiresAtUtc.Value.Offset == TimeSpan.Zero), name + " full old UTC ticks");
    Check(result.MutationVersion is { } mutation && mutation > previous.Version, name + " separate monotonic delete version");
}

async Task VerifyRemainingAsync()
{
    foreach (var key in new[] { mainKey, emptyKey, raceKey }.Take(3))
        Check(await client.GetKvAsync(Database, Keyspace, key, cancellationToken: token) is null, "deleted key absent via Get: " + Convert.ToHexStringLower(key));
    Check(await client.GetKvAsync(Database, Keyspace, missingKey, cancellationToken: token) is null, "missing key remains absent");
    var page = await client.ScanKvPageAsync(Database, Keyspace, prefix, limit: 8, cancellationToken: token);
    Check(page.Count == 1 && page[0].Key.AsSpan().SequenceEqual(survivorKey), "bounded Scan contains only the surviving key");
    Check(page[0].Value.AsSpan().SequenceEqual(survivorValue), "Scan retains the surviving binary value");
    await RequireValueAsync(client, survivorKey, survivorValue, null, "survivor proves continued service");
}
