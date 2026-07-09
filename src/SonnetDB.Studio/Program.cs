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
        await using var bridge = options.BridgeEnabled ? new StudioBridgeHost(options) : null;
        if (bridge is not null)
        {
            await bridge.StartAsync(CancellationToken.None).ConfigureAwait(false);
            if (options.AutoStartManagedServer)
            {
                var status = await bridge.StartManagedServerAsync(CancellationToken.None).ConfigureAwait(false);
                if (!status.Healthy && !string.IsNullOrWhiteSpace(status.Error))
                    Console.Error.WriteLine(status.Error);
            }
        }

        var studioUrl = BuildStudioUrl(options.ServerUrl, options.Route, bridge?.EndpointUrl, bridge?.Token);

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

    private static string BuildStudioUrl(string serverUrl, string route, string? bridgeUrl, string? bridgeToken)
    {
        var normalizedServer = string.IsNullOrWhiteSpace(serverUrl)
            ? DefaultServerUrl
            : serverUrl.Trim().TrimEnd('/');
        var normalizedRoute = string.IsNullOrWhiteSpace(route)
            ? "/admin/app/studio"
            : route.Trim();

        if (!normalizedRoute.StartsWith('/'))
            normalizedRoute = "/" + normalizedRoute;

        var url = normalizedServer + normalizedRoute;
        if (string.IsNullOrWhiteSpace(bridgeUrl) || string.IsNullOrWhiteSpace(bridgeToken))
            return url;

        var separator = url.Contains('?') ? "&" : "?";
        return url
            + separator
            + "studioBridgeUrl="
            + Uri.EscapeDataString(bridgeUrl)
            + "&studioBridgeToken="
            + Uri.EscapeDataString(bridgeToken);
    }
}

internal sealed record StudioHostOptions(
    string ServerUrl,
    string Route,
    int Width,
    int Height,
    bool BridgeEnabled,
    int BridgePort,
    string DataRoot,
    string ManagedServerUrl,
    string ConnectionLibraryPath,
    string? ServerExecutable,
    bool AutoStartManagedServer,
    bool KeepManagedServer)
{
    private const string DefaultServerUrl = "http://localhost:5080";

    /// <summary>
    /// 解析 Studio 桌面宿主启动参数。
    /// </summary>
    /// <param name="args">命令行参数。</param>
    public static StudioHostOptions Parse(string[] args)
    {
        var explicitServerUrl = ReadOption(args, "--server-url");
        var managedServerUrl = ReadOption(args, "--managed-server-url") ?? DefaultServerUrl;
        var serverUrl = explicitServerUrl ?? managedServerUrl;
        var route = ReadOption(args, "--route") ?? "/admin/app/studio";
        var width = ReadIntOption(args, "--width") ?? 1440;
        var height = ReadIntOption(args, "--height") ?? 920;
        var bridgeEnabled = !HasFlag(args, "--no-bridge");
        var bridgePort = ReadIntOption(args, "--bridge-port") ?? 54980;
        var dataRoot = ReadOption(args, "--data-root") ?? DefaultDataRoot();
        var connectionLibraryPath = ReadOption(args, "--connection-library")
            ?? Path.Combine(DefaultStudioHome(), "connections.json");
        var serverExecutable = ReadOption(args, "--server-exe");
        var autoStart = HasFlag(args, "--auto-start-server")
            || (explicitServerUrl is null && !HasFlag(args, "--no-auto-start-server"));
        var keepManagedServer = HasFlag(args, "--keep-managed-server");
        return new StudioHostOptions(
            serverUrl,
            route,
            width,
            height,
            bridgeEnabled,
            bridgePort,
            Path.GetFullPath(dataRoot),
            managedServerUrl.Trim().TrimEnd('/'),
            connectionLibraryPath,
            serverExecutable,
            autoStart,
            keepManagedServer);
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

    private static bool HasFlag(string[] args, string name)
        => args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    private static int? ReadIntOption(string[] args, string name)
    {
        var value = ReadOption(args, name);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
    }

    private static string DefaultDataRoot()
        => Path.Combine(DefaultStudioHome(), "data");

    private static string DefaultStudioHome()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = AppContext.BaseDirectory;
        return Path.Combine(localAppData, "SonnetDB", "Studio");
    }
}
