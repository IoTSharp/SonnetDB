using Microsoft.Extensions.Configuration;
using SonnetDB.Configuration;

namespace SonnetDB.Hosting;

/// <summary>
/// 负责服务器配置绑定与启动期默认值补齐。
/// </summary>
internal static class ServerOptionsBinder
{
    private static readonly string[] DefaultCopilotDocsRoots =
    [
        "./docs",
        "./web/help",
        "./src/SonnetDB/wwwroot/help",
    ];

    /// <summary>
    /// 从配置源绑定完整服务器选项，并补齐运行所需的默认值。
    /// </summary>
    /// <param name="configuration">应用配置根。</param>
    public static ServerOptions Bind(IConfiguration configuration)
    {
        var options = new ServerOptions();
        Bind(configuration, options);
        ApplyDefaults(options);
        return options;
    }

    /// <summary>
    /// 绑定 <see cref="ServerOptions"/>，保留配置系统覆盖集合属性的语义。
    /// </summary>
    /// <param name="configuration">应用配置根。</param>
    /// <param name="options">待填充的服务器选项。</param>
    public static void Bind(IConfiguration configuration, ServerOptions options)
    {
        options.Copilot.Docs.Roots.Clear();
        configuration.GetSection("SonnetDBServer").Bind(options);
    }

    /// <summary>
    /// 补齐配置中未显式提供的服务器默认值。
    /// </summary>
    /// <param name="options">待补齐的服务器选项。</param>
    public static void ApplyDefaults(ServerOptions options)
    {
        if (options.Copilot.Docs.Roots.Count == 0)
            options.Copilot.Docs.Roots.AddRange(DefaultCopilotDocsRoots);
    }
}
