using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

internal static class CreateTableAsCrashScenario
{
    internal static void Run(string root, string readyFile, string scenario)
    {
        string phase = scenario["crash_ctas_".Length..];
        if (phase == "recovery-after-owner-removed")
        {
            TableCreationJournal.AfterOwnerRemovedTestHook = directory =>
            {
                if (Path.GetFullPath(directory).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) Pause();
            };
            using var recovering = Tsdb.Open(Options(root));
            throw new InvalidOperationException("CTAS recovery crash boundary was not reached.");
        }
        using var database = Tsdb.Open(Options(root));
        SqlExecutor.Execute(database, "CREATE TABLE Source(Id INT,Value INT,PRIMARY KEY(Id))");
        database.Tables.Open("Source").InsertMany(Enumerable.Range(1, 1_024)
            .Select(static number => (IReadOnlyList<object?>)new object?[] { (long)number, (long)number * 2 }).ToArray());
        database.Tables.CheckpointAll();
        database.Tables.CreateAsPhaseTestHook = actual => { if (actual == phase) Pause(); };
        TableCreationJournal.PreparationPhaseTestHook = (_, actual) => { if (actual == phase) Pause(); };
        SqlExecutor.Execute(database, "CREATE TABLE Copied AS SELECT Id,Value FROM Source");
        throw new InvalidOperationException("CTAS creation crash boundary was not reached.");

        void Pause()
        {
            File.WriteAllText(readyFile, scenario);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var stopped = new ManualResetEventSlim(false);
            stopped.Wait(cancellation.Token);
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
