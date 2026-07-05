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
using SonnetDB.Json;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// M28 P5b #238 sql 流式结果集帧端点端到端测试：帧查询 → meta/rows/end 帧序列解码、
/// 与 REST NDJSON 结果等价、大结果集多 rows 帧、NULL 位图、参数化查询、
/// 只读语句门禁（写语句 / 控制面 SQL 拒绝）、鉴权与批内混合 service。
/// </summary>
public sealed class SqlFrameEndpointTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;
    private const string _adminToken = "admin-sqlframe-token";
    private const string _readOnlyToken = "ro-sqlframe-token";
    private const string _dbName = "sqlframe";
    private const string FrameContentType = "application/x-sonnetdb-frame";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-sqlframe-tests-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// 执行一条帧 SQL 查询并解码完整响应流：meta → rows × N → end。
    /// </summary>
    private async Task<(string[] Columns, List<object?[]> Rows, long RowCount, int RowsFrameCount)> QueryFrameAsync(
        HttpClient client, string sql, uint streamId = 1, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRequest(writer, streamId, _dbName, sql, parameters);
        var frames = await PostFramesAsync(client, writer.WrittenMemory.ToArray());
        return DecodeQueryFrames(frames, streamId);
    }

    private static (string[] Columns, List<object?[]> Rows, long RowCount, int RowsFrameCount) DecodeQueryFrames(
        List<(FrameHeader Header, byte[] Payload)> frames, uint streamId)
    {
        Assert.True(frames.Count >= 2, $"响应至少应含 meta + end 两帧，实际 {frames.Count}。");
        foreach (var frame in frames)
        {
            Assert.Equal((byte)FrameService.Sql, frame.Header.Service);
            Assert.Equal(streamId, frame.Header.StreamId);
            Assert.True(frame.Header.IsResponse);
            Assert.False(frame.Header.IsError, frame.Header.IsError ? FrameCodec.ReadErrorPayload(frame.Payload).Message : "");
        }

        Assert.Equal(SqlQueryChunkKind.Meta, SqlFrameCodec.PeekChunkKind(frames[0].Payload));
        string[] columns = SqlFrameCodec.DecodeQueryMetaFrame(frames[0].Payload);

        var rows = new List<object?[]>();
        int rowsFrameCount = 0;
        for (int i = 1; i < frames.Count - 1; i++)
        {
            Assert.Equal(SqlQueryChunkKind.Rows, SqlFrameCodec.PeekChunkKind(frames[i].Payload));
            rows.AddRange(SqlFrameCodec.DecodeQueryRowsFrame(frames[i].Payload));
            rowsFrameCount++;
        }

        Assert.Equal(SqlQueryChunkKind.End, SqlFrameCodec.PeekChunkKind(frames[^1].Payload));
        (long rowCount, double elapsed) = SqlFrameCodec.DecodeQueryEndFrame(frames[^1].Payload);
        Assert.True(elapsed >= 0);
        Assert.Equal(rows.Count, rowCount);
        return (columns, rows, rowCount, rowsFrameCount);
    }

    private async Task IngestJsonAsync(HttpClient client, string measurement, string jsonBody)
    {
        var resp = await client.PostAsync($"/v1/db/{_dbName}/measurements/{measurement}/json",
            new StringContent(jsonBody, Encoding.UTF8, "application/json"));
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
    }

    private async Task ExecRestSqlAsync(HttpClient client, string sql)
    {
        var resp = await client.PostAsync($"/v1/db/{_dbName}/sql",
            JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));
        string text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, text);
        Assert.DoesNotContain("\"error\"", text);
    }

    // ────────────────────────────── 1. 时序查询 + 跨协议等价 ──────────────────────────────

    [Fact]
    public async Task Query_Measurement_MatchesRestNdjson()
    {
        using var admin = CreateClient();
        await IngestJsonAsync(admin, "sf_cpu", """
        {"points":[
          {"t":1000,"tags":{"host":"a"},"fields":{"value":1.5,"cnt":10}},
          {"t":2000,"tags":{"host":"a"},"fields":{"value":2.5,"cnt":20}},
          {"t":3000,"tags":{"host":"a"},"fields":{"value":3.5,"cnt":30}}
        ]}
        """);

        const string sql = "SELECT time, host, value, cnt FROM sf_cpu WHERE time >= 1000 AND time <= 3000";
        (string[] columns, List<object?[]> rows, long rowCount, _) = await QueryFrameAsync(admin, sql, streamId: 7);

        Assert.Equal(["time", "host", "value", "cnt"], columns);
        Assert.Equal(3, rowCount);
        Assert.Equal(1000L, rows[0][0]);
        Assert.Equal("a", rows[0][1]);
        Assert.Equal(1.5, rows[0][2]);
        Assert.Equal(10L, rows[0][3]);
        Assert.Equal(3000L, rows[2][0]);
        Assert.Equal(3.5, rows[2][2]);

        // REST NDJSON 同查询逐行等价
        var resp = await admin.PostAsync($"/v1/db/{_dbName}/sql",
            JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));
        string[] lines = (await resp.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length); // meta + 3 行 + end
        for (int i = 0; i < 3; i++)
        {
            using var doc = JsonDocument.Parse(lines[i + 1]);
            JsonElement row = doc.RootElement;
            Assert.Equal((long)rows[i][0]!, row[0].GetInt64());
            Assert.Equal((string)rows[i][1]!, row[1].GetString());
            Assert.Equal((double)rows[i][2]!, row[2].GetDouble());
            Assert.Equal((long)rows[i][3]!, row[3].GetInt64());
        }
    }

    // ────────────────────────────── 2. 大结果集多 rows 帧 ──────────────────────────────

    [Fact]
    public async Task Query_LargeResult_StreamsMultipleRowsFrames()
    {
        using var admin = CreateClient();
        // 8192 行 > DefaultMaxChunkRows(4096) → 至少 2 个 rows 帧
        var sb = new StringBuilder("{\"points\":[");
        for (int i = 0; i < 8192; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"{{\"t\":{i + 1},\"tags\":{{\"host\":\"big\"}},\"fields\":{{\"value\":{i}.5}}}}");
        }
        sb.Append("]}");
        await IngestJsonAsync(admin, "sf_big", sb.ToString());

        (string[] columns, List<object?[]> rows, long rowCount, int rowsFrameCount) = await QueryFrameAsync(
            admin, "SELECT time, value FROM sf_big WHERE time >= 1 AND time <= 8192");

        Assert.Equal(["time", "value"], columns);
        Assert.Equal(8192, rowCount);
        Assert.True(rowsFrameCount >= 2, $"应至少 2 个 rows 帧，实际 {rowsFrameCount}。");
        Assert.Equal(1L, rows[0][0]);
        Assert.Equal(8192L, rows[^1][0]);
        Assert.Equal(0.5, rows[0][1]);
        Assert.Equal(8191.5, rows[^1][1]);
    }

    // ────────────────────────────── 3. 关系表 + NULL 位图 + 空结果集 ──────────────────────────────

    [Fact]
    public async Task Query_RelationalTableWithNulls_RoundTrips()
    {
        using var admin = CreateClient();
        await ExecRestSqlAsync(admin, "CREATE TABLE sf_users (id INT, name STRING, age INT, PRIMARY KEY (id))");
        await ExecRestSqlAsync(admin, "INSERT INTO sf_users (id, name, age) VALUES (1, 'alice', 30)");
        await ExecRestSqlAsync(admin, "INSERT INTO sf_users (id, name) VALUES (2, 'bob')");
        await ExecRestSqlAsync(admin, "INSERT INTO sf_users (id, age) VALUES (3, 25)");

        (string[] columns, List<object?[]> rows, long rowCount, _) = await QueryFrameAsync(
            admin, "SELECT id, name, age FROM sf_users ORDER BY id");

        Assert.Equal(["id", "name", "age"], columns);
        Assert.Equal(3, rowCount);
        Assert.Equal([1L, "alice", 30L], rows[0]);
        Assert.Equal([2L, "bob", null], rows[1]);
        Assert.Equal([3L, null, 25L], rows[2]);
    }

    [Fact]
    public async Task Query_EmptyResult_MetaAndEndOnly()
    {
        using var admin = CreateClient();
        await ExecRestSqlAsync(admin, "CREATE TABLE sf_empty (id INT, v STRING, PRIMARY KEY (id))");

        (string[] columns, List<object?[]> rows, long rowCount, int rowsFrameCount) = await QueryFrameAsync(
            admin, "SELECT id, v FROM sf_empty");

        Assert.Equal(["id", "v"], columns);
        Assert.Equal(0, rowCount);
        Assert.Empty(rows);
        Assert.Equal(0, rowsFrameCount);
    }

    // ────────────────────────────── 4. 参数化查询 ──────────────────────────────

    [Fact]
    public async Task Query_NamedParameters_Bound()
    {
        using var admin = CreateClient();
        await ExecRestSqlAsync(admin, "CREATE TABLE sf_param (id INT, name STRING, score FLOAT, PRIMARY KEY (id))");
        await ExecRestSqlAsync(admin, "INSERT INTO sf_param (id, name, score) VALUES (1, 'x', 0.5)");
        await ExecRestSqlAsync(admin, "INSERT INTO sf_param (id, name, score) VALUES (2, 'y', 1.5)");
        await ExecRestSqlAsync(admin, "INSERT INTO sf_param (id, name, score) VALUES (3, 'y', 2.5)");

        (_, List<object?[]> rows, long rowCount, _) = await QueryFrameAsync(
            admin, "SELECT id FROM sf_param WHERE name = @n AND score > @s ORDER BY id",
            parameters: new Dictionary<string, object?> { ["n"] = "y", ["s"] = 2.0 });

        Assert.Equal(1, rowCount);
        Assert.Equal(3L, rows[0][0]);
    }

    // ────────────────────────────── 5. 只读门禁与错误 ──────────────────────────────

    [Fact]
    public async Task Query_ReadOnlyToken_SelectSucceeds()
    {
        using var admin = CreateClient();
        using var ro = CreateClient(_readOnlyToken);
        await IngestJsonAsync(admin, "sf_ro", """{"points":[{"t":1,"tags":{},"fields":{"v":1.0}}]}""");

        (_, _, long rowCount, _) = await QueryFrameAsync(ro, "SELECT time, v FROM sf_ro WHERE time >= 1 AND time <= 1");
        Assert.Equal(1, rowCount);
    }

    [Fact]
    public async Task Query_WriteStatement_RejectedBadRequest()
    {
        using var admin = CreateClient();
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRequest(writer, 4, _dbName, "CREATE TABLE sf_reject (id INT, PRIMARY KEY (id))");
        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());

        Assert.Single(frames);
        Assert.True(frames[0].Header.IsError);
        Assert.Equal(4u, frames[0].Header.StreamId);
        (string code, string message) = FrameCodec.ReadErrorPayload(frames[0].Payload);
        Assert.Equal("bad_request", code);
        Assert.Contains("只读", message);
    }

    [Fact]
    public async Task Query_ControlPlaneStatement_RejectedBadRequest()
    {
        using var admin = CreateClient();
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRequest(writer, 5, _dbName, "SHOW USERS");
        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());

        Assert.True(frames[0].Header.IsError);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[0].Payload);
        Assert.Equal("bad_request", code);
    }

    [Fact]
    public async Task Query_ParseError_SqlErrorFrame()
    {
        using var admin = CreateClient();
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRequest(writer, 6, _dbName, "SELEC bogus FROM nowhere");
        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());

        Assert.True(frames[0].Header.IsError);
        Assert.Equal(6u, frames[0].Header.StreamId);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[0].Payload);
        Assert.Equal("sql_error", code);
    }

    [Fact]
    public async Task Query_UnknownDb_DbNotFoundErrorFrame()
    {
        using var admin = CreateClient();
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRequest(writer, 9, "no-such-db", "SELECT 1 FROM t");
        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());

        Assert.True(frames[0].Header.IsError);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[0].Payload);
        Assert.Equal("db_not_found", code);
    }

    // ────────────────────────────── 6. 批内混合 service ──────────────────────────────

    [Fact]
    public async Task MixedBatch_MqAndSqlQuery_IsolatedPerFrame()
    {
        using var admin = CreateClient();
        await IngestJsonAsync(admin, "sf_mix", """{"points":[{"t":10,"tags":{},"fields":{"v":7.5}}]}""");

        var writer = new ArrayBufferWriter<byte>();
        // 帧 1：MQ publish（单响应帧）
        MqFrameCodec.EncodePublishRequest(writer, 1, _dbName, "sfmixt", null, [0xCD]);
        // 帧 2：SQL 查询（meta + rows + end 三帧）
        SqlFrameCodec.EncodeQueryRequest(writer, 2, _dbName, "SELECT time, v FROM sf_mix WHERE time >= 10 AND time <= 10");
        // 帧 3：SQL 解析错误（单错误帧，不影响前两帧）
        SqlFrameCodec.EncodeQueryRequest(writer, 3, _dbName, "NOT A QUERY");

        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());
        Assert.Equal(5, frames.Count);

        // 帧 1 响应
        Assert.Equal(1u, frames[0].Header.StreamId);
        Assert.False(frames[0].Header.IsError);
        Assert.Equal(0, MqFrameCodec.DecodePublishResponse(frames[0].Payload));

        // 帧 2 响应：meta + rows + end，全部 streamId=2
        var sqlFrames = frames.GetRange(1, 3);
        (string[] columns, List<object?[]> rows, long rowCount, _) = DecodeQueryFrames(sqlFrames, 2);
        Assert.Equal(["time", "v"], columns);
        Assert.Equal(1, rowCount);
        Assert.Equal(10L, rows[0][0]);
        Assert.Equal(7.5, rows[0][1]);

        // 帧 3 错误帧
        Assert.Equal(3u, frames[4].Header.StreamId);
        Assert.True(frames[4].Header.IsError);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[4].Payload);
        Assert.Equal("sql_error", code);
    }
}
