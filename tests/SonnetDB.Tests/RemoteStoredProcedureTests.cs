using System.Collections.Concurrent;
using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Tests;

public sealed class RemoteStoredProcedureTests : IAsyncLifetime
{
    private const string Database = "stored_procedure";
    private const string Token = "stored-procedure-admin";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-remote-procedure-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(90));
    private readonly ConcurrentQueue<(string Path, string Protocol, byte Op)> _requests = new();
    private WebApplication? _app;
    private string _rest = string.Empty, _frames = string.Empty;
    private int _abortNextProcedure;
    private int _corruptNextProcedure;
    private bool _disposed;

    public async Task InitializeAsync()
    {
        try
        {
            _app = TestServerHost.Build(new ServerOptions
            {
                DataRoot = Path.Combine(_root, "server"),
                AllowAnonymousProbes = true,
                Tokens = new Dictionary<string, string> { [Token] = ServerRoles.Admin, ["procedure-reader"] = ServerRoles.ReadOnly },
            }, extraArgs: ["--Kestrel:Endpoints:ProcedureH2:Url=http://127.0.0.1:0", "--Kestrel:Endpoints:ProcedureH2:Protocols=Http2"]);
            _app.Use(async (context, next) =>
            {
                byte op = 0;
                if (context.Request.Path == "/v1/frame")
                {
                    context.Request.EnableBuffering();
                    byte[] header = new byte[FrameHeader.Size];
                    await context.Request.Body.ReadExactlyAsync(header, _deadline.Token);
                    context.Request.Body.Position = 0;
                    if (FrameHeader.TryRead(header, out var frame)) op = frame.Op;
                }
                _requests.Enqueue((context.Request.Path.Value ?? string.Empty, context.Request.Protocol, op));
                bool procedure = op == (byte)SqlFrameOp.Procedure || context.Request.Path.Value?.EndsWith("/sql/procedure", StringComparison.Ordinal) == true;
                if (procedure && Interlocked.Exchange(ref _corruptNextProcedure, 0) != 0)
                {
                    Stream actual = context.Response.Body;
                    using var held = new MemoryStream();
                    context.Response.Body = held;
                    try { await next(context); }
                    finally { context.Response.Body = actual; }
                    context.Response.ContentLength = null;
                    await context.Response.Body.WriteAsync(new byte[] { 1, 2, 3 }, _deadline.Token);
                    return;
                }
                if (procedure && Interlocked.Exchange(ref _abortNextProcedure, 0) != 0)
                {
                    Stream actual = context.Response.Body;
                    using var held = new MemoryStream();
                    context.Response.Body = held;
                    try { await next(context); context.Abort(); }
                    finally { context.Response.Body = actual; }
                    return;
                }
                await next(context);
            });
            await _app.StartAsync(_deadline.Token);
            var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            Assert.Equal(2, addresses.Count);
            foreach (string address in addresses)
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var request = new HttpRequestMessage(HttpMethod.Get, address + "/healthz")
                { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                try
                {
                    using var response = await http.SendAsync(request, _deadline.Token);
                    if (response.IsSuccessStatusCode) _rest = address; else _frames = address;
                }
                catch (HttpRequestException) { _frames = address; }
            }
            Assert.NotEmpty(_rest);
            Assert.NotEmpty(_frames);
            using var setup = new HttpClient { BaseAddress = new Uri(_rest), Timeout = TimeSpan.FromSeconds(10) };
            setup.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            using var created = await setup.PostAsync("v1/db", new StringContent("{\"name\":\"" + Database + "\"}", Encoding.UTF8, "application/json"), _deadline.Token);
            created.EnsureSuccessStatusCode();
            using var connection = Open("rest");
            Execute(connection, "CREATE TABLE items(id INT,value INT,PRIMARY KEY(id))");
            Execute(connection, """
            CREATE PROCEDURE exchange(IN row_id INT,INOUT state INT,OUT output STRING) LANGUAGE SQL AS BEGIN
              INSERT INTO items(id,value) VALUES(@row_id,@state);
              SET @state=@state+2;
              SELECT @state AS first;
              SELECT 'second' AS second;
              SET @output='done'; RETURN 7;
            END
            """);
            Execute(connection, "CREATE PROCEDURE two_values(IN input INT) LANGUAGE SQL AS BEGIN SELECT @input AS first; SELECT 'second' AS second; END");
            _requests.Clear();
        }
        catch { await DisposeAsync(); throw; }
    }

    public async Task DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _deadline.Cancel();
        if (_app is not null)
        {
            using var stopped = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await _app.StopAsync(stopped.Token); }
            finally { await _app.DisposeAsync(); }
        }
        _deadline.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("rest", "scalar", false)]
    [InlineData("rest", "scalar", true)]
    [InlineData("rest", "reader", false)]
    [InlineData("rest", "reader", true)]
    [InlineData("rest", "nonquery", false)]
    [InlineData("rest", "nonquery", true)]
    [InlineData("frame-http2", "scalar", false)]
    [InlineData("frame-http2", "scalar", true)]
    [InlineData("frame-http2", "reader", false)]
    [InlineData("frame-http2", "reader", true)]
    [InlineData("frame-http2", "nonquery", false)]
    [InlineData("frame-http2", "nonquery", true)]
    public async Task StandardProcedure_OrderedResultsOutputsAndAffectedRows_UsesActualProtocol(string protocol, string entry, bool asynchronous)
    {
        using var connection = Open(protocol);
        var command = Call(connection);
        var output = command.Parameters["output"];
        if (entry == "scalar")
            Assert.Equal(12L, asynchronous ? await command.ExecuteScalarAsync(_deadline.Token) : command.ExecuteScalar());
        else if (entry == "nonquery")
            Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(_deadline.Token) : command.ExecuteNonQuery());
        else
        {
            var reader = asynchronous ? await command.ExecuteReaderAsync(_deadline.Token) : command.ExecuteReader();
            command.Dispose();
            Assert.Equal("old", output.Value);
            Assert.Equal(1, reader.RecordsAffected);
            Assert.True(asynchronous ? await reader.ReadAsync(_deadline.Token) : reader.Read());
            Assert.Equal("first", reader.GetName(0));
            Assert.Equal(12L, reader.GetInt64(0));
            Assert.True(asynchronous ? await reader.NextResultAsync(_deadline.Token) : reader.NextResult());
            Assert.Equal("second", reader.GetName(0));
            Assert.True(asynchronous ? await reader.ReadAsync(_deadline.Token) : reader.Read());
            Assert.Equal("second", reader.GetString(0));
            Assert.Equal("old", output.Value);
            if (asynchronous) await reader.DisposeAsync(); else reader.Close();
            reader.Close();
        }
        Assert.Equal("done", output.Value);
        Assert.Equal(12L, command.Parameters["state"].Value);
        Assert.Equal(7L, command.Parameters["status"].Value);
        AssertProcedureRequest(protocol);
        command.Dispose();
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Completion_InvalidOutputSize_PreservesCallerEarlierWriteAndDoesNotPublish(string protocol)
    {
        using var connection = Open(protocol);
        using var transaction = connection.BeginTransaction();
        using (var before = new SndbCommand("INSERT INTO items(id,value) VALUES(50,50)", connection) { Transaction = transaction })
            before.ExecuteNonQuery();
        _requests.Clear();
        using var command = Call(connection);
        command.Transaction = transaction;
        command.Parameters["output"].Size = 2;
        await Assert.ThrowsAsync<SndbServerException>(() => command.ExecuteNonQueryAsync(_deadline.Token));
        Assert.Equal("old", command.Parameters["output"].Value);
        Assert.Equal(10L, command.Parameters["state"].Value);
        Assert.Equal(-1L, command.Parameters["status"].Value);
        AssertProcedureRequest(protocol);
        await transaction.CommitAsync(_deadline.Token);
        using var check = new SndbCommand("SELECT id FROM items", connection);
        Assert.Equal(50L, await check.ExecuteScalarAsync(_deadline.Token));
        using var reader = await check.ExecuteReaderAsync(_deadline.Token);
        Assert.True(await reader.ReadAsync(_deadline.Token));
        Assert.False(await reader.ReadAsync(_deadline.Token));
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task ResponseDroppedAfterCommit_RemainsUnknownWithoutRetryOrOutputPublication(string protocol)
    {
        using var connection = Open(protocol);
        using var command = Call(connection);
        Interlocked.Exchange(ref _abortNextProcedure, 1);
        Assert.NotNull(await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync(_deadline.Token)));
        Assert.Equal("old", command.Parameters["output"].Value);
        Assert.Equal(-1L, command.Parameters["status"].Value);
        AssertProcedureRequest(protocol);
        using var check = Open("rest");
        using var count = new SndbCommand("SELECT COUNT(*) FROM items", check);
        Assert.Equal(1L, await count.ExecuteScalarAsync(_deadline.Token));
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task MalformedResponseAfterCommit_RemainsUnknownWithoutReplayOrOutputs(string protocol)
    {
        using var connection = Open(protocol);
        using var command = Call(connection);
        Interlocked.Exchange(ref _corruptNextProcedure, 1);
        var error = await Assert.ThrowsAsync<SndbServerException>(() => command.ExecuteNonQueryAsync(_deadline.Token));
        Assert.Equal("procedure_outcome_unknown", error.Error);
        Assert.Equal("old", command.Parameters["output"].Value);
        Assert.Equal(-1L, command.Parameters["status"].Value);
        AssertProcedureRequest(protocol);
        using var check = Open("rest");
        using var count = new SndbCommand("SELECT COUNT(*) FROM items", check);
        Assert.Equal(1L, await count.ExecuteScalarAsync(_deadline.Token));
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Reader_CancelledReadAndRepeatedClose_DoesNotPublishOutputs(string protocol)
    {
        using var connection = Open(protocol);
        using var command = Call(connection);
        using var reader = await command.ExecuteReaderAsync(_deadline.Token);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cancellation.Token));
        reader.Close();
        reader.Close();
        Assert.Equal("old", command.Parameters["output"].Value);
        Assert.Equal(10L, command.Parameters["state"].Value);
        Assert.Equal(-1L, command.Parameters["status"].Value);
        AssertProcedureRequest(protocol);
    }

    [Theory]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task Completion_OutputTypeViolation_RollsBackOnlyCurrentCall(string protocol, bool callerTransaction)
    {
        using var connection = Open(protocol);
        using var transaction = callerTransaction ? connection.BeginTransaction() : null;
        if (transaction is not null)
        {
            using var before = new SndbCommand("INSERT INTO items(id,value) VALUES(50,50)", connection) { Transaction = transaction };
            before.ExecuteNonQuery();
        }
        _requests.Clear();
        using var command = Call(connection);
        command.Transaction = transaction;
        command.Parameters["output"].DbType = DbType.Int64;
        await Assert.ThrowsAsync<SndbServerException>(() => command.ExecuteNonQueryAsync(_deadline.Token));
        Assert.Equal("old", command.Parameters["output"].Value);
        Assert.Equal(10L, command.Parameters["state"].Value);
        Assert.Equal(-1L, command.Parameters["status"].Value);
        AssertProcedureRequest(protocol);
        if (transaction is not null) await transaction.CommitAsync(_deadline.Token);
        using var check = new SndbCommand("SELECT COUNT(*) FROM items", connection);
        Assert.Equal(callerTransaction ? 1L : 0L, await check.ExecuteScalarAsync(_deadline.Token));
    }

    [Theory]
    [InlineData("rest", "\"MixedName\"")]
    [InlineData("rest", "\"say\"\"hello\"")]
    [InlineData("frame-http2", "\"MixedName\"")]
    [InlineData("frame-http2", "\"say\"\"hello\"")]
    public async Task ProcedureName_ExactQuotedSpelling_UsesDecodedCatalogName(string protocol, string name)
    {
        using (var createConnection = Open("rest"))
            Execute(createConnection, "CREATE PROCEDURE " + name + "() LANGUAGE SQL AS BEGIN SELECT 42 AS value; END");
        _requests.Clear();
        using var connection = Open(protocol);
        using var command = new SndbCommand(name, connection) { CommandType = CommandType.StoredProcedure };
        Assert.Equal(42L, await command.ExecuteScalarAsync(_deadline.Token));
        AssertProcedureRequest(protocol);
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task TextCall_TwoResults_PreservesExistingRouteAndBothResults(string protocol)
    {
        using var connection = Open(protocol);
        using var command = new SndbCommand("CALL two_values(42)", connection);
        using var reader = await command.ExecuteReaderAsync(_deadline.Token);
        Assert.Equal("first", reader.GetName(0));
        Assert.True(await reader.ReadAsync(_deadline.Token));
        Assert.Equal(42L, reader.GetInt64(0));
        Assert.True(await reader.NextResultAsync(_deadline.Token));
        Assert.Equal("second", reader.GetName(0));
        Assert.True(await reader.ReadAsync(_deadline.Token));
        Assert.Equal("second", reader.GetString(0));
        Assert.False(await reader.NextResultAsync(_deadline.Token));
        var request = Assert.Single(_requests);
        // 旧 Text CALL 在 frame-http2 配置下仍是 HTTP/2 SQL 路由；不是 Procedure=3 的证据。
        Assert.Equal("/v1/db/" + Database + "/sql", request.Path);
        Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", request.Protocol);
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task ReadOnlyPrincipal_WriteProcedure_IsRejectedWithoutOutputsOrWrites(string protocol)
    {
        using var connection = Open(protocol, "procedure-reader");
        using var command = Call(connection);
        var error = await Assert.ThrowsAsync<SndbServerException>(() => command.ExecuteNonQueryAsync(_deadline.Token));
        Assert.Equal("routine_forbidden", error.Error);
        Assert.Equal("old", command.Parameters["output"].Value);
        AssertProcedureRequest(protocol);
        using var administrator = Open("rest");
        using var count = new SndbCommand("SELECT COUNT(*) FROM items", administrator);
        Assert.Equal(0L, await count.ExecuteScalarAsync(_deadline.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestJson_OverEncodedByteBudget_RejectsBeforeDeserialization(bool chunked)
    {
        using var http = new HttpClient { BaseAddress = new Uri(_rest), Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using HttpContent content = chunked
            ? new ChunkedBytes(SqlProcedureFrameCodec.MaxJsonRequestBytes + 1, _deadline.Token)
            : new ByteArrayContent(new byte[SqlProcedureFrameCodec.MaxJsonRequestBytes + 1]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await http.PostAsync("v1/db/" + Database + "/sql/procedure", content, _deadline.Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var connection = Open("rest");
        using var count = new SndbCommand("SELECT COUNT(*) FROM items", connection);
        Assert.Equal(0L, await count.ExecuteScalarAsync(_deadline.Token));
    }

    private SndbConnection Open(string protocol, string token = Token)
    {
        string address = protocol == "rest" ? _rest : _frames;
        var connection = new SndbConnection($"Data Source=sonnetdb+http://{new Uri(address).Authority}/{Database};Token={token};Protocol={protocol};Timeout=20");
        connection.Open();
        return connection;
    }

    private static SndbCommand Call(SndbConnection connection)
    {
        var command = new SndbCommand("exchange", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 10 };
        command.Parameters.Add(new SndbParameter("output", "old") { Direction = ParameterDirection.Output, DbType = DbType.String });
        command.Parameters.Add(new SndbParameter("state", 10L) { Direction = ParameterDirection.InputOutput, DbType = DbType.Int64 });
        command.Parameters.Add(new SndbParameter("status", -1L) { Direction = ParameterDirection.ReturnValue, DbType = DbType.Int64 });
        command.Parameters.Add(new SndbParameter("row_id", 1L));
        return command;
    }

    private void AssertProcedureRequest(string protocol)
    {
        var request = Assert.Single(_requests);
        Assert.Equal(protocol == "rest" ? "/v1/db/" + Database + "/sql/procedure" : "/v1/frame", request.Path);
        Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", request.Protocol);
        if (protocol == "frame-http2") Assert.Equal((byte)SqlFrameOp.Procedure, request.Op);
    }

    private static void Execute(SndbConnection connection, string sql)
    {
        using var command = new SndbCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    private sealed class ChunkedBytes(int totalBytes, CancellationToken cancellationToken) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            byte[] block = new byte[8192];
            int remaining = totalBytes;
            for (int index = 0; remaining > 0 && index <= totalBytes / block.Length + 1; index++)
            {
                int count = Math.Min(block.Length, remaining);
                await stream.WriteAsync(block.AsMemory(0, count), cancellationToken);
                remaining -= count;
            }
            if (remaining != 0) throw new InvalidOperationException("Chunked test content budget exceeded.");
        }
    }
}
