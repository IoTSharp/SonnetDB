using EasyCaching.Core;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Caching;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class SonnetDbCachingProviderTests : IDisposable
{
    private readonly string _root;

    public SonnetDbCachingProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void EasyCachingProvider_SetGetBatchAndPrefix_Works()
    {
        using var store = NewStore();
        var provider = new SonnetDbEasyCachingProvider("sonnetdb", store);

        provider.Set("flow:1", "alpha", TimeSpan.FromMinutes(1));
        provider.SetAll(new Dictionary<string, int>
        {
            ["flow:2"] = 2,
            ["other:1"] = 9,
        }, TimeSpan.FromMinutes(1));

        Assert.Equal("alpha", provider.Get<string>("flow:1").Value);
        Assert.True(provider.Exists("flow:2"));
        Assert.Equal(2, provider.GetCount("flow:"));

        provider.RemoveByPrefix("flow:");
        Assert.False(provider.Exists("flow:1"));
        Assert.True(provider.Exists("other:1"));
    }

    [Fact]
    public void EasyCachingProvider_DiRegistration_ResolvesNamedProvider()
    {
        var services = new ServiceCollection();
        services.AddSonnetDbEasyCaching("CachingUseIn-SonnetDB", options =>
        {
            options.ConnectionString = $"Data Source={_root}";
            options.Namespace = "iotsharp";
            options.ExpirationScanInterval = TimeSpan.Zero;
        });

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IEasyCachingProviderFactory>();
        var cache = factory.GetCachingProvider("CachingUseIn-SonnetDB");

        cache.Set("rule:1", new CacheProbe("flow", 7), TimeSpan.FromMinutes(1));

        var value = cache.Get<CacheProbe>("rule:1");
        Assert.True(value.HasValue);
        Assert.Equal(new CacheProbe("flow", 7), value.Value);
    }

    [Fact]
    public void CacheStore_CleanExpired_RemovesOnlyExpiredKeys()
    {
        using var store = NewStore();

        store.Set("expired", [1], DateTimeOffset.UtcNow.AddSeconds(-1));
        store.Set("active", [2], DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal(1, store.CleanExpired());
        Assert.Null(store.GetEntry("expired"));
        Assert.NotNull(store.GetEntry("active"));
    }

    [Fact]
    public async Task DistributedCache_SlidingExpiration_RefreshExtendsTtl()
    {
        using var store = NewStore();
        var cache = new SonnetDbDistributedCache(store);

        cache.Set(
            "session",
            [1, 2, 3],
            new DistributedCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(5),
            });

        Assert.Equal([1, 2, 3], cache.Get("session"));
        await cache.RefreshAsync("session");
        Assert.Equal([1, 2, 3], await cache.GetAsync("session"));
        await cache.RemoveAsync("session");
        Assert.Null(cache.Get("session"));
    }

    [Fact]
    public void DistributedCache_AbsoluteExpiration_ReturnsMiss()
    {
        using var store = NewStore();
        var cache = new SonnetDbDistributedCache(store);

        cache.Set(
            "session",
            [9],
            new DistributedCacheEntryOptions
            {
                AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(-1),
            });

        Assert.Null(cache.Get("session"));
    }

    private SonnetDbCacheStore NewStore() => new(new SonnetDbCacheOptions
    {
        ConnectionString = $"Data Source={_root}",
        Namespace = "tests",
        ExpirationScanInterval = TimeSpan.Zero,
    });

    private sealed record CacheProbe(string Name, int Version);
}
