using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Data;
using SonnetDB.Hosting;
using Xunit;

namespace SonnetDB.Tests;

public sealed partial class RemoteAdoHttp2TransportTests
{
    /// <summary>普通等待实际发往服务端；请求取消后会话仍可继续，持有者结束后等待者正常获得行。</summary>
    [Theory]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task RowLocks_RemoteWaitAndCancellation_ReleaseRequestAndSession(string protocol, bool asynchronous)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var first = new SndbConnection(IntegralConnectionString(protocol, ""));
        await using var second = new SndbConnection(IntegralConnectionString(protocol, ""));
        await first.OpenAsync(deadline.Token); await second.OpenAsync(deadline.Token);
        await using var setup = first.CreateCommand();
        setup.CommandText = "CREATE TABLE wait_rows(id INT,PRIMARY KEY(id))";
        await setup.ExecuteNonQueryAsync(deadline.Token);
        setup.CommandText = "INSERT INTO wait_rows(id) VALUES(1)";
        await setup.ExecuteNonQueryAsync(deadline.Token);
        foreach (bool cancel in new[] { true, false })
        {
            await using var holder = await first.BeginTransactionAsync(deadline.Token);
            await using var waiter = await second.BeginTransactionAsync(deadline.Token);
            await using var hold = first.CreateCommand();
            hold.Transaction = holder;
            hold.CommandText = "SELECT id FROM wait_rows FOR UPDATE";
            Assert.Equal(1L, await hold.ExecuteScalarAsync(deadline.Token));
            await using var waiting = second.CreateCommand();
            waiting.Transaction = waiter;
            waiting.CommandText = "SELECT id FROM wait_rows FOR UPDATE";
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            using var registration = cancellation.Token.Register(waiting.Cancel);
            int before = _requests.Count;
            Task<object?> pending = asynchronous ? waiting.ExecuteScalarAsync(cancellation.Token)
                : Task.Run(waiting.ExecuteScalar, cancellation.Token);
            for (int attempt = 0; attempt < 100 && _requests.Count <= before; attempt++)
                await Task.Delay(20, deadline.Token);
            Assert.True(_requests.Count > before, "等待请求未到达实际 Server。");
            Assert.False(pending.IsCompleted, "持有者仍活动，普通锁定读不应已经完成。");
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(1L, await hold.ExecuteScalarAsync(deadline.Token));
            }
            await holder.RollbackAsync(deadline.Token);
            if (!cancel) Assert.Equal(1L, await pending.WaitAsync(TimeSpan.FromSeconds(10)));
            waiting.CommandText = "SELECT id FROM wait_rows FOR UPDATE NOWAIT";
            Assert.Equal(1L, await waiting.ExecuteScalarAsync(deadline.Token));
            await waiter.RollbackAsync(deadline.Token);
        }
    }

    /// <summary>真实 REST/h2c 事务请求保留 NOWAIT 错误码并在提交、回滚和释放后归还行锁。</summary>
    [Theory]
    [InlineData("rest", false, "commit")]
    [InlineData("rest", true, "commit")]
    [InlineData("rest", false, "rollback")]
    [InlineData("rest", true, "rollback")]
    [InlineData("rest", false, "dispose")]
    [InlineData("rest", true, "dispose")]
    [InlineData("rest", false, "close")]
    [InlineData("rest", true, "close")]
    [InlineData("frame-http2", false, "commit")]
    [InlineData("frame-http2", true, "commit")]
    [InlineData("frame-http2", false, "rollback")]
    [InlineData("frame-http2", true, "rollback")]
    [InlineData("frame-http2", false, "dispose")]
    [InlineData("frame-http2", true, "dispose")]
    [InlineData("frame-http2", false, "close")]
    [InlineData("frame-http2", true, "close")]
    public async Task RowLocks_RealTransactionSessions_PreserveConflictAndLifetime(string protocol, bool asynchronous, string completion)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var first = new SndbConnection(IntegralConnectionString(protocol, ""));
        await using var second = new SndbConnection(IntegralConnectionString(protocol, ""));
        await first.OpenAsync(deadline.Token);
        await second.OpenAsync(deadline.Token);
        await using (var setup = first.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE locked_rows(id INT,value INT,PRIMARY KEY(id))";
            await setup.ExecuteNonQueryAsync(deadline.Token);
            setup.CommandText = "INSERT INTO locked_rows(id,value) VALUES(1,10),(2,20),(3,30)";
            Assert.Equal(3, await setup.ExecuteNonQueryAsync(deadline.Token));
        }
        await using var holder = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted, deadline.Token);
        await using var contender = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted, deadline.Token);
        Assert.Equal(1L, await Lock(first, holder, "id=1"));
        int before = _requests.Count;
        using (var conflictDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var error = await Assert.ThrowsAsync<SndbServerException>(() => Lock(second, contender, "id IN (1,3)", conflictDeadline.Token));
            Assert.Equal("sql_lock_not_available", error.Error);
            Assert.Equal(System.Net.HttpStatusCode.Conflict, error.StatusCode);
            Assert.False(conflictDeadline.IsCancellationRequested);
        }
        Assert.Contains(_requests.Skip(before), request =>
            request.Protocol == (protocol == "rest" ? "HTTP/1.1" : "HTTP/2")
            && request.Path.StartsWith($"/v1/db/{DatabaseName}/sql/transactions/", StringComparison.Ordinal));
        Assert.Equal(2L, await Lock(second, contender, "id=2"));
        Assert.Null(await Lock(second, contender, "id=404"));
        switch (completion)
        {
            case "commit": await holder.CommitAsync(deadline.Token); break;
            case "rollback": await holder.RollbackAsync(deadline.Token); break;
            case "dispose": await holder.DisposeAsync(); break;
            case "close": await first.CloseAsync(); break;
        }
        Assert.Equal(1L, await Lock(second, contender, "id=1"));
        await contender.RollbackAsync(deadline.Token);

        async Task<object?> Lock(SndbConnection connection, System.Data.Common.DbTransaction transaction,
            string predicate, CancellationToken cancellation = default)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SndbTransaction)transaction;
            command.CommandText = $"SELECT id FROM locked_rows WHERE {predicate} ORDER BY id FOR UPDATE NOWAIT";
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellation);
            using var registration = linked.Token.Register(command.Cancel);
            return asynchronous ? await command.ExecuteScalarAsync(linked.Token)
                : await Task.Run(command.ExecuteScalar, linked.Token);
        }
    }

    /// <summary>服务端租约过期实际回滚锁定会话，之后另一连接立即取得原目标。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task RowLocks_ExpiredServerSession_ReleasesHeldRow(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = new SndbConnection(IntegralConnectionString(protocol, ""));
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE expired_lock(id INT,PRIMARY KEY(id))";
        await command.ExecuteNonQueryAsync(deadline.Token);
        command.CommandText = "INSERT INTO expired_lock(id) VALUES(1)";
        await command.ExecuteNonQueryAsync(deadline.Token);
        Assert.True(_app!.Services.GetRequiredService<TsdbRegistry>().TryGet(DatabaseName, out var database));
        var sessions = _app.Services.GetRequiredService<SqlTransactionSessions>();
        Assert.True(sessions.TryCreate(database, DatabaseName, "Bearer " + AdminToken, out var session));
        Assert.NotNull(session);
        using var client = new HttpClient
        {
            BaseAddress = new Uri(protocol == "rest" ? _http11Url : _frameH2Url),
            DefaultRequestVersion = protocol == "rest" ? HttpVersion.Version11 : HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        string route = $"/v1/db/{DatabaseName}/sql/transactions/{session.Id}";
        using (var response = await client.PostAsync(route + "/sql", new StringContent(
            "{\"sql\":\"SELECT id FROM expired_lock FOR UPDATE\"}", Encoding.UTF8, "application/json"), deadline.Token))
            response.EnsureSuccessStatusCode();
        await using var observer = await connection.BeginTransactionAsync(deadline.Token);
        command.Transaction = observer;
        command.CommandText = "SELECT id FROM expired_lock FOR UPDATE NOWAIT";
        Assert.Equal("sql_lock_not_available", (await Assert.ThrowsAsync<SndbServerException>(
            () => command.ExecuteScalarAsync(deadline.Token))).Error);
        session.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        using (var response = await client.GetAsync(route, deadline.Token))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(session.Transaction.IsCompleted);
        Assert.Equal(1L, await command.ExecuteScalarAsync(deadline.Token));
        await observer.RollbackAsync(deadline.Token);
    }

    /// <summary>提交乐观并发冲突终止服务端事务，并归还它此前取得的另一行锁。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task RowLocks_FailedRemoteCommit_ReleasesPriorHeldRow(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var first = new SndbConnection(IntegralConnectionString(protocol, ""));
        await using var second = new SndbConnection(IntegralConnectionString(protocol, ""));
        await first.OpenAsync(deadline.Token);
        await second.OpenAsync(deadline.Token);
        await using var command = first.CreateCommand();
        command.CommandText = "CREATE TABLE failed_lock(id INT,value INT,PRIMARY KEY(id))";
        await command.ExecuteNonQueryAsync(deadline.Token);
        command.CommandText = "INSERT INTO failed_lock(id,value) VALUES(1,10),(2,20)";
        await command.ExecuteNonQueryAsync(deadline.Token);
        await using var holder = await first.BeginTransactionAsync(deadline.Token);
        command.Transaction = holder;
        command.CommandText = "SELECT id FROM failed_lock WHERE id=1 FOR UPDATE";
        Assert.Equal(1L, await command.ExecuteScalarAsync(deadline.Token));
        command.CommandText = "UPDATE failed_lock SET value=21 WHERE id=2";
        await command.ExecuteNonQueryAsync(deadline.Token);
        await using var other = second.CreateCommand();
        other.CommandText = "UPDATE failed_lock SET value=22 WHERE id=2";
        await other.ExecuteNonQueryAsync(deadline.Token);
        await Assert.ThrowsAsync<SndbServerException>(() => holder.CommitAsync(deadline.Token));
        await using var observer = await second.BeginTransactionAsync(deadline.Token);
        other.Transaction = observer;
        other.CommandText = "SELECT id FROM failed_lock WHERE id=1 FOR UPDATE NOWAIT";
        Assert.Equal(1L, await other.ExecuteScalarAsync(deadline.Token));
        other.CommandText = "SELECT value FROM failed_lock WHERE id=2";
        Assert.Equal(22L, await other.ExecuteScalarAsync(deadline.Token));
        await observer.RollbackAsync(deadline.Token);
    }
}
