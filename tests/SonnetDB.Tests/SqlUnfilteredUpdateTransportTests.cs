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
using Xunit;

namespace SonnetDB.Tests;

/// <summary>通过真实 REST 与 h2c ADO 验证全表 UPDATE 的返回值、事务和取消合同。</summary>
public sealed class SqlUnfilteredUpdateTransportTests : IAsyncLifetime
{
    private const string Token = "update257-admin";
    private const string Database = "update257";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-u257-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _protocols = new();
    private WebApplication? _app;
    private string _rest = string.Empty;
    private string _http2 = string.Empty;

    /// <summary>为每个用例启动动态端口的独立真实 Kestrel 数据库。</summary>
    public async Task InitializeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = _root,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [Token] = ServerRoles.Admin }
        }, extraArgs:
            ["--Kestrel:Endpoints:UpdateH2:Url=http://127.0.0.1:0", "--Kestrel:Endpoints:UpdateH2:Protocols=Http2"]);
        _app.Use(async (context, next) =>
        {
            if (context.Request.Path.Value?.Contains("/sql", StringComparison.Ordinal) == true
                || context.Request.Path == "/v1/frame")
                _protocols.Enqueue(context.Request.Protocol);
            await next(context);
        });
        await _app.StartAsync(deadline.Token);
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        Assert.Equal(2, addresses.Addresses.Count);
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
        using var client = new HttpClient { BaseAddress = new Uri(_rest) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var created = await client.PostAsync("/v1/db", new StringContent(
            $"{{\"name\":\"{Database}\"}}", Encoding.UTF8, "application/json"), deadline.Token);
        created.EnsureSuccessStatusCode();
    }

    /// <summary>同步与异步调用都返回完整行和影响数，真实事务回滚及提交保持一致。</summary>
    [Theory]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task Update_AllRows_PreservesReturningTransactionsAndCancellation(string protocol, bool asynchronous)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var connection = Open(protocol);
        await Execute(connection, "CREATE TABLE Items(Id INT,Value INT,PRIMARY KEY(Id))", deadline.Token);
        await Execute(connection, "INSERT INTO Items(Id,Value) VALUES(1,10),(2,20)", deadline.Token);
        await using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE Items SET Value=Value+@step RETURNING Id,Value";
            update.Parameters.Add(new SndbParameter("step", 3L));
            await using var reader = asynchronous
                ? await update.ExecuteReaderAsync(deadline.Token)
                : update.ExecuteReader();
            Assert.Equal("Id", reader.GetName(0));
            Assert.Equal(typeof(long), reader.GetFieldType(1));
            int count = 0;
            while (asynchronous ? await reader.ReadAsync(deadline.Token) : reader.Read())
            {
                Assert.Equal(reader.GetInt64(0) * 10 + 3, reader.GetInt64(1));
                count++;
            }
            Assert.Equal(2, count);
            Assert.Equal(2, reader.RecordsAffected);
        }
        foreach (bool commit in new[] { false, true })
        {
            await using var transaction = await connection.BeginTransactionAsync(deadline.Token);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE Items SET Value=Value+1";
            Assert.Equal(2, asynchronous ? await update.ExecuteNonQueryAsync(deadline.Token) : update.ExecuteNonQuery());
            update.CommandText = "SELECT SUM(Value) FROM Items";
            Assert.Equal(38L, await update.ExecuteScalarAsync(deadline.Token));
            if (commit) await transaction.CommitAsync(deadline.Token);
            else await transaction.RollbackAsync(deadline.Token);
        }
        await using (var canceled = connection.CreateCommand())
        {
            canceled.CommandText = "UPDATE Items SET Value=99";
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.ExecuteNonQueryAsync(stop.Token));
        }
        await connection.CloseAsync();
        await using var reopened = Open(protocol);
        await using var query = reopened.CreateCommand();
        query.CommandText = "SELECT SUM(Value) FROM Items";
        Assert.Equal(38L, await query.ExecuteScalarAsync(deadline.Token));
        Assert.NotEmpty(_protocols);
        Assert.All(_protocols, observed => Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", observed));
    }

    private SndbConnection Open(string protocol)
    {
        string endpoint = protocol == "rest" ? _rest : _http2;
        var connection = new SndbConnection(
            $"Data Source=sonnetdb+http://{new Uri(endpoint).Authority}/{Database};Token={Token};Protocol={protocol};Timeout=10");
        connection.Open();
        return connection;
    }

    private static async Task Execute(SndbConnection connection, string sql, CancellationToken cancellation)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellation);
    }

    /// <summary>停止本夹具服务并只回收本次创建的唯一目录。</summary>
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
            throw new InvalidOperationException("更新传输夹具目录超出临时目录。");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
}
