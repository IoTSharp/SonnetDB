using DotNetCore.CAP;
using DotNetCore.CAP.Transport;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.CAP.Transport;

namespace DotNetCore.CAP;

/// <summary>CAP 的 SonnetDB MQ 传输注册入口。</summary>
public static class SonnetDbTransportCapOptionsExtensions
{
    /// <summary>使用 SonnetDB MQ 传输 CAP 消息，存储实现单独配置。</summary>
    /// <param name="options">CAP 选项。</param>
    /// <param name="configure">MQ 传输配置。</param>
    /// <returns>原 CAP 选项。</returns>
    public static CapOptions UseSonnetDbTransport(this CapOptions options, Action<SonnetDbCapTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configure);
        if (options.ConsumerThreadCount != 1)
            throw new NotSupportedException("SonnetDB CAP 传输要求 ConsumerThreadCount=1，以保持逐条确认。");
        options.RegisterExtension(new SonnetDbTransportExtension(configure));
        return options;
    }
}

internal sealed class SonnetDbTransportExtension(Action<SonnetDbCapTransportOptions> configure) : ICapOptionsExtension
{
    public void AddServices(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(CapMessageQueueMakerService)))
            throw new InvalidOperationException("每个 CAP 实例只能注册一个传输实现。");
        services.AddSingleton(new CapMessageQueueMakerService("SonnetDB.MQ"));
        services.AddOptions<SonnetDbCapTransportOptions>().Configure(configure)
            .Validate(options => options.Client is not null || !string.IsNullOrWhiteSpace(options.ConnectionString), "必须提供 Client 或 ConnectionString。")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Namespace), "Namespace 不可为空。")
            .Validate(options => options.PollInterval >= TimeSpan.FromMilliseconds(10) && options.PollInterval <= TimeSpan.FromSeconds(5), "PollInterval 必须为 10ms 到 5s。")
            .Validate(options => options.MaxEnvelopeBytes is > 0 and <= 16 * 1024 * 1024, "信封预算必须为 1 到 16MiB 字节。");
        services.PostConfigure<CapOptions>(options =>
        {
            if (options.ConsumerThreadCount != 1)
                throw new NotSupportedException("SonnetDB CAP 传输要求 ConsumerThreadCount=1。");
        });
        services.AddSingleton<CapMqConnection>();
        services.AddSingleton<ITransport, SonnetDbTransport>();
        services.AddSingleton<IConsumerClientFactory, SonnetDbConsumerClientFactory>();
    }
}
