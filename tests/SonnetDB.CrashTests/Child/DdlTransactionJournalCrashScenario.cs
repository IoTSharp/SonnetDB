using System.Text;
using SonnetDB.Tables;

internal static class DdlTransactionJournalCrashScenario
{
    internal static void Run(string root, string readyFile, string scenario)
    {
        string tables = Path.Combine(root, "tables");
        Directory.CreateDirectory(tables);
        string journalPath = Path.Combine(tables, TableDdlTransactionJournal.FileName);
        if (scenario is "crash_ddl_journal_prepared" or "crash_ddl_journal_committed")
        {
            Guid owner = Guid.NewGuid();
            var journal = new TableDdlTransactionJournal(
                owner,
                TableDdlTransactionDecision.Prepared,
                "ddl-transactions/workspace-" + owner.ToString("N"),
                "ddl-transactions/undo-" + owner.ToString("N"),
                17,
                [new TableDdlObjectIdentity("main", "items")]);
            TableDdlTransactionJournal.Prepare(journalPath, journal);
            if (scenario.EndsWith("committed", StringComparison.Ordinal))
                TableDdlTransactionJournal.MarkCommitted(journalPath, owner);
            Pause();
            throw new InvalidOperationException("DDL journal crash boundary was not reached.");
        }

        var backend = new FileBackend(tables);
        if (scenario == "crash_ddl_recovery_after-backend")
        {
            TableDdlTransactionRecovery.AfterBackendTestHook = _ => Pause();
            _ = TableDdlTransactionRecovery.Recover(journalPath, backend);
            throw new InvalidOperationException("DDL recovery crash boundary was not reached.");
        }
        if (scenario == "crash_ddl_recovery_complete")
        {
            _ = TableDdlTransactionRecovery.Recover(journalPath, backend);
            File.WriteAllText(readyFile, scenario);
            return;
        }
        throw new ArgumentOutOfRangeException(nameof(scenario));

        void Pause()
        {
            File.WriteAllText(readyFile, scenario);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var stopped = new ManualResetEventSlim(false);
            stopped.Wait(cancellation.Token);
        }
    }

    private sealed class FileBackend(string tables) : ITableDdlTransactionRecoveryBackend
    {
        public void RollbackPrepared(TableDdlTransactionJournal journal, CancellationToken cancellationToken)
            => Append("prepared", cancellationToken);

        public void CompleteCommitted(TableDdlTransactionJournal journal, CancellationToken cancellationToken)
            => Append("committed", cancellationToken);

        private void Append(string decision, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(tables, "ddl-recovery-" + decision + ".log");
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(Encoding.ASCII.GetBytes(decision + "\n"));
            stream.Flush(flushToDisk: true);
        }
    }
}
