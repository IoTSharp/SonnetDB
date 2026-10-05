using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SonnetDB.Studio.Tests;

public sealed class StudioHostContractTests
{
    [Fact]
    public async Task ConnectionsBridge_SaveAndLoad_ReturnsCanonicalHostAndDatabaseIdentity()
    {
        await using var fixture = await BridgeFixture.StartAsync();
        const string payload = """
            {"profiles":[{"id":"Remote-Prod","name":"Production","kind":"remote","baseUrl":"HTTPS://SERVER.EXAMPLE/API/Admin/","defaultDatabase":"DefaultCase","tokenMode":"current-session","createdAt":1,"updatedAt":2}],"activeProfileId":"remote-prod","activeDatabase":"Telemetry:Pump"}
            """;

        using var saveResponse = await fixture.SendAsync(HttpMethod.Put, "/connections", payload);
        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
        using var saveDocument = JsonDocument.Parse(await saveResponse.Content.ReadAsStringAsync());
        AssertIdentity(saveDocument.RootElement);

        using var loadResponse = await fixture.SendAsync(HttpMethod.Get, "/connections");
        Assert.Equal(HttpStatusCode.OK, loadResponse.StatusCode);
        using var loadDocument = JsonDocument.Parse(await loadResponse.Content.ReadAsStringAsync());
        AssertIdentity(loadDocument.RootElement);
    }

    [Theory]
    [InlineData("https://user:secret@server.example")]
    [InlineData("https://server.example/admin?token=secret")]
    [InlineData("file:///C:/private")]
    public async Task ConnectionsBridge_WithInvalidUrl_ReturnsBadRequestWithoutCredentialOrDiskWrite(string url)
    {
        await using var fixture = await BridgeFixture.StartAsync();
        var snapshot = new StudioConnectionLibrarySnapshot(
            [new StudioConnectionProfile("remote", "Remote", "remote", url, "PrivateDb", "disk", 1, 1)],
            "remote", "PrivateDb");
        var payload = JsonSerializer.Serialize(snapshot, StudioBridgeJsonContext.Default.StudioConnectionLibrarySnapshot);

        using var response = await fixture.SendAsync(HttpMethod.Put, "/connections", payload);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain(url, body, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.ConnectionLibraryPath));
    }

    [Fact]
    public async Task ConnectionsBridge_WithNullProfileArray_ReturnsBadRequest()
    {
        await using var fixture = await BridgeFixture.StartAsync();

        using var response = await fixture.SendAsync(HttpMethod.Put, "/connections",
            """{"profiles":null,"activeProfileId":"remote","activeDatabase":"PrivateDb"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(fixture.ConnectionLibraryPath));
    }

    [Fact]
    public async Task ManagedServerBridge_WithMissingExecutable_ReturnsFailedUnownedLifecycle()
    {
        await using var fixture = await BridgeFixture.StartAsync();
        using var initialResponse = await fixture.SendAsync(HttpMethod.Get, "/server/status");
        using var initial = JsonDocument.Parse(await initialResponse.Content.ReadAsStringAsync());
        Assert.Equal("stopped", initial.RootElement.GetProperty("lifecycleState").GetString());
        Assert.Equal("none", initial.RootElement.GetProperty("processOwner").GetString());
        Assert.False(initial.RootElement.GetProperty("canStop").GetBoolean());

        using var startResponse = await fixture.SendAsync(HttpMethod.Post, "/server/start", "{}");
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);
        using var started = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        Assert.Equal("failed", started.RootElement.GetProperty("lifecycleState").GetString());
        Assert.Equal("none", started.RootElement.GetProperty("processOwner").GetString());
        Assert.False(started.RootElement.GetProperty("canStop").GetBoolean());
        Assert.Equal(JsonValueKind.Null, started.RootElement.GetProperty("processId").ValueKind);
        Assert.Contains("executable was not found", started.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, true, true, 123, null, "studio", "running", true)]
    [InlineData(true, true, false, 123, null, "studio", "unhealthy", true)]
    [InlineData(true, false, true, null, null, "external", "external-running", false)]
    [InlineData(false, false, false, null, null, "none", "stopped", false)]
    [InlineData(false, false, false, null, "launch failed", "none", "failed", false)]
    [InlineData(false, true, false, 123, null, "none", "stopped", false)]
    [InlineData(true, true, false, null, null, "studio", "unhealthy", false)]
    public void ManagedServerStatus_WithObservedLifecycle_SerializesStableOwnership(
        bool isRunning, bool startedByStudio, bool healthy, int? processId, string? error,
        string processOwner, string lifecycleState, bool canStop)
    {
        var status = new StudioManagedServerStatus(isRunning, startedByStudio, healthy, processId,
            "http://127.0.0.1:5080", "data", error);

        var payload = JsonSerializer.Serialize(status, StudioBridgeJsonContext.Default.StudioManagedServerStatus);
        using var document = JsonDocument.Parse(payload);

        Assert.Equal(processOwner, document.RootElement.GetProperty("processOwner").GetString());
        Assert.Equal(lifecycleState, document.RootElement.GetProperty("lifecycleState").GetString());
        Assert.Equal(canStop, document.RootElement.GetProperty("canStop").GetBoolean());
    }

    private static void AssertIdentity(JsonElement snapshot)
    {
        Assert.Equal("Remote-Prod", snapshot.GetProperty("activeProfileId").GetString());
        var identity = snapshot.GetProperty("activeIdentity");
        Assert.Equal("studio-desktop", identity.GetProperty("host").GetString());
        Assert.Equal("Remote-Prod", identity.GetProperty("profileId").GetString());
        Assert.Equal("https://server.example/API/Admin", identity.GetProperty("baseUrl").GetString());
        Assert.Equal("Telemetry:Pump", identity.GetProperty("database").GetString());
        var remote = Assert.Single(snapshot.GetProperty("profiles").EnumerateArray(), profile => profile.GetProperty("id").GetString() == "Remote-Prod");
        Assert.Equal("DefaultCase", remote.GetProperty("identity").GetProperty("database").GetString());
    }

    private sealed class BridgeFixture : IAsyncDisposable
    {
        private const string TrustedOrigin = "https://studio.example.test:7443";
        private readonly string _directory;
        private readonly StudioBridgeHost _host;
        private readonly HttpClient _client;

        private BridgeFixture(string directory, StudioBridgeHost host)
        {
            _directory = directory;
            _host = host;
            _client = new HttpClient { BaseAddress = new Uri(host.EndpointUrl + "/"), Timeout = TimeSpan.FromSeconds(5) };
            ConnectionLibraryPath = Path.Combine(directory, "connections.json");
        }

        public string ConnectionLibraryPath { get; }

        public static async Task<BridgeFixture> StartAsync()
        {
            var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sonnetdb-studio-host-contract-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(directory);
            var options = new StudioHostOptions(TrustedOrigin + "/admin", "/admin/app/studio", 1280, 800, true,
                ReserveLoopbackPort(), Path.Combine(directory, "data"), $"http://127.0.0.1:{ReserveLoopbackPort()}",
                Path.Combine(directory, "connections.json"), Path.Combine(directory, "missing-server.exe"), false, false);
            var fixture = new BridgeFixture(directory, new StudioBridgeHost(options));
            try
            {
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await fixture._host.StartAsync(startup.Token);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? payload = null)
        {
            using var request = new HttpRequestMessage(method, path.TrimStart('/'));
            request.Headers.Add("Origin", TrustedOrigin);
            request.Headers.Add("X-SonnetDB-Studio-Bridge-Token", _host.Token);
            if (payload is not null)
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            return await _client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            try
            {
                await _host.DisposeAsync();
            }
            finally
            {
                var expectedParent = Path.GetFullPath(Path.GetTempPath());
                Assert.Equal(Path.TrimEndingDirectorySeparator(expectedParent), Path.GetDirectoryName(_directory), StringComparer.OrdinalIgnoreCase);
                Assert.StartsWith("sonnetdb-studio-host-contract-", Path.GetFileName(_directory), StringComparison.Ordinal);
                Directory.Delete(_directory, recursive: true);
            }
        }

        private static int ReserveLoopbackPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
