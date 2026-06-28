using EasyCaching.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Caching;
using SonnetDB.Configuration;
using Xunit;

namespace SonnetDB.Tests;

public sealed class CachingModeEndToEndTests : IAsyncLifetime
{
    private const string _adminToken = "cache-admin";
    private const string _dbName = "cache_e2e";
    private WebApplication? _app;
    private string _baseUrl = string.Empty;
    private string? _dataRoot;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-cache-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [_adminToken] = ServerRoles.Admin,
            },
        });
        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        using var http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _adminToken);
        using var response = await http.PostAsync(
            "/v1/db",
            new StringContent(
                $"{{\"name\":\"{_dbName}\"}}",
                System.Text.Encoding.UTF8,
                "application/json"));
        response.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_dataRoot is not null && Directory.Exists(_dataRoot))
        {
            try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("remote")]
    public void EasyCachingProvider_EmbeddedAndRemote_SupportsKvTtlAndPrefixRemove(string mode)
    {
        using var store = NewStore(mode, "easy-" + Guid.NewGuid().ToString("N"));
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

        provider.Set("expired:1", "gone", TimeSpan.FromMilliseconds(1));
        SpinWait.SpinUntil(() => !provider.Exists("expired:1"), TimeSpan.FromSeconds(3));
        Assert.False(provider.Exists("expired:1"));

        provider.RemoveByPrefix("flow:");
        Assert.False(provider.Exists("flow:1"));
        Assert.False(provider.Exists("flow:2"));
        Assert.True(provider.Exists("other:1"));
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("remote")]
    public async Task DistributedCache_EmbeddedAndRemote_SupportsTtlRefreshRemoveAndJanitor(string mode)
    {
        using var store = NewStore(mode, "distributed-" + Guid.NewGuid().ToString("N"));
        var cache = new SonnetDbDistributedCache(store);

        await cache.SetAsync(
            "session",
            [1, 2, 3],
            new DistributedCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(5),
            });

        Assert.Equal([1, 2, 3], await cache.GetAsync("session"));
        await cache.RefreshAsync("session");
        Assert.Equal([1, 2, 3], cache.Get("session"));
        await cache.RemoveAsync("session");
        Assert.Null(await cache.GetAsync("session"));

        cache.Set(
            "short",
            [9],
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMilliseconds(1),
            });
        await Task.Delay(50);
        Assert.Null(await cache.GetAsync("short"));

        store.Set("expired:janitor", [4], DateTimeOffset.UtcNow.AddMilliseconds(-1));
        store.Set("active:janitor", [5], DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(1, await store.CleanExpiredAsync());
        Assert.Null(await store.GetEntryAsync("expired:janitor"));
        Assert.NotNull(await store.GetEntryAsync("active:janitor"));

        store.Set("prefix:1", [1]);
        store.Set("prefix:2", [2]);
        store.Set("keep:1", [3]);
        Assert.Equal(2, await store.RemovePrefixAsync("prefix:"));
        Assert.Empty(await store.ScanPrefixAsync("prefix:"));
        Assert.NotNull(await store.GetEntryAsync("keep:1"));
    }

    private SonnetDbCacheStore NewStore(string mode, string @namespace)
        => new(new SonnetDbCacheOptions
        {
            ConnectionString = ConnectionString(mode),
            Namespace = @namespace,
            ExpirationScanInterval = TimeSpan.Zero,
        });

    private string ConnectionString(string mode)
    {
        if (string.Equals(mode, "remote", StringComparison.Ordinal))
            return $"Data Source=sonnetdb+http://{new Uri(_baseUrl).Authority}/{_dbName};Token={_adminToken};Timeout=30";

        var root = Path.Combine(
            _dataRoot ?? Path.GetTempPath(),
            "embedded-cache-" + Guid.NewGuid().ToString("N"));
        return $"Data Source={root}";
    }
}
