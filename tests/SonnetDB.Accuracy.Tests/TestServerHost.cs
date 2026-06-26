using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using SonnetDB;
using SonnetDB.Configuration;

namespace SonnetDB.Accuracy.Tests;

internal static class TestServerHost
{
    public static WebApplication Build(ServerOptions options)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-accuracy-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);

        var settings = new AppSettings(options);
        var json = JsonSerializer.Serialize(settings);
        File.WriteAllText(Path.Combine(contentRoot, "appsettings.json"), json);

        var app = Program.BuildApp(["--contentRoot", contentRoot, "--Kestrel:Endpoints:Http:Url=http://127.0.0.1:0"]);
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

    private sealed record AppSettings(ServerOptions SonnetDBServer);
}
