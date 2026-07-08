using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using CoAP;
using CoAP.Channel;
using CoAP.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// M30 #265：CoAP 设备写入端到端测试。
/// </summary>
[Collection(CoapEndpointTestCollection.Name)]
public sealed class CoapEndpointTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;
    private int _coapPort;
    private int _coapsPort;
    private readonly ConcurrentQueue<string> _serverLogs = new();
    private const string AdminToken = "admin-coap-token";
    private const string ReadWriteToken = "rw-coap-token";
    private const string ReadOnlyToken = "ro-coap-token";
    private const string DtlsIdentity = "coap-device";
    private const string DtlsPsk = "coap-secret";
    private const string DbName = "coapdb";

    public async Task InitializeAsync()
    {
        _coapPort = GetFreeUdpPort();
        _coapsPort = GetFreeUdpPort();
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-coap-server-tests-" + Guid.NewGuid().ToString("N"));
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
            Coap = new CoapServerOptions
            {
                Enabled = true,
                Port = _coapPort,
                MaxPayloadBytes = 128 * 1024,
                Dtls = new CoapDtlsOptions
                {
                    Enabled = true,
                    Port = _coapsPort,
                    SessionIdleSeconds = 60,
                    PskKeys = new Dictionary<string, string>
                    {
                        [DtlsIdentity] = DtlsPsk,
                    },
                },
            },
        };

        _app = TestServerHost.Build(
            options,
            services => services.AddLogging(builder => builder.AddProvider(new QueueLoggerProvider(_serverLogs))));
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        using var admin = CreateClient(AdminToken);
        var create = await admin.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(DbName), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var sql = await admin.PostAsync($"/v1/db/{DbName}/sql",
            JsonContent.Create(new SqlRequest("CREATE MEASUREMENT coap_cpu (host TAG, value FIELD FLOAT)"),
                ServerJsonContext.Default.SqlRequest));
        Assert.True(sql.IsSuccessStatusCode);
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
    public async Task CoapPost_LineProtocol_WritesRows()
    {
        var response = PostCoap(
            $"coap://127.0.0.1:{_coapPort}/db/{DbName}/m/coap_cpu?token={ReadWriteToken}",
            "coap_cpu,host=a value=1 1\ncoap_cpu,host=a value=2 2",
            MediaType.TextPlain);

        Assert.NotNull(response);
        AssertStatus(response, StatusCode.Changed);
        await AssertRowCountAsync("host='a'", expectedRows: 2);
    }

    [Fact]
    public async Task CoapPut_LineProtocol_WritesRows()
    {
        var response = PutCoap(
            $"coap://127.0.0.1:{_coapPort}/db/{DbName}/m/coap_cpu?token={ReadWriteToken}",
            "coap_cpu,host=put value=3 3",
            MediaType.TextPlain);

        Assert.NotNull(response);
        AssertStatus(response, StatusCode.Changed);
        await AssertRowCountAsync("host='put'", expectedRows: 1);
    }

    [Fact]
    public void CoapPost_ReadOnlyToken_ReturnsForbidden()
    {
        var response = PostCoap(
            $"coap://127.0.0.1:{_coapPort}/db/{DbName}/m/coap_cpu?token={ReadOnlyToken}",
            "coap_cpu,host=ro value=1 1",
            MediaType.TextPlain);

        Assert.NotNull(response);
        AssertStatus(response, StatusCode.Forbidden);
    }

    [Fact]
    public async Task CoapPost_BlockwisePayload_WritesRows()
    {
        var lines = Enumerable.Range(0, 160)
            .Select(i => $"coap_cpu,host=block value={i} {10_000 + i}");
        var response = PostCoap(
            $"coap://127.0.0.1:{_coapPort}/db/{DbName}/m/coap_cpu?token={ReadWriteToken}",
            string.Join('\n', lines),
            MediaType.TextPlain);

        Assert.NotNull(response);
        AssertStatus(response, StatusCode.Changed);
        await AssertRowCountAsync("host='block'", expectedRows: 160);
    }

    [Fact]
    public async Task CoapsPost_DtlsPsk_WritesRows()
    {
        var response = PostCoaps(
            $"coaps://127.0.0.1:{_coapsPort}/db/{DbName}/m/coap_cpu?token={ReadWriteToken}",
            "coap_cpu,host=dtls value=7 7",
            MediaType.TextPlain);

        Assert.NotNull(response);
        AssertStatus(response, StatusCode.Changed);
        await AssertRowCountAsync("host='dtls'", expectedRows: 1);
    }

    [Fact]
    public async Task CoapObserve_MqTopic_PushesPublishedPayload()
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new CoapConfig();
        using var endpoint = new CoAPEndPoint(config);
        endpoint.Start();
        var client = new CoapClient(
            new Uri($"coap://127.0.0.1:{_coapPort}/db/{DbName}/mq/alerts?token={ReadOnlyToken}"),
            config)
        {
            EndPoint = endpoint,
            Timeout = 5000,
        };

        var relation = client.Observe(response =>
        {
            if (response.Payload is { Length: > 0 })
                received.TrySetResult(response.PayloadString);
        }, reason => received.TrySetException(new InvalidOperationException("CoAP Observe 失败：" + reason)));

        Assert.False(relation.Canceled);

        using var http = CreateClient(AdminToken);
        var publish = await http.PostAsync($"/v1/db/{DbName}/mq/alerts/publish",
            JsonContent.Create(
                new MqPublishRequest(
                    Encoding.UTF8.GetBytes("alarm-one"),
                    new Dictionary<string, string> { ["contentType"] = "text/plain" }),
                ServerJsonContext.Default.MqPublishRequest));
        Assert.True(publish.IsSuccessStatusCode, await publish.Content.ReadAsStringAsync());

        var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        relation.ProactiveCancel();
        Assert.Equal("alarm-one", payload);
    }

    private void AssertStatus(Response response, StatusCode expected)
    {
        Assert.True(
            response.StatusCode == expected,
            $"Expected {expected}, actual {response.StatusCode}, payload: {response.PayloadString}{Environment.NewLine}{string.Join(Environment.NewLine, _serverLogs)}");
    }

    private Response PostCoap(string uri, string payload, int mediaType)
    {
        var config = new CoapConfig();
        using var endpoint = new CoAPEndPoint(config);
        endpoint.Start();
        var client = new CoapClient(new Uri(uri), config)
        {
            EndPoint = endpoint,
            Timeout = 5000,
        };
        return client.Post(payload, mediaType);
    }

    private Response PutCoap(string uri, string payload, int mediaType)
    {
        var config = new CoapConfig();
        using var endpoint = new CoAPEndPoint(config);
        endpoint.Start();
        var client = new CoapClient(new Uri(uri), config)
        {
            EndPoint = endpoint,
            Timeout = 5000,
        };
        return client.Put(payload, mediaType);
    }

    private static Response PostCoaps(string uri, string payload, int mediaType)
    {
        var config = new CoapConfig();
        using var endpoint = new CoAPEndPoint(new DtlsPskClientChannel(DtlsIdentity, DtlsPsk), config);
        endpoint.Start();
        var client = new CoapClient(new Uri(uri), config)
        {
            EndPoint = endpoint,
            Timeout = 10000,
        };
        return client.Post(payload, mediaType);
    }

    private async Task AssertRowCountAsync(string predicate, int expectedRows)
    {
        using var client = CreateClient(AdminToken);
        var select = await client.PostAsync($"/v1/db/{DbName}/sql",
            JsonContent.Create(
                new SqlRequest($"SELECT value FROM coap_cpu WHERE {predicate}"),
                ServerJsonContext.Default.SqlRequest));
        Assert.True(select.IsSuccessStatusCode, await select.Content.ReadAsStringAsync());
        var text = await select.Content.ReadAsStringAsync();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expectedRows + 2, lines.Length);
    }

    private HttpClient CreateClient(string token)
    {
        var c = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static int GetFreeUdpPort()
    {
        using var udp = new UdpClient(0);
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private sealed class QueueLoggerProvider(ConcurrentQueue<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new QueueLogger(categoryName, messages);

        public void Dispose()
        {
        }
    }

    private sealed class QueueLogger(string categoryName, ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            messages.Enqueue($"{logLevel} {categoryName}: {formatter(state, exception)} {exception}");
        }
    }
}

/// <summary>
/// CoAP/DTLS 端到端测试绑定 UDP 端口，禁用并行避免端口探测与协议静态状态串扰。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CoapEndpointTestCollection
{
    public const string Name = "CoAP endpoint tests";
}
