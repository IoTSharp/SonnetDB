using SonnetDB.Data.Kv;

namespace SonnetDB.Caching;

/// <summary>
/// SonnetDB 缓存 Provider 共享的 Data-layer KV store。
/// </summary>
public sealed class SonnetDbCacheStore : IDisposable
{
    private readonly SndbKvClient _client;

    public SonnetDbCacheStore(SonnetDbCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException("SonnetDB cache provider requires a SonnetDB.Data connection string.");

        Options = options;
        _client = new SndbKvClient(options.ConnectionString);
    }

    public SonnetDbCacheOptions Options { get; }

    public object Database => _client;

    public SndbKvEntry? GetEntry(string key) =>
        Run(_client.GetAsync(Options.Keyspace, Options.Namespace, key));

    public Task<SndbKvEntry?> GetEntryAsync(string key, CancellationToken cancellationToken = default) =>
        _client.GetAsync(Options.Keyspace, Options.Namespace, key, cancellationToken);

    public IReadOnlyDictionary<string, SndbKvEntry?> GetMany(IEnumerable<string> keys) =>
        Run(_client.GetManyAsync(Options.Keyspace, Options.Namespace, keys));

    public Task<IReadOnlyDictionary<string, SndbKvEntry?>> GetManyAsync(
        IEnumerable<string> keys,
        CancellationToken cancellationToken = default) =>
        _client.GetManyAsync(Options.Keyspace, Options.Namespace, keys, cancellationToken);

    public long Set(string key, byte[] value, DateTimeOffset? expiresAtUtc = null) =>
        Run(_client.SetAsync(Options.Keyspace, Options.Namespace, key, value, expiresAtUtc));

    public Task<long> SetAsync(
        string key,
        byte[] value,
        DateTimeOffset? expiresAtUtc = null,
        CancellationToken cancellationToken = default) =>
        _client.SetAsync(Options.Keyspace, Options.Namespace, key, value, expiresAtUtc, cancellationToken);

    public IReadOnlyDictionary<string, long> SetMany(
        IEnumerable<KeyValuePair<string, byte[]>> values,
        DateTimeOffset? expiresAtUtc = null) =>
        Run(_client.SetManyAsync(Options.Keyspace, Options.Namespace, values, expiresAtUtc));

    public Task<IReadOnlyDictionary<string, long>> SetManyAsync(
        IEnumerable<KeyValuePair<string, byte[]>> values,
        DateTimeOffset? expiresAtUtc = null,
        CancellationToken cancellationToken = default) =>
        _client.SetManyAsync(Options.Keyspace, Options.Namespace, values, expiresAtUtc, cancellationToken);

    public bool Remove(string key) =>
        Run(_client.RemoveAsync(Options.Keyspace, Options.Namespace, key));

    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        _client.RemoveAsync(Options.Keyspace, Options.Namespace, key, cancellationToken);

    public int RemoveMany(IEnumerable<string> keys) =>
        Run(_client.RemoveManyAsync(Options.Keyspace, Options.Namespace, keys));

    public Task<int> RemoveManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default) =>
        _client.RemoveManyAsync(Options.Keyspace, Options.Namespace, keys, cancellationToken);

    public IReadOnlyList<SndbKvEntry> ScanPrefix(string prefix, int? limit = null) =>
        Run(_client.ScanPrefixAsync(Options.Keyspace, Options.Namespace, prefix, limit));

    public Task<IReadOnlyList<SndbKvEntry>> ScanPrefixAsync(
        string prefix,
        int? limit = null,
        CancellationToken cancellationToken = default) =>
        _client.ScanPrefixAsync(Options.Keyspace, Options.Namespace, prefix, limit, cancellationToken);

    public int RemovePrefix(string prefix, int? limit = null) =>
        Run(_client.RemovePrefixAsync(Options.Keyspace, Options.Namespace, prefix, limit));

    public Task<int> RemovePrefixAsync(string prefix, int? limit = null, CancellationToken cancellationToken = default) =>
        _client.RemovePrefixAsync(Options.Keyspace, Options.Namespace, prefix, limit, cancellationToken);

    public int CleanExpired(int? limit = null) =>
        Run(_client.CleanExpiredAsync(Options.Keyspace, limit));

    public Task<int> CleanExpiredAsync(int? limit = null, CancellationToken cancellationToken = default) =>
        _client.CleanExpiredAsync(Options.Keyspace, limit, cancellationToken);

    public void Dispose() => _client.Dispose();

    private static T Run<T>(Task<T> task) => task.ConfigureAwait(false).GetAwaiter().GetResult();
}
