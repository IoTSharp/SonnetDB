using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>PostgreSQL 19 风格 ON CONFLICT DO SELECT 的关系表回归。</summary>
public sealed class SqlOnConflictDoSelectTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-sql-do-select-" + Guid.NewGuid().ToString("N"));

    public SqlOnConflictDoSelectTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ParseDoSelect_PreservesWhereLockAndRequiresTargetAndReturning()
    {
        var statement = Assert.IsType<InsertStatement>(SqlParser.Parse(
            "INSERT INTO items (id, value) VALUES (1, 20) "
            + "ON CONFLICT (id) DO SELECT FOR UPDATE WHERE excluded.value > value "
            + "RETURNING id, value"));

        Assert.Equal(SqlOnConflictAction.DoSelect, statement.OnConflict!.Action);
        Assert.True(statement.OnConflict.SelectForUpdate);
        Assert.NotNull(statement.OnConflict.SelectWhere);
        Assert.Equal(["id", "value"], statement.ReturningColumns);

        Assert.Throws<SqlParseException>(() => SqlParser.Parse(
            "INSERT INTO items (id, value) VALUES (1, 20) "
            + "ON CONFLICT (id) DO SELECT"));
        Assert.Throws<SqlParseException>(() => SqlParser.Parse(
            "INSERT INTO items (id, value) VALUES (1, 20) "
            + "ON CONFLICT DO SELECT RETURNING id"));
    }

    [Fact]
    public void ExecuteDoSelect_MixedGetOrCreateReturnsExistingRowWithoutChangingRowVersion()
    {
        using var db = Open();
        SqlExecutor.Execute(db, """
            CREATE TABLE versioned_items (
                id INT,
                value INT,
                version INT ROWVERSION,
                PRIMARY KEY (id))
            """);
        SqlExecutor.Execute(db, "INSERT INTO versioned_items (id, value) VALUES (1, 10)");

        var result = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db,
            "INSERT INTO versioned_items (id, value) VALUES (1, 99), (2, 20) "
            + "ON CONFLICT (id) DO SELECT RETURNING id, value, version"));

        Assert.Equal(2, result.RowsInserted);
        Assert.Equal(
            [
                new object?[] { 1L, 10L, 1L },
                new object?[] { 2L, 20L, 1L },
            ],
            result.Returning!.Rows);
        var stored = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id, value, version FROM versioned_items ORDER BY id"));
        Assert.Equal(result.Returning.Rows, stored.Rows);
    }

    [Fact]
    public void ExecuteDoSelect_WithWhereReturnsOnlyMatchingExistingRowsAndInsertedRows()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(db, "INSERT INTO items (id, value) VALUES (1, 10), (2, 20)");

        var result = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db,
            "INSERT INTO items (id, value) VALUES (1, 11), (2, 19), (3, 30) "
            + "ON CONFLICT (id) DO SELECT WHERE excluded.value > value RETURNING id, value"));

        Assert.Equal(2, result.RowsInserted);
        Assert.Equal(
            [
                new object?[] { 1L, 10L },
                new object?[] { 3L, 30L },
            ],
            result.Returning!.Rows);
        Assert.Equal(20L, Value(db, "SELECT value FROM items WHERE id=2"));
    }

    [Fact]
    public void ExecuteDoSelect_WithMaterializationBudgetRejectsWholeStatement()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(db, "INSERT INTO items (id, value) VALUES (1, 10)");

        const string sql = "INSERT INTO items (id, value) VALUES (1, 99), (2, 20) "
            + "ON CONFLICT (id) DO SELECT RETURNING id, value";
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.ExecuteStatement(
            db,
            databaseName: null,
            SqlParser.Parse(sql),
            controlPlane: null,
            transaction: null,
            new SqlExecutionOptions { MaxMaterializedRows = 1 }));

        var rows = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT id, value FROM items ORDER BY id"));
        Assert.Equal([new object?[] { 1L, 10L }], rows.Rows);
    }

    [Fact]
    public void ExecuteDoSelect_InTransactionReadsBufferedRows()
    {
        using var db = Open();
        CreateItemsTable(db);
        var transaction = new SqlTransactionContext();
        try
        {
            Run(db, "INSERT INTO items (id, value) VALUES (1, 10)", transaction);
            var result = Assert.IsType<InsertExecutionResult>(Run(db,
                "INSERT INTO items (id, value) VALUES (1, 99) "
                + "ON CONFLICT (id) DO SELECT RETURNING id, value",
                transaction));

            Assert.Equal(1, result.RowsInserted);
            Assert.Equal([new object?[] { 1L, 10L }], result.Returning!.Rows);
            Run(db, "COMMIT", transaction);
        }
        finally
        {
            Finish(transaction);
        }

        Assert.Equal(10L, Value(db, "SELECT value FROM items WHERE id=1"));
    }

    [Fact]
    public void ExecuteDoSelectForUpdate_RetainsConflictLockUntilRollback()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(db, "INSERT INTO items (id, value) VALUES (1, 10)");
        var holder = new SqlTransactionContext();
        var observer = new SqlTransactionContext();
        try
        {
            var result = Assert.IsType<InsertExecutionResult>(Run(db,
                "INSERT INTO items (id, value) VALUES (1, 99) "
                + "ON CONFLICT (id) DO SELECT FOR UPDATE RETURNING id, value",
                holder));
            Assert.Equal([new object?[] { 1L, 10L }], result.Returning!.Rows);

            var error = Assert.Throws<SqlRowLockException>(() => Run(db,
                "SELECT id FROM items WHERE id=1 FOR UPDATE NOWAIT",
                observer));
            Assert.Equal(SqlRowLockException.NotAvailable, error.Code);

            Run(db, "ROLLBACK", holder);
            var unlocked = Assert.IsType<SelectExecutionResult>(Run(db,
                "SELECT id FROM items WHERE id=1 FOR UPDATE NOWAIT",
                observer));
            Assert.Equal([new object?[] { 1L }], unlocked.Rows);
        }
        finally
        {
            Finish(holder);
            Finish(observer);
        }

        Assert.Equal((0, 0, 0), db.Tables.RowLocks.Inspect());
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static void CreateItemsTable(Tsdb db)
        => SqlExecutor.Execute(db, """
            CREATE TABLE items (
                id INT,
                value INT,
                PRIMARY KEY (id))
            """);

    private static object? Run(
        Tsdb db,
        string sql,
        SqlTransactionContext? transaction = null)
        => SqlExecutor.ExecuteStatement(
            db,
            databaseName: null,
            SqlParser.Parse(sql),
            controlPlane: null,
            transaction,
            SqlExecutionOptions.Default);

    private static object? Value(Tsdb db, string sql)
        => Assert.Single(Assert.IsType<SelectExecutionResult>(Run(db, sql)).Rows)[0];

    private static void Finish(SqlTransactionContext transaction)
    {
        if (!transaction.IsCompleted)
            transaction.MarkCompleted();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
