using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;

internal static class KeylessCrashScenario
{
    internal static void Run(string root, string readyFile, string scenario)
    {
        using var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = root,
            SyncWalOnEveryWrite = true,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
            Kv = KvOptions.Default with { AutoCheckpointEnabled = false, ExpirerEnabled = false },
        });
        SqlExecutor.Execute(db, "CREATE TABLE logs(value INT,CHECK(value>0))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_logs ON logs(value)");
        SqlExecutor.Execute(db, "CREATE TABLE keyed(id INT,value INT,PRIMARY KEY(id))");
        SqlExecutor.Execute(db, "INSERT INTO logs(value) VALUES(1),(1)");
        SqlExecutor.Execute(db, "INSERT INTO keyed(id,value) VALUES(1,1)");
        SaveIds("before.ids");
        if (scenario.EndsWith("between_tables", StringComparison.Ordinal))
            db.Tables.ApplyTransactionAfterTableTestHook = _ => PauseAtBoundary();
        else if (scenario.EndsWith("before_complete", StringComparison.Ordinal))
            db.Tables.ApplyTransactionBeforeCompleteTestHook = PauseAtBoundary;
        else
            db.Tables.ApplyTransactionAfterCompleteTestHook = PauseAtBoundary;
        SqlExecutor.ExecuteScript(db, """
            BEGIN;
            DELETE FROM logs WHERE value=1 LIMIT 1;
            UPDATE logs SET value=2 WHERE value=1;
            INSERT INTO logs(value) VALUES(3),(3);
            UPDATE keyed SET value=2 WHERE id=1;
            COMMIT;
            """);
        throw new InvalidOperationException("Expected hard-kill boundary was not reached.");

        void SaveIds(string name)
            => File.WriteAllLines(Path.Combine(root, name), db.Tables.Open("logs").Scan()
                .Select(row => Convert.ToHexString(row.PrimaryKey.Span)).Order());

        void PauseAtBoundary()
        {
            SaveIds("at-boundary.ids");
            File.WriteAllText(readyFile, scenario);
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
