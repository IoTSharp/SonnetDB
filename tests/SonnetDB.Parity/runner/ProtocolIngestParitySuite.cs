using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CoAP;
using CoAP.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using SonnetDB.Configuration;
using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Parity.Runner;

/// <summary>
/// M30 #268 多协议接入收口测试：同一 Line Protocol payload 分别经 HTTP、UDP、MQTT 与 CoAP
/// 写入独立数据库，并比较最终落库结果。
/// </summary>
[Collection(ProtocolIngestParityCollection.Name)]
public sealed class ProtocolIngestParitySuite : IAsyncLifetime
{
    private const string AdminToken = "protocol-parity-admin";
    private const string Measurement = "protocol_cpu";
    private const string Payload =
        "protocol_cpu,host=edge-a value=11.5 1000\n" +
        "protocol_cpu,host=edge-b value=12.5 2000";
    private const string HttpDb = "protocol_http";
    private const string UdpDb = "protocol_udp";
    private const string MqttDb = "protocol_mqtt";
    private const string CoapDb = "protocol_coap";

    private WebApplication? _app;
    private string _baseUrl = string.Empty;
    private string? _dataRoot;
    private int _mqttPort;
    private int _coapPort;
    private int _udpPort;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _mqttPort = GetFreeTcpPort();
        _coapPort = GetFreeUdpPort();
        do
        {
            _udpPort = GetFreeUdpPort();
        }
        while (_udpPort == _coapPort);

        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-protocol-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
            Mqtt = new MqttBrokerOptions
            {
                Enabled = true,
                Port = _mqttPort,
                WebSocketPath = string.Empty,
            },
            Coap = new CoapServerOptions
            {
                Enabled = true,
                Port = _coapPort,
            },
            LineProtocolUdp = new LineProtocolUdpOptions
            {
                Enabled = true,
                Port = _udpPort,
                Database = UdpDb,
                MaxDatagramBytes = 4096,
                Precision = "ms",
            },
        };

        _app = BuildTestServer(options);
        await _app.StartAsync();
        _baseUrl = await ResolveHttpAddressAsync(_app);

        foreach (string database in new[] { HttpDb, UdpDb, MqttDb, CoapDb })
        {
            await CreateDatabaseAsync(database);
            await ExecuteSqlAsync(
                database,
                $"CREATE MEASUREMENT {Measurement} (host TAG, value FIELD FLOAT)");
        }
    }

    /// <inheritdoc />
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
                // Windows 上服务停止后文件句柄可能短暂延迟释放，测试清理尽力而为。
            }
        }
    }

    /// <summary>
    /// 验证四种协议入口对同一 Line Protocol payload 产生相同的时序行。
    /// </summary>
    [Fact]
    public async Task LineProtocol_HttpUdpMqttAndCoap_ProduceEquivalentRows()
    {
        await WriteHttpAsync();
        await WriteUdpAsync();
        await WriteMqttAsync();
        WriteCoap();

        ProtocolSnapshot http = await WaitForRowsAsync(HttpDb);
        ProtocolSnapshot udp = await WaitForRowsAsync(UdpDb);
        ProtocolSnapshot mqtt = await WaitForRowsAsync(MqttDb);
        ProtocolSnapshot coap = await WaitForRowsAsync(CoapDb);

        Assert.Equal(http, udp);
        Assert.Equal(http, mqtt);
        Assert.Equal(http, coap);
    }

    private async Task WriteHttpAsync()
    {
        using var client = CreateHttpClient();
        using var content = new StringContent(Payload, Encoding.UTF8, "text/plain");
        using var response = await client.PostAsync($"/write?db={HttpDb}&precision=ms", content);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task WriteUdpAsync()
    {
        using var client = new UdpClient();
        byte[] payload = Encoding.UTF8.GetBytes(Payload);
        await client.SendAsync(payload, "127.0.0.1", _udpPort);
    }

    private async Task WriteMqttAsync()
    {
        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", _mqttPort)
            .WithClientId("protocol-parity-writer")
            .WithCredentials("sonnetdb", AdminToken)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCleanStart(true)
            .Build();

        var connect = await client.ConnectAsync(options, CancellationToken.None);
        Assert.Equal(MqttClientConnectResultCode.Success, connect.ResultCode);
        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic($"db/{MqttDb}/m/{Measurement}")
                .WithPayload(Payload)
                .WithContentType("text/plain")
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            var publish = await client.PublishAsync(message, CancellationToken.None);
            Assert.True(publish.IsSuccess, publish.ReasonString);
        }
        finally
        {
            if (client.IsConnected)
                await client.DisconnectAsync(new MqttClientDisconnectOptions(), CancellationToken.None);
        }
    }

    private void WriteCoap()
    {
        var config = new CoapConfig();
        using var endpoint = new CoAPEndPoint(config);
        endpoint.Start();
        var client = new CoapClient(
            new Uri($"coap://127.0.0.1:{_coapPort}/db/{CoapDb}/m/{Measurement}?token={AdminToken}"),
            config)
        {
            EndPoint = endpoint,
            Timeout = 5000,
        };

        Response response = client.Post(Payload, MediaType.TextPlain);
        Assert.NotNull(response);
        Assert.Equal(StatusCode.Changed, response.StatusCode);
    }

    private async Task<ProtocolSnapshot> WaitForRowsAsync(string database)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        ProtocolSnapshot snapshot = default;
        while (DateTimeOffset.UtcNow < deadline)
        {
            snapshot = QuerySnapshot(database);
            if (snapshot.RowCount == 2)
                return snapshot;

            await Task.Delay(100);
        }

        Assert.Equal(2, snapshot.RowCount);
        return snapshot;
    }

    private ProtocolSnapshot QuerySnapshot(string database)
    {
        using var connection = new SndbConnection(
            $"Data Source=sonnetdb+http://{new Uri(_baseUrl).Authority}/{database};" +
            $"Token={AdminToken};Timeout=30;Protocol=rest");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT time, host, value FROM {Measurement} ORDER BY time";
        using var reader = command.ExecuteReader();

        var rows = new List<string>();
        while (reader.Read())
        {
            var cells = new string[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
            {
                object value = reader.GetValue(i);
                cells[i] = value switch
                {
                    DBNull => "null",
                    DateTime timestamp => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                    _ => value.ToString() ?? string.Empty,
                };
            }

            rows.Add(string.Join('|', cells));
        }

        return new ProtocolSnapshot(rows.Count, string.Join('\n', rows));
    }

    private async Task CreateDatabaseAsync(string database)
    {
        using var client = CreateHttpClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(new { name = database }),
            Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync(
            "/v1/db",
            content);
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());
    }

    private async Task ExecuteSqlAsync(string database, string sql)
    {
        using var client = CreateHttpClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(new { sql }),
            Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync(
            $"/v1/db/{database}/sql",
            content);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private HttpClient CreateHttpClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        return client;
    }

    private static WebApplication BuildTestServer(ServerOptions options)
    {
        string contentRoot = Path.Combine(Path.GetTempPath(), "sndb-protocol-parity-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        File.WriteAllText(
            Path.Combine(contentRoot, "appsettings.json"),
            JsonSerializer.Serialize(new AppSettings(options)));

        var app = global::SonnetDB.Program.BuildApp(
            ["--contentRoot", contentRoot, "--Kestrel:Endpoints:Http:Url=http://127.0.0.1:0"]);
        app.Lifetime.ApplicationStopped.Register(static state =>
        {
            string root = (string)state!;
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 测试宿主配置目录清理尽力而为。
            }
        }, contentRoot);
        return app;
    }

    private static async Task<string> ResolveHttpAddressAsync(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");

        foreach (string address in addresses.Addresses)
        {
            string target = address
                .Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)
                .Replace("[::]", "127.0.0.1", StringComparison.Ordinal);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, target + "/healthz")
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                };
                using var response = await client.SendAsync(request);
                if (response.IsSuccessStatusCode)
                    return target;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }
        }

        throw new InvalidOperationException("未找到可用的 HTTP/1.1 测试端口。");
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

    private static int GetFreeUdpPort()
    {
        using var listener = new UdpClient(0);
        return ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
    }

    private sealed record AppSettings(ServerOptions SonnetDBServer);

    private readonly record struct ProtocolSnapshot(int RowCount, string Rows);
}

/// <summary>
/// 多协议 parity 测试占用真实 TCP/UDP 端口，禁用并行以降低端口探测竞争。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProtocolIngestParityCollection
{
    /// <summary>测试集合名称。</summary>
    public const string Name = "Protocol ingest parity tests";
}
