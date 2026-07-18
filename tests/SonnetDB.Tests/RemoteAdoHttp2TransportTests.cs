using System.Collections.Concurrent;
using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// 远程 ADO 的 <c>Protocol=frame-http2</c> 必须使用真正的 h2c，而不只是把 SQL 编码成帧。
/// 写语句仍走 REST endpoint，但与只读帧查询共享同一个 HTTP/2 exact 客户端。
/// </summary>
public sealed class RemoteAdoHttp2TransportTests : IAsyncLifetime
{
    private readonly ConcurrentQueue<ObservedRequest> _requests = new();
    private WebApplication? _app;
    private string _http11Url = string.Empty;
    private string _frameH2Url = string.Empty;
    private string? _dataRoot;
    private const string AdminToken = "ado-h2-admin";
    private const string DatabaseName = "ado_h2_transport";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-ado-h2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
        };
        _app = TestServerHost.Build(options, extraArgs:
        [
            "--Kestrel:Endpoints:FrameH2:Url=http://127.0.0.1:0",
            "--Kestrel:Endpoints:FrameH2:Protocols=Http2",
        ]);
        _app.Use(async (context, next) =>
        {
            _requests.Enqueue(new ObservedRequest(
                context.Connection.LocalPort,
                context.Request.Path.Value ?? string.Empty,
                context.Request.Protocol));
            await next(context);
        });

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose listening addresses.");
        Assert.Equal(2, addresses.Addresses.Count);

        foreach (string address in addresses.Addresses)
        {
            if (await ProbeIsHttp11Async(address))
                _http11Url = address;
            else
                _frameH2Url = address;
        }

        Assert.NotEmpty(_http11Url);
        Assert.NotEmpty(_frameH2Url);

        using var http = new HttpClient { BaseAddress = new Uri(_http11Url) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        using var response = await http.PostAsync("/v1/db", new StringContent(
            $"{{\"name\":\"{DatabaseName}\"}}", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        _requests.Clear();
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
    public void FrameHttp2_RestWritesAndFrameSelect_UseExactHttp2()
    {
        using var connection = new SndbConnection(ConnectionString(_frameH2Url, "frame-http2"));
        connection.Open();
        Assert.Equal(ConnectionState.Open, connection.State);

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE h2_rows (id INT, value STRING, PRIMARY KEY (id))";
            create.ExecuteNonQuery();
        }
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO h2_rows (id, value) VALUES (1, 'ok')";
            Assert.Equal(1, insert.ExecuteNonQuery());
        }
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT id, value FROM h2_rows WHERE id = 1";
            using var reader = select.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal("ok", reader.GetString(1));
            Assert.False(reader.Read());
        }

        int h2Port = new Uri(_frameH2Url).Port;
        ObservedRequest[] h2Requests = _requests.Where(r => r.LocalPort == h2Port).ToArray();
        Assert.Contains(h2Requests, r => r.Path == $"/v1/db/{DatabaseName}/sql");
        Assert.Contains(h2Requests, r => r.Path == "/v1/frame");
        Assert.All(h2Requests, r => Assert.Equal("HTTP/2", r.Protocol));
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("auto")]
    public void RestAndAuto_KeepDefaultHttp11Behavior(string protocol)
    {
        string table = "http11_" + protocol;
        using var connection = new SndbConnection(ConnectionString(_http11Url, protocol));
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = $"CREATE TABLE {table} (id INT, PRIMARY KEY (id))";
            create.ExecuteNonQuery();
        }
        using (var select = connection.CreateCommand())
        {
            select.CommandText = $"SELECT id FROM {table}";
            using var reader = select.ExecuteReader();
            Assert.False(reader.Read());
        }

        int http11Port = new Uri(_http11Url).Port;
        ObservedRequest[] requests = _requests
            .Where(r => r.LocalPort == http11Port)
            .ToArray();
        Assert.NotEmpty(requests);
        Assert.All(requests, r => Assert.Equal("HTTP/1.1", r.Protocol));
    }

    private string ConnectionString(string baseUrl, string protocol)
        => $"Data Source=sonnetdb+http://{new Uri(baseUrl).Authority}/{DatabaseName};" +
           $"Token={AdminToken};Timeout=30;Protocol={protocol}";

    private static async Task<bool> ProbeIsHttp11Async(string address)
    {
        using var client = new HttpClient();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address + "/healthz")
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            using var response = await client.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private sealed record ObservedRequest(int LocalPort, string Path, string Protocol);
}
