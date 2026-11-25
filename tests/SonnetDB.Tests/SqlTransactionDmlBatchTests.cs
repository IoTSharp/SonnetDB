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
using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Hosting;
using SonnetDB.Ingest;
using SonnetDB.Json;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>验证真实 HTTP 事务 DML 批次的有序前缀、逐条回执和会话隔离。</summary>
public sealed class SqlTransactionDmlBatchTests : IAsyncLifetime
{
    private const string SqlPath = "/v1/db/dml_batch/sql";
    private const string TransactionsPath = SqlPath + "/transactions";
    private const string ConnectionHeader = "X-SonnetDB-Connection-Session";
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-dml-batch-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private HttpClient _client = null!;

    /// <summary>在临时目录启动隔离的 Kestrel 测试服务。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            _app = TestServerHost.Build(new ServerOptions
            {
                DataRoot = _dataRoot,
                Tokens = new Dictionary<string, string> { ["dml-batch-test-admin"] = ServerRoles.Admin },
            });
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _app.StartAsync(startup.Token);
            string address = _app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            _client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) };
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "dml-batch-test-admin");
            _client.DefaultRequestHeaders.Add(SqlFrameCodec.ResultVersionHeader, SqlFrameCodec.NativeDateTime2ResultVersion);
            using var create = await _client.PostAsync("/v1/db",
                JsonContent.Create(new CreateDatabaseRequest("dml_batch"), ServerJsonContext.Default.CreateDatabaseRequest));
            Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
            await ExecuteAsync(SqlPath, "CREATE TABLE items (id INT, val INT, sampled DATETIME2 NULL, PRIMARY KEY (id))");
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>关闭隔离服务并清理本测试创建的数据库目录。</summary>
    public async Task DisposeAsync()
    {
        _client?.Dispose();
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
            Assert.Equal(tempRoot, Path.GetDirectoryName(target));
            Assert.StartsWith("sndb-dml-batch-", Path.GetFileName(target));
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
    }

    /// <summary>零行不符时仅执行前缀，同一会话可以显式继续并提交。</summary>
    [Fact]
    public async Task MismatchStopsSuffixAndSessionCanResume()
    {
        using var createSession = await _client.PostAsync(SqlPath + "/sessions", null);
        string connection = await ReadIdAsync(createSession);
        string transaction = await BeginAsync(connection);
        SqlTransactionDmlBatchStatement[] statements =
        [
            new("INSERT INTO items (id, val) VALUES (1, 10) RETURNING val", ExpectedRecordsAffected: 1),
            new("UPDATE items SET val = val + 1 WHERE id = 1", ExpectedRecordsAffected: 1),
            new("UPDATE items SET val = 99 WHERE id = 2", ExpectedRecordsAffected: 1),
            new("INSERT INTO items (id, val) VALUES (2, 20)", ExpectedRecordsAffected: 1),
        ];
        using (var wrongSession = await SendBatchAsync(transaction, null, statements))
            Assert.Equal(HttpStatusCode.NotFound, wrongSession.StatusCode);

        using var batch = await SendBatchAsync(transaction, connection, statements);
        JsonElement[] lines = await ReadResultAsync(batch);
        Assert.Equal(new[] { 1, 1, 0 }, AffectedCounts(lines));
        Assert.Equal(10, Rows(lines).Single()[0].GetInt32());
        AssertBatchEnd(lines[^1], 3, SqlTransactionDmlBatchProtocol.RecordsAffectedMismatch);
        Assert.Empty(Rows(await ExecuteAsync(SqlPath, "SELECT val FROM items")));

        JsonElement[] transactionRows = await ExecuteAsync(transaction + "/sql", "SELECT id, val FROM items", connection);
        JsonElement row = Assert.Single(Rows(transactionRows));
        Assert.Equal(1, row[0].GetInt32());
        Assert.Equal(11, row[1].GetInt32());

        using var resumed = await SendBatchAsync(transaction, connection, statements[^1]);
        JsonElement[] resumedLines = await ReadResultAsync(resumed);
        Assert.Equal(new[] { 1 }, AffectedCounts(resumedLines));
        AssertBatchEnd(resumedLines[^1], 1, SqlTransactionDmlBatchProtocol.Completed);
        using var commit = await PostAsync(transaction + "/commit", null, connection);
        Assert.True(commit.IsSuccessStatusCode, await commit.Content.ReadAsStringAsync());
        Assert.Equal(new[] { 1, 2 }, Rows(await ExecuteAsync(SqlPath, "SELECT id FROM items ORDER BY id"))
            .Select(static row => row[0].GetInt32()));
    }

    [Fact]
    public async Task TimeSeriesWriteRecoveryReachesAdoAndFramesWithAccuratePrefix()
    {
        var registry = _app!.Services.GetRequiredService<TsdbRegistry>();
        Assert.True(registry.TryGet("dml_batch", out var database));
        database.Write(Model.Point.Create("recovery_metric", 1000, new Dictionary<string, string>(),
            new Dictionary<string, Model.FieldValue> { ["v"] = Model.FieldValue.FromLong(1) }));
        string segment = TsdbPaths.SegmentPath(database.RootDirectory, segmentId: 1);
        Directory.CreateDirectory(Path.GetDirectoryName(segment)!);
        File.WriteAllText(segment + ".tmp", "private_flush_failure_data");
        Assert.ThrowsAny<IOException>(() => database.FlushNow());

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var connection = new SndbConnection(
            $"Mode=Remote;Protocol=rest;Data Source={_client.BaseAddress};Database=dml_batch;Token=dml-batch-test-admin;Timeout=10");
        await connection.OpenAsync(deadline.Token);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO recovery_metric (TIME, v) VALUES (2000, 2)";
        var failure = await Assert.ThrowsAsync<SndbServerException>(() => command.ExecuteNonQueryAsync(deadline.Token));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.Equal(TimeSeriesWriteRecoveryRequiredException.ErrorCode, failure.Error);
        Assert.DoesNotContain("private_flush_failure_data", failure.ServerMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(database.RootDirectory, failure.ServerMessage, StringComparison.Ordinal);

        command.CommandText = "INSERT INTO items (id, val) VALUES (11, 1); " +
            "INSERT INTO recovery_metric (TIME, v) VALUES (2000, 2); INSERT INTO items (id, val) VALUES (12, 1)";
        await using var reader = await command.ExecuteReaderAsync(deadline.Token);
        Assert.False(await reader.ReadAsync(deadline.Token));
        Assert.Equal(1, reader.RecordsAffected);
        var partial = await Assert.ThrowsAsync<SndbServerException>(() => reader.NextResultAsync(deadline.Token));
        Assert.Equal(HttpStatusCode.OK, partial.StatusCode);
        Assert.Equal(TimeSeriesWriteRecoveryRequiredException.PartialErrorCode, partial.Error);
        Assert.Equal(11, Rows(await ExecuteAsync(SqlPath, "SELECT id FROM items")).Single()[0].GetInt32());

        var blocks = new TsdbColumnarBlock(new Dictionary<string, string>(), new long[] { 2000 },
            [TsdbColumnarColumn.Int64("v", new long[] { 2 })]);
        var frame = new ArrayBufferWriter<byte>();
        TsdbFrameCodec.EncodeWriteColumnarRequest(frame, 9, "dml_batch", "recovery_metric", BulkFlushMode.None, [blocks]);
        using var body = new ByteArrayContent(frame.WrittenSpan.ToArray());
        body.Headers.ContentType = new MediaTypeHeaderValue("application/x-sonnetdb-frame");
        using var response = await _client.PostAsync("/v1/frame", body, deadline.Token);
        response.EnsureSuccessStatusCode();
        var responseFrames = new ReadOnlySequence<byte>(await response.Content.ReadAsByteArrayAsync(deadline.Token));
        Assert.True(FrameCodec.TryReadFrame(ref responseFrames, out var header, out var payload));
        Assert.True(header.IsError);
        Assert.Equal(TimeSeriesWriteRecoveryRequiredException.ErrorCode, FrameCodec.ReadErrorPayload(payload.ToArray()).Code);
        Assert.True(responseFrames.IsEmpty);
    }

    /// <summary>首个 SQL 错误使用正常错误映射，保留事务前缀并阻止后缀。</summary>
    [Fact]
    public async Task SqlErrorStopsSuffixAndPrefixCanBeRolledBack()
    {
        string transaction = await BeginAsync();
        using var batch = await SendBatchAsync(transaction, null,
            new("INSERT INTO items (id, val) VALUES (1, 10)"),
            new("UPDATE items SET missing_column = 20 WHERE id = 1"),
            new("INSERT INTO items (id, val) VALUES (2, 20)"));
        JsonElement[] lines = await ReadResultAsync(batch);
        Assert.Equal(new[] { 1 }, AffectedCounts(lines));
        Assert.True(lines[^1].TryGetProperty("error", out _));
        Assert.False(lines[^1].TryGetProperty("type", out _));
        JsonElement row = Assert.Single(Rows(await ExecuteAsync(transaction + "/sql", "SELECT id, val FROM items")));
        Assert.Equal(1, row[0].GetInt32());
        Assert.Equal(10, row[1].GetInt32());
        using var rollback = await _client.PostAsync(transaction + "/rollback", null);
        Assert.True(rollback.IsSuccessStatusCode, await rollback.Content.ReadAsStringAsync());
        Assert.Empty(Rows(await ExecuteAsync(SqlPath, "SELECT id FROM items")));
        using var completed = await SendBatchAsync(transaction, null,
            new SqlTransactionDmlBatchStatement("DELETE FROM items WHERE id = 1"));
        Assert.Equal(HttpStatusCode.Conflict, completed.StatusCode);
    }

    /// <summary>精确 DATETIME2 参数和 RETURNING 保留原始 ticks、列声明和受影响行数。</summary>
    [Fact]
    public async Task PreciseDateTimeReturningPreservesTicks()
    {
        string transaction = await BeginAsync();
        const string sql = "INSERT INTO items (id, val, sampled) VALUES (1, 10, @sampled) RETURNING sampled";
        DateTime sample = new DateTime(2026, 11, 24, 1, 2, 3, DateTimeKind.Utc).AddTicks(1234567);
        var payload = new ArrayBufferWriter<byte>();
        SqlExecuteFrameCodec.EncodeRequest(payload, 1, string.Empty, sql,
            [SqlExecuteFrameCodec.FromClr("sampled", sample)]);
        using var batch = await SendBatchAsync(transaction, null, new SqlTransactionDmlBatchStatement(
            sql, TypedParameters: payload.WrittenSpan[FrameHeader.Size..].ToArray(), ExpectedRecordsAffected: 1));
        JsonElement[] lines = await ReadResultAsync(batch);
        Assert.Equal(SqlFrameCodec.NativeDateTime2ResultVersion,
            Assert.Single(batch.Headers.GetValues(SqlFrameCodec.ResultVersionHeader)));
        Assert.Equal("datetime2", lines[0].GetProperty("columnTypes")[0].GetString());
        JsonElement returned = Assert.Single(Rows(lines))[0];
        Assert.Equal("datetime", returned.GetProperty("$type").GetString());
        Assert.Equal(sample.Ticks, returned.GetProperty("ticks").GetInt64());
        Assert.Equal((int)sample.Kind, returned.GetProperty("kind").GetInt32());
        Assert.Equal(new[] { 1 }, AffectedCounts(lines));
        AssertBatchEnd(lines[^1], 1, SqlTransactionDmlBatchProtocol.Completed);
    }

    /// <summary>空批次、超限语句数、超限字节数和非 DML 命令均被拒绝。</summary>
    [Fact]
    public async Task InvalidAndOversizedRequestsDoNotWrite()
    {
        string transaction = await BeginAsync();
        using var empty = await SendBatchAsync(transaction, null);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        var statement = new SqlTransactionDmlBatchStatement("INSERT INTO items (id, val) VALUES (1, 10)");
        using var tooMany = await SendBatchAsync(transaction, null,
            Enumerable.Repeat(statement, SqlTransactionDmlBatchProtocol.MaxStatements + 1).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        using var oversizedContent = new StringContent(
            new string(' ', SqlTransactionDmlBatchProtocol.MaxRequestBytes + 1), Encoding.UTF8, "application/json");
        using var tooLarge = await PostAsync(transaction + "/sql/batch", oversizedContent, null);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        using var forbiddenDdl = await SendBatchAsync(transaction, null,
            new("CREATE TABLE other (id INT, PRIMARY KEY (id))"), statement);
        Assert.Equal(HttpStatusCode.BadRequest, forbiddenDdl.StatusCode);
        Assert.Empty(Rows(await ExecuteAsync(transaction + "/sql", "SELECT id FROM items")));
    }

    private async Task<string> BeginAsync(string? connection = null)
    {
        using var response = await PostAsync(TransactionsPath, null, connection);
        Assert.Equal(SqlTransactionDmlBatchProtocol.Version,
            Assert.Single(response.Headers.GetValues(SqlTransactionDmlBatchProtocol.CapabilityHeader)));
        return TransactionsPath + "/" + await ReadIdAsync(response);
    }

    private static async Task<string> ReadIdAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetString()!;
    }

    private async Task<HttpResponseMessage> SendBatchAsync(
        string transaction, string? connection, params SqlTransactionDmlBatchStatement[] statements)
    {
        using var content = JsonContent.Create(new SqlTransactionDmlBatchRequest(statements),
            ServerJsonContext.Default.SqlTransactionDmlBatchRequest);
        return await PostAsync(transaction + "/sql/batch", content, connection);
    }

    private async Task<JsonElement[]> ExecuteAsync(string path, string sql, string? connection = null)
    {
        using var content = JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest);
        using var response = await PostAsync(path, content, connection);
        return await ReadResultAsync(response);
    }

    private async Task<HttpResponseMessage> PostAsync(string path, HttpContent? content, string? connection)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (connection is not null)
            request.Headers.Add(ConnectionHeader, connection);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement[]> ReadResultAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();
    }

    private static IEnumerable<JsonElement> Rows(JsonElement[] lines)
        => lines.Where(static line => line.ValueKind == JsonValueKind.Array);

    private static IEnumerable<int> AffectedCounts(JsonElement[] lines)
        => lines.Where(static line => line.ValueKind == JsonValueKind.Object
                && line.TryGetProperty("type", out var type) && type.GetString() == "end")
            .Select(static line => line.GetProperty("recordsAffected").GetInt32());

    private static void AssertBatchEnd(JsonElement end, int statementsExecuted, string stopReason)
    {
        Assert.Equal(SqlTransactionDmlBatchProtocol.EndRecordType, end.GetProperty("type").GetString());
        Assert.Equal(statementsExecuted, end.GetProperty("statementsExecuted").GetInt32());
        Assert.Equal(stopReason, end.GetProperty("stopReason").GetString());
    }
}
