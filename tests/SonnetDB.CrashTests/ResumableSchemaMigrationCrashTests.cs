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
    private const string ResumableMigrationSql = "ALTER TABLE parents ALTER PRIMARY KEY(new_tenant,code)";
    private const int ResumableParentRows = 1_027;

    /// <summary>真实强杀落在分批日志、数据、游标和最终切换中的持久化边界。</summary>
    [Theory]
    [InlineData(KvStorageEngine.Legacy, "page-pending", false)]
    [InlineData(KvStorageEngine.Lsm, "page-pending", false)]
    [InlineData(KvStorageEngine.Legacy, "page-data-durable", false)]
    [InlineData(KvStorageEngine.Lsm, "page-data-durable", false)]
    [InlineData(KvStorageEngine.Legacy, "page-checkpointed", false)]
    [InlineData(KvStorageEngine.Lsm, "page-checkpointed", false)]
    [InlineData(KvStorageEngine.Legacy, "commit-intent", true)]
    [InlineData(KvStorageEngine.Lsm, "commit-intent", true)]
    [InlineData(KvStorageEngine.Legacy, "after-resumable-source-move:0", true)]
    [InlineData(KvStorageEngine.Legacy, "after-resumable-swap:0", true)]
    [InlineData(KvStorageEngine.Lsm, "after-resumable-swap:0", true)]
    [InlineData(KvStorageEngine.Lsm, "after-resumable-catalog", true)]
    public void ResumableSchemaMigration_HardKillRestoresCompositeKeysAndReferences(
        KvStorageEngine engine, string phase, bool committed)
    {
        string root = NewScenarioRoot();
        using (var db = Tsdb.Open(ResumableOptions(root, engine)))
            SeedResumableMigration(db);

        KillResumableMigration(root, engine, phase, "initial.ready");
        string journal = Path.Combine(root, "tables", "rowstore", TableResumableSchemaMigrationJournal.DirectoryName);
        ResumableMigrationState interrupted = Assert.IsType<ResumableMigrationState>(
            TableResumableSchemaMigrationJournal.Read(journal));
        Assert.Equal(committed, interrupted.Committing);
        if (!committed)
        {
            Assert.Equal(0, interrupted.Step);
            Assert.NotNull(interrupted.Cursor);
            Assert.True(interrupted.RowsInStep > 0);
            Assert.Equal(interrupted.Id, TableResumableSchemaMigrationJournal.ReadPending(journal)!.MigrationId);
        }

        using (var db = Tsdb.Open(ResumableOptions(root, engine)))
        {
            AssertResumableState(db, committed);
            db.Tables.ForceResumableSchemaMigrationForTests = true;
            if (!committed)
            {
                ResumableMigrationPending? firstRetriedPage = null;
                db.Tables.ResumableSchemaMigrationPhaseTestHook = current =>
                {
                    if (current == "page-pending" && firstRetriedPage is null)
                        firstRetriedPage = TableResumableSchemaMigrationJournal.ReadPending(journal);
                };
                SqlExecutor.Execute(db, ResumableMigrationSql);
                Assert.NotNull(firstRetriedPage);
                Assert.Equal(interrupted.Id, firstRetriedPage.MigrationId);
                Assert.Equal(interrupted.Step, firstRetriedPage.Step);
                Assert.Equal(interrupted.Cursor, firstRetriedPage.Cursor);
                db.Tables.ResumableSchemaMigrationPhaseTestHook = null;
            }
            db.Tables.ForceResumableSchemaMigrationForTests = false;
            SqlExecutor.Execute(db, ResumableMigrationSql);
            AssertResumableState(db, committed: true);
        }
        Assert.Null(TableResumableSchemaMigrationJournal.Read(journal));
        for (int reopen = 0; reopen < 2; reopen++)
        {
            using var db = Tsdb.Open(ResumableOptions(root, engine));
            AssertResumableState(db, committed: true);
        }
    }

    /// <summary>恢复中再次强杀，后续重开仍完成同一次切换。</summary>
    [Theory]
    [InlineData(KvStorageEngine.Legacy)]
    [InlineData(KvStorageEngine.Lsm)]
    public void ResumableSchemaMigration_SecondHardKillDuringRecovery_RollsForward(KvStorageEngine engine)
    {
        string root = NewScenarioRoot();
        using (var db = Tsdb.Open(ResumableOptions(root, engine)))
            SeedResumableMigration(db);

        KillResumableMigration(root, engine, "commit-intent", "before-recovery.ready");
        KillResumableMigration(root, engine, "recovery-after-resumable-swap:0", "during-recovery.ready");
        for (int reopen = 0; reopen < 2; reopen++)
        {
            using var db = Tsdb.Open(ResumableOptions(root, engine));
            AssertResumableState(db, committed: true);
        }

        using (var db = Tsdb.Open(ResumableOptions(root, engine)))
            SqlExecutor.Execute(db, "INSERT INTO children(id,ref_tenant,ref_id) VALUES(20,2,2)");
        using var final = Tsdb.Open(ResumableOptions(root, engine));
        Assert.Equal(2L, final.Tables.Open("children").GetByPrimaryKey([20L])!.Values[2]);
    }

    private static TsdbOptions ResumableOptions(string root, KvStorageEngine engine) => MakeOptions(root) with
    {
        Kv = KvOptions.Default with
        {
            StorageEngine = engine,
            LegacyMigration = KvLegacyMigrationMode.Disabled,
            SyncWalOnEveryWrite = true,
            AutoCheckpointEnabled = false,
            ExpirerEnabled = false,
            CleanupEnabled = false,
        },
    };

    private static void SeedResumableMigration(Tsdb db)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        SqlExecutor.Execute(db,
            "CREATE TABLE parents(tenant INT,id INT,new_tenant INT,code INT,label STRING,ref_tenant INT,ref_id INT,revision INT ROWVERSION," +
            "PRIMARY KEY(tenant,id),CHECK(code>0),FOREIGN KEY(ref_tenant,ref_id) REFERENCES parents(tenant,id))");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_label ON parents(label)");
        SqlExecutor.Execute(db, "CREATE INDEX ix_parent_ref ON parents(ref_tenant,ref_id) INCLUDE(label)");
        SqlExecutor.Execute(db,
            "CREATE TABLE children(id INT,ref_tenant INT,ref_id INT,PRIMARY KEY(id)," +
            "FOREIGN KEY(ref_tenant,ref_id) REFERENCES parents(tenant,id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_child_ref ON children(ref_tenant,ref_id)");
        SqlExecutor.Execute(db,
            "INSERT INTO parents(tenant,id,new_tenant,code,label,ref_tenant,ref_id) " +
            "VALUES(1,1,2,2,'one',NULL,NULL),(2,2,1,1,'two',NULL,NULL)");
        SqlExecutor.Execute(db, "UPDATE parents SET ref_tenant=2,ref_id=2 WHERE tenant=1 AND id=1");
        SqlExecutor.Execute(db, "UPDATE parents SET ref_tenant=1,ref_id=1 WHERE tenant=2 AND id=2");
        SqlExecutor.Execute(db, "INSERT INTO children(id,ref_tenant,ref_id) VALUES(10,1,1),(11,2,2),(12,2,NULL)");

        TableStore store = db.Tables.Open("parents");
        const int batchRows = 128;
        for (int start = 2; start < ResumableParentRows; start += batchRows)
        {
            deadline.Token.ThrowIfCancellationRequested();
            int count = Math.Min(batchRows, ResumableParentRows - start);
            var rows = new IReadOnlyList<object?>[count];
            for (int offset = 0; offset < count; offset++)
            {
                long id = start + offset;
                rows[offset] = new object?[] { 10L, id, 20L, id, "page-" + id.ToString(CultureInfo.InvariantCulture),
                    null, null, 1L };
            }
            Assert.Equal(count, store.InsertMany(rows));
        }
        db.Tables.CheckpointAll();
    }

    private static void KillResumableMigration(string root, KvStorageEngine engine, string phase, string readyName)
    {
        string scenario = $"crash_kill9_primary_key_migration_resumable_{engine.ToString().ToLowerInvariant()}_{phase}";
        string readyPath = Path.Combine(root, readyName);
        using Process worker = StartChild(scenario, root, readyPath);
        try
        {
            WaitForReady(worker, readyPath, TimeSpan.FromSeconds(30));
            Assert.Equal(scenario, File.ReadAllText(readyPath));
            Assert.False(worker.HasExited);
        }
        finally
        {
            TerminateAndWait(worker, TimeSpan.FromSeconds(10));
            File.Move(Path.Combine(root, "child-process.txt"), readyPath + ".process");
        }
        Assert.NotEqual(0, worker.ExitCode);
    }

    private static void AssertResumableState(Tsdb db, bool committed)
    {
        string[] key = committed ? ["new_tenant", "code"] : ["tenant", "id"];
        TableStore parents = db.Tables.Open("parents");
        TableStore children = db.Tables.Open("children");
        Assert.Equal(key, parents.Schema.PrimaryKey);
        Assert.Equal(key, Assert.Single(parents.Schema.ForeignKeys).PrincipalColumns);
        Assert.Equal(key, Assert.Single(children.Schema.ForeignKeys).PrincipalColumns);
        Assert.Equal(ResumableParentRows, parents.RowCount);
        Assert.Equal(3, children.RowCount);

        long one = committed ? 2L : 1L;
        long two = committed ? 1L : 2L;
        TableRow first = Assert.IsType<TableRow>(parents.GetByPrimaryKey([one, one]));
        TableRow second = Assert.IsType<TableRow>(parents.GetByPrimaryKey([two, two]));
        Assert.Equal("one", first.Values[4]);
        Assert.Equal("two", second.Values[4]);
        Assert.Equal(two, first.Values[5]);
        Assert.Equal(two, first.Values[6]);
        Assert.Equal(one, second.Values[5]);
        Assert.Equal(one, second.Values[6]);
        Assert.Equal(2L, first.Values[7]);
        Assert.Equal(2L, second.Values[7]);
        Assert.Equal("page-1026", parents.GetByPrimaryKey([committed ? 20L : 10L, 1_026L])!.Values[4]);

        Assert.Equal(one, children.GetByPrimaryKey([10L])!.Values[1]);
        Assert.Equal(one, children.GetByPrimaryKey([10L])!.Values[2]);
        Assert.Equal(two, children.GetByPrimaryKey([11L])!.Values[1]);
        Assert.Equal(two, children.GetByPrimaryKey([11L])!.Values[2]);
        Assert.Equal(2L, children.GetByPrimaryKey([12L])!.Values[1]);
        Assert.Null(children.GetByPrimaryKey([12L])!.Values[2]);

        Assert.Equal("one", Assert.Single(parents.GetByIndex(parents.Schema.TryGetIndex("ux_label")!, ["one"])).Values[4]);
        Assert.Equal("one", Assert.Single(parents.GetByIndex(parents.Schema.TryGetIndex("ix_parent_ref")!, [two, two])).Values[4]);
        Assert.Equal(10L, Assert.Single(children.GetByIndex(children.Schema.TryGetIndex("ix_child_ref")!, [one, one])).Values[0]);
        Assert.Equal(TableConstraintException.ForeignKeyViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO children(id,ref_tenant,ref_id) VALUES(99,99,99)")).ErrorCode);
        Assert.Equal(TableConstraintException.UniqueViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO parents(tenant,id,new_tenant,code,label) VALUES(99,99,98,98,'one')")).ErrorCode);
        Assert.Equal(TableConstraintException.CheckViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO parents(tenant,id,new_tenant,code,label) VALUES(99,99,98,-1,'bad')")).ErrorCode);
    }
}
