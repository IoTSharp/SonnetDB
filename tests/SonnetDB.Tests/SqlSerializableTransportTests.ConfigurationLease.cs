using System.Data;
using System.Data.Common;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Data;
using SonnetDB.Exceptions;
using SonnetDB.Hosting;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Tests;

public sealed partial class SqlSerializableTransportTests
{
    private const string GetUnownedGateways = "SELECT COUNT(*) FROM lease_gateways WHERE lease_id IS NULL AND lease_expiry IS NULL AND lease_owner IS NULL";

    /// <summary>验证真实事务中的影响行数、同值释放后的提交隔离和配套业务写入的回滚。</summary>
    [Theory]
    [InlineData("rest", IsolationLevel.ReadCommitted)]
    [InlineData("rest", IsolationLevel.Serializable)]
    [InlineData("frame-http2", IsolationLevel.ReadCommitted)]
    [InlineData("frame-http2", IsolationLevel.Serializable)]
    public async Task ConfigurationLease_AcquireAndReleaseRequiresSerializableForOptimisticCompetition(
        string protocol,
        IsolationLevel isolation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken cancellation = deadline.Token;
        await using var first = Open(protocol);
        await using var second = Open(protocol);
        await InitializeLeaseTables(first, cancellation);

        await using var winner = await first.BeginTransactionAsync(isolation, cancellation);
        await using var contender = await second.BeginTransactionAsync(isolation, cancellation);
        await StageOperation(first, winner, 1, "first");
        await StageOperation(second, contender, 2, "second");

        await winner.CommitAsync(cancellation);
        if (isolation == IsolationLevel.Serializable)
        {
            var error = await Assert.ThrowsAsync<SndbServerException>(() => contender.CommitAsync(cancellation));
            Assert.Equal(TableConstraintException.SerializationConflict, error.Error);
        }
        else
        {
            await contender.CommitAsync(cancellation);
        }

        Assert.Equal(isolation == IsolationLevel.Serializable ? 1L : 2L,
            await Scalar(first, "SELECT COUNT(*) FROM lease_operations", cancellation: cancellation));
        Assert.Equal(isolation == IsolationLevel.Serializable ? 1L : 2L,
            await Scalar(first, "SELECT COUNT(*) FROM lease_audit", cancellation: cancellation));
        Assert.Equal(1L, await Scalar(first, GetUnownedGateways, cancellation: cancellation));
        Assert.Equal(1L, await Scalar(first, "SELECT COUNT(*) FROM lease_audit WHERE operation_id = 1",
            cancellation: cancellation));

        string prefix = $"/v1/db/{Database}/sql/transactions/";
        var transactionRequests = _requests.Where(request => request.Path.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, transactionRequests.Count(request => request.Path.EndsWith("/commit", StringComparison.Ordinal)));
        Assert.All(transactionRequests, request => Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", request.Protocol));
        Assert.DoesNotContain(_requests, request => request.Path == $"/v1/db/{Database}/sql/batch");

        async Task StageOperation(SndbConnection connection, DbTransaction transaction, int operationId, string owner)
        {
            Assert.Equal(1, await AcquireGateway(connection, transaction, owner, cancellation));
            Assert.Equal(0, await AcquireGateway(connection, transaction, "other", cancellation));
            Assert.Equal(0, await ReleaseGateway(connection, transaction, "other", cancellation));
            Assert.Equal(1, await Execute(connection, $"INSERT INTO lease_operations (operation_id) VALUES ({operationId})", transaction, cancellation));
            Assert.Equal(1, await Execute(connection, $"INSERT INTO lease_audit (operation_id) VALUES ({operationId})", transaction, cancellation));
            Assert.Equal(1, await ReleaseGateway(connection, transaction, owner, cancellation));
            Assert.Equal(0, await ReleaseGateway(connection, transaction, owner, cancellation));
        }
    }

    /// <summary>验证锁定读及时拒绝竞争者，且回滚归还原目标并清除业务写入。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task ConfigurationLease_LockingReadRejectsContenderUntilRollback(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken cancellation = deadline.Token;
        await using var first = Open(protocol);
        await using var second = Open(protocol);
        await InitializeLeaseTables(first, cancellation);

        const string lockGateway = "SELECT id FROM lease_gateways WHERE id = 1 AND NOT deleted FOR UPDATE NOWAIT";
        await using var holder = await first.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
        Assert.Equal(1L, await Scalar(first, lockGateway, holder, cancellation));
        Assert.Equal(1, await AcquireGateway(first, holder, "holder", cancellation));
        Assert.Equal(1, await Execute(first, "INSERT INTO lease_operations (operation_id) VALUES (1)", holder, cancellation));
        Assert.Equal(1L, await Scalar(second, GetUnownedGateways, cancellation: cancellation));

        await using (var contender = await second.BeginTransactionAsync(IsolationLevel.Serializable, cancellation))
        {
            var error = await Assert.ThrowsAsync<SndbServerException>(() => Scalar(second, lockGateway, contender, cancellation));
            Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
            Assert.Equal("sql_lock_not_available", error.Error);
            await contender.RollbackAsync(cancellation);
        }

        await holder.RollbackAsync(cancellation);
        Assert.Equal(1L, await Scalar(second, GetUnownedGateways, cancellation: cancellation));
        Assert.Equal(0L, await Scalar(second, "SELECT COUNT(*) FROM lease_operations", cancellation: cancellation));
        await using var retry = await second.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
        Assert.Equal(1L, await Scalar(second, lockGateway, retry, cancellation));
        Assert.Equal(1, await AcquireGateway(second, retry, "retry", cancellation));
        Assert.Equal(1, await ReleaseGateway(second, retry, "retry", cancellation));
        await retry.CommitAsync(cancellation);
    }

    /// <summary>批次后续目标忙时回滚已获取租约和业务写入；到期边界可重新获取。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task ConfigurationLease_MultiTargetRollbackAndExpiry(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken cancellation = deadline.Token;
        await using var connection = Open(protocol);
        await using var observer = Open(protocol);
        await InitializeLeaseTables(connection, cancellation);
        Assert.Equal(1, await Execute(connection,
            "INSERT INTO lease_gateways (id, deleted, lease_id, lease_expiry, lease_owner) VALUES (2, FALSE, 'busy', CAST(639313920001234568 AS DATETIME2), 'busy')",
            cancellation: cancellation));

        await using (var batch = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellation))
        {
            Assert.Equal(1, await AcquireGateway(connection, batch, "batch", cancellation));
            Assert.Equal(1, await Execute(connection, "INSERT INTO lease_operations (operation_id) VALUES (1)", batch, cancellation));
            Assert.Equal(0, await AcquireGateway(connection, batch, "batch", cancellation, gatewayId: 2));
            await batch.RollbackAsync(cancellation);
        }
        Assert.Equal(1L, await Scalar(observer, GetUnownedGateways, cancellation: cancellation));
        Assert.Equal(0L, await Scalar(observer, "SELECT COUNT(*) FROM lease_operations", cancellation: cancellation));
        Assert.Equal(1, await Execute(connection,
            "UPDATE lease_gateways SET lease_expiry = CAST(639313920001234567 AS DATETIME2) WHERE id = 2 AND lease_id = 'busy'",
            cancellation: cancellation));

        await using var retry = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
        Assert.Equal(1, await AcquireGateway(connection, retry, "retry", cancellation));
        Assert.Equal(1, await AcquireGateway(connection, retry, "retry", cancellation, gatewayId: 2));
        Assert.Equal(2, await Execute(connection,
            "UPDATE lease_gateways SET lease_id = NULL, lease_expiry = NULL, lease_owner = NULL "
            + "WHERE id IN (1, 2) AND lease_id = 'retry'", retry, cancellation));
        await retry.CommitAsync(cancellation);
        Assert.Equal(2L, await Scalar(observer, GetUnownedGateways, cancellation: cancellation));
    }

    /// <summary>已持久化后的故障不能被远程会话报告为回滚，重开数据库后可按业务键确认完整结果。</summary>
    [Fact]
    public async Task ConfigurationLease_MultiTableStorageRecoveryPreservesUnknownTerminalOutcome()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken cancellation = deadline.Token;
        string statusPath;
        await using (var connection = Open("rest"))
        {
            await InitializeLeaseTables(connection, cancellation);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
            Assert.Equal(1, await AcquireGateway(connection, transaction, "holder", cancellation));
            Assert.Equal(1, await Execute(connection, "INSERT INTO lease_operations (operation_id) VALUES (1)", transaction, cancellation));
            Assert.Equal(1, await Execute(connection, "INSERT INTO lease_audit (operation_id) VALUES (1)", transaction, cancellation));
            Assert.Equal(1, await ReleaseGateway(connection, transaction, "holder", cancellation));

            var registry = _app!.Services.GetRequiredService<TsdbRegistry>();
            Assert.True(registry.TryGet(Database, out var database));
            PropertyInfo injectFailure = typeof(TableManager).GetProperty(
                "ApplyTransactionAfterCompleteTestHook", BindingFlags.Instance | BindingFlags.NonPublic)!;
            injectFailure.SetValue(database.Tables, (Action)(() => throw new IOException("injected post-commit I/O failure")));
            try
            {
                var error = await Assert.ThrowsAsync<IOException>(() => transaction.CommitAsync(cancellation));
                Assert.Contains("提交结果未知", error.Message, StringComparison.Ordinal);
                var errors = Assert.IsType<AggregateException>(error.InnerException);
                Assert.Contains(errors.InnerExceptions, item => item is SndbServerException serverError
                    && serverError.Error == RoutineErrorCodes.CommitUnknown
                    && serverError.StatusCode == HttpStatusCode.ServiceUnavailable);
                Assert.True(database.Tables.RequiresTransactionRecovery);
            }
            finally
            {
                injectFailure.SetValue(database.Tables, null);
            }

            var commit = Assert.Single(_requests, request => request.Path.EndsWith("/commit", StringComparison.Ordinal));
            statusPath = commit.Path[..^"/commit".Length];
            using var http = Http();
            using var status = await http.GetAsync(statusPath, cancellation);
            status.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await status.Content.ReadAsStringAsync(cancellation));
            Assert.Equal("unknown", body.RootElement.GetProperty("state").GetString());

            using var rollback = await http.PostAsync(statusPath + "/rollback", null, cancellation);
            Assert.Equal(HttpStatusCode.Conflict, rollback.StatusCode);
        }

        await StopServerAsync(cancellation);
        await StartServerAsync(cancellation);
        using (var http = Http())
        using (var status = await http.GetAsync(statusPath, cancellation))
            Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
        await using var observer = Open("rest");
        Assert.Equal(1L, await Scalar(observer, "SELECT COUNT(*) FROM lease_operations WHERE operation_id = 1", cancellation: cancellation));
        Assert.Equal(1L, await Scalar(observer, "SELECT COUNT(*) FROM lease_audit WHERE operation_id = 1", cancellation: cancellation));
        Assert.Equal(1L, await Scalar(observer, GetUnownedGateways, cancellation: cancellation));
    }

    private static async Task InitializeLeaseTables(SndbConnection connection, CancellationToken cancellation)
    {
        await Execute(connection, "CREATE TABLE lease_gateways (id INT, deleted BOOL, lease_id STRING, lease_expiry DATETIME2, lease_owner STRING, PRIMARY KEY(id))",
            cancellation: cancellation);
        await Execute(connection, "CREATE TABLE lease_operations (operation_id INT, PRIMARY KEY(operation_id))", cancellation: cancellation);
        await Execute(connection, "CREATE TABLE lease_audit (operation_id INT, PRIMARY KEY(operation_id))", cancellation: cancellation);
        Assert.Equal(1, await Execute(connection, "INSERT INTO lease_gateways (id, deleted) VALUES (1, FALSE)", cancellation: cancellation));
    }

    private static Task<int> AcquireGateway(
        SndbConnection connection, DbTransaction transaction, string owner, CancellationToken cancellation, int gatewayId = 1)
        => Execute(connection,
            $"UPDATE lease_gateways SET lease_id = '{owner}', lease_expiry = CAST(639313920001234568 AS DATETIME2), lease_owner = '{owner}' "
            + $"WHERE id = {gatewayId} AND NOT deleted AND (lease_id IS NULL OR lease_expiry IS NULL OR lease_expiry <= CAST(639313920001234567 AS DATETIME2))",
            transaction, cancellation);

    private static Task<int> ReleaseGateway(SndbConnection connection, DbTransaction transaction, string owner, CancellationToken cancellation)
        => Execute(connection,
            $"UPDATE lease_gateways SET lease_id = NULL, lease_expiry = NULL, lease_owner = NULL WHERE id = 1 AND lease_id = '{owner}'",
            transaction, cancellation);
}
