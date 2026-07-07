using System.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// M28 P5b #261 端到端等价测试：远程 ADO 批量入库（<see cref="CommandType.TableDirect"/>）分别在
/// <c>Protocol=frame-http2</c>（tsdb 列式写帧，服务端 #237）与 <c>Protocol=rest</c>（REST bulk 端点）
/// 下执行，断言写入行数与 SQL 回查数据一致；并验证帧不适用的场景（BulkValues / onerror=skip）
/// 优雅回落 REST，以及错误码在两传输下同码（forbidden / db_not_found / bulk_ingest_error）。
/// </summary>
public sealed class TsdbBulkFrameTransportParityTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string _baseUrl = string.Empty;
    private string? _dataRoot;
    private const string _adminToken = "tsdb-bulk-admin";
    private const string _readOnlyToken = "tsdb-bulk-ro";
    private const string _dbName = "tsdb_bulk_parity";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-tsdb-bulk-parity-" + Guid.NewGuid().ToString("N"));
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
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        using var http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _adminToken);
        var resp = await http.PostAsync("/v1/db", new StringContent(
            $"{{\"name\":\"{_dbName}\"}}", System.Text.Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
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

    private string ConnString(string protocol, string token = _adminToken)
        => $"Data Source=sonnetdb+http://{new Uri(_baseUrl).Authority}/{_dbName};Token={token};Timeout=30;Protocol={protocol}";

    private SndbConnection Open(string protocol, string token = _adminToken)
    {
        var c = new SndbConnection(ConnString(protocol, token));
        c.Open();
        return c;
    }

    // ────────────────────────────── Line Protocol ──────────────────────────────

    [Theory]
    [InlineData("frame-http2")]
    [InlineData("rest")]
    public void LineProtocol_WriteThenReadBack(string protocol)
    {
        string measurement = "lp_" + protocol.Replace('-', '_');
        using var c = Open(protocol);
        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText =
            $"{measurement}\n" +
            $"{measurement},host=a value=1.5 1000\n" +
            $"{measurement},host=a value=2.5 2000\n" +
            $"{measurement},host=b value=3.5 3000";
        Assert.Equal(3, cmd.ExecuteNonQuery());

        Assert.Equal(3, CountRows(c, $"SELECT value FROM {measurement} WHERE time >= 1000 AND time <= 3000"));
        Assert.Equal(2, CountRows(c, $"SELECT value FROM {measurement} WHERE host='a' AND time >= 1000 AND time <= 3000"));
    }

    // ────────────────────────────── JSON ──────────────────────────────

    [Theory]
    [InlineData("frame-http2")]
    [InlineData("rest")]
    public void Json_WriteThenReadBack(string protocol)
    {
        string measurement = "json_" + protocol.Replace('-', '_');
        using var c = Open(protocol);
        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText =
            "{\"m\":\"" + measurement + "\",\"points\":[" +
            "{\"t\":100,\"tags\":{\"host\":\"a\"},\"fields\":{\"value\":1.25,\"n\":10}}," +
            "{\"t\":200,\"tags\":{\"host\":\"a\"},\"fields\":{\"value\":2.25}}," +
            "{\"t\":300,\"tags\":{\"host\":\"b\"},\"fields\":{\"value\":3.25,\"n\":30}}" +
            "]}";
        Assert.Equal(3, cmd.ExecuteNonQuery());

        Assert.Equal(3, CountRows(c, $"SELECT value FROM {measurement} WHERE time >= 100 AND time <= 300"));
        // 稀疏字段 n 只有 2 个点
        Assert.Equal(2, CountRows(c, $"SELECT n FROM {measurement} WHERE time >= 100 AND time <= 300"));
    }

    [Fact]
    public void Frame_And_Rest_ProduceIdenticalData()
    {
        // 同数据分别经帧与 REST 写入不同 measurement，SQL 回查逐行相等。
        const string body =
            "{0}\n{0},host=eq value=1.5 100\n{0},host=eq value=2.5 200";

        using var frameConn = Open("frame-http2");
        using (var f = frameConn.CreateCommand())
        {
            f.CommandType = CommandType.TableDirect;
            f.CommandText = string.Format(body, "eq_frame");
            Assert.Equal(2, f.ExecuteNonQuery());
        }

        using var restConn = Open("rest");
        using (var r = restConn.CreateCommand())
        {
            r.CommandType = CommandType.TableDirect;
            r.CommandText = string.Format(body, "eq_rest");
            Assert.Equal(2, r.ExecuteNonQuery());
        }

        var frameRows = ReadValues(frameConn, "SELECT time, value FROM eq_frame WHERE time >= 100 AND time <= 200 ORDER BY time");
        var restRows = ReadValues(restConn, "SELECT time, value FROM eq_rest WHERE time >= 100 AND time <= 200 ORDER BY time");
        Assert.Equal(restRows, frameRows);
        Assert.Equal(new[] { 1.5, 2.5 }, frameRows);
    }

    // ────────────────────────────── 回落 REST ──────────────────────────────

    [Fact]
    public void BulkValues_FallsBackToRest_UnderFrameProtocol()
    {
        // BulkValues 需服务端 schema 解析 tag/field 列角色，客户端无法列式编码 → 恒走 REST。
        using var c = Open("frame-http2");
        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText = "INSERT INTO bv_m(host,value,time) VALUES ('a',1.0,10),('b',2.0,20)";
        Assert.Equal(2, cmd.ExecuteNonQuery());
        Assert.Equal(2, CountRows(c, "SELECT value FROM bv_m WHERE time >= 10 AND time <= 20"));
    }

    [Fact]
    public void OnErrorSkip_FallsBackToRest_UnderFrameProtocol()
    {
        // onerror=skip 语义帧端点不支持（FailFast），必须回落 REST 才能跳过坏行。
        using var c = Open("frame-http2");
        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText = "skip_m\nskip_m,host=a value=1 1\nbroken-line\nskip_m,host=a value=3 3";
        var p = cmd.CreateParameter();
        p.ParameterName = "onerror";
        p.Value = "skip";
        cmd.Parameters.Add(p);
        Assert.Equal(2, cmd.ExecuteNonQuery());
    }

    [Theory]
    [InlineData("frame-http2")]
    [InlineData("rest")]
    public void FlushSync_WriteThenReadBack(string protocol)
    {
        string measurement = "flush_" + protocol.Replace('-', '_');
        using var c = Open(protocol);
        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText = $"{measurement}\n{measurement},host=a value=9.5 500";
        var p = cmd.CreateParameter();
        p.ParameterName = "flush";
        p.Value = "sync";
        cmd.Parameters.Add(p);
        Assert.Equal(1, cmd.ExecuteNonQuery());
        Assert.Equal(1, CountRows(c, $"SELECT value FROM {measurement} WHERE time >= 500 AND time <= 500"));
    }

    // ────────────────────────────── 错误码同码 ──────────────────────────────

    [Theory]
    [InlineData("frame-http2")]
    [InlineData("rest")]
    public void ReadOnlyToken_Forbidden_SameCode(string protocol)
    {
        using var c = Open(protocol, _readOnlyToken);
        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText = "forbid_m\nforbid_m,host=a value=1 1";
        var ex = Assert.Throws<SndbServerException>(() => cmd.ExecuteNonQuery());
        Assert.Equal("forbidden", ex.Error);
    }

    [Theory]
    [InlineData("frame-http2")]
    [InlineData("rest")]
    public void UnknownDb_NotFound_SameCode(string protocol)
    {
        var conn = new SndbConnection(
            $"Data Source=sonnetdb+http://{new Uri(_baseUrl).Authority}/no_such_db;Token={_adminToken};Timeout=30;Protocol={protocol}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText = "m\nm,host=a value=1 1";
        var ex = Assert.Throws<SndbServerException>(() => cmd.ExecuteNonQuery());
        Assert.Equal("db_not_found", ex.Error);
        conn.Dispose();
    }

    [Theory]
    [InlineData("frame-http2")]
    [InlineData("rest")]
    public void SchemaTypeConflict_BulkIngestError_SameCode(string protocol)
    {
        string measurement = "conflict_" + protocol.Replace('-', '_');
        using var c = Open(protocol);

        // 先以 float 建 schema
        using (var first = c.CreateCommand())
        {
            first.CommandType = CommandType.TableDirect;
            first.CommandText = $"{measurement}\n{measurement},host=a value=1.5 1";
            Assert.Equal(1, first.ExecuteNonQuery());
        }

        // 同字段改发字符串 → 引擎 schema 校验拒绝 → bulk_ingest_error（两传输同码）
        using var second = c.CreateCommand();
        second.CommandType = CommandType.TableDirect;
        second.CommandText =
            "{\"m\":\"" + measurement + "\",\"points\":[" +
            "{\"t\":2,\"tags\":{\"host\":\"a\"},\"fields\":{\"value\":\"oops\"}}]}";
        var ex = Assert.Throws<SndbServerException>(() => second.ExecuteNonQuery());
        Assert.Equal("bulk_ingest_error", ex.Error);
    }

    // ────────────────────────────── 辅助 ──────────────────────────────

    private static int CountRows(SndbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        int n = 0;
        while (r.Read()) n++;
        return n;
    }

    private static List<double> ReadValues(SndbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        int valueOrdinal = r.GetOrdinal("value");
        var list = new List<double>();
        while (r.Read())
            list.Add(Convert.ToDouble(r.GetValue(valueOrdinal)));
        return list;
    }
}
