using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>验证 DDL 事务决策日志与重复恢复的持久边界。</summary>
public sealed class TableDdlTransactionJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-ddl-journal-" + Guid.NewGuid().ToString("N"));

    /// <summary>prepared 到 committed 保留身份、对象 pair 与目录 floor。</summary>
    [Fact]
    public void PrepareMarkCommittedRead_RoundTripsStructuredContract()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, TableDdlTransactionJournal.FileName);
        Guid owner = Guid.NewGuid();
        var journal = Create(owner, TableDdlTransactionDecision.Prepared);

        TableDdlTransactionJournal.Prepare(path, journal);
        TableDdlTransactionJournal prepared = TableDdlTransactionJournal.Read(path)!;
        Assert.Equal(journal.Owner, prepared.Owner);
        Assert.Equal(journal.Decision, prepared.Decision);
        Assert.Equal(journal.WorkspaceRelativePath, prepared.WorkspaceRelativePath);
        Assert.Equal(journal.UndoRelativePath, prepared.UndoRelativePath);
        Assert.Equal(journal.CatalogVersionFloor, prepared.CatalogVersionFloor);
        Assert.Equal(journal.Objects, prepared.Objects);
        TableDdlTransactionJournal.MarkCommitted(path, owner);
        TableDdlTransactionJournal committed = TableDdlTransactionJournal.Read(path)!;
        Assert.Equal(TableDdlTransactionDecision.Committed, committed.Decision);
        Assert.Equal("main", committed.Objects[0].SchemaName);
        Assert.Equal("items", committed.Objects[0].Name);
        Assert.Equal(17, committed.CatalogVersionFloor);
        TableDdlTransactionJournal.Complete(path);
        Assert.False(File.Exists(path));
    }

    /// <summary>损坏、路径跳转和重复对象均在恢复动作前拒绝。</summary>
    [Fact]
    public void ReadOrPrepare_CorruptTraversalOrDuplicate_Rejects()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, TableDdlTransactionJournal.FileName);
        var duplicate = Create(Guid.NewGuid(), TableDdlTransactionDecision.Prepared) with
        {
            Objects =
            [
                new TableDdlObjectIdentity("main", "items"),
                new TableDdlObjectIdentity("main", "items"),
            ],
        };
        Assert.Throws<ArgumentException>(() => TableDdlTransactionJournal.Prepare(path, duplicate));
        var traversal = Create(Guid.NewGuid(), TableDdlTransactionDecision.Prepared) with
        { WorkspaceRelativePath = "../foreign" };
        Assert.Throws<ArgumentException>(() => TableDdlTransactionJournal.Prepare(path, traversal));

        TableDdlTransactionJournal.Prepare(path, Create(Guid.NewGuid(), TableDdlTransactionDecision.Prepared));
        byte[] bytes = File.ReadAllBytes(path);
        bytes[12] ^= 0x5a;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => TableDdlTransactionJournal.Read(path));
    }

    /// <summary>恢复在 backend 后强杀时保留日志，下一次按同一决策幂等重做并最终删除。</summary>
    [Theory]
    [InlineData((int)TableDdlTransactionDecision.Prepared)]
    [InlineData((int)TableDdlTransactionDecision.Committed)]
    public void Recover_InterruptedAfterBackend_RepeatsSameDecisionAndCompletes(
        int decisionValue)
    {
        var decision = (TableDdlTransactionDecision)decisionValue;
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, TableDdlTransactionJournal.FileName);
        TableDdlTransactionJournal journal = Create(Guid.NewGuid(), TableDdlTransactionDecision.Prepared);
        TableDdlTransactionJournal.Prepare(path, journal);
        if (decision == TableDdlTransactionDecision.Committed)
            TableDdlTransactionJournal.MarkCommitted(path, journal.Owner);
        var backend = new RecordingBackend();
        TableDdlTransactionRecovery.AfterBackendTestHook = _ => throw new SimulatedCrashException();
        try
        {
            Assert.Throws<SimulatedCrashException>(() =>
                TableDdlTransactionRecovery.Recover(path, backend));
            Assert.True(File.Exists(path));
        }
        finally
        {
            TableDdlTransactionRecovery.AfterBackendTestHook = null;
        }

        TableDdlRecoveryResult result = TableDdlTransactionRecovery.Recover(path, backend);
        Assert.Equal(decision == TableDdlTransactionDecision.Prepared
            ? TableDdlRecoveryResult.RolledBackPrepared
            : TableDdlRecoveryResult.CompletedCommitted, result);
        Assert.Equal(decision == TableDdlTransactionDecision.Prepared ? 2 : 0, backend.Rollbacks);
        Assert.Equal(decision == TableDdlTransactionDecision.Committed ? 2 : 0, backend.Completions);
        Assert.False(File.Exists(path));
        Assert.Equal(TableDdlRecoveryResult.None,
            TableDdlTransactionRecovery.Recover(path, backend));
    }

    private static TableDdlTransactionJournal Create(Guid owner, TableDdlTransactionDecision decision)
        => new(
            owner,
            decision,
            "ddl-transactions/workspace-" + owner.ToString("N"),
            "ddl-transactions/undo-" + owner.ToString("N"),
            17,
            [new TableDdlObjectIdentity("main", "items")]);

    /// <summary>只回收本测试创建的唯一目录。</summary>
    public void Dispose()
    {
        TableDdlTransactionRecovery.AfterBackendTestHook = null;
        TableDdlTransactionJournal.AfterDurableWriteTestHook = null;
        string full = Path.GetFullPath(_root);
        string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Assert.StartsWith(temporary, full, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("sndb-ddl-journal-", Path.GetFileName(full), StringComparison.Ordinal);
        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
    }

    private sealed class RecordingBackend : ITableDdlTransactionRecoveryBackend
    {
        internal int Rollbacks { get; private set; }
        internal int Completions { get; private set; }
        public void RollbackPrepared(TableDdlTransactionJournal journal, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rollbacks++;
        }
        public void CompleteCommitted(TableDdlTransactionJournal journal, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Completions++;
        }
    }

    private sealed class SimulatedCrashException : Exception;
}
