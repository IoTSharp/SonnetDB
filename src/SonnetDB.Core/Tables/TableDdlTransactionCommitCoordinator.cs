namespace SonnetDB.Tables;

internal interface ITableDdlTransactionCommitBackend : ITableDdlTransactionRecoveryBackend
{
    void PrepareUndo(TableDdlTransactionJournal journal, CancellationToken cancellationToken);
    void PublishPrepared(TableDdlTransactionJournal journal, CancellationToken cancellationToken);
}

internal sealed class TableDdlTransactionRecoveryException(string message, Exception innerException)
    : IOException(message, innerException);

/// <summary>执行 DDL undo、prepared、publish、committed 与 cleanup 的固定持久顺序。</summary>
internal static class TableDdlTransactionCommitCoordinator
{
    internal static Action<string>? PhaseTestHook { get; set; }

    internal static void Commit(
        string journalPath,
        TableDdlTransactionJournal journal,
        ITableDdlTransactionCommitBackend backend,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(backend);
        if (journal.Decision != TableDdlTransactionDecision.Prepared)
            throw new ArgumentException("DDL 提交协调器只接受 prepared 初始决策。", nameof(journal));
        cancellationToken.ThrowIfCancellationRequested();
        backend.PrepareUndo(journal, cancellationToken);
        PhaseTestHook?.Invoke("undo-durable");
        cancellationToken.ThrowIfCancellationRequested();
        TableDdlTransactionJournal.Prepare(journalPath, journal);
        PhaseTestHook?.Invoke("prepared-durable");
        try
        {
            backend.PublishPrepared(journal, cancellationToken);
            PhaseTestHook?.Invoke("published");
        }
        catch (Exception publishFailure)
        {
            try
            {
                backend.RollbackPrepared(journal, CancellationToken.None);
                PhaseTestHook?.Invoke("rollback-complete");
                TableDdlTransactionJournal.Complete(journalPath);
            }
            catch (Exception rollbackFailure)
            {
                throw new TableDdlTransactionRecoveryException(
                    "DDL 发布失败且补偿失败；保留 prepared 日志，必须重启恢复。",
                    new AggregateException(publishFailure, rollbackFailure));
            }
            throw;
        }

        try
        {
            TableDdlTransactionJournal.MarkCommitted(journalPath, journal.Owner);
            PhaseTestHook?.Invoke("committed-durable");
        }
        catch (Exception decisionFailure)
        {
            throw new TableDdlTransactionRecoveryException(
                "DDL 已发布但 committed 决策写入未确认；保留日志，必须重启判定。",
                decisionFailure);
        }

        try
        {
            backend.CompleteCommitted(
                journal with { Decision = TableDdlTransactionDecision.Committed },
                CancellationToken.None);
            PhaseTestHook?.Invoke("cleanup-complete");
            TableDdlTransactionJournal.Complete(journalPath);
        }
        catch (Exception cleanupFailure)
        {
            throw new TableDdlTransactionRecoveryException(
                "DDL committed 决策已持久化，但清理未完成；保留日志供重启幂等清理。",
                cleanupFailure);
        }
    }
}
