using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// /help 帮助文档静态站点端到端测试。
/// </summary>
public sealed class HelpDocsEndToEndTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;
    private string? _helpRoot;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-help-e2e-data-" + Guid.NewGuid().ToString("N"));
        _helpRoot = Path.Combine(Path.GetTempPath(), "sndb-help-e2e-site-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_helpRoot);
        Directory.CreateDirectory(Path.Combine(_helpRoot, "getting-started"));
        Directory.CreateDirectory(Path.Combine(_helpRoot, "assets"));

        await File.WriteAllTextAsync(Path.Combine(_helpRoot, "index.html"),
            "<!doctype html><html><body><main>SonnetDB Help Home</main></body></html>");
        await File.WriteAllTextAsync(Path.Combine(_helpRoot, "getting-started", "index.html"),
            "<!doctype html><html><body><main>Getting Started</main></body></html>");
        await File.WriteAllTextAsync(Path.Combine(_helpRoot, "assets", "docs.css"),
            "body{background:#fff;color:#000;}");

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AllowAnonymousProbes = true,
            HelpDocsRoot = _helpRoot,
        };

        _app = TestServerHost.Build(options);
        await _app.StartAsync();
        _baseUrl = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_helpRoot is not null && Directory.Exists(_helpRoot))
        {
            try { Directory.Delete(_helpRoot, recursive: true); } catch { }
        }

        if (_dataRoot is not null && Directory.Exists(_dataRoot))
        {
            try { Directory.Delete(_dataRoot, recursive: true); } catch { }
        }
    }

    private HttpClient CreateClient() => new() { BaseAddress = new Uri(_baseUrl!) };

    [Fact]
    public async Task GetHelpRoot_AnonymouslyReturnsHtml()
    {
        using var client = CreateClient();
        var resp = await client.GetAsync("/help");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/html", resp.Content.Headers.ContentType?.MediaType ?? string.Empty);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("SonnetDB Help Home", body);
    }

    [Fact]
    public async Task GetHelpDirectoryPath_ResolvesIndexHtml()
    {
        using var client = CreateClient();
        var resp = await client.GetAsync("/help/getting-started");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Getting Started", body);
    }

    [Fact]
    public async Task GetHelpAsset_ReturnsCss()
    {
        using var client = CreateClient();
        var resp = await client.GetAsync("/help/assets/docs.css");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/css", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetMissingHelpAsset_Returns404()
    {
        using var client = CreateClient();
        var resp = await client.GetAsync("/help/assets/missing.css");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetOtherEndpoints_StillRequireAuth()
    {
        using var client = CreateClient();
        var resp = await client.GetAsync("/v1/db");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
