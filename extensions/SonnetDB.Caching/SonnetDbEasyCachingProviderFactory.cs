using EasyCaching.Core;

namespace SonnetDB.Caching;

/// <summary>
/// 只暴露 SonnetDB Provider 的 EasyCaching factory。
/// </summary>
public sealed class SonnetDbEasyCachingProviderFactory : IEasyCachingProviderFactory
{
    private readonly IEasyCachingProvider _provider;

    /// <summary>
    /// 初始化 SonnetDB EasyCaching factory。
    /// </summary>
    public SonnetDbEasyCachingProviderFactory(IEasyCachingProvider provider)
    {
        _provider = provider;
    }

    /// <inheritdoc />
    public IEasyCachingProvider GetCachingProvider(string name)
    {
        if (string.Equals(name, _provider.Name, StringComparison.Ordinal))
            return _provider;

        throw new InvalidOperationException($"EasyCaching provider '{name}' is not registered in SonnetDB cache mode.");
    }

    /// <inheritdoc />
    public IRedisCachingProvider GetRedisProvider(string name) =>
        throw new NotSupportedException("SonnetDB cache mode does not expose an IRedisCachingProvider.");
}
