using SonnetDB.Cli;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Cli;

[Collection(nameof(SonnetDB.Core.Tests.Kv.KvLegacyMigrationTests))]
public sealed class KvMigrationCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-rollback-cli-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OfflineRollback_PreservesLegacyDataAndRejectsDuplicate(bool shared)
    {
        string target = Prepare(shared);
        Assert.Equal(ExitCodes.Success, Run(target).Code);
        Assert.False(KvLsmLayoutMarkerFile.TryRead(target, out _));
        Assert.Equal(ExitCodes.ExecutionFailed, Run(target).Code);
        using Tsdb db = Open(KvStorageEngine.Legacy);
        if (shared)
            Assert.Equal(1, db.Tables.Open("items").RowCount);
        else
        {
            using KvKeyspace keyspace = KvKeyspace.Open("k", target, Options(KvStorageEngine.Legacy));
            Assert.Equal([42], keyspace.Get("key"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewWritesOrLiveDatabase_RejectWithoutDroppingData(bool shared)
    {
        string target = Prepare(shared);
        using (Tsdb db = Open(KvStorageEngine.Lsm))
        {
            Assert.Equal(ExitCodes.ExecutionFailed, Run(target).Code);
            if (shared) SqlExecutor.Execute(db, "INSERT INTO items(id) VALUES(2)");
            else
            {
                using KvKeyspace keyspace = KvKeyspace.Open("k", target, Options(KvStorageEngine.Lsm));
                Assert.Equal(ExitCodes.ExecutionFailed, Run(target).Code);
                keyspace.Put("new", [43]);
            }
        }
        Assert.Equal(ExitCodes.ExecutionFailed, Run(target).Code);
        Assert.True(KvLsmLayoutMarkerFile.TryRead(target, out _));
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--database")]
    [InlineData("--offline")]
    public void MissingOrUnknownArguments_ReturnUsage(string argument)
    {
        var app = new CliApplication(new StringReader(""), new StringWriter(), new StringWriter());
        Assert.Equal(ExitCodes.InvalidArguments, app.Run(["kv", "rollback-migration", argument]));
    }

    [Theory]
    [InlineData("rollback-planned", false)]
    [InlineData("rollback-isolated", false)]
    [InlineData("rollback-restored", false)]
    [InlineData("rollback-planned", true)]
    [InlineData("rollback-isolated", true)]
    [InlineData("rollback-restored", true)]
    public void InterruptedRollback_RepeatedOpenFinishesWithoutDataLoss(string phase, bool shared)
    {
        string target = Prepare(shared);
        KvLegacyMigration.PhaseTestHook = (actual, directory) =>
        {
            if (actual == phase && Path.GetFullPath(directory).StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("rollback crash");
        };
        try { Assert.Equal(ExitCodes.ExecutionFailed, Run(target).Code); }
        finally { KvLegacyMigration.PhaseTestHook = null; }
        Assert.True(File.Exists(Path.Combine(target, KvLegacyMigration.RollbackPendingFileName)));
        // Recovery runs before engine selection. LegacyMigration=Disabled avoids automatic re-migration.
        for (int reopen = 0; reopen < 2; reopen++)
        {
            using Tsdb db = Open(KvStorageEngine.Legacy);
            if (shared) Assert.Equal(1, db.Tables.Open("items").RowCount);
            else
            {
                using KvKeyspace keyspace = KvKeyspace.Open("k", target, Options(KvStorageEngine.Legacy));
                Assert.Equal([42], keyspace.Get("key"));
                Assert.Equal(KvStorageEngine.Legacy, keyspace.StorageEngine);
            }
        }
        Assert.False(File.Exists(Path.Combine(target, KvLegacyMigration.RollbackPendingFileName)));
    }

    private string Prepare(bool shared)
    {
        string target;
        using (Tsdb db = Open(KvStorageEngine.Legacy))
        {
            if (shared)
            {
                SqlExecutor.Execute(db, "CREATE TABLE items(id INT,PRIMARY KEY(id))");
                SqlExecutor.Execute(db, "INSERT INTO items(id) VALUES(1)");
                target = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "tables", "rowstore")));
            }
            else
            {
                target = Path.Combine(_root, "kv", "standalone");
                using KvKeyspace keyspace = KvKeyspace.Open("k", target, Options(KvStorageEngine.Legacy));
                keyspace.Put("key", [42]);
            }
        }
        using (Tsdb db = Open(KvStorageEngine.Lsm))
        {
            if (shared) _ = db.Tables.Open("items").RowCount;
            else { using KvKeyspace keyspace = KvKeyspace.Open("k", target, Options(KvStorageEngine.Lsm)); }
        }
        return target;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BarrierWithoutIntent_RejectsOpenAndOldGenerationReader_AndCliCanResume(bool shared)
    {
        string target = Prepare(shared);
        KvLegacyMigration.PhaseTestHook = (actual, directory) =>
        {
            if (actual == "rollback-barrier-written" && directory == target) throw new IOException("barrier crash");
        };
        try { Assert.Equal(ExitCodes.ExecutionFailed, Run(target).Code); }
        finally { KvLegacyMigration.PhaseTestHook = null; }
        Assert.False(File.Exists(Path.Combine(target, KvLegacyMigration.RollbackPendingFileName)));
        Assert.True(KvLsmLayoutMarkerFile.TryRead(target, out var marker));
        Assert.Equal(KvLsmLayoutMarkerFile.RollbackVersion, marker.FormatVersion);
        Assert.Throws<InvalidDataException>(() => KvGenerationFile.Load(target));
        if (shared)
        {
            using Tsdb db = Open(KvStorageEngine.Legacy);
            Assert.Throws<InvalidDataException>(() => db.Tables.Open("items"));
        }
        else
            Assert.Throws<InvalidDataException>(() => KvKeyspace.Open("k", target, Options(KvStorageEngine.Legacy)));
        Assert.Equal(ExitCodes.Success, Run(target).Code);
        Assert.False(KvLsmLayoutMarkerFile.TryRead(target, out _));
    }

    private (int Code, string Output, string Error) Run(string target)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var app = new CliApplication(new StringReader(""), output, error);
        return (app.Run(["kv", "rollback-migration", "--database", _root, "--path", target, "--offline"]), output.ToString(), error.ToString());
    }

    private static KvOptions Options(KvStorageEngine engine) => KvOptions.Default with
    {
        StorageEngine = engine,
        LegacyMigration = engine == KvStorageEngine.Legacy ? KvLegacyMigrationMode.Disabled : KvLegacyMigrationMode.Auto,
        SyncWalOnEveryWrite = true,
        AutoCheckpointEnabled = false,
        ExpirerEnabled = false,
    };

    private Tsdb Open(KvStorageEngine engine) => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        Kv = Options(engine),
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
    });

    public void Dispose()
    {
        KvLegacyMigration.PhaseTestHook = null;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
