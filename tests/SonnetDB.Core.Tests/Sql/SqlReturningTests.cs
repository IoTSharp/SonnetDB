using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlReturningTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-sql-returning-" + Guid.NewGuid().ToString("N"));

    public SqlReturningTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试清理不覆盖断言。 */ }
    }

    [Fact]
    public void ParseInsert_WithReturningColumns_PreservesRequestedOrder()
    {
        var statement = Assert.IsType<InsertStatement>(SqlParser.Parse(
            "INSERT INTO items (name) VALUES ('pump') RETURNING name, id;"));

        Assert.Equal(["name", "id"], statement.ReturningColumns);
    }

    [Fact]
    public void ParseInsert_WithDefaultValuesReturningStar_ReturnsWildcard()
    {
        var statement = Assert.IsType<InsertStatement>(SqlParser.Parse(
            "INSERT INTO items DEFAULT VALUES RETURNING *"));

        Assert.True(statement.IsDefaultValues);
        Assert.Equal(["*"], statement.ReturningColumns);
    }

    [Fact]
    public void ExecuteInsert_WithGeneratedKeyReturning_ReturnsCommittedRowsInInputOrder()
    {
        using var db = Open();
        CreateItemsTable(db);

        var inserted = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db, """
            INSERT INTO items (name)
            VALUES ('first'), ('second')
            RETURNING id, name
            """));

        Assert.Equal(2, inserted.RowsInserted);
        var returning = Assert.IsType<SelectExecutionResult>(inserted.Returning);
        Assert.Equal(["id", "name"], returning.Columns);
        Assert.Equal(
            [
                new object?[] { 1L, "first" },
                new object?[] { 2L, "second" },
            ],
            returning.Rows);
    }

    [Fact]
    public void ExecuteInsert_DefaultValuesReturningStar_ReturnsDefaultsAndRowVersion()
    {
        using var db = Open();
        CreateGeneratedItemsTable(db);

        var inserted = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db,
            "INSERT INTO generated_items DEFAULT VALUES RETURNING *"));

        var returning = Assert.IsType<SelectExecutionResult>(inserted.Returning);
        Assert.Equal(["id", "name", "version"], returning.Columns);
        Assert.Equal(new object?[] { 1L, "generated", 1L }, Assert.Single(returning.Rows));
    }

    [Fact]
    public void ExecuteInsert_DefaultValuesReturningStarInTransaction_ReturnsFinalCommittedRow()
    {
        using var db = Open();
        CreateGeneratedItemsTable(db);

        var results = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO generated_items DEFAULT VALUES RETURNING *;
            COMMIT;
            """);

        var inserted = Assert.IsType<InsertExecutionResult>(results[1]);
        var returning = Assert.IsType<SelectExecutionResult>(inserted.Returning);
        Assert.Equal(["id", "name", "version"], returning.Columns);
        Assert.Equal(new object?[] { 1L, "generated", 1L }, Assert.Single(returning.Rows));

        var committed = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id, name, version FROM generated_items"));
        Assert.Equal(Assert.Single(returning.Rows), Assert.Single(committed.Rows));
    }

    [Fact]
    public void ExecuteInsert_WithUnknownReturningColumn_RejectsBeforeWriting()
    {
        using var db = Open();
        CreateItemsTable(db);

        var error = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            "INSERT INTO items (name) VALUES ('not-written') RETURNING missing"));

        Assert.Contains("missing", error.Message, StringComparison.Ordinal);
        var rows = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SELECT id FROM items"));
        Assert.Empty(rows.Rows);

        var inserted = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db,
            "INSERT INTO items (name) VALUES ('written') RETURNING id"));
        Assert.Equal(1L, Assert.Single(inserted.Returning!.Rows)[0]);
    }

    [Fact]
    public void ExecuteInsert_InRolledBackTransaction_ReturnsReservedValueWithoutReusingIt()
    {
        using var db = Open();
        CreateItemsTable(db);

        var results = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO items (name) VALUES ('rolled-back') RETURNING id;
            ROLLBACK;
            """);

        var rolledBackInsert = Assert.IsType<InsertExecutionResult>(results[1]);
        Assert.Equal(1L, Assert.Single(rolledBackInsert.Returning!.Rows)[0]);

        var committedInsert = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db,
            "INSERT INTO items (name) VALUES ('committed') RETURNING id"));
        Assert.Equal(2L, Assert.Single(committedInsert.Returning!.Rows)[0]);
    }

    [Fact]
    public void ExecuteInsert_OnNonTableReturning_RejectsUnsupportedTarget()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");

        var error = Assert.Throws<NotSupportedException>(() => SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, usage) VALUES (1000, 'node-1', 1.5) RETURNING time"));

        Assert.Contains("仅支持关系表", error.Message, StringComparison.Ordinal);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static void CreateItemsTable(Tsdb db)
        => SqlExecutor.Execute(db, """
            CREATE TABLE items (
                id INT AUTO_INCREMENT,
                name STRING NOT NULL,
                PRIMARY KEY (id))
            """);

    private static void CreateGeneratedItemsTable(Tsdb db)
        => SqlExecutor.Execute(db, """
            CREATE TABLE generated_items (
                id INT AUTO_INCREMENT,
                name STRING NOT NULL DEFAULT 'generated',
                version INT ROWVERSION,
                PRIMARY KEY (id))
            """);
}
