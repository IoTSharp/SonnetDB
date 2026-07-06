using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Json;
using SonnetDB.Protocol;
using SonnetDB.Query;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// M28 P5b #239 vector 检索帧端点端到端测试：f32 二进制查询向量 → meta/rows/end 帧序列
/// （sql 块布局解码器互通）、与 SQL knn TVF 结果等价、tag 过滤/时间窗/metric、
/// 维度不匹配与非向量列错误帧、鉴权与批内混合 service。
/// </summary>
public sealed class VectorFrameEndpointTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;
    private const string _adminToken = "admin-vecframe-token";
    private const string _readOnlyToken = "ro-vecframe-token";
    private const string _dbName = "vecframe";
    private const string FrameContentType = "application/x-sonnetdb-frame";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-vecframe-tests-" + Guid.NewGuid().ToString("N"));
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

        // 建向量 measurement 并插入 4 条向量（与 SqlExecutorKnnTests 同形状）
        await ExecRestSqlAsync(admin, "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
        await ExecRestSqlAsync(admin, "INSERT INTO docs (source, embedding, time) VALUES " +
            "('a', [1, 0, 0], 1000), " +
            "('b', [1, 1, 0], 2000), " +
            "('c', [0, 1, 0], 3000), " +
            "('d', [-1, 0, 0], 4000)");
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
    /// 执行一次帧 vector search 并解码完整响应流（meta → rows × N → end，sql 块布局解码器互通）。
    /// </summary>
    private async Task<(string[] Columns, List<object?[]> Rows)> SearchFrameAsync(
        HttpClient client,
        float[] query,
        int k,
        KnnMetric metric = KnnMetric.Cosine,
        IReadOnlyDictionary<string, string>? tagFilter = null,
        TimeRange? timeRange = null,
        uint streamId = 1)
    {
        var writer = new ArrayBufferWriter<byte>();
        VectorFrameCodec.EncodeSearchRequest(writer, streamId, _dbName, "docs", "embedding",
            query, k, metric, tagFilter, timeRange);
        var frames = await PostFramesAsync(client, writer.WrittenMemory.ToArray());
        return DecodeSearchFrames(frames, streamId);
    }

    private static (string[] Columns, List<object?[]> Rows) DecodeSearchFrames(
        List<(FrameHeader Header, byte[] Payload)> frames, uint streamId)
    {
        Assert.True(frames.Count >= 2, $"响应至少应含 meta + end 两帧，实际 {frames.Count}。");
        foreach (var frame in frames)
        {
            Assert.Equal((byte)FrameService.Vector, frame.Header.Service);
            Assert.Equal((byte)VectorFrameOp.Search, frame.Header.Op);
            Assert.Equal(streamId, frame.Header.StreamId);
            Assert.True(frame.Header.IsResponse);
            Assert.False(frame.Header.IsError, frame.Header.IsError ? FrameCodec.ReadErrorPayload(frame.Payload).Message : "");
        }

        Assert.Equal(SqlQueryChunkKind.Meta, SqlFrameCodec.PeekChunkKind(frames[0].Payload));
        string[] columns = SqlFrameCodec.DecodeQueryMetaFrame(frames[0].Payload);

        var rows = new List<object?[]>();
        for (int i = 1; i < frames.Count - 1; i++)
        {
            Assert.Equal(SqlQueryChunkKind.Rows, SqlFrameCodec.PeekChunkKind(frames[i].Payload));
            rows.AddRange(SqlFrameCodec.DecodeQueryRowsFrame(frames[i].Payload));
        }

        Assert.Equal(SqlQueryChunkKind.End, SqlFrameCodec.PeekChunkKind(frames[^1].Payload));
        (long rowCount, double elapsed) = SqlFrameCodec.DecodeQueryEndFrame(frames[^1].Payload);
        Assert.True(elapsed >= 0);
        Assert.Equal(rows.Count, rowCount);
        return (columns, rows);
    }

    private async Task ExecRestSqlAsync(HttpClient client, string sql)
    {
        var resp = await client.PostAsync($"/v1/db/{_dbName}/sql",
            JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));
        string text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, text);
        Assert.DoesNotContain("\"error\"", text);
    }

    // ────────────────────────────── 1. 基础检索 + 排序 ──────────────────────────────

    [Fact]
    public async Task Search_ReturnsTopKByDistance_Cosine()
    {
        using var admin = CreateClient();
        (string[] columns, List<object?[]> rows) = await SearchFrameAsync(admin, [1f, 0f, 0f], 2);

        Assert.Equal(new[] { "time", "distance", "source", "embedding" }, columns);
        Assert.Equal(2, rows.Count);

        Assert.Equal(1000L, rows[0][0]);
        Assert.Equal(0.0, Assert.IsType<double>(rows[0][1]), 6);
        Assert.Equal("a", rows[0][2]);
        Assert.Equal(new float[] { 1f, 0f, 0f }, rows[0][3]);

        Assert.Equal(2000L, rows[1][0]);
        Assert.True((double)rows[1][1]! > 0.0);
        Assert.Equal("b", rows[1][2]);
    }

    // ────────────────────────────── 2. 与 SQL knn TVF 帧路径等价 ──────────────────────────────

    [Fact]
    public async Task Search_EquivalentToSqlKnnTvf()
    {
        using var admin = CreateClient();
        (string[] vColumns, List<object?[]> vRows) = await SearchFrameAsync(admin, [1f, 0f, 0f], 4);

        // 同一查询走 sql service 帧的 knn TVF
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRequest(writer, 9, _dbName, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 4)");
        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());
        Assert.All(frames, f => Assert.Equal((byte)FrameService.Sql, f.Header.Service));
        string[] sColumns = SqlFrameCodec.DecodeQueryMetaFrame(frames[0].Payload);
        var sRows = new List<object?[]>();
        for (int i = 1; i < frames.Count - 1; i++)
            sRows.AddRange(SqlFrameCodec.DecodeQueryRowsFrame(frames[i].Payload));

        Assert.Equal(sColumns, vColumns);
        Assert.Equal(sRows.Count, vRows.Count);
        for (int r = 0; r < sRows.Count; r++)
        {
            Assert.Equal(sRows[r][0], vRows[r][0]);
            Assert.Equal((double)sRows[r][1]!, (double)vRows[r][1]!, 9);
            Assert.Equal(sRows[r][2], vRows[r][2]);
            Assert.Equal(sRows[r][3], vRows[r][3]);
        }
    }

    // ────────────────────────────── 3. metric / tag 过滤 / 时间窗 ──────────────────────────────

    [Fact]
    public async Task Search_L2Metric_OrdersByEuclidean()
    {
        using var admin = CreateClient();
        (_, List<object?[]> rows) = await SearchFrameAsync(admin, [1f, 0f, 0f], 4, KnnMetric.L2);

        Assert.Equal(4, rows.Count);
        // L2: a=0, b=1, c=√2, d=2
        Assert.Equal("a", rows[0][2]);
        Assert.Equal("b", rows[1][2]);
        Assert.Equal("c", rows[2][2]);
        Assert.Equal("d", rows[3][2]);
    }

    [Fact]
    public async Task Search_TagFilter_RestrictsSeries()
    {
        using var admin = CreateClient();
        (_, List<object?[]> rows) = await SearchFrameAsync(admin, [1f, 0f, 0f], 4,
            tagFilter: new Dictionary<string, string> { ["source"] = "c" });

        Assert.Single(rows);
        Assert.Equal("c", rows[0][2]);
    }

    [Fact]
    public async Task Search_TimeRange_RestrictsWindow()
    {
        using var admin = CreateClient();
        (_, List<object?[]> rows) = await SearchFrameAsync(admin, [1f, 0f, 0f], 4,
            timeRange: new TimeRange(2000, 3000));

        Assert.Equal(2, rows.Count);
        Assert.Equal("b", rows[0][2]);
        Assert.Equal("c", rows[1][2]);
    }

    // ────────────────────────────── 4. 错误帧 ──────────────────────────────

    [Fact]
    public async Task Search_DimensionMismatch_ErrorFrame()
    {
        using var admin = CreateClient();
        var writer = new ArrayBufferWriter<byte>();
        VectorFrameCodec.EncodeSearchRequest(writer, 3, _dbName, "docs", "embedding", [1f, 0f], 2);
        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());

        Assert.Single(frames);
        Assert.True(frames[0].Header.IsError);
        Assert.Equal(3u, frames[0].Header.StreamId);
        (string code, string message) = FrameCodec.ReadErrorPayload(frames[0].Payload);
        Assert.Equal("vector_search_error", code);
        Assert.Contains("维度", message);
    }

    [Fact]
    public async Task Search_NonVectorColumn_ErrorFrame()
    {
        using var admin = CreateClient();
        var writer = new ArrayBufferWriter<byte>();
        VectorFrameCodec.EncodeSearchRequest(writer, 4, _dbName, "docs", "source", [1f, 0f, 0f], 2);
        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());

        Assert.Single(frames);
        Assert.True(frames[0].Header.IsError);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[0].Payload);
        Assert.Equal("vector_search_error", code);
    }

    [Fact]
    public async Task Search_NonTagFilterColumn_ErrorFrame()
    {
        using var admin = CreateClient();
        var writer = new ArrayBufferWriter<byte>();
        VectorFrameCodec.EncodeSearchRequest(writer, 5, _dbName, "docs", "embedding", [1f, 0f, 0f], 2,
            tagFilter: new Dictionary<string, string> { ["embedding"] = "x" });
        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());

        Assert.Single(frames);
        Assert.True(frames[0].Header.IsError);
        (string code, string message) = FrameCodec.ReadErrorPayload(frames[0].Payload);
        Assert.Equal("vector_search_error", code);
        Assert.Contains("TAG", message);
    }

    [Fact]
    public async Task Search_DbNotFound_ErrorFrame()
    {
        using var admin = CreateClient();
        var writer = new ArrayBufferWriter<byte>();
        VectorFrameCodec.EncodeSearchRequest(writer, 6, "no-such-db", "docs", "embedding", [1f, 0f, 0f], 2);
        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());

        Assert.Single(frames);
        Assert.True(frames[0].Header.IsError);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[0].Payload);
        Assert.Equal("db_not_found", code);
    }

    // ────────────────────────────── 5. 鉴权 ──────────────────────────────

    [Fact]
    public async Task Search_ReadOnlyToken_Succeeds()
    {
        using var readOnly = CreateClient(_readOnlyToken);
        (_, List<object?[]> rows) = await SearchFrameAsync(readOnly, [1f, 0f, 0f], 1);
        Assert.Single(rows);
        Assert.Equal("a", rows[0][2]);
    }

    // ────────────────────────────── 6. 批内混合 service ──────────────────────────────

    [Fact]
    public async Task MixedBatch_VectorAndSqlAndBadFrame_Isolated()
    {
        using var admin = CreateClient();
        var writer = new ArrayBufferWriter<byte>();
        VectorFrameCodec.EncodeSearchRequest(writer, 11, _dbName, "docs", "embedding", [1f, 0f, 0f], 1);
        // 畸形 vector 帧：维度声明超出帧体
        var badHeader = new FrameHeader(3, FrameHeader.CurrentVersion,
            (byte)FrameService.Vector, (byte)VectorFrameOp.Search, 0, 12);
        Span<byte> headerBytes = stackalloc byte[FrameHeader.Size];
        badHeader.Write(headerBytes);
        writer.Write(headerBytes);
        writer.Write((ReadOnlySpan<byte>)[1, (byte)'d', 0]);
        SqlFrameCodec.EncodeQueryRequest(writer, 13, _dbName, "SELECT count(*) AS cnt FROM docs");

        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());

        // 帧 1（vector search，streamId=11）：meta + rows + end
        var vectorFrames = frames.Where(f => f.Header.StreamId == 11).ToList();
        (_, List<object?[]> rows) = DecodeSearchFrames(vectorFrames, 11);
        Assert.Single(rows);
        Assert.Equal("a", rows[0][2]);

        // 帧 2（畸形，streamId=12）：bad_frame 错误帧
        var badFrames = frames.Where(f => f.Header.StreamId == 12).ToList();
        var errorFrame = Assert.Single(badFrames);
        Assert.True(errorFrame.Header.IsError);
        (string code, _) = FrameCodec.ReadErrorPayload(errorFrame.Payload);
        Assert.Equal("bad_frame", code);

        // 帧 3（sql，streamId=13）：正常执行
        var sqlFrames = frames.Where(f => f.Header.StreamId == 13).ToList();
        Assert.True(sqlFrames.Count >= 2);
        Assert.All(sqlFrames, f => Assert.False(f.Header.IsError));
        object?[][] sqlRows = SqlFrameCodec.DecodeQueryRowsFrame(sqlFrames[1].Payload);
        Assert.Equal(4L, sqlRows[0][0]);
    }
}
