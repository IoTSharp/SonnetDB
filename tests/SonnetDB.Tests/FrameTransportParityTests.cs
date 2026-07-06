using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.Data.Documents;
using SonnetDB.Data.Kv;
using SonnetDB.Data.Mq;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// M28 P5b #241 端到端等价测试：同一操作分别在 <c>Protocol=frame-http2</c> 与 <c>Protocol=rest</c>
/// 下经真实 Kestrel 执行，断言 MQ / KV / 文档结果字节一致；ADO SQL 因帧路径返回更富的 CLR 类型
/// （记录在案的差异，见 docs/frame-protocol.md）单独断言帧富类型。并验证帧不支持的操作在
/// <c>frame-http2</c> 下优雅回落 REST。
/// </summary>
public sealed class FrameTransportParityTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string _baseUrl = string.Empty;
    private string? _dataRoot;
    private const string _adminToken = "parity-admin";
    private const string _dbName = "parity_e2e";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-frame-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [_adminToken] = ServerRoles.Admin },
        };
        _app = TestServerHost.Build(options);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        using var http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        var resp = await http.PostAsync("/v1/db", new StringContent(
            $"{{\"name\":\"{_dbName}\"}}", Encoding.UTF8, "application/json"));
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

    private string ConnString(string protocol)
        => $"Data Source=sonnetdb+http://{new Uri(_baseUrl).Authority}/{_dbName};Token={_adminToken};Timeout=30;Protocol={protocol}";

    // ────────────────────────────── MQ ──────────────────────────────

    [Theory]
    [InlineData("frame-http2")]
    [InlineData("rest")]
    public async Task Mq_Publish_Pull_Ack_Batch_Stats(string protocol)
    {
        using var mq = new SndbMqClient(ConnString(protocol));
        string topic = "mq_" + protocol.Replace('-', '_');

        byte[] p1 = [0x00, 0x7F, 0x80, 0xFF];
        var headers = new Dictionary<string, string> { ["src"] = "设备-α" };
        long off1 = await mq.PublishAsync(topic, p1, headers);
        Assert.Equal(0, off1);

        var batch = new[]
        {
            new SndbMqPublishEntry(new byte[] { 1, 2, 3 }),
            new SndbMqPublishEntry(new byte[] { 4, 5, 6 }),
        };
        var offs = await mq.PublishManyAsync(topic, batch);
        Assert.Equal(new long[] { 1, 2 }, offs);

        var messages = await mq.PullAsync(topic, "g1", 10);
        Assert.Equal(3, messages.Count);
        Assert.Equal(p1, messages[0].Payload);
        Assert.Equal("设备-α", messages[0].Headers["src"]);
        Assert.Equal(new byte[] { 4, 5, 6 }, messages[2].Payload);

        long next = await mq.AckAsync(topic, "g1", messages[^1].Offset);
        Assert.Equal(3, next);

        // stats 恒走 REST，两模式都可用
        var stats = await mq.GetStatsAsync(topic);
        Assert.Equal(3, stats.NextOffset);
    }

    // ────────────────────────────── KV ──────────────────────────────

    [Theory]
    [InlineData("frame-http2")]
    [InlineData("rest")]
    public async Task Kv_Set_Get_Scan_WithNamespace(string protocol)
    {
        using var kv = new SndbKvClient(ConnString(protocol));
        string keyspace = "ks_" + protocol.Replace('-', '_');
        const string ns = "dev";

        long v1 = await kv.SetAsync(keyspace, ns, "k1", [10, 20, 30]);
        Assert.True(v1 > 0);
        await kv.SetAsync(keyspace, ns, "k2", [40, 50]);

        var got = await kv.GetAsync(keyspace, ns, "k1");
        Assert.NotNull(got);
        Assert.Equal(new byte[] { 10, 20, 30 }, got!.Value);
        Assert.Equal("k1", got.Key); // 返回的是未限定 key

        var missing = await kv.GetAsync(keyspace, ns, "nope");
        Assert.Null(missing);

        var scanned = await kv.ScanPrefixAsync(keyspace, ns, "k");
        Assert.Equal(2, scanned.Count);
        Assert.All(scanned, e => Assert.DoesNotContain(":", e.Key)); // 前缀已剥离
        Assert.Contains(scanned, e => e.Key == "k1");
        Assert.Contains(scanned, e => e.Key == "k2");
    }

    [Fact]
    public async Task Kv_UnsupportedOp_FallsBackToRest_UnderFrameProtocol()
    {
        using var kv = new SndbKvClient(ConnString("frame-http2"));
        // Increment 无帧 op → 无条件走 REST，即使 Protocol=frame-http2
        var (value, version) = await kv.IncrementAsync("ks_incr", "n", "counter", 5);
        Assert.Equal(5, value);
        Assert.True(version > 0);
    }

    // ────────────────────────────── 文档 ──────────────────────────────

    [Theory]
    [InlineData("frame-http2")]
    [InlineData("rest")]
    public async Task Document_Insert_FindOne_FindByIds(string protocol)
    {
        using var doc = new SndbDocumentClient(ConnString(protocol));
        string coll = "coll_" + protocol.Replace('-', '_');
        await doc.CreateCollectionAsync(coll);

        var writeResult = await doc.InsertManyAsync(coll, new[]
        {
            new KeyValuePair<string, string>("d1", "{\"name\":\"甲\",\"n\":1}"),
            new KeyValuePair<string, string>("d2", "{\"name\":\"乙\",\"n\":2}"),
        });
        Assert.Equal(2, writeResult.Inserted);

        var one = await doc.FindOneAsync(coll, "d1");
        Assert.NotNull(one);
        Assert.Equal("d1", one!.Id);
        using (var parsed = System.Text.Json.JsonDocument.Parse(one.Json))
            Assert.Equal("甲", parsed.RootElement.GetProperty("name").GetString());

        var found = await doc.FindAsync(coll, new SndbDocumentFindOptions { Ids = ["d1", "d2"] });
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public async Task Document_AdvancedFind_FallsBackToRest_UnderFrameProtocol()
    {
        using var doc = new SndbDocumentClient(ConnString("frame-http2"));
        const string coll = "coll_adv";
        await doc.CreateCollectionAsync(coll);
        await doc.InsertManyAsync(coll, new[]
        {
            new KeyValuePair<string, string>("a", "{\"tag\":\"x\"}"),
            new KeyValuePair<string, string>("b", "{\"tag\":\"y\"}"),
        });

        // 带 filter 的 advanced find 无帧 op → 回落 REST
        var found = await doc.FindAsync(coll, new SndbDocumentFindOptions
        {
            Filter = new SndbDocumentFilter { Path = "$.tag", Op = "eq", Value = System.Text.Json.JsonSerializer.SerializeToElement("x") },
        });
        var single = Assert.Single(found);
        Assert.Equal("a", single.Id);
    }

    // ────────────────────────────── ADO SQL ──────────────────────────────

    [Theory]
    [InlineData("frame-http2")]
    [InlineData("rest")]
    public void AdoSql_Select_ColumnsAndRowsMatch(string protocol)
    {
        SeedCpuMeasurement();

        using var c = new SndbConnection(ConnString(protocol));
        c.Open();
        using var sel = c.CreateCommand();
        sel.CommandText = "SELECT time, host, value FROM cpu ORDER BY time";
        using var r = sel.ExecuteReader();

        Assert.Equal(3, r.FieldCount);
        Assert.Equal(["time", "host", "value"], new[] { r.GetName(0), r.GetName(1), r.GetName(2) });
        Assert.True(r.Read());
        Assert.Equal(1000L, r.GetInt64(0));
        Assert.Equal("a", r.GetString(1));
        Assert.Equal(1.5, r.GetDouble(2));
        Assert.True(r.Read());
        Assert.Equal(2000L, r.GetInt64(0));
        Assert.False(r.Read());
    }

    [Fact]
    public void AdoSql_Write_FallsBackToRest_UnderFrameProtocol()
    {
        using var c = new SndbConnection(ConnString("frame-http2"));
        c.Open();
        using (var ddl = c.CreateCommand())
        {
            ddl.CommandText = "CREATE MEASUREMENT wr (host TAG, value FIELD FLOAT)";
            ddl.ExecuteNonQuery(); // 写语句 → 回落 REST，不抛
        }
        using var ins = c.CreateCommand();
        ins.CommandText = "INSERT INTO wr (time, host, value) VALUES (10, 'z', 9.5)";
        Assert.Equal(1, ins.ExecuteNonQuery());
    }

    [Fact]
    public void AdoSql_DateTimeColumn_FramePathReturnsRicherType()
    {
        // 记录在案的类型差异（docs/frame-protocol.md）：DATETIME 列在帧路径返回 DateTime、
        // REST NDJSON 路径返回 ISO 字符串。写与建表走 REST，只读 SELECT 分别验证两传输的类型。
        using (var c = new SndbConnection(ConnString("rest")))
        {
            c.Open();
            using var ddl = c.CreateCommand();
            ddl.CommandText = "CREATE TABLE evt (id INT, at DATETIME, PRIMARY KEY (id))";
            ddl.ExecuteNonQuery();
            using var ins = c.CreateCommand();
            ins.CommandText = "INSERT INTO evt (id, at) VALUES (1, '2026-07-06T10:20:30Z')";
            ins.ExecuteNonQuery();
        }

        using (var frameConn = new SndbConnection(ConnString("frame-http2")))
        {
            frameConn.Open();
            using var sel = frameConn.CreateCommand();
            sel.CommandText = "SELECT at FROM evt WHERE id = 1";
            using var r = sel.ExecuteReader();
            Assert.True(r.Read());
            Assert.IsType<DateTime>(r.GetValue(0)); // 帧富类型
        }

        using (var restConn = new SndbConnection(ConnString("rest")))
        {
            restConn.Open();
            using var sel = restConn.CreateCommand();
            sel.CommandText = "SELECT at FROM evt WHERE id = 1";
            using var r = sel.ExecuteReader();
            Assert.True(r.Read());
            Assert.IsType<string>(r.GetValue(0)); // REST NDJSON 字符串
        }
    }

    private void SeedCpuMeasurement()
    {
        using var c = new SndbConnection(ConnString("rest"));
        c.Open();
        using var ddl = c.CreateCommand();
        ddl.CommandText = "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)";
        ddl.ExecuteNonQuery();
        using var ins = c.CreateCommand();
        ins.CommandText = "INSERT INTO cpu (time, host, value) VALUES (1000, 'a', 1.5), (2000, 'a', 2.5)";
        ins.ExecuteNonQuery();
    }
}
