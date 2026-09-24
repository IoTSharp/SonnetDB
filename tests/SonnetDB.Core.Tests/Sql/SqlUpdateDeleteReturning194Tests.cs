using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证关系表 UPDATE/DELETE RETURNING 的目标行、触发器与事务合同。</summary>
public sealed class SqlUpdateDeleteReturning194Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-returning-194-" + Guid.NewGuid().ToString("N"));

    public SqlUpdateDeleteReturning194Tests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试清理不覆盖断言。 */ }
    }

    [Fact]
    public void DeleteReturning_WithCascade_CountsOnlyTargetAndReturnsOldRowVersion()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE parents (id INT, rv INT ROWVERSION, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE children (id INT, parent_id INT, PRIMARY KEY (id), FOREIGN KEY (parent_id) REFERENCES parents (id) ON DELETE CASCADE)");
        SqlExecutor.Execute(db, "INSERT INTO parents (id) VALUES (1)");
        SqlExecutor.Execute(db, "INSERT INTO children (id, parent_id) VALUES (10, 1)");

        var deleted = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(db,
            "DELETE FROM parents WHERE id = 1 RETURNING id, rv"));

        Assert.Equal(1, deleted.SeriesAffected);
        Assert.Equal([new object?[] { 1L, 1L }], deleted.Returning!.Rows);
        Assert.Empty(Select(db, "SELECT id FROM children").Rows);
    }

    [Fact]
    public async Task DeleteReturning_ConcurrentChangeAfterPredicate_RejectsStaleOldImage()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE items (id INT, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO items (id, value) VALUES (1, 10)");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        db.Functions.RegisterScalar("pause_delete", _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("DELETE predicate was not released.");
            return 1L;
        }, minArgumentCount: 1, maxArgumentCount: 1);

        var pending = Task.Run(() => Record.Exception(() => SqlExecutor.Execute(db,
            "DELETE FROM items WHERE pause_delete(value) = 1 RETURNING id, value")));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            SqlExecutor.Execute(db, "UPDATE items SET value = 20 WHERE id = 1");
        }
        finally
        {
            release.Set();
        }

        var error = await pending;
        Assert.IsType<TableConstraintException>(error);
        Assert.Equal([new object?[] { 1L, 20L }], Select(db, "SELECT id, value FROM items").Rows);
    }

    [Fact]
    public void UpdateReturning_BeforeAndAfterTriggers_ExposeFinalStatementImage()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE items (id INT, name STRING, amount INT DEFAULT 2, rv INT ROWVERSION, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE audit_rows (id INT, name STRING, rv INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO items (id, name, amount) VALUES (1, 'before', 5)");
        SqlExecutor.Execute(db, "CREATE TRIGGER normalize BEFORE UPDATE ON items FOR EACH ROW LANGUAGE SQL AS BEGIN SET NEW.name = UPPER(NEW.name); END");
        SqlExecutor.Execute(db, "CREATE TRIGGER audit_update AFTER UPDATE ON items FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO audit_rows (id, name, rv) VALUES (NEW.id, NEW.name, NEW.rv); END");

        var updated = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(db,
            "UPDATE items SET name = 'after', amount = DEFAULT WHERE id = 1 RETURNING id, name, amount, rv"));

        Assert.Equal(1, updated.RowsAffected);
        Assert.Equal([new object?[] { 1L, "AFTER", 2L, 2L }], updated.Returning!.Rows);
        Assert.Equal([new object?[] { 1L, "AFTER", 2L }], Select(db, "SELECT id, name, rv FROM audit_rows").Rows);
    }

    [Fact]
    public void DeleteReturning_TriggerAndRollback_LeaveNoCommittedEffects()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE items (id INT, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE audit_rows (id INT, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO items (id, value) VALUES (1, 10)");
        SqlExecutor.Execute(db, "CREATE TRIGGER audit_delete AFTER DELETE ON items FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO audit_rows (id, value) VALUES (OLD.id, OLD.value); END");

        var results = SqlExecutor.ExecuteScript(db, "BEGIN; DELETE FROM items WHERE id = 1 RETURNING id, value; ROLLBACK;");
        var deleted = Assert.IsType<DeleteExecutionResult>(results[1]);

        Assert.Equal(1, deleted.SeriesAffected);
        Assert.Equal([new object?[] { 1L, 10L }], deleted.Returning!.Rows);
        Assert.Equal([new object?[] { 1L, 10L }], Select(db, "SELECT id, value FROM items").Rows);
        Assert.Empty(Select(db, "SELECT id FROM audit_rows").Rows);
    }

    [Fact]
    public void UpdateDeleteReturning_CompositeKeyParameters_PreserveAffectedCounts()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE items (tenant INT, id INT, value INT, PRIMARY KEY (tenant, id))");
        SqlExecutor.Execute(db, "INSERT INTO items (tenant, id, value) VALUES (1, 1, 10), (2, 1, 20)");
        var parameters = new SqlParameters();
        parameters.AddNamed("tenant", 2L);
        parameters.AddNamed("id", 1L);

        var updated = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(db, null,
            "UPDATE items SET value = value + 1 WHERE tenant = @tenant AND id = @id RETURNING tenant, id, value",
            parameters));
        Assert.Equal(1, updated.RowsAffected);
        Assert.Equal([new object?[] { 2L, 1L, 21L }], updated.Returning!.Rows);

        var deleted = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(db, null,
            "DELETE FROM items WHERE tenant = @tenant AND id = @id RETURNING tenant, id, value",
            parameters));
        Assert.Equal(1, deleted.SeriesAffected);
        Assert.Equal([new object?[] { 2L, 1L, 21L }], deleted.Returning!.Rows);
        Assert.Equal([new object?[] { 1L, 1L, 10L }], Select(db, "SELECT tenant, id, value FROM items").Rows);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
}
