using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

internal static class DateTimeOffsetMigrationCrashScenario
{
    internal static void Run(string root, string readyFile, string scenario)
    {
        string phase = scenario["crash_offset_".Length..];
        if (phase == "recovery")
        {
            TableManager.SchemaMigrationRecoveryPhaseTestHook = (_, actual) =>
            {
                if (actual == "after-catalog") Pause();
            };
            using var recovering = Tsdb.Open(Options(root));
            throw new InvalidOperationException("DateTimeOffset recovery boundary was not reached.");
        }
        using var db = Tsdb.Open(Options(root));
        // 无主键夹具保留 DATETIMEOFFSET 独立的 v13→v15 屏障断言，不预先启用命名主键的 v16。
        SqlExecutor.Execute(db, "CREATE TABLE offsets(id INT,stamp INT,label STRING)");
        SqlExecutor.Execute(db, "CREATE INDEX ix_stamp ON offsets(stamp)");
        SqlExecutor.Execute(db, "INSERT INTO offsets(id,stamp,label) VALUES(1,1,'negative'),(2,639270596961230001,'long')");
        db.Tables.CheckpointAll();
        db.Tables.SchemaMigrationPhaseTestHook = actual =>
        {
            if (actual == phase) Pause();
        };
        SqlExecutor.Execute(db, "ALTER TABLE offsets ALTER COLUMN stamp TYPE DATETIMEOFFSET");
        throw new InvalidOperationException("DateTimeOffset migration boundary was not reached.");

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
