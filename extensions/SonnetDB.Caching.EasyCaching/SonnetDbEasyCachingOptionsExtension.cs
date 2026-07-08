using EasyCaching.Core;
using EasyCaching.Core.Configurations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SonnetDB.Caching.EasyCaching;

internal sealed class SonnetDbEasyCachingOptionsExtension : IEasyCachingOptionsExtension
{
    private readonly Action<SonnetDbEasyCachingOptions> _configure;
    private readonly string _name;

    public SonnetDbEasyCachingOptionsExtension(string name, Action<SonnetDbEasyCachingOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        _name = name;
        _configure = configure;
    }

    public void AddServices(IServiceCollection services)
    {
        services.AddOptions();
        services.Configure(_name, _configure);
        services.TryAddSingleton<IEasyCachingProviderFactory, DefaultEasyCachingProviderFactory>();
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<SonnetDbEasyCachingOptions>>().Get(_name);
            return new SonnetDbEasyCachingRegistration(_name, options);
        });
        services.AddSingleton<IEasyCachingProvider>(sp =>
        {
            var registration = GetRegistration(sp);
            return new SonnetDbEasyCachingProvider(registration.Name, registration.Store);
        });
        services.AddSingleton<IHostedService>(sp =>
        {
            var registration = GetRegistration(sp);
            var logger = sp.GetRequiredService<ILogger<SonnetDbEasyCachingJanitor>>();
            return new SonnetDbEasyCachingJanitor(registration, logger);
        });
    }

    private SonnetDbEasyCachingRegistration GetRegistration(IServiceProvider services) =>
        services.GetServices<SonnetDbEasyCachingRegistration>()
            .First(registration => string.Equals(registration.Name, _name, StringComparison.Ordinal));
}
