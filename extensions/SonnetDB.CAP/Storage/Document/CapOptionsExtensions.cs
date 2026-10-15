using DotNetCore.CAP;
using DotNetCore.CAP.Internal;
using DotNetCore.CAP.Persistence;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.CAP.Storage.Document;

namespace DotNetCore.CAP;

/// <summary>CAP 的 SonnetDB 原生文档存储注册入口。</summary>
public static class SonnetDbDocumentCapOptionsExtensions
{
    /// <summary>使用 SonnetDB Document Store 保存 CAP 消息与锁。</summary>
    /// <param name="options">CAP 选项。</param>
    /// <param name="configure">文档存储配置。</param>
    /// <returns>原 CAP 选项。</returns>
    public static CapOptions UseSonnetDbDocumentStorage(this CapOptions options, Action<SonnetDbCapDocumentOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configure);
        options.RegisterExtension(new DocumentStorageExtension(configure));
        return options;
    }
}

internal sealed class DocumentStorageExtension(Action<SonnetDbCapDocumentOptions> configure) : ICapOptionsExtension
{
    public void AddServices(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(CapStorageMarkerService)))
            throw new InvalidOperationException("每个 CAP 实例只能注册一个存储实现。");
        services.AddSingleton(new CapStorageMarkerService("SonnetDB.Document"));
        services.AddOptions<SonnetDbCapDocumentOptions>().Configure(configure)
            .Validate(options => options.Client is not null || !string.IsNullOrWhiteSpace(options.ConnectionString), "必须提供 Client 或 ConnectionString。")
            .Validate(options => !string.IsNullOrWhiteSpace(options.CollectionName) && options.CollectionName.Length <= 128, "CollectionName 必须为 1 到 128 个字符。")
            .Validate(options => options.TimeProvider is not null, "TimeProvider 不可为空。");
        services.AddSingleton<CapDocumentStore>();
        services.AddSingleton<IStorageInitializer>(provider => provider.GetRequiredService<CapDocumentStore>());
        services.AddSingleton<IDataStorage, SonnetDbDocumentStorage>();
    }
}
