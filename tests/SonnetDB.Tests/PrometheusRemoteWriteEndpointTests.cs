using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Snappier;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// Prometheus Remote Write v1 兼容入站端点端到端测试：
/// <c>POST /api/v1/prom/write?db=&lt;name&gt;</c>，body = snappy(block) + protobuf(prometheus.WriteRequest)。
/// </summary>
public sealed class PrometheusRemoteWriteEndpointTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;
    private const string _adminToken = "admin-prom-token";
    private const string _readWriteToken = "rw-prom-token";
    private const string _readOnlyToken = "ro-prom-token";
    private const string _dbName = "promdb";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-prom-server-tests-" + Guid.NewGuid().ToString("N"));
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

        using var admin = CreateClient(_adminToken);
        var create = await admin.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(_dbName), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        // measurement 名取自 Prometheus metric 名（__name__）。
        foreach (var ddl in new[]
        {
            "CREATE MEASUREMENT http_requests_total (instance TAG, job TAG, value FIELD FLOAT)",
            "CREATE MEASUREMENT process_cpu_seconds (instance TAG, value FIELD FLOAT)",
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

    private HttpClient CreateClient(string? token)
    {
        var c = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        if (token is not null)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static ByteArrayContent SnappyProto(byte[] protoBytes)
    {
        var compressed = Snappy.CompressToArray(protoBytes);
        var c = new ByteArrayContent(compressed);
        c.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        c.Headers.ContentEncoding.Add("snappy");
        return c;
    }

    [Fact]
    public async Task PromWrite_HappyPath_Returns204AndPersists()
    {
        using var c = CreateClient(_readWriteToken);
        // WriteRequest{ ts1: __name__=http_requests_total, instance="a", job="api"; samples=[(1.0, 1000ms),(2.0, 2000ms)] }
        var bytes = ProtoEncoder.WriteRequest(
            ProtoEncoder.TimeSeries(
                labels: new (string, string)[] { ("__name__", "http_requests_total"), ("instance", "a"), ("job", "api") },
                samples: new (double, long)[] { (1.0d, 1000L), (2.0d, 2000L) }));

        var resp = await c.PostAsync($"/api/v1/prom/write?db={_dbName}", SnappyProto(bytes));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // 回查：应当能读到 2 行。
        var sel = await c.PostAsync($"/v1/db/{_dbName}/sql",
            JsonContent.Create(
                new SqlRequest("SELECT value FROM http_requests_total WHERE instance='a' AND time >= 1000 AND time <= 2000"),
                ServerJsonContext.Default.SqlRequest));
        Assert.True(sel.IsSuccessStatusCode);
        var text = await sel.Content.ReadAsStringAsync();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length); // meta + 2 行 + end
    }

    [Fact]
    public async Task PromWrite_MultipleTimeSeries_Returns204()
    {
        using var c = CreateClient(_readWriteToken);
        var bytes = ProtoEncoder.WriteRequest(
            ProtoEncoder.TimeSeries(
                labels: new (string, string)[] { ("__name__", "http_requests_total"), ("instance", "a"), ("job", "api") },
                samples: new (double, long)[] { (1.0d, 1000L) }),
            ProtoEncoder.TimeSeries(
                labels: new (string, string)[] { ("__name__", "process_cpu_seconds"), ("instance", "a") },
                samples: new (double, long)[] { (3.14d, 1500L) }));
        var resp = await c.PostAsync($"/api/v1/prom/write?db={_dbName}", SnappyProto(bytes));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task PromWrite_NaNSamples_AreSkipped_Returns204()
    {
        using var c = CreateClient(_readWriteToken);
        var bytes = ProtoEncoder.WriteRequest(
            ProtoEncoder.TimeSeries(
                labels: new (string, string)[] { ("__name__", "http_requests_total"), ("instance", "a"), ("job", "api") },
                samples: new (double, long)[] { (double.NaN, 5000L), (5.0d, 5500L) }));
        var resp = await c.PostAsync($"/api/v1/prom/write?db={_dbName}", SnappyProto(bytes));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // 仅一行落库：5500ms 的 5.0
        var sel = await c.PostAsync($"/v1/db/{_dbName}/sql",
            JsonContent.Create(
                new SqlRequest("SELECT value FROM http_requests_total WHERE instance='a' AND time >= 5000 AND time <= 5500"),
                ServerJsonContext.Default.SqlRequest));
        Assert.True(sel.IsSuccessStatusCode);
        var text = await sel.Content.ReadAsStringAsync();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // meta + 1 行 + end
    }

    [Fact]
    public async Task PromWrite_MissingMetricName_SeriesSkipped_Returns204()
    {
        using var c = CreateClient(_readWriteToken);
        // 没有 __name__ label：整个 series 被跳过，仍返回 204。
        var bytes = ProtoEncoder.WriteRequest(
            ProtoEncoder.TimeSeries(
                labels: new (string, string)[] { ("instance", "a") },
                samples: new (double, long)[] { (1.0d, 1000L) }));
        var resp = await c.PostAsync($"/api/v1/prom/write?db={_dbName}", SnappyProto(bytes));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task PromWrite_LabelWithReservedChar_SeriesSkipped_Returns204()
    {
        using var c = CreateClient(_readWriteToken);
        // tag value 含逗号 → 整个 series 被跳过，不报错。
        var bytes = ProtoEncoder.WriteRequest(
            ProtoEncoder.TimeSeries(
                labels: new (string, string)[] { ("__name__", "http_requests_total"), ("instance", "a,b") },
                samples: new (double, long)[] { (1.0d, 1000L) }));
        var resp = await c.PostAsync($"/api/v1/prom/write?db={_dbName}", SnappyProto(bytes));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task PromWrite_EmptyBody_Returns204()
    {
        using var c = CreateClient(_readWriteToken);
        var content = new ByteArrayContent(Array.Empty<byte>());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        content.Headers.ContentEncoding.Add("snappy");
        var resp = await c.PostAsync($"/api/v1/prom/write?db={_dbName}", content);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task PromWrite_BadSnappy_Returns400()
    {
        using var c = CreateClient(_readWriteToken);
        // 非 snappy 字节
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes("not snappy data!!"));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        var resp = await c.PostAsync($"/api/v1/prom/write?db={_dbName}", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PromWrite_BadProtobuf_Returns400()
    {
        using var c = CreateClient(_readWriteToken);
        // 合法 snappy，但解压后是垃圾 protobuf：tag=0x08（field=1, wireType=0 varint）后跟一串
        // 高位全为 1 的字节，varint 永远不结束 → 触发 PrometheusProtoException。
        var raw = new byte[] { 0x08, 0xFF, 0xFF, 0xFF };
        var resp = await c.PostAsync($"/api/v1/prom/write?db={_dbName}", SnappyProto(raw));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PromWrite_MissingDb_Returns400()
    {
        using var c = CreateClient(_readWriteToken);
        var bytes = ProtoEncoder.WriteRequest(
            ProtoEncoder.TimeSeries(
                labels: new (string, string)[] { ("__name__", "x") },
                samples: new (double, long)[] { (1.0d, 1L) }));
        var resp = await c.PostAsync("/api/v1/prom/write", SnappyProto(bytes));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PromWrite_UnknownDatabase_Returns404()
    {
        using var c = CreateClient(_adminToken);
        var bytes = ProtoEncoder.WriteRequest(
            ProtoEncoder.TimeSeries(
                labels: new (string, string)[] { ("__name__", "x") },
                samples: new (double, long)[] { (1.0d, 1L) }));
        var resp = await c.PostAsync("/api/v1/prom/write?db=nope", SnappyProto(bytes));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PromWrite_ReadOnly_Returns403()
    {
        using var c = CreateClient(_readOnlyToken);
        var bytes = ProtoEncoder.WriteRequest(
            ProtoEncoder.TimeSeries(
                labels: new (string, string)[] { ("__name__", "http_requests_total"), ("instance", "a"), ("job", "api") },
                samples: new (double, long)[] { (1.0d, 1L) }));
        var resp = await c.PostAsync($"/api/v1/prom/write?db={_dbName}", SnappyProto(bytes));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task PromWrite_NoToken_Returns401()
    {
        using var c = CreateClient(token: null);
        var bytes = ProtoEncoder.WriteRequest(
            ProtoEncoder.TimeSeries(
                labels: new (string, string)[] { ("__name__", "http_requests_total") },
                samples: new (double, long)[] { (1.0d, 1L) }));
        var resp = await c.PostAsync($"/api/v1/prom/write?db={_dbName}", SnappyProto(bytes));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task PromWrite_BadSnappyError_BodyIsJson()
    {
        using var c = CreateClient(_readWriteToken);
        var content = new ByteArrayContent(new byte[] { 0xFF, 0xFE, 0xFD });
        var resp = await c.PostAsync($"/api/v1/prom/write?db={_dbName}", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var s = await resp.Content.ReadAsStreamAsync();
        var err = await JsonSerializer.DeserializeAsync(s, ServerJsonContext.Default.ErrorResponse);
        Assert.NotNull(err);
        Assert.Equal("snappy_error", err!.Error);
    }

    /// <summary>
    /// 极简手写 protobuf encoder，仅供测试构造 Prometheus <c>WriteRequest</c> payload。
    /// 不引入 Google.Protobuf / Grpc.Tools 依赖，与生产代码同样只用 BCL。
    /// </summary>
    private static class ProtoEncoder
    {
        /// <summary>构造一个 <c>WriteRequest</c>（field 1 = repeated TimeSeries）的字节序列。</summary>
        public static byte[] WriteRequest(params byte[][] timeSeries)
        {
            using var ms = new MemoryStream();
            foreach (var ts in timeSeries)
            {
                WriteTag(ms, fieldNo: 1, wireType: 2);
                WriteVarint(ms, (ulong)ts.Length);
                ms.Write(ts, 0, ts.Length);
            }
            return ms.ToArray();
        }

        /// <summary>构造一条 <c>TimeSeries</c>（field 1 = repeated Label, field 2 = repeated Sample）。</summary>
        public static byte[] TimeSeries((string Name, string Value)[] labels, (double Value, long TimestampMs)[] samples)
        {
            using var ms = new MemoryStream();
            foreach (var (n, v) in labels)
            {
                var lab = Label(n, v);
                WriteTag(ms, fieldNo: 1, wireType: 2);
                WriteVarint(ms, (ulong)lab.Length);
                ms.Write(lab, 0, lab.Length);
            }
            foreach (var (v, t) in samples)
            {
                var s = Sample(v, t);
                WriteTag(ms, fieldNo: 2, wireType: 2);
                WriteVarint(ms, (ulong)s.Length);
                ms.Write(s, 0, s.Length);
            }
            return ms.ToArray();
        }

        private static byte[] Label(string name, string value)
        {
            using var ms = new MemoryStream();
            var nb = Encoding.UTF8.GetBytes(name);
            WriteTag(ms, fieldNo: 1, wireType: 2);
            WriteVarint(ms, (ulong)nb.Length);
            ms.Write(nb, 0, nb.Length);
            var vb = Encoding.UTF8.GetBytes(value);
            WriteTag(ms, fieldNo: 2, wireType: 2);
            WriteVarint(ms, (ulong)vb.Length);
            ms.Write(vb, 0, vb.Length);
            return ms.ToArray();
        }

        private static byte[] Sample(double value, long timestampMs)
        {
            using var ms = new MemoryStream();
            // value: field 1, wire type 1 (I64), little-endian double bits
            WriteTag(ms, fieldNo: 1, wireType: 1);
            Span<byte> buf = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buf, BitConverter.DoubleToInt64Bits(value));
            ms.Write(buf);
            // timestamp: field 2, wire type 0 (varint)
            WriteTag(ms, fieldNo: 2, wireType: 0);
            WriteVarint(ms, (ulong)timestampMs);
            return ms.ToArray();
        }

        private static void WriteTag(MemoryStream ms, int fieldNo, int wireType)
            => WriteVarint(ms, (ulong)((fieldNo << 3) | wireType));

        private static void WriteVarint(MemoryStream ms, ulong value)
        {
            while (value >= 0x80)
            {
                ms.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            ms.WriteByte((byte)value);
        }
    }
}
