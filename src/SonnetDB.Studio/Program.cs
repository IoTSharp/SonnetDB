using NativeWebHost;
using NativeWebHost.Windows;

namespace SonnetDB.Studio;

/// <summary>
/// SonnetDB Studio 桌面宿主入口。
/// </summary>
internal static class Program
{
    private const string DefaultServerUrl = "http://localhost:5080";

    public static async Task Main(string[] args)
    {
        var options = StudioHostOptions.Parse(args);
        var studioUrl = BuildStudioUrl(options.ServerUrl, options.Route);

        var app = NativeWebApp.CreateBuilder(args)
            .Configure(nativeOptions =>
            {
                nativeOptions.Title = "SonnetDB Studio";
                nativeOptions.CustomScheme = "app";
                nativeOptions.ContentRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                nativeOptions.StartUrl = studioUrl;
                nativeOptions.Width = options.Width;
                nativeOptions.Height = options.Height;
            })
            .UseAdapter(new NativeWebView2AdapterFactory())
            .UseRuntime(new Win32Runtime())
            .Build();

        await app.RunAsync().ConfigureAwait(false);
    }

    private static string BuildStudioUrl(string serverUrl, string route)
    {
        var normalizedServer = string.IsNullOrWhiteSpace(serverUrl)
            ? DefaultServerUrl
            : serverUrl.Trim().TrimEnd('/');
        var normalizedRoute = string.IsNullOrWhiteSpace(route)
            ? "/admin/app/studio"
            : route.Trim();

        if (!normalizedRoute.StartsWith('/'))
            normalizedRoute = "/" + normalizedRoute;

        return normalizedServer + normalizedRoute;
    }
}

internal sealed record StudioHostOptions(string ServerUrl, string Route, int Width, int Height)
{
    private const string DefaultServerUrl = "http://localhost:5080";

    public static StudioHostOptions Parse(string[] args)
    {
        var serverUrl = ReadOption(args, "--server-url") ?? DefaultServerUrl;
        var route = ReadOption(args, "--route") ?? "/admin/app/studio";
        var width = ReadIntOption(args, "--width") ?? 1440;
        var height = ReadIntOption(args, "--height") ?? 920;
        return new StudioHostOptions(serverUrl, route, width, height);
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];

            var prefix = name + "=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return args[i][prefix.Length..];
        }

        return null;
    }

    private static int? ReadIntOption(string[] args, string name)
    {
        var value = ReadOption(args, name);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
    }
}
