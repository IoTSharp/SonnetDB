using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Client;
using SonnetDB.Configuration;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>独立 Client → 真实 HTTP/2 → Server → 持久关系表的 Execute 切片。</summary>
public sealed class SqlExecuteFrameEndpointTests : IAsyncLifetime
{
    private const string Token = "execute-admin";
    private const string Database = "execute_contract";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-execute-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Path, string Protocol)> _requests = new();
    private WebApplication? _app;
    private Uri _address = null!;
    private int _abortNextFrame;

    public async Task InitializeAsync()
    {
        await StartAsync();
        using var http = CreateHttpClient();
        using var content = new StringContent("{\"name\":\"" + Database + "\"}", System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/v1/db", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        _requests.Clear();
    }

    public async Task DisposeAsync()
    {
        await StopAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Execute_ExactParameters_ReopenPreservesValues()
    {
        using var client = CreateClient();
        await client.ExecuteSqlAsync(Database, "CREATE TABLE ExactRows (Id INT, Amount DECIMAL(28,6), Data BLOB, Stamp DATETIMEOFFSET, Elapsed DURATION, Clock TIME, Legacy DATETIME, Note STRING NULL, PRIMARY KEY (Id))");
        var stamp = new DateTimeOffset(2026, 10, 17, 14, 15, 16, TimeSpan.FromHours(8)).AddTicks(1234567);
        var date = new DateTime(2026, 10, 17, 6, 15, 16, DateTimeKind.Utc).AddMilliseconds(123);
        var clock = new TimeOnly(23, 59, 59).Add(TimeSpan.FromTicks(9999999));
        var result = await client.ExecuteSqlAsync(Database,
            "INSERT INTO ExactRows (Id, Amount, Data, Stamp, Elapsed, Clock, Legacy, Note) VALUES (@id, @amount, @data, @stamp, @elapsed, @clock, @legacy, @note)",
            [
                SqlExecuteFrameCodec.FromClr("id", long.MaxValue),
                SqlExecuteFrameCodec.FromClr("amount", 9007199254740993.123456m),
                SqlExecuteFrameCodec.FromClr("data", new byte[] { 0, 255, 42 }),
                SqlExecuteFrameCodec.FromClr("stamp", stamp),
                SqlExecuteFrameCodec.FromClr("elapsed", TimeSpan.FromTicks(long.MinValue)),
                SqlExecuteFrameCodec.FromClr("clock", clock),
                SqlExecuteFrameCodec.FromClr("legacy", date),
                new("note", SqlFrameParameterKind.String, null),
            ]);
        Assert.Equal(1, result.RecordsAffected);
        await StopAsync();
        await StartAsync();
        object?[] row = Assert.Single(await QueryAsync("SELECT Id, Amount, Data, Stamp, Elapsed, Clock, Legacy, Note FROM ExactRows"));
        Assert.Equal(long.MaxValue, row[0]);
        Assert.Equal(9007199254740993.123456m, Assert.IsType<decimal>(row[1]));
        Assert.Equal(new byte[] { 0, 255, 42 }, Assert.IsType<byte[]>(row[2]));
        Assert.Equal(stamp.ToUniversalTime(), Assert.IsType<DateTimeOffset>(row[3]));
        Assert.Equal(TimeSpan.FromTicks(long.MinValue), Assert.IsType<TimeSpan>(row[4]));
        // 既有 Query v6 仍以完整七位小数字符串返回 TIME；不改写旧查询合同。
        Assert.Equal(clock.ToString("HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture),
            Assert.IsType<string>(row[5]));
        Assert.Equal(date, Assert.IsType<DateTime>(row[6]));
        Assert.Null(row[7]);
        Assert.All(_requests, request => { Assert.Equal("/v1/frame", request.Path); Assert.Equal("HTTP/2", request.Protocol); });
    }

    [Fact]
    public async Task Execute_PositionalParametersAndInjectionText_DoNotChangeSql()
    {
        using var client = CreateClient();
        await client.ExecuteSqlAsync(Database, "CREATE TABLE PositionRows (id INT, note STRING, PRIMARY KEY(id))");
        const string text = "单引号 '); DROP TABLE PositionRows; --\n🙂";
        Assert.Equal(1, (await client.ExecuteSqlAsync(Database, "INSERT INTO PositionRows (id, note) VALUES (?, ?)",
            [SqlExecuteFrameCodec.FromClr("", long.MinValue), SqlExecuteFrameCodec.FromClr("", text)])).RecordsAffected);
        Assert.Equal(text, Assert.Single(await QueryAsync("SELECT note FROM PositionRows"))[0]);
        Assert.Equal(1, (await client.ExecuteSqlAsync(Database, "UPDATE PositionRows SET note=@note WHERE id=@id",
            [SqlExecuteFrameCodec.FromClr("note", "updated"), SqlExecuteFrameCodec.FromClr("id", long.MinValue)])).RecordsAffected);
        Assert.Equal(1, (await client.ExecuteSqlAsync(Database, "DELETE FROM PositionRows WHERE id=?",
            [SqlExecuteFrameCodec.FromClr("", long.MinValue)])).RecordsAffected);
        Assert.Empty(await QueryAsync("SELECT id FROM PositionRows"));
        Assert.Equal(0, (await client.ExecuteSqlAsync(Database, "DROP TABLE PositionRows")).RecordsAffected);
    }

    [Theory]
    [InlineData("readonly", "forbidden")]
    [InlineData(null, "http_error")]
    public async Task Execute_Unauthorized_DoesNotMutate(string? token, string errorCode)
    {
        using var admin = CreateClient();
        await admin.ExecuteSqlAsync(Database, "CREATE TABLE ProtectedRows (id INT, PRIMARY KEY(id))");
        using var client = CreateClient(token);
        var error = await Assert.ThrowsAsync<SndbFrameException>(() => client.ExecuteSqlAsync(Database,
            "INSERT INTO ProtectedRows (id) VALUES (1)"));
        Assert.Equal(errorCode, error.Code);
        Assert.Empty(await QueryAsync("SELECT id FROM ProtectedRows"));
    }

    [Theory]
    [InlineData("INSERT INTO GuardedRows (id) VALUES(1) RETURNING id")]
    [InlineData("UPDATE GuardedRows SET id=2 WHERE id=1 RETURNING id")]
    [InlineData("DELETE FROM GuardedRows WHERE id=1 RETURNING id")]
    [InlineData("BEGIN")]
    [InlineData("COMMIT")]
    [InlineData("ROLLBACK")]
    [InlineData("SELECT 1")]
    [InlineData("CREATE DATABASE unapproved")]
    [InlineData("CALL missing_procedure()")]
    public async Task Execute_UnsupportedCapability_IsRejectedBeforeMutation(string sql)
    {
        using var client = CreateClient();
        await client.ExecuteSqlAsync(Database, "CREATE TABLE GuardedRows (id INT, PRIMARY KEY(id))");
        var error = await Assert.ThrowsAsync<SndbFrameException>(() => client.ExecuteSqlAsync(Database, sql));
        Assert.Equal("unsupported_capability", error.Code);
        Assert.Empty(await QueryAsync("SELECT id FROM GuardedRows"));
    }

    [Fact]
    public async Task Execute_UniqueViolation_RollsBackWholeStatement()
    {
        using var client = CreateClient();
        await client.ExecuteSqlAsync(Database, "CREATE TABLE AtomicRows (id INT, PRIMARY KEY(id))");
        await client.ExecuteSqlAsync(Database, "INSERT INTO AtomicRows (id) VALUES (1)");
        var error = await Assert.ThrowsAsync<SndbFrameException>(() => client.ExecuteSqlAsync(Database,
            "INSERT INTO AtomicRows (id) VALUES (2), (1)"));
        Assert.Equal("table_unique_violation", error.Code);
        Assert.Equal(1L, Assert.Single(await QueryAsync("SELECT id FROM AtomicRows"))[0]);
    }

    [Fact]
    public async Task Execute_TypedNullsFloatAndBoolean_PreserveValues()
    {
        using var client = CreateClient();
        await client.ExecuteSqlAsync(Database, "CREATE TABLE ScalarRows (id INT, value FLOAT, flag BOOL, stamp DATETIMEOFFSET NULL, PRIMARY KEY(id))");
        await client.ExecuteSqlAsync(Database, "INSERT INTO ScalarRows (id,value,flag,stamp) VALUES (@id,@value,@flag,@stamp)",
            [SqlExecuteFrameCodec.FromClr("id", 1), SqlExecuteFrameCodec.FromClr("value", -123.25),
             SqlExecuteFrameCodec.FromClr("flag", true), new("stamp", SqlFrameParameterKind.DateTimeOffset, null)]);
        object?[] row = Assert.Single(await QueryAsync("SELECT value,flag,stamp FROM ScalarRows"));
        Assert.Equal(-123.25, Assert.IsType<double>(row[0]));
        Assert.True(Assert.IsType<bool>(row[1]));
        Assert.Null(row[2]);
    }

    [Fact]
    public async Task Execute_StatementBatch_IsRejectedBeforeFirstWrite()
    {
        using var client = CreateClient();
        await client.ExecuteSqlAsync(Database, "CREATE TABLE BatchRows (id INT, PRIMARY KEY(id))");
        await Assert.ThrowsAsync<SndbFrameException>(() => client.ExecuteSqlAsync(Database,
            "INSERT INTO BatchRows (id) VALUES(1); INSERT INTO BatchRows (id) VALUES(2)"));
        Assert.Empty(await QueryAsync("SELECT id FROM BatchRows"));
    }

    [Fact]
    public async Task Execute_SubMillisecondLegacyDate_IsRejectedWithoutRoundingOrMutation()
    {
        using var client = CreateClient();
        await client.ExecuteSqlAsync(Database, "CREATE TABLE LegacyDateRows (id INT, stamp DATETIME, PRIMARY KEY(id))");
        var date = new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc).AddTicks(1);
        var error = await Assert.ThrowsAsync<SndbFrameException>(() => client.ExecuteSqlAsync(Database,
            "INSERT INTO LegacyDateRows (id,stamp) VALUES(1,@stamp)", [SqlExecuteFrameCodec.FromClr("stamp", date)]));
        Assert.Equal("unsupported_parameter_precision", error.Code);
        Assert.Empty(await QueryAsync("SELECT id FROM LegacyDateRows"));
    }

    [Fact]
    public async Task Execute_DisconnectAfterCommit_PreservesUnknownAndDoesNotReplay()
    {
        using var client = CreateClient();
        await client.ExecuteSqlAsync(Database, "CREATE TABLE UnknownRows (id INT, PRIMARY KEY(id))");
        _requests.Clear();
        Interlocked.Exchange(ref _abortNextFrame, 1);
        var error = await Assert.ThrowsAsync<SndbFrameException>(() => client.ExecuteSqlAsync(Database,
            "INSERT INTO UnknownRows (id) VALUES (1)"));
        Assert.True(error.OutcomeUnknown);
        Assert.Single(_requests);
        Assert.Equal(1L, Assert.Single(await QueryAsync("SELECT id FROM UnknownRows"))[0]);
    }

    [Fact]
    public async Task Execute_UnsupportedVersionAndMalformedPayload_AreRejected()
    {
        var request = new ArrayBufferWriter<byte>();
        SqlExecuteFrameCodec.EncodeRequest(request, 91, Database, "CREATE TABLE NeverCreated (id INT)");
        byte[] bytes = request.WrittenMemory.ToArray();
        bytes[FrameHeader.Size] = 2;
        Assert.Equal("unsupported_execute_version", await PostErrorAsync(bytes));
        bytes[FrameHeader.Size] = SqlExecuteFrameCodec.CurrentVersion;
        bytes[^1] = 255; // 超出 256 的畸形参数计数/截断 varuint。
        Assert.Equal("bad_frame", await PostErrorAsync(bytes));
    }

    [Fact]
    public async Task Execute_PreviewRequest_IsRejectedBeforeCreatingTable()
    {
        using var http = CreateHttpClient();
        http.DefaultRequestHeaders.Add("X-SonnetDB-Workbench-Profile", "preview-1");
        var encoded = new ArrayBufferWriter<byte>();
        SqlExecuteFrameCodec.EncodeRequest(encoded, 1, Database, "CREATE TABLE PreviewDenied (id INT)");
        using var content = new ByteArrayContent(encoded.WrittenMemory.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sonnetdb-frame");
        using var response = await http.PostAsync("/v1/frame", content);
        var sequence = new ReadOnlySequence<byte>(await response.Content.ReadAsByteArrayAsync());
        Assert.True(FrameCodec.TryReadFrame(ref sequence, out var header, out var payload));
        Assert.True(header.IsError);
        Assert.Equal("preview_action_denied", FrameCodec.ReadErrorPayload(payload.ToArray()).Code);
        Assert.Empty(await QueryAsync("SHOW TABLES"));
    }

    [Fact]
    public async Task Execute_RealHttp1_ServerRejectsBeforeExecution()
    {
        await using var server = TestServerHost.Build(new ServerOptions
        {
            DataRoot = Path.Combine(_root, "http1-only"),
            Tokens = new Dictionary<string, string> { [Token] = ServerRoles.Admin },
        }, extraArgs: ["--Kestrel:Endpoints:Http:Protocols=Http1"]);
        await server.StartAsync();
        var address = new Uri(Assert.Single(server.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses));
        using var http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var encoded = new ArrayBufferWriter<byte>();
        SqlExecuteFrameCodec.EncodeRequest(encoded, 1, Database, "CREATE TABLE Http1Denied (id INT)");
        using var content = new ByteArrayContent(encoded.WrittenMemory.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sonnetdb-frame");
        using var response = await http.PostAsync("/v1/frame", content);
        Assert.Equal(HttpVersion.Version11, response.Version);
        var sequence = new ReadOnlySequence<byte>(await response.Content.ReadAsByteArrayAsync());
        Assert.True(FrameCodec.TryReadFrame(ref sequence, out var header, out var payload));
        Assert.True(header.IsError);
        Assert.Equal("unsupported_transport", FrameCodec.ReadErrorPayload(payload.ToArray()).Code);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.StopAsync(deadline.Token);
    }

    private SndbFrameClient CreateClient(string? token = Token)
        => new(_address, token, timeout: TimeSpan.FromSeconds(10));

    private HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            BaseAddress = _address,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    private async Task<string> PostErrorAsync(byte[] request)
    {
        var frames = await PostAsync(request);
        var error = Assert.Single(frames);
        Assert.True(error.Header.IsError);
        return FrameCodec.ReadErrorPayload(error.Payload).Code;
    }

    private async Task<List<object?[]>> QueryAsync(string sql)
    {
        var request = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRequest(request, 92, Database, sql);
        var frames = await PostAsync(request.WrittenMemory.ToArray());
        Assert.All(frames, frame => Assert.False(frame.Header.IsError,
            frame.Header.IsError ? FrameCodec.ReadErrorPayload(frame.Payload).Message : ""));
        Assert.Equal(SqlQueryChunkKind.Meta, SqlFrameCodec.PeekChunkKind(frames[0].Payload));
        Assert.Equal(SqlQueryChunkKind.End, SqlFrameCodec.PeekChunkKind(frames[^1].Payload));
        _ = SqlFrameCodec.DecodeQueryMetaFrameWithInfo(frames[0].Payload,
            out bool nativeDateTime, out bool nativeDuration, out bool nativeDateTimeOffset);
        return frames.Skip(1).SkipLast(1).SelectMany(frame => SqlFrameCodec.DecodeQueryRowsFrame(
            frame.Payload, nativeDateTime, nativeDuration, nativeDateTimeOffset)).ToList();
    }

    private async Task<List<(FrameHeader Header, byte[] Payload)>> PostAsync(byte[] bytes)
    {
        using var http = CreateHttpClient();
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sonnetdb-frame");
        http.DefaultRequestHeaders.Add(SqlFrameCodec.ResultVersionHeader, SqlFrameCodec.NativeDateTimeOffsetResultVersion);
        using var response = await http.PostAsync("/v1/frame", content);
        Assert.Equal(HttpVersion.Version20, response.Version);
        response.EnsureSuccessStatusCode();
        var sequence = new ReadOnlySequence<byte>(await response.Content.ReadAsByteArrayAsync());
        var frames = new List<(FrameHeader, byte[])>();
        while (FrameCodec.TryReadFrame(ref sequence, out var header, out var payload)) frames.Add((header, payload.ToArray()));
        Assert.True(sequence.IsEmpty);
        return frames;
    }

    private async Task StartAsync()
    {
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = _root,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [Token] = ServerRoles.Admin, ["readonly"] = ServerRoles.ReadOnly },
        }, extraArgs: ["--Kestrel:Endpoints:Http:Protocols=Http2"]);
        _app.Use(async (ctx, next) =>
        {
            _requests.Enqueue((ctx.Request.Path.Value!, ctx.Request.Protocol));
            if (Interlocked.Exchange(ref _abortNextFrame, 0) == 1)
            {
                Stream original = ctx.Response.Body;
                await using var buffer = new MemoryStream();
                try { ctx.Response.Body = buffer; await next(ctx); ctx.Abort(); }
                finally { ctx.Response.Body = original; }
            }
            else await next(ctx);
        });
        await _app.StartAsync();
        _address = new Uri(Assert.Single(_app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses));
    }

    private async Task StopAsync()
    {
        if (_app is null) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _app.StopAsync(deadline.Token);
        await _app.DisposeAsync();
        _app = null;
    }
}
