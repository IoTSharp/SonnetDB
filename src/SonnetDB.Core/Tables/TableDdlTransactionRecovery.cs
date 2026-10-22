namespace SonnetDB.Tables;

internal interface ITableDdlTransactionRecoveryBackend
{
    void RollbackPrepared(TableDdlTransactionJournal journal, CancellationToken cancellationToken);
    void CompleteCommitted(TableDdlTransactionJournal journal, CancellationToken cancellationToken);
}

internal enum TableDdlRecoveryResult
{
    None,
    RolledBackPrepared,
    CompletedCommitted,
}

/// <summary>按 durable 决策幂等恢复 DDL 事务；日志删除是恢复的最后一步。</summary>
internal static class TableDdlTransactionRecovery
{
    internal static Action<TableDdlTransactionDecision>? AfterBackendTestHook { get; set; }

    internal static TableDdlRecoveryResult Recover(
        string journalPath,
        ITableDdlTransactionRecoveryBackend backend,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        ArgumentNullException.ThrowIfNull(backend);
        TableDdlTransactionJournal? journal = TableDdlTransactionJournal.Read(journalPath);
        if (journal is null)
            return TableDdlRecoveryResult.None;
        cancellationToken.ThrowIfCancellationRequested();
        TableDdlRecoveryResult result;
        if (journal.Decision == TableDdlTransactionDecision.Prepared)
        {
            backend.RollbackPrepared(journal, cancellationToken);
            result = TableDdlRecoveryResult.RolledBackPrepared;
        }
        else
        {
            backend.CompleteCommitted(journal, cancellationToken);
            result = TableDdlRecoveryResult.CompletedCommitted;
        }
        AfterBackendTestHook?.Invoke(journal.Decision);
        cancellationToken.ThrowIfCancellationRequested();
        TableDdlTransactionJournal.Complete(journalPath);
        return result;
    }
}
