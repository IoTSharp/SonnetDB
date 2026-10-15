using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>验证主键迁移预检与 raw 应用保持键置换、索引和原值恢复。</summary>
public sealed class TableStoreSchemaMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-schema-migration-plan-" + Guid.NewGuid().ToString("N"));

    /// <summary>目标键与其他行旧键互换时，新行不能被稍后处理的旧键删除覆盖。</summary>
    [Fact]
    public void SchemaMigration_SwappedOldAndNewKeys_PreservesBothRowsAndIndexes()
    {
        using var db = Open();
        Setup(db);
        var store = db.Tables.Open("items");
        var updated = store.Schema.WithPrimaryKey(["code"]);
        var plan = Prepare(store, updated);
        Assert.Equal(1L, store.GetByPrimaryKey([1L])!.Values[0]);
        store.ApplySchemaMigrationRaw(plan);
        store.PublishSchemaMigration(plan);
        Assert.Equal(new object?[] { 2L, 1L, "two" }, store.GetByPrimaryKey([1L])!.Values);
        Assert.Equal(new object?[] { 1L, 2L, "one" }, store.GetByPrimaryKey([2L])!.Values);
        Assert.Equal(2, store.RowCount);
        Assert.Equal(2L, Assert.Single(store.GetByIndex(updated.TryGetIndex("ix_label")!, ["two"])).Values[0]);
    }

    /// <summary>raw 应用后的 undo 可重复恢复原行与索引，目录尚未发布。</summary>
    [Fact]
    public void SchemaMigration_RawApplyThenUndo_RestoresOriginalKeysIdempotently()
    {
        using var db = Open();
        Setup(db);
        var store = db.Tables.Open("items");
        var original = store.Schema;
        var plan = Prepare(store, original.WithPrimaryKey(["code"]));
        store.ApplySchemaMigrationRaw(plan);
        store.RestoreSchemaMigrationRaw(plan.Undo);
        store.RestoreSchemaMigrationRaw(plan.Undo);
        Assert.Same(original, store.Schema);
        Assert.Equal(new object?[] { 1L, 2L, "one" }, store.GetByPrimaryKey([1L])!.Values);
        Assert.Equal(new object?[] { 2L, 1L, "two" }, store.GetByPrimaryKey([2L])!.Values);
        Assert.Equal(2L, Assert.Single(store.GetByIndex(original.TryGetIndex("ix_label")!, ["two"])).Values[0]);
    }

    /// <summary>重复的新主键在任何 raw 应用前拒绝，原 schema、行和 WAL 不变。</summary>
    [Fact]
    public void SchemaMigration_DuplicateNewPrimaryKey_RejectsBeforeSourceMutation()
    {
        using var db = Open();
        Setup(db);
        SqlExecutor.Execute(db, "UPDATE items SET code=1 WHERE id=1");
        var store = db.Tables.Open("items");
        var original = store.Schema;
        long walLength = store.ActiveWalLengthForEvidence;
        var error = Assert.Throws<TableConstraintException>(() => Prepare(store, original.WithPrimaryKey(["code"])));
        Assert.Equal(TableConstraintException.UniqueViolation, error.ErrorCode);
        Assert.Equal(walLength, store.ActiveWalLengthForEvidence);
        Assert.Same(original, store.Schema);
        Assert.Equal(2, store.RowCount);
        Assert.NotNull(store.GetByPrimaryKey([1L]));
        Assert.NotNull(store.GetByPrimaryKey([2L]));
    }

    /// <summary>转换造成唯一索引碰撞时预检拒绝，不能靠持久重建阶段才发现冲突。</summary>
    [Fact]
    public void SchemaMigration_DuplicateUniqueIndex_RejectsBeforeSourceMutation()
    {
        using var db = Open();
        Setup(db);
        var store = db.Tables.Open("items");
        long walLength = store.ActiveWalLengthForEvidence;
        var error = Assert.Throws<TableConstraintException>(() => store.PrepareSchemaMigration(
            store.Schema.WithPrimaryKey(["code"]),
            (_, row) => new object?[] { row.Values[0], row.Values[1], "duplicate" },
            (_, _) => { }, () => throw new InvalidOperationException("本测试不分配无主键行身份。"),
            new TableSchemaMigrationBudget()));
        Assert.Equal(TableConstraintException.UniqueViolation, error.ErrorCode);
        Assert.Equal(walLength, store.ActiveWalLengthForEvidence);
        Assert.Equal("one", store.GetByPrimaryKey([1L])!.Values[2]);
        Assert.Equal("two", store.GetByPrimaryKey([2L])!.Values[2]);
    }

    /// <summary>剩余预算不足时，预检在大行磁盘读取和复制之前拒绝。</summary>
    [Fact]
    public void SchemaMigration_ExhaustedBudget_RejectsBeforeReadingDiskValues()
    {
        using var db = Open();
        Setup(db);
        var store = db.Tables.Open("items");
        db.Tables.CheckpointAll();
        byte[] catalog = File.ReadAllBytes(db.Tables.SchemaPath);
        long wal = store.ActiveWalLengthForEvidence;
        int reads = 0;
        store.ConfigureDiskReadTestHook(() => reads++);
        var budget = new TableSchemaMigrationBudget();
        budget.Reserve(TableSchemaMigrationBudget.MaxWorkingBytes);
        Assert.Throws<TableConstraintException>(() => store.PrepareSchemaMigration(
            store.Schema.WithPrimaryKey(["code"]), (_, row) => row.Values, (_, _) => { },
            TableRowIdentity.Create, budget));
        Assert.Equal(0, reads);
        store.ConfigureDiskReadTestHook(null);
        Assert.Equal(catalog, File.ReadAllBytes(db.Tables.SchemaPath));
        Assert.Equal(wal, store.ActiveWalLengthForEvidence);
        Assert.Equal(2, store.RowCount);
        Assert.Equal("one", store.GetByPrimaryKey([1L])!.Values[2]);
    }

    private static TableStore.SchemaMigrationPlan Prepare(TableStore store, TableSchema schema)
        => store.PrepareSchemaMigration(schema, (_, row) => row.Values, (_, _) => { },
            () => throw new InvalidOperationException("本测试不分配无主键行身份。"), new TableSchemaMigrationBudget());

    private static void Setup(Tsdb db)
    {
        SqlExecutor.Execute(db, "CREATE TABLE items(id INT,code INT,label STRING,PRIMARY KEY(id))");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ix_label ON items(label)");
        SqlExecutor.Execute(db, "INSERT INTO items(id,code,label) VALUES(1,2,'one'),(2,1,'two')");
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    /// <summary>回收本测试的独占目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
