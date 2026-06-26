using Microsoft.Extensions.VectorData;
using SonnetDB.Data;
using SonnetDB.Data.VectorData;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// SonnetDB.Data 的依赖注入扩展方法。
/// </summary>
public static class SonnetDBServiceCollectionExtensions
{
    /// <summary>
    /// 将 <see cref="SonnetDBVectorStore"/> 注册为单例 <see cref="VectorStore"/>。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="connectionFactory">从 <see cref="IServiceProvider"/> 创建 SonnetDB 连接的工厂。</param>
    /// <returns>原 <paramref name="services"/>，便于链式调用。</returns>
    public static IServiceCollection AddSonnetDBVectorStore(
        this IServiceCollection services,
        Func<IServiceProvider, SndbConnection> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        services.AddSingleton<VectorStore>(sp => new SonnetDBVectorStore(connectionFactory(sp), ownsConnection: true));
        return services;
    }
}
