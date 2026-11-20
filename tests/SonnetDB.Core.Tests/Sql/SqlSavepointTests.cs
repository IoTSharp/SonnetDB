using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlSavepointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-savepoint-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SqlSavepoint_RollbackAndRelease_PreserveOnlyPostSavepointWrites()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.ExecuteScript(db, """
            CREATE TABLE items (id INT, value STRING, PRIMARY KEY (id));
            BEGIN;
            INSERT INTO items (id, value) VALUES (1, 'before');
            SAVEPOINT first;
            UPDATE items SET value = 'updated' WHERE id = 1;
            INSERT INTO items (id, value) VALUES (2, 'removed');
            ROLLBACK TO SAVEPOINT first;
            INSERT INTO items (id, value) VALUES (3, 'after');
            RELEASE SAVEPOINT first;
            COMMIT;
            """);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id, value FROM items ORDER BY id"));
        Assert.Collection(result.Rows,
            row => Assert.Equal(new object?[] { 1L, "before" }, row),
            row => Assert.Equal(new object?[] { 3L, "after" }, row));
    }

    [Fact]
    public void SqlSavepoint_DuplicateNamesUseNewest_AndRequireActiveTransaction()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var outside = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, "SAVEPOINT missing"));
        Assert.Contains("活动轻事务", outside.Message, StringComparison.Ordinal);
        SqlExecutor.Execute(db, "CREATE TABLE items (id INT, PRIMARY KEY (id))");
        SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO items (id) VALUES (1);
            SAVEPOINT same;
            INSERT INTO items (id) VALUES (2);
            SAVEPOINT same;
            INSERT INTO items (id) VALUES (3);
            ROLLBACK TO same;
            COMMIT;
            """);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM items ORDER BY id"));
        Assert.Collection(result.Rows,
            row => Assert.Equal(new object?[] { 1L }, row),
            row => Assert.Equal(new object?[] { 2L }, row));
        Assert.IsType<SavepointStatement>(SqlParser.Parse("SAVEPOINT \"CaseSensitive\""));
        Assert.IsType<RollbackToSavepointStatement>(SqlParser.Parse("ROLLBACK TO \"CaseSensitive\""));
        Assert.IsType<ReleaseSavepointStatement>(SqlParser.Parse("RELEASE SAVEPOINT \"CaseSensitive\""));
    }

    [Fact]
    public void SqlSavepoint_AfterDdlWorkspaceChange_RejectsPartialRollback()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var failure = Assert.Throws<NotSupportedException>(() => SqlExecutor.ExecuteScript(db, """
            CREATE TABLE items (id INT, PRIMARY KEY (id));
            BEGIN;
            SAVEPOINT before_ddl;
            ALTER TABLE items ADD COLUMN label STRING;
            ROLLBACK TO SAVEPOINT before_ddl;
            """));

        Assert.Contains("DDL workspace", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlSavepoint_DdlAfterSavepointRequiresFullRollback()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE items (id INT, PRIMARY KEY (id))");

        var exception = Assert.Throws<NotSupportedException>(() => SqlExecutor.ExecuteScript(db, """
            BEGIN;
            SAVEPOINT before_ddl;
            CREATE TABLE later (id INT, PRIMARY KEY (id));
            ROLLBACK TO before_ddl;
            """));
        Assert.Contains("DDL workspace", exception.Message, StringComparison.Ordinal);
        Assert.Null(db.Tables.Catalog.TryGet("later"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
