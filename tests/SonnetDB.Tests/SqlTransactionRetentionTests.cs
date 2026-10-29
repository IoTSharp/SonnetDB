using Microsoft.Extensions.Logging.Abstractions;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Kv;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>验证高频短事务的终态回执不会耗尽准入配额，同时保留回滚预算和未过期终态。</summary>
public sealed class SqlTransactionRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-txn-retention-" + Guid.NewGuid().ToString("N"));

    /// <summary>8192 个已完成过期会话必须在一次有界清理后释放，不受八项回滚预算限制。</summary>
    [Fact]
    public void CompletedExpiredReceiptsDoNotExhaustTheRetainedCapacity()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var db = OpenDatabase();
        using var sessions = new SqlTransactionSessions(NullLogger<SqlTransactionSessions>.Instance);
        for (int index = 0; index < 8192; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            Assert.True(sessions.TryCreate(db, "test", "owner", out var session));
            SqlExecutor.ExecuteStatement(db, "test", new RollbackTransactionStatement(), null, session!.Transaction);
            sessions.Complete(session, "rolled_back");
            session.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        }
        Assert.Equal(0, sessions.ActiveCount);
        Assert.Equal(8192, sessions.RetainedCount);
        Assert.False(sessions.TryCreate(db, "test", "owner", out _));
        sessions.Sweep();
        Assert.Equal(0, sessions.RetainedCount);
        Assert.True(sessions.TryCreate(db, "test", "owner", out _));
    }

    /// <summary>活动事务到期回滚仍每轮最多八项，终态清理不会提高活动并发上限。</summary>
    [Fact]
    public void ExpiredActiveTransactionsRetainTheRollbackBudget()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var db = OpenDatabase();
        using var sessions = new SqlTransactionSessions(NullLogger<SqlTransactionSessions>.Instance);
        for (int index = 0; index < 128; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            Assert.True(sessions.TryCreate(db, "test", "owner", out var session));
            session!.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        }
        Assert.False(sessions.TryCreate(db, "test", "owner", out _));
        sessions.Sweep();
        Assert.Equal(120, sessions.ActiveCount);
        Assert.Equal(120, sessions.RetainedCount);
    }

    /// <summary>仍在保留期内或被请求持有的终态不能提前移除，提交结果查询合同保持。</summary>
    [Fact]
    public async Task UnexpiredAndInUseTerminalReceiptsRemainAvailable()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var db = OpenDatabase();
        using var sessions = new SqlTransactionSessions(NullLogger<SqlTransactionSessions>.Instance);
        Assert.True(sessions.TryCreate(db, "test", "owner", out var terminal));
        SqlExecutor.ExecuteStatement(db, "test", new RollbackTransactionStatement(), null, terminal!.Transaction);
        sessions.Complete(terminal, "rolled_back");
        sessions.Sweep();
        Assert.Equal(1, sessions.RetainedCount);
        var acquired = await sessions.AcquireAsync(terminal.Id, db, "test", "owner", deadline.Token);
        Assert.Same(terminal, acquired);
        try
        {
            terminal.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            sessions.Sweep();
            Assert.Equal(1, sessions.RetainedCount);
        }
        finally { terminal.Gate.Release(); }
        sessions.Sweep();
        Assert.Equal(0, sessions.RetainedCount);
    }

    /// <summary>建立关闭后台维护的独占测试数据库，不启动网络服务或业务任务。</summary>
    private Tsdb OpenDatabase() => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        Kv = KvOptions.Default with { ExpirerEnabled = false, CleanupEnabled = false, SyncWalOnEveryWrite = false }
    });

    /// <summary>只回收本测试创建且已关闭的固定前缀目录。</summary>
    public void Dispose()
    {
        var path = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(path) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
            && Path.GetFileName(path).StartsWith("sonnetdb-txn-retention-", StringComparison.Ordinal)
            && Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
