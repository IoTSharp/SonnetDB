using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// PR #44 端到端测试：批量入库三端点 (lp / json / bulk) + RBAC + 错误策略 + flush。
/// </summary>
public sealed class BulkIngestEndpointTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;
    private const string _adminToken = "admin-bulk-token";
    private const string _readWriteToken = "rw-bulk-token";
    private const string _readOnlyToken = "ro-bulk-token";
    private const string _dbName = "bulkdb";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-bulk-server-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [_adminToken] = ServerRoles.Admin,
                [_readWriteToken] = ServerRoles.ReadWrite,
                [_readOnlyToken] = ServerRoles.ReadOnly,
            },
        };
        _app = TestServerHost.Build(options);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        // 准备数据库与 measurement
        using var admin = CreateClient(_adminToken);
        var create = await admin.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(_dbName), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var sql = await admin.PostAsync($"/v1/db/{_dbName}/sql",
            JsonContent.Create(new SqlRequest("CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)"),
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
            try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    private HttpClient CreateClient(string? token)
    {
        var c = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        if (token is not null)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static StringContent Text(string body, string mediaType = "text/plain")
        => new(body, Encoding.UTF8, mediaType);

    private static async Task<BulkIngestResponse> ParseAsync(HttpResponseMessage resp)
    {
        Assert.True(resp.IsSuccessStatusCode, $"{(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        var s = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync(s, ServerJsonContext.Default.BulkIngestResponse))!;
    }

    [Fact]
    public async Task LineProtocolEndpoint_WritesAllPoints()
    {
        using var c = CreateClient(_readWriteToken);
        var body = "cpu,host=a value=1 1\ncpu,host=a value=2 2\ncpu,host=b value=3 3";
        var resp = await c.PostAsync($"/v1/db/{_dbName}/measurements/cpu/lp", Text(body));
        var parsed = await ParseAsync(resp);
        Assert.Equal(3, parsed.WrittenRows);
        Assert.Equal(0, parsed.SkippedRows);
    }

    [Fact]
    public async Task JsonEndpoint_WritesAllPoints()
    {
        using var c = CreateClient(_readWriteToken);
        var body = """
        {"m":"ignored","points":[
          {"t":10,"tags":{"host":"a"},"fields":{"value":1.5}},
          {"t":20,"tags":{"host":"b"},"fields":{"value":2.5}}
        ]}
        """;
        var resp = await c.PostAsync($"/v1/db/{_dbName}/measurements/cpu/json", Text(body, "application/json"));
        var parsed = await ParseAsync(resp);
        Assert.Equal(2, parsed.WrittenRows);
    }

    [Fact]
    public async Task BulkValuesEndpoint_WritesAllPoints()
    {
        using var c = CreateClient(_readWriteToken);
        var body = "INSERT INTO cpu(host, value, time) VALUES "
            + "('a', 1.0, 100),('b', 2.0, 200),('c', 3.0, 300)";
        var resp = await c.PostAsync($"/v1/db/{_dbName}/measurements/cpu/bulk", Text(body));
        var parsed = await ParseAsync(resp);
        Assert.Equal(3, parsed.WrittenRows);
    }

    [Fact]
    public async Task LineProtocolEndpoint_OnErrorSkip_SkipsBadLines()
    {
        using var c = CreateClient(_readWriteToken);
        var body = "cpu,host=a value=1 1\nbad-line-without-fields\ncpu,host=a value=3 3";
        var resp = await c.PostAsync($"/v1/db/{_dbName}/measurements/cpu/lp?onerror=skip", Text(body));
        var parsed = await ParseAsync(resp);
        Assert.Equal(2, parsed.WrittenRows);
        Assert.Equal(1, parsed.SkippedRows);
    }

    [Fact]
    public async Task LineProtocolEndpoint_FailFastOnBadLine_Returns400()
    {
        using var c = CreateClient(_readWriteToken);
        var body = "cpu,host=a value=1 1\nnotvalid";
        var resp = await c.PostAsync($"/v1/db/{_dbName}/measurements/cpu/lp", Text(body));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task LineProtocolEndpoint_FlushTrue_Succeeds()
    {
        using var c = CreateClient(_readWriteToken);
        var resp = await c.PostAsync($"/v1/db/{_dbName}/measurements/cpu/lp?flush=true",
            Text("cpu,host=a value=1 1\ncpu,host=a value=2 2"));
        var parsed = await ParseAsync(resp);
        Assert.Equal(2, parsed.WrittenRows);
    }

    [Fact]
    public async Task LineProtocolEndpoint_FlushAsync_Succeeds()
    {
        using var c = CreateClient(_readWriteToken);
        var resp = await c.PostAsync($"/v1/db/{_dbName}/measurements/cpu/lp?flush=async",
            Text("cpu,host=a value=10 100\ncpu,host=a value=20 200"));
        var parsed = await ParseAsync(resp);
        // PR #48：?flush=async 仅向后台 Flush 线程发信号，立即返回；写入计数与 sync/none 一致。
        Assert.Equal(2, parsed.WrittenRows);
    }

    [Fact]
    public async Task LineProtocolEndpoint_FlushFalse_Succeeds()
    {
        using var c = CreateClient(_readWriteToken);
        var resp = await c.PostAsync($"/v1/db/{_dbName}/measurements/cpu/lp?flush=false",
            Text("cpu,host=a value=11 110\ncpu,host=a value=22 220"));
        var parsed = await ParseAsync(resp);
        // PR #48：?flush=false（默认）仅入 MemTable + WAL，不触发 Flush。
        Assert.Equal(2, parsed.WrittenRows);
    }

    [Fact]
    public async Task BulkEndpoint_RequiresWriteRole()
    {
        using var c = CreateClient(_readOnlyToken);
        var resp = await c.PostAsync($"/v1/db/{_dbName}/measurements/cpu/lp",
            Text("cpu,host=a value=1 1"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task BulkEndpoint_UnknownDatabase_Returns404()
    {
        using var c = CreateClient(_adminToken);
        var resp = await c.PostAsync($"/v1/db/nope/measurements/cpu/lp",
            Text("cpu,host=a value=1 1"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task BulkEndpoint_RequiresAuth()
    {
        using var c = CreateClient(token: null);
        var resp = await c.PostAsync($"/v1/db/{_dbName}/measurements/cpu/lp",
            Text("cpu,host=a value=1 1"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task BulkEndpoint_FollowedBySelect_ReturnsWrittenRows()
    {
        using var c = CreateClient(_adminToken);
        var resp = await c.PostAsync($"/v1/db/{_dbName}/measurements/cpu/lp",
            Text("cpu,host=verify value=10 1000\ncpu,host=verify value=20 2000"));
        await ParseAsync(resp);

        // 通过普通 SQL 端点回查
        var sel = await c.PostAsync($"/v1/db/{_dbName}/sql",
            JsonContent.Create(
                new SqlRequest("SELECT value FROM cpu WHERE host='verify' AND time >= 1000 AND time <= 2000"),
                ServerJsonContext.Default.SqlRequest));
        Assert.True(sel.IsSuccessStatusCode);
        var text = await sel.Content.ReadAsStringAsync();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // meta + 2 行 + end
        Assert.Equal(4, lines.Length);
    }
}
