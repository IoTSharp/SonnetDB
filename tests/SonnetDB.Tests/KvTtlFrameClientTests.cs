using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Client;
using SonnetDB.Configuration;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>独立 typed KV TTL Client → 真实 HTTP/2 Frame → Server → 持久化 keyspace。</summary>
public sealed class KvTtlFrameClientTests : IAsyncLifetime
{
    private const string Database = "typed_kv_ttl";
    private const string Token = "typed-kv-ttl-admin";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-typed-kv-ttl-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Path, string Protocol)> _requests = new();
    private WebApplication? _app;
    private Uri _address = null!;

    public async Task InitializeAsync()
    {
        await StartAsync();
        using var http = new HttpClient
        {
            BaseAddress = _address,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(10)
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var content = new StringContent(
            "{\"name\":\"" + Database + "\"}",
            System.Text.Encoding.UTF8,
            "application/json");
        using var response = await http.PostAsync("v1/db", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        _requests.Clear();
    }

    public async Task DisposeAsync()
    {
        await StopAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task GetTimeToLiveKvAsync_PreservesThreeStatesAcrossReopen()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1).AddTicks(1234);
        using (var client = CreateClient())
        {
            await client.PutKvAsync(Database, "Cache", new byte[] { 0 }, new byte[] { 1 }, expiry);
            await client.PutKvAsync(Database, "Cache", new byte[] { 1 }, new byte[] { 2 });

            KvDataFrameTtl expiring = await client.GetTimeToLiveKvAsync(Database, "Cache", new byte[] { 0 });
            Assert.True(expiring.Milliseconds > 0);
            Assert.Equal(expiry, expiring.ExpiresAtUtc);

            Assert.Equal(new KvDataFrameTtl(-1, null),
                await client.GetTimeToLiveKvAsync(Database, "Cache", new byte[] { 1 }));
            Assert.Equal(new KvDataFrameTtl(-2, null),
                await client.GetTimeToLiveKvAsync(Database, "Cache", new byte[] { 9 }));
        }

        await StopAsync();
        await StartAsync();
        using var reopened = CreateClient();
        KvDataFrameTtl afterReopen = await reopened.GetTimeToLiveKvAsync(Database, "Cache", new byte[] { 0 });
        Assert.True(afterReopen.Milliseconds > 0);
        Assert.Equal(expiry, afterReopen.ExpiresAtUtc);
        AssertFrameOnly();
    }

    private SndbFrameClient CreateClient(string? token = Token)
        => new(_address, token, timeout: TimeSpan.FromSeconds(10));

    private void AssertFrameOnly()
    {
        Assert.NotEmpty(_requests);
        Assert.All(_requests, request =>
        {
            Assert.Equal("/v1/frame", request.Path);
            Assert.Equal("HTTP/2", request.Protocol);
        });
    }

    private async Task StartAsync()
    {
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = _root,
            AutoLoadExistingDatabases = true,
            Tokens = new Dictionary<string, string> { [Token] = ServerRoles.Admin }
        }, extraArgs: ["--Kestrel:Endpoints:Http:Protocols=Http2"]);
        _app.Use(async (context, next) =>
        {
            _requests.Enqueue((context.Request.Path.Value!, context.Request.Protocol));
            await next(context);
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _app.StartAsync(deadline.Token);
        _address = new Uri(Assert.Single(
            _app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses));
    }

    private async Task StopAsync()
    {
        if (_app is null)
            return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _app.StopAsync(deadline.Token);
        }
        finally
        {
            await _app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            _app = null;
        }
    }
}
