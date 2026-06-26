using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace SonnetDB.Hosting;

/// <summary>
/// 配置统一的 Vue SPA：根路径 <c>/</c> 承载产品官网，<c>/admin/*</c> 承载 SonnetDB Studio。
/// 开发期通过 SpaProxy 走 Vite dev server；发布期直接用 wwwroot 下的构建产物 + SPA fallback。
/// </summary>
internal static class SpaEndpoints
{
    private const string DefaultSpaProxyServerUrl = "https://localhost:5173";

    /// <summary>
    /// 不属于 SPA 的 HTTP API / 文档前缀。fallback 命中这些路径不应返回 index.html，
    /// 而是交回管线由对应 endpoint 自行处理（不存在则 404）。
    /// </summary>
    private static readonly string[] _nonSpaPrefixes =
    [
        "/v1/",
        "/help",
        "/healthz",
        "/metrics",
        "/mcp/",
    ];

    /// <summary>
    /// 注册 SPA 相关路由（产品首页、Studio、客户端路由 fallback）。
    /// </summary>
    /// <param name="app">当前 web 应用。</param>
    public static void MapSpa(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            MapDevelopmentSpa(app);
            return;
        }

        var webRoot = app.Environment.WebRootPath;
        if (string.IsNullOrEmpty(webRoot) || !File.Exists(Path.Combine(webRoot, "index.html")))
        {
            app.MapMethods("/", ["GET", "HEAD"], static (HttpContext ctx) => WriteUnavailableAsync(ctx));
            app.MapMethods("/admin/{**path}", ["GET", "HEAD"], static (HttpContext ctx) => WriteUnavailableAsync(ctx));
            return;
        }

        var spaFileProvider = new PhysicalFileProvider(webRoot);

        // 静态资源：assets/、favicon.svg 等所有 dist 产物（含哈希文件名，可以长缓存）。
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = spaFileProvider,
        });

        // SPA 客户端路由统一回 index.html：
        // - 路径必须是 GET / HEAD；
        // - 不能命中真实静态文件（:nonfile 约束）；
        // - 不能命中 HTTP API / 帮助文档前缀（在 handler 内显式判断）。
        app.MapMethods("/", ["GET", "HEAD"], (HttpContext ctx) => ServeSpaIndexAsync(ctx, webRoot));
        app.MapMethods("/{**path:nonfile}", ["GET", "HEAD"], (HttpContext ctx) =>
        {
            var path = ctx.Request.Path.Value ?? string.Empty;
            foreach (var prefix in _nonSpaPrefixes)
            {
                if (path.StartsWith(prefix, StringComparison.Ordinal)
                    || (prefix.EndsWith('/') && path.Equals(prefix.TrimEnd('/'), StringComparison.Ordinal)))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return Task.CompletedTask;
                }
            }
            return ServeSpaIndexAsync(ctx, webRoot);
        });
    }

    private static async Task ServeSpaIndexAsync(HttpContext ctx, string webRoot)
    {
        var indexPath = Path.Combine(webRoot, "index.html");
        var bytes = await File.ReadAllBytesAsync(indexPath, ctx.RequestAborted).ConfigureAwait(false);
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        ctx.Response.ContentLength = bytes.Length;
        if (HttpMethods.IsHead(ctx.Request.Method))
            return;
        await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted).ConfigureAwait(false);
    }

    private static void MapDevelopmentSpa(WebApplication app)
    {
        var proxyServerUrl = GetSpaProxyServerUrl(app.Configuration);

        app.MapMethods("/", ["GET"], (HttpContext ctx) => WriteLaunchPageAsync(ctx, proxyServerUrl, string.Empty));
        app.MapMethods("/{**path:nonfile}", ["GET"], (HttpContext ctx) =>
        {
            var path = ctx.Request.Path.Value ?? string.Empty;
            foreach (var prefix in _nonSpaPrefixes)
            {
                if (path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return Task.CompletedTask;
                }
            }
            var routeValue = ctx.Request.RouteValues.TryGetValue("path", out var value) && value is string text ? text : string.Empty;
            return WriteLaunchPageAsync(ctx, proxyServerUrl, routeValue);
        });
    }

    private static string GetSpaProxyServerUrl(IConfiguration configuration)
    {
        var serverUrl = configuration["SpaProxyServer:ServerUrl"];
        return string.IsNullOrWhiteSpace(serverUrl)
            ? DefaultSpaProxyServerUrl
            : serverUrl.TrimEnd('/');
    }

    private static async Task WriteLaunchPageAsync(HttpContext ctx, string proxyServerUrl, string relativePath)
    {
        var normalizedPath = string.IsNullOrWhiteSpace(relativePath)
            ? string.Empty
            : "/" + relativePath.Trim('/');
        var targetUrl = $"{proxyServerUrl}{normalizedPath}{ctx.Request.QueryString}";
        var probeUrl = $"{proxyServerUrl}/";
        var htmlEncodedTargetUrl = HtmlEncoder.Default.Encode(targetUrl);
        var jsEncodedTargetUrl = JavaScriptEncoder.Default.Encode(targetUrl);
        var jsEncodedProbeUrl = JavaScriptEncoder.Default.Encode(probeUrl);

        var html =
$$"""
<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>SonnetDB Dev Proxy</title>
    <style>
      :root { color-scheme: light; }
      body {
        margin: 0; min-height: 100vh; display: grid; place-items: center;
        background: linear-gradient(135deg, #f7fafc, #edf2f7);
        font-family: "Segoe UI", "Microsoft YaHei UI", sans-serif; color: #1a202c;
      }
      main {
        width: min(640px, calc(100vw - 32px));
        background: rgba(255,255,255,0.92);
        border: 1px solid rgba(148,163,184,0.35);
        border-radius: 20px; padding: 32px;
        box-shadow: 0 24px 60px rgba(15,23,42,0.12);
      }
      h1 { margin: 0 0 12px; font-size: 28px; }
      p { margin: 0 0 12px; line-height: 1.6; }
      code { display: inline-block; padding: 2px 8px; border-radius: 999px; background: #e2e8f0; font-family: Consolas, monospace; }
      a { color: #0f766e; }
    </style>
  </head>
  <body>
    <main>
      <h1>Connecting SonnetDB to the Vite dev server</h1>
      <p>The backend is running in SPA debug mode and will redirect to the frontend as soon as Vite is ready.</p>
      <p>If this is the first run, execute <code>npm install</code> once in the <code>web</code> folder.</p>
      <p>If the browser does not redirect automatically, open: <a href="{{htmlEncodedTargetUrl}}">{{htmlEncodedTargetUrl}}</a></p>
    </main>
    <script>
      const targetUrl = "{{jsEncodedTargetUrl}}";
      const probeUrl = "{{jsEncodedProbeUrl}}";
      const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
      async function redirectWhenReady() {
        for (;;) {
          try {
            await fetch(probeUrl, { cache: 'no-store', mode: 'no-cors' });
            window.location.replace(targetUrl);
            return;
          } catch {
            await sleep(1000);
          }
        }
      }
      redirectWhenReady();
    </script>
  </body>
</html>
""";

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        await ctx.Response.WriteAsync(html).ConfigureAwait(false);
    }

    private static async Task WriteUnavailableAsync(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync(
            "SonnetDB Studio static files are missing. Run `npm install && npm run build` in `web`, or publish the server with `dotnet publish src/SonnetDB/SonnetDB.csproj`."
        ).ConfigureAwait(false);
    }
}
