using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace SonnetDB.Hosting;

/// <summary>
/// 配置 Server 控制台日志格式与 Activity 关联字段。
/// </summary>
internal static class ServerLogging
{
    /// <summary>
    /// 配置生产 JSON 行与开发单行文本控制台日志。
    /// </summary>
    /// <param name="builder">Web 应用构建器。</param>
    internal static void Configure(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Logging.ClearProviders();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.Configure(options =>
        {
            options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId
                | ActivityTrackingOptions.SpanId
                | ActivityTrackingOptions.ParentId
                | ActivityTrackingOptions.Tags;
        });

        if (UsesJsonConsole(builder.Environment.EnvironmentName))
        {
            builder.Logging.AddJsonConsole(options =>
            {
                options.IncludeScopes = true;
                options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
                options.UseUtcTimestamp = true;
                options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
            });
            return;
        }

        builder.Logging.AddSimpleConsole(options =>
        {
            options.IncludeScopes = true;
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss.fff ";
            options.UseUtcTimestamp = true;
            options.ColorBehavior = LoggerColorBehavior.Enabled;
        });
    }

    /// <summary>
    /// 判断指定宿主环境是否应使用 JSON 控制台 formatter。
    /// </summary>
    /// <param name="environmentName">宿主环境名称。</param>
    /// <returns>非 Development 环境返回 <c>true</c>。</returns>
    internal static bool UsesJsonConsole(string? environmentName)
        => !string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
}
