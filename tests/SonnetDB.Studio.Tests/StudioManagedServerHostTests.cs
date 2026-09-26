using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Studio.Tests;

public sealed class StudioManagedServerHostTests
{
    [Fact]
    public async Task StartEmbeddedAsync_WithExistingDatabase_MountsItAndRejectsInvalidSwitch()
    {
        var root = CreateDataRoot();
        var databasePath = Path.Combine(root, "source", "inspection");
        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath }))
            SqlExecutor.Execute(database, "CREATE TABLE inspections (id INT, PRIMARY KEY (id))");

        var controlRoot = Path.Combine(root, "control");
        await using var host = new StudioManagedServerHost(GetServerExecutable(), keepRunningOnExit: false);
        var url = CreateLoopbackUrl();
        var opened = await host.StartEmbeddedAsync(databasePath, controlRoot, url, CancellationToken.None);

        Assert.True(opened.Healthy, opened.Error);
        Assert.True(opened.StartedByStudio);
        Assert.Equal("inspection", opened.MountedDatabaseName);
        Assert.Equal(Path.GetFullPath(databasePath), opened.MountedDatabasePath);

        var rejected = await host.StartEmbeddedAsync(Path.Combine(root, "missing"), controlRoot, url, CancellationToken.None);
        Assert.NotNull(rejected.Error);
        Assert.Equal(opened.ProcessId, rejected.ProcessId);
        Assert.Equal(opened.MountedDatabasePath, rejected.MountedDatabasePath);

        var lockedPath = Path.Combine(root, "source", "locked");
        using (var locked = Tsdb.Open(new TsdbOptions { RootDirectory = lockedPath }))
        {
            var failedSwitch = await host.StartEmbeddedAsync(lockedPath, controlRoot, url, CancellationToken.None);
            Assert.True(failedSwitch.Healthy, failedSwitch.Error);
            Assert.True(failedSwitch.StartedByStudio);
            Assert.NotNull(failedSwitch.Error);
            Assert.Equal(opened.MountedDatabasePath, failedSwitch.MountedDatabasePath);
        }

        var stopped = await host.StopAsync(controlRoot, url, CancellationToken.None);
        Assert.False(stopped.StartedByStudio);
        Directory.Delete(root, recursive: true);
    }

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

    [Fact]
    public async Task StartAsync_WhenExternalHealthyServerOwnsTargetPort_DoesNotStartOrStopIt()
    {
        await using var externalServer = new ExternalHealthServer();
        var dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-studio-tests", Guid.NewGuid().ToString("N"));
        await using var host = new StudioManagedServerHost(Path.Combine(dataRoot, "missing-server.exe"), keepRunningOnExit: false);

        var started = await host.StartAsync(dataRoot, externalServer.Url, CancellationToken.None);

        Assert.True(started.IsRunning);
        Assert.False(started.StartedByStudio);
        Assert.True(started.Healthy);
        Assert.Null(started.Error);

        var stopped = await host.StopAsync(dataRoot, externalServer.Url, CancellationToken.None);

        Assert.True(stopped.IsRunning);
        Assert.False(stopped.StartedByStudio);
        Assert.True(stopped.Healthy);

        using var client = new HttpClient();
        using var response = await client.GetAsync(externalServer.Url + "/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StartAsync_WhenSwitchingToHealthyExternalServer_DoesNotClaimExistingManagedProcess()
    {
        var dataRoot = CreateDataRoot();
        var managedUrl = CreateLoopbackUrl();
        await using var host = new StudioManagedServerHost(GetServerExecutable(), keepRunningOnExit: false);

        var managed = await host.StartAsync(dataRoot, managedUrl, CancellationToken.None);
        Assert.True(managed.StartedByStudio);
        Assert.True(managed.Healthy);

        await using var externalServer = new ExternalHealthServer();
        var external = await host.StartAsync(CreateDataRoot(), externalServer.Url, CancellationToken.None);

        Assert.True(external.IsRunning);
        Assert.False(external.StartedByStudio);
        Assert.True(external.Healthy);

        var original = await host.GetStatusAsync(dataRoot, managedUrl, CancellationToken.None);
        Assert.True(original.StartedByStudio);
        Assert.True(original.Healthy);
    }

    [Fact]
    public async Task StartAsync_WhenSwitchingToNewUnhealthyTarget_ReplacesExistingManagedProcess()
    {
        var firstDataRoot = CreateDataRoot();
        var firstUrl = CreateLoopbackUrl();
        var secondDataRoot = CreateDataRoot();
        var secondUrl = CreateLoopbackUrl();
        await using var host = new StudioManagedServerHost(GetServerExecutable(), keepRunningOnExit: false);

        var first = await host.StartAsync(firstDataRoot, firstUrl, CancellationToken.None);
        Assert.True(first.StartedByStudio);
        Assert.True(first.Healthy);

        var second = await host.StartAsync(secondDataRoot, secondUrl, CancellationToken.None);
        Assert.True(second.StartedByStudio);
        Assert.True(second.Healthy);
        Assert.NotEqual(first.ProcessId, second.ProcessId);

        var firstStatus = await host.GetStatusAsync(firstDataRoot, firstUrl, CancellationToken.None);
        Assert.False(firstStatus.IsRunning);
        Assert.False(firstStatus.Healthy);
    }

    [Fact]
    public async Task StopAsync_WhenTargetingHealthyExternalServer_DoesNotStopExistingManagedProcess()
    {
        var dataRoot = CreateDataRoot();
        var managedUrl = CreateLoopbackUrl();
        await using var host = new StudioManagedServerHost(GetServerExecutable(), keepRunningOnExit: false);

        var managed = await host.StartAsync(dataRoot, managedUrl, CancellationToken.None);
        Assert.True(managed.StartedByStudio);
        Assert.True(managed.Healthy);

        await using var externalServer = new ExternalHealthServer();
        var external = await host.StopAsync(CreateDataRoot(), externalServer.Url, CancellationToken.None);

        Assert.True(external.IsRunning);
        Assert.False(external.StartedByStudio);
        Assert.True(external.Healthy);

        var original = await host.GetStatusAsync(dataRoot, managedUrl, CancellationToken.None);
        Assert.True(original.StartedByStudio);
        Assert.True(original.Healthy);
    }

    private static string CreateDataRoot()
        => Path.Combine(Path.GetTempPath(), "sonnetdb-studio-tests", Guid.NewGuid().ToString("N"));

    private static string CreateLoopbackUrl()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
    }

    private static string GetServerExecutable()
    {
        var serverAssembly = typeof(StudioManagedServerHostTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "SonnetDB.ServerAssemblyPath").Value;
        Assert.False(string.IsNullOrWhiteSpace(serverAssembly));
        var path = Path.ChangeExtension(serverAssembly, ".exe");
        Assert.True(File.Exists(path), $"Server executable was not found at {path}.");
        return path;
    }

    private sealed class ExternalHealthServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _accepting;

        public ExternalHealthServer()
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _accepting = AcceptConnectionsAsync();
        }

        public string Url { get; }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            await _accepting.ConfigureAwait(false);
            _stopping.Dispose();
        }

        private async Task AcceptConnectionsAsync()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
                    _ = WriteHealthyResponseAsync(client);
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
            catch (SocketException) when (_stopping.IsCancellationRequested)
            {
            }
        }

        private static async Task WriteHealthyResponseAsync(TcpClient client)
        {
            using (client)
            {
                await using var stream = client.GetStream();
                var requestBuffer = new byte[1024];
                _ = await stream.ReadAsync(requestBuffer).ConfigureAwait(false);
                byte[] response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
                await stream.WriteAsync(response).ConfigureAwait(false);
            }
        }
    }
}
