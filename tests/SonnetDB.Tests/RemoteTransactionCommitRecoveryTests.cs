using System.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.Hosting;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// 真实 Kestrel 回归：远程事务提交响应丢失时，客户端必须先读取服务器终态，
/// 且终态也不可读时不得把已提交的 UPSERT 当作新事务重放。
/// </summary>
public sealed class RemoteTransactionCommitRecoveryTests : IAsyncLifetime
{
    private const string AdminToken = "remote-commit-recovery-admin";
    private const string DatabaseName = "remote_commit_recovery";

    private WebApplication? _app;
    private string _baseUrl = string.Empty;
    private string? _dataRoot;
    private int _failureMode;
    private int _commitRequests;
    private int _statusReads;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(
            Path.GetTempPath(),
            "sndb-remote-commit-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [AdminToken] = ServerRoles.Admin,
            },
        };

        _app = TestServerHost.Build(options);
        _app.Use(async (context, next) =>
        {
            string path = context.Request.Path.Value ?? string.Empty;
            bool isTransactionEndpoint = path.StartsWith(
                $"/v1/db/{DatabaseName}/sql/transactions/",
                StringComparison.Ordinal);
            if (!isTransactionEndpoint)
            {
                await next();
                return;
            }

            bool isCommit = HttpMethods.IsPost(context.Request.Method)
                && path.EndsWith("/commit", StringComparison.Ordinal);
            bool isStatus = HttpMethods.IsGet(context.Request.Method);
            if (isCommit)
                Interlocked.Increment(ref _commitRequests);
            if (isStatus)
                Interlocked.Increment(ref _statusReads);

            int failureMode = Volatile.Read(ref _failureMode);
            bool dropCommitResponse = isCommit && failureMode is 1 or 2;
            bool dropStatusResponse = isStatus && failureMode == 2;
            if (!dropCommitResponse && !dropStatusResponse)
            {
                await next();
                return;
            }

            if (dropStatusResponse)
            {
                context.Abort();
                return;
            }

            Stream originalBody = context.Response.Body;
            await using var sink = new MemoryStream();
            context.Response.Body = sink;
            try
            {
                await next();
            }
            finally
            {
                context.Response.Body = originalBody;
                context.Abort();
            }
        });

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        using var http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            AdminToken);
        using var response = await http.PostAsync(
            "/v1/db",
            new StringContent(
                $"{{\"name\":\"{DatabaseName}\"}}",
                System.Text.Encoding.UTF8,
                "application/json"));
        response.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_dataRoot is not null && Directory.Exists(_dataRoot))
        {
            try
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
            catch
            {
                // best effort test cleanup
            }
        }
    }

    [Fact]
    public async Task Commit_ResponseLoss_ReadsTerminalStateAndReturnsSuccess()
    {
        await using var connection = OpenRemote();
        await PrepareTableAsync(connection, "commit_readback_success");
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        await StageUpsertAsync(connection, transaction, "commit_readback_success");
        Volatile.Write(ref _failureMode, 1);

        await transaction.CommitAsync();

        Assert.True(Volatile.Read(ref _statusReads) > 0);
        Assert.Equal(20L, await ReadValueAsync(connection, "commit_readback_success"));
    }

    [Fact]
    public async Task Commit_ResponseAndStatusLoss_ReportsUnknownWithoutReplay()
    {
        await using var connection = OpenRemote();
        await PrepareTableAsync(connection, "commit_readback_unknown");
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        await StageUpsertAsync(connection, transaction, "commit_readback_unknown");
        Volatile.Write(ref _failureMode, 2);

        var error = await Assert.ThrowsAsync<IOException>(() => transaction.CommitAsync());
        Assert.Contains("提交结果未知", error.Message, StringComparison.Ordinal);
        Assert.Contains("客户端不会自动重放", error.Message, StringComparison.Ordinal);
        Assert.True(Volatile.Read(ref _statusReads) > 0);

        int commitsAfterUnknown = Volatile.Read(ref _commitRequests);
        Assert.Equal(1, commitsAfterUnknown);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
        Assert.Equal(commitsAfterUnknown, Volatile.Read(ref _commitRequests));

        Assert.Equal(20L, await ReadValueAsync(connection, "commit_readback_unknown"));
    }

    private SndbConnection OpenRemote()
    {
        var connection = new SndbConnection(
            $"Data Source=sonnetdb+http://{new Uri(_baseUrl).Authority}/{DatabaseName};"
            + $"Token={AdminToken};Timeout=30");
        connection.Open();
        return connection;
    }

    private static async Task PrepareTableAsync(SndbConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE {table} (id INT, value INT, PRIMARY KEY (id))";
        Assert.Equal(0, await command.ExecuteNonQueryAsync());
        command.CommandText = $"INSERT INTO {table} (id, value) VALUES (1, 10)";
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task StageUpsertAsync(
        SndbConnection connection,
        SndbTransaction transaction,
        string table)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {table} (id, value) VALUES (1, 20) "
            + "ON CONFLICT (id) DO UPDATE SET value = excluded.value RETURNING value";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(20L, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync());
        Assert.Equal(1, reader.RecordsAffected);
    }

    private static async Task<long> ReadValueAsync(SndbConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT value FROM {table} WHERE id = 1";
        object? value = await command.ExecuteScalarAsync();
        return Assert.IsType<long>(value);
    }
}
