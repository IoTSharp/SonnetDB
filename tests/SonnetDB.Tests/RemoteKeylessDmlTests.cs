using System.Collections.Concurrent;
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

/// <summary>#243/#253/#255：真实 REST/h2c 与嵌入式的无主键 DML 同步、异步合同。</summary>
public sealed class RemoteKeylessDmlTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-keyless-ado-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Path, string Protocol)> _requests = new();
    private WebApplication? _app;
    private string _rest = string.Empty;
    private string _h2 = string.Empty;
    private const string Token = "keyless-admin";
    private const string Database = "keyless_dml";

    public async Task InitializeAsync()
    {
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = _root,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [Token] = ServerRoles.Admin },
        }, extraArgs: ["--Kestrel:Endpoints:FrameH2:Url=http://127.0.0.1:0", "--Kestrel:Endpoints:FrameH2:Protocols=Http2"]);
        _app.Use(async (context, next) =>
        {
            _requests.Enqueue((context.Request.Path.Value ?? string.Empty, context.Request.Protocol));
            await next(context);
        });
        await _app.StartAsync();
        foreach (string address in _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, address + "/healthz")
                { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                using var response = await client.SendAsync(request);
                if (response.IsSuccessStatusCode) _rest = address;
                else _h2 = address;
            }
            catch (HttpRequestException) { _h2 = address; }
        }
        Assert.NotEmpty(_rest);
        Assert.NotEmpty(_h2);
        using var http = new HttpClient { BaseAddress = new Uri(_rest) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var created = await http.PostAsync("/v1/db", new StringContent($"{{\"name\":\"{Database}\"}}", Encoding.UTF8, "application/json"));
        created.EnsureSuccessStatusCode();
        _requests.Clear();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("embedded", false)]
    [InlineData("embedded", true)]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task KeylessDml_DuplicatesTransactionAndLimit_ReturnSameRows(string protocol, bool asynchronous)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string connectionString = protocol == "embedded" ? $"Data Source={Path.Combine(_root, "embedded")}" :
            $"Data Source=sonnetdb+http://{new Uri(protocol == "rest" ? _rest : _h2).Authority}/{Database};Token={Token};Protocol={protocol};Timeout=10";
        await using var connection = new SndbConnection(connectionString);
        if (asynchronous) await connection.OpenAsync(deadline.Token);
        else connection.Open();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        await Change("CREATE TABLE logs(message STRING NULL,value INT NULL,CHECK(value>=0))");
        command.Parameters.AddWithValue("@message", "event");
        Assert.Equal(3, await Change("INSERT INTO logs(message,value) VALUES(@message,1),(@message,1),(NULL,2)"));
        command.Parameters.Clear();
        await using (var tx = asynchronous ? await connection.BeginTransactionAsync(deadline.Token) : connection.BeginTransaction())
        {
            command.Transaction = (SndbTransaction)tx;
            Assert.Equal(2, await Change("INSERT INTO logs(message,value) VALUES('event',1),('event',1)"));
            Assert.Equal(4, await Change("UPDATE logs SET value=3 WHERE message='event'"));
            Assert.Equal(1, await Change("DELETE FROM logs WHERE value=3 LIMIT 1"));
            command.CommandText = "SELECT COUNT(*) FROM logs WHERE value=3";
            Assert.Equal(3L, asynchronous ? await command.ExecuteScalarAsync(deadline.Token) : command.ExecuteScalar());
            if (asynchronous) await tx.RollbackAsync(deadline.Token);
            else tx.Rollback();
        }
        command.Transaction = null;
        Assert.NotNull(await Record.ExceptionAsync(async () => { await Change("UPDATE logs SET value=-1 WHERE message='event'"); }));
        Assert.Equal(2, await Change("UPDATE logs SET value=3 WHERE message='event'"));
        Assert.Equal(0, await Change("DELETE FROM logs WHERE TRUE LIMIT 0"));
        command.Parameters.AddWithValue("@maximum", 1L);
        command.CommandText = "DELETE FROM logs WHERE message='event' LIMIT @maximum RETURNING *";
        await using (var reader = asynchronous ? await command.ExecuteReaderAsync(deadline.Token) : command.ExecuteReader())
        {
            Assert.Equal(2, reader.FieldCount);
            Assert.True(asynchronous ? await reader.ReadAsync(deadline.Token) : reader.Read());
            Assert.Equal("event", reader.GetString(0));
            Assert.Equal(3L, reader.GetInt64(1));
            Assert.False(asynchronous ? await reader.ReadAsync(deadline.Token) : reader.Read());
        }
        command.Parameters.Clear();
        Assert.Equal(1, await Change("DELETE FROM logs WHERE message IS NULL LIMIT 99"));
        command.CommandText = "SELECT COUNT(*) FROM logs";
        Assert.Equal(1L, asynchronous ? await command.ExecuteScalarAsync(deadline.Token) : command.ExecuteScalar());
        await using (var tx = asynchronous ? await connection.BeginTransactionAsync(deadline.Token) : connection.BeginTransaction())
        {
            command.Transaction = (SndbTransaction)tx;
            Assert.Equal(2, await Change("INSERT INTO logs(message,value) VALUES('committed',4),('committed',4)"));
            Assert.Equal(1, await Change("DELETE FROM logs WHERE message='committed' LIMIT 1"));
            if (asynchronous) await tx.CommitAsync(deadline.Token);
            else tx.Commit();
        }
        command.Transaction = null;
        command.CommandText = "SELECT COUNT(*) FROM logs WHERE message='committed'";
        Assert.Equal(1L, asynchronous ? await command.ExecuteScalarAsync(deadline.Token) : command.ExecuteScalar());
        var columns = connection.GetSchema("Columns", [null, null, "logs", null]);
        Assert.Equal(new[] { "message", "value" }, columns.Rows.Cast<System.Data.DataRow>()
            .Select(row => (string)row["COLUMN_NAME"]).Order());
        if (protocol != "embedded")
        {
            Assert.NotEmpty(_requests);
            Assert.All(_requests, request => Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", request.Protocol));
            if (protocol == "frame-http2") Assert.Contains(_requests, request => request.Path == "/v1/frame");
        }

        async Task<int> Change(string sql)
        {
            command.CommandText = sql;
            return asynchronous ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery();
        }
    }
}
