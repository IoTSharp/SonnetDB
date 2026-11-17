using SonnetDB.FullText.Storage;
using Xunit;

namespace SonnetDB.Core.Tests.FullText;

public sealed class FullTextBlockCacheTests
{
    [Fact]
    public void Tracks_hit_miss_and_keeps_payload_within_byte_budget()
    {
        var cache = new FullTextBlockCache(8);
        FullTextBlockCacheKey key = new(1, 0, 4, 7);
        byte[] payload = [1, 2, 3, 4];

        Assert.False(cache.TryGet(key, out _));
        Assert.True(cache.TryAdd(key, payload));
        Assert.True(cache.TryGet(key, out byte[] actual));
        Assert.Equal(payload, actual);
        Assert.Equal(1, cache.MissCount);
        Assert.Equal(1, cache.HitCount);
        Assert.Equal(4, cache.CurrentBytes);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Evicts_least_recently_used_entry_by_bytes()
    {
        var cache = new FullTextBlockCache(8);
        FullTextBlockCacheKey first = new(1, 0, 4, 1);
        FullTextBlockCacheKey second = new(1, 4, 4, 2);
        FullTextBlockCacheKey third = new(1, 8, 4, 3);

        Assert.True(cache.TryAdd(first, new byte[4]));
        Assert.True(cache.TryAdd(second, new byte[4]));
        Assert.True(cache.TryGet(first, out _));
        Assert.True(cache.TryAdd(third, new byte[4]));

        Assert.True(cache.TryGet(first, out _));
        Assert.False(cache.TryGet(second, out _));
        Assert.True(cache.TryGet(third, out _));
        Assert.Equal(1, cache.EvictionCount);
        Assert.Equal(8, cache.CurrentBytes);
    }

    [Fact]
    public void Rejects_disabled_and_oversized_entries_without_mutating_existing_state()
    {
        FullTextBlockCacheKey key = new(1, 0, 16, 1);
        var disabled = new FullTextBlockCache(0);
        Assert.False(disabled.TryAdd(key, new byte[4]));
        Assert.Equal(0, disabled.Count);

        var cache = new FullTextBlockCache(4);
        Assert.False(cache.TryAdd(key, new byte[5]));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CurrentBytes);
    }

    [Fact]
    public void Clear_releases_bytes_but_preserves_cumulative_counters()
    {
        var cache = new FullTextBlockCache(8);
        FullTextBlockCacheKey key = new(1, 0, 4, 1);
        Assert.True(cache.TryAdd(key, new byte[4]));
        Assert.True(cache.TryGet(key, out _));
        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CurrentBytes);
        Assert.Equal(1, cache.HitCount);
        Assert.False(cache.TryGet(key, out _));
        Assert.Equal(1, cache.MissCount);
    }

    [Fact]
    public void Concurrent_short_operations_never_exceed_budget()
    {
        var cache = new FullTextBlockCache(64);
        Parallel.For(0, 32, index =>
        {
            FullTextBlockCacheKey key = new(2, index * 4L, 4, (uint)index);
            _ = cache.TryAdd(key, new byte[4]);
            _ = cache.TryGet(key, out _);
        });

        Assert.InRange(cache.CurrentBytes, 0, 64);
        Assert.InRange(cache.Count, 0, 16);
    }
}
