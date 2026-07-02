using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Hosting;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// SSE 端到端测试：验证 <c>/v1/events</c> 推送
/// <c>db</c> / <c>slow_query</c> / <c>metrics</c> / <c>hello</c> 事件，
/// 并验证 <c>?access_token=</c> query 鉴权。
/// </summary>
public sealed class SseEndToEndTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;
    private const string _adminToken = "admin-sse-token";
    private const string _visibleDb = "alpha";
    private const string _hiddenDb = "beta";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-sse-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            // 阈值压到 0 → 任何 SQL 都会广播 slow_query，便于断言
            SlowQueryThresholdMs = 0,
            // metrics tick 拉到 1s，加快测试
            MetricsTickSeconds = 1,
            Tokens = new Dictionary<string, string>
            {
                [_adminToken] = ServerRoles.Admin,
            },
        };
        options.Copilot.Docs.AutoIngestOnStartup = false;
        options.Copilot.Skills.AutoIngestOnStartup = false;

        _app = TestServerHost.Build(options);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
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
            try { Directory.Delete(_dataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Events_RequiresToken_ReturnsUnauthorized()
    {
        using var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        var resp = await client.GetAsync("/v1/events");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Events_AccessTokenQueryString_ReceivesHelloAndDbEvents()
    {
        // 1) 打开 SSE 流（用 query token，模拟 EventSource）
        using var sseClient = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        using var streamReq = new HttpRequestMessage(HttpMethod.Get, $"/v1/events?access_token={_adminToken}&stream=db,slow_query,metrics");
        var streamResp = await sseClient.SendAsync(streamReq, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, streamResp.StatusCode);
        Assert.Equal("text/event-stream", streamResp.Content.Headers.ContentType?.MediaType);

        var buffer = new StringBuilder();
        var stream = await streamResp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // 先收 hello
        var hello = await ReadOneEventAsync(reader, buffer, TimeSpan.FromSeconds(5));
        Assert.Equal("hello", hello.Event);

        // 2) 用另一个 client 触发 CREATE/DROP DATABASE
        using var apiClient = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        var dbName = "ssetest_" + Guid.NewGuid().ToString("N")[..8];
        var create = await apiClient.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(dbName), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        // 3) 流上应当能收到 db.created 事件
        var dbEvt = await ReadDatabaseEventAsync(reader, buffer, dbName, DatabaseEvent.ActionCreated, TimeSpan.FromSeconds(5));
        using (var doc = JsonDocument.Parse(dbEvt.Data))
        {
            Assert.Equal(dbName, doc.RootElement.GetProperty("database").GetString());
            Assert.Equal("created", doc.RootElement.GetProperty("action").GetString());
        }

        // 4) 触发一条 SQL，因为阈值 = 0，必收到 slow_query
        await apiClient.PostAsync($"/v1/db/{dbName}/sql",
            JsonContent.Create(new SqlRequest("CREATE MEASUREMENT m (host TAG, v FIELD FLOAT)"), ServerJsonContext.Default.SqlRequest));
        var slow = await ReadEventOfTypeAsync(reader, buffer, "slow_query", TimeSpan.FromSeconds(5));
        using (var doc = JsonDocument.Parse(slow.Data))
        {
            Assert.Equal(dbName, doc.RootElement.GetProperty("database").GetString());
            Assert.False(doc.RootElement.GetProperty("failed").GetBoolean());
            Assert.Equal(SlowQuerySeverity.Slow, doc.RootElement.GetProperty("severity").GetString());
        }

        // 5) metrics tick = 1s，等 ≤ 3 秒应能收到至少一次 metrics
        var metrics = await ReadEventOfTypeAsync(reader, buffer, "metrics", TimeSpan.FromSeconds(4));
        using (var doc = JsonDocument.Parse(metrics.Data))
        {
            Assert.True(doc.RootElement.GetProperty("databases").GetInt32() >= 1);
            Assert.True(doc.RootElement.GetProperty("subscriberCount").GetInt32() >= 1);
        }

        // 6) DROP → db.dropped
        var drop = await apiClient.DeleteAsync($"/v1/db/{dbName}");
        Assert.Equal(HttpStatusCode.OK, drop.StatusCode);
        var dropEvt = await ReadDatabaseEventAsync(reader, buffer, dbName, DatabaseEvent.ActionDropped, TimeSpan.FromSeconds(5));
        using (var doc = JsonDocument.Parse(dropEvt.Data))
        {
            Assert.Equal(dbName, doc.RootElement.GetProperty("database").GetString());
            Assert.Equal("dropped", doc.RootElement.GetProperty("action").GetString());
        }
    }

    [Fact]
    public async Task Events_WithDynamicUser_FiltersDbAndSlowQueryByGrant()
    {
        using var admin = CreateClient(_adminToken);
        await CreateDatabaseAsync(admin, _visibleDb);
        await CreateDatabaseAsync(admin, _hiddenDb);
        await ExecuteSqlAsync(admin, _visibleDb, "CREATE USER watcher WITH PASSWORD 'p'");
        await ExecuteSqlAsync(admin, _visibleDb, $"GRANT READ ON DATABASE {_visibleDb} TO watcher");
        var watcherToken = await LoginAsync("watcher", "p");

        using var sseClient = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        using var streamReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v1/events?access_token={watcherToken}&stream=db,slow_query");
        var streamResp = await sseClient.SendAsync(streamReq, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, streamResp.StatusCode);

        using var stream = await streamResp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buffer = new StringBuilder();

        var hello = await ReadOneEventAsync(reader, buffer, TimeSpan.FromSeconds(5));
        Assert.Equal("hello", hello.Event);

        await ExecuteSqlAsync(admin, _hiddenDb, "CREATE MEASUREMENT hidden_cpu (host TAG, v FIELD FLOAT)");
        await ExecuteSqlAsync(admin, _visibleDb, "CREATE MEASUREMENT visible_cpu (host TAG, v FIELD FLOAT)");

        var slow = await ReadEventOfTypeAsync(reader, buffer, ServerEvent.ChannelSlowQuery, TimeSpan.FromSeconds(5));
        using (var doc = JsonDocument.Parse(slow.Data))
        {
            Assert.Equal(_visibleDb, doc.RootElement.GetProperty("database").GetString());
            Assert.Contains("visible_cpu", doc.RootElement.GetProperty("sql").GetString());
            Assert.Equal(SlowQuerySeverity.Slow, doc.RootElement.GetProperty("severity").GetString());
        }

        await CreateDatabaseAsync(admin, "gamma");
        var drop = await admin.DeleteAsync($"/v1/db/{_visibleDb}");
        Assert.Equal(HttpStatusCode.OK, drop.StatusCode);

        var dbEvt = await ReadEventOfTypeAsync(reader, buffer, ServerEvent.ChannelDatabase, TimeSpan.FromSeconds(5));
        using (var doc = JsonDocument.Parse(dbEvt.Data))
        {
            Assert.Equal(_visibleDb, doc.RootElement.GetProperty("database").GetString());
            Assert.Equal(DatabaseEvent.ActionDropped, doc.RootElement.GetProperty("action").GetString());
        }
    }

    [Fact]
    public async Task Events_WithDynamicUser_FiltersMetricsPerDatabaseSegments()
    {
        using var admin = CreateClient(_adminToken);
        await CreateDatabaseAsync(admin, _visibleDb);
        await CreateDatabaseAsync(admin, _hiddenDb);
        await ExecuteSqlAsync(admin, _visibleDb, "CREATE USER metrics_reader WITH PASSWORD 'p'");
        await ExecuteSqlAsync(admin, _visibleDb, $"GRANT READ ON DATABASE {_visibleDb} TO metrics_reader");
        var readerToken = await LoginAsync("metrics_reader", "p");

        using var sseClient = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        using var streamReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v1/events?access_token={readerToken}&stream=metrics");
        var streamResp = await sseClient.SendAsync(streamReq, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, streamResp.StatusCode);

        using var stream = await streamResp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buffer = new StringBuilder();

        var hello = await ReadOneEventAsync(reader, buffer, TimeSpan.FromSeconds(5));
        Assert.Equal("hello", hello.Event);

        var metrics = await ReadEventOfTypeAsync(reader, buffer, ServerEvent.ChannelMetrics, TimeSpan.FromSeconds(4));
        using var doc = JsonDocument.Parse(metrics.Data);
        Assert.Equal(1, doc.RootElement.GetProperty("databases").GetInt32());
        var perDatabase = doc.RootElement.GetProperty("perDatabaseSegments");
        Assert.True(perDatabase.TryGetProperty(_visibleDb, out _));
        Assert.False(perDatabase.TryGetProperty(_hiddenDb, out _));
    }

    private HttpClient CreateClient(string token)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> LoginAsync(string username, string password)
    {
        using var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        var response = await client.PostAsync(
            "/v1/auth/login",
            JsonContent.Create(new LoginRequest(username, password), ServerJsonContext.Default.LoginRequest));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"登录失败：{(int)response.StatusCode} {body}");

        var login = JsonSerializer.Deserialize(body, ServerJsonContext.Default.LoginResponse);
        Assert.NotNull(login);
        return login!.Token;
    }

    private static async Task CreateDatabaseAsync(HttpClient client, string databaseName)
    {
        var response = await client.PostAsync(
            "/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(databaseName), ServerJsonContext.Default.CreateDatabaseRequest));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"创建数据库失败：{(int)response.StatusCode} {body}");
    }

    private static async Task ExecuteSqlAsync(HttpClient client, string db, string sql)
    {
        var response = await client.PostAsync(
            $"/v1/db/{db}/sql",
            JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"执行 SQL 失败：{(int)response.StatusCode} {body}");
    }

    private static async Task<(string Event, string Data)> ReadEventOfTypeAsync(
        StreamReader reader, StringBuilder buffer, string expectedType, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            var evt = await ReadOneEventAsync(reader, buffer, remaining);
            if (evt.Event == expectedType)
                return evt;
            // 忽略其他通道（hello / metrics / db / slow_query）继续等
        }
        throw new TimeoutException($"未在 {timeout.TotalSeconds:F1}s 内收到 type='{expectedType}' 事件。");
    }

    private static async Task<(string Event, string Data)> ReadDatabaseEventAsync(
        StreamReader reader,
        StringBuilder buffer,
        string databaseName,
        string action,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            var evt = await ReadEventOfTypeAsync(reader, buffer, ServerEvent.ChannelDatabase, remaining);
            using var doc = JsonDocument.Parse(evt.Data);
            if (string.Equals(doc.RootElement.GetProperty("database").GetString(), databaseName, StringComparison.Ordinal)
                && string.Equals(doc.RootElement.GetProperty("action").GetString(), action, StringComparison.Ordinal))
            {
                return evt;
            }
        }

        throw new TimeoutException(
            $"未在 {timeout.TotalSeconds:F1}s 内收到 database='{databaseName}', action='{action}' 的 db 事件。");
    }

    /// <summary>
    /// 从 SSE 流读取一个完整事件（以空行结尾）。注释行（": ..."）被跳过。
    /// </summary>
    private static async Task<(string Event, string Data)> ReadOneEventAsync(
        StreamReader reader, StringBuilder buffer, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        string evtType = "message";
        var data = new StringBuilder();

        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            if (line is null)
                throw new IOException("SSE 流已关闭。");
            if (line.Length == 0)
            {
                if (data.Length > 0 || evtType != "message")
                    return (evtType, data.ToString().TrimEnd('\n'));
                continue; // 空事件，继续
            }
            if (line.StartsWith(':')) continue; // 注释 / 心跳
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                evtType = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line["data: ".Length..]);
            }
            // 忽略 id: / retry:
        }
    }
}
