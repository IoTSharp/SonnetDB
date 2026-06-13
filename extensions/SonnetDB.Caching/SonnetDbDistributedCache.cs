using Microsoft.Extensions.Caching.Distributed;

namespace SonnetDB.Caching;

/// <summary>
/// 基于 SonnetDB KV keyspace 的 <see cref="IDistributedCache"/> 实现。
/// </summary>
public sealed class SonnetDbDistributedCache : IDistributedCache
{
    private readonly SonnetDbCacheStore _store;

    /// <summary>
    /// 初始化 SonnetDB 分布式缓存。
    /// </summary>
    public SonnetDbDistributedCache(SonnetDbCacheStore store)
    {
        _store = store;
    }

    /// <inheritdoc />
    public byte[]? Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var envelope = ReadEnvelope(key);
        return envelope?.Value;
    }

    /// <inheritdoc />
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(Get(key));
    }

    /// <inheritdoc />
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);

        var now = DateTimeOffset.UtcNow;
        var envelope = CreateEnvelope(value, options, now);
        DateTimeOffset? expiresAt = ResolveExpiresAt(envelope, now);
        _store.Set(key, SonnetDbCacheCodec.SerializeDistributed(envelope), expiresAt);
    }

    /// <inheritdoc />
    public Task SetAsync(
        string key,
        byte[] value,
        DistributedCacheEntryOptions options,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Set(key, value, options);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Refresh(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var envelope = ReadEnvelope(key);
        if (envelope is null || envelope.SlidingExpirationTicks is null)
            return;

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? absolute = envelope.AbsoluteExpirationUtcTicks.HasValue
            ? new DateTimeOffset(envelope.AbsoluteExpirationUtcTicks.Value, TimeSpan.Zero)
            : null;
        var sliding = TimeSpan.FromTicks(envelope.SlidingExpirationTicks.Value);
        DateTimeOffset slidingExpiresAt = now.Add(sliding);
        DateTimeOffset? expiresAt = absolute.HasValue && absolute.Value < slidingExpiresAt
            ? absolute
            : slidingExpiresAt;

        _store.Set(key, SonnetDbCacheCodec.SerializeDistributed(envelope), expiresAt);
    }

    /// <inheritdoc />
    public Task RefreshAsync(string key, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Refresh(key);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _store.Remove(key);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Remove(key);
        return Task.CompletedTask;
    }

    private DistributedCacheEnvelope? ReadEnvelope(string key)
    {
        var entry = _store.GetEntry(key);
        return entry is null ? null : SonnetDbCacheCodec.DeserializeDistributed(entry.Value);
    }

    private static DistributedCacheEnvelope CreateEnvelope(
        byte[] value,
        DistributedCacheEntryOptions options,
        DateTimeOffset now)
    {
        DateTimeOffset? absolute = options.AbsoluteExpirationRelativeToNow.HasValue
            ? now.Add(options.AbsoluteExpirationRelativeToNow.Value)
            : options.AbsoluteExpiration;

        return new DistributedCacheEnvelope(
            value,
            absolute?.UtcTicks,
            options.SlidingExpiration?.Ticks);
    }

    private static DateTimeOffset? ResolveExpiresAt(DistributedCacheEnvelope envelope, DateTimeOffset now)
    {
        DateTimeOffset? absolute = envelope.AbsoluteExpirationUtcTicks.HasValue
            ? new DateTimeOffset(envelope.AbsoluteExpirationUtcTicks.Value, TimeSpan.Zero)
            : null;
        DateTimeOffset? sliding = envelope.SlidingExpirationTicks.HasValue
            ? now.Add(TimeSpan.FromTicks(envelope.SlidingExpirationTicks.Value))
            : null;

        if (absolute.HasValue && sliding.HasValue)
            return absolute.Value < sliding.Value ? absolute : sliding;

        return absolute ?? sliding;
    }
}
