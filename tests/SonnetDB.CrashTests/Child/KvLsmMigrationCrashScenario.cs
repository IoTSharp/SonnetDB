using System.Buffers.Binary;
using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;

internal static class KvLsmMigrationCrashScenario
{
    internal static void Run(string root, string readyFile, string phase)
    {
        bool standalone = phase.StartsWith("standalone-", StringComparison.Ordinal);
        string data = Path.Combine(root, "data");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        if (standalone)
        {
            using KvKeyspace legacy = KvKeyspace.Open("migration", data, Options(KvStorageEngine.Legacy));
            legacy.Put("removed", [9]);
            legacy.Clear();
            DateTimeOffset expires = DateTimeOffset.UtcNow.AddHours(1);
            for (int index = 0; index < 2050; index++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                byte[] value = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(value, index);
                legacy.Put($"k{index:D8}", value, index == 2049 ? expires : null);
                if (index == 1023) legacy.CreateSnapshot();
            }
            File.WriteAllLines(Path.Combine(root, "expected.txt"),
                [legacy.LastSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                 legacy.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
                 expires.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                 legacy.GetEntry("k00000000")!.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                 legacy.GetEntry("k00002049")!.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }
        else
        {
            using Tsdb legacy = Tsdb.Open(DatabaseOptions(data, KvStorageEngine.Legacy));
            SqlExecutor.Execute(legacy, "CREATE TABLE items(id INT, code VARCHAR, PRIMARY KEY(id))");
            SqlExecutor.Execute(legacy, "CREATE INDEX ix_items_code ON items(code)");
            SqlExecutor.Execute(legacy, "TRUNCATE TABLE items");
            SqlExecutor.Execute(legacy, "CREATE TABLE untouched(id INT, PRIMARY KEY(id))");
            SqlExecutor.Execute(legacy, "INSERT INTO untouched(id) VALUES(77)");
            var rows = new IReadOnlyList<object?>[2050];
            for (int index = 0; index < rows.Length; index++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                rows[index] = new object?[] { (long)index, $"v{index:D8}" };
            }
            legacy.Tables.Open("items").InsertMany(rows);
            legacy.Tables.CheckpointAll();
        }

        KvLegacyMigration.PhaseTestHook = (actual, directory) =>
        {
            if (actual != phase || !Path.GetFullPath(directory).StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFullPath(directory), data, StringComparison.OrdinalIgnoreCase))
                return;
            if (!standalone && !string.Equals(Path.GetFileName(directory), Convert.ToHexString("items"u8), StringComparison.OrdinalIgnoreCase))
                return;
            using (var ready = new FileStream(readyFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                ready.Write(System.Text.Encoding.UTF8.GetBytes(phase));
                ready.Flush(flushToDisk: true);
            }
            var wait = Stopwatch.StartNew();
            for (int poll = 0; poll < 300 && wait.Elapsed < TimeSpan.FromSeconds(30); poll++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                Thread.Sleep(100);
            }
            throw new TimeoutException("Parent did not kill the migration child at " + phase);
        };
        if (standalone)
        {
            using KvKeyspace migrated = KvKeyspace.Open("migration", data, Options(KvStorageEngine.Lsm));
        }
        else
        {
            using Tsdb migrated = Tsdb.Open(DatabaseOptions(data, KvStorageEngine.Lsm));
            _ = migrated.Tables.Open("items").RowCount;
        }
        throw new InvalidOperationException("Migration did not reach phase " + phase);
    }

    private static KvOptions Options(KvStorageEngine engine) => KvOptions.Default with
    {
        StorageEngine = engine,
        SyncWalOnEveryWrite = true,
        ExpirerEnabled = false,
        CleanupEnabled = false,
    };

    private static TsdbOptions DatabaseOptions(string root, KvStorageEngine engine) => new()
    {
        RootDirectory = root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
        Kv = Options(engine),
    };
}
