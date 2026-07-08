using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SonnetDB.Caching.Distributed;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// SonnetDB IDistributedCache 服务注册扩展。
/// </summary>
public static class SonnetDbDistributedCacheServiceCollectionExtensions
{
    /// <summary>
    /// 注册基于 SonnetDB KV keyspace 的 <see cref="IDistributedCache"/>。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">SonnetDB 连接、keyspace、namespace 与后台清理配置。</param>
    /// <returns>原始服务集合，便于链式注册其他服务。</returns>
    public static IServiceCollection AddDistributedSonnetDBCache(
        this IServiceCollection services,
        Action<SonnetDbDistributedCacheOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions();
        services.Configure(configure);
        services.Add(ServiceDescriptor.Singleton<IDistributedCache, SonnetDbDistributedCache>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SonnetDbDistributedCacheJanitor>());
        return services;
    }
}
