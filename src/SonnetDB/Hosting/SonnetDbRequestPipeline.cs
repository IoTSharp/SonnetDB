using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Auth;
using SonnetDB.Coap;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Endpoints;
using SonnetDB.Json;
using SonnetDB.Mqtt;

namespace SonnetDB.Hosting;

/// <summary>
/// 配置 SonnetDB HTTP 请求管线与协议运行期中间件。
/// </summary>
internal static class SonnetDbRequestPipeline
{
    /// <summary>
    /// 在端点映射前挂载认证、MCP 数据库绑定和协议中间件。
    /// </summary>
    /// <param name="app">已构建的 Web 应用。</param>
    /// <param name="serverOptions">运行期服务器配置。</param>
    public static void Configure(WebApplication app, ServerOptions serverOptions)
    {
        MqttServerBootstrap.ConfigureMiddleware(app, serverOptions);
        CoapServerBootstrap.ConfigureMiddleware(app, serverOptions);

        var userStore = app.Services.GetRequiredService<UserStore>();
        var grants = app.Services.GetRequiredService<GrantsStore>();
        var registry = app.Services.GetRequiredService<TsdbRegistry>();

        app.Use(async (context, next) =>
        {
            var status = BearerAuthMiddleware.Authenticate(context, serverOptions, userStore);
            if (status is not null)
            {
                context.Response.StatusCode = status.Value;
                context.Response.ContentType = "application/json; charset=utf-8";
                var err = new ErrorResponse(status.Value == 401 ? "unauthorized" : "forbidden",
                    status.Value == 401 ? "缺失或无效的 Bearer token。" : "权限不足。");
                await JsonSerializer.SerializeAsync(context.Response.Body, err, ServerJsonContext.Default.ErrorResponse).ConfigureAwait(false);
                return;
            }
            await next(context).ConfigureAwait(false);
        });

        app.Use(async (context, next) =>
        {
            if (await SonnetDbEndpoints.TryBindMcpDatabaseAsync(context, registry, grants).ConfigureAwait(false))
                return;
            await next(context).ConfigureAwait(false);
        });
    }
}
