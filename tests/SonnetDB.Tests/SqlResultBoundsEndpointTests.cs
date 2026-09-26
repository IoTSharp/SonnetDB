using System.Buffers;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Data;
using SonnetDB.Json;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Tests;

public sealed class SqlResultBoundsEndpointTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-sql-result-bounds-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = _dataRoot,
            SqlExecution = new SqlExecutionResourceOptions { MaxResultRows = 2 },
            Tokens = new Dictionary<string, string> { ["bounds-admin"] = ServerRoles.Admin },
        });
        await _app.StartAsync();
        string address = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "bounds-admin");
        using var create = await _client.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest("bounds"), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        await ExecuteAsync("CREATE TABLE rows_to_preview (id INT, PRIMARY KEY (id))");
        await ExecuteAsync("INSERT INTO rows_to_preview (id) VALUES (1), (2), (3)");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        if (Directory.Exists(_dataRoot))
            Directory.Delete(_dataRoot, recursive: true);
    }

    [Fact]
    public async Task RestQuery_WithoutPreviewRequest_PreservesCompleteResults()
    {
        string response = await ExecuteAsync("SELECT id FROM rows_to_preview ORDER BY id");
        string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var end = JsonDocument.Parse(lines[^1]);

        Assert.Equal(5, lines.Length);
        Assert.Equal(3, end.RootElement.GetProperty("rowCount").GetInt64());
        Assert.False(end.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdoQuery_WithoutPreviewRequest_PreservesCompleteResults(bool asynchronous)
    {
        string address = _client!.BaseAddress!.Authority;
        using var connection = new SndbConnection(
            $"Data Source=sonnetdb+http://{address}/bounds;Token=bounds-admin;Timeout=15");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM rows_to_preview ORDER BY id";
        command.CommandTimeout = 15;
        using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
        int rows = 0;
        for (int attempt = 0; attempt < 5 && (asynchronous ? await reader.ReadAsync() : reader.Read()); attempt++)
            rows++;

        Assert.Equal(3, rows);
        Assert.False(Assert.IsType<SndbDataReader>(reader).Truncated);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(100, 2)]
    public async Task RestQuery_WithPreviewRequest_AppliesCallerAndServerBounds(int requested, int expected)
    {
        string response = await ExecuteAsync("SELECT id FROM rows_to_preview ORDER BY id", requested);
        string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var end = JsonDocument.Parse(lines[^1]);

        Assert.Equal(expected + 2, lines.Length);
        Assert.Equal(expected, end.RootElement.GetProperty("rowCount").GetInt64());
        Assert.True(end.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal("[1]", lines[1].Trim());
    }

    [Fact]
    public async Task RestQuery_WithExactPreviewSize_ReportsCompleteResults()
    {
        string response = await ExecuteAsync("SELECT id FROM rows_to_preview ORDER BY id LIMIT 2", 2);
        string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var end = JsonDocument.Parse(lines[^1]);

        Assert.Equal(2, end.RootElement.GetProperty("rowCount").GetInt64());
        Assert.False(end.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task FrameQuery_WithoutPreviewNegotiation_PreservesOriginalEndPayload()
    {
        var request = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRequest(request, 1, "bounds", "SELECT id FROM rows_to_preview ORDER BY id", null);
        using var content = new ByteArrayContent(request.WrittenSpan.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sonnetdb-frame");
        using var response = await _client!.PostAsync("/v1/frame", content);
        response.EnsureSuccessStatusCode();
        var bytes = new ReadOnlySequence<byte>(await response.Content.ReadAsByteArrayAsync());
        int rows = 0;
        bool sawEnd = false;
        for (int count = 0; count < 8 && FrameCodec.TryReadFrame(ref bytes, out _, out var payload); count++)
        {
            byte[] data = payload.ToArray();
            switch (SqlFrameCodec.PeekChunkKind(data))
            {
                case SqlQueryChunkKind.Rows:
                    rows += SqlFrameCodec.DecodeQueryRowsFrame(data).Length;
                    break;
                case SqlQueryChunkKind.End:
                    var end = SqlFrameCodec.DecodeQueryEndFrame(data);
                    Assert.Equal(3, end.RowCount);
                    // The original end payload is chunk kind + one-byte count + double.
                    Assert.Equal(10, data.Length);
                    sawEnd = true;
                    break;
            }
        }

        Assert.Equal(3, rows);
        Assert.True(sawEnd);
        Assert.True(bytes.IsEmpty);
    }

    [Fact]
    public async Task BatchTruncatedSelect_DoesNotPreventCommit()
    {
        using var response = await _client!.PostAsync("/v1/db/bounds/sql/batch",
            JsonContent.Create(new SqlBatchRequest([
                new SqlRequest("BEGIN"),
                new SqlRequest("SELECT id FROM rows_to_preview ORDER BY id") { PreviewMaxRows = 1 },
                new SqlRequest("INSERT INTO rows_to_preview (id) VALUES (4)"),
                new SqlRequest("COMMIT"),
            ]), ServerJsonContext.Default.SqlBatchRequest));
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.DoesNotContain("\"error\"", body);
        Assert.Contains("\"truncated\":true", body);
        string check = await ExecuteAsync("SELECT COUNT(*) AS count FROM rows_to_preview");
        Assert.Contains("[4]", check);
    }

    [Fact]
    public async Task BatchTruncatedSelect_LaterFailureRollsBackAndStopsBatch()
    {
        using var response = await _client!.PostAsync("/v1/db/bounds/sql/batch",
            JsonContent.Create(new SqlBatchRequest([
                new SqlRequest("BEGIN"),
                new SqlRequest("INSERT INTO rows_to_preview (id) VALUES (4)"),
                new SqlRequest("SELECT id FROM rows_to_preview ORDER BY id") { PreviewMaxRows = 1 },
                new SqlRequest("INSERT INTO rows_to_preview (id) VALUES (1)"),
                new SqlRequest("COMMIT"),
                new SqlRequest("INSERT INTO rows_to_preview (id) VALUES (5)"),
            ]), ServerJsonContext.Default.SqlBatchRequest));
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"truncated\":true", body);
        Assert.Contains("\"error\"", body);
        string check = await ExecuteAsync("SELECT COUNT(*) AS count FROM rows_to_preview");
        Assert.Contains("[3]", check);
    }

    [Fact]
    public async Task ReturningPreview_DoesNotChangeWriteCountOrPersistedRows()
    {
        string response = await ExecuteAsync(
            "INSERT INTO rows_to_preview (id) VALUES (4), (5), (6) RETURNING id", 1);
        string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var end = JsonDocument.Parse(lines[^1]);

        Assert.Equal(1, end.RootElement.GetProperty("rowCount").GetInt64());
        Assert.Equal(3, end.RootElement.GetProperty("recordsAffected").GetInt32());
        Assert.True(end.RootElement.GetProperty("truncated").GetBoolean());
        string check = await ExecuteAsync("SELECT COUNT(*) AS count FROM rows_to_preview");
        Assert.Contains("[6]", check);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidPreviewBound_RejectsBeforeExecutingWrite(int requested)
    {
        using var response = await _client!.PostAsync("/v1/db/bounds/sql",
            JsonContent.Create(new SqlRequest(
                "INSERT INTO rows_to_preview (id) VALUES (4) RETURNING id")
            { PreviewMaxRows = requested },
                ServerJsonContext.Default.SqlRequest));
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);
        string check = await ExecuteAsync("SELECT COUNT(*) AS count FROM rows_to_preview");
        Assert.Contains("[3]", check);
    }

    private async Task<string> ExecuteAsync(string sql, int? previewMaxRows = null)
    {
        using var response = await _client!.PostAsync("/v1/db/bounds/sql",
            JsonContent.Create(new SqlRequest(sql) { PreviewMaxRows = previewMaxRows }, ServerJsonContext.Default.SqlRequest));
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}");
        Assert.DoesNotContain("\"error\"", body);
        return body;
    }
}
