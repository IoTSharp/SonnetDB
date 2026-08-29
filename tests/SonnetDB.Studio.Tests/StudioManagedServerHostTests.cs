using Xunit;

namespace SonnetDB.Studio.Tests;

public sealed class StudioManagedServerHostTests
{
    [Fact]
    public async Task StartAsync_WhenExecutableIsMissing_ReturnsDiagnosticWithoutRunning()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-studio-tests", Guid.NewGuid().ToString("N"));
        await using var host = new StudioManagedServerHost(Path.Combine(dataRoot, "missing-server.exe"), keepRunningOnExit: false);

        var status = await host.StartAsync(dataRoot, "http://127.0.0.1:0", CancellationToken.None);

        Assert.False(status.IsRunning);
        Assert.False(status.StartedByStudio);
        Assert.False(status.Healthy);
        Assert.Contains("executable was not found", status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(dataRoot), status.DataRoot);
    }

    [Fact]
    public async Task StopAsync_WithoutManagedProcess_IsIdempotent()
    {
        await using var host = new StudioManagedServerHost(null, keepRunningOnExit: false);

        var first = await host.StopAsync(string.Empty, string.Empty, CancellationToken.None);
        var second = await host.StopAsync(string.Empty, string.Empty, CancellationToken.None);

        Assert.False(first.IsRunning);
        Assert.False(second.IsRunning);
        Assert.Equal("http://127.0.0.1:5080", second.Url);
    }

    [Fact]
    public async Task StartAsync_WhenProcessExitsBeforeHealth_WritesManagedServerLog()
    {
        var dotnet = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet",
            "dotnet.exe");
        if (!File.Exists(dotnet))
            return;

        var dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-studio-tests", Guid.NewGuid().ToString("N"));
        await using var host = new StudioManagedServerHost(dotnet, keepRunningOnExit: false);

        var status = await host.StartAsync(dataRoot, "http://127.0.0.1:0", CancellationToken.None);

        Assert.False(status.IsRunning);
        Assert.False(status.Healthy);
        Assert.Contains("exited before becoming healthy", status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(dataRoot, ".studio", "managed-server.log")));
    }
}
