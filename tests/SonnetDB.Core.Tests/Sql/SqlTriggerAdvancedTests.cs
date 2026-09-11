using System.Buffers.Binary;
using SonnetDB.Backup;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;
using Xunit.Abstractions;
using System.Diagnostics;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlTriggerAdvancedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-m39-advanced-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
    private readonly ITestOutputHelper _output;

    public SqlTriggerAdvancedTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        _deadline.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = Path.Combine(_root, "db") });
    private object? Execute(Tsdb db, string sql, SqlTransactionContext? transaction = null, SqlExecutionOptions? options = null)
        => SqlExecutor.ExecuteStatement(db, null, SqlParser.Parse(sql), null, transaction,
            (options ?? SqlExecutionOptions.Default) with { CancellationToken = _deadline.Token });
    private SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(Execute(db, sql));

    private void Setup(Tsdb db)
    {
        Execute(db, "CREATE TABLE source_rows (id INT, value INT, PRIMARY KEY (id))");
        Execute(db, "CREATE TABLE totals (id INT, amount INT, calls INT, PRIMARY KEY (id))");
        Execute(db, "INSERT INTO totals (id, amount, calls) VALUES (1, 0, 0)");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(10_000)]
    public void StatementTrigger_BatchAndEmptySet_FiresOnceWithExactImages(int count)
    {
        using var db = Open();
        Setup(db);
        Execute(db, """
            CREATE TRIGGER inserted AFTER INSERT ON source_rows REFERENCING NEW TABLE AS incoming
            FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                UPDATE totals SET amount = amount + (SELECT COALESCE(SUM(value), 0) FROM incoming), calls = calls + 1 WHERE id = 1;
            END
            """);
        Execute(db, """
            CREATE TRIGGER updated AFTER UPDATE ON source_rows
            REFERENCING OLD TABLE AS previous NEW TABLE AS incoming FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                UPDATE totals SET amount = amount + (SELECT COALESCE(SUM(n.value - o.value), 0)
                    FROM incoming n JOIN previous o ON n.id = o.id), calls = calls + 1 WHERE id = 1;
            END
            """);
        Execute(db, """
            CREATE TRIGGER deleted AFTER DELETE ON source_rows REFERENCING OLD TABLE AS previous
            FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                UPDATE totals SET amount = amount - (SELECT COALESCE(SUM(value), 0) FROM previous), calls = calls + 1 WHERE id = 1;
            END
            """);
        string insert = count == 0
            ? "INSERT INTO source_rows (id, value) SELECT id, value FROM source_rows WHERE id < 0"
            : "INSERT INTO source_rows (id, value) VALUES " + string.Join(',', Enumerable.Range(1, count).Select(id => $"({id}, 2)"));
        Execute(db, insert);
        Assert.Equal(new object?[] { count * 2L, 1L }, Select(db, "SELECT amount, calls FROM totals").Rows[0]);
        Execute(db, "UPDATE source_rows SET value = value + 3 WHERE id > 0");
        Assert.Equal(new object?[] { count * 5L, 2L }, Select(db, "SELECT amount, calls FROM totals").Rows[0]);
        Execute(db, "DELETE FROM source_rows WHERE id > 0");
        Assert.Equal(new object?[] { 0L, 3L }, Select(db, "SELECT amount, calls FROM totals").Rows[0]);
        Assert.Equal(3, db.Routines.Diagnostics.GetMetrics().TriggerExecutions);
    }

    [Fact]
    public void StatementTrigger_InsertSelectAndChain_RestoresOuterSnapshot()
    {
        using var db = Open();
        Setup(db);
        Execute(db, "CREATE TABLE audit_rows (id INT, value INT, PRIMARY KEY (id))");
        Execute(db, """
            CREATE TRIGGER copied AFTER INSERT ON audit_rows REFERENCING NEW TABLE AS incoming
            FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                UPDATE totals SET calls = calls + (SELECT COUNT(*) FROM incoming) WHERE id = 1;
            END
            """);
        Execute(db, """
            CREATE TRIGGER inserted AFTER INSERT ON source_rows REFERENCING NEW TABLE AS incoming
            FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                INSERT INTO audit_rows (id, value) SELECT id, value FROM incoming WHERE value > 1;
                UPDATE totals SET amount = amount + (SELECT SUM(value) FROM incoming) WHERE id = 1;
            END
            """);
        Execute(db, "INSERT INTO source_rows (id, value) VALUES (1, 1), (2, 2), (3, 3)");
        Assert.Equal(new object?[] { 6L, 2L }, Select(db, "SELECT amount, calls FROM totals").Rows[0]);
        Assert.Equal(new object?[] { 2L, 3L }, Select(db, "SELECT id FROM audit_rows ORDER BY id").Rows.Select(row => row[0]));
        Assert.ThrowsAny<Exception>(() => Execute(db, "SELECT * FROM incoming"));
        Assert.DoesNotContain("incoming", db.Routines.TryGetTrigger("inserted")!.ObjectDependencies);
        Assert.Throws<RoutineExecutionException>(() => Execute(db, "DROP TABLE audit_rows"));
    }

    [Fact]
    public void StatementTrigger_EmptySetAndAfterRows_SeesFinalBufferedActions()
    {
        using var db = Open();
        Setup(db);
        Execute(db, """
            CREATE TRIGGER row_action AFTER UPDATE ON source_rows FOR EACH ROW LANGUAGE SQL AS BEGIN
                UPDATE totals SET amount = amount + 1 WHERE id = 1;
            END
            """);
        Execute(db, """
            CREATE TRIGGER statement_action AFTER UPDATE ON source_rows FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                UPDATE totals SET calls = calls + amount + 1 WHERE id = 1;
            END
            """);
        Execute(db, "UPDATE source_rows SET value = 1 WHERE id > 0");
        Execute(db, "INSERT INTO source_rows (id, value) VALUES (1, 0), (2, 0)");
        Execute(db, "UPDATE source_rows SET value = 1 WHERE id > 0");
        Assert.Equal(new object?[] { 2L, 4L }, Select(db, "SELECT amount, calls FROM totals").Rows[0]);
    }

    [Fact]
    public void StatementTrigger_TransactionScope_OnlyCapturesCurrentStatement()
    {
        using var db = Open();
        Setup(db);
        Execute(db, """
            CREATE TRIGGER inserted AFTER INSERT ON source_rows REFERENCING NEW TABLE AS incoming
            FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                UPDATE totals SET amount = amount + (SELECT COUNT(*) FROM incoming), calls = calls + 1 WHERE id = 1;
            END
            """);
        var transaction = Assert.IsType<SqlTransactionContext>(Execute(db, "BEGIN"));
        Execute(db, "INSERT INTO source_rows (id, value) VALUES (1, 1)", transaction);
        Execute(db, "INSERT INTO source_rows (id, value) VALUES (2, 2), (3, 3)", transaction);
        Execute(db, "COMMIT", transaction);
        Assert.Equal(new object?[] { 3L, 2L }, Select(db, "SELECT amount, calls FROM totals").Rows[0]);
    }

    [Theory]
    [InlineData(1, 1_000_000)]
    [InlineData(100, 200)]
    public void StatementTrigger_TransitionBudget_RollsBackAndReleasesScope(int rows, long bytes)
    {
        using var db = Open();
        Setup(db);
        Execute(db, """
            CREATE TRIGGER inserted AFTER INSERT ON source_rows FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                UPDATE totals SET calls = calls + 1 WHERE id = 1;
            END
            """);
        var exception = Assert.Throws<RoutineExecutionException>(() => Execute(db,
            "INSERT INTO source_rows (id, value) VALUES (1, 1), (2, 2)", options: new SqlExecutionOptions
            { MaxTriggerTransitionRows = rows, MaxTriggerTransitionBytes = bytes }));
        Assert.Equal(RoutineErrorCodes.TransitionLimit, exception.Code);
        Assert.Empty(Select(db, "SELECT * FROM source_rows").Rows);
        Execute(db, "INSERT INTO source_rows (id, value) VALUES (3, 3)");
        Assert.Equal(1L, Select(db, "SELECT calls FROM totals").Rows[0][0]);
    }

    [Fact]
    public void StatementTrigger_NestedBudget_CountsActiveParentImages()
    {
        using var db = Open();
        Setup(db);
        Execute(db, "CREATE TABLE audit_rows (id INT, PRIMARY KEY (id))");
        Execute(db, """
            CREATE TRIGGER inner_action AFTER INSERT ON audit_rows FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                UPDATE totals SET calls = calls + 1 WHERE id = 1;
            END
            """);
        Execute(db, """
            CREATE TRIGGER outer_action AFTER INSERT ON source_rows FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                INSERT INTO audit_rows (id) VALUES (1);
            END
            """);
        var error = Assert.Throws<RoutineExecutionException>(() => Execute(db,
            "INSERT INTO source_rows (id, value) VALUES (1, 1), (2, 2)", options: new SqlExecutionOptions { MaxTriggerTransitionRows = 2 }));
        Assert.Equal(RoutineErrorCodes.TransitionLimit, error.Code);
        Assert.Empty(Select(db, "SELECT * FROM source_rows").Rows);
        Assert.Empty(Select(db, "SELECT * FROM audit_rows").Rows);
    }

    [Fact]
    public void StatementTrigger_Failure_RollsBackEarlierActionsAndCallerSavepoint()
    {
        using var db = Open();
        Setup(db);
        Execute(db, """
            CREATE TRIGGER inserted AFTER INSERT ON source_rows REFERENCING NEW TABLE AS incoming
            FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                UPDATE totals SET calls = calls + 1 WHERE id = 1;
                UPDATE totals SET amount = 1 / (SELECT MIN(value) FROM incoming) WHERE id = 1;
            END
            """);
        var transaction = Assert.IsType<SqlTransactionContext>(Execute(db, "BEGIN"));
        Execute(db, "INSERT INTO source_rows (id, value) VALUES (1, 1)", transaction);
        Assert.Throws<RoutineExecutionException>(() => Execute(db, "INSERT INTO source_rows (id, value) VALUES (2, 0)", transaction));
        Execute(db, "COMMIT", transaction);
        Assert.Single(Select(db, "SELECT * FROM source_rows").Rows);
        Assert.Equal(new object?[] { 1L, 1L }, Select(db, "SELECT amount, calls FROM totals").Rows[0]);
        Assert.DoesNotContain(db.Routines.Diagnostics.SnapshotAudit(), row => row.Outcome == "pending");
    }

    [Fact]
    public void BeforeTrigger_Normalization_DefaultsSequentialAssignmentsAndRowVersion()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE items (id INT AUTO_INCREMENT, name STRING NOT NULL, amount INT DEFAULT 2, rv INT ROWVERSION, PRIMARY KEY (id), CHECK (amount >= 0))");
        Execute(db, """
            CREATE TRIGGER normalize BEFORE INSERT ON items FOR EACH ROW LANGUAGE SQL AS BEGIN
                SET NEW.name = LOWER(COALESCE(NEW.name, 'UNKNOWN'));
                SET NEW.amount = NEW.amount + NEW.id;
                SET NEW.amount = NEW.amount * 2;
            END
            """);
        var result = Assert.IsType<InsertExecutionResult>(Execute(db, "INSERT INTO items (name) VALUES (NULL), ('HELLO') RETURNING *"));
        Assert.Equal(new object?[] { 1L, "unknown", 6L, 1L }, result.Returning!.Rows[0]);
        Assert.Equal(new object?[] { 2L, "hello", 8L, 1L }, result.Returning.Rows[1]);
        Execute(db, """
            CREATE TRIGGER update_normalize BEFORE UPDATE ON items FOR EACH ROW WHEN (NEW.amount < 0) LANGUAGE SQL AS BEGIN
                SET NEW.amount = OLD.amount + 1;
                SET NEW.name = UPPER(NEW.name);
            END
            """);
        Execute(db, "UPDATE items SET amount = -1 WHERE id = 1 AND rv = 1");
        Assert.Equal(new object?[] { "UNKNOWN", 7L, 2L }, Select(db, "SELECT name, amount, rv FROM items WHERE id = 1").Rows[0]);
        Assert.Throws<RoutineExecutionException>(() => Execute(db, "UPDATE items SET amount = -1 WHERE id = 1 AND rv = 1"));
    }

    [Fact]
    public void BeforeTrigger_RewrittenKeys_StillChecksForeignKeysAndUniqueConstraints()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE parent_rows (id INT, PRIMARY KEY (id))");
        Execute(db, "INSERT INTO parent_rows (id) VALUES (2)");
        Execute(db, "CREATE TABLE child_rows (id INT, parent_id INT, amount INT, PRIMARY KEY (id), FOREIGN KEY (parent_id) REFERENCES parent_rows (id), CHECK (amount >= 0))");
        Execute(db, """
            CREATE TRIGGER normalize BEFORE INSERT ON child_rows FOR EACH ROW LANGUAGE SQL AS BEGIN
                SET NEW.id = NEW.id + 10;
                SET NEW.parent_id = NEW.parent_id + 1;
            END
            """);
        Execute(db, "INSERT INTO child_rows (id, parent_id, amount) VALUES (1, 1, 1)");
        Assert.Equal(11L, Select(db, "SELECT id FROM child_rows").Rows[0][0]);
        Assert.Throws<RoutineExecutionException>(() => Execute(db, "INSERT INTO child_rows (id, parent_id, amount) VALUES (1, 1, 1)"));
        Assert.Throws<RoutineExecutionException>(() => Execute(db, "INSERT INTO child_rows (id, parent_id, amount) VALUES (2, 8, 1)"));
        Assert.Throws<RoutineExecutionException>(() => Execute(db, "INSERT INTO child_rows (id, parent_id, amount) VALUES (2, 1, -1)"));
        Assert.Single(Select(db, "SELECT * FROM child_rows").Rows);
    }

    [Fact]
    public void BeforeTrigger_OrderAndStatementAfter_UsesRewrittenImagesAndPersists()
    {
        using (var db = Open())
        {
            Setup(db);
            Execute(db, "CREATE TRIGGER add_value BEFORE INSERT ON source_rows FOR EACH ROW LANGUAGE SQL AS BEGIN SET NEW.value = NEW.value + 1; END");
            Execute(db, "CREATE TRIGGER multiply_value BEFORE INSERT ON source_rows FOR EACH ROW FOLLOWS add_value LANGUAGE SQL AS BEGIN SET NEW.value = NEW.value * 10; END");
            Execute(db, """
                CREATE TRIGGER inserted AFTER INSERT ON source_rows REFERENCING NEW TABLE AS incoming
                FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                    UPDATE totals SET amount = amount + (SELECT SUM(value) FROM incoming) WHERE id = 1;
                END
                """);
            Assert.Throws<RoutineExecutionException>(() => Execute(db, "ALTER TRIGGER inserted FOLLOWS add_value"));
            Execute(db, "INSERT INTO source_rows (id, value) VALUES (1, 1)");
            Assert.Equal(20L, Select(db, "SELECT amount FROM totals").Rows[0][0]);
            Execute(db, "ALTER TRIGGER multiply_value PRECEDES add_value");
            Execute(db, "ALTER TRIGGER add_value DISABLE");
        }
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(Path.Combine(_root, "db", "routines", "routines.sdbrtn")).AsSpan(8, 4)));
        using var reopened = Open();
        Execute(reopened, "INSERT INTO source_rows (id, value) VALUES (2, 1)");
        Assert.Equal(30L, Select(reopened, "SELECT amount FROM totals").Rows[0][0]);
        Assert.Equal(SqlTriggerTiming.Before, reopened.Routines.TryGetTrigger("add_value")!.Timing);
        Assert.Equal(SqlTriggerLevel.Statement, reopened.Routines.TryGetTrigger("inserted")!.Level);
        Assert.Equal("incoming", reopened.Routines.TryGetTrigger("inserted")!.NewTableName);
    }

    [Fact]
    public void BeforeTrigger_LaterRowFailure_RestoresCallerSavepointAndAudits()
    {
        using var db = Open();
        Setup(db);
        Execute(db, "CREATE TRIGGER validate_value BEFORE INSERT ON source_rows FOR EACH ROW LANGUAGE SQL AS BEGIN SET NEW.value = 10 / NEW.value; END");
        var transaction = Assert.IsType<SqlTransactionContext>(Execute(db, "BEGIN"));
        Execute(db, "INSERT INTO source_rows (id, value) VALUES (1, 2)", transaction);
        Assert.Throws<RoutineExecutionException>(() => Execute(db,
            "INSERT INTO source_rows (id, value) VALUES (2, 2), (3, 0)", transaction));
        Execute(db, "COMMIT", transaction);
        Assert.Equal(new object?[] { 1L, 5L }, Assert.Single(Select(db, "SELECT * FROM source_rows").Rows));
        Assert.Equal(new[] { "committed", "rolled_back", "failed" },
            db.Routines.Diagnostics.SnapshotAudit().Select(row => row.Outcome));
    }

    [Fact]
    public void StatementTrigger_RecursiveEmptyDml_RejectsAndRollsBack()
    {
        using var db = Open();
        Setup(db);
        Execute(db, """
            CREATE TRIGGER recursive_action AFTER UPDATE ON source_rows FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                UPDATE totals SET calls = calls + 1 WHERE id = 1;
                UPDATE source_rows SET value = 1 WHERE id < 0;
            END
            """);
        var error = Assert.Throws<RoutineExecutionException>(() => Execute(db, "UPDATE source_rows SET value = 1 WHERE id > 0"));
        Assert.Equal(RoutineErrorCodes.TriggerRecursion, error.Code);
        Assert.Equal(0L, Select(db, "SELECT calls FROM totals").Rows[0][0]);
    }

    [Fact]
    public void StatementTrigger_AliasCollisionAfterCreation_RejectsWithoutReadingPhysicalTable()
    {
        using var db = Open();
        Setup(db);
        Execute(db, """
            CREATE TRIGGER inserted AFTER INSERT ON source_rows REFERENCING NEW TABLE AS incoming
            FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                UPDATE totals SET calls = calls + (SELECT COUNT(*) FROM incoming) WHERE id = 1;
            END
            """);
        Execute(db, "CREATE TABLE incoming (id INT, PRIMARY KEY (id))");
        var error = Assert.Throws<RoutineExecutionException>(() => Execute(db, "INSERT INTO source_rows (id, value) VALUES (1, 1)"));
        Assert.Equal(RoutineErrorCodes.Dependency, error.Code);
        Assert.Empty(Select(db, "SELECT * FROM source_rows").Rows);
        Execute(db, "DROP TABLE incoming");
        Execute(db, "INSERT INTO source_rows (id, value) VALUES (1, 1)");
        Assert.Equal(1L, Select(db, "SELECT calls FROM totals").Rows[0][0]);
    }

    [Fact]
    public void BeforeTrigger_UpdateKey_TransitionImagesRetainOldAndFinalKeys()
    {
        using var db = Open();
        Setup(db);
        Execute(db, "INSERT INTO source_rows (id, value) VALUES (1, 2), (2, 3)");
        Execute(db, "CREATE TRIGGER change_key BEFORE UPDATE ON source_rows FOR EACH ROW LANGUAGE SQL AS BEGIN SET NEW.id = NEW.id + 10; END");
        Execute(db, """
            CREATE TRIGGER key_difference AFTER UPDATE ON source_rows REFERENCING OLD TABLE AS previous NEW TABLE AS incoming
            FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                UPDATE totals SET amount = (SELECT SUM(id) FROM incoming) - (SELECT SUM(id) FROM previous) WHERE id = 1;
            END
            """);
        Execute(db, "UPDATE source_rows SET value = value + 1 WHERE id > 0");
        Assert.Equal(new object?[] { 11L, 12L }, Select(db, "SELECT id FROM source_rows ORDER BY id").Rows.Select(row => row[0]));
        Assert.Equal(20L, Select(db, "SELECT amount FROM totals").Rows[0][0]);
    }

    [Fact]
    public void StatementTrigger_InAndExistsSubqueries_BindTransitionSources()
    {
        using var db = Open();
        Setup(db);
        Execute(db, "CREATE TABLE copied_rows (id INT, value INT, PRIMARY KEY (id))");
        Execute(db, "INSERT INTO source_rows (id, value) VALUES (1, 1), (2, 2)");
        Execute(db, "INSERT INTO copied_rows (id, value) VALUES (1, 1), (2, 2)");
        Execute(db, """
            CREATE TRIGGER remove_copies AFTER DELETE ON source_rows REFERENCING OLD TABLE AS removed
            FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                DELETE FROM copied_rows WHERE id IN (SELECT id FROM removed);
                UPDATE totals SET calls = calls + 1 WHERE id = 1 AND EXISTS (SELECT id FROM removed);
            END
            """);
        Execute(db, "DELETE FROM source_rows WHERE id = 1");
        Assert.Equal(2L, Assert.Single(Select(db, "SELECT id FROM copied_rows").Rows)[0]);
        Execute(db, "DELETE FROM source_rows WHERE id = 1");
        Assert.Equal(1L, Select(db, "SELECT calls FROM totals").Rows[0][0]);
    }

    [Theory]
    [InlineData("SET NEW.id = 42")]
    [InlineData("SET NEW.rv = 42")]
    [InlineData("SET NEW.value = NEW.rv")]
    [InlineData("SET OLD.value = 42")]
    public void BeforeTrigger_GeneratedColumnsAndOld_AreReadOnly(string assignment)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE source_rows (id INT AUTO_INCREMENT, value INT, rv INT ROWVERSION, PRIMARY KEY (id))");
        Assert.ThrowsAny<Exception>(() => Execute(db,
            $"CREATE TRIGGER invalid_trigger BEFORE UPDATE ON source_rows FOR EACH ROW LANGUAGE SQL AS BEGIN {assignment}; END"));
        Assert.Null(db.Routines.TryGetTrigger("invalid_trigger"));
    }

    [Fact]
    public void AdvancedTriggers_BackupRestore_RetainsDefinitionsAndBehavior()
    {
        string backup = Path.Combine(_root, "backup");
        string restoredPath = Path.Combine(_root, "restored");
        using (var db = Open())
        {
            Setup(db);
            Execute(db, "CREATE TRIGGER normalize BEFORE INSERT ON source_rows FOR EACH ROW LANGUAGE SQL AS BEGIN SET NEW.value = NEW.value + 1; END");
            Execute(db, """
                CREATE TRIGGER inserted AFTER INSERT ON source_rows REFERENCING NEW TABLE AS incoming
                FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                    UPDATE totals SET amount = amount + (SELECT SUM(value) FROM incoming) WHERE id = 1;
                END
                """);
            new BackupService().Create(db, new BackupCreateOptions { DestinationDirectory = backup });
        }
        new BackupService().Restore(new BackupRestoreOptions { BackupDirectory = backup, TargetDirectory = restoredPath });
        using var restored = Tsdb.Open(new TsdbOptions { RootDirectory = restoredPath });
        Execute(restored, "INSERT INTO source_rows (id, value) VALUES (1, 2), (2, 3)");
        Assert.Equal(7L, Select(restored, "SELECT amount FROM totals").Rows[0][0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(10_000)]
    public void StatementTrigger_EquivalentCounterJourney_ReducesBodyExecutions(int count)
    {
        string values = string.Join(',', Enumerable.Range(1, count).Select(id => $"({id}, 2)"));
        foreach (bool statementLevel in new[] { false, true })
        {
            using var db = Tsdb.Open(new TsdbOptions { RootDirectory = Path.Combine(_root, statementLevel ? "statement" : "row") });
            Setup(db);
            Execute(db, statementLevel ? """
                CREATE TRIGGER count_rows AFTER INSERT ON source_rows REFERENCING NEW TABLE AS incoming
                FOR EACH STATEMENT LANGUAGE SQL AS BEGIN
                    UPDATE totals SET amount = amount + (SELECT SUM(value) FROM incoming), calls = calls + (SELECT COUNT(*) FROM incoming) WHERE id = 1;
                END
                """ : """
                CREATE TRIGGER count_rows AFTER INSERT ON source_rows FOR EACH ROW LANGUAGE SQL AS BEGIN
                    UPDATE totals SET amount = amount + NEW.value, calls = calls + 1 WHERE id = 1;
                END
                """);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            Execute(db, "INSERT INTO source_rows (id, value) VALUES " + values,
                options: new SqlExecutionOptions { MaxRoutineStatements = count + 1 });
            double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.Equal(new object?[] { 2L * count, (long)count }, Select(db, "SELECT amount, calls FROM totals").Rows[0]);
            long executions = db.Routines.Diagnostics.GetMetrics().TriggerExecutions;
            Assert.Equal(statementLevel ? 1 : count, executions);
            _output.WriteLine($"rows={count}; mode={(statementLevel ? "statement" : "row")}; elapsed_ms={milliseconds:F3}; allocated_bytes={allocated}; trigger_executions={executions}");
        }
    }

    [Theory]
    [InlineData("BEFORE DELETE ON source_rows FOR EACH ROW", "SET NEW.value = 1")]
    [InlineData("BEFORE INSERT ON source_rows FOR EACH STATEMENT", "SET NEW.value = 1")]
    [InlineData("BEFORE INSERT ON source_rows FOR EACH ROW", "UPDATE source_rows SET value = 1 WHERE id = 1")]
    [InlineData("BEFORE INSERT ON source_rows FOR EACH ROW", "SET NEW.value = (SELECT amount FROM totals)")]
    [InlineData("BEFORE INSERT ON source_rows FOR EACH ROW", "SET NEW.value = arbitrary_udf(NEW.value)")]
    [InlineData("BEFORE INSERT ON source_rows FOR EACH ROW", "SET NEW.value = OLD.value")]
    [InlineData("AFTER INSERT ON source_rows FOR EACH ROW", "SET NEW.value = 1")]
    [InlineData("AFTER INSERT ON source_rows FOR EACH STATEMENT", "INSERT INTO totals (id) VALUES (NEW.id)")]
    [InlineData("AFTER INSERT ON source_rows REFERENCING OLD TABLE AS old_rows FOR EACH STATEMENT", "DELETE FROM totals WHERE id = 1")]
    [InlineData("AFTER UPDATE ON source_rows REFERENCING NEW TABLE AS incoming FOR EACH ROW", "DELETE FROM totals WHERE id = 1")]
    [InlineData("AFTER UPDATE ON source_rows REFERENCING NEW TABLE AS incoming FOR EACH STATEMENT", "DELETE FROM incoming WHERE id = 1")]
    [InlineData("AFTER UPDATE ON source_rows REFERENCING NEW TABLE AS totals FOR EACH STATEMENT", "DELETE FROM totals WHERE id = 1")]
    public void CreateTrigger_UnsupportedContracts_RejectsBeforePersistence(string declaration, string body)
    {
        using var db = Open();
        Setup(db);
        Assert.ThrowsAny<Exception>(() => Execute(db, $"CREATE TRIGGER invalid_trigger {declaration} LANGUAGE SQL AS BEGIN {body}; END"));
        Assert.Null(db.Routines.TryGetTrigger("invalid_trigger"));
    }
}
