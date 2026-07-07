using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// PostgreSQL 风格接入端到端测试：
/// (1) 容器环境变量引导（SONNETDB_USER/PASSWORD/DB，经 SONNETDB_ 前缀剥离为 USER/PASSWORD/DB 配置键）；
/// (2) 客户端 HTTP Basic 认证（用户名/密码）访问受保护端点。
/// </summary>
public sealed class PostgresStyleBootstrapEndToEndTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;
    private const string _bootstrapUser = "tolnsd";
    private const string _bootstrapPassword = "s3cret-pass";
    private const string _bootstrapDb = "TOLNSD";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-pg-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
        };

        // 通过命令行参数注入引导配置键（等价于 SONNETDB_USER/PASSWORD/DB 环境变量剥离前缀后的效果）。
        string[] extraArgs =
        [
            $"--USER={_bootstrapUser}",
            $"--PASSWORD={_bootstrapPassword}",
            $"--DB={_bootstrapDb}",
        ];

        _app = TestServerHost.Build(options, extraArgs: extraArgs);
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
            try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    private HttpClient CreateBasicClient(string user, string password)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        return client;
    }

    [Fact]
    public void Bootstrap_CreatesUserAndDatabase_AndMarksInitialized()
    {
        // 引导应已在 BuildApp 期间执行：用户存在、库存在、NeedsSetup 变 false。
        var users = _app!.Services.GetRequiredService<SonnetDB.Auth.UserStore>();
        var registry = _app.Services.GetRequiredService<SonnetDB.Hosting.TsdbRegistry>();
        var installation = _app.Services.GetRequiredService<SonnetDB.Auth.InstallationStore>();

        Assert.True(users.Exists(_bootstrapUser));
        Assert.True(users.IsSuperuser(_bootstrapUser));
        Assert.Contains(_bootstrapDb, registry.ListDatabases());
        Assert.False(installation.GetStatus(users.Count, registry.Count).NeedsSetup);
    }

    [Fact]
    public async Task BasicAuth_WithBootstrapCredentials_AccessesProtectedEndpoint()
    {
        using var client = CreateBasicClient(_bootstrapUser, _bootstrapPassword);
        var resp = await client.GetAsync("/v1/db");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task BasicAuth_WithWrongPassword_Returns401()
    {
        using var client = CreateBasicClient(_bootstrapUser, "wrong-password");
        var resp = await client.GetAsync("/v1/db");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task BasicAuth_SuperuserBootstrap_CanRunControlPlaneDdl()
    {
        using var client = CreateBasicClient(_bootstrapUser, _bootstrapPassword);
        var resp = await client.PostAsync("/v1/sql",
            new StringContent("{\"sql\":\"CREATE USER extra WITH PASSWORD 'p'\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
