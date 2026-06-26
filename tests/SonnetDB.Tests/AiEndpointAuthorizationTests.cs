using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// AI 端点鉴权测试：确保 sql_gen 不会绕过数据库级 grant 读取 schema。
/// </summary>
public sealed class AiEndpointAuthorizationTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string _baseUrl = string.Empty;
    private string? _dataRoot;
    private const string _adminToken = "ai-admin-token";
    private const string _dbName = "alpha";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-ai-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [_adminToken] = ServerRoles.Admin,
            },
        };

        _app = TestServerHost.Build(options);
        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        using var admin = CreateClient(_adminToken);
        await CreateDatabaseAsync(admin, _dbName);
        await ExecuteSqlAsync(admin, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
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
            try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task AiSqlGen_WithoutGrant_ReturnsForbidden()
    {
        SetAiConfig(new AiOptions
        {
            Enabled = true,
            CloudAccessToken = "dummy-token",
            TimeoutSeconds = 1,
        });

        using var admin = CreateClient(_adminToken);
        await ExecuteSqlAsync(admin, "CREATE USER nogrant WITH PASSWORD 'p'");
        var token = await LoginAsync("nogrant", "p");

        using var client = CreateClient(token);
        var response = await client.PostAsync(
            "/v1/ai/chat",
            JsonContent.Create(
                new AiChatRequest([new AiMessage("user", "帮我生成查询 cpu 的 SQL")], _dbName, "sql_gen"),
                ServerJsonContext.Default.AiChatRequest));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("forbidden", body);
        Assert.Contains(_dbName, body);
    }

    [Fact]
    public async Task AiSqlGen_WithReadGrant_ReachesAiConfigGate()
    {
        SetAiConfig(new AiOptions
        {
            Enabled = true,
            CloudAccessToken = "",
            TimeoutSeconds = 1,
        });

        using var admin = CreateClient(_adminToken);
        await ExecuteSqlAsync(admin, "CREATE USER reader WITH PASSWORD 'p'");
        await ExecuteSqlAsync(admin, $"GRANT READ ON DATABASE {_dbName} TO reader");
        var token = await LoginAsync("reader", "p");

        using var client = CreateClient(token);
        var response = await client.PostAsync(
            "/v1/ai/chat",
            JsonContent.Create(
                new AiChatRequest([new AiMessage("user", "帮我生成查询 cpu 的 SQL")], _dbName, "sql_gen"),
                ServerJsonContext.Default.AiChatRequest));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("cloud_not_bound", body);
    }

    private void SetAiConfig(AiOptions options)
    {
        var store = _app!.Services.GetRequiredService<AiConfigStore>();
        store.Save(options);
    }

    private HttpClient CreateClient(string? token = null)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        if (!string.IsNullOrEmpty(token))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> LoginAsync(string username, string password)
    {
        using var client = CreateClient();
        var response = await client.PostAsync(
            "/v1/auth/login",
            JsonContent.Create(new LoginRequest(username, password), ServerJsonContext.Default.LoginRequest));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"登录失败：{(int)response.StatusCode} {body}");

        var login = JsonSerializer.Deserialize(body, ServerJsonContext.Default.LoginResponse);
        Assert.NotNull(login);
        return login!.Token;
    }

    private static async Task CreateDatabaseAsync(HttpClient client, string databaseName)
    {
        var response = await client.PostAsync(
            "/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(databaseName), ServerJsonContext.Default.CreateDatabaseRequest));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"创建数据库失败：{(int)response.StatusCode} {body}");
    }

    private async Task ExecuteSqlAsync(HttpClient client, string sql)
    {
        var response = await client.PostAsync(
            $"/v1/db/{_dbName}/sql",
            JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"执行 SQL 失败：{(int)response.StatusCode} {body}");
    }
}
