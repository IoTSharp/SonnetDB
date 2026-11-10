using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.CrashTests;

public sealed partial class CrashReliabilityTests
{
    /// <summary>独立 KV 在每个迁移发布边界被真实强杀后，数据、版本、TTL、generation 与二次重开均保持。</summary>
    [Theory]
    [InlineData("standalone-sst-finished")]
    [InlineData("standalone-built")]
    [InlineData("standalone-switching")]
    [InlineData("standalone-legacy-moved")]
    [InlineData("standalone-current-moved")]
    [InlineData("standalone-marker-written")]
    [InlineData("standalone-completed")]
    public void KvLsmMigration_Standalone_HardKillAtEachPhase(string phase)
    {
        string root = KillKvMigration(phase);
        string data = Path.Combine(root, "data");
        long[] expected = File.ReadAllLines(Path.Combine(root, "expected.txt"))
            .Select(value => long.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var elapsed = Stopwatch.StartNew();
        for (int reopen = 0; reopen < 2; reopen++)
        {
            using KvKeyspace store = KvKeyspace.Open("migration", data, KvMigrationOptions());
            Assert.Equal(KvStorageEngine.Lsm, store.StorageEngine);
            Assert.Equal(expected[0], store.LastSequence);
            Assert.Equal(expected[1], store.Generation);
            Assert.Equal(2050, store.Count);
            Assert.Null(store.Get("removed"));
            for (int index = 0; index < 2050; index++)
            {
                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30), "Recovery verification timed out.");
                KvEntry entry = Assert.IsType<KvEntry>(store.GetEntry($"k{index:D8}"));
                Assert.Equal(index, BinaryPrimitives.ReadInt64LittleEndian(entry.Value.Span));
                if (index == 0) Assert.Equal(expected[3], entry.Version);
                if (index == 2049)
                {
                    Assert.Equal(expected[4], entry.Version);
                    Assert.Equal(expected[2], entry.ExpiresAtUtc!.Value.UtcTicks);
                }
                else Assert.Null(entry.ExpiresAtUtc);
            }
        }
        Assert.False(Directory.Exists(Path.Combine(data, KvLegacyMigration.StagingDirectoryName)));
        Assert.Single(Directory.GetDirectories(data, KvLegacyMigration.RetainedPrefix + "*"));
        using (KvKeyspace store = KvKeyspace.Open("migration", data, KvMigrationOptions()))
            store.Put("after-recovery", [42]);
        using KvKeyspace final = KvKeyspace.Open("migration", data, KvMigrationOptions());
        Assert.Equal([42], final.Get("after-recovery"));
    }

    /// <summary>共享表在导入与目录切换阶段被真实强杀后，主行、索引、其他表和孤立编号回收保持一致。</summary>
    [Theory]
    [InlineData("shared-batch-imported")]
    [InlineData("shared-imported")]
    [InlineData("shared-pending")]
    [InlineData("shared-legacy-moved")]
    [InlineData("shared-marker-written")]
    [InlineData("shared-completed")]
    public void KvLsmMigration_SharedTables_HardKillAtEachPhase(string phase)
    {
        string root = KillKvMigration(phase);
        string data = Path.Combine(root, "data");
        var elapsed = Stopwatch.StartNew();
        for (int reopen = 0; reopen < 2; reopen++)
        {
            using Tsdb database = Tsdb.Open(MakeOptions(data) with { Kv = KvMigrationOptions() });
            var table = database.Tables.Open("items");
            Assert.Equal(1, table.Generation);
            Assert.Equal(2050, table.RowCount);
            for (int index = 0; index < 2050; index++)
            {
                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30), "Recovery verification timed out.");
                var row = table.GetByPrimaryKey(new object?[] { (long)index });
                Assert.NotNull(row);
                Assert.Equal($"v{index:D8}", row.Values[1]);
            }
            var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
                "SELECT id FROM items WHERE code='v00002049'"));
            Assert.Equal(2049L, Assert.Single(result.Rows)[0]);
            Assert.NotNull(database.Tables.Open("untouched").GetByPrimaryKey(new object?[] { 77L }));
        }
        using (var budget = new KvDiskReadBudget(2))
        using (KvLsmStore store = KvLsmStore.Open(Path.Combine(data, "tables", "lsm"), KvMigrationOptions(), budget))
            Assert.Equal(2, store.List().Count);
        using (Tsdb database = Tsdb.Open(MakeOptions(data) with { Kv = KvMigrationOptions() }))
            SqlExecutor.Execute(database, "INSERT INTO items(id,code) VALUES(2050,'after-recovery')");
        using Tsdb final = Tsdb.Open(MakeOptions(data) with { Kv = KvMigrationOptions() });
        Assert.Equal(2051, final.Tables.Open("items").RowCount);
    }

    private string KillKvMigration(string phase)
    {
        string root = NewScenarioRoot();
        string ready = Path.Combine(root, "migration.ready");
        using Process process = StartChild("kv_lsm_migration_" + phase, root, ready);
        try
        {
            WaitForReady(process, ready, TimeSpan.FromSeconds(40));
            Assert.False(process.HasExited);
        }
        finally
        {
            TerminateAndWait(process, TimeSpan.FromSeconds(10));
        }
        Assert.NotEqual(0, process.ExitCode);
        Assert.Equal(phase, File.ReadAllText(ready));
        return root;
    }

    private static KvOptions KvMigrationOptions() => KvOptions.Default with
    {
        StorageEngine = KvStorageEngine.Lsm,
        SyncWalOnEveryWrite = true,
        ExpirerEnabled = false,
        CleanupEnabled = false,
    };
}
