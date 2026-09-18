using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlDmlReturningAndConflictTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-sql-dml-" + Guid.NewGuid().ToString("N"));

    public SqlDmlReturningAndConflictTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试清理不覆盖断言。 */ }
    }

    [Fact]
    public void ParseDml_WithReturningAndConflict_PreservesClauses()
    {
        var insert = Assert.IsType<InsertStatement>(SqlParser.Parse(
            "INSERT INTO items (id, value) VALUES (1, 10) ON CONFLICT (id) DO NOTHING RETURNING id"));
        Assert.Equal(["id"], insert.OnConflict!.TargetColumns);
        Assert.Equal(SqlOnConflictAction.DoNothing, insert.OnConflict.Action);
        Assert.Equal(["id"], insert.ReturningColumns);

        var update = Assert.IsType<UpdateStatement>(SqlParser.Parse(
            "UPDATE items SET value = value + 1 WHERE id = 1 RETURNING id, value"));
        Assert.Equal(["id", "value"], update.ReturningColumns);

        var delete = Assert.IsType<DeleteStatement>(SqlParser.Parse(
            "DELETE FROM items WHERE id = 1 RETURNING *"));
        Assert.Equal(["*"], delete.ReturningColumns);
    }

    [Fact]
    public void ExecuteUpdateReturning_ReturnsFinalRowsInScanOrder()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(db, "INSERT INTO items (id, value) VALUES (1, 10), (2, 20)");

        var result = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(db,
            "UPDATE items SET value = value + 1 WHERE id >= 1 RETURNING id, value"));

        Assert.Equal(2, result.RowsAffected);
        Assert.Equal(["id", "value"], result.Returning!.Columns);
        Assert.Equal(
            [
                new object?[] { 1L, 11L },
                new object?[] { 2L, 21L },
            ],
            result.Returning.Rows);
    }

    [Fact]
    public void ExecuteDeleteReturning_ReturnsDeletedImagesBeforeRemoval()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(db, "INSERT INTO items (id, value) VALUES (1, 10), (2, 20)");

        var result = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(db,
            "DELETE FROM items WHERE id >= 1 RETURNING id, value"));

        Assert.Equal(2, result.SeriesAffected);
        Assert.Equal(["id", "value"], result.Returning!.Columns);
        Assert.Equal(
            [
                new object?[] { 1L, 10L },
                new object?[] { 2L, 20L },
            ],
            result.Returning.Rows);
        var remaining = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM items"));
        Assert.Empty(remaining.Rows);
    }

    [Fact]
    public void ExecuteInsertOnConflictDoNothing_SkipsOnlyConflictsAndPreservesReturningOrder()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(db, "INSERT INTO items (id, value) VALUES (1, 10)");

        var result = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db,
            "INSERT INTO items (id, value) VALUES (1, 99), (2, 20), (1, 100) ON CONFLICT (id) DO NOTHING RETURNING id, value"));

        Assert.Equal(1, result.RowsInserted);
        Assert.Equal(
            [new object?[] { 2L, 20L }],
            result.Returning!.Rows);
    }

    [Fact]
    public void ExecuteInsertOnConflict_UnknownTargetRejectsBeforeWriting()
    {
        using var db = Open();
        CreateItemsTable(db);

        var error = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            "INSERT INTO items (id, value) VALUES (1, 10) ON CONFLICT (missing) DO NOTHING"));
        Assert.Contains("唯一索引", error.Message, StringComparison.Ordinal);

        var rows = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM items"));
        Assert.Empty(rows.Rows);
    }

    [Fact]
    public void ExecuteInsertOnConflict_InTransactionReadsBufferedRows()
    {
        using var db = Open();
        CreateItemsTable(db);

        var results = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO items (id, value) VALUES (1, 10);
            INSERT INTO items (id, value) VALUES (1, 99), (2, 20)
                ON CONFLICT (id) DO NOTHING RETURNING id;
            COMMIT;
            """);

        var conflictResult = Assert.IsType<InsertExecutionResult>(results[2]);
        Assert.Equal(1, conflictResult.RowsInserted);
        Assert.Equal([new object?[] { 2L }], conflictResult.Returning!.Rows);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static void CreateItemsTable(Tsdb db)
        => SqlExecutor.Execute(db, """
            CREATE TABLE items (
                id INT,
                value INT,
                PRIMARY KEY (id))
            """);
}
