using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Endpoints;
using SonnetDB.Hosting;
using SonnetDB.Mqtt;

namespace SonnetDB;

/// <summary>
/// AOT-friendly Minimal API 入口。
/// </summary>
public static class Program
{
    /// <summary>
    /// 构建并运行 SonnetDB Server。
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        var app = BuildApp(args);
        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// 构造但不启动 <see cref="WebApplication"/>。供测试代码注入自定义配置。
    /// </summary>
    /// <param name="args">命令行参数（透传给 <see cref="WebApplication.CreateSlimBuilder(string[])"/>）。</param>
    /// <param name="configureServices">测试或宿主可选的附加 DI 覆盖。</param>
    public static WebApplication BuildApp(
        string[] args,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        ServerLogging.Configure(builder);

        if (OperatingSystem.IsWindows())
        {
            builder.Services.AddWindowsService(options => options.ServiceName = "SonnetDB");
        }

        builder.Configuration.AddEnvironmentVariables(prefix: "SONNETDB_");
        var configuredServerOptions = ServerOptionsBinder.Bind(builder.Configuration);
        builder.Services.Configure<ServerOptions>(options => ServerOptionsBinder.Bind(builder.Configuration, options));
        builder.Services.PostConfigure<ServerOptions>(ServerOptionsBinder.ApplyDefaults);

        MqttServerBootstrap.ConfigureKestrel(builder, configuredServerOptions.Mqtt);
        SonnetDbServiceRegistration.Configure(builder, configuredServerOptions);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        var serverOptions = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        SonnetDbRequestPipeline.Configure(app, serverOptions);
        app.MapSonnetDbEndpoints(serverOptions);
        EnvironmentBootstrapper.Run(app);
        return app;
    }
}
