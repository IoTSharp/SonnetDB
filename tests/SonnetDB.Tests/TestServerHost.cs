using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;

namespace SonnetDB.Tests;

internal static class TestServerHost
{
    public static WebApplication Build(
        ServerOptions options,
        Action<IServiceCollection>? configureServices = null,
        string[]? extraArgs = null)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-test-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        WriteAppSettings(contentRoot, options);

        string[] args = ["--contentRoot", contentRoot, "--Kestrel:Endpoints:Http:Url=http://127.0.0.1:0"];
        if (extraArgs is { Length: > 0 })
            args = [.. args, .. extraArgs];

        var app = Program.BuildApp(args, configureServices);

        app.Lifetime.ApplicationStopped.Register(static state =>
        {
            var root = (string)state!;
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best effort cleanup for test host files
            }
        }, contentRoot);

        return app;
    }

    private static void WriteAppSettings(string contentRoot, ServerOptions options)
    {
        var settings = new AppSettings(options);
        var json = JsonSerializer.Serialize(settings);
        File.WriteAllText(Path.Combine(contentRoot, "appsettings.json"), json);
    }

    private sealed record AppSettings(ServerOptions SonnetDBServer);
}
