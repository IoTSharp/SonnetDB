using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
using SonnetDB.Mqtt;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// M28 P5b #242 内建 MQTT broker 端到端测试：设备直连入库、鉴权与 SonnetMQ 订阅推送。
/// </summary>
[Collection(MqttBrokerTestCollection.Name)]
public sealed class MqttBrokerEndpointTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;
    private int _mqttPort;
    private const string AdminToken = "admin-mqtt-token";
    private const string ReadWriteToken = "rw-mqtt-token";
    private const string ReadOnlyToken = "ro-mqtt-token";

    public async Task InitializeAsync()
    {
        _mqttPort = GetFreeTcpPort();
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-mqtt-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [AdminToken] = ServerRoles.Admin,
                [ReadWriteToken] = ServerRoles.ReadWrite,
                [ReadOnlyToken] = ServerRoles.ReadOnly,
            },
            Mqtt = new MqttBrokerOptions
            {
                Enabled = true,
                Port = _mqttPort,
                WebSocketPath = string.Empty,
                MaxMqSubscriptionsPerClient = 8,
                Sparkplug = new SparkplugOptions
                {
                    Enabled = true,
                    Database = "sparkplug",
                    MaxPayloadBytes = 1024 * 1024,
                    HostId = "sonnetdb-primary",
                    PublishHostState = true,
                    AllowCommands = true,
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

        if (_dataRoot is not null && Directory.Exists(_dataRoot))
        {
            try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task MqttPublishMeasurement_WithLineProtocol_WritesRows()
    {
        const string db = "mqttingest";
        await CreateDatabaseAsync(db);
        await ExecuteSqlAsync(db, "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)");

        var client = await ConnectMqttAsync(ReadWriteToken, "mqtt-ingest-writer");
        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic($"db/{db}/m/cpu")
                .WithPayload("cpu,host=mqtt value=42 1000")
                .WithContentType("text/plain")
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            var result = await client.PublishAsync(message, CancellationToken.None);
            Assert.True(result.IsSuccess, result.ReasonString);
        }
        finally
        {
            await DisconnectMqttAsync(client);
        }

        int rows = await CountSelectRowsAsync(db, "SELECT value FROM cpu WHERE host='mqtt' AND time >= 1000 AND time <= 1000");
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task MqttPublishSparkplug_BirthThenAliasData_WritesResolvedMetric()
    {
        const string db = "sparkplug";
        await CreateDatabaseAsync(db);

        var client = await ConnectMqttAsync(ReadWriteToken, "sparkplug-writer");
        try
        {
            var birth = new MqttApplicationMessageBuilder()
                .WithTopic("spBv1.0/factory/NBIRTH/edge01")
                .WithPayload(SparkplugTestPayloads.PayloadWithSequence(
                    1000,
                    0,
                    SparkplugTestPayloads.UInt64("bdSeq", null, 1),
                    SparkplugTestPayloads.Float("temperature", 1, 21.5f)))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            var birthResult = await client.PublishAsync(birth, CancellationToken.None);
            Assert.True(birthResult.IsSuccess, birthResult.ReasonString);

            var data = new MqttApplicationMessageBuilder()
                .WithTopic("spBv1.0/factory/NDATA/edge01")
                .WithPayload(SparkplugTestPayloads.PayloadWithSequence(
                    2000,
                    1,
                    SparkplugTestPayloads.Float(null, 1, 37.25f)))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            var dataResult = await client.PublishAsync(data, CancellationToken.None);
            Assert.True(dataResult.IsSuccess, dataResult.ReasonString);
        }
        finally
        {
            await DisconnectMqttAsync(client);
        }

        int rows = await CountSelectRowsAsync(
            db,
            "SELECT temperature FROM edge01 WHERE group_id='factory' AND edge_node_id='edge01' AND time >= 2000 AND time <= 2000");
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task MqttPublishSparkplug_SequenceGap_PublishesRebirthCommandAndSkipsData()
    {
        const string db = "sparkplug";
        await CreateDatabaseAsync(db);
        var received = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = await ConnectMqttAsync(ReadWriteToken, "sparkplug-rebirth-node");
        client.ApplicationMessageReceivedAsync += args =>
        {
            received.TrySetResult(args.ApplicationMessage);
            return Task.CompletedTask;
        };

        try
        {
            var subscribe = await client.SubscribeAsync(
                new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter("spBv1.0/factory/NCMD/edgegap", MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build(),
                CancellationToken.None);
            Assert.Equal(MqttClientSubscribeResultCode.GrantedQoS1, Assert.Single(subscribe.Items).ResultCode);

            var birth = new MqttApplicationMessageBuilder()
                .WithTopic("spBv1.0/factory/NBIRTH/edgegap")
                .WithPayload(SparkplugTestPayloads.PayloadWithSequence(
                    1000,
                    0,
                    SparkplugTestPayloads.UInt64("bdSeq", null, 1),
                    SparkplugTestPayloads.Float("temperature", 1, 20)))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            Assert.True((await client.PublishAsync(birth, CancellationToken.None)).IsSuccess);

            var gap = new MqttApplicationMessageBuilder()
                .WithTopic("spBv1.0/factory/NDATA/edgegap")
                .WithPayload(SparkplugTestPayloads.PayloadWithSequence(
                    2000,
                    2,
                    SparkplugTestPayloads.Float(null, 1, 99)))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            Assert.True((await client.PublishAsync(gap, CancellationToken.None)).IsSuccess);

            MqttApplicationMessage command = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("spBv1.0/factory/NCMD/edgegap", command.Topic);
            var reader = new SparkplugPayloadReader(
                command.Payload.ToArray(),
                ParseSparkplug("spBv1.0/factory/NCMD/edgegap"),
                new SparkplugAliasStore());
            Assert.True(reader.TryRead(out SonnetDB.Model.Point point));
            Assert.True(point.Fields["Node Control/Rebirth"].AsBool());
        }
        finally
        {
            await DisconnectMqttAsync(client);
        }

        int rows = await CountSelectRowsAsync(
            db,
            "SELECT temperature FROM edgegap WHERE time >= 2000 AND time <= 2000");
        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task MqttPublishSparkplug_CommandRequiresAdminAndExplicitApproval()
    {
        await CreateDatabaseAsync("sparkplug");
        byte[] payload = SparkplugCommandEncoder.EncodeRebirth(1);

        var readWrite = await ConnectMqttAsync(ReadWriteToken, "sparkplug-command-rw");
        try
        {
            var denied = await readWrite.PublishAsync(
                CreateApprovedCommand(payload),
                CancellationToken.None);
            Assert.False(denied.IsSuccess);
            Assert.Equal(MqttClientPublishReasonCode.NotAuthorized, denied.ReasonCode);
        }
        finally
        {
            await DisconnectMqttAsync(readWrite);
        }

        var admin = await ConnectMqttAsync(AdminToken, "sparkplug-command-admin");
        try
        {
            var unapproved = new MqttApplicationMessageBuilder()
                .WithTopic("spBv1.0/factory/NCMD/edge-command")
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            var missingApproval = await admin.PublishAsync(unapproved, CancellationToken.None);
            Assert.False(missingApproval.IsSuccess);
            Assert.Equal(MqttClientPublishReasonCode.NotAuthorized, missingApproval.ReasonCode);

            var approved = await admin.PublishAsync(
                CreateApprovedCommand(payload),
                CancellationToken.None);
            Assert.True(approved.IsSuccess, approved.ReasonString);
        }
        finally
        {
            await DisconnectMqttAsync(admin);
        }
    }

    [Fact]
    public async Task MqttSubscribeSparkplugHostState_ReceivesRetainedOnlineState()
    {
        await CreateDatabaseAsync("sparkplug");
        var client = await ConnectMqttAsync(ReadOnlyToken, "sparkplug-state-reader");

        try
        {
            var result = await client.SubscribeAsync(
                new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter("spBv1.0/STATE/sonnetdb-primary", MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build(),
                CancellationToken.None);
            Assert.Equal(MqttClientSubscribeResultCode.GrantedQoS1, Assert.Single(result.Items).ResultCode);

            var server = _app!.Services.GetRequiredService<MqttServer>();
            MqttApplicationMessage state = await server.GetRetainedMessageAsync(
                "spBv1.0/STATE/sonnetdb-primary");
            Assert.Equal("ONLINE", Encoding.UTF8.GetString(state.Payload.ToArray()));
            Assert.True(state.Retain);
        }
        finally
        {
            await DisconnectMqttAsync(client);
        }
    }

    [Fact]
    public async Task MqttPublishSparkplug_WithReadOnlyToken_ReturnsNotAuthorized()
    {
        await CreateDatabaseAsync("sparkplug");
        var client = await ConnectMqttAsync(ReadOnlyToken, "sparkplug-readonly");
        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic("spBv1.0/factory/NBIRTH/edge01")
                .WithPayload(SparkplugTestPayloads.Payload(
                    1000,
                    SparkplugTestPayloads.Float("temperature", 1, 21.5f)))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            var result = await client.PublishAsync(message, CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal(MqttClientPublishReasonCode.NotAuthorized, result.ReasonCode);
        }
        finally
        {
            await DisconnectMqttAsync(client);
        }
    }

    [Fact]
    public async Task MqttPublishMeasurement_WithReadOnlyToken_ReturnsNotAuthorized()
    {
        const string db = "mqttreadonly";
        await CreateDatabaseAsync(db);
        await ExecuteSqlAsync(db, "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)");

        var client = await ConnectMqttAsync(ReadOnlyToken, "mqtt-readonly-writer");
        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic($"db/{db}/m/cpu")
                .WithPayload("cpu,host=ro value=1 1")
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            var result = await client.PublishAsync(message, CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.Equal(MqttClientPublishReasonCode.NotAuthorized, result.ReasonCode);
        }
        finally
        {
            await DisconnectMqttAsync(client);
        }
    }

    [Fact]
    public async Task MqttPublishMeasurement_WithWrongCaseManagedTopic_ReturnsTopicNameInvalid()
    {
        const string db = "mqttcase";
        await CreateDatabaseAsync(db);
        await ExecuteSqlAsync(db, "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)");

        var client = await ConnectMqttAsync(ReadWriteToken, "mqtt-case-writer");
        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic($"DB/{db}/M/cpu")
                .WithPayload("cpu,host=case value=1 1")
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            var result = await client.PublishAsync(message, CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.Equal(MqttClientPublishReasonCode.TopicNameInvalid, result.ReasonCode);
        }
        finally
        {
            await DisconnectMqttAsync(client);
        }

        int rows = await CountSelectRowsAsync(db, "SELECT value FROM cpu WHERE host='case'");
        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task MqttSubscribeMqTopic_AfterPublish_ReceivesPersistedMessage()
    {
        const string db = "mqttmq";
        const string topic = "events";
        await CreateDatabaseAsync(db);

        var received = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriber = await ConnectMqttAsync(ReadOnlyToken, "mqtt-mq-subscriber");
        subscriber.ApplicationMessageReceivedAsync += args =>
        {
            received.TrySetResult(args.ApplicationMessage);
            return Task.CompletedTask;
        };

        try
        {
            var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter($"db/{db}/mq/{topic}", MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            var subscribeResult = await subscriber.SubscribeAsync(subscribeOptions, CancellationToken.None);
            var item = Assert.Single(subscribeResult.Items);
            Assert.Equal(MqttClientSubscribeResultCode.GrantedQoS1, item.ResultCode);

            var publisher = await ConnectMqttAsync(ReadWriteToken, "mqtt-mq-publisher");
            try
            {
                var payload = Encoding.UTF8.GetBytes("hello from mqtt");
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic($"db/{db}/mq/{topic}")
                    .WithPayload(payload)
                    .WithContentType("text/plain")
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                var publishResult = await publisher.PublishAsync(message, CancellationToken.None);
                Assert.True(publishResult.IsSuccess, publishResult.ReasonString);
            }
            finally
            {
                await DisconnectMqttAsync(publisher);
            }

            Assert.Equal(1, await GetMqNextOffsetAsync(db, topic));
            var pushed = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal($"db/{db}/mq/{topic}", pushed.Topic);
            Assert.Equal("hello from mqtt", Encoding.UTF8.GetString(pushed.Payload.ToArray()));
        }
        finally
        {
            await DisconnectMqttAsync(subscriber);
        }
    }

    private HttpClient CreateHttpClient(string token = AdminToken)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static MqttApplicationMessage CreateApprovedCommand(byte[] payload)
        => new MqttApplicationMessageBuilder()
            .WithTopic("spBv1.0/factory/NCMD/edge-command")
            .WithPayload(payload)
            .WithUserProperty("sndb-approved", (ReadOnlyMemory<byte>)"true"u8.ToArray())
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

    private static SonnetDB.Mqtt.SparkplugTopicRoute ParseSparkplug(string topic)
    {
        Assert.True(SonnetDB.Mqtt.SparkplugTopicParser.TryParse(
            topic,
            out SonnetDB.Mqtt.SparkplugTopicRoute route,
            out string error), error);
        return route;
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

    private async Task<long> GetMqNextOffsetAsync(string db, string topic)
    {
        using var client = CreateHttpClient();
        var resp = await client.PostAsync($"/v1/db/{db}/mq/{topic}/stats", null);
        string text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"MQ stats 失败：{(int)resp.StatusCode} {text}");
        var stats = JsonSerializer.Deserialize(text, ServerJsonContext.Default.MqStatsResponse)!;
        return stats.NextOffset;
    }

    private async Task<IMqttClient> ConnectMqttAsync(string token, string clientId)
    {
        var factory = new MqttClientFactory();
        var client = factory.CreateMqttClient();
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", _mqttPort)
            .WithClientId(clientId)
            .WithCredentials("sonnetdb", token)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCleanStart(true)
            .Build();

        var result = await client.ConnectAsync(options, CancellationToken.None);
        Assert.Equal(MqttClientConnectResultCode.Success, result.ResultCode);
        return client;
    }

    private static async Task DisconnectMqttAsync(IMqttClient client)
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

/// <summary>
/// MQTTnet 的进程内 broker 与生成路由共享宿主级资源，端到端实例不并行启动。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MqttBrokerTestCollection
{
    public const string Name = "mqtt-broker-endpoint-tests";
}
