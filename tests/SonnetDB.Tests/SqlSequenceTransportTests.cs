using System.Collections.Concurrent;
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
using SonnetDB.Exceptions;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>独立序列通过真实 Kestrel REST 与 h2c ADO 的产品合同。</summary>
public sealed class SqlSequenceTransportTests : IAsyncLifetime
{
    private const string Token = "sequence231-admin";
    private const string Reader = "sequence231-readonly";
    private const string Database = "sequence231";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-s231-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _protocols = new();
    private WebApplication? _app;
    private string _rest = string.Empty;
    private string _http2 = string.Empty;

    /// <summary>启动真实服务并创建独立数据库。</summary>
    public async Task InitializeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = _root,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [Token] = ServerRoles.Admin, [Reader] = ServerRoles.ReadOnly }
        }, extraArgs:
            ["--Kestrel:Endpoints:SequenceH2:Url=http://127.0.0.1:0", "--Kestrel:Endpoints:SequenceH2:Protocols=Http2"]);
        _app.Use(async (context, next) =>
        {
            if (context.Request.Path.Value?.Contains("/sql", StringComparison.Ordinal) == true || context.Request.Path == "/v1/frame")
                _protocols.Enqueue(context.Request.Protocol);
            await next(context);
        });
        await _app.StartAsync(deadline.Token);
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        foreach (string address in addresses.Addresses)
        {
            using var probe = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(3) };
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz")
                { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                using var response = await probe.SendAsync(request, deadline.Token);
                if (response.IsSuccessStatusCode) _rest = address;
                else _http2 = address;
            }
            catch (HttpRequestException) { _http2 = address; }
        }
        Assert.NotEmpty(_rest);
        Assert.NotEmpty(_http2);
        using var client = Http();
        using var created = await client.PostAsync("/v1/db", new StringContent(
            $"{{\"name\":\"{Database}\"}}", Encoding.UTF8, "application/json"), deadline.Token);
        created.EnsureSuccessStatusCode();
    }

    /// <summary>取号、DEFAULT、表达式批量与回滚耗号在两种传输下一致。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Sequence_ConcurrentDefaultsRollbackAndErrors_UseRealTransport(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var first = Open(protocol);
        await using var second = Open(protocol);
        await Execute(first, "CREATE SEQUENCE shared START WITH 100", deadline.Token);
        await Execute(first, "CREATE TABLE items(id INT DEFAULT nextval('shared'), value INT, PRIMARY KEY(id))", deadline.Token);
        await Execute(first, "INSERT INTO items(value) VALUES(10),(20)", deadline.Token);
        await Execute(first, "INSERT INTO items(id,value) VALUES(nextval('shared')+10,30),(DEFAULT,40)", deadline.Token);
        Assert.Equal(4L, await Scalar(first, "SELECT COUNT(*) FROM items", deadline.Token));
        await using (var transaction = await first.BeginTransactionAsync(deadline.Token))
        {
            await using var insert = first.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO items(value) VALUES(50),(60)";
            Assert.Equal(2, await insert.ExecuteNonQueryAsync(deadline.Token));
            await transaction.RollbackAsync(deadline.Token);
        }
        var numbers = new ConcurrentBag<long>();
        await Task.WhenAll(Allocate(first), Allocate(second));
        Assert.Equal(Enumerable.Range(106, 24).Select(static n => (long)n), numbers.Order());
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scalar(first, "SELECT nextval('shared')", stop.Token));
        }
        await using (var metadata = first.CreateCommand())
        {
            metadata.CommandText = "SELECT nextval('shared') FROM items WHERE id < 0";
            await using var result = await metadata.ExecuteReaderAsync(deadline.Token);
            Assert.Equal(typeof(long), result.GetFieldType(0));
            Assert.False(await result.ReadAsync(deadline.Token));
        }
        Assert.Equal(130L, await Scalar(first, "SELECT nextval('shared')", deadline.Token));
        await Execute(first, $"CREATE SEQUENCE edge START WITH {long.MaxValue}", deadline.Token);
        Assert.Equal(long.MaxValue, await Scalar(first, "SELECT nextval('edge')", deadline.Token));
        var exhausted = await Assert.ThrowsAsync<SndbServerException>(() => Scalar(first, "SELECT nextval('edge')", deadline.Token));
        Assert.Equal(SequenceException.Exhausted, exhausted.Error);
        var missing = await Assert.ThrowsAsync<SndbServerException>(() => Scalar(first, "SELECT nextval('missing')", deadline.Token));
        Assert.Equal(SequenceException.NotFound, missing.Error);
        await Execute(first, "DROP SEQUENCE shared", deadline.Token);
        var defaultMissing = await Assert.ThrowsAsync<SndbServerException>(() => Execute(first, "INSERT INTO items(value) VALUES(1)", deadline.Token));
        Assert.Equal(SequenceException.NotFound, defaultMissing.Error);
        Assert.All(_protocols, observed => Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", observed));

        async Task Allocate(SndbConnection connection)
        {
            for (int i = 0; i < 12; i++)
                numbers.Add((long)(await Scalar(connection, "SELECT nextval('shared')", deadline.Token))!);
        }
    }

    /// <summary>只读凭据不能通过 SELECT、嵌套表达式或过程消耗号码。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Sequence_ReadOnlyPermission_RejectsBeforeConsumption(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var admin = Open(protocol);
        await Execute(admin, "CREATE SEQUENCE n", deadline.Token);
        await using var read = Open(protocol, Reader);
        foreach (string sql in new[] { "SELECT nextval('n')", "SELECT coalesce(NULL,nextval('n'))", "CREATE SEQUENCE denied", "DROP SEQUENCE n" })
        {
            var error = await Assert.ThrowsAsync<SndbServerException>(() => Execute(read, sql, deadline.Token));
            Assert.Equal("forbidden", error.Error);
        }
        Assert.Equal(1L, await Scalar(admin, "SELECT nextval('n')", deadline.Token));
    }

    /// <summary>Web Preview 的只读动作不允许取号。</summary>
    [Fact]
    public async Task Sequence_WorkbenchReadPreview_RejectsSideEffects()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = Open("rest");
        await Execute(connection, "CREATE SEQUENCE n", deadline.Token);
        using var http = Http();
        http.DefaultRequestHeaders.Add("X-SonnetDB-Workbench-Profile", "preview-1");
        http.DefaultRequestHeaders.Add("X-SonnetDB-Workbench-Action", "sql.read");
        using var response = await http.PostAsync($"/v1/db/{Database}/sql", new StringContent(
            "{\"sql\":\"SELECT nextval('n')\"}", Encoding.UTF8, "application/json"), deadline.Token);
        string body = await response.Content.ReadAsStringAsync(deadline.Token);
        Assert.Contains("preview_action_denied", body, StringComparison.Ordinal);
        Assert.Equal(1L, await Scalar(connection, "SELECT nextval('n')", deadline.Token));
    }

    private HttpClient Http()
    {
        var client = new HttpClient { BaseAddress = new Uri(_rest), Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }
    private SndbConnection Open(string protocol, string token = Token)
    {
        string endpoint = protocol == "rest" ? _rest : _http2;
        var connection = new SndbConnection($"Data Source=sonnetdb+http://{new Uri(endpoint).Authority}/{Database};Token={token};Protocol={protocol};Timeout=10");
        connection.Open();
        return connection;
    }
    private static async Task Execute(SndbConnection connection, string sql, CancellationToken cancellation)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellation);
    }
    private static async Task<object?> Scalar(SndbConnection connection, string sql, CancellationToken cancellation)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellation);
    }

    /// <summary>停止服务并回收本次独占目录。</summary>
    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _app.StopAsync(deadline.Token);
            await _app.DisposeAsync();
        }
        string absolute = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(absolute) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())))
            throw new InvalidOperationException("序列夹具目录超出临时目录。");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
}
