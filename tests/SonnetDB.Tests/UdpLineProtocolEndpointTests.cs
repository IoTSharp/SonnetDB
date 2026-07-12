using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
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
/// M30 #267：Line Protocol UDP 监听端点端到端测试。
/// </summary>
[Collection(UdpLineProtocolEndpointTestCollection.Name)]
public sealed class UdpLineProtocolEndpointTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;
    private int _udpPort;
    private const string AdminToken = "admin-udp-token";
    private const string DbName = "udpdb";

    public async Task InitializeAsync()
    {
        _udpPort = GetFreeUdpPort();
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-udp-lp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [AdminToken] = ServerRoles.Admin,
            },
            LineProtocolUdp = new LineProtocolUdpOptions
            {
                Enabled = true,
                Port = _udpPort,
                Database = DbName,
                MaxDatagramBytes = 4096,
                Precision = "ms",
            },
        };

        _app = TestServerHost.Build(options);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        using var admin = CreateClient();
        var create = await admin.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(DbName), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var ddl = await admin.PostAsync($"/v1/db/{DbName}/sql",
            JsonContent.Create(
                new SqlRequest("CREATE MEASUREMENT udp_cpu (host TAG, value FIELD FLOAT)"),
                ServerJsonContext.Default.SqlRequest));
        Assert.True(ddl.IsSuccessStatusCode, await ddl.Content.ReadAsStringAsync());
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
            try { Directory.Delete(_dataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task UdpLineProtocol_DatagramWritesRows()
    {
        var payload = "udp_cpu,host=udp value=11 1000\nudp_cpu,host=udp value=12 2000";
        using var udp = new UdpClient();
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        await udp.SendAsync(bytes, "127.0.0.1", _udpPort);

        await WaitForRowsAsync("udp", expectedRows: 2);
    }

    [Fact]
    public async Task UdpLineProtocol_InvalidDatagrams_DoNotStopListener()
    {
        using var udp = new UdpClient();
        await udp.SendAsync(new byte[4097], "127.0.0.1", _udpPort);
        byte[] invalidUtf8 = [0xC3, 0x28];
        await udp.SendAsync(invalidUtf8, "127.0.0.1", _udpPort);
        await udp.SendAsync(Encoding.UTF8.GetBytes("invalid-line-protocol"), "127.0.0.1", _udpPort);
        await udp.SendAsync(
            Encoding.UTF8.GetBytes("udp_cpu,host=recovered value=13 3000"),
            "127.0.0.1",
            _udpPort);

        await WaitForRowsAsync("recovered", expectedRows: 1);
    }

    private async Task WaitForRowsAsync(string host, int expectedRows)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        int lastRows = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            lastRows = await CountRowsAsync(host);
            if (lastRows == expectedRows)
                return;

            await Task.Delay(100);
        }

        Assert.Equal(expectedRows, lastRows);
    }

    private async Task<int> CountRowsAsync(string host)
    {
        using var client = CreateClient();
        var select = await client.PostAsync($"/v1/db/{DbName}/sql",
            JsonContent.Create(
                new SqlRequest($"SELECT value FROM udp_cpu WHERE host='{host}'"),
                ServerJsonContext.Default.SqlRequest));
        Assert.True(select.IsSuccessStatusCode, await select.Content.ReadAsStringAsync());
        string text = await select.Content.ReadAsStringAsync();
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length - 2;
    }

    private HttpClient CreateClient()
    {
        var c = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        return c;
    }

    private static int GetFreeUdpPort()
    {
        using var udp = new UdpClient(0);
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }
}

/// <summary>
/// UDP 端到端测试使用临时 UDP 端口，禁用并行降低端口探测竞争。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UdpLineProtocolEndpointTestCollection
{
    public const string Name = "UDP Line Protocol endpoint tests";
}
