using System.Buffers;
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
using SonnetDB.Ingest;
using SonnetDB.Json;
using SonnetDB.Model;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// M28 P5b #237 tsdb 列式批量写帧端点端到端测试：列式帧写入 → REST SQL 回查等价、
/// 稀疏列、flushMode、鉴权、错误隔离与批内混合 service。
/// </summary>
public sealed class TsdbFrameEndpointTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;
    private const string _adminToken = "admin-tsdbframe-token";
    private const string _readOnlyToken = "ro-tsdbframe-token";
    private const string _dbName = "tsdbframe";
    private const string FrameContentType = "application/x-sonnetdb-frame";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-tsdbframe-tests-" + Guid.NewGuid().ToString("N"));
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
        _baseUrl = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        using var admin = CreateClient();
        var create = await admin.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(_dbName), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
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

    private HttpClient CreateClient(string? token = _adminToken)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<List<(FrameHeader Header, byte[] Payload)>> PostFramesAsync(HttpClient client, byte[] body)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(FrameContentType);
        using var response = await client.PostAsync("/v1/frame", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        byte[] responseBody = await response.Content.ReadAsByteArrayAsync();

        var frames = new List<(FrameHeader, byte[])>();
        var buffer = new ReadOnlySequence<byte>(responseBody);
        while (FrameCodec.TryReadFrame(ref buffer, out FrameHeader header, out ReadOnlySequence<byte> payload))
            frames.Add((header, payload.ToArray()));
        Assert.Equal(0, buffer.Length);
        return frames;
    }

    private async Task<string[]> QuerySqlLinesAsync(HttpClient client, string sql)
    {
        var resp = await client.PostAsync($"/v1/db/{_dbName}/sql",
            JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        string text = await resp.Content.ReadAsStringAsync();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    // ────────────────────────────── 1. 列式写入 → REST SQL 回查等价 ──────────────────────────────

    [Fact]
    public async Task WriteColumnar_ThenSqlSelect_RowsVisible()
    {
        using var admin = CreateClient();
        long[] timestamps = [1000, 2000, 3000];
        var block = new TsdbColumnarBlock(
            new Dictionary<string, string> { ["host"] = "col-a" },
            timestamps,
            [
                TsdbColumnarColumn.Float64("value", new double[] { 1.5, 2.5, 3.5 }),
                TsdbColumnarColumn.Int64("count", new long[] { 10, 20, 30 }),
            ]);

        var writer = new ArrayBufferWriter<byte>();
        TsdbFrameCodec.EncodeWriteColumnarRequest(writer, 5, _dbName, "col_m1", BulkFlushMode.None, [block]);
        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());

        Assert.Single(frames);
        Assert.True(frames[0].Header.IsResponse);
        Assert.False(frames[0].Header.IsError);
        Assert.Equal((byte)FrameService.Tsdb, frames[0].Header.Service);
        Assert.Equal(5u, frames[0].Header.StreamId);
        Assert.Equal(3, TsdbFrameCodec.DecodeWriteColumnarResponse(frames[0].Payload));

        string[] lines = await QuerySqlLinesAsync(admin,
            "SELECT value, count FROM col_m1 WHERE host='col-a' AND time >= 1000 AND time <= 3000");
        // meta + 3 行 + end
        Assert.Equal(5, lines.Length);
    }

    [Fact]
    public async Task WriteColumnar_SparseAndMultiBlock_ThenSqlAggregate()
    {
        using var admin = CreateClient();
        // 块 1：host=s1，5 行，temp 只有 2 行
        bool[] presence = [true, false, false, true, false];
        var block1 = new TsdbColumnarBlock(
            new Dictionary<string, string> { ["host"] = "s1" },
            new long[] { 1000, 2000, 3000, 4000, 5000 },
            [
                TsdbColumnarColumn.Int64("value", new long[] { 1, 2, 3, 4, 5 }),
                TsdbColumnarColumn.Float64("temp", new double[] { 0.5, 4.5 }, presence),
            ]);
        // 块 2：host=s2，2 行
        var block2 = new TsdbColumnarBlock(
            new Dictionary<string, string> { ["host"] = "s2" },
            new long[] { 1500, 2500 },
            [TsdbColumnarColumn.Int64("value", new long[] { 100, 200 })]);

        var writer = new ArrayBufferWriter<byte>();
        TsdbFrameCodec.EncodeWriteColumnarRequest(writer, 1, _dbName, "col_sparse", BulkFlushMode.Sync, [block1, block2]);
        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());
        Assert.Equal(7, TsdbFrameCodec.DecodeWriteColumnarResponse(frames[0].Payload));

        string[] countAll = await QuerySqlLinesAsync(admin,
            "SELECT count(*) FROM col_sparse WHERE time >= 1000 AND time <= 5000");
        Assert.Contains("7", countAll[1]);

        // 稀疏列只落了 2 个点
        string[] countTemp = await QuerySqlLinesAsync(admin,
            "SELECT count(temp) FROM col_sparse WHERE time >= 1000 AND time <= 5000");
        Assert.Contains("2", countTemp[1]);
    }

    // ────────────────────────────── 2. 与既有 JSON bulk 端点等价 ──────────────────────────────

    [Fact]
    public async Task WriteColumnar_MatchesJsonBulkIngest_SameData()
    {
        using var admin = CreateClient();

        // JSON 路径
        var jsonBody = """
        {"points":[
          {"t":100,"tags":{"host":"eq"},"fields":{"value":1.25}},
          {"t":200,"tags":{"host":"eq"},"fields":{"value":2.25}}
        ]}
        """;
        var jsonResp = await admin.PostAsync($"/v1/db/{_dbName}/measurements/col_eq_json/json",
            new StringContent(jsonBody, Encoding.UTF8, "application/json"));
        Assert.True(jsonResp.IsSuccessStatusCode);

        // 列式帧路径（同数据不同 measurement）
        var block = new TsdbColumnarBlock(
            new Dictionary<string, string> { ["host"] = "eq" },
            new long[] { 100, 200 },
            [TsdbColumnarColumn.Float64("value", new double[] { 1.25, 2.25 })]);
        var writer = new ArrayBufferWriter<byte>();
        TsdbFrameCodec.EncodeWriteColumnarRequest(writer, 1, _dbName, "col_eq_frame", BulkFlushMode.None, [block]);
        await PostFramesAsync(admin, writer.WrittenMemory.ToArray());

        string[] jsonRows = await QuerySqlLinesAsync(admin,
            "SELECT value FROM col_eq_json WHERE time >= 100 AND time <= 200");
        string[] frameRows = await QuerySqlLinesAsync(admin,
            "SELECT value FROM col_eq_frame WHERE time >= 100 AND time <= 200");
        Assert.Equal(jsonRows.Length, frameRows.Length);
        // 首行 meta 与末行 end 含耗时字段，只比较中间数据行
        for (int i = 1; i < jsonRows.Length - 1; i++)
            Assert.Equal(jsonRows[i], frameRows[i]);
    }

    // ────────────────────────────── 3. 鉴权 ──────────────────────────────

    [Fact]
    public async Task WriteColumnar_ReadOnlyToken_ForbiddenErrorFrame()
    {
        using var readOnly = CreateClient(_readOnlyToken);
        var block = new TsdbColumnarBlock(
            null, new long[] { 1 },
            [TsdbColumnarColumn.Float64("v", new double[] { 1.0 })]);
        var writer = new ArrayBufferWriter<byte>();
        TsdbFrameCodec.EncodeWriteColumnarRequest(writer, 8, _dbName, "col_auth", BulkFlushMode.None, [block]);

        var frames = await PostFramesAsync(readOnly, writer.WrittenMemory.ToArray());
        Assert.Single(frames);
        Assert.True(frames[0].Header.IsError);
        Assert.Equal(8u, frames[0].Header.StreamId);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[0].Payload);
        Assert.Equal("forbidden", code);
    }

    [Fact]
    public async Task WriteColumnar_UnknownDb_DbNotFoundErrorFrame()
    {
        using var admin = CreateClient();
        var block = new TsdbColumnarBlock(
            null, new long[] { 1 },
            [TsdbColumnarColumn.Float64("v", new double[] { 1.0 })]);
        var writer = new ArrayBufferWriter<byte>();
        TsdbFrameCodec.EncodeWriteColumnarRequest(writer, 2, "no-such-db", "m", BulkFlushMode.None, [block]);

        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());
        Assert.True(frames[0].Header.IsError);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[0].Payload);
        Assert.Equal("db_not_found", code);
    }

    // ────────────────────────────── 4. 错误隔离 + 混合 service ──────────────────────────────

    [Fact]
    public async Task MixedBatch_MqAndTsdbAndBadFrame_IsolatedPerFrame()
    {
        using var admin = CreateClient();

        var writer = new ArrayBufferWriter<byte>();
        // 帧 1：MQ publish（成功）
        MqFrameCodec.EncodePublishRequest(writer, 1, _dbName, "mixt", null, [0xAB]);
        // 帧 2：tsdb 列式写（成功）
        var block = new TsdbColumnarBlock(
            new Dictionary<string, string> { ["host"] = "mix" },
            new long[] { 42 },
            [TsdbColumnarColumn.Float64("v", new double[] { 9.5 })]);
        TsdbFrameCodec.EncodeWriteColumnarRequest(writer, 2, _dbName, "col_mix", BulkFlushMode.None, [block]);
        // 帧 3：tsdb 不支持的 op（错误帧，不影响前两帧）
        var badHeader = new FrameHeader(0, FrameHeader.CurrentVersion, (byte)FrameService.Tsdb, 99, 0, 3);
        Span<byte> headerBytes = stackalloc byte[FrameHeader.Size];
        badHeader.Write(headerBytes);
        writer.Write(headerBytes);

        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());
        Assert.Equal(3, frames.Count);
        Assert.False(frames[0].Header.IsError);
        Assert.False(frames[1].Header.IsError);
        Assert.Equal(1, TsdbFrameCodec.DecodeWriteColumnarResponse(frames[1].Payload));
        Assert.True(frames[2].Header.IsError);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[2].Payload);
        Assert.Equal("unsupported_op", code);

        // 帧 2 的数据真实落库
        string[] rows = await QuerySqlLinesAsync(admin,
            "SELECT v FROM col_mix WHERE time >= 42 AND time <= 42");
        Assert.Equal(3, rows.Length);
    }

    [Fact]
    public async Task WriteColumnar_TypeMismatchOnExistingSchema_BulkIngestErrorFrame()
    {
        using var admin = CreateClient();

        // 先以 Float64 建 schema
        var f64 = new TsdbColumnarBlock(
            null, new long[] { 1 },
            [TsdbColumnarColumn.Float64("v", new double[] { 1.0 })]);
        var writer = new ArrayBufferWriter<byte>();
        TsdbFrameCodec.EncodeWriteColumnarRequest(writer, 1, _dbName, "col_schema", BulkFlushMode.None, [f64]);
        var ok = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());
        Assert.False(ok[0].Header.IsError);

        // 同字段改发 String → 引擎 schema 校验拒绝 → bulk_ingest_error 错误帧
        var str = new TsdbColumnarBlock(
            null, new long[] { 2 },
            [TsdbColumnarColumn.String("v", ["oops"])]);
        writer = new ArrayBufferWriter<byte>();
        TsdbFrameCodec.EncodeWriteColumnarRequest(writer, 2, _dbName, "col_schema", BulkFlushMode.None, [str]);
        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());
        Assert.True(frames[0].Header.IsError);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[0].Payload);
        Assert.Equal("bulk_ingest_error", code);
    }
}
