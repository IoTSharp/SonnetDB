using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// PR #34b-2：嵌入式 Admin SPA 静态资源端到端测试。
/// </summary>
public sealed class AdminUiEndToEndTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;
    private bool _hasPublishedAdminUi;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-admin-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AllowAnonymousProbes = true,
        };

        _app = TestServerHost.Build(options);
        await _app.StartAsync();
        _baseUrl = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _hasPublishedAdminUi = _app.Environment.WebRootFileProvider.GetFileInfo("index.html").Exists;
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
            try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    private HttpClient CreateClient() => new() { BaseAddress = new Uri(_baseUrl!) };

    [Fact]
    public async Task GetAdminRoot_AnonymouslyReturnsHtml()
    {
        using var client = CreateClient();
        var resp = await client.GetAsync("/admin");
        if (resp.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            return;
        }
        // /admin 作为 SPA 路由入口，应被 fallback 到 index.html。
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/html", resp.Content.Headers.ContentType?.MediaType ?? string.Empty);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<div id=\"app\">", body);
    }

    [Fact]
    public async Task GetRoot_AnonymouslyReturnsSpaHtml()
    {
        using var client = CreateClient();
        var resp = await client.GetAsync("/");
        if (resp.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/html", resp.Content.Headers.ContentType?.MediaType ?? string.Empty);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<div id=\"app\">", body);
    }

    [Fact]
    public async Task GetAdminSubpath_FallsBackToIndexHtml()
    {
        using var client = CreateClient();
        var resp = await client.GetAsync("/admin/login");
        if (resp.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            return;
        }
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/html", resp.Content.Headers.ContentType?.MediaType ?? string.Empty);
    }

    [Fact]
    public async Task GetAdminAssetWithExtension_NotFound_Returns404()
    {
        using var client = CreateClient();
        var resp = await client.GetAsync("/admin/this-does-not-exist.js");
        if (!_hasPublishedAdminUi)
        {
            // 未发布时所有 GET 都被占位 503 / 未注册路由返回 404。
            Assert.True(resp.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.NotFound);
            return;
        }
        // 发布后：带扩展名的不存在资源不会被 SPA fallback接管，返回 404。
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetAdmin_DoesNotRequireBearerToken()
    {
        using var client = CreateClient();
        // 不带 Authorization；SPA 入口路由应返回 index.html
        var resp = await client.GetAsync("/admin/login");
        if (resp.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/html", resp.Content.Headers.ContentType?.MediaType ?? string.Empty);
    }

    [Fact]
    public async Task GetAdminFavicon_ReturnsSvg()
    {
        using var client = CreateClient();
        var resp = await client.GetAsync("/favicon.svg");
        if (resp.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        if (resp.StatusCode == HttpStatusCode.NotFound) return; // favicon 可选
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("image/svg+xml", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetOtherEndpoints_StillRequireAuth()
    {
        // 验证 admin 路径豁免不会误伤其他端点：/v1/db 仍需 Bearer。
        using var client = CreateClient();
        var resp = await client.GetAsync("/v1/db");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
