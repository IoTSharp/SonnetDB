using System.Buffers;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Data;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>通过真实 Kestrel 验证显式 SQL 预览的行数、字节和传输兼容合同。</summary>
public sealed class SqlExecutionPreviewEndpointTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(), "sndb-sql-preview-endpoint-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private HttpClient? _client;

    /// <summary>启动隔离服务并插入多于预览上限的关系行。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            await InitializeCoreAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private async Task InitializeCoreAsync()
    {
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = _dataRoot,
            SqlExecution = new SqlExecutionResourceOptions
            {
                MaxResultRows = 3,
                MaxPreviewBytes = 200,
            },
            Tokens = new Dictionary<string, string> { ["preview-admin"] = ServerRoles.Admin },
        });
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await _app.StartAsync(startup.Token);
        string address = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "preview-admin");

        using var create = await _client.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest("preview"), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        await ExecuteAsync("CREATE TABLE items (id INT, category INT, payload STRING, PRIMARY KEY (id))");
        string payload = new('x', 100);
        string values = string.Join(", ", Enumerable.Range(1, 8)
            .Select(id => $"({id}, {id % 2}, '{payload}')"));
        await ExecuteAsync($"INSERT INTO items (id, category, payload) VALUES {values}");
    }

    /// <summary>关闭服务并删除本测试创建的数据库目录。</summary>
    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        WebApplication? app = _app;
        _app = null;
        try
        {
            if (app is not null)
            {
                try
                {
                    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await app.StopAsync(shutdown.Token);
                }
                finally
                {
                    await app.DisposeAsync();
                }
            }
        }
        finally
        {
            string target = Path.GetFullPath(_dataRoot);
            string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(target), tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("sndb-sql-preview-endpoint-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("端点测试数据库清理路径不在本测试的临时目录范围内。");
            }
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
    }

    /// <summary>关系表预览只返回前 N 行，恰好 N 行的 SQL 结果保持完整标记。</summary>
    [Fact]
    public async Task RestPreview_OverflowAndExactLimit_ReportCorrectEndMarker()
    {
        AssertIdRows(await ExecuteAsync("SELECT id FROM items", previewMaxRows: 3),
            truncated: true, 1, 2, 3);
        AssertIdRows(await ExecuteAsync("SELECT id FROM items LIMIT 3", previewMaxRows: 3),
            truncated: false, 1, 2, 3);
    }

    /// <summary>WHERE 与 SQL OFFSET/LIMIT 先于预览限制，返回的是完整查询的前缀。</summary>
    [Fact]
    public async Task RestPreview_WithWhereOffsetAndLimit_KeepsQueryPrefix()
    {
        AssertIdRows(await ExecuteAsync(
                "SELECT id FROM items WHERE category = 1 LIMIT 3 OFFSET 1", previewMaxRows: 2),
            truncated: true, 3, 5);
        AssertIdRows(await ExecuteAsync(
                "SELECT id FROM items WHERE category = 1 LIMIT 2 OFFSET 1", previewMaxRows: 2),
            truncated: false, 3, 5);
    }

    /// <summary>IN 访问计划的输出顺序由原执行器决定，预览必须保留该顺序的前缀。</summary>
    [Fact]
    public async Task RestPreview_WithPrimaryKeyIn_PreservesCompleteQueryPrefix()
    {
        const string sql = "SELECT id FROM items WHERE id IN (7, 2, 5)";
        string complete = await ExecuteAsync(sql);
        string preview = await ExecuteAsync(sql, previewMaxRows: 2);

        long[] allIds = ReadIds(complete);
        long[] previewIds = ReadIds(preview);
        Assert.Equal(3, allIds.Length);
        Assert.Equal(allIds.Take(2), previewIds);
        using var end = JsonDocument.Parse(preview.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
        Assert.Equal(2, end.RootElement.GetProperty("rowCount").GetInt64());
        Assert.True(end.RootElement.GetProperty("truncated").GetBoolean());
    }

    /// <summary>阻塞算子和 CTE 回退路径仍按完整查询结果的原始顺序截取预览。</summary>
    [Theory]
    [InlineData("SELECT DISTINCT category FROM items", 1)]
    [InlineData("WITH chosen AS (SELECT id FROM items WHERE id <= 3) SELECT id FROM chosen", 2)]
    public async Task RestPreview_WithFallbackQuery_PreservesCompleteQueryPrefix(string sql, int maxRows)
    {
        string complete = await ExecuteAsync(sql);
        string preview = await ExecuteAsync(sql, previewMaxRows: maxRows);

        long[] fullValues = ReadIds(complete);
        long[] previewValues = ReadIds(preview);
        Assert.True(fullValues.Length > maxRows);
        Assert.Equal(fullValues.Take(maxRows), previewValues);
        using var end = JsonDocument.Parse(preview.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
        Assert.Equal(maxRows, end.RootElement.GetProperty("rowCount").GetInt64());
        Assert.True(end.RootElement.GetProperty("truncated").GetBoolean());
    }

    /// <summary>大于服务端估算字节预算的首行不会进入 NDJSON 结果。</summary>
    [Fact]
    public async Task RestPreview_WideFirstRow_ReturnsEmptyTruncatedResult()
    {
        string response = await ExecuteAsync("SELECT payload FROM items", previewMaxRows: 3);
        string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using var meta = JsonDocument.Parse(lines[0]);
        using var end = JsonDocument.Parse(lines[1]);

        Assert.Equal("meta", meta.RootElement.GetProperty("type").GetString());
        Assert.Equal("payload", meta.RootElement.GetProperty("columns")[0].GetString());
        Assert.Equal(0, end.RootElement.GetProperty("rowCount").GetInt64());
        Assert.True(end.RootElement.GetProperty("truncated").GetBoolean());
    }

    /// <summary>未请求预览的 REST 调用仍返回完整结果，即使服务端预览预算很小。</summary>
    [Fact]
    public async Task RestQuery_WithoutPreview_ReturnsAllRows()
    {
        AssertIdRows(await ExecuteAsync("SELECT id FROM items"),
            truncated: false, 1, 2, 3, 4, 5, 6, 7, 8);
    }

    /// <summary>未提供预览参数的远程 ADO 查询仍返回完整行集。</summary>
    [Fact]
    public async Task AdoQuery_WithoutPreview_ReturnsAllRows()
    {
        string authority = _client!.BaseAddress!.Authority;
        using var connection = new SndbConnection(
            $"Data Source=sonnetdb+http://{authority}/preview;Token=preview-admin;Timeout=15");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM items";
        using var reader = await command.ExecuteReaderAsync();
        var ids = new List<long>();
        for (int attempt = 0; attempt < 10 && await reader.ReadAsync(); attempt++)
            ids.Add(reader.GetInt64(0));

        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L], ids);
        Assert.False(Assert.IsType<SndbDataReader>(reader).Truncated);
    }

    /// <summary>Frame 未协商预览，行数和原始 end 帧格式均保持不变。</summary>
    [Fact]
    public async Task FrameQuery_WithoutPreviewNegotiation_ReturnsAllRows()
    {
        var request = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRequest(request, 1, "preview", "SELECT id FROM items", null);
        using var content = new ByteArrayContent(request.WrittenSpan.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sonnetdb-frame");
        using var response = await _client!.PostAsync("/v1/frame", content);
        response.EnsureSuccessStatusCode();
        var bytes = new ReadOnlySequence<byte>(await response.Content.ReadAsByteArrayAsync());
        int rows = 0;
        bool sawEnd = false;
        for (int frame = 0; frame < 16 && FrameCodec.TryReadFrame(ref bytes, out _, out var payload); frame++)
        {
            byte[] data = payload.ToArray();
            switch (SqlFrameCodec.PeekChunkKind(data))
            {
                case SqlQueryChunkKind.Rows:
                    rows += SqlFrameCodec.DecodeQueryRowsFrame(data).Length;
                    break;
                case SqlQueryChunkKind.End:
                    Assert.Equal(8, SqlFrameCodec.DecodeQueryEndFrame(data).RowCount);
                    Assert.Equal(10, data.Length);
                    sawEnd = true;
                    break;
            }
        }

        Assert.Equal(8, rows);
        Assert.True(sawEnd);
        Assert.True(bytes.IsEmpty);
    }

    /// <summary>预览扫描中的客户端断连释放请求，后续请求仍可读取同一张表。</summary>
    [Fact]
    public async Task RestPreview_ClientCancellationDuringScan_AllowsFollowingQuery()
    {
        Assert.True(_app!.Services.GetRequiredService<TsdbRegistry>().TryGet("preview", out var db));
        object store = db.Tables.Open("items");
        PropertyInfo hook = store.GetType().GetProperty(
            "RowDecodedTestHook", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("未找到关系表扫描测试钩子。");
        using var reached = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        hook.SetValue(store, (Action<int>)(row =>
        {
            if (row != 2)
                return;
            reached.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("预览扫描取消测试未释放解码门闩。");
        }));

        Task<HttpResponseMessage>? pending = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/db/preview/sql")
            {
                Content = JsonContent.Create(new SqlRequest("SELECT id FROM items")
                {
                    PreviewMaxRows = 3,
                }, ServerJsonContext.Default.SqlRequest),
            };
            pending = _client!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellation.Token);
            Assert.True(await Task.Run(() => reached.Wait(TimeSpan.FromSeconds(5))));
            cancellation.Cancel();
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            });
        }
        finally
        {
            cancellation.Cancel();
            release.Set();
            hook.SetValue(store, null);
            if (pending is not null)
            {
                try
                {
                    using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                    // 客户端取消后任务完成的预期路径。
                }
                catch (HttpRequestException)
                {
                    // 已断开的连接也可能表现为传输错误。
                }
            }
        }

        AssertIdRows(await ExecuteAsync("SELECT id FROM items"),
            truncated: false, 1, 2, 3, 4, 5, 6, 7, 8);
    }

    private async Task<string> ExecuteAsync(string sql, int? previewMaxRows = null)
    {
        using var response = await _client!.PostAsync("/v1/db/preview/sql",
            JsonContent.Create(new SqlRequest(sql) { PreviewMaxRows = previewMaxRows },
                ServerJsonContext.Default.SqlRequest));
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}");
        Assert.DoesNotContain("\"error\"", body);
        return body;
    }

    private static void AssertIdRows(string response, bool truncated, params long[] expectedIds)
    {
        string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expectedIds.Length + 2, lines.Length);
        using var meta = JsonDocument.Parse(lines[0]);
        Assert.Equal("meta", meta.RootElement.GetProperty("type").GetString());
        for (int index = 0; index < expectedIds.Length; index++)
        {
            using var row = JsonDocument.Parse(lines[index + 1]);
            Assert.Equal(expectedIds[index], row.RootElement[0].GetInt64());
        }
        using var end = JsonDocument.Parse(lines[^1]);
        Assert.Equal(expectedIds.Length, end.RootElement.GetProperty("rowCount").GetInt64());
        Assert.Equal(truncated, end.RootElement.GetProperty("truncated").GetBoolean());
    }

    private static long[] ReadIds(string response)
    {
        string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var ids = new long[lines.Length - 2];
        for (int index = 0; index < ids.Length; index++)
        {
            using var row = JsonDocument.Parse(lines[index + 1]);
            ids[index] = row.RootElement[0].GetInt64();
        }
        return ids;
    }
}
