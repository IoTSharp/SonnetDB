using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>迁移不能用新 schema 解释旧事务的缓冲行或主键。</summary>
public sealed class SqlPrimaryKeyMigrationTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-pk-tx-" + Guid.NewGuid().ToString("N"));

    /// <summary>纯 INSERT 即使没有首次读令牌，也必须拒绝把子行挂到迁移后的另一父行。</summary>
    [Theory]
    [InlineData(SqlTransactionIsolationLevel.ReadCommitted)]
    [InlineData(SqlTransactionIsolationLevel.Serializable)]
    public void Commit_BufferedInsertBeforePrimaryKeySwap_RejectsAndAllowsFreshTransaction(SqlTransactionIsolationLevel isolation)
    {
        using var db = Open();
        Setup(db);
        var transaction = new SqlTransactionContext(isolation);
        Execute(db, "INSERT INTO children(id,parent_id) VALUES(10,1)", transaction);
        Execute(db, "ALTER TABLE parents ALTER PRIMARY KEY(code)");
        AssertConflict(db, transaction);
        Assert.Empty(Select(db, "SELECT id FROM children").Rows);
        var fresh = new SqlTransactionContext(isolation);
        Execute(db, "INSERT INTO children(id,parent_id) VALUES(10,2)", fresh);
        Execute(db, "COMMIT", fresh);
        Assert.Equal(new object?[] { 1L, 2L }, Assert.Single(Select(db,
            "SELECT p.id,c.parent_id FROM children c JOIN parents p ON c.parent_id=p.code").Rows).ToArray());
    }

    /// <summary>所有缓冲写形状在定义或类型迁移后整笔拒绝，不能连带发布不相关表的写入。</summary>
    [Theory]
    [InlineData(SqlTransactionIsolationLevel.ReadCommitted, "insert", false)]
    [InlineData(SqlTransactionIsolationLevel.Serializable, "insert", false)]
    [InlineData(SqlTransactionIsolationLevel.ReadCommitted, "update", false)]
    [InlineData(SqlTransactionIsolationLevel.Serializable, "update", false)]
    [InlineData(SqlTransactionIsolationLevel.ReadCommitted, "delete", false)]
    [InlineData(SqlTransactionIsolationLevel.Serializable, "delete", false)]
    [InlineData(SqlTransactionIsolationLevel.ReadCommitted, "cascade", false)]
    [InlineData(SqlTransactionIsolationLevel.Serializable, "cascade", false)]
    [InlineData(SqlTransactionIsolationLevel.ReadCommitted, "merge", false)]
    [InlineData(SqlTransactionIsolationLevel.Serializable, "merge", false)]
    [InlineData(SqlTransactionIsolationLevel.ReadCommitted, "insert", true)]
    [InlineData(SqlTransactionIsolationLevel.Serializable, "insert", true)]
    [InlineData(SqlTransactionIsolationLevel.ReadCommitted, "update", true)]
    [InlineData(SqlTransactionIsolationLevel.Serializable, "update", true)]
    [InlineData(SqlTransactionIsolationLevel.ReadCommitted, "delete", true)]
    [InlineData(SqlTransactionIsolationLevel.Serializable, "delete", true)]
    [InlineData(SqlTransactionIsolationLevel.ReadCommitted, "cascade", true)]
    [InlineData(SqlTransactionIsolationLevel.Serializable, "cascade", true)]
    [InlineData(SqlTransactionIsolationLevel.ReadCommitted, "merge", true)]
    [InlineData(SqlTransactionIsolationLevel.Serializable, "merge", true)]
    public void Commit_BufferedWritesBeforeMigration_RejectsWholeTransaction(
        SqlTransactionIsolationLevel isolation, string operation, bool typeChange)
    {
        using (var db = Open())
        {
            Setup(db);
            Execute(db, "INSERT INTO children(id,parent_id) VALUES(10,1)");
            Execute(db, "CREATE TABLE unrelated(id INT,PRIMARY KEY(id))");
            var transaction = new SqlTransactionContext(isolation);
            Execute(db, "INSERT INTO unrelated(id) VALUES(99)", transaction);
            string sql = operation switch
            {
                "insert" or "merge" => "INSERT INTO children(id,parent_id) VALUES(11,1)",
                "update" => "UPDATE children SET parent_id=2 WHERE id=10",
                "delete" => "DELETE FROM children WHERE id=10",
                _ => "DELETE FROM parents WHERE id=1",
            };
            Execute(db, sql, transaction);
            if (operation == "merge") Execute(db, "UPDATE children SET parent_id=2 WHERE id=11", transaction);
            Execute(db, typeChange ? "ALTER TABLE parents ALTER COLUMN id TYPE DECIMAL(18,0)"
                : "ALTER TABLE parents ALTER PRIMARY KEY(code)");
            AssertConflict(db, transaction);
            Assert.Empty(Select(db, "SELECT id FROM unrelated").Rows);
            Assert.Equal(2, Select(db, "SELECT id FROM parents").Rows.Count);
            Assert.Equal(new object?[] { 10L, typeChange ? (object)1m : 2L }, Assert.Single(Select(db,
                "SELECT id,parent_id FROM children").Rows).ToArray());
        }
        using var reopened = Open();
        Assert.Empty(Select(reopened, "SELECT id FROM unrelated").Rows);
        Assert.Single(Select(reopened, "SELECT id FROM children").Rows);
        Assert.Equal(2, Select(reopened, "SELECT id FROM parents").Rows.Count);
    }

    /// <summary>新 schema 不得用于旧缓冲行的查询、更新归并或删除定位。</summary>
    [Theory]
    [InlineData(SqlTransactionIsolationLevel.ReadCommitted, "SELECT parent_id FROM children")]
    [InlineData(SqlTransactionIsolationLevel.Serializable, "SELECT parent_id FROM children")]
    [InlineData(SqlTransactionIsolationLevel.ReadCommitted, "UPDATE children SET parent_id=2 WHERE id=10")]
    [InlineData(SqlTransactionIsolationLevel.Serializable, "UPDATE children SET parent_id=2 WHERE id=10")]
    [InlineData(SqlTransactionIsolationLevel.ReadCommitted, "DELETE FROM children WHERE id=10")]
    [InlineData(SqlTransactionIsolationLevel.Serializable, "DELETE FROM children WHERE id=10")]
    public void Statement_ConsumesOldBufferAfterMigration_RejectsBeforeOverlay(
        SqlTransactionIsolationLevel isolation, string statement)
    {
        using var db = Open();
        Setup(db);
        var transaction = new SqlTransactionContext(isolation);
        Execute(db, "INSERT INTO children(id,parent_id) VALUES(10,1)", transaction);
        Execute(db, "ALTER TABLE parents ALTER PRIMARY KEY(code)");
        var error = Assert.Throws<TableConstraintException>(() => Execute(db, statement, transaction));
        Assert.Equal(TableConstraintException.SerializationConflict, error.ErrorCode);
        Assert.Empty(Select(db, "SELECT id FROM children").Rows);
        AssertConflict(db, transaction);
    }

    /// <summary>保存点撤销的首次写绑定随写集删除，避免不存在的旧写错误阻止新 schema 写入。</summary>
    [Theory]
    [InlineData(SqlTransactionIsolationLevel.ReadCommitted)]
    [InlineData(SqlTransactionIsolationLevel.Serializable)]
    public void Savepoint_RollsBackOnlyOldSchemaBinding_AllowsNewSchemaInsert(SqlTransactionIsolationLevel isolation)
    {
        using var db = Open();
        Setup(db);
        var transaction = new SqlTransactionContext(isolation);
        var savepoint = transaction.CreateSavepoint();
        Execute(db, "INSERT INTO children(id,parent_id) VALUES(10,1)", transaction);
        transaction.RollbackTo(savepoint);
        Execute(db, "ALTER TABLE parents ALTER PRIMARY KEY(code)");
        Execute(db, "INSERT INTO children(id,parent_id) VALUES(10,2)", transaction);
        Execute(db, "COMMIT", transaction);
        Assert.Equal(2L, Assert.Single(Select(db, "SELECT parent_id FROM children").Rows)[0]);
    }

    /// <summary>保存点前仍存在的写集保留首次绑定，回滚后不能被新的 schema 覆盖。</summary>
    [Theory]
    [InlineData(SqlTransactionIsolationLevel.ReadCommitted)]
    [InlineData(SqlTransactionIsolationLevel.Serializable)]
    public void Savepoint_RetainsEarlierWriteSchema_StillRejectsMigration(SqlTransactionIsolationLevel isolation)
    {
        using var db = Open();
        Setup(db);
        var transaction = new SqlTransactionContext(isolation);
        Execute(db, "INSERT INTO children(id,parent_id) VALUES(10,1)", transaction);
        var savepoint = transaction.CreateSavepoint();
        Execute(db, "INSERT INTO children(id,parent_id) VALUES(11,2)", transaction);
        transaction.RollbackTo(savepoint);
        Execute(db, "ALTER TABLE parents ALTER PRIMARY KEY(code)");
        AssertConflict(db, transaction);
        Assert.Empty(Select(db, "SELECT id FROM children").Rows);
    }

    /// <summary>迁移不相关的表不会使已有缓冲写发生虚假的 schema 冲突。</summary>
    [Theory]
    [InlineData(SqlTransactionIsolationLevel.ReadCommitted)]
    [InlineData(SqlTransactionIsolationLevel.Serializable)]
    public void Commit_MigrationAffectsOnlyUnrelatedTables_PreservesBufferedWrite(SqlTransactionIsolationLevel isolation)
    {
        using var db = Open();
        Setup(db);
        Execute(db, "CREATE TABLE unrelated(id INT,PRIMARY KEY(id))");
        var transaction = new SqlTransactionContext(isolation);
        Execute(db, "INSERT INTO unrelated(id) VALUES(99)", transaction);
        Execute(db, "ALTER TABLE parents ALTER PRIMARY KEY(code)");
        Execute(db, "COMMIT", transaction);
        Assert.Equal(99L, Assert.Single(Select(db, "SELECT id FROM unrelated").Rows)[0]);
    }

    /// <summary>底层缓冲入口也拒绝覆盖最初的 schema 身份，级联展开必须先校验旧键。</summary>
    [Theory]
    [InlineData(SqlTransactionIsolationLevel.ReadCommitted)]
    [InlineData(SqlTransactionIsolationLevel.Serializable)]
    public void MergeOrCascade_AfterMigration_RejectsOriginalSchemaBinding(SqlTransactionIsolationLevel isolation)
    {
        using var db = Open();
        Setup(db);
        var transaction = new SqlTransactionContext(isolation);
        Execute(db, "INSERT INTO children(id,parent_id) VALUES(10,1)", transaction);
        Execute(db, "ALTER TABLE parents ALTER PRIMARY KEY(code)");
        var schema = db.Tables.Catalog.TryGet("children")!;
        Assert.Equal(TableConstraintException.SerializationConflict, Assert.Throws<TableConstraintException>(() =>
            transaction.AddOrMergeTableMutation(schema, new TableRowMutation(null, [11L, 2L]))).ErrorCode);
        Assert.Equal(TableConstraintException.SerializationConflict, Assert.Throws<TableConstraintException>(() =>
            db.Tables.ExpandDeferredCascades(transaction)).ErrorCode);
        AssertConflict(db, transaction);
        Assert.Empty(Select(db, "SELECT id FROM children").Rows);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    private static void Setup(Tsdb db)
    {
        Execute(db, "CREATE TABLE parents(id INT,code INT,PRIMARY KEY(id))");
        Execute(db, "CREATE TABLE children(id INT,parent_id INT,PRIMARY KEY(id),FOREIGN KEY(parent_id) REFERENCES parents(id) ON DELETE CASCADE)");
        Execute(db, "INSERT INTO parents(id,code) VALUES(1,2),(2,1)");
    }
    private static object? Execute(Tsdb db, string sql, SqlTransactionContext? transaction = null)
        => SqlExecutor.ExecuteStatement(db, null, SqlParser.Parse(sql), null, transaction, SqlExecutionOptions.Default);
    private static SelectExecutionResult Select(Tsdb db, string sql, SqlTransactionContext? transaction = null)
        => Assert.IsType<SelectExecutionResult>(Execute(db, sql, transaction));
    private static void AssertConflict(Tsdb db, SqlTransactionContext transaction)
    {
        var failure = Assert.Throws<TableConstraintException>(() => Execute(db, "COMMIT", transaction));
        Assert.Equal(TableConstraintException.SerializationConflict, failure.ErrorCode);
        Assert.True(transaction.IsCompleted);
        Assert.False(transaction.WasCommitted);
    }

    /// <summary>删除测试专属目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
