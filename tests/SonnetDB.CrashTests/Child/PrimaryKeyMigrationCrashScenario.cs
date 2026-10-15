using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

internal static class PrimaryKeyMigrationCrashScenario
{
    internal static void Run(string root, string readyFile, string scenario)
    {
        const string prefix = "crash_kill9_primary_key_migration_";
        string phase = scenario[prefix.Length..].Replace('_', '-');
        if (phase.StartsWith("recovery-", StringComparison.Ordinal))
        {
            string recoveryPhase = phase["recovery-".Length..];
            TableManager.SchemaMigrationRecoveryPhaseTestHook = (_, actual) =>
            {
                if (actual == recoveryPhase) Pause();
            };
            using var recovering = Tsdb.Open(Options(root));
            throw new InvalidOperationException("Recovery crash boundary was not reached.");
        }

        using var db = Tsdb.Open(Options(root));
        bool typeChange = phase.StartsWith("type-", StringComparison.Ordinal);
        if (typeChange) phase = phase["type-".Length..];
        SqlExecutor.Execute(db, "CREATE TABLE parents(id INT AUTO_INCREMENT,code INT NOT NULL,label STRING DEFAULT 'base',version INT ROWVERSION,PRIMARY KEY(id),CHECK(code>0))");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_label ON parents(label)");
        SqlExecutor.Execute(db, "CREATE INDEX ix_code ON parents(code)");
        SqlExecutor.Execute(db, "CREATE TABLE children(id INT,parent_id INT,PRIMARY KEY(id),FOREIGN KEY(parent_id) REFERENCES parents(id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_parent ON children(parent_id)");
        SqlExecutor.Execute(db, "INSERT INTO parents(code,label) VALUES(2,'one'),(1,'two')");
        SqlExecutor.Execute(db, "INSERT INTO children(id,parent_id) VALUES(10,1)");
        if (typeChange)
            SqlExecutor.Execute(db, "ALTER TABLE parents ALTER PRIMARY KEY(code)");
        if (phase == "after-page")
        {
            string rows = string.Join(',', Enumerable.Range(3, 1030).Select(index => $"({index},'row-{index}')"));
            SqlExecutor.Execute(db, "INSERT INTO parents(code,label) VALUES" + rows);
            TableStore.SchemaMigrationPageTestHook = _ => Pause();
        }
        db.Tables.CheckpointAll();
        db.Tables.SchemaMigrationPhaseTestHook = actual =>
        {
            if (actual == phase || phase == "after-parent" && actual == "after-table:parents") Pause();
        };
        SqlExecutor.Execute(db, typeChange
            ? "ALTER TABLE parents ALTER COLUMN code TYPE DECIMAL(18,0)"
            : "ALTER TABLE parents ALTER PRIMARY KEY(code)");
        throw new InvalidOperationException("Migration crash boundary was not reached.");

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
