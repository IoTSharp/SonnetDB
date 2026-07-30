using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlAutoIncrementTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-sql-auto-increment-" + Guid.NewGuid().ToString("N"));

    public SqlAutoIncrementTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试清理不覆盖断言。 */ }
    }

    [Theory]
    [InlineData("AUTO_INCREMENT")]
    [InlineData("AUTOINCREMENT")]
    [InlineData("IDENTITY")]
    [InlineData("AUTO_INCREMENT NOT NULL")]
    [InlineData("NOT NULL AUTO_INCREMENT")]
    public void ParseCreateTable_WithAutoIncrementSpelling_MarksColumnAndForcesNotNull(string spelling)
    {
        var statement = Assert.IsType<CreateTableStatement>(SqlParser.Parse(
            $"CREATE TABLE items (id INT {spelling}, name STRING, PRIMARY KEY (id))"));

        var column = Assert.Single(statement.Columns, static column => column.Name == "id");
        Assert.True(column.IsAutoIncrement);
        Assert.Equal(SqlDataType.Int64, column.DataType);
        Assert.Equal(ColumnNullability.NotNull, column.Nullability);
        Assert.Null(column.DefaultExpression);
    }

    [Theory]
    [InlineData("CREATE TABLE items (id STRING AUTO_INCREMENT, PRIMARY KEY (id))")]
    [InlineData("CREATE TABLE items (id INT NULL AUTO_INCREMENT, PRIMARY KEY (id))")]
    [InlineData("CREATE TABLE items (id INT AUTO_INCREMENT NULL, PRIMARY KEY (id))")]
    [InlineData("CREATE TABLE items (id INT AUTO_INCREMENT DEFAULT 1, PRIMARY KEY (id))")]
    [InlineData("CREATE TABLE items (id INT AUTO_INCREMENT ROWVERSION, PRIMARY KEY (id))")]
    public void ParseCreateTable_WithInvalidAutoIncrementDefinition_ThrowsSqlParseException(string sql)
    {
        var error = Assert.Throws<SqlParseException>(() => SqlParser.Parse(sql));

        Assert.Contains("AUTO_INCREMENT", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateTable_WithMultipleAutoIncrementColumns_ThrowsArgumentException()
    {
        using var db = Open();

        var error = Assert.Throws<ArgumentException>(() => SqlExecutor.Execute(db, """
            CREATE TABLE items (
                id INT AUTO_INCREMENT,
                sequence INT AUTO_INCREMENT,
                PRIMARY KEY (id))
            """));

        Assert.Contains("AUTO_INCREMENT", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Insert_WithOmittedNullAndDefaultAutoIncrementValues_AssignsSequence()
    {
        using var db = Open();
        CreateItemsTable(db);

        SqlExecutor.Execute(db, "INSERT INTO items (name) VALUES ('omitted')");
        SqlExecutor.Execute(db, "INSERT INTO items (id, name) VALUES (NULL, 'null')");
        SqlExecutor.Execute(db, "INSERT INTO items (id, name) VALUES (DEFAULT, 'default')");

        var result = Select(db, "SELECT id, name FROM items ORDER BY id");
        Assert.Equal(
            [
                new object?[] { 1L, "omitted" },
                new object?[] { 2L, "null" },
                new object?[] { 3L, "default" },
            ],
            result.Rows);
    }

    [Fact]
    public void Insert_WithMultipleRows_AssignsConsecutiveValues()
    {
        using var db = Open();
        CreateItemsTable(db);

        var inserted = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db, """
            INSERT INTO items (name)
            VALUES ('first'), ('second'), ('third')
            """));

        Assert.Equal(3, inserted.RowsInserted);
        var result = Select(db, "SELECT id FROM items ORDER BY id");
        Assert.Equal([1L, 2L, 3L], result.Rows.Select(static row => (long)row[0]!).ToArray());
    }

    [Fact]
    public void Insert_DefaultValuesIntoGeneratedOnlyTable_AssignsValue()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE identities (id INT AUTO_INCREMENT, PRIMARY KEY (id))");

        SqlExecutor.Execute(db, "INSERT INTO identities DEFAULT VALUES");

        var row = Assert.Single(Select(db, "SELECT id FROM identities").Rows);
        Assert.Equal(1L, row[0]);
    }

    [Fact]
    public void Insert_WithExplicitValues_AdvancesButNeverLowersHighWaterMark()
    {
        using var db = Open();
        CreateItemsTable(db);

        SqlExecutor.Execute(db, "INSERT INTO items (id, name) VALUES (100, 'high')");
        SqlExecutor.Execute(db, "INSERT INTO items (name) VALUES ('after-high')");
        SqlExecutor.Execute(db, "INSERT INTO items (id, name) VALUES (25, 'low')");
        SqlExecutor.Execute(db, "INSERT INTO items (name) VALUES ('after-low')");

        var result = Select(db, "SELECT id, name FROM items ORDER BY id");
        Assert.Equal(
            [
                new object?[] { 25L, "low" },
                new object?[] { 100L, "high" },
                new object?[] { 101L, "after-high" },
                new object?[] { 102L, "after-low" },
            ],
            result.Rows);
    }

    [Fact]
    public void TableStore_WriteApis_UseSharedAutoIncrementSequence()
    {
        using var db = Open();
        CreateItemsTable(db);
        var store = db.Tables.Open("items");

        store.Insert([null, "insert"]);
        Assert.Equal(2, store.InsertMany(
        [
            new object?[] { null, "many-generated" },
            new object?[] { 20L, "many-explicit" },
        ]));
        store.Upsert([null, "upsert"]);
        Assert.Equal(1, store.ApplyBatch(
        [
            new TableRowMutation(PrimaryKeyValues: null, NewValues: [null, "batch"]),
        ]));

        var result = Select(db, "SELECT id FROM items ORDER BY id");
        Assert.Equal([1L, 2L, 20L, 21L, 22L], result.Rows.Select(static row => (long)row[0]!).ToArray());
    }

    [Fact]
    public void AutoIncrement_OnNonUniqueColumn_GeneratesValuesWithoutAddingConstraint()
    {
        using var db = Open();
        SqlExecutor.Execute(db, """
            CREATE TABLE events (
                event_key STRING,
                sequence INT AUTO_INCREMENT,
                PRIMARY KEY (event_key))
            """);

        SqlExecutor.Execute(db, "INSERT INTO events (event_key) VALUES ('generated')");
        SqlExecutor.Execute(db, "INSERT INTO events (event_key, sequence) VALUES ('explicit', 1)");

        var result = Select(db, "SELECT event_key, sequence FROM events ORDER BY event_key");
        Assert.Equal(
            [
                new object?[] { "explicit", 1L },
                new object?[] { "generated", 1L },
            ],
            result.Rows);
    }

    [Fact]
    public void Update_AutoIncrementColumn_ThrowsAndLeavesSequenceAvailable()
    {
        using var db = Open();
        SqlExecutor.Execute(db, """
            CREATE TABLE events (
                event_key STRING,
                sequence INT AUTO_INCREMENT,
                PRIMARY KEY (event_key))
            """);
        SqlExecutor.Execute(db, "INSERT INTO events (event_key) VALUES ('first')");

        var error = Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "UPDATE events SET sequence = 100 WHERE event_key = 'first'"));
        Assert.Contains("AUTO_INCREMENT", error.Message, StringComparison.Ordinal);

        SqlExecutor.Execute(db, "INSERT INTO events (event_key) VALUES ('second')");
        var result = Select(db, "SELECT sequence FROM events ORDER BY sequence");
        Assert.Equal([1L, 2L], result.Rows.Select(static row => (long)row[0]!).ToArray());
    }

    [Fact]
    public void TableStore_UpdateMutationWithExplicitValue_AdvancesHighWaterMark()
    {
        using var db = Open();
        SqlExecutor.Execute(db, """
            CREATE TABLE events (
                event_key STRING,
                sequence INT AUTO_INCREMENT,
                PRIMARY KEY (event_key))
            """);
        SqlExecutor.Execute(db, "INSERT INTO events (event_key) VALUES ('first')");

        var store = db.Tables.Open("events");
        Assert.Equal(1, store.ApplyBatch(
        [
            new TableRowMutation(
                PrimaryKeyValues: ["first"],
                NewValues: ["first", 100L]),
        ]));

        SqlExecutor.Execute(db, "INSERT INTO events (event_key) VALUES ('second')");
        var result = Select(db, "SELECT event_key, sequence FROM events ORDER BY sequence");
        Assert.Equal(
            [
                new object?[] { "first", 100L },
                new object?[] { "second", 101L },
            ],
            result.Rows);
    }

    [Fact]
    public void Metadata_ForAutoIncrementColumn_ExposesFlag()
    {
        using var db = Open();
        CreateItemsTable(db);

        var describe = Select(db, "DESCRIBE TABLE items");
        int autoIncrementOrdinal = Array.IndexOf(describe.Columns.ToArray(), "is_auto_increment");
        Assert.True((bool)describe.Rows.Single(static row => (string)row[0]! == "id")[autoIncrementOrdinal]!);
        Assert.False((bool)describe.Rows.Single(static row => (string)row[0]! == "name")[autoIncrementOrdinal]!);

        var informationSchema = Select(db, """
            SELECT is_auto_increment
            FROM information_schema.columns
            WHERE table_name = 'items' AND column_name = 'id'
            """);
        Assert.True((bool)Assert.Single(informationSchema.Rows)[0]!);
    }

    [Fact]
    public void Insert_AfterReopen_ContinuesPersistedSequence()
    {
        using (var db = Open())
        {
            CreateItemsTable(db);
            SqlExecutor.Execute(db, "INSERT INTO items (name) VALUES ('first')");
            SqlExecutor.Execute(db, "INSERT INTO items (id, name) VALUES (10, 'explicit')");
        }

        using var reopened = Open();
        SqlExecutor.Execute(reopened, "INSERT INTO items (name) VALUES ('after-reopen')");

        var result = Select(reopened, "SELECT id FROM items ORDER BY id");
        Assert.Equal([1L, 10L, 11L], result.Rows.Select(static row => (long)row[0]!).ToArray());
    }

    [Fact]
    public void Insert_AfterTransactionRollback_DoesNotReuseAllocatedValue()
    {
        using var db = Open();
        CreateItemsTable(db);

        SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO items (name) VALUES ('rolled-back');
            ROLLBACK;
            """);

        Assert.Empty(Select(db, "SELECT id FROM items").Rows);
        SqlExecutor.Execute(db, "INSERT INTO items (name) VALUES ('committed')");

        var row = Assert.Single(Select(db, "SELECT id, name FROM items").Rows);
        Assert.Equal(new object?[] { 2L, "committed" }, row);
    }

    [Fact]
    public void QueuedInserts_AcrossTruncate_RejectStaleReservationAndAvoidDuplicateKey()
    {
        using var db = Open();
        CreateItemsTable(db);
        var schema = db.Tables.Catalog.TryGet("items")!;
        var insert = Assert.IsType<InsertStatement>(
            SqlParser.Parse("INSERT INTO items (name) VALUES ('queued')"));
        var staleTransaction = new SqlTransactionContext();
        TableSqlExecutor.QueueInsert(db, staleTransaction, insert, schema, out _);

        SqlExecutor.Execute(db, "TRUNCATE TABLE items");

        var currentTransaction = new SqlTransactionContext();
        TableSqlExecutor.QueueInsert(db, currentTransaction, insert, schema, out _);
        var staleError = Assert.Throws<InvalidOperationException>(() =>
            TableSqlExecutor.CommitTransaction(db, staleTransaction));
        Assert.Contains("generation", staleError.Message, StringComparison.Ordinal);
        TableSqlExecutor.CommitTransaction(db, currentTransaction);

        var row = Assert.Single(Select(db, "SELECT id, name FROM items").Rows);
        Assert.Equal(new object?[] { 1L, "queued" }, row);
    }

    [Fact]
    public void Insert_WithAfterTrigger_ExposesGeneratedValueToNewRow()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(db, "CREATE TABLE item_audit (item_id INT, PRIMARY KEY (item_id))");
        SqlExecutor.Execute(db, """
            CREATE TRIGGER audit_item_insert AFTER INSERT ON items FOR EACH ROW
            LANGUAGE SQL AS BEGIN
                INSERT INTO item_audit (item_id) VALUES (NEW.id);
            END
            """);

        SqlExecutor.Execute(db, "INSERT INTO items (name) VALUES ('triggered')");

        var auditRow = Assert.Single(Select(db, "SELECT item_id FROM item_audit").Rows);
        Assert.Equal(1L, auditRow[0]);
    }

    [Fact]
    public void Insert_Concurrently_AssignsEveryValueExactlyOnce()
    {
        const int insertCount = 64;
        using var db = Open();
        CreateItemsTable(db);

        Parallel.For(0, insertCount, index =>
            SqlExecutor.Execute(db, $"INSERT INTO items (name) VALUES ('item-{index:D2}')"));

        var result = Select(db, "SELECT id FROM items ORDER BY id");
        Assert.Equal(
            Enumerable.Range(1, insertCount).Select(static value => (long)value).ToArray(),
            result.Rows.Select(static row => (long)row[0]!).ToArray());
    }

    [Fact]
    public void Insert_AfterLongMaxValue_ThrowsOverflowAndDoesNotAddRow()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(
            db,
            $"INSERT INTO items (id, name) VALUES ({long.MaxValue}, 'maximum')");

        Assert.Throws<OverflowException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO items (name) VALUES ('overflow')"));

        var row = Assert.Single(Select(db, "SELECT id, name FROM items").Rows);
        Assert.Equal(new object?[] { long.MaxValue, "maximum" }, row);
    }

    [Fact]
    public void Insert_BatchOverflow_PreservesValuesReservedBeforeFailure()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(
            db,
            $"INSERT INTO items (id, name) VALUES ({long.MaxValue - 1}, 'before-maximum')");

        Assert.Throws<OverflowException>(() => SqlExecutor.Execute(db, """
            INSERT INTO items (name)
            VALUES ('reserved-maximum'), ('overflow')
            """));
        Assert.Throws<OverflowException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO items (name) VALUES ('must-not-reuse-maximum')"));

        var row = Assert.Single(Select(db, "SELECT id, name FROM items").Rows);
        Assert.Equal(new object?[] { long.MaxValue - 1, "before-maximum" }, row);
    }

    [Fact]
    public void TruncateTable_AfterAutoIncrementRows_ResetsSequence()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(db, "INSERT INTO items (name) VALUES ('first'), ('second')");

        var truncated = Assert.IsType<RowsAffectedExecutionResult>(
            SqlExecutor.Execute(db, "TRUNCATE TABLE items"));
        Assert.Equal(2, truncated.RowsAffected);

        SqlExecutor.Execute(db, "INSERT INTO items (name) VALUES ('after-truncate')");
        var row = Assert.Single(Select(db, "SELECT id, name FROM items").Rows);
        Assert.Equal(new object?[] { 1L, "after-truncate" }, row);
    }

    [Fact]
    public void DeleteAll_AfterAutoIncrementRows_PreservesSequence()
    {
        using var db = Open();
        CreateItemsTable(db);
        SqlExecutor.Execute(db, "INSERT INTO items (name) VALUES ('first'), ('second')");

        var deleted = Assert.IsType<DeleteExecutionResult>(
            SqlExecutor.Execute(db, "DELETE FROM items WHERE TRUE"));
        Assert.Equal(2, deleted.SeriesAffected);

        SqlExecutor.Execute(db, "INSERT INTO items (name) VALUES ('after-delete')");
        var row = Assert.Single(Select(db, "SELECT id, name FROM items").Rows);
        Assert.Equal(new object?[] { 3L, "after-delete" }, row);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static void CreateItemsTable(Tsdb db)
        => SqlExecutor.Execute(db, """
            CREATE TABLE items (
                id INT AUTO_INCREMENT,
                name STRING NOT NULL,
                PRIMARY KEY (id))
            """);

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
}
