using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MQTTnet.Server;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// M28 P5b #243 外部 MQTT broker 订阅端到端测试。
/// </summary>
public sealed class MqttExternalClientEndpointTests : IAsyncLifetime
{
    private const string AdminToken = "admin-external-mqtt-token";
    private WebApplication? _app;
    private MqttServer? _externalBroker;
    private string? _baseUrl;
    private string? _dataRoot;
    private int _externalBrokerPort;

    public async Task InitializeAsync()
    {
        _externalBrokerPort = GetFreeTcpPort();
        var mqttServerOptions = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(_externalBrokerPort)
            .Build();
        _externalBroker = new MqttServerFactory().CreateMqttServer(mqttServerOptions);
        await _externalBroker.StartAsync();

        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-mqtt-external-tests-" + Guid.NewGuid().ToString("N"));
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
            Mqtt = new MqttBrokerOptions
            {
                Enabled = false,
                ExternalClient = new MqttExternalClientOptions
                {
                    Enabled = true,
                    Host = "127.0.0.1",
                    Port = _externalBrokerPort,
                    ClientId = "sonnetdb-external-test",
                    ReconnectDelaySeconds = 1,
                    MaxReconnectDelaySeconds = 1,
                    Subscriptions =
                    [
                        new MqttExternalSubscriptionOptions
                        {
                            TopicFilter = "db/+/m/+",
                            Qos = 1,
                        },
                    ],
                },
            },
        };

        _app = TestServerHost.Build(options);
        await _app.StartAsync();
        _baseUrl = await ResolveHttpAddressAsync(_app);
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_externalBroker is not null)
        {
            await _externalBroker.StopAsync();
            _externalBroker.Dispose();
        }

        if (_dataRoot is not null && Directory.Exists(_dataRoot))
        {
            try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ExternalMqttClient_WithMeasurementTopic_WritesRows()
    {
        const string db = "mqttsource";
        await CreateDatabaseAsync(db);
        await ExecuteSqlAsync(db, "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)");

        for (int attempt = 0; attempt < 30; attempt++)
        {
            await PublishExternalAsync($"db/{db}/m/cpu", "cpu,host=external value=24 1000");
            int rows = await CountSelectRowsAsync(db, "SELECT value FROM cpu WHERE host='external' AND time >= 1000 AND time <= 1000");
            if (rows == 1)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        int finalRows = await CountSelectRowsAsync(db, "SELECT value FROM cpu WHERE host='external' AND time >= 1000 AND time <= 1000");
        Assert.Equal(1, finalRows);
    }

    private async Task PublishExternalAsync(string topic, string payload)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        try
        {
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", _externalBrokerPort)
                .WithClientId("external-publisher-" + Guid.NewGuid().ToString("N"))
                .WithProtocolVersion(MqttProtocolVersion.V500)
                .WithCleanStart(true)
                .Build();
            var connectResult = await client.ConnectAsync(options, CancellationToken.None);
            Assert.Equal(MqttClientConnectResultCode.Success, connectResult.ResultCode);

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithContentType("text/plain")
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            var publishResult = await client.PublishAsync(message, CancellationToken.None);
            Assert.True(publishResult.IsSuccess, publishResult.ReasonString);
        }
        finally
        {
            try
            {
                if (client.IsConnected)
                    await client.DisconnectAsync(new MqttClientDisconnectOptions(), CancellationToken.None);
            }
            finally
            {
                (client as IDisposable)?.Dispose();
            }
        }
    }

    private HttpClient CreateHttpClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        return client;
    }

    private async Task CreateDatabaseAsync(string db)
    {
        using var client = CreateHttpClient();
        var resp = await client.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(db), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    private async Task ExecuteSqlAsync(string db, string sql)
    {
        using var client = CreateHttpClient();
        var resp = await client.PostAsync($"/v1/db/{db}/sql",
            JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));
        string text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"SQL 失败：{(int)resp.StatusCode} {text}");
    }

    private async Task<int> CountSelectRowsAsync(string db, string sql)
    {
        using var client = CreateHttpClient();
        var resp = await client.PostAsync($"/v1/db/{db}/sql",
            JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));
        string text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"SELECT 失败：{(int)resp.StatusCode} {text}");
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2, $"ndjson 至少含 meta + end，实际 {lines.Length} 行：{text}");
        return lines.Length - 2;
    }

    private static async Task<string> ResolveHttpAddressAsync(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");

        foreach (string address in addresses.Addresses)
        {
            if (await ProbeIsHttp11Async(address))
                return address;
        }

        throw new InvalidOperationException("未找到可用的 HTTP/1.1 测试端口。");
    }

    private static async Task<bool> ProbeIsHttp11Async(string address)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
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
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
