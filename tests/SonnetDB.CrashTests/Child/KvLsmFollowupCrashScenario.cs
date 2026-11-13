using System.Diagnostics;
using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

internal static class KvLsmFollowupCrashScenario
{
    internal static void Run(string root, string readyFile, string phase)
    {
        string data = Path.Combine(root, "data");
        if (phase.StartsWith("empty-import-", StringComparison.Ordinal))
        {
            using (Tsdb legacy = Tsdb.Open(Options(data, KvStorageEngine.Legacy)))
            {
                SqlExecutor.Execute(legacy, "CREATE TABLE empty_rows(id INT,PRIMARY KEY(id))");
                SqlExecutor.Execute(legacy, "TRUNCATE TABLE empty_rows");
            }
            string boundary = phase["empty-import-".Length..];
            KvLegacyMigration.PhaseTestHook = (actual, _) => { if (actual == boundary) Pause(); };
            using Tsdb migrated = Tsdb.Open(Options(data));
            _ = migrated.Tables.Open("empty_rows").RowCount;
            throw new InvalidOperationException("Empty migration did not reach " + boundary);
        }
        if (phase.StartsWith("txn-", StringComparison.Ordinal))
        {
            TsdbOptions options = Options(data);
            if (phase == "txn-after-buffered") options = options with { Kv = options.Kv with { SyncWalOnEveryWrite = false } };
            using Tsdb db = Tsdb.Open(options);
            SqlExecutor.Execute(db, "CREATE TABLE a(id INT,code STRING,PRIMARY KEY(id))");
            SqlExecutor.Execute(db, "CREATE INDEX ix_code ON a(code)");
            SqlExecutor.Execute(db, "CREATE TABLE b(id INT,PRIMARY KEY(id))");
            db.Tables.Open("a");
            db.Tables.Open("b");
            if (phase == "txn-before") db.Tables.ApplyTransactionBeforeCompleteTestHook = Pause;
            if (phase == "txn-wal") db.Tables.Open("a").SharedStore!.Engine.AfterWalAppendTestHook = Pause;
            if (phase is "txn-after" or "txn-after-buffered") db.Tables.ApplyTransactionAfterCompleteTestHook = Pause;
            db.Tables.ApplyTransaction(new Dictionary<string, IReadOnlyList<TableRowMutation>>
            {
                ["a"] = [new TableRowMutation(null, [1L, "x"])],
                ["b"] = [new TableRowMutation(null, [1L])],
            });
        }
        else
        {
            bool shared = phase.StartsWith("shared-", StringComparison.Ordinal);
            string target;
            using (Tsdb legacy = Tsdb.Open(Options(data, KvStorageEngine.Legacy)))
            {
                if (shared)
                {
                    SqlExecutor.Execute(legacy, "CREATE TABLE a(id INT,code STRING,payload STRING,PRIMARY KEY(id))");
                    SqlExecutor.Execute(legacy, "CREATE INDEX ix_code ON a(code)");
                    SqlExecutor.Execute(legacy, "TRUNCATE TABLE a");
                    var rows = new IReadOnlyList<object?>[65];
                    for (int index = 0; index < rows.Length; index++)
                        rows[index] = new object?[] { (long)index, "group-" + index % 7, new string((char)('a' + index % 20), 33 + index) };
                    legacy.Tables.Open("a").InsertMany(rows);
                    legacy.Tables.CheckpointAll();
                    target = Directory.GetDirectories(Path.Combine(data, "tables", "rowstore")).Single();
                }
                else
                {
                    target = Path.Combine(data, "kv", "standalone");
                    using KvKeyspace keyspace = KvKeyspace.Open("k", target, KvOptions.Default with { StorageEngine = KvStorageEngine.Legacy });
                    keyspace.Put("removed", [9]);
                    keyspace.Clear();
                    var expected = new List<string>();
                    DateTimeOffset expires = DateTimeOffset.UtcNow.AddHours(1);
                    var deadline = Stopwatch.StartNew();
                    for (int index = 0; index < 65 && deadline.Elapsed < TimeSpan.FromSeconds(15); index++)
                    {
                        string key = $"key-{index:D4}";
                        byte[] value = Enumerable.Range(0, 33 + index).Select(value => (byte)(value ^ index)).ToArray();
                        keyspace.Put(key, value, index % 3 == 0 ? expires : null);
                        var entry = keyspace.GetEntry(key)!;
                        expected.Add(key + "|" + entry.Version.ToString(CultureInfo.InvariantCulture) + "|"
                            + (entry.ExpiresAtUtc?.UtcTicks ?? 0).ToString(CultureInfo.InvariantCulture));
                    }
                    if (expected.Count != 65)
                        throw new TimeoutException("Rollback seed exceeded its deadline.");
                    File.WriteAllLines(Path.Combine(root, "rollback-expected.txt"), expected);
                    File.WriteAllText(Path.Combine(root, "rollback-sequence.txt"), keyspace.LastSequence.ToString(CultureInfo.InvariantCulture));
                    keyspace.CreateSnapshot();
                }
            }
            using (Tsdb migrated = Tsdb.Open(Options(data)))
            {
                if (shared) _ = migrated.Tables.Open("a").RowCount;
                else { using KvKeyspace keyspace = KvKeyspace.Open("k", target, KvOptions.Default with { StorageEngine = KvStorageEngine.Lsm }); }
            }
            File.WriteAllText(Path.Combine(root, "target.txt"), target);
            KvLegacyMigration.PhaseTestHook = (actual, directory) =>
            {
                if (actual == phase[(shared ? "shared-" : "standalone-").Length..]
                    && string.Equals(directory, target, StringComparison.OrdinalIgnoreCase)) Pause();
            };
            Tsdb.RollbackKvMigration(data, target);
        }
        throw new InvalidOperationException("Crash boundary was not reached: " + phase);

        void Pause()
        {
            using (var ready = new FileStream(readyFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                ready.Write(System.Text.Encoding.UTF8.GetBytes(phase));
                ready.Flush(flushToDisk: true);
            }
            var clock = Stopwatch.StartNew();
            for (int poll = 0; poll < 300 && clock.Elapsed < TimeSpan.FromSeconds(30); poll++)
                Thread.Sleep(100);
            throw new TimeoutException("Parent did not kill child at " + phase);
        }
    }

    private static TsdbOptions Options(string data, KvStorageEngine engine = KvStorageEngine.Lsm) => new()
    {
        RootDirectory = data,
        Kv = KvOptions.Default with { StorageEngine = engine, SyncWalOnEveryWrite = true, ExpirerEnabled = false, AutoCheckpointEnabled = false },
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
    };
}
