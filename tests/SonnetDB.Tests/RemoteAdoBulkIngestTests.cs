using System.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.Data.Remote;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// PR #44 端到端测试：远程 ADO 客户端 + <see cref="CommandType.TableDirect"/> →
/// HTTP 三端点 → 服务端 <see cref="SonnetDB.Ingest.BulkIngestor"/>。
/// </summary>
public sealed class RemoteAdoBulkIngestTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string _baseUrl = string.Empty;
    private string? _dataRoot;
    private const string _adminToken = "remote-bulk-admin";
    private const string _readOnlyToken = "remote-bulk-ro";
    private const string _dbName = "remote_bulk_e2e";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-remote-bulk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [_adminToken] = ServerRoles.Admin,
                [_readOnlyToken] = ServerRoles.ReadOnly,
            },
        };
        _app = TestServerHost.Build(options);
        await _app.StartAsync();
        var addr = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addr.Addresses.First();

        // 创建数据库 + measurement
        using var http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _adminToken);
        var resp = await http.PostAsync("/v1/db", new StringContent(
            $"{{\"name\":\"{_dbName}\"}}", System.Text.Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
        var ddl = await http.PostAsync($"/v1/db/{_dbName}/sql", new StringContent(
            "{\"sql\":\"CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)\"}",
            System.Text.Encoding.UTF8, "application/json"));
        ddl.EnsureSuccessStatusCode();
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

    private string ConnString(string token = _adminToken)
        => $"Data Source=sonnetdb+http://{new Uri(_baseUrl).Authority}/{_dbName};Token={token};Timeout=30";

    private SndbConnection Open(string token = _adminToken)
    {
        var c = new SndbConnection(ConnString(token));
        c.Open();
        return c;
    }

    [Fact]
    public void Remote_TableDirect_LineProtocol_WithMeasurementPrefix_Succeeds()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText = "cpu\ncpu,host=a value=1 1\ncpu,host=a value=2 2\ncpu,host=b value=3 3";
        var written = cmd.ExecuteNonQuery();
        Assert.Equal(3, written);
    }

    [Fact]
    public void Remote_TableDirect_LineProtocol_MeasurementParameter_Succeeds()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText = "ignored,host=a value=1 1\nignored,host=b value=2 2";
        var p = cmd.CreateParameter();
        p.ParameterName = "measurement";
        p.Value = "cpu";
        cmd.Parameters.Add(p);
        Assert.Equal(2, cmd.ExecuteNonQuery());
    }

    [Fact]
    public void Remote_TableDirect_Json_Succeeds()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText = """
        {"m":"cpu","points":[
          {"t":1,"tags":{"host":"a"},"fields":{"value":1.5}},
          {"t":2,"tags":{"host":"b"},"fields":{"value":2.5}}
        ]}
        """;
        Assert.Equal(2, cmd.ExecuteNonQuery());
    }

    [Fact]
    public void Remote_TableDirect_BulkValues_Succeeds()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText = "INSERT INTO cpu(host,value,time) VALUES ('a',1.0,10),('b',2.0,20)";
        Assert.Equal(2, cmd.ExecuteNonQuery());
    }

    [Fact]
    public void Remote_TableDirect_OnErrorSkip_PassesQueryString()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText = "cpu\ncpu,host=a value=1 1\nbroken-line\ncpu,host=a value=3 3";
        var p = cmd.CreateParameter();
        p.ParameterName = "onerror";
        p.Value = "skip";
        cmd.Parameters.Add(p);
        Assert.Equal(2, cmd.ExecuteNonQuery());
    }

    [Fact]
    public void Remote_TableDirect_ReadOnlyToken_Throws403()
    {
        using var c = Open(_readOnlyToken);
        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText = "cpu\ncpu,host=a value=1 1";
        var ex = Assert.Throws<SndbServerException>(() => cmd.ExecuteNonQuery());
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, ex.StatusCode);
    }

    [Fact]
    public void Remote_TableDirect_NoMeasurement_ThrowsInvalidOperation()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        // 单行 LP（无前缀）→ DetectWithPrefix 不返回 measurement，且未提供参数
        cmd.CommandText = "cpu,host=a value=1 1";
        Assert.Throws<InvalidOperationException>(() => cmd.ExecuteNonQuery());
    }
}
