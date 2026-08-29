using Xunit;

namespace SonnetDB.Studio.Tests;

public sealed class StudioHostOptionsTests
{
    [Fact]
    public void Parse_Defaults_UseUserDataRootAndManagedServer()
    {
        var options = StudioHostOptions.Parse([]);

        Assert.Equal("http://localhost:5080", options.ServerUrl);
        Assert.Equal("/admin/app/studio", options.Route);
        Assert.Equal(54980, options.BridgePort);
        Assert.True(options.BridgeEnabled);
        Assert.True(options.AutoStartManagedServer);
        Assert.Contains(Path.Combine("SonnetDB", "Studio", "data"), options.DataRoot, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine("SonnetDB", "Studio", "connections.json"), options.ConnectionLibraryPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ExplicitArguments_OverrideDefaults()
    {
        var options = StudioHostOptions.Parse([
            "--server-url", "http://remote:5080/",
            "--route=/admin/app/sql",
            "--bridge-port", "55001",
            "--data-root", ".\\studio-data",
            "--server-exe", ".\\server.exe",
            "--no-bridge",
            "--no-auto-start-server",
            "--keep-managed-server",
        ]);

        Assert.Equal("http://remote:5080", options.ServerUrl);
        Assert.Equal("/admin/app/sql", options.Route);
        Assert.Equal(55001, options.BridgePort);
        Assert.False(options.BridgeEnabled);
        Assert.False(options.AutoStartManagedServer);
        Assert.True(options.KeepManagedServer);
        Assert.EndsWith(Path.Combine("studio-data"), options.DataRoot, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("server.exe"), options.ServerExecutable!, StringComparison.OrdinalIgnoreCase);
    }
}
