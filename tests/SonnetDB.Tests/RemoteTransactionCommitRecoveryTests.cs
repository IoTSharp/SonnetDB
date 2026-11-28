using System.Data;
using System.Data.Common;
using System.Net;
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
    private string? _lastCommitPath;
    private TaskCompletionSource? _beginRequestSeen;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(
            Path.GetTempPath(),
            "sndb-remote-commit-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await StartServerAsync(deadline.Token);
        using var http = CreateHttpClient();
        using var response = await http.PostAsync(
            "/v1/db",
            new StringContent(
                $"{{\"name\":\"{DatabaseName}\"}}",
                System.Text.Encoding.UTF8,
                "application/json"),
            deadline.Token);
        response.EnsureSuccessStatusCode();
    }

    private async Task StartServerAsync(CancellationToken cancellationToken)
    {
        var options = new ServerOptions
        {
            DataRoot = _dataRoot!,
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
            if (path == $"/v1/db/{DatabaseName}/sql/transactions"
                && HttpMethods.IsPost(context.Request.Method)
                && Volatile.Read(ref _beginRequestSeen) is { } beginRequestSeen)
            {
                beginRequestSeen.TrySetResult();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, context.RequestAborted);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, wait.Token);
                }
                catch (OperationCanceledException) when (wait.IsCancellationRequested)
                {
                }
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

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
            {
                Volatile.Write(ref _lastCommitPath, path);
                Interlocked.Increment(ref _commitRequests);
            }
            if (isStatus)
                Interlocked.Increment(ref _statusReads);

            int failureMode = Volatile.Read(ref _failureMode);
            bool dropCommitResponse = isCommit && failureMode is 1 or 2;
            bool dropStatusResponse = isStatus && failureMode == 2;
            bool corruptCommitResponse = isCommit && failureMode == 3;
            if (!dropCommitResponse && !dropStatusResponse && !corruptCommitResponse)
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
                if (dropCommitResponse)
                    context.Abort();
            }

            if (corruptCommitResponse)
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentLength = null;
                await context.Response.WriteAsync("{\"type\":\"begin\"}\n", context.RequestAborted);
            }
        });

        await _app.StartAsync(cancellationToken);
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();
    }

    private HttpClient CreateHttpClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            AdminToken);
        return http;
    }

    public async Task DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await StopServerAsync(deadline.Token);

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

    private async Task StopServerAsync(CancellationToken cancellationToken)
    {
        if (_app is not { } app)
            return;
        try
        {
            await app.StopAsync(cancellationToken);
        }
        finally
        {
            await app.DisposeAsync();
            _app = null;
        }
    }

    [Fact]
    public async Task Begin_AdoBaseAsyncCancelsOutstandingSessionRequest()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using DbConnection connection = OpenRemote();
        var requestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _beginRequestSeen, requestSeen);

        Task<DbTransaction> pending = connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellation.Token)
            .AsTask();
        await requestSeen.Task.WaitAsync(deadline.Token);
        Assert.False(pending.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.BeginTransactionAsync(IsolationLevel.Serializable, deadline.Token).AsTask());
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(3), deadline.Token));

        Volatile.Write(ref _beginRequestSeen, null);
        await using var next = await connection.BeginTransactionAsync(IsolationLevel.Serializable, deadline.Token);
        await next.RollbackAsync(deadline.Token);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Commit_UnusableResponse_ReadsTerminalStateAndReturnsSuccess(int failureMode)
    {
        await using var connection = OpenRemote();
        await PrepareTableAsync(connection, "commit_readback_success");
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        await StageUpsertAsync(connection, transaction, "commit_readback_success");
        Volatile.Write(ref _failureMode, failureMode);

        await transaction.CommitAsync();

        Assert.True(Volatile.Read(ref _statusReads) > 0);
        Assert.Equal(20L, await ReadValueAsync(connection, "commit_readback_success"));
    }

    /// <summary>提交前已取消时保留原会话供回滚，并验证服务器未收到提交请求。</summary>
    [Fact]
    public async Task Commit_PreCancelled_CanStillRollBackTheActualSession()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        await using var connection = OpenRemote();
        await PrepareTableAsync(connection, "commit_cancelled", token);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await StageUpsertAsync(connection, transaction, "commit_cancelled", token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transaction.CommitAsync(new CancellationToken(canceled: true)));
        Assert.Equal(0, Volatile.Read(ref _commitRequests));
        await transaction.RollbackAsync(token);
        Assert.Equal(10L, await ReadValueAsync(connection, "commit_cancelled", token));
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

    /// <summary>提交响应和状态均丢失后，通过新服务实例确认两张业务表的持久结果。</summary>
    [Fact]
    public async Task Commit_UnknownOutcome_ReconcilesBothWritesAfterServerRestart()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken cancellation = deadline.Token;
        await using (var connection = OpenRemote())
        {
            await PrepareTableAsync(connection, "restart_config_version", cancellation);
            await PrepareTableAsync(connection, "restart_config_audit", cancellation);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
            await StageUpsertAsync(connection, transaction, "restart_config_version", cancellation);
            await StageUpsertAsync(connection, transaction, "restart_config_audit", cancellation);
            Volatile.Write(ref _failureMode, 2);

            var error = await Assert.ThrowsAsync<IOException>(() => transaction.CommitAsync(cancellation));
            Assert.Contains("提交结果未知", error.Message, StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref _commitRequests));
            Assert.True(Volatile.Read(ref _statusReads) > 0);
        }

        string commitPath = Assert.IsType<string>(Volatile.Read(ref _lastCommitPath));
        Assert.EndsWith("/commit", commitPath, StringComparison.Ordinal);
        await StopServerAsync(cancellation);
        Volatile.Write(ref _failureMode, 0);
        await StartServerAsync(cancellation);

        using var http = CreateHttpClient();
        using var response = await http.GetAsync(commitPath[..^"/commit".Length], cancellation);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var observer = OpenRemote();
        Assert.Equal(20L, await ReadValueAsync(observer, "restart_config_version", cancellation));
        Assert.Equal(20L, await ReadValueAsync(observer, "restart_config_audit", cancellation));
        Assert.Equal(1, Volatile.Read(ref _commitRequests));
    }

    private SndbConnection OpenRemote()
    {
        var connection = new SndbConnection(
            $"Data Source=sonnetdb+http://{new Uri(_baseUrl).Authority}/{DatabaseName};"
            + $"Token={AdminToken};Timeout=30");
        connection.Open();
        return connection;
    }

    private static async Task PrepareTableAsync(SndbConnection connection, string table, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE {table} (id INT, value INT, PRIMARY KEY (id))";
        Assert.Equal(0, await command.ExecuteNonQueryAsync(cancellationToken));
        command.CommandText = $"INSERT INTO {table} (id, value) VALUES (1, 10)";
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }

    private static async Task StageUpsertAsync(
        SndbConnection connection,
        SndbTransaction transaction,
        string table,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {table} (id, value) VALUES (1, 20) "
            + "ON CONFLICT (id) DO UPDATE SET value = excluded.value RETURNING value";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal(20L, reader.GetInt64(0));
        Assert.False(await reader.ReadAsync(cancellationToken));
        Assert.Equal(1, reader.RecordsAffected);
    }

    private static async Task<long> ReadValueAsync(SndbConnection connection, string table, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT value FROM {table} WHERE id = 1";
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return Assert.IsType<long>(value);
    }
}
