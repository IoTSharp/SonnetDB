using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Diagnostics;
using SonnetDB.Json;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    /// <summary>
    /// 映射管理后台“关于”页面使用的只读服务器信息端点。
    /// </summary>
    private static void MapSystemAboutEndpoints(this WebApplication app)
    {
        var about = app.Services.GetRequiredService<SystemAboutService>();
        app.MapGet("/v1/system/about", (HttpContext ctx) =>
        {
            if (BearerAuthMiddleware.GetRole(ctx) is not (ServerRoles.Admin or ServerRoles.ReadWrite or ServerRoles.ReadOnly))
                return ForbiddenResult("当前凭据无权读取服务器信息。");

            return Results.Json(about.Capture(), ServerJsonContext.Default.SystemAboutResponse);
        });
    }
}
