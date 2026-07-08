using System.Text;
using SonnetDB.Data;
using SonnetDB.Data.Kv;

namespace SonnetDB.Caching.Distributed;

internal sealed class SonnetDbDistributedCacheStore : IDisposable
{
    public const string StartupProbeKey = "__sonnetdb_distributed_cache_startup_probe";

    private readonly SndbKvClient _client;

    public SonnetDbDistributedCacheStore(SonnetDbDistributedCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException("SonnetDB distributed cache provider requires a SonnetDB.Data connection string.");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Keyspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Namespace);

        Options = options;
        SndbResourceInitializer.EnsureDatabase(options.ConnectionString, "IDistributedCache 缓存数据库");
        _client = new SndbKvClient(options.ConnectionString);
        EnsureKeyspaceReady();
    }

    public SonnetDbDistributedCacheOptions Options { get; }

    public SndbKvEntry? GetEntry(string key) =>
        Run(_client.GetAsync(Options.Keyspace, Options.Namespace, key));

    public long Set(string key, byte[] value, DateTimeOffset? expiresAtUtc = null) =>
        Run(_client.SetAsync(Options.Keyspace, Options.Namespace, key, value, expiresAtUtc));

    public bool Remove(string key) =>
        Run(_client.RemoveAsync(Options.Keyspace, Options.Namespace, key));

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
