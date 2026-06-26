using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// 首次安装状态与初始化流程端到端测试。
/// </summary>
public sealed class SetupEndToEndTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-setup-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
        };
        options.Copilot.Docs.AutoIngestOnStartup = false;
        options.Copilot.Skills.AutoIngestOnStartup = false;

        _app = TestServerHost.Build(options);
        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_dataRoot is not null && Directory.Exists(_dataRoot))
        {
            try
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private HttpClient CreateClient(string? token = null)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }

    [Fact]
    public async Task GetSetupStatus_WithEmptySystemDirectory_ReturnsNeedsSetup()
    {
        using var client = CreateClient();
        var resp = await client.GetFromJsonAsync<SetupStatusResponse>("/v1/setup/status", ServerJsonContext.Default.SetupStatusResponse);

        Assert.NotNull(resp);
        Assert.True(resp!.NeedsSetup);
        Assert.StartsWith("sndb-", resp.SuggestedServerId);
        Assert.Null(resp.ServerId);
        Assert.Null(resp.Organization);
        Assert.Equal(0, resp.UserCount);
        Assert.Equal(0, resp.DatabaseCount);
    }

    [Fact]
    public async Task InitializeSetup_CreatesAdminUserAndToken()
    {
        const string bearerToken = "sonnetdb-admin-bootstrap-token";

        using var client = CreateClient();
        var initializeResp = await client.PostAsJsonAsync(
            "/v1/setup/initialize",
            new SetupInitializeRequest(
                "sonnetdb-dev-01",
                "Acme Observability",
                "admin",
                "Admin123!",
                bearerToken),
            ServerJsonContext.Default.SetupInitializeRequest);

        Assert.Equal(HttpStatusCode.Created, initializeResp.StatusCode);

        var payload = await initializeResp.Content.ReadFromJsonAsync<SetupInitializeResponse>(ServerJsonContext.Default.SetupInitializeResponse);
        Assert.NotNull(payload);
        Assert.Equal("sonnetdb-dev-01", payload!.ServerId);
        Assert.Equal("Acme Observability", payload.Organization);
        Assert.Equal("admin", payload.Username);
        Assert.Equal(bearerToken, payload.Token);
        Assert.StartsWith("tok_", payload.TokenId);
        Assert.True(payload.IsSuperuser);

        var status = await client.GetFromJsonAsync<SetupStatusResponse>("/v1/setup/status", ServerJsonContext.Default.SetupStatusResponse);
        Assert.NotNull(status);
        Assert.False(status!.NeedsSetup);
        Assert.Equal("sonnetdb-dev-01", status.ServerId);
        Assert.Equal("Acme Observability", status.Organization);
        Assert.Equal(1, status.UserCount);

        using var tokenClient = CreateClient(bearerToken);
        var databasesResp = await tokenClient.GetAsync("/v1/db");
        Assert.Equal(HttpStatusCode.OK, databasesResp.StatusCode);

        var loginResp = await client.PostAsJsonAsync(
            "/v1/auth/login",
            new LoginRequest("admin", "Admin123!"),
            ServerJsonContext.Default.LoginRequest);
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);

        var systemDirectory = Path.Combine(_dataRoot!, ".system");
        Assert.True(File.Exists(Path.Combine(systemDirectory, "users.json")));
        Assert.True(File.Exists(Path.Combine(systemDirectory, "installation.json")));
    }

    [Fact]
    public async Task InitializeSetup_AfterInstalled_ReturnsConflict()
    {
        using var client = CreateClient();
        var request = new SetupInitializeRequest(
            "sonnetdb-dev-01",
            "Acme Observability",
            "admin",
            "Admin123!",
            "sonnetdb-admin-bootstrap-token");

        var firstResp = await client.PostAsJsonAsync("/v1/setup/initialize", request, ServerJsonContext.Default.SetupInitializeRequest);
        Assert.Equal(HttpStatusCode.Created, firstResp.StatusCode);

        var secondResp = await client.PostAsJsonAsync("/v1/setup/initialize", request, ServerJsonContext.Default.SetupInitializeRequest);
        Assert.Equal(HttpStatusCode.Conflict, secondResp.StatusCode);
    }
}
