using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Tests;

public sealed class SqlSerializableTransportTests : IAsyncLifetime
{
    private const string Token = "serial225-test-admin";
    private const string Database = "serial225";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-serial225-server-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Path, string Protocol, string Isolation)> _requests = new();
    private WebApplication? _app;
    private string _rest = string.Empty;
    private string _h2 = string.Empty;
    private int _legacyMode;

    public async Task InitializeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = _root,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [Token] = ServerRoles.Admin }
        }, extraArgs:
            ["--Kestrel:Endpoints:SerialH2:Url=http://127.0.0.1:0", "--Kestrel:Endpoints:SerialH2:Protocols=Http2"]);
        _app.Use(async (context, next) =>
        {
            string path = context.Request.Path.Value ?? string.Empty;
            _requests.Enqueue((path, context.Request.Protocol, context.Request.Headers["X-SonnetDB-Isolation-Level"].ToString()));
            bool create = path == $"/v1/db/{Database}/sql/transactions";
            if (create && _legacyMode == 1)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (create && _legacyMode is 2 or 3)
                context.Request.Headers.Remove("X-SonnetDB-Isolation-Level");
            if (!create || _legacyMode != 3)
            {
                await next(context);
                return;
            }
            Stream original = context.Response.Body;
            await using var captured = new MemoryStream();
            context.Response.Body = captured;
            try
            {
                await next(context);
                captured.Position = 0;
                using var body = await JsonDocument.ParseAsync(captured, cancellationToken: context.RequestAborted);
                context.Response.Body = original;
                await using var writer = new Utf8JsonWriter(context.Response.BodyWriter);
                writer.WriteStartObject();
                writer.WriteString("id", body.RootElement.GetProperty("id").GetString());
                writer.WriteEndObject();
                await writer.FlushAsync(context.RequestAborted);
            }
            finally { context.Response.Body = original; }
        });
        await _app.StartAsync(deadline.Token);
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        Assert.Equal(2, addresses.Addresses.Count);
        foreach (string address in addresses.Addresses.Take(2))
        {
            using var probe = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(3) };
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz")
                { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                using var response = await probe.SendAsync(request, deadline.Token);
                if (response.IsSuccessStatusCode) _rest = address;
                else _h2 = address;
            }
            catch (HttpRequestException) { _h2 = address; }
        }
        Assert.NotEmpty(_rest);
        Assert.NotEmpty(_h2);
        using var http = Http();
        using var created = await http.PostAsync("/v1/db", new StringContent($"{{\"name\":\"{Database}\"}}",
            Encoding.UTF8, "application/json"), deadline.Token);
        created.EnsureSuccessStatusCode();
        _requests.Clear();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _app.StopAsync(deadline.Token);
            await _app.DisposeAsync();
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Serializable_TwoConnections_AbortChangingReadsPhantomsAndWriteSkew(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var cancellation = deadline.Token;
        await using var first = Open(protocol);
        await using var second = Open(protocol);
        await Execute(first, "CREATE TABLE items(id INT,value INT,PRIMARY KEY(id))", cancellation: cancellation);
        await Execute(first, "INSERT INTO items(id,value) VALUES(1,10)", cancellation: cancellation);
        await using (var transaction = await first.BeginTransactionAsync(IsolationLevel.Serializable, cancellation))
        {
            Assert.Equal(IsolationLevel.Serializable, transaction.IsolationLevel);
            Assert.Equal(10L, await Scalar(first, "SELECT value FROM items WHERE id = 1", transaction, cancellation));
            await Execute(second, "UPDATE items SET value = 20 WHERE id = 1", cancellation: cancellation);
            Assert.Equal(20L, await Scalar(first, "SELECT value FROM items WHERE id = 1", transaction, cancellation));
            var error = await Assert.ThrowsAsync<SndbServerException>(() => transaction.CommitAsync(cancellation));
            Assert.Equal(TableConstraintException.SerializationConflict, error.Error);
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(cancellation));
        }
        await using (var transaction = await first.BeginTransactionAsync(IsolationLevel.Serializable, cancellation))
        {
            Assert.Equal(0L, await Scalar(first, "SELECT COUNT(*) FROM items WHERE id = 99", transaction, cancellation));
            await Execute(second, "INSERT INTO items(id,value) VALUES(99,10)", cancellation: cancellation);
            var error = await Assert.ThrowsAsync<SndbServerException>(() => transaction.CommitAsync(cancellation));
            Assert.Equal(TableConstraintException.SerializationConflict, error.Error);
        }
        await Execute(first, "DELETE FROM items WHERE id = 99", cancellation: cancellation);
        await Execute(first, "UPDATE items SET value = 10 WHERE id = 1", cancellation: cancellation);
        await Execute(first, "INSERT INTO items(id,value) VALUES(2,10)", cancellation: cancellation);
        await using (var a = await first.BeginTransactionAsync(IsolationLevel.Serializable, cancellation))
        await using (var b = await second.BeginTransactionAsync(IsolationLevel.Serializable, cancellation))
        {
            Assert.Equal(20L, await Scalar(first, "SELECT SUM(value) FROM items", a, cancellation));
            Assert.Equal(20L, await Scalar(second, "SELECT SUM(value) FROM items", b, cancellation));
            await Execute(first, "UPDATE items SET value = 0 WHERE id = 1", a, cancellation);
            await Execute(second, "UPDATE items SET value = 0 WHERE id = 2", b, cancellation);
            await a.CommitAsync(cancellation);
            var error = await Assert.ThrowsAsync<SndbServerException>(() => b.CommitAsync(cancellation));
            Assert.Equal(TableConstraintException.SerializationConflict, error.Error);
        }
        Assert.Equal(10L, await Scalar(first, "SELECT SUM(value) FROM items", cancellation: cancellation));
        await using (var transaction = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellation))
        {
            await Scalar(first, "SELECT value FROM items WHERE id = 1", transaction, cancellation);
            await Execute(second, "UPDATE items SET value = 5 WHERE id = 1", cancellation: cancellation);
            Assert.Equal(5L, await Scalar(first, "SELECT value FROM items WHERE id = 1", transaction, cancellation));
            await transaction.CommitAsync(cancellation);
        }
        var sessionRequests = _requests.Where(request => request.Path.Contains("/sql/transactions", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(sessionRequests);
        Assert.Contains(sessionRequests, request => request.Isolation == "serializable");
        Assert.All(sessionRequests, request => Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", request.Protocol));
        var commits = sessionRequests.Where(request => request.Path.EndsWith("/commit", StringComparison.Ordinal)).ToArray();
        Assert.Equal(5, commits.Length);
        Assert.DoesNotContain(sessionRequests, request => request.Path.EndsWith("/frame", StringComparison.Ordinal));
        using var http = Http();
        foreach (var commit in commits.Take(5))
        {
            string statusPath = commit.Path[..^"/commit".Length];
            using var response = await http.GetAsync(statusPath, cancellation);
            response.EnsureSuccessStatusCode();
            using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            string? state = status.RootElement.GetProperty("state").GetString();
            Assert.Contains(state, new[] { "commit", "rollback" });
        }
        Assert.DoesNotContain(_requests, request => request.Path.Contains("/sql/batch", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task BeginSerializable_Legacy404OrUnacknowledgedIsolation_RejectsWithoutDowngrade(int mode)
    {
        _legacyMode = mode;
        await using var connection = Open("rest");
        Assert.Throws<NotSupportedException>(() => connection.BeginTransaction(IsolationLevel.Serializable));
        Assert.Single(_requests, request => request.Path == $"/v1/db/{Database}/sql/transactions");
        Assert.DoesNotContain(_requests, request => request.Path.Contains("/sql/batch", StringComparison.Ordinal));
        Assert.DoesNotContain(_requests, request => request.Path.EndsWith("/commit", StringComparison.Ordinal));
        var rollbacks = _requests.Where(request => request.Path.EndsWith("/rollback", StringComparison.Ordinal)).ToArray();
        Assert.Equal(mode == 1 ? 0 : 1, rollbacks.Length);
        if (mode != 1)
        {
            using var http = Http();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await http.GetAsync(rollbacks[0].Path[..^"/rollback".Length], deadline.Token);
            using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            Assert.Equal("rollback", status.RootElement.GetProperty("state").GetString());
        }
    }

    private SndbConnection Open(string protocol)
    {
        string endpoint = protocol == "rest" ? _rest : _h2;
        var connection = new SndbConnection($"Data Source=sonnetdb+http://{new Uri(endpoint).Authority}/{Database};Token={Token};Protocol={protocol};Timeout=15");
        connection.Open();
        return connection;
    }
    private HttpClient Http()
    {
        var http = new HttpClient { BaseAddress = new Uri(_rest), Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return http;
    }
    private static async Task<int> Execute(SndbConnection connection, string sql, DbTransaction? transaction = null,
        CancellationToken cancellation = default)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return await command.ExecuteNonQueryAsync(cancellation);
    }
    private static async Task<long> Scalar(SndbConnection connection, string sql, DbTransaction? transaction = null,
        CancellationToken cancellation = default)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return Assert.IsType<long>(await command.ExecuteScalarAsync(cancellation));
    }
}
