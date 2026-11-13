using System.Diagnostics;
using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.CrashTests;

public sealed partial class CrashReliabilityTests
{
    [Theory]
    [InlineData("empty-import-shared-pending")]
    [InlineData("empty-import-shared-marker-written")]
    public void KvLsmFollowup_EmptyMigration_HardKillPreservesGenerationAndRollback(string phase)
    {
        string root = KillKvFollowup(phase);
        string data = Path.Combine(root, "data");
        for (int reopen = 0; reopen < 2; reopen++)
        {
            using Tsdb db = Tsdb.Open(MakeOptions(data));
            Assert.Equal(0, db.Tables.Open("empty_rows").RowCount);
            Assert.Equal(1, db.Tables.Open("empty_rows").Generation);
        }
        string target = Assert.Single(Directory.GetDirectories(Path.Combine(data, "tables", "rowstore")));
        Tsdb.RollbackKvMigration(data, target);
        using Tsdb restored = Tsdb.Open(MakeOptions(data) with
        {
            Kv = KvOptions.Default with { StorageEngine = KvStorageEngine.Legacy, LegacyMigration = KvLegacyMigrationMode.Disabled },
        });
        Assert.Equal(0, restored.Tables.Open("empty_rows").RowCount);
        Assert.Equal(1, restored.Tables.Open("empty_rows").Generation);
    }

    [Theory]
    [InlineData("txn-before", 0)]
    [InlineData("txn-wal", 1)]
    [InlineData("txn-after", 1)]
    [InlineData("txn-after-buffered", 1)]
    public void KvLsmFollowup_AtomicTransaction_HardKillKeepsWholePairAndCount(string phase, int expected)
    {
        string root = KillKvFollowup(phase);
        for (int reopen = 0; reopen < 2; reopen++)
        {
            using Tsdb db = Tsdb.Open(MakeOptions(Path.Combine(root, "data")));
            foreach (string name in new[] { "a", "b" })
                Assert.Equal(expected, db.Tables.Open(name).RowCount);
            Assert.Equal((long)expected, Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
                "SELECT COUNT(*) FROM a WHERE code='x'")).Rows)[0]);
        }
        Assert.False(File.Exists(Path.Combine(root, "data", "tables", TableTransactionJournal.FileName)));
    }

    [Theory]
    [InlineData("standalone-rollback-planned")]
    [InlineData("standalone-rollback-barrier-written")]
    [InlineData("standalone-rollback-isolated")]
    [InlineData("standalone-rollback-restored")]
    [InlineData("shared-rollback-planned")]
    [InlineData("shared-rollback-barrier-written")]
    [InlineData("shared-rollback-isolated")]
    [InlineData("shared-rollback-restored")]
    public void KvLsmFollowup_Rollback_HardKillResumesAtEachDurablePhase(string phase)
    {
        string root = KillKvFollowup(phase);
        string target = File.ReadAllText(Path.Combine(root, "target.txt"));
        if (phase.EndsWith("rollback-barrier-written", StringComparison.Ordinal))
        {
            Assert.Throws<InvalidDataException>(() => KvGenerationFile.Load(target));
            Tsdb.RollbackKvMigration(Path.Combine(root, "data"), target);
        }
        for (int reopen = 0; reopen < 2; reopen++)
        {
            var kv = KvOptions.Default with { StorageEngine = KvStorageEngine.Legacy, LegacyMigration = KvLegacyMigrationMode.Disabled };
            using Tsdb db = Tsdb.Open(MakeOptions(Path.Combine(root, "data")) with { Kv = kv });
            if (phase.StartsWith("shared-", StringComparison.Ordinal))
            {
                var table = db.Tables.Open("a");
                Assert.Equal(65, table.RowCount);
                Assert.Equal(1, table.Generation);
                var deadline = Stopwatch.StartNew();
                for (int index = 0; index < 65; index++)
                {
                    Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(15), "Rollback full-table verification timed out.");
                    var row = table.GetByPrimaryKey(new object?[] { (long)index });
                    Assert.NotNull(row);
                    Assert.Equal((long)index, row.Values[0]);
                    Assert.Equal("group-" + index % 7, row.Values[1]);
                    Assert.Equal(new string((char)('a' + index % 20), 33 + index), row.Values[2]);
                }
                var indexed = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
                    "SELECT id,code FROM a WHERE code='group-3' ORDER BY id"));
                Assert.Equal(Enumerable.Range(0, 65).Where(index => index % 7 == 3).Select(index => (long)index),
                    indexed.Rows.Select(row => (long)row[0]!));
                Assert.All(indexed.Rows, row => Assert.Equal("group-3", row[1]));
            }
            else
            {
                using KvKeyspace keyspace = KvKeyspace.Open("k", target, kv);
                Assert.Equal(65, keyspace.Count);
                Assert.Equal(1, keyspace.Generation);
                Assert.Null(keyspace.Get("removed"));
                Assert.Equal(long.Parse(File.ReadAllText(Path.Combine(root, "rollback-sequence.txt")), CultureInfo.InvariantCulture), keyspace.LastSequence);
                string[] expected = File.ReadAllLines(Path.Combine(root, "rollback-expected.txt"));
                Assert.Equal(65, expected.Length);
                var deadline = Stopwatch.StartNew();
                for (int index = 0; index < 65; index++)
                {
                    Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(15), "Rollback full-KV verification timed out.");
                    string[] fields = expected[index].Split('|');
                    var entry = Assert.IsType<KvEntry>(keyspace.GetEntry(fields[0]));
                    Assert.Equal(long.Parse(fields[1], CultureInfo.InvariantCulture), entry.Version);
                    Assert.Equal(long.Parse(fields[2], CultureInfo.InvariantCulture), entry.ExpiresAtUtc?.UtcTicks ?? 0);
                    Assert.Equal(Enumerable.Range(0, 33 + index).Select(value => (byte)(value ^ index)), entry.Value.ToArray());
                }
            }
        }
        Assert.False(File.Exists(Path.Combine(target, KvLegacyMigration.RollbackPendingFileName)));
    }

    private string KillKvFollowup(string phase)
    {
        string root = NewScenarioRoot();
        string ready = Path.Combine(root, "followup.ready");
        using Process process = StartChild("kv_lsm_followup_" + phase, root, ready);
        try
        {
            WaitForReady(process, ready, TimeSpan.FromSeconds(30));
            Assert.Equal(phase, File.ReadAllText(ready));
        }
        finally { TerminateAndWait(process, TimeSpan.FromSeconds(10)); }
        Assert.NotEqual(0, process.ExitCode);
        return root;
    }
}
