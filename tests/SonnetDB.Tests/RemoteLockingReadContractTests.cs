using System.Data;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.Sql;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>GH-Issue #190 的嵌入式和真实 REST ADO.NET 拒绝及乐观并发验收。</summary>
public sealed class RemoteLockingReadContractTests : IAsyncLifetime
{
    private const string DatabaseName = "locking_read";
    private const string AdminToken = "locking-read-admin";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-locking-ado-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private string _baseUrl = string.Empty;

    /// <summary>启动真实 REST 服务并创建测试数据库。</summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = Path.Combine(_root, "server"),
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
        });
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AdminToken);
        using var response = await http.PostAsync(
            "/v1/db",
            new StringContent("{\"name\":\"" + DatabaseName + "\"}",
                System.Text.Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    /// <summary>关闭服务并清理本测试拥有的目录。</summary>
    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("remote")]
    public async Task LockingRead_InTransactions_ThrowsBeforeReadAndLeavesOtherConnectionUsable(string mode)
    {
        using var first = Open(mode);
        using var second = Open(mode);
        Execute(first, "CREATE TABLE jobs (id INT, status STRING, version INT ROWVERSION, PRIMARY KEY (id))");
        Execute(first, "INSERT INTO jobs (id, status) VALUES (1, 'ready')");

        using (var transaction = first.BeginTransaction())
        {
            using var command = first.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM jobs WHERE id = 1 FOR UPDATE NOWAIT";
            var error = Assert.Throws<SqlParseException>(() => command.ExecuteReader());
            Assert.Equal(SqlErrorCodes.LockingReadUnsupported, error.Code);
            Assert.Equal(1L, ReadVersion(second));
            transaction.Rollback();
        }

        using (var transaction = first.BeginTransaction())
        {
            using var command = first.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM jobs WHERE id = 404 FOR UPDATE SKIP LOCKED";
            var error = await Assert.ThrowsAsync<SqlParseException>(
                async () => await command.ExecuteReaderAsync());
            Assert.Equal(SqlErrorCodes.LockingReadUnsupported, error.Code);
            transaction.Commit();
        }

        Assert.Equal(1, Execute(second,
            "UPDATE jobs SET status = 'running' WHERE id = 1 AND version = 1"));
        Assert.Equal(2L, ReadVersion(first));
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("remote")]
    public void RowVersion_TwoConnections_RejectsStaleUpdateAndAllowsRetry(string mode)
    {
        using var first = Open(mode);
        using var second = Open(mode);
        Execute(first, "CREATE TABLE jobs (id INT, status STRING, version INT ROWVERSION, PRIMARY KEY (id))");
        Execute(first, "INSERT INTO jobs (id, status) VALUES (1, 'ready')");
        Assert.Equal(1L, ReadVersion(first));
        Assert.Equal(1L, ReadVersion(second));

        Assert.Equal(1, Execute(first,
            "UPDATE jobs SET status = 'running' WHERE id = 1 AND version = 1"));
        var stale = Record.Exception(() => Execute(second,
            "UPDATE jobs SET status = 'stale' WHERE id = 1 AND version = 1"));
        Assert.NotNull(stale);
        string code = stale switch
        {
            TableConstraintException constraint => constraint.ErrorCode,
            SndbServerException remote => remote.Error,
            _ => throw new Xunit.Sdk.XunitException($"意外的并发错误类型：{stale.GetType().Name}"),
        };
        Assert.Equal(TableConstraintException.ConcurrencyConflict, code);

        Assert.Equal(2L, ReadVersion(second));
        Assert.Equal(1, Execute(second,
            "UPDATE jobs SET status = 'retried' WHERE id = 1 AND version = 2"));
        Assert.Equal(3L, ReadVersion(first));
        Assert.Equal(1, Execute(first, "DELETE FROM jobs WHERE id = 1 AND version = 3"));
        Assert.Equal(0, Execute(second, "UPDATE jobs SET status = 'missing' WHERE id = 404 AND version = 1"));
    }

    [Fact]
    public async Task RestSql_LockingRead_ReportsStableDomainCode()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AdminToken);
        using var response = await http.PostAsync(
            $"/v1/db/{DatabaseName}/sql",
            new StringContent(
                "{\"sql\":\"SELECT id FROM jobs FOR UPDATE SKIP LOCKED\"}",
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(SqlErrorCodes.LockingReadUnsupported,
            document.RootElement.GetProperty("code").GetString());
    }

    private SndbConnection Open(string mode)
    {
        string connectionString = mode == "embedded"
            ? $"Data Source={Path.Combine(_root, "embedded")}"
            : $"Data Source=sonnetdb+http://{new Uri(_baseUrl).Authority}/{DatabaseName};Token={AdminToken};Timeout=30";
        var connection = new SndbConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static int Execute(SndbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    private static long ReadVersion(SndbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM jobs WHERE id = 1";
        return (long)command.ExecuteScalar()!;
    }
}
