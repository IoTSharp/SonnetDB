using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SonnetDB.Configuration;

namespace SonnetDB.Hosting;

/// <summary>
/// 把构建后的帮助文档挂载到 <c>/help/*</c>。
/// </summary>
internal static class HelpDocsEndpoints
{
    /// <summary>
    /// 注册帮助文档路由：<c>GET /help</c> 和 <c>GET /help/{**path}</c>。
    /// </summary>
    public static void MapHelpDocs(this IEndpointRouteBuilder app, ServerOptions serverOptions)
    {
        var helpRoot = ResolveHelpRoot(serverOptions);

        app.MapMethods("/help", ["GET"], (RequestDelegate)(ctx => ServeAsync(ctx, string.Empty, helpRoot)));
        app.MapMethods("/help/{**path}", ["GET"], (RequestDelegate)(ctx =>
        {
            var path = ctx.Request.RouteValues.TryGetValue("path", out var value) && value is string text
                ? text
                : string.Empty;
            return ServeAsync(ctx, path, helpRoot);
        }));
    }

    private static async Task ServeAsync(HttpContext ctx, string relativePath, string helpRoot)
    {
        var path = relativePath.Trim('/');

        var candidates = string.IsNullOrEmpty(path)
            ? new[] { "index.html" }
            : Path.HasExtension(path)
                ? new[] { path }
                : new[] { path, $"{path}.html", $"{path}/index.html" };

        foreach (var candidate in candidates)
        {
            if (TryResolveFile(helpRoot, candidate, out var filePath))
            {
                await WriteAsync(ctx, filePath).ConfigureAwait(false);
                return;
            }
        }

        if (!File.Exists(Path.Combine(helpRoot, "index.html")))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync(
                "SonnetDB Help docs are not built. Run `dotnet tool restore` and `dotnet tool run jekyllnet build --source docs --destination src/SonnetDB/wwwroot/help` before publishing."
            ).ConfigureAwait(false);
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static string ResolveHelpRoot(ServerOptions serverOptions)
    {
        if (string.IsNullOrWhiteSpace(serverOptions.HelpDocsRoot))
            return Path.Combine(AppContext.BaseDirectory, "wwwroot", "help");

        return Path.GetFullPath(serverOptions.HelpDocsRoot, AppContext.BaseDirectory);
    }

    private static bool TryResolveFile(string helpRoot, string candidatePath, out string filePath)
    {
        var root = Path.GetFullPath(helpRoot);
        var candidate = candidatePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, candidate));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootedPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootedPrefix, comparison))
        {
            filePath = string.Empty;
            return false;
        }

        if (!File.Exists(fullPath))
        {
            filePath = string.Empty;
            return false;
        }

        filePath = fullPath;
        return true;
    }

    private static async Task WriteAsync(HttpContext ctx, string filePath)
    {
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = StaticAssetContentTypes.Guess(filePath);
        ctx.Response.ContentLength = new FileInfo(filePath).Length;

        var requestPath = ctx.Request.Path.Value ?? string.Empty;
        if (requestPath.EndsWith("/help", StringComparison.Ordinal)
            || requestPath.EndsWith("/help/", StringComparison.Ordinal)
            || requestPath.EndsWith(".html", StringComparison.Ordinal))
        {
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        }
        else
        {
            ctx.Response.Headers.CacheControl = "public, max-age=86400";
        }

        await ctx.Response.SendFileAsync(filePath, 0, null, ctx.RequestAborted).ConfigureAwait(false);
    }
}
