using EasyCaching.Core.Configurations;
using SonnetDB.Caching.EasyCaching;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// EasyCaching SonnetDB Provider 配置扩展。
/// </summary>
public static class SonnetDbEasyCachingOptionsExtensions
{
    /// <summary>
    /// 使用 SonnetDB KV keyspace 作为 EasyCaching Provider。
    /// </summary>
    /// <param name="options">EasyCaching 配置对象。</param>
    /// <param name="configure">SonnetDB 连接、keyspace、namespace 与过期策略配置。</param>
    /// <param name="name">EasyCaching provider 名称。</param>
    /// <returns>原始 EasyCaching 配置对象，便于链式配置其他 provider。</returns>
    public static EasyCachingOptions UseSonnetDB(
        this EasyCachingOptions options,
        Action<SonnetDbEasyCachingOptions> configure,
        string name = "DefaultSonnetDB")
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        options.RegisterExtension(new SonnetDbEasyCachingOptionsExtension(name, configure));
        return options;
    }
}
