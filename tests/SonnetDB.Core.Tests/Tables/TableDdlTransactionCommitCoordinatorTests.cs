using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>验证 DDL 提交协调器不会跨 durable decision 写相反补偿。</summary>
public sealed class TableDdlTransactionCommitCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-ddl-commit-" + Guid.NewGuid().ToString("N"));

    /// <summary>成功路径严格完成 undo/prepared/publish/committed/cleanup 并删除日志。</summary>
    [Fact]
    public void Commit_Success_CompletesOrderedPhasesAndRemovesJournal()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, TableDdlTransactionJournal.FileName);
        var backend = new Backend();
        var phases = new List<string>();
        TableDdlTransactionCommitCoordinator.PhaseTestHook = phases.Add;
        TableDdlTransactionCommitCoordinator.Commit(path, Create(), backend);
        Assert.Equal(
            ["undo-durable", "prepared-durable", "published", "committed-durable", "cleanup-complete"],
            phases);
        Assert.Equal(1, backend.Prepared);
        Assert.Equal(1, backend.Published);
        Assert.Equal(1, backend.Completed);
        Assert.Equal(0, backend.RolledBack);
        Assert.False(File.Exists(path));
    }

    /// <summary>publish 前后失败只在 prepared 决策下补偿；补偿成功后删除日志。</summary>
    [Fact]
    public void Commit_PublishFails_RollsBackPreparedAndRemovesJournal()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, TableDdlTransactionJournal.FileName);
        var backend = new Backend { FailPublish = true };
        Assert.Throws<InvalidOperationException>(() =>
            TableDdlTransactionCommitCoordinator.Commit(path, Create(), backend));
        Assert.Equal(1, backend.RolledBack);
        Assert.Equal(0, backend.Completed);
        Assert.False(File.Exists(path));
    }

    /// <summary>committed durable 后清理失败保留 committed 日志，恢复只重做清理而不回滚。</summary>
    [Fact]
    public void Commit_CleanupFails_PreservesCommittedDecisionForRecovery()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, TableDdlTransactionJournal.FileName);
        var backend = new Backend { FailCleanup = true };
        Assert.Throws<TableDdlTransactionRecoveryException>(() =>
            TableDdlTransactionCommitCoordinator.Commit(path, Create(), backend));
        Assert.Equal(TableDdlTransactionDecision.Committed,
            TableDdlTransactionJournal.Read(path)!.Decision);
        Assert.Equal(0, backend.RolledBack);

        backend.FailCleanup = false;
        Assert.Equal(TableDdlRecoveryResult.CompletedCommitted,
            TableDdlTransactionRecovery.Recover(path, backend));
        Assert.Equal(2, backend.Completed);
        Assert.False(File.Exists(path));
    }

    /// <summary>committed 写入返回异常时保留可判定日志，不在当前进程猜测回滚。</summary>
    [Fact]
    public void Commit_CommittedWriteHookFails_PreservesDurableDecisionWithoutRollback()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, TableDdlTransactionJournal.FileName);
        var backend = new Backend();
        TableDdlTransactionJournal.AfterDurableWriteTestHook = (_, decision) =>
        {
            if (decision == TableDdlTransactionDecision.Committed)
                throw new IOException("fixture");
        };
        Assert.Throws<TableDdlTransactionRecoveryException>(() =>
            TableDdlTransactionCommitCoordinator.Commit(path, Create(), backend));
        Assert.Equal(TableDdlTransactionDecision.Committed,
            TableDdlTransactionJournal.Read(path)!.Decision);
        Assert.Equal(0, backend.RolledBack);
        Assert.Equal(0, backend.Completed);
    }

    private static TableDdlTransactionJournal Create()
    {
        Guid owner = Guid.NewGuid();
        return new TableDdlTransactionJournal(
            owner,
            TableDdlTransactionDecision.Prepared,
            "ddl-transactions/workspace-" + owner.ToString("N"),
            "ddl-transactions/undo-" + owner.ToString("N"),
            17,
            [new TableDdlObjectIdentity("main", "items")]);
    }

    /// <summary>只回收本测试创建的唯一目录。</summary>
    public void Dispose()
    {
        TableDdlTransactionCommitCoordinator.PhaseTestHook = null;
        TableDdlTransactionJournal.AfterDurableWriteTestHook = null;
        string full = Path.GetFullPath(_root);
        string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Assert.StartsWith(temporary, full, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("sndb-ddl-commit-", Path.GetFileName(full), StringComparison.Ordinal);
        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
    }

    private sealed class Backend : ITableDdlTransactionCommitBackend
    {
        internal int Prepared { get; private set; }
        internal int Published { get; private set; }
        internal int RolledBack { get; private set; }
        internal int Completed { get; private set; }
        internal bool FailPublish { get; init; }
        internal bool FailCleanup { get; set; }

        public void PrepareUndo(TableDdlTransactionJournal journal, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Prepared++;
        }
        public void PublishPrepared(TableDdlTransactionJournal journal, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Published++;
            if (FailPublish) throw new InvalidOperationException("fixture");
        }
        public void RollbackPrepared(TableDdlTransactionJournal journal, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RolledBack++;
        }
        public void CompleteCommitted(TableDdlTransactionJournal journal, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Completed++;
            if (FailCleanup) throw new IOException("fixture");
        }
    }
}
