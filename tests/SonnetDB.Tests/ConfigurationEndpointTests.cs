using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Hosting;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>真实 HTTP 配置读取必须显示生效值、隐藏秘密并保持数据库授权边界。</summary>
public sealed class ConfigurationEndpointTests : IAsyncLifetime
{
    private const string AdminToken = "configuration-test-admin-secret";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-configuration-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _address = null!;
    private string _demoToken = null!;

    public async Task InitializeAsync()
    {
        var options = new ServerOptions
        {
            DataRoot = _root,
            Tokens = new() { [AdminToken] = ServerRoles.Admin },
            Copilot = new() { Enabled = false, Chat = new() { ApiKey = "provider-key-hidden", Endpoint = "https://user:uri-secret@example.com/v1?key=query-secret#fragment" } },
        };
        options.Mqtt.ExternalClient.Password = "mqtt-password-hidden";
        options.SqlExecution.MaxResultRows = 1234;
        options.Kv.MaxOverlayEntries = 2345;
        _app = TestServerHost.Build(options);
        await _app.StartAsync();
        _address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var registry = _app.Services.GetRequiredService<TsdbRegistry>();
        registry.TryCreate("demo", out _);
        registry.TryCreate("private", out _);
        var users = _app.Services.GetRequiredService<UserStore>();
        users.CreateUser("config-demo", "local-test-password", false);
        _demoToken = users.IssueToken("config-demo").Token;
        _app.Services.GetRequiredService<GrantsStore>().Grant("config-demo", "demo", DatabasePermission.Admin);
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task InstanceConfiguration_AdminReceivesEffectiveValuesWithoutSecrets()
    {
        using var client = Client(AdminToken);
        using var response = await client.GetAsync("/v1/system/configuration");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var text = await response.Content.ReadAsStringAsync();
        foreach (var secret in new[] { AdminToken, "provider-key-hidden", "mqtt-password-hidden", "uri-secret", "query-secret", "local-test-password" })
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(text);
        var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.True(entries.Length > 100);
        Assert.Contains(entries, row => row.GetProperty("path").GetString() == "SonnetDBServer:SqlExecution:MaxResultRows" && row.GetProperty("value").GetString() == "1234");
        Assert.Contains(entries, row => row.GetProperty("path").GetString() == "SonnetDBServer:Tokens" && row.GetProperty("redacted").GetBoolean());
        Assert.Contains(entries, row => row.GetProperty("value").GetString() == "https://example.com/v1");
    }

    [Fact]
    public async Task DatabaseConfiguration_UsesActualEngineOptionsAndHidesHostPath()
    {
        using var client = Client(_demoToken);
        using var response = await client.GetAsync("/v1/db/demo/configuration");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(_root, text, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(text);
        var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Contains(entries, row => row.GetProperty("path").GetString() == "Engine:Kv:MaxOverlayEntries" && row.GetProperty("value").GetString() == "2345");
        Assert.Contains(entries, row => row.GetProperty("path").GetString() == "Engine:SyncWalOnEveryWrite");
        Assert.Contains(entries, row => row.GetProperty("path").GetString() == "Engine:RootDirectory" && row.GetProperty("redacted").GetBoolean());
    }

    [Theory]
    [InlineData("/v1/system/configuration")]
    [InlineData("/v1/db/private/configuration")]
    public async Task DatabaseAdministrator_CannotReadInstanceOrAnotherDatabase(string path)
    {
        using var client = Client(_demoToken);
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/v1/system/configuration")]
    [InlineData("/v1/db/demo/configuration")]
    public async Task Configuration_AnonymousIsRejected(string path)
    {
        using var client = Client(null);
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private HttpClient Client(string? token)
    {
        var client = new HttpClient { BaseAddress = new Uri(_address), Timeout = TimeSpan.FromSeconds(15) };
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
