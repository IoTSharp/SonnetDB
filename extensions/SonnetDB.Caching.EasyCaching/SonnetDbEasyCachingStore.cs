using System.Text;
using SonnetDB.Data;
using SonnetDB.Data.Kv;

namespace SonnetDB.Caching.EasyCaching;

internal sealed class SonnetDbEasyCachingStore : IDisposable
{
    public const string StartupProbeKey = "__sonnetdb_easycaching_startup_probe";

    private readonly SndbKvClient _client;

    public SonnetDbEasyCachingStore(SonnetDbEasyCachingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException("SonnetDB EasyCaching provider requires a SonnetDB.Data connection string.");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Keyspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Namespace);

        Options = options;
        SndbResourceInitializer.EnsureDatabase(options.ConnectionString, "EasyCaching 缓存数据库");
        _client = new SndbKvClient(options.ConnectionString);
        EnsureKeyspaceReady();
    }

    public SonnetDbEasyCachingOptions Options { get; }

    public object Database => _client;

    public SndbKvEntry? GetEntry(string key) =>
        Run(_client.GetAsync(Options.Keyspace, Options.Namespace, key));

    public IReadOnlyDictionary<string, SndbKvEntry?> GetMany(IEnumerable<string> keys) =>
        Run(_client.GetManyAsync(Options.Keyspace, Options.Namespace, keys));

    public long Set(string key, byte[] value, DateTimeOffset? expiresAtUtc = null) =>
        Run(_client.SetAsync(Options.Keyspace, Options.Namespace, key, value, expiresAtUtc));

    public IReadOnlyDictionary<string, long> SetMany(
        IEnumerable<KeyValuePair<string, byte[]>> values,
        DateTimeOffset? expiresAtUtc = null) =>
        Run(_client.SetManyAsync(Options.Keyspace, Options.Namespace, values, expiresAtUtc));

    public bool Remove(string key) =>
        Run(_client.RemoveAsync(Options.Keyspace, Options.Namespace, key));

    public int RemoveMany(IEnumerable<string> keys) =>
        Run(_client.RemoveManyAsync(Options.Keyspace, Options.Namespace, keys));

    public IReadOnlyList<SndbKvEntry> ScanPrefix(string prefix, int? limit = null) =>
        Run(_client.ScanPrefixAsync(Options.Keyspace, Options.Namespace, prefix, limit));

    public int RemovePrefix(string prefix, int? limit = null) =>
        Run(_client.RemovePrefixAsync(Options.Keyspace, Options.Namespace, prefix, limit));

    public Task<int> CleanExpiredAsync(int? limit = null, CancellationToken cancellationToken = default) =>
        _client.CleanExpiredAsync(Options.Keyspace, limit, cancellationToken);

    public void Dispose() => _client.Dispose();

    private static T Run<T>(Task<T> task) => task.ConfigureAwait(false).GetAwaiter().GetResult();

    private void EnsureKeyspaceReady()
    {
        var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5);
        Run(_client.SetAsync(
            Options.Keyspace,
            Options.Namespace,
            StartupProbeKey,
            Encoding.UTF8.GetBytes("ok"),
            expiresAtUtc));
    }
}
