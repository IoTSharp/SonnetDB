using Microsoft.Extensions.Logging.Abstractions;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>验证服务端连接会话的容量、准入、事务排空和失败清理边界。</summary>
public sealed class SqlConnectionSessionsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-server-session-" + Guid.NewGuid().ToString("N"));

    /// <summary>构造会话失败不会泄漏容量计数。</summary>
    [Fact]
    public void TryCreate_ContextFactoryFails_DoesNotLeakCapacity()
    {
        using var transactions = new SqlTransactionSessions(NullLogger<SqlTransactionSessions>.Instance);
        using var sessions = new SqlConnectionSessions(
            NullLogger<SqlConnectionSessions>.Instance,
            transactions,
            TimeSpan.FromMilliseconds(50),
            static _ => throw new IOException("fixture"));
        using var database = Open();

        Assert.Throws<IOException>(() => sessions.TryCreate(database, "db", "Bearer a", out _));
        Assert.Equal(0, sessions.ActiveCount);
        Assert.Equal(0, sessions.CleanupCount);
    }

    /// <summary>会话 gate 的竞争等待有独立墙钟，不会无限挂起。</summary>
    [Fact]
    public async Task AcquireAsync_GateHeld_TimesOutWithinConfiguredBudget()
    {
        using var transactions = new SqlTransactionSessions(NullLogger<SqlTransactionSessions>.Instance);
        using var sessions = new SqlConnectionSessions(
            NullLogger<SqlConnectionSessions>.Instance,
            transactions,
            TimeSpan.FromMilliseconds(30),
            static database => new SqlSessionContext(database));
        using var database = Open();
        Assert.True(sessions.TryCreate(database, "db", "Bearer a", out var created));
        using var owner = await sessions.AcquireAsync(created!.Id, database, "db", "Bearer a", CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await sessions.AcquireAsync(created.Id, database, "db", "Bearer a", CancellationToken.None));
        Assert.Equal(1, sessions.ActiveCount);
    }

    /// <summary>显式关闭先回滚绑定事务，再释放连接临时目录。</summary>
    [Fact]
    public async Task Close_WithBoundTransaction_RollsBackBeforeSessionCleanup()
    {
        using var transactions = new SqlTransactionSessions(NullLogger<SqlTransactionSessions>.Instance);
        using var sessions = new SqlConnectionSessions(
            NullLogger<SqlConnectionSessions>.Instance, transactions);
        using var database = Open();
        Assert.True(sessions.TryCreate(database, "db", "Bearer a", out var created));
        using var connection = await sessions.AcquireAsync(
            created!.Id, database, "db", "Bearer a", CancellationToken.None);
        Assert.True(transactions.TryCreate(database, "db", "Bearer a", out var transaction,
            connectionSessionId: created.Id));

        _ = SqlExecutor.ExecuteStatement(
            database,
            "db",
            SqlParser.Parse("CREATE TEMP TABLE staged(Id INT,PRIMARY KEY(Id))"),
            controlPlane: null,
            transaction!.Transaction,
            new SqlExecutionOptions { SessionContext = connection!.Context });

        sessions.Close(connection);

        Assert.True(transaction.Transaction.IsCompleted);
        Assert.False(transaction.Transaction.WasCommitted);
        Assert.Equal(0, sessions.ActiveCount);
    }

    /// <summary>临时目录首次清理失败会保留归属对象并由后续有界 sweep 重试。</summary>
    [Fact]
    public async Task Close_CleanupFails_QueuesOwnedRetryUntilMarkerRestored()
    {
        using var transactions = new SqlTransactionSessions(NullLogger<SqlTransactionSessions>.Instance);
        using var sessions = new SqlConnectionSessions(
            NullLogger<SqlConnectionSessions>.Instance, transactions);
        using var database = Open();
        Assert.True(sessions.TryCreate(database, "db", "Bearer a", out var created));
        using var connection = await sessions.AcquireAsync(
            created!.Id, database, "db", "Bearer a", CancellationToken.None);
        _ = SqlExecutor.ExecuteStatement(
            database,
            "db",
            SqlParser.Parse("CREATE TEMP TABLE staged(Id INT,PRIMARY KEY(Id))"),
            controlPlane: null,
            transaction: null,
            new SqlExecutionOptions { SessionContext = connection!.Context });
        string directory = Path.Combine(
            Path.GetTempPath(), "sonnetdb-sql-session-" + connection.Context.Id.ToString("N"));
        string marker = Path.Combine(directory, "session.owner");
        string owner = File.ReadAllText(marker);
        File.WriteAllText(marker, "foreign");

        var failure = Assert.Throws<SqlConnectionSessionCleanupException>(() => sessions.Close(connection));
        Assert.IsType<IOException>(failure.InnerException);
        Assert.Equal(0, sessions.ActiveCount);
        Assert.Equal(1, sessions.CleanupCount);
        Assert.True(Directory.Exists(directory));

        File.WriteAllText(marker, owner);
        connection.Dispose();
        sessions.SweepExpiredForTests();
        Assert.Equal(0, sessions.CleanupCount);
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>sweep 每轮只处理固定数量，重复调用最终回收全部过期会话。</summary>
    [Fact]
    public void Sweep_ManyExpiredSessions_IsBoundedPerRoundAndEventuallyCompletes()
    {
        using var transactions = new SqlTransactionSessions(NullLogger<SqlTransactionSessions>.Instance);
        using var sessions = new SqlConnectionSessions(
            NullLogger<SqlConnectionSessions>.Instance, transactions);
        using var database = Open();
        var created = new List<SqlConnectionSessions.Session>();
        for (int index = 0; index < 10; index++)
        {
            Assert.True(sessions.TryCreate(database, "db", "Bearer a", out var session));
            session!.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            created.Add(session);
        }

        sessions.SweepExpiredForTests();
        Assert.Equal(9, sessions.ActiveCount);
        for (int round = 0; round < 12 && sessions.ActiveCount != 0; round++)
            sessions.SweepExpiredForTests();
        Assert.Equal(0, sessions.ActiveCount);
    }

    /// <summary>Dispose 与有界创建竞态结束后不允许新增，也不遗留容量计数。</summary>
    [Fact]
    public async Task Dispose_RacingBoundedCreates_LeavesNoActiveSessions()
    {
        using var transactions = new SqlTransactionSessions(NullLogger<SqlTransactionSessions>.Instance);
        var sessions = new SqlConnectionSessions(
            NullLogger<SqlConnectionSessions>.Instance, transactions);
        using var database = Open();
        using var start = new ManualResetEventSlim(false);
        Task creator = Task.Run(() =>
        {
            start.Wait(TimeSpan.FromSeconds(2));
            for (int attempt = 0; attempt < 64; attempt++)
            {
                try { _ = sessions.TryCreate(database, "db", "Bearer a", out _); }
                catch (ObjectDisposedException) { break; }
            }
        });
        Task disposer = Task.Run(() =>
        {
            start.Wait(TimeSpan.FromSeconds(2));
            sessions.Dispose();
        });
        start.Set();
        await Task.WhenAll(creator, disposer).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, sessions.ActiveCount);
        Assert.Throws<ObjectDisposedException>(() =>
            sessions.TryCreate(database, "db", "Bearer a", out _));
    }

    /// <summary>cleanup pending 继续占用总槽位，真正清理成功后才允许新会话。</summary>
    [Fact]
    public async Task CleanupPending_CountsTowardRetainedLimitUntilRetrySucceeds()
    {
        using var transactions = new SqlTransactionSessions(NullLogger<SqlTransactionSessions>.Instance);
        using var sessions = new SqlConnectionSessions(
            NullLogger<SqlConnectionSessions>.Instance, transactions);
        using var database = Open();
        var pending = new List<(string Marker, string Owner)>();
        for (int index = 0; index < 3; index++)
        {
            Assert.True(sessions.TryCreate(database, "db", "Bearer a", out var created));
            var lease = await sessions.AcquireAsync(
                created!.Id, database, "db", "Bearer a", CancellationToken.None);
            Assert.NotNull(lease);
            _ = SqlExecutor.ExecuteStatement(
                database,
                "db",
                SqlParser.Parse($"CREATE TEMP TABLE staged_{index}(Id INT,PRIMARY KEY(Id))"),
                controlPlane: null,
                transaction: null,
                new SqlExecutionOptions { SessionContext = lease!.Context });
            string directory = Path.Combine(
                Path.GetTempPath(), "sonnetdb-sql-session-" + lease.Context.Id.ToString("N"));
            string marker = Path.Combine(directory, "session.owner");
            string owner = File.ReadAllText(marker);
            File.WriteAllText(marker, "foreign");
            Assert.Throws<SqlConnectionSessionCleanupException>(() => sessions.Close(lease));
            lease.Dispose();
            pending.Add((marker, owner));
        }
        Assert.Equal(3, sessions.CleanupCount);
        Assert.Equal(3, sessions.RetainedCount);

        for (int index = 0; index < 125; index++)
            Assert.True(sessions.TryCreate(database, "db", "Bearer a", out _));
        Assert.Equal(125, sessions.ActiveCount);
        Assert.Equal(128, sessions.RetainedCount);
        Assert.False(sessions.TryCreate(database, "db", "Bearer a", out _));

        foreach ((string marker, string owner) in pending)
            File.WriteAllText(marker, owner);
        for (int round = 0; round < pending.Count; round++)
            sessions.SweepExpiredForTests();
        Assert.Equal(0, sessions.CleanupCount);
        Assert.Equal(125, sessions.RetainedCount);
        for (int index = 0; index < 3; index++)
            Assert.True(sessions.TryCreate(database, "db", "Bearer a", out _));
        Assert.Equal(128, sessions.RetainedCount);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    /// <summary>只回收本测试创建的唯一目录。</summary>
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Assert.StartsWith(temporary, full, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("sndb-server-session-", Path.GetFileName(full), StringComparison.Ordinal);
        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
    }
}
