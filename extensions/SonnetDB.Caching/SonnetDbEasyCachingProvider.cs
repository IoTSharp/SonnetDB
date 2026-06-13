using EasyCaching.Core;
using EasyCaching.Core.Serialization;

namespace SonnetDB.Caching;

/// <summary>
/// 基于 SonnetDB KV keyspace 的 EasyCaching Provider。
/// </summary>
public sealed class SonnetDbEasyCachingProvider : IEasyCachingProvider
{
    private readonly SonnetDbCacheStore _store;

    /// <summary>
    /// 初始化 SonnetDB EasyCaching Provider。
    /// </summary>
    public SonnetDbEasyCachingProvider(string name, SonnetDbCacheStore store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(store);
        Name = name;
        _store = store;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsDistributedCache => true;

    /// <inheritdoc />
    public bool UseLock => false;

    /// <inheritdoc />
    public int MaxRdSecond => 0;

    /// <inheritdoc />
    public CachingProviderType CachingProviderType => CachingProviderType.Ext1;

    /// <inheritdoc />
    public CacheStats CacheStats { get; } = new();

    /// <inheritdoc />
    public object Database => _store.Database;

    /// <inheritdoc />
    public void Set<T>(string cacheKey, T cacheValue, TimeSpan expiration)
    {
        ValidateKey(cacheKey);
        byte[] payload = SonnetDbCacheCodec.Serialize(cacheValue);
        _store.Set(cacheKey, payload, ToExpiresAt(expiration));
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string cacheKey, T cacheValue, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Set(cacheKey, cacheValue, expiration);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public CacheValue<T> Get<T>(string cacheKey)
    {
        ValidateKey(cacheKey);
        var entry = _store.GetEntry(cacheKey);
        if (entry is null)
        {
            CacheStats.OnMiss();
            return CacheValue<T>.NoValue;
        }

        CacheStats.OnHit();
        T? value = SonnetDbCacheCodec.Deserialize<T>(entry.Value);
        return new CacheValue<T>(value!, value is not null);
    }

    /// <inheritdoc />
    public Task<CacheValue<T>> GetAsync<T>(string cacheKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Get<T>(cacheKey));
    }

    /// <inheritdoc />
    public object? Get(string cacheKey, Type type)
    {
        ValidateKey(cacheKey);
        var entry = _store.GetEntry(cacheKey);
        if (entry is null)
        {
            CacheStats.OnMiss();
            return null;
        }

        CacheStats.OnHit();
        return System.Text.Json.JsonSerializer.Deserialize(entry.Value, type);
    }

    /// <inheritdoc />
    public Task<object> GetAsync(string cacheKey, Type type, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Get(cacheKey, type)!);
    }

    /// <inheritdoc />
    public void Remove(string cacheKey)
    {
        ValidateKey(cacheKey);
        _store.Remove(cacheKey);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Remove(cacheKey);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public bool Exists(string cacheKey)
    {
        ValidateKey(cacheKey);
        return _store.GetEntry(cacheKey) is not null;
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Exists(cacheKey));
    }

    /// <inheritdoc />
    public bool TrySet<T>(string cacheKey, T cacheValue, TimeSpan expiration)
    {
        if (Exists(cacheKey))
            return false;

        Set(cacheKey, cacheValue, expiration);
        return true;
    }

    /// <inheritdoc />
    public Task<bool> TrySetAsync<T>(
        string cacheKey,
        T cacheValue,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TrySet(cacheKey, cacheValue, expiration));
    }

    /// <inheritdoc />
    public void SetAll<T>(IDictionary<string, T> value, TimeSpan expiration)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (var key in value.Keys)
            ValidateKey(key);

        var expiresAt = ToExpiresAt(expiration);
        var rows = value.Select(pair => new KeyValuePair<string, byte[]>(
            pair.Key,
            SonnetDbCacheCodec.Serialize(pair.Value)));
        _store.SetMany(rows, expiresAt);
    }

    /// <inheritdoc />
    public Task SetAllAsync<T>(
        IDictionary<string, T> value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetAll(value, expiration);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void RemoveAll(IEnumerable<string> cacheKeys)
    {
        ArgumentNullException.ThrowIfNull(cacheKeys);
        _store.RemoveMany(cacheKeys);
    }

    /// <inheritdoc />
    public Task RemoveAllAsync(IEnumerable<string> cacheKeys, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoveAll(cacheKeys);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public CacheValue<T> Get<T>(string cacheKey, Func<T> dataRetriever, TimeSpan expiration)
    {
        var value = Get<T>(cacheKey);
        if (value.HasValue)
            return value;

        T loaded = dataRetriever();
        Set(cacheKey, loaded, expiration);
        return new CacheValue<T>(loaded, true);
    }

    /// <inheritdoc />
    public async Task<CacheValue<T>> GetAsync<T>(
        string cacheKey,
        Func<Task<T>> dataRetriever,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = Get<T>(cacheKey);
        if (value.HasValue)
            return value;

        T loaded = await dataRetriever().ConfigureAwait(false);
        Set(cacheKey, loaded, expiration);
        return new CacheValue<T>(loaded, true);
    }

    /// <inheritdoc />
    public void RemoveByPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        _store.RemovePrefix(prefix);
    }

    /// <inheritdoc />
    public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoveByPrefix(prefix);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void RemoveByPattern(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        RemoveByPrefix(pattern.TrimEnd('*'));
    }

    /// <inheritdoc />
    public Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoveByPattern(pattern);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<string> GetAllKeysByPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return _store.ScanPrefix(prefix, int.MaxValue)
            .Select(static row => row.Key)
            .ToArray();
    }

    /// <inheritdoc />
    public Task<IEnumerable<string>> GetAllKeysByPrefixAsync(
        string prefix,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetAllKeysByPrefix(prefix));
    }

    /// <inheritdoc />
    public IDictionary<string, CacheValue<T>> GetAll<T>(IEnumerable<string> cacheKeys)
    {
        ArgumentNullException.ThrowIfNull(cacheKeys);
        var keys = cacheKeys.ToArray();
        foreach (string key in keys)
            ValidateKey(key);

        var entries = _store.GetMany(keys);
        var result = new Dictionary<string, CacheValue<T>>(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            if (!entries.TryGetValue(key, out var entry) || entry is null)
            {
                CacheStats.OnMiss();
                result[key] = CacheValue<T>.NoValue;
                continue;
            }

            CacheStats.OnHit();
            T? value = SonnetDbCacheCodec.Deserialize<T>(entry.Value);
            result[key] = new CacheValue<T>(value!, value is not null);
        }

        return result;
    }

    /// <inheritdoc />
    public Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(
        IEnumerable<string> cacheKeys,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetAll<T>(cacheKeys));
    }

    /// <inheritdoc />
    public IDictionary<string, CacheValue<T>> GetByPrefix<T>(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var entries = _store.ScanPrefix(prefix, int.MaxValue);
        var result = new Dictionary<string, CacheValue<T>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            CacheStats.OnHit();
            T? value = SonnetDbCacheCodec.Deserialize<T>(entry.Value);
            result[entry.Key] = new CacheValue<T>(value!, value is not null);
        }

        return result;
    }

    /// <inheritdoc />
    public Task<IDictionary<string, CacheValue<T>>> GetByPrefixAsync<T>(
        string prefix,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetByPrefix<T>(prefix));
    }

    /// <inheritdoc />
    public int GetCount(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return _store.ScanPrefix(prefix, int.MaxValue).Count;
    }

    /// <inheritdoc />
    public Task<int> GetCountAsync(string prefix, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetCount(prefix));
    }

    /// <inheritdoc />
    public void Flush() => _store.RemovePrefix(string.Empty);

    /// <inheritdoc />
    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public TimeSpan GetExpiration(string cacheKey)
    {
        ValidateKey(cacheKey);
        var entry = _store.GetEntry(cacheKey);
        if (entry?.ExpiresAtUtc is null)
            return TimeSpan.Zero;

        TimeSpan remaining = entry.ExpiresAtUtc.Value - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <inheritdoc />
    public Task<TimeSpan> GetExpirationAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetExpiration(cacheKey));
    }

    /// <inheritdoc />
    public ProviderInfo GetProviderInfo() => new()
    {
        ProviderName = Name,
        CacheStats = CacheStats,
        ProviderType = CachingProviderType,
        MaxRdSecond = MaxRdSecond,
        IsDistributedProvider = IsDistributedCache,
        EnableLogging = false,
        SleepMs = 0,
        LockMs = 0,
        SerializerName = "System.Text.Json",
        CacheNulls = false,
    };

    private DateTimeOffset? ToExpiresAt(TimeSpan expiration)
    {
        var ttl = expiration > TimeSpan.Zero ? expiration : _store.Options.DefaultExpiration;
        return ttl > TimeSpan.Zero ? DateTimeOffset.UtcNow.Add(ttl) : null;
    }

    private static void ValidateKey(string cacheKey) => ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
}
