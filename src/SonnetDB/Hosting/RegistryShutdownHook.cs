using Microsoft.Extensions.Hosting;

namespace SonnetDB.Hosting;

/// <summary>
/// 在宿主关闭时释放注册表内持有的数据库实例。
/// </summary>
internal sealed class RegistryShutdownHook(TsdbRegistry registry) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        registry.Dispose();
        return Task.CompletedTask;
    }
}
