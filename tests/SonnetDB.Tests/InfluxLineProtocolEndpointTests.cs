using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
/// InfluxDB 兼容的 Line Protocol 写入端点端到端测试：
/// <c>POST /write</c>（v1）与 <c>POST /api/v2/write</c>（v2），
/// 含 measurement 解析、precision、gzip、Token 头、RBAC 与错误返回。
/// </summary>
public sealed class InfluxLineProtocolEndpointTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;
    private const string _adminToken = "admin-influx-token";
    private const string _readWriteToken = "rw-influx-token";
    private const string _readOnlyToken = "ro-influx-token";
    private const string _dbName = "influxdb";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-influx-server-tests-" + Guid.NewGuid().ToString("N"));
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

        // 准备数据库与多个 measurement，证明 measurement 来自 LP 行而不是 URL。
        using var admin = CreateClient(_adminToken, scheme: "Bearer");
        var create = await admin.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(_dbName), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        foreach (var ddl in new[]
        {
            "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)",
            "CREATE MEASUREMENT mem (host TAG, used FIELD FLOAT)",
        })
        {
            var sql = await admin.PostAsync($"/v1/db/{_dbName}/sql",
                JsonContent.Create(new SqlRequest(ddl), ServerJsonContext.Default.SqlRequest));
            Assert.True(sql.IsSuccessStatusCode, $"DDL '{ddl}' failed: {(int)sql.StatusCode} {await sql.Content.ReadAsStringAsync()}");
        }
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

    private HttpClient CreateClient(string? token, string scheme = "Bearer")
    {
        var c = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        if (token is not null)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(scheme, token);
        return c;
    }

    private static StringContent Lp(string body)
        => new(body, Encoding.UTF8, "text/plain");

    private static ByteArrayContent GzipLp(string body)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var w = new StreamWriter(gz, Encoding.UTF8))
            w.Write(body);
        var bytes = ms.ToArray();
        var c = new ByteArrayContent(bytes);
        c.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
        c.Headers.ContentEncoding.Add("gzip");
        return c;
    }

    [Fact]
    public async Task V1Write_ParsesMeasurementFromLine_Returns204()
    {
        using var c = CreateClient(_readWriteToken);
        // 同一请求里不同 measurement，URL 中没有 measurement，验证 measurement 来自每行。
        // precision=ms 让 timestamp 直接按毫秒解读。
        var body = "cpu,host=a value=1 1000\nmem,host=a used=2 2000\ncpu,host=b value=3 3000";
        var resp = await c.PostAsync($"/write?db={_dbName}&precision=ms", Lp(body));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task V2Write_ParsesMeasurementFromLine_Returns204()
    {
        using var c = CreateClient(_readWriteToken);
        var body = "cpu,host=a value=1 1000\nmem,host=a used=2 2000";
        var resp = await c.PostAsync($"/api/v2/write?bucket={_dbName}&org=ignored&precision=ms", Lp(body));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task V2Write_AcceptsTokenScheme()
    {
        // InfluxDB v2 客户端使用 `Authorization: Token <token>` 而不是 Bearer。
        using var c = CreateClient(_readWriteToken, scheme: "Token");
        var body = "cpu,host=a value=1 1000";
        var resp = await c.PostAsync($"/api/v2/write?bucket={_dbName}&precision=ms", Lp(body));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task V1Write_GzipBody_Decompressed()
    {
        using var c = CreateClient(_readWriteToken);
        var body = "cpu,host=a value=1 1000\ncpu,host=b value=2 2000\ncpu,host=c value=3 3000";
        var resp = await c.PostAsync($"/write?db={_dbName}&precision=ms", GzipLp(body));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task V1Write_DefaultPrecisionIsNanoseconds()
    {
        using var c = CreateClient(_readWriteToken);
        // 不带 precision 参数 → InfluxDB 默认 ns；时间戳 1_700_000_000_000_000_000ns 可被接受（÷1e6 → ms）。
        var body = "cpu,host=a value=1 1700000000000000000";
        var resp = await c.PostAsync($"/write?db={_dbName}", Lp(body));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Theory]
    [InlineData("n")]
    [InlineData("ns")]
    [InlineData("u")]
    [InlineData("us")]
    [InlineData("ms")]
    [InlineData("s")]
    public async Task V1Write_AllInfluxPrecisionAliases_Accepted(string precision)
    {
        using var c = CreateClient(_readWriteToken);
        // 任意精度都应当成功；用 1 作为兜底时间戳避免溢出。
        var body = "cpu,host=a value=1 1";
        var resp = await c.PostAsync($"/write?db={_dbName}&precision={precision}", Lp(body));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task V1Write_EmptyBody_Returns204()
    {
        using var c = CreateClient(_readWriteToken);
        var resp = await c.PostAsync($"/write?db={_dbName}&precision=ms", Lp(string.Empty));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task V1Write_BadLine_Returns400()
    {
        using var c = CreateClient(_readWriteToken);
        // 缺 fields 段，FailFast 下应该 400。
        var resp = await c.PostAsync($"/write?db={_dbName}&precision=ms", Lp("cpu,host=a"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task V1Write_MissingDb_Returns400()
    {
        using var c = CreateClient(_readWriteToken);
        var resp = await c.PostAsync("/write?precision=ms", Lp("cpu,host=a value=1 1"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task V2Write_MissingBucket_Returns400()
    {
        using var c = CreateClient(_readWriteToken);
        var resp = await c.PostAsync("/api/v2/write?org=x&precision=ms", Lp("cpu,host=a value=1 1"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task V1Write_UnknownDatabase_Returns404()
    {
        using var c = CreateClient(_adminToken);
        var resp = await c.PostAsync("/write?db=nope&precision=ms", Lp("cpu,host=a value=1 1"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task V1Write_ReadOnlyToken_Returns403()
    {
        using var c = CreateClient(_readOnlyToken);
        var resp = await c.PostAsync($"/write?db={_dbName}&precision=ms", Lp("cpu,host=a value=1 1"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task V1Write_NoToken_Returns401()
    {
        using var c = CreateClient(token: null);
        var resp = await c.PostAsync($"/write?db={_dbName}&precision=ms", Lp("cpu,host=a value=1 1"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task V1Write_ErrorBodyIsJson()
    {
        using var c = CreateClient(_readWriteToken);
        var resp = await c.PostAsync("/write?precision=ms", Lp("cpu,host=a value=1 1"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var s = await resp.Content.ReadAsStreamAsync();
        var err = await JsonSerializer.DeserializeAsync(s, ServerJsonContext.Default.ErrorResponse);
        Assert.NotNull(err);
        Assert.Equal("bad_request", err!.Error);
    }
}
