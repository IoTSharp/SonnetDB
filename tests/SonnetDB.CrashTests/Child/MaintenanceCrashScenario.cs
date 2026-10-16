using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

internal static class MaintenanceCrashScenario
{
    internal static void Run(string root, string readyFile, string scenario)
    {
        const string dropPrefix = "crash_kill9_maintenance_drop_";
        if (scenario.StartsWith(dropPrefix, StringComparison.Ordinal))
        {
            RunPrimaryKeyDrop(root, readyFile, scenario, scenario[dropPrefix.Length..]);
            return;
        }
        const string identityPrefix = "crash_kill9_maintenance_identity_";
        string phase = scenario[identityPrefix.Length..];
        using var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = root,
            SyncWalOnEveryWrite = false,
            FlushWalToOsOnWrite = false,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
            Kv = KvOptions.Default with
            {
                SyncWalOnEveryWrite = false,
                AutoCheckpointEnabled = false,
                ExpirerEnabled = false,
            },
        });
        SqlExecutor.Execute(db, "CREATE TABLE Device(Id INT AUTO_INCREMENT,Payload STRING,PRIMARY KEY(Id))");
        if (phase is "existing" or "deleted" or "rejected")
        {
            SqlExecutor.Execute(db, "INSERT INTO Device(Id,Payload) VALUES(1,'kept'),(100,'maximum')");
            db.Tables.CheckpointAll();
            if (phase is "deleted" or "rejected")
                SqlExecutor.Execute(db, "DELETE FROM Device WHERE Id=100");
        }
        long next = phase == "maximum" ? long.MaxValue : phase == "empty" ? 50 : 200;
        SqlExecutor.Execute(db, $"ALTER TABLE Device AUTO_INCREMENT = {next}");
        if (phase == "rejected")
        {
            bool rejected = false;
            try
            {
                SqlExecutor.Execute(db, "ALTER TABLE Device AUTO_INCREMENT = 100");
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            if (!rejected)
                throw new InvalidOperationException("Conflicting identity maintenance unexpectedly succeeded.");
        }
        // 维护语句已确认成功；保持数据库打开，由父进程终止，禁止正常 Dispose 帮助刷新 WAL。
        File.WriteAllText(readyFile, scenario);
        Thread.Sleep(Timeout.Infinite);
    }

    private static void RunPrimaryKeyDrop(string root, string readyFile, string scenario, string phase)
    {
        if (phase == "recovery")
        {
            TableManager.SchemaMigrationRecoveryPhaseTestHook = (_, actual) =>
            {
                if (actual == "after-catalog") Pause();
            };
            using var recovering = Tsdb.Open(Options(root));
            throw new InvalidOperationException("Maintenance recovery crash boundary was not reached.");
        }

        using var db = Tsdb.Open(Options(root));
        SqlExecutor.Execute(db, "CREATE TABLE devices(Id INT,Code STRING,Payload STRING,CONSTRAINT PkDevices PRIMARY KEY(Id))");
        SqlExecutor.Execute(db, "CREATE INDEX IxCovered ON devices(Code DESC) INCLUDE(Payload)");
        SqlExecutor.Execute(db, "INSERT INTO devices(Id,Code,Payload) VALUES(1,'a','first'),(2,'b','second')");
        db.Tables.CheckpointAll();
        db.Tables.SchemaMigrationPhaseTestHook = actual =>
        {
            if (actual == phase) Pause();
        };
        SqlExecutor.Execute(db, "ALTER TABLE devices DROP CONSTRAINT PkDevices");
        throw new InvalidOperationException("Maintenance migration crash boundary was not reached.");

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
