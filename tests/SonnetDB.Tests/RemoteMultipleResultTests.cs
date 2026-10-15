using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>GH-Issue #229：真实 REST 和 h2c ADO 顺序多结果及资源释放合同。</summary>
public sealed class RemoteMultipleResultTests : IAsyncLifetime
{
    private const string DatabaseName = "multiple_results";
    private const string AdminToken = "multiple-results-admin";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-remote-multiple-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
    private readonly ConcurrentQueue<(string Path, string Protocol)> _requests = new();
    private readonly TaskCompletionSource _queryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _app;
    private string _restUrl = string.Empty;
    private string _frameUrl = string.Empty;
    private volatile bool _delayQueries;
    private bool _disposed;

    /// <summary>启动独立的 HTTP/1.1 与真正 HTTP/2 监听器。</summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        try
        {
            _app = TestServerHost.Build(new ServerOptions
            {
                DataRoot = Path.Combine(_root, "server"),
                AllowAnonymousProbes = true,
                Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
            }, extraArgs:
            [
                "--Kestrel:Endpoints:FrameH2:Url=http://127.0.0.1:0",
                "--Kestrel:Endpoints:FrameH2:Protocols=Http2",
            ]);
            _app.Use(async (context, next) =>
            {
                string path = context.Request.Path.Value ?? string.Empty;
                _requests.Enqueue((path, context.Request.Protocol));
                if (_delayQueries && (path == "/v1/frame" || path.EndsWith("/sql", StringComparison.Ordinal)))
                {
                    _queryStarted.TrySetResult();
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _deadline.Token);
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    return;
                }
                await next(context);
            });
            await _app.StartAsync(_deadline.Token);
            var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            Assert.Equal(2, addresses.Addresses.Count);
            foreach (string address in addresses.Addresses)
            {
                using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var request = new HttpRequestMessage(HttpMethod.Get, address + "/healthz")
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                };
                try
                {
                    using var response = await probe.SendAsync(request, _deadline.Token);
                    if (response.IsSuccessStatusCode) _restUrl = address;
                    else _frameUrl = address;
                }
                catch (HttpRequestException) { _frameUrl = address; }
            }
            Assert.NotEmpty(_restUrl);
            Assert.NotEmpty(_frameUrl);
            using var http = new HttpClient { BaseAddress = new Uri(_restUrl), Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
            using var created = await http.PostAsync("/v1/db", new StringContent(
                "{\"name\":\"" + DatabaseName + "\"}", Encoding.UTF8, "application/json"), _deadline.Token);
            created.EnsureSuccessStatusCode();
            _requests.Clear();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>关闭宿主并清理本测试的专属目录。</summary>
    public async Task DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _deadline.Cancel();
        if (_app is not null)
        {
            using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await _app.StopAsync(stopDeadline.Token); }
            finally { await _app.DisposeAsync(); _app = null; }
        }
        _deadline.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task NextResult_RelationalQueries_UsesActualProtocolAndRefreshesEmptyAndNonemptyMetadata(string protocol, bool useAsync)
    {
        using var connection = await OpenAsync(protocol);
        using var command = connection.CreateCommand();
        command.CommandText = "/* ; */ SELECT id FROM items WHERE id < 0; SELECT name AS \"part;name\" FROM items WHERE id=@id; SELECT id FROM items WHERE id=1";
        command.Parameters.AddWithValue("id", 2L);
        _requests.Clear();
        using var reader = useAsync ? await command.ExecuteReaderAsync(_deadline.Token) : command.ExecuteReader();
        Assert.Equal("id", reader.GetName(0));
        Assert.Equal(typeof(long), reader.GetFieldType(0));
        Assert.False(reader.HasRows);
        Assert.True(useAsync ? await reader.NextResultAsync(_deadline.Token) : reader.NextResult());
        Assert.Equal("part;name", reader.GetName(0));
        Assert.Equal(typeof(string), reader.GetFieldType(0));
        Assert.Equal("part;name", reader.GetSchemaTable()!.Rows[0]["ColumnName"]);
        Assert.True(reader.HasRows);
        // HasRows 已缓存第二组首行，提前切换须丢弃该行且释放其远程响应。
        Assert.True(useAsync ? await reader.NextResultAsync(_deadline.Token) : reader.NextResult());
        Assert.Equal("id", reader.GetName(0));
        Assert.True(useAsync ? await reader.ReadAsync(_deadline.Token) : reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.False(useAsync ? await reader.NextResultAsync(_deadline.Token) : reader.NextResult());
        Assert.False(reader.HasRows);
        AssertQueryProtocol(protocol, count: 3);
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task ExecuteReader_FrozenNamedParametersAndQuotedValues_ReturnsBothResults(string protocol)
    {
        using var connection = await OpenAsync(protocol);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM items WHERE id=@id; SELECT @text AS value FROM items WHERE id=:id";
        command.Parameters.AddWithValue("id", 2L);
        command.Parameters.AddWithValue("text", "a;'quote -- ; @id");
        _requests.Clear();
        await using var reader = await command.ExecuteReaderAsync(_deadline.Token);
        command.Parameters[0].Value = 1L;
        command.Parameters[1].Value = "changed";
        Assert.True(await reader.ReadAsync(_deadline.Token));
        Assert.Equal("second", reader.GetString(0));
        Assert.True(await reader.NextResultAsync(_deadline.Token));
        Assert.True(await reader.ReadAsync(_deadline.Token));
        Assert.Equal("a;'quote -- ; @id", reader.GetString(0));
        Assert.False(await reader.NextResultAsync(_deadline.Token));
        AssertQueryProtocol(protocol, count: 2);
    }

    [Theory]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task NextResult_UnmatchedNamedParameter_PreservesStrictRemoteBinding(string protocol, bool useAsync)
    {
        using var connection = await OpenAsync(protocol);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM items WHERE id=@x; SELECT @missing AS value FROM items WHERE id=2";
        command.Parameters.AddWithValue("x", 1L);
        command.Parameters.AddWithValue("y", 2L);
        _requests.Clear();
        using var reader = useAsync ? await command.ExecuteReaderAsync(_deadline.Token) : command.ExecuteReader();
        Assert.True(useAsync ? await reader.ReadAsync(_deadline.Token) : reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        var exception = useAsync
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => reader.NextResultAsync(_deadline.Token))
            : Assert.Throws<InvalidOperationException>(() => reader.NextResult());
        Assert.Contains("@missing", exception.Message, StringComparison.Ordinal);
        Assert.False(await reader.NextResultAsync(_deadline.Token));
        AssertQueryProtocol(protocol, count: 1);
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task ExecuteReader_PositionalParametersAcrossStatements_UsesGlobalOrdinals(string protocol)
    {
        using var connection = await OpenAsync(protocol);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM items WHERE id=?; SELECT name FROM items WHERE id=?";
        command.Parameters.AddWithValue("x", 1L);
        command.Parameters.AddWithValue("y", 2L);
        _requests.Clear();
        await using var reader = await command.ExecuteReaderAsync(_deadline.Token);
        Assert.True(await reader.ReadAsync(_deadline.Token));
        Assert.Equal("first", reader.GetString(0));
        Assert.True(await reader.NextResultAsync(_deadline.Token));
        Assert.True(await reader.ReadAsync(_deadline.Token));
        Assert.Equal("second", reader.GetString(0));
        Assert.False(await reader.NextResultAsync(_deadline.Token));
        AssertQueryProtocol(protocol, count: 2);
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task ExecuteReader_BaseAndViewStrings_DataSetLoadsBothUnboundedResults(string protocol)
    {
        using var connection = await OpenAsync(protocol);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE VIEW item_names AS SELECT id,name FROM items";
        await command.ExecuteNonQueryAsync(_deadline.Token);
        command.CommandText = "SELECT name FROM items WHERE id=1; SELECT name FROM item_names WHERE id=2";
        _requests.Clear();
        using var reader = command.ExecuteReader();
        Assert.Null(reader.GetColumnSchema()[0].ColumnSize);
        Assert.Equal(-1, reader.GetSchemaTable()!.Rows[0][SchemaTableColumn.ColumnSize]);
        using var data = new DataSet();
        data.Load(reader, LoadOption.OverwriteChanges, "base_names", "view_names");
        Assert.Equal("first", data.Tables[0].Rows[0]["name"]);
        Assert.Equal("second", data.Tables[1].Rows[0]["name"]);
        Assert.Equal(-1, data.Tables[0].Columns["name"]!.MaxLength);
        Assert.Equal(-1, data.Tables[1].Columns["name"]!.MaxLength);
        AssertQueryProtocol(protocol, count: 2);
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task NextResult_TransactionReadYourWritesAndRollback_PreservesExistingTransaction(string protocol)
    {
        using var connection = await OpenAsync(protocol);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO items(id,name) VALUES(3,'third')";
        Assert.Equal(1, await command.ExecuteNonQueryAsync(_deadline.Token));
        command.CommandText = "SELECT id FROM items WHERE id=3; SELECT name FROM items WHERE id=3";
        await using (var reader = await command.ExecuteReaderAsync(_deadline.Token))
        {
            Assert.True(await reader.ReadAsync(_deadline.Token));
            Assert.Equal(3L, reader.GetInt64(0));
            Assert.True(await reader.NextResultAsync(_deadline.Token));
            Assert.True(await reader.ReadAsync(_deadline.Token));
            Assert.Equal("third", reader.GetString(0));
            Assert.False(await reader.NextResultAsync(_deadline.Token));
        }
        transaction.Rollback();
        command.Transaction = null;
        command.CommandText = "SELECT id FROM items WHERE id=3";
        Assert.Null(await command.ExecuteScalarAsync(_deadline.Token));
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task NextResult_LaterServerError_PropagatesAndReaderCloseReleasesCommand(string protocol)
    {
        using var connection = await OpenAsync(protocol);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM items; SELECT id FROM missing_table";
        var reader = await command.ExecuteReaderAsync(_deadline.Token);
        Assert.True(await reader.ReadAsync(_deadline.Token));
        await Assert.ThrowsAsync<SndbServerException>(() => reader.NextResultAsync(_deadline.Token));
        reader.Close();
        command.CommandText = "SELECT name FROM items WHERE id=2";
        Assert.Equal("second", await command.ExecuteScalarAsync(_deadline.Token));
    }

    [Theory]
    [InlineData("rest", "timeout")]
    [InlineData("rest", "caller")]
    [InlineData("rest", "command")]
    [InlineData("frame-http2", "timeout")]
    [InlineData("frame-http2", "caller")]
    [InlineData("frame-http2", "command")]
    public async Task NextResult_DelayedRealRequest_PreservesTimeoutAndCancellation(string protocol, string cancellationKind)
    {
        using var connection = await OpenAsync(protocol);
        using var command = connection.CreateCommand();
        command.CommandTimeout = cancellationKind == "timeout" ? 1 : 0;
        command.CommandText = "SELECT id FROM items; SELECT name FROM items";
        using var reader = await command.ExecuteReaderAsync(_deadline.Token);
        using var caller = new CancellationTokenSource();
        _delayQueries = true;
        var move = reader.NextResultAsync(caller.Token);
        await _queryStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (cancellationKind == "caller") caller.Cancel();
        if (cancellationKind == "command") command.Cancel();
        Exception? error = await Record.ExceptionAsync(() => move).WaitAsync(TimeSpan.FromSeconds(5));
        if (cancellationKind == "timeout") Assert.IsType<TimeoutException>(error);
        else
        {
            var canceled = Assert.IsAssignableFrom<OperationCanceledException>(error);
            if (cancellationKind == "caller") Assert.Equal(caller.Token, canceled.CancellationToken);
        }
        reader.Close();
        _delayQueries = false;
        command.CommandText = "SELECT name FROM items WHERE id=1";
        Assert.Equal("first", await command.ExecuteScalarAsync(_deadline.Token));
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task CloseConnection_PendingLaterQuery_DoesNotRunAndClosesConnection(string protocol)
    {
        using var connection = await OpenAsync(protocol);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM items; SELECT name FROM items";
        _requests.Clear();
        var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection, _deadline.Token);
        Assert.True(await reader.ReadAsync(_deadline.Token));
        await reader.DisposeAsync();
        Assert.True(reader.IsClosed);
        Assert.Equal(ConnectionState.Closed, connection.State);
        AssertQueryProtocol(protocol, count: 1);
    }

    private async Task<SndbConnection> OpenAsync(string protocol)
    {
        string url = protocol == "rest" ? _restUrl : _frameUrl;
        var connection = new SndbConnection($"Data Source=sonnetdb+http://{new Uri(url).Authority}/{DatabaseName};Token={AdminToken};Protocol={protocol};Timeout=10");
        await connection.OpenAsync(_deadline.Token);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE items(id INT, name STRING, PRIMARY KEY(id))";
        await command.ExecuteNonQueryAsync(_deadline.Token);
        command.CommandText = "INSERT INTO items(id,name) VALUES(1,'first'),(2,'second')";
        await command.ExecuteNonQueryAsync(_deadline.Token);
        return connection;
    }

    private void AssertQueryProtocol(string protocol, int count)
    {
        string path = protocol == "rest" ? "/v1/db/" + DatabaseName + "/sql" : "/v1/frame";
        var queries = _requests.Where(request => request.Path == path).ToArray();
        Assert.Equal(count, queries.Length);
        Assert.All(queries, request => Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", request.Protocol));
    }
}
