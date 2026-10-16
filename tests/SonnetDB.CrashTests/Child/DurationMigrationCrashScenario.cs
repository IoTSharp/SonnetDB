using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

internal static class DurationMigrationCrashScenario
{
    internal static void Run(string root, string readyFile, string scenario)
    {
        string phase = scenario["crash_duration_".Length..];
        if (phase == "recovery")
        {
            TableManager.SchemaMigrationRecoveryPhaseTestHook = (_, actual) =>
            {
                if (actual == "after-catalog") Pause();
            };
            using var recovering = Tsdb.Open(Options(root));
            throw new InvalidOperationException("Duration recovery boundary was not reached.");
        }
        using var db = Tsdb.Open(Options(root));
        // 无主键夹具保留 DURATION 独立的 v13→v14 屏障断言，不预先启用命名主键的 v16。
        SqlExecutor.Execute(db, "CREATE TABLE durations(id INT,elapsed INT,label STRING)");
        SqlExecutor.Execute(db, "CREATE INDEX ix_elapsed ON durations(elapsed)");
        SqlExecutor.Execute(db, "INSERT INTO durations(id,elapsed,label) VALUES(1,-1,'negative'),(2,972000000001,'long')");
        db.Tables.CheckpointAll();
        db.Tables.SchemaMigrationPhaseTestHook = actual =>
        {
            if (actual == phase) Pause();
        };
        SqlExecutor.Execute(db, "ALTER TABLE durations ALTER COLUMN elapsed TYPE DURATION");
        throw new InvalidOperationException("Duration migration boundary was not reached.");

        void Pause()
        {
            File.WriteAllText(readyFile, scenario);
            Thread.Sleep(Timeout.Infinite);
        }
    }

    private static TsdbOptions Options(string root) => new()
    {
        RootDirectory = root,
        SyncWalOnEveryWrite = true,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
        Kv = KvOptions.Default with { SyncWalOnEveryWrite = true, AutoCheckpointEnabled = false, ExpirerEnabled = false },
    };
}
