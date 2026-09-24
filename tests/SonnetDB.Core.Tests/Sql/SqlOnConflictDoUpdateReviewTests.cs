using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// #184 ON CONFLICT DO UPDATE 的隔离回归探针。
/// </summary>
public sealed class SqlOnConflictDoUpdateReviewTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-sql-conflict-review-" + Guid.NewGuid().ToString("N"));

    public SqlOnConflictDoUpdateReviewTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Fact]
    public void ParseDoUpdate_PreservesActionAssignmentsAndExcludedQualifier()
    {
        var statement = Assert.IsType<SonnetDB.Sql.Ast.InsertStatement>(SqlParser.Parse(
            "INSERT INTO items (id, value) VALUES (1, 20) ON CONFLICT (id) DO UPDATE SET value = excluded.value + 1 RETURNING id, value"));

        Assert.NotNull(statement.OnConflict);
        Assert.Equal(SonnetDB.Sql.Ast.SqlOnConflictAction.DoUpdate, statement.OnConflict!.Action);
        var assignment = Assert.Single(statement.OnConflict.UpdateAssignments);
        Assert.Equal("value", assignment.ColumnName);
        Assert.Contains("excluded", assignment.Value.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDoUpdate_WithoutConflictTarget_RejectsExplicitly()
    {
        Assert.Throws<SqlParseException>(() => SqlParser.Parse(
            "INSERT INTO items (id, value) VALUES (1, 20) "
            + "ON CONFLICT DO UPDATE SET value = excluded.value"));
    }

    [Fact]
    public void ExecuteDoUpdate_UsesExcludedValueAndReturnsUpdatedRow()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(db, "INSERT INTO items (id, value) VALUES (1, 10)");

        var result = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db,
            "INSERT INTO items (id, value) VALUES (1, 20) ON CONFLICT (id) DO UPDATE SET value = excluded.value + 1 RETURNING id, value"));

        Assert.Equal(1, result.RowsInserted);
        Assert.Equal([new object?[] { 1L, 21L }], result.Returning!.Rows);
        var row = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id, value FROM items"));
        Assert.Equal([new object?[] { 1L, 21L }], row.Rows);
    }

    [Fact]
    public void ExecuteDoUpdate_InTransactionUsesSameExcludedValue()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(db, "INSERT INTO items (id, value) VALUES (1, 10)");

        var results = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO items (id, value) VALUES (1, 20)
                ON CONFLICT (id) DO UPDATE SET value = excluded.value + 1 RETURNING id, value;
            COMMIT;
            """);

        var result = Assert.IsType<InsertExecutionResult>(results[1]);
        Assert.Equal([new object?[] { 1L, 21L }], result.Returning!.Rows);
        var row = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id, value FROM items"));
        Assert.Equal([new object?[] { 1L, 21L }], row.Rows);
    }

    [Fact]
    public void ExecuteDoUpdate_WithWhere_UpdatesOnlyMatchingConflicts()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(db, "INSERT INTO items (id, value) VALUES (1, 10), (2, 20)");

        var result = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db,
            "INSERT INTO items (id, value) VALUES (1, 11), (2, 19), (3, 30) "
            + "ON CONFLICT (id) DO UPDATE SET value = excluded.value "
            + "WHERE excluded.value > value RETURNING id, value"));

        Assert.Equal(2, result.RowsInserted);
        Assert.Equal(
            [new object?[] { 1L, 11L }, new object?[] { 3L, 30L }],
            result.Returning!.Rows);
        var rows = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id, value FROM items ORDER BY id"));
        Assert.Equal(
            [new object?[] { 1L, 11L }, new object?[] { 2L, 20L }, new object?[] { 3L, 30L }],
            rows.Rows);
    }

    [Fact]
    public void ExecuteDoUpdate_WithParameterizedWhere_BindsPredicate()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(db, "INSERT INTO items (id, value) VALUES (1, 10)");

        var result = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: null,
            "INSERT INTO items (id, value) VALUES (@id, @value) "
            + "ON CONFLICT (id) DO UPDATE SET value = excluded.value "
            + "WHERE value < @limit RETURNING id, value",
            new SqlParameters().AddNamed("id", 1L).AddNamed("value", 22L).AddNamed("limit", 11L)));

        Assert.Equal(1, result.RowsInserted);
        Assert.Equal([new object?[] { 1L, 22L }], result.Returning!.Rows);
    }

    [Fact]
    public void ExecuteDoUpdate_WithWhereInTransaction_SkipsAndUpdatesBufferedRows()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(db, "INSERT INTO items (id, value) VALUES (1, 10)");

        var results = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO items (id, value) VALUES (1, 9)
                ON CONFLICT (id) DO UPDATE SET value = excluded.value
                WHERE excluded.value > value RETURNING id, value;
            INSERT INTO items (id, value) VALUES (1, 12)
                ON CONFLICT (id) DO UPDATE SET value = excluded.value
                WHERE excluded.value > value RETURNING id, value;
            COMMIT;
            """);

        Assert.Empty(Assert.IsType<InsertExecutionResult>(results[1]).Returning!.Rows);
        Assert.Equal([new object?[] { 1L, 12L }],
            Assert.IsType<InsertExecutionResult>(results[2]).Returning!.Rows);
        Assert.Equal([new object?[] { 1L, 12L }],
            Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
                "SELECT id, value FROM items")).Rows);
    }

    [Fact]
    public void ExecuteDoUpdate_WithInvalidWhere_RejectsBeforeUnrelatedInsert()
    {
        using var db = Open();
        CreateItemsTable(db);

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            "INSERT INTO items (id, value) VALUES (1, 10) "
            + "ON CONFLICT (id) DO UPDATE SET value = excluded.value "
            + "WHERE other.value > 0"));
        Assert.Empty(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM items")).Rows);
    }

    [Fact]
    public void ExecuteDoUpdate_InTransactionUsesGeneratedExcludedRowVersion()
    {
        using var db = Open();
        SqlExecutor.Execute(db, """
            CREATE TABLE versioned_items (
                id INT,
                marker INT,
                version INT ROWVERSION,
                PRIMARY KEY (id))
            """);
        SqlExecutor.Execute(db, "INSERT INTO versioned_items (id, marker) VALUES (1, 99)");

        var results = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO versioned_items (id, marker)
                VALUES (1, 20)
                ON CONFLICT (id) DO UPDATE SET marker = excluded.version
                RETURNING marker, version;
            COMMIT;
            """);

        var result = Assert.IsType<InsertExecutionResult>(results[1]);
        Assert.Equal([new object?[] { 1L, 2L }], result.Returning!.Rows);
        var row = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT marker, version FROM versioned_items WHERE id = 1"));
        Assert.Equal([new object?[] { 1L, 2L }], row.Rows);
    }

    [Fact]
    public void ExecuteDoUpdate_InTransactionRejectsMissingRequiredCandidate()
    {
        using var db = Open();
        SqlExecutor.Execute(db, """
            CREATE TABLE required_tx_items (
                id INT,
                value INT NOT NULL,
                PRIMARY KEY (id))
            """);
        SqlExecutor.Execute(db, "INSERT INTO required_tx_items (id, value) VALUES (1, 10)");

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO required_tx_items (id)
                VALUES (1)
                ON CONFLICT (id) DO UPDATE SET value = 11;
            COMMIT;
            """));

        var row = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT id, value FROM required_tx_items"));
        Assert.Equal([new object?[] { 1L, 10L }], row.Rows);
    }

    [Fact]
    public void QueueInsert_ConflictWithoutDatabaseContext_ThrowsNotSupported()
    {
        using var db = Open();
        CreateItemsTable(db);
        var schema = db.Tables.Catalog.TryGet("items")!;
        var statement = Assert.IsType<SonnetDB.Sql.Ast.InsertStatement>(SqlParser.Parse(
            "INSERT INTO items (id, value) VALUES (1, 20) ON CONFLICT (id) DO NOTHING"));

        Assert.Throws<NotSupportedException>(() => TableSqlExecutor.QueueInsert(
            new SqlTransactionContext(),
            statement,
            schema));
    }

    [Fact]
    public void ExecuteDoUpdate_RejectsMissingRequiredCandidateBeforeConflictUpdate()
    {
        using var db = Open();
        SqlExecutor.Execute(db, """
            CREATE TABLE required_items (
                id INT,
                value INT NOT NULL,
                PRIMARY KEY (id))
            """);
        SqlExecutor.Execute(db, "INSERT INTO required_items (id, value) VALUES (1, 10)");

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            "INSERT INTO required_items (id) VALUES (1) ON CONFLICT (id) DO UPDATE SET value = excluded.value"));
    }

    [Fact]
    public void ExecuteDoUpdate_WithDuplicateAssignmentNamesIsRejected()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(db, "INSERT INTO items (id, value) VALUES (1, 10)");

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            "INSERT INTO items (id, value) VALUES (1, 20) ON CONFLICT (id) DO UPDATE SET value = 2, value = 3"));
    }

    [Fact]
    public void ExecuteDoUpdate_WithDuplicateTargetRowsIsRejectedAtomically()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(db, "INSERT INTO items (id, value) VALUES (1, 10)");

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            "INSERT INTO items (id, value) VALUES (1, 20), (1, 30) "
            + "ON CONFLICT (id) DO UPDATE SET value = excluded.value"));

        var row = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT id, value FROM items"));
        Assert.Equal([new object?[] { 1L, 10L }], row.Rows);
    }

    [Fact]
    public void ExecuteDoUpdate_InTransactionWithDuplicateTargetRowsIsRejectedAtomically()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(db, "INSERT INTO items (id, value) VALUES (1, 10)");

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO items (id, value) VALUES (1, 20), (1, 30)
                ON CONFLICT (id) DO UPDATE SET value = excluded.value;
            COMMIT;
            """));

        var row = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT id, value FROM items"));
        Assert.Equal([new object?[] { 1L, 10L }], row.Rows);
    }

    [Fact]
    public void ExecuteDoUpdate_WithDuplicateUniqueIndexTargetRowsIsRejectedAtomically()
    {
        using var db = Open();
        CreateUniqueItemsTable(db);
        SqlExecutor.Execute(db, "INSERT INTO unique_items (id, code, value) VALUES (1, 'a', 10)");

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            "INSERT INTO unique_items (id, code, value) VALUES (2, 'a', 20), (3, 'a', 30) "
            + "ON CONFLICT (code) DO UPDATE SET value = excluded.value"));

        var row = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT id, code, value FROM unique_items"));
        Assert.Equal([new object?[] { 1L, "a", 10L }], row.Rows);
    }

    [Fact]
    public void ExecuteDoUpdate_InTransactionWithDuplicateUniqueIndexTargetRowsIsRejectedAtomically()
    {
        using var db = Open();
        CreateUniqueItemsTable(db);
        SqlExecutor.Execute(db, "INSERT INTO unique_items (id, code, value) VALUES (1, 'a', 10)");

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO unique_items (id, code, value) VALUES (2, 'a', 20), (3, 'a', 30)
                ON CONFLICT (code) DO UPDATE SET value = excluded.value;
            COMMIT;
            """));

        var row = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT id, code, value FROM unique_items"));
        Assert.Equal([new object?[] { 1L, "a", 10L }], row.Rows);
    }

    [Fact]
    public void ExecuteDoUpdate_WhenFirstCandidateInsertsAndSecondConflictsIsRejectedAtomically()
    {
        using var db = Open();
        CreateItemsTable(db);

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            "INSERT INTO items (id, value) VALUES (1, 20), (1, 30) "
            + "ON CONFLICT (id) DO UPDATE SET value = excluded.value"));

        var row = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT id, value FROM items"));
        Assert.Empty(row.Rows);
    }

    [Fact]
    public void ExecuteDoUpdate_InTransactionWhenFirstCandidateInsertsAndSecondConflictsIsRejectedAtomically()
    {
        using var db = Open();
        CreateItemsTable(db);

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO items (id, value) VALUES (1, 20), (1, 30)
                ON CONFLICT (id) DO UPDATE SET value = excluded.value;
            COMMIT;
            """));

        var row = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT id, value FROM items"));
        Assert.Empty(row.Rows);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static void CreateItemsTable(Tsdb db)
        => SqlExecutor.Execute(db, """
            CREATE TABLE items (
                id INT,
                value INT,
                PRIMARY KEY (id))
            """);

    private static void CreateUniqueItemsTable(Tsdb db)
    {
        SqlExecutor.Execute(db, """
            CREATE TABLE unique_items (
                id INT,
                code STRING,
                value INT,
                PRIMARY KEY (id))
            """);
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_unique_items_code ON unique_items (code)");
    }
}
