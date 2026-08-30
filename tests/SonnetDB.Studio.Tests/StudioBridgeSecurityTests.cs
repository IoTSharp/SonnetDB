using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NativeWebHost;
using Xunit;

namespace SonnetDB.Studio.Tests;

public sealed class StudioBridgeSecurityTests
{
    private const string TokenHeader = "X-SonnetDB-Studio-Bridge-Token";
    private const string TrustedOrigin = "https://studio.example.test:7443";

    [Fact]
    public async Task DesktopApp_OnStart_RegistersMemoryOnlyBootstrapHandler()
    {
        var bootstrap = new StudioBridgeBootstrap(
            "http://127.0.0.1:54980/studio-bridge",
            "process-token");
        var bridge = new RecordingJsBridge();
        var adapter = new RecordingWebViewAdapter(bridge);
        var app = new StudioDesktopApp("SonnetDB Studio", bootstrap);

        await app.OnStartAsync(adapter, CancellationToken.None);

        var handler = Assert.Single(bridge.Handlers);
        Assert.Equal(StudioDesktopApp.BridgeBootstrapRequestHandler, handler.Key);
        Assert.Equal("null", await handler.Value("null"));
        var message = Assert.Single(bridge.PostedMessages);
        Assert.Equal(StudioDesktopApp.BridgeBootstrapEvent, message.EventName);
        using var document = JsonDocument.Parse(message.Payload);
        Assert.Equal(bootstrap.EndpointUrl, document.RootElement.GetProperty("endpointUrl").GetString());
        Assert.Equal(bootstrap.Token, document.RootElement.GetProperty("token").GetString());
    }

    [Fact]
    public void BuildStudioUrl_WithBridgeEnabled_KeepsCredentialsOutOfUrl()
    {
        var result = Program.BuildStudioUrl(
            "https://studio.example.test:7443/",
            "/admin/app/studio?tool=sql");

        Assert.Equal("https://studio.example.test:7443/admin/app/studio?tool=sql", result);
        Assert.DoesNotContain("studioBridge", result, StringComparison.Ordinal);
        Assert.DoesNotContain("token", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BridgeRequest_WithTrustedOriginAndHeaderToken_Succeeds()
    {
        await using var fixture = await BridgeFixture.StartAsync();
        using var request = fixture.CreateRequest(HttpMethod.Get, "/connections", TrustedOrigin, includeTokenHeader: true);
        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TrustedOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task BridgeRequest_WithQueryToken_IsRejectedEvenWithValidHeader()
    {
        await using var fixture = await BridgeFixture.StartAsync();
        using var request = fixture.CreateRequest(
            HttpMethod.Get,
            $"/connections?token={fixture.Host.Token}",
            TrustedOrigin,
            includeTokenHeader: true);
        using var response = await fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(fixture.Host.Token, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://attacker.example.test")]
    public async Task BridgeRequest_WithoutTrustedOrigin_IsForbidden(string? origin)
    {
        await using var fixture = await BridgeFixture.StartAsync();
        using var request = fixture.CreateRequest(HttpMethod.Get, "/connections", origin, includeTokenHeader: true);
        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task BridgePreflight_WithTrustedOrigin_SucceedsAndUntrustedOriginIsForbidden()
    {
        await using var fixture = await BridgeFixture.StartAsync();
        using var trustedRequest = fixture.CreatePreflightRequest(TrustedOrigin);
        using var trustedResponse = await fixture.Client.SendAsync(trustedRequest);
        using var untrustedRequest = fixture.CreatePreflightRequest("https://attacker.example.test");
        using var untrustedResponse = await fixture.Client.SendAsync(untrustedRequest);

        Assert.Equal(HttpStatusCode.NoContent, trustedResponse.StatusCode);
        Assert.Equal(TrustedOrigin, Assert.Single(trustedResponse.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains(
            TokenHeader,
            Assert.Single(trustedResponse.Headers.GetValues("Access-Control-Allow-Headers")),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Forbidden, untrustedResponse.StatusCode);
        Assert.False(untrustedResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private sealed class BridgeFixture : IAsyncDisposable
    {
        private readonly string _directory;

        private BridgeFixture(string directory, StudioBridgeHost host, HttpClient client)
        {
            _directory = directory;
            Host = host;
            Client = client;
        }

        public StudioBridgeHost Host { get; }

        public HttpClient Client { get; }

        public static async Task<BridgeFixture> StartAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"sonnetdb-studio-bridge-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var port = ReserveLoopbackPort();
            var options = new StudioHostOptions(
                TrustedOrigin + "/admin",
                "/admin/app/studio",
                1280,
                800,
                true,
                port,
                Path.Combine(directory, "data"),
                "http://127.0.0.1:5080",
                Path.Combine(directory, "connections.json"),
                null,
                false,
                false);
            var host = new StudioBridgeHost(options);
            await host.StartAsync(CancellationToken.None);
            return new BridgeFixture(directory, host, new HttpClient { BaseAddress = new Uri(host.EndpointUrl + "/") });
        }

        public HttpRequestMessage CreateRequest(
            HttpMethod method,
            string relativeUrl,
            string? origin,
            bool includeTokenHeader)
        {
            var request = new HttpRequestMessage(method, relativeUrl.TrimStart('/'));
            if (origin is not null)
                request.Headers.Add("Origin", origin);
            if (includeTokenHeader)
                request.Headers.Add(TokenHeader, Host.Token);
            return request;
        }

        public HttpRequestMessage CreatePreflightRequest(string origin)
        {
            var request = CreateRequest(HttpMethod.Options, "/connections", origin, includeTokenHeader: false);
            request.Headers.Add("Access-Control-Request-Method", "GET");
            request.Headers.Add("Access-Control-Request-Headers", TokenHeader);
            return request;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }

        private static int ReserveLoopbackPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed class RecordingWebViewAdapter(IJsBridge jsBridge) : IWebViewAdapter
    {
        public string AdapterId => "test";

        public BrowserCapabilities Capabilities => null!;

        public IJsBridge JsBridge { get; } = jsBridge;

        public Task InitializeAsync(
            HostSurfaceDescriptor surface,
            NativeWebHostOptions options,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NavigateAsync(string url, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Resize(int width, int height)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingJsBridge : IJsBridge
    {
        public Dictionary<string, Func<string, Task<string>>> Handlers { get; } = [];

        public List<(string EventName, string Payload)> PostedMessages { get; } = [];

        public Task<string?> ExecuteScriptAsync(
            string script,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public void RegisterHandler(string name, Func<string, Task<string>> handler)
            => Handlers.Add(name, handler);

        public Task PostMessageAsync(string eventName, string jsonPayload)
        {
            PostedMessages.Add((eventName, jsonPayload));
            return Task.CompletedTask;
        }
    }
}
