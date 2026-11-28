using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>验证关系表分批迁移的游标、取消、重新执行和发布隔离。</summary>
public sealed class TableResumableSchemaMigrationTests : IDisposable
{
    private const string Migration = "ALTER TABLE items ALTER PRIMARY KEY(code)";
    private const int LargeRowCount = 100_001;
    private const int PartialRowCount = 2_051;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LargeTimeout = TimeSpan.FromMinutes(3);
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
        "sndb-resumable-schema-" + Guid.NewGuid().ToString("N")));

    /// <summary>两种引擎原生迁移超过原行数预算的置换键，并校验重开后的每一条行和索引。</summary>
    [Theory]
    [InlineData(KvStorageEngine.Legacy)]
    [InlineData(KvStorageEngine.Lsm)]
    public void LargeSchemaMigration_PreservesSwappedSignedKeysAndIndexes(KvStorageEngine engine)
    {
        using var deadline = new CancellationTokenSource(LargeTimeout);
        using (var db = Open(engine))
        {
            Setup(db, LargeRowCount, deadline.Token);
            TableStore held = db.Tables.Open("items");
            int checkpoints = 0;
            db.Tables.ResumableSchemaMigrationPhaseTestHook = phase =>
            {
                if (phase == "page-checkpointed") checkpoints++;
            };

            Migrate(db, deadline.Token);

            Assert.True(checkpoints > 1, $"持久页数量为 {checkpoints}。");
            Assert.Equal(["code"], db.Tables.Catalog.TryGet("items")!.PrimaryKey);
            Assert.Same(held, db.Tables.Open("items"));
            Assert.Equal(["code"], held.Schema.PrimaryKey);
            Assert.Equal(LargeRowCount, held.RowCount);
            Assert.Equal(50_000L, held.GetByPrimaryKey([-50_000L])!.Values[0]);
        }
        using var reopened = Open(engine);
        Assert.Equal(["code"], reopened.Tables.Catalog.TryGet("items")!.PrimaryKey);
        AssertAllRows(reopened.Tables.Open("items"), LargeRowCount, deadline.Token);
        AssertIndexesAndCheck(reopened);
    }

    /// <summary>取消后的完成页可复用，期间普通写入会使旧检查点失效并纳入重试。</summary>
    [Theory]
    [InlineData(KvStorageEngine.Legacy, false)]
    [InlineData(KvStorageEngine.Legacy, true)]
    [InlineData(KvStorageEngine.Lsm, false)]
    [InlineData(KvStorageEngine.Lsm, true)]
    public void CancelledSchemaMigration_RetryUsesOnlyMatchingSource(KvStorageEngine engine, bool mutateSource)
    {
        using var deadline = new CancellationTokenSource(TestTimeout);
        using (var db = Open(engine))
        {
            Setup(db, PartialRowCount, deadline.Token);
            db.Tables.ForceResumableSchemaMigrationForTests = true;
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            int checkpoints = 0;
            db.Tables.ResumableSchemaMigrationPhaseTestHook = phase =>
            {
                if (phase == "page-checkpointed") checkpoints++;
                if (phase == "before-commit") cancel.Cancel();
            };

            var failure = Assert.Throws<RoutineExecutionException>(() => Migrate(db, cancel.Token));
            Assert.Equal(RoutineErrorCodes.Cancelled, failure.Code);
            Assert.True(checkpoints > 1, $"取消前的持久页数量为 {checkpoints}。");
            AssertOriginal(db, PartialRowCount);
            if (mutateSource)
                SqlExecutor.Execute(db, "UPDATE items SET label='new-source-value' WHERE id=1");
        }
        using (var reopened = Open(engine))
        {
            AssertOriginal(reopened, PartialRowCount, mutateSource ? "new-source-value" : Label(1));
            reopened.Tables.ForceResumableSchemaMigrationForTests = true;
            int replayedPages = 0;
            reopened.Tables.ResumableSchemaMigrationPhaseTestHook = phase =>
            {
                if (phase == "page-checkpointed") replayedPages++;
            };

            Migrate(reopened, deadline.Token);

            if (mutateSource)
                Assert.True(replayedPages > 1, $"源行变更后重建的页数量为 {replayedPages}。");
            else
                Assert.Equal(0, replayedPages);
            Assert.Equal(["code"], reopened.Tables.Catalog.TryGet("items")!.PrimaryKey);
            Assert.Equal(mutateSource ? "new-source-value" : Label(1),
                reopened.Tables.Open("items").GetByPrimaryKey([-1L])!.Values[2]);
        }
        using var again = Open(engine);
        Assert.Equal(PartialRowCount, again.Tables.Open("items").RowCount);
        Assert.Equal(["code"], again.Tables.Catalog.TryGet("items")!.PrimaryKey);
        Assert.Equal(mutateSource ? "new-source-value" : Label(1),
            again.Tables.Open("items").GetByPrimaryKey([-1L])!.Values[2]);
    }

    /// <summary>迁移的中间页不可见，并发 INSERT 只在切换后进入目标主键空间。</summary>
    [Theory]
    [InlineData(KvStorageEngine.Legacy)]
    [InlineData(KvStorageEngine.Lsm)]
    public async Task SchemaMigration_BlocksConcurrentWriterThroughCatalogSwitch(KvStorageEngine engine)
    {
        using var deadline = new CancellationTokenSource(TestTimeout);
        using var db = Open(engine);
        Setup(db, PartialRowCount, deadline.Token);
        TableStore held = db.Tables.Open("items");
        db.Tables.ForceResumableSchemaMigrationForTests = true;
        var pageCheckpointed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        db.Tables.ResumableSchemaMigrationPhaseTestHook = phase =>
        {
            if (phase == "page-checkpointed" && pageCheckpointed.TrySetResult())
                release.Wait(deadline.Token);
        };

        Task migration = Task.Run(() => Migrate(db, deadline.Token), deadline.Token);
        Task? writer = null;
        try
        {
            await pageCheckpointed.Task.WaitAsync(deadline.Token);
            Assert.Equal(["id"], db.Tables.Catalog.TryGet("items")!.PrimaryKey);

            var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            writer = Task.Run(() =>
            {
                writerStarted.SetResult();
                SqlExecutor.Execute(db, null, "INSERT INTO items(id,code,label,bucket) VALUES(2000,-2000,'concurrent',1)",
                    null, null, new SqlExecutionOptions { CancellationToken = deadline.Token });
            }, deadline.Token);
            await writerStarted.Task.WaitAsync(deadline.Token);
            Task writerWait = Task.Delay(TimeSpan.FromMilliseconds(150), deadline.Token);
            Assert.Same(writerWait, await Task.WhenAny(writer, writerWait));
        }
        finally
        {
            release.Set();
            await migration.WaitAsync(TestTimeout);
            if (writer is not null)
                await writer.WaitAsync(TestTimeout);
        }

        Assert.Equal(["code"], db.Tables.Catalog.TryGet("items")!.PrimaryKey);
        Assert.Same(held, db.Tables.Open("items"));
        Assert.Equal(["code"], held.Schema.PrimaryKey);
        Assert.Equal(PartialRowCount + 1, held.RowCount);
        Assert.Equal("concurrent", held.GetByPrimaryKey([-2_000L])!.Values[2]);
        Assert.Null(held.GetByPrimaryKey([2_000L]));
    }

    /// <summary>后续页的新主键与已完成页冲突时，原表保留完整内容，修正后可重新迁移。</summary>
    [Theory]
    [InlineData(KvStorageEngine.Legacy)]
    [InlineData(KvStorageEngine.Lsm)]
    public void SchemaMigration_DuplicateNewKeyAcrossPages_PreservesOriginalRows(KvStorageEngine engine)
    {
        const int rowCount = 520;
        using var deadline = new CancellationTokenSource(TestTimeout);
        using (var db = Open(engine))
        {
            SqlExecutor.Execute(db, "CREATE TABLE items(id INT,code INT,label STRING,PRIMARY KEY(id))");
            SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_label ON items(label)");
            TableStore source = db.Tables.Open("items");
            var rows = new IReadOnlyList<object?>[rowCount];
            for (int index = 0; index < rows.Length; index++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                long id = index + 1;
                long code = id == rowCount ? 1 : id;
                rows[index] = new object?[] { id, code, Label(id) };
            }
            Assert.Equal(rowCount, source.InsertMany(rows));
            db.Tables.CheckpointAll();
            db.Tables.ForceResumableSchemaMigrationForTests = true;
            int checkpoints = 0;
            db.Tables.ResumableSchemaMigrationPhaseTestHook = phase =>
            {
                if (phase == "page-checkpointed") checkpoints++;
            };

            var failure = Assert.Throws<TableConstraintException>(() => Migrate(db, deadline.Token));
            Assert.Equal(TableConstraintException.UniqueViolation, failure.ErrorCode);
            Assert.True(checkpoints > 0);
            AssertOriginal(db, rowCount);
            Assert.Equal(1L, source.GetByPrimaryKey([(long)rowCount])!.Values[1]);
            Assert.Equal(Label(rowCount), Assert.Single(source.GetByIndex(source.Schema.TryGetIndex("ux_label")!,
                [Label(rowCount)])).Values[2]);

            SqlExecutor.Execute(db, "UPDATE items SET code=521 WHERE id=520");
            Migrate(db, deadline.Token);
        }
        using var reopened = Open(engine);
        TableStore target = reopened.Tables.Open("items");
        Assert.Equal(["code"], target.Schema.PrimaryKey);
        Assert.Equal(rowCount, target.RowCount);
        Assert.Equal(1L, target.GetByPrimaryKey([1L])!.Values[0]);
        Assert.Equal(520L, target.GetByPrimaryKey([521L])!.Values[0]);
        Assert.Null(target.GetByPrimaryKey([520L]));
    }

    /// <summary>分批复制拒绝子表转换后的 CHECK 冲突及已有的无效外键。</summary>
    [Theory]
    [InlineData(KvStorageEngine.Legacy, true)]
    [InlineData(KvStorageEngine.Legacy, false)]
    [InlineData(KvStorageEngine.Lsm, true)]
    [InlineData(KvStorageEngine.Lsm, false)]
    public void SchemaMigration_InvalidChildConstraint_LeavesOriginalCatalogAndRows(KvStorageEngine engine, bool checkFailure)
    {
        using var deadline = new CancellationTokenSource(TestTimeout);
        using (var db = Open(engine))
        {
            SqlExecutor.Execute(db, "CREATE TABLE items(id INT,code INT,PRIMARY KEY(id))");
            SqlExecutor.Execute(db,
                "CREATE TABLE links(id INT,item_id INT,PRIMARY KEY(id),CHECK(item_id<5),FOREIGN KEY(item_id) REFERENCES items(id))");
            SqlExecutor.Execute(db, "INSERT INTO items(id,code) VALUES(1,7)");
            if (checkFailure)
                SqlExecutor.Execute(db, "INSERT INTO links(id,item_id) VALUES(10,1)");
            else
                db.Tables.Open("links").Insert([10L, 4L]);

            db.Tables.ForceResumableSchemaMigrationForTests = true;
            TableConstraintException failure = Assert.Throws<TableConstraintException>(() => Migrate(db, deadline.Token));
            Assert.Equal(checkFailure ? TableConstraintException.CheckViolation : TableConstraintException.ForeignKeyViolation,
                failure.ErrorCode);
            Assert.Equal(["id"], db.Tables.Catalog.TryGet("items")!.PrimaryKey);
            Assert.Equal(checkFailure ? 1L : 4L, db.Tables.Open("links").GetByPrimaryKey([10L])!.Values[1]);

            if (!checkFailure)
                SqlExecutor.Execute(db, "UPDATE links SET item_id=1 WHERE id=10");
            SqlExecutor.Execute(db, "UPDATE items SET code=2 WHERE id=1");
            Migrate(db, deadline.Token);
        }
        using var reopened = Open(engine);
        Assert.Equal(["code"], reopened.Tables.Catalog.TryGet("items")!.PrimaryKey);
        Assert.Equal(["code"], Assert.Single(reopened.Tables.Catalog.TryGet("links")!.ForeignKeys).PrincipalColumns);
        Assert.Equal(2L, reopened.Tables.Open("links").GetByPrimaryKey([10L])!.Values[1]);
    }

    /// <summary>持久游标所依赖的目标目录缺失时，重试明确拒绝，并且仍能读取全部原数据。</summary>
    [Theory]
    [InlineData(KvStorageEngine.Legacy)]
    [InlineData(KvStorageEngine.Lsm)]
    public void SchemaMigration_MissingCheckpointStage_RejectsRetryWithoutSkippingRows(KvStorageEngine engine)
    {
        using var deadline = new CancellationTokenSource(TestTimeout);
        using (var db = Open(engine))
        {
            Setup(db, PartialRowCount, deadline.Token);
            db.Tables.ForceResumableSchemaMigrationForTests = true;
            db.Tables.ResumableSchemaMigrationPhaseTestHook = phase =>
            {
                if (phase == "page-checkpointed") throw new IOException("Injected after the page checkpoint.");
            };
            Assert.Throws<IOException>(() => Migrate(db, deadline.Token));
            db.Tables.ResumableSchemaMigrationPhaseTestHook = null;
            AssertOriginal(db, PartialRowCount);

            string journal = Path.Combine(_root, "tables", "rowstore", TableResumableSchemaMigrationJournal.DirectoryName);
            string stage = TableResumableSchemaMigrationJournal.StageDirectory(journal, 0);
            string parked = Path.Combine(_root, "missing-test-stage");
            Directory.Move(stage, parked);
            try
            {
                Assert.Throws<InvalidDataException>(() => Migrate(db, deadline.Token));
                AssertOriginal(db, PartialRowCount);
                AssertAllRows(db.Tables.Open("items"), PartialRowCount, deadline.Token);
            }
            finally
            {
                Directory.Move(parked, stage);
            }
        }

        using var repaired = Open(engine);
        repaired.Tables.ForceResumableSchemaMigrationForTests = true;
        Migrate(repaired, deadline.Token);
        Assert.Equal(["code"], repaired.Tables.Catalog.TryGet("items")!.PrimaryKey);
        AssertAllRows(repaired.Tables.Open("items"), PartialRowCount, deadline.Token);
    }

    private static void Setup(Tsdb db, int rowCount, CancellationToken cancellationToken)
    {
        SqlExecutor.Execute(db, "CREATE TABLE items(id INT,code INT,label STRING,bucket INT,PRIMARY KEY(id),CHECK(id+code=0))");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_label ON items(label)");
        SqlExecutor.Execute(db, "CREATE INDEX ix_bucket ON items(bucket)");
        TableStore store = db.Tables.Open("items");
        const int batchSize = 512;
        for (int start = 0; start < rowCount; start += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(batchSize, rowCount - start);
            var rows = new IReadOnlyList<object?>[count];
            for (int offset = 0; offset < count; offset++)
            {
                long id = start + offset - rowCount / 2;
                rows[offset] = new object?[] { id, -id, Label(id), id % 5 };
            }
            Assert.Equal(count, store.InsertMany(rows));
        }
        db.Tables.CheckpointAll();
    }

    private static void AssertOriginal(Tsdb db, int rowCount, string? label = null)
    {
        Assert.Equal(["id"], db.Tables.Catalog.TryGet("items")!.PrimaryKey);
        TableStore store = db.Tables.Open("items");
        Assert.Equal(rowCount, store.RowCount);
        Assert.Equal(label ?? Label(1), store.GetByPrimaryKey([1L])!.Values[2]);
    }

    private static void AssertAllRows(TableStore store, int rowCount, CancellationToken cancellationToken)
    {
        Assert.Equal(rowCount, store.RowCount);
        var seen = new bool[rowCount];
        int count = 0;
        foreach (TableRow row in store.EnumerateScan(rowCount + 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long id = Assert.IsType<long>(row.Values[0]);
            int ordinal = checked((int)id + rowCount / 2);
            Assert.InRange(ordinal, 0, rowCount - 1);
            Assert.False(seen[ordinal], $"重复的原主键 {id}。");
            seen[ordinal] = true;
            Assert.Equal(-id, row.Values[1]);
            Assert.Equal(Label(id), row.Values[2]);
            Assert.Equal(id % 5, row.Values[3]);
            count++;
        }
        Assert.Equal(rowCount, count);
    }

    private static void AssertIndexesAndCheck(Tsdb db)
    {
        TableStore store = db.Tables.Open("items");
        var unique = store.Schema.TryGetIndex("ux_label")!;
        var regular = store.Schema.TryGetIndex("ix_bucket")!;
        Assert.Equal(-50_000L, Assert.Single(store.GetByIndex(unique, [Label(50_000)])).Values[1]);
        var matching = store.GetByIndex(regular, [1L], limit: 4);
        Assert.Equal(4, matching.Count);
        Assert.All(matching, row => Assert.Equal(1L, row.Values[3]));

        Assert.Equal(TableConstraintException.UniqueViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO items(id,code,label,bucket) VALUES(60000,-60000,'id-1',1)")).ErrorCode);
        Assert.Equal(TableConstraintException.CheckViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO items(id,code,label,bucket) VALUES(60000,-59999,'check',1)")).ErrorCode);
    }

    private static void Migrate(Tsdb db, CancellationToken cancellationToken)
        => SqlExecutor.Execute(db, null, Migration, null, null, new SqlExecutionOptions { CancellationToken = cancellationToken });

    private static string Label(long id) => "id-" + id.ToString(CultureInfo.InvariantCulture);

    private Tsdb Open(KvStorageEngine engine) => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Kv = KvOptions.Default with
        {
            StorageEngine = engine,
            SyncWalOnEveryWrite = false,
            AutoCheckpointEnabled = false,
            ExpirerEnabled = false,
            CleanupEnabled = false,
        },
    });

    /// <summary>回收当前用例创建的独占目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
