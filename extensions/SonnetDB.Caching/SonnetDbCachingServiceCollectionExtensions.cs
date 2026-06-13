using EasyCaching.Core;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace SonnetDB.Caching;

/// <summary>
/// SonnetDB 缓存 Provider 的 DI 注册扩展。
/// </summary>
public static class SonnetDbCachingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 SonnetDB KV-backed EasyCaching Provider。
    /// </summary>
    public static IServiceCollection AddSonnetDbEasyCaching(
        this IServiceCollection services,
        string name,
        Action<SonnetDbCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        services.AddSingleton(CreateStore(configure));
        services.AddSingleton<IEasyCachingProvider>(sp => new SonnetDbEasyCachingProvider(
            name,
            sp.GetRequiredService<SonnetDbCacheStore>()));
        services.AddSingleton<IEasyCachingProviderFactory, SonnetDbEasyCachingProviderFactory>();
        AddJanitorIfEnabled(services, configure);
        return services;
    }

    /// <summary>
    /// 注册 SonnetDB KV-backed <see cref="IDistributedCache"/>。
    /// </summary>
    public static IServiceCollection AddSonnetDbDistributedCache(
        this IServiceCollection services,
        Action<SonnetDbCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(CreateStore(configure));
        services.AddSingleton<IDistributedCache, SonnetDbDistributedCache>();
        AddJanitorIfEnabled(services, configure);
        return services;
    }

    private static SonnetDbCacheStore CreateStore(Action<SonnetDbCacheOptions>? configure)
    {
        var options = new SonnetDbCacheOptions();
        configure?.Invoke(options);
        return new SonnetDbCacheStore(options);
    }

    private static void AddJanitorIfEnabled(IServiceCollection services, Action<SonnetDbCacheOptions>? configure)
    {
        var options = new SonnetDbCacheOptions();
        configure?.Invoke(options);
        if (options.ExpirationScanInterval > TimeSpan.Zero)
            services.AddHostedService<SonnetDbCacheJanitor>();
    }
}
