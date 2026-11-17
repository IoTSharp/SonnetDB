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

    [Fact]
    public async Task Concurrent_loaders_share_one_inflight_physical_read()
    {
        var cache = new FullTextBlockCache(64);
        FullTextBlockCacheKey key = new(3, 0, 4, 9);
        byte[] expected = [9, 8, 7, 6];
        using var loaderEntered = new ManualResetEventSlim();
        using var releaseLoader = new ManualResetEventSlim();
        int loaderCount = 0;

        Task<byte[]>[] tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => cache.GetOrLoad(
                key,
                () =>
                {
                    Interlocked.Increment(ref loaderCount);
                    loaderEntered.Set();
                    Assert.True(releaseLoader.Wait(TimeSpan.FromSeconds(2)));
                    return expected;
                })))
            .ToArray();

        Assert.True(loaderEntered.Wait(TimeSpan.FromSeconds(2)));
        Thread.Sleep(50);
        Assert.Equal(1, loaderCount);
        releaseLoader.Set();
        await Task.WhenAll(tasks);

        Assert.All(tasks, task => Assert.Equal(expected, task.Result));
        Assert.Equal(1, loaderCount);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(key, out byte[] cached));
        Assert.Equal(expected, cached);
    }

    [Fact]
    public void Failed_loader_is_removed_so_the_same_key_can_retry()
    {
        var cache = new FullTextBlockCache(64);
        FullTextBlockCacheKey key = new(4, 0, 4, 10);
        int loaderCount = 0;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            cache.GetOrLoad(
                key,
                () =>
                {
                    Interlocked.Increment(ref loaderCount);
                    throw new InvalidOperationException("synthetic loader failure");
                }));

        Assert.Equal("synthetic loader failure", error.Message);
        byte[] actual = cache.GetOrLoad(
            key,
            () =>
            {
                Interlocked.Increment(ref loaderCount);
                return [1, 2, 3, 4];
            });

        Assert.Equal([1, 2, 3, 4], actual);
        Assert.Equal(2, loaderCount);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task Clear_during_inflight_load_does_not_repopulate_old_generation()
    {
        var cache = new FullTextBlockCache(64);
        FullTextBlockCacheKey key = new(5, 0, 4, 11);
        using var loaderEntered = new ManualResetEventSlim();
        using var releaseLoader = new ManualResetEventSlim();
        int loaderCount = 0;

        Task<byte[]> oldLoad = Task.Run(() => cache.GetOrLoad(
            key,
            () =>
            {
                Interlocked.Increment(ref loaderCount);
                loaderEntered.Set();
                Assert.True(releaseLoader.Wait(TimeSpan.FromSeconds(2)));
                return [5, 5, 5, 5];
            }));

        Assert.True(loaderEntered.Wait(TimeSpan.FromSeconds(2)));
        cache.Clear();
        Assert.Equal(0, cache.Count);
        releaseLoader.Set();

        Assert.Equal([5, 5, 5, 5], await oldLoad);
        Assert.Equal(0, cache.Count);

        using var freshLoaderEntered = new ManualResetEventSlim();
        using var releaseFreshLoader = new ManualResetEventSlim();
        Task<byte[]>[] freshLoads = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => cache.GetOrLoad(
                key,
                () =>
                {
                    Interlocked.Increment(ref loaderCount);
                    freshLoaderEntered.Set();
                    Assert.True(releaseFreshLoader.Wait(TimeSpan.FromSeconds(2)));
                    return [6, 6, 6, 6];
                })))
            .ToArray();

        Assert.True(freshLoaderEntered.Wait(TimeSpan.FromSeconds(2)));
        releaseFreshLoader.Set();
        await Task.WhenAll(freshLoads);

        Assert.All(freshLoads, task => Assert.Equal([6, 6, 6, 6], task.Result));
        Assert.Equal(2, loaderCount);
        Assert.True(cache.TryGet(key, out byte[] cached));
        Assert.Equal([6, 6, 6, 6], cached);
    }
}
