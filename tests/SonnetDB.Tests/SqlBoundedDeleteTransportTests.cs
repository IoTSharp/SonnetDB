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

/// <summary>通过真实 REST 与 h2c ADO 验证有界删除的返回值、事务和失败原子性。</summary>
public sealed class SqlBoundedDeleteTransportTests : IAsyncLifetime
{
    private const string Token = "delete243-admin";
    private const string Database = "delete243";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-d243-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _protocols = new();
    private WebApplication? _app;
    private string _rest = string.Empty;
    private string _http2 = string.Empty;

    /// <summary>启动独立真实 Kestrel 服务并创建本夹具数据库。</summary>
    public async Task InitializeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = _root,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [Token] = ServerRoles.Admin }
        }, extraArgs:
            ["--Kestrel:Endpoints:DeleteH2:Url=http://127.0.0.1:0", "--Kestrel:Endpoints:DeleteH2:Protocols=Http2"]);
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

    /// <summary>同步与异步参数路径精确删除完整复合键，RETURNING、提交、回滚和重连一致。</summary>
    [Theory]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task Delete_CompositeKey_PreservesReturningTransactionsAndCancellation(string protocol, bool asynchronous)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var connection = Open(protocol);
        await Execute(connection, "CREATE TABLE Items(A INT,B INT,PRIMARY KEY(A,B))", deadline.Token);
        await Execute(connection, "INSERT INTO Items(A,B) VALUES(1,1),(1,2),(2,1),(2,2)", deadline.Token);
        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM Items WHERE A>0 LIMIT @maximum RETURNING A,B";
            delete.Parameters.Add(new SndbParameter("maximum", 1L));
            await using var reader = asynchronous
                ? await delete.ExecuteReaderAsync(deadline.Token)
                : delete.ExecuteReader();
            Assert.Equal("A", reader.GetName(0));
            Assert.Equal(typeof(long), reader.GetFieldType(1));
            int count = 0;
            while (asynchronous ? await reader.ReadAsync(deadline.Token) : reader.Read()) count++;
            Assert.Equal(1, count);
            Assert.Equal(1, reader.RecordsAffected);
        }
        foreach (bool commit in new[] { false, true })
        {
            await using var transaction = await connection.BeginTransactionAsync(deadline.Token);
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM Items WHERE TRUE LIMIT @maximum";
            delete.Parameters.Add(new SndbParameter("maximum", 1L));
            Assert.Equal(1, asynchronous ? await delete.ExecuteNonQueryAsync(deadline.Token) : delete.ExecuteNonQuery());
            delete.CommandText = "SELECT COUNT(*) FROM Items";
            Assert.Equal(2L, await delete.ExecuteScalarAsync(deadline.Token));
            if (commit) await transaction.CommitAsync(deadline.Token);
            else await transaction.RollbackAsync(deadline.Token);
        }
        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM Items WHERE TRUE LIMIT @maximum";
            delete.Parameters.Add(new SndbParameter("maximum", 0L));
            Assert.Equal(0, await delete.ExecuteNonQueryAsync(deadline.Token));
            delete.Parameters[0].Value = -1L;
            await Assert.ThrowsAnyAsync<Exception>(() => delete.ExecuteNonQueryAsync(deadline.Token));
        }
        await using (var canceled = connection.CreateCommand())
        {
            canceled.CommandText = "DELETE FROM Items WHERE TRUE LIMIT 2";
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.ExecuteNonQueryAsync(stop.Token));
        }
        await connection.CloseAsync();
        await using var reopened = Open(protocol);
        await using var query = reopened.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM Items";
        Assert.Equal(2L, await query.ExecuteScalarAsync(deadline.Token));
        Assert.NotEmpty(_protocols);
        Assert.All(_protocols, observed => Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", observed));
    }

    /// <summary>第二个目标违反外键时整句拒绝，原行和索引保持。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Delete_ForeignKeyFailure_PreservesBatch(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = Open(protocol);
        await Execute(connection, "CREATE TABLE Parents(Id INT,PRIMARY KEY(Id))", deadline.Token);
        await Execute(connection, "CREATE TABLE Children(Id INT,ParentId INT,PRIMARY KEY(Id),FOREIGN KEY(ParentId) REFERENCES Parents(Id))", deadline.Token);
        await Execute(connection, "INSERT INTO Parents(Id) VALUES(1),(2),(3)", deadline.Token);
        await Execute(connection, "INSERT INTO Children(Id,ParentId) VALUES(1,2)", deadline.Token);
        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM Parents WHERE Id<3 LIMIT 2";
        await Assert.ThrowsAnyAsync<Exception>(() => delete.ExecuteNonQueryAsync(deadline.Token));
        delete.CommandText = "SELECT COUNT(*) FROM Parents";
        Assert.Equal(3L, await delete.ExecuteScalarAsync(deadline.Token));
    }

    /// <summary>远程参数在字面量格式化前拒绝日期和非整数，不能扩大删除上限。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Delete_DateLimitParameter_RejectsBeforeDeletingRows(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = Open(protocol);
        await Execute(connection, "CREATE TABLE Items(Id INT,PRIMARY KEY(Id))", deadline.Token);
        await Execute(connection, "INSERT INTO Items(Id) VALUES(1),(2),(3)", deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "/* LIMIT in comment */ DELETE FROM Items WHERE TRUE LIMIT @maximum";
        command.Parameters.Add(new SndbParameter("maximum", DateTime.UtcNow));
        foreach (object maximum in new object[] { DateTime.UtcNow, DateTimeOffset.UtcNow, ulong.MaxValue, 1m })
        {
            command.Parameters[0].Value = maximum;
            await Assert.ThrowsAnyAsync<Exception>(() => command.ExecuteNonQueryAsync(deadline.Token));
        }
        command.CommandText = "SELECT COUNT(*) FROM Items";
        Assert.Equal(3L, await command.ExecuteScalarAsync(deadline.Token));
        command.CommandText = "DELETE FROM Items WHERE TRUE LIMIT @maximum";
        command.Parameters[0].Value = 1ul;
        Assert.Equal(1, await command.ExecuteNonQueryAsync(deadline.Token));
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

    /// <summary>停止服务并只回收本夹具的唯一临时目录。</summary>
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
            throw new InvalidOperationException("删除传输夹具目录超出临时目录。");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
}
