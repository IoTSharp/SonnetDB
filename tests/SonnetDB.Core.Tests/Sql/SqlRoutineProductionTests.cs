using System.Text;
using System.Text.Json;
using System.Buffers.Binary;
using System.IO.Hashing;
using SonnetDB.Routines;
using SonnetDB.Tables;
using SonnetDB.Backup;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlRoutineProductionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-m39-production-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("COMMIT", "committed", true)]
    [InlineData("ROLLBACK", "rolled_back", false)]
    public void Call_OuterTransaction_ReportsFinalOutcome(string ending, string outcome, bool succeeded)
    {
        using var db = Open();
        Setup(db);
        var transaction = Assert.IsType<SqlTransactionContext>(SqlExecutor.Execute(db, "BEGIN"));
        Execute(db, "CALL add_order(1)", transaction);
        var pending = db.Routines.Diagnostics.SnapshotAudit();
        Assert.Equal(2, pending.Count);
        Assert.All(pending, record => { Assert.Equal("pending", record.Outcome); Assert.False(record.Succeeded); });
        Assert.Equal(0, db.Routines.Diagnostics.GetMetrics().ProcedureFailures);
        Execute(db, ending, transaction);
        Assert.All(db.Routines.Diagnostics.SnapshotAudit(), record =>
        {
            Assert.Equal(outcome, record.Outcome);
            Assert.Equal(succeeded, record.Succeeded);
        });
        Assert.Equal(succeeded ? 1 : 0, Select(db, "SELECT * FROM orders").Rows.Count);
        Assert.Equal(succeeded ? 0 : 1, db.Routines.Diagnostics.GetMetrics().ProcedureFailures);
        Assert.Equal(succeeded ? 0 : 1, db.Routines.Diagnostics.GetMetrics().TriggerFailures);
    }

    [Fact]
    public void Call_NestedSuccessThenFailure_RollsBackAllAuditsExactlyOnce()
    {
        using var db = Open();
        Setup(db);
        SqlExecutor.Execute(db, """
            CREATE PROCEDURE outer_call() LANGUAGE SQL AS BEGIN
                CALL add_order(1);
                CALL add_order(1);
            END
            """);
        Assert.Throws<RoutineExecutionException>(() => SqlExecutor.Execute(db, "CALL outer_call()"));
        Assert.Empty(Select(db, "SELECT * FROM orders").Rows);
        Assert.Empty(Select(db, "SELECT * FROM audit_outbox").Rows);
        var metrics = db.Routines.Diagnostics.GetMetrics();
        Assert.Equal(3, metrics.ProcedureExecutions);
        Assert.Equal(3, metrics.ProcedureFailures);
        Assert.Equal(2, metrics.TriggerFailures);
        Assert.All(db.Routines.Diagnostics.SnapshotAudit(), record => Assert.False(record.Succeeded));
    }

    [Fact]
    public void Call_FailedSavepoint_PreservesEarlierMutationsAndAudit()
    {
        using var db = Open();
        Setup(db);
        SqlExecutor.Execute(db, """
            CREATE PROCEDURE fail_order() LANGUAGE SQL AS BEGIN
                CALL add_order(2);
                SELECT * FROM orders;
            END
            """);
        var transaction = Assert.IsType<SqlTransactionContext>(SqlExecutor.Execute(db, "BEGIN"));
        Execute(db, "CALL add_order(1)", transaction);
        Assert.Throws<RoutineExecutionException>(() => Execute(db, "CALL fail_order()", transaction,
            new SqlExecutionOptions { MaxRoutineResultRows = 1 }));
        Execute(db, "COMMIT", transaction);
        Assert.Equal(1L, Assert.Single(Select(db, "SELECT id FROM orders").Rows)[0]);
        var records = db.Routines.Diagnostics.SnapshotAudit();
        Assert.Equal(2, records.Count(record => record.Outcome == "committed"));
        Assert.Equal(2, records.Count(record => record.Outcome == "rolled_back"));
        Assert.Single(records, record => record.Outcome == "failed");
    }

    [Theory]
    [InlineData("BEGIN; CALL add_order(1);")]
    [InlineData("BEGIN; CALL add_order(1); SELECT * FROM missing_table;")]
    public void ExecuteScript_AbandonedTransaction_ResolvesPendingAudits(string script)
    {
        using var db = Open();
        Setup(db);
        Assert.ThrowsAny<Exception>(() => SqlExecutor.ExecuteScript(db, script));
        Assert.All(db.Routines.Diagnostics.SnapshotAudit(), record => Assert.Equal("rolled_back", record.Outcome));
        Assert.Empty(Select(db, "SELECT * FROM orders").Rows);
    }

    [Fact]
    public void Call_NestedSelect_CountsReturnedRowsOnce()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE values_table (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO values_table (id) VALUES (1)");
        SqlExecutor.Execute(db, "CREATE PROCEDURE inner_read() LANGUAGE SQL AS BEGIN SELECT * FROM values_table; END");
        SqlExecutor.Execute(db, "CREATE PROCEDURE outer_read() LANGUAGE SQL AS BEGIN CALL inner_read(); END");
        var result = Execute(db, "CALL outer_read()", null, new SqlExecutionOptions { MaxRoutineResultRows = 1 });
        Assert.Single(Assert.IsType<SelectExecutionResult>(result).Rows);
    }

    [Fact]
    public void Trigger_MoreThanAuditCapacity_RollbackCountsEveryInvocationOnce()
    {
        using var db = Open();
        Setup(db);
        string rows = string.Join(',', Enumerable.Range(1, 300).Select(static id => $"({id})"));
        SqlExecutor.ExecuteScript(db, $"BEGIN; INSERT INTO orders (id) VALUES {rows}; ROLLBACK;",
            new SqlExecutionOptions { MaxRoutineStatements = 400 });
        Assert.Equal(300, db.Routines.Diagnostics.GetMetrics().TriggerFailures);
        Assert.Equal(256, db.Routines.Diagnostics.SnapshotAudit().Count);
        Assert.All(db.Routines.Diagnostics.SnapshotAudit(), record => Assert.Equal("rolled_back", record.Outcome));
        using var export = new MemoryStream();
        db.Routines.Diagnostics.ExportAudit(export, "trigger", "order_audit", afterSequence: 298);
        using var json = JsonDocument.Parse(export.ToArray());
        Assert.Equal(2, json.RootElement.GetProperty("records").GetArrayLength());
        Assert.DoesNotContain("VALUES", Encoding.UTF8.GetString(export.ToArray()));
    }

    [Fact]
    public void Commit_CompletedTransaction_RejectsReplay()
    {
        using var db = Open();
        Setup(db);
        var transaction = Assert.IsType<SqlTransactionContext>(SqlExecutor.Execute(db, "BEGIN"));
        Execute(db, "CALL add_order(1)", transaction);
        Execute(db, "COMMIT", transaction);
        Assert.Throws<InvalidOperationException>(() => Execute(db, "COMMIT", transaction));
        Assert.Single(Select(db, "SELECT * FROM orders").Rows);
    }

    [Fact]
    public void Transaction_MergeAndSavepointUndo_PreservesRowsAndOrder()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE values_table (id INT, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO values_table (id, value) VALUES (1, 10), (2, 20)");
        SqlExecutor.Execute(db, """
            CREATE PROCEDURE merge_fail() LANGUAGE SQL AS BEGIN
                UPDATE values_table SET value = 99 WHERE id = 1;
                DELETE FROM values_table WHERE id = 3;
                INSERT INTO values_table (id, value) VALUES (3, 99);
                SELECT * FROM values_table;
            END
            """);
        var transaction = Assert.IsType<SqlTransactionContext>(SqlExecutor.Execute(db, "BEGIN"));
        Execute(db, "UPDATE values_table SET value = 11 WHERE id = 1", transaction);
        Execute(db, "INSERT INTO values_table (id, value) VALUES (3, 30)", transaction);
        Assert.Throws<RoutineExecutionException>(() => Execute(db, "CALL merge_fail()", transaction,
            new SqlExecutionOptions { MaxRoutineResultRows = 1 }));
        Execute(db, "COMMIT", transaction);
        Assert.Equal(new object?[] { 11L, 20L, 30L },
            Select(db, "SELECT value FROM values_table ORDER BY id").Rows.Select(static row => row[0]).ToArray());
    }

    [Fact]
    public void AlterTrigger_LifecycleAndOrder_PersistAndRetainDependencies()
    {
        long created;
        using (var db = Open())
        {
            SqlExecutor.Execute(db, "CREATE TABLE source_rows (id INT, PRIMARY KEY (id))");
            SqlExecutor.Execute(db, "CREATE TABLE total (id INT, value INT, PRIMARY KEY (id))");
            SqlExecutor.Execute(db, "INSERT INTO total (id, value) VALUES (1, 1)");
            SqlExecutor.Execute(db, """
                CREATE TRIGGER add_value AFTER INSERT ON source_rows FOR EACH ROW LANGUAGE SQL AS BEGIN
                    UPDATE total SET value = value + 1 WHERE id = 1;
                END
                """);
            SqlExecutor.Execute(db, """
                CREATE TRIGGER multiply_value AFTER INSERT ON source_rows FOR EACH ROW FOLLOWS add_value LANGUAGE SQL AS BEGIN
                    UPDATE total SET value = value * 10 WHERE id = 1;
                END
                """);
            created = db.Routines.TryGetTrigger("add_value")!.CreatedAtUtcTicks;
            SqlExecutor.Execute(db, "INSERT INTO source_rows (id) VALUES (1)");
            Assert.Equal(20L, Select(db, "SELECT value FROM total").Rows[0][0]);
            SqlExecutor.Execute(db, "ALTER TRIGGER multiply_value PRECEDES add_value");
            SqlExecutor.Execute(db, "ALTER TRIGGER add_value DISABLE");
            SqlExecutor.Execute(db, "ALTER TRIGGER add_value RENAME TO renamed_add");
            Assert.Throws<RoutineExecutionException>(() => SqlExecutor.Execute(db, "DROP TABLE total"));
        }
        using (var db = Open())
        {
            var definition = db.Routines.TryGetTrigger("renamed_add")!;
            Assert.False(definition.Enabled);
            Assert.Equal(created, definition.CreatedAtUtcTicks);
            SqlExecutor.Execute(db, "INSERT INTO source_rows (id) VALUES (2)");
            Assert.Equal(200L, Select(db, "SELECT value FROM total").Rows[0][0]);
            SqlExecutor.Execute(db, "ALTER TRIGGER renamed_add ENABLE");
            SqlExecutor.Execute(db, "INSERT INTO source_rows (id) VALUES (3)");
            Assert.Equal(2001L, Select(db, "SELECT value FROM total").Rows[0][0]);
        }
    }

    [Theory]
    [InlineData("ALTER TRIGGER order_audit FOLLOWS order_audit")]
    [InlineData("ALTER TRIGGER order_audit PRECEDES missing_trigger")]
    [InlineData("ALTER TRIGGER order_audit RENAME TO order_audit")]
    public void AlterTrigger_InvalidChange_PreservesDiskAndMemory(string sql)
    {
        using var db = Open();
        Setup(db);
        byte[] previous = File.ReadAllBytes(db.Routines.CatalogPath);
        Assert.ThrowsAny<Exception>(() => SqlExecutor.Execute(db, sql));
        Assert.Equal(previous, File.ReadAllBytes(db.Routines.CatalogPath));
        Assert.True(db.Routines.TryGetTrigger("order_audit")!.Enabled);
        SqlExecutor.Execute(db, "CALL add_order(1)");
        Assert.Single(Select(db, "SELECT * FROM audit_outbox").Rows);
    }

    [Fact]
    public void AlterTrigger_PersistFailure_KeepsPublishedDefinition()
    {
        using var db = Open();
        Setup(db);
        string temporary = db.Routines.CatalogPath + ".tmp";
        Directory.CreateDirectory(temporary);
        try
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(() => SqlExecutor.Execute(db, "ALTER TRIGGER order_audit DISABLE"));
            Assert.True(db.Routines.TryGetTrigger("order_audit")!.Enabled);
        }
        finally { Directory.Delete(temporary); }
        SqlExecutor.Execute(db, "CALL add_order(1)");
        Assert.Single(Select(db, "SELECT * FROM audit_outbox").Rows);
    }

    [Fact]
    public void AlterTrigger_ReadOnlyAndActiveTransaction_RejectChanges()
    {
        using var db = Open();
        Setup(db);
        Assert.Equal(RoutineErrorCodes.Forbidden, Assert.Throws<RoutineExecutionException>(() =>
            Execute(db, "ALTER TRIGGER order_audit DISABLE", null, new SqlExecutionOptions { CanWrite = false })).Code);
        Assert.Throws<NotSupportedException>(() => SqlExecutor.ExecuteScript(db, "BEGIN; ALTER TRIGGER order_audit DISABLE; COMMIT;"));
        Assert.True(db.Routines.TryGetTrigger("order_audit")!.Enabled);
    }

    [Fact]
    public void RoutineCatalog_VersionOne_UpgradesWithoutChangingOriginalOrder()
    {
        using (var db = Open()) { Setup(db); }
        string path = Path.Combine(_root, "routines", "routines.sdbrtn");
        byte[] current = File.ReadAllBytes(path);
        // 仅有一个触发器的已发布 v1 布局比 v2 少尾部的 enabled + order 九字节。
        byte[] legacy = new byte[current.Length - 9];
        current.AsSpan(0, current.Length - 16 - 9).CopyTo(legacy);
        current.AsSpan(current.Length - 16).CopyTo(legacy.AsSpan(legacy.Length - 16));
        BinaryPrimitives.WriteInt32LittleEndian(legacy.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(legacy.AsSpan(legacy.Length - 16),
            Crc32.HashToUInt32(legacy.AsSpan(32, legacy.Length - 48)));
        File.WriteAllBytes(path, legacy);
        using var reopened = Open();
        Assert.True(reopened.Routines.TryGetTrigger("order_audit")!.Enabled);
        SqlExecutor.Execute(reopened, "ALTER TRIGGER order_audit DISABLE");
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(path).AsSpan(8)));
    }

    [Fact]
    public void ExplainAndDiagnostics_ReadOnly_ValidateWithoutExecutingBody()
    {
        using var db = Open();
        Setup(db);
        var readOnly = new SqlExecutionOptions { CanWrite = false };
        Assert.Single(Assert.IsType<SelectExecutionResult>(Execute(db, "EXPLAIN PROCEDURE add_order", null, readOnly)).Rows);
        Assert.Single(Assert.IsType<SelectExecutionResult>(Execute(db, "EXPLAIN TRIGGER order_audit", null, readOnly)).Rows);
        Assert.Empty(db.Routines.Diagnostics.SnapshotAudit());
        Assert.Empty(Select(db, "SELECT * FROM orders").Rows);
        SqlExecutor.Execute(db, "CALL add_order(1)");
        Assert.Single(Assert.IsType<SelectExecutionResult>(
            Execute(db, "SHOW ROUTINE AUDIT FOR TRIGGER order_audit", null, readOnly)).Rows);
        var stats = Assert.IsType<SelectExecutionResult>(Execute(db, "SHOW ROUTINE STATS FOR PROCEDURE add_order", null, readOnly));
        Assert.Equal(1, stats.Rows[0][0]);
        Assert.NotNull(stats.Rows[0][5]);
        Assert.Throws<SqlParseException>(() => SqlParser.Parse("EXPLAIN ANALYZE PROCEDURE add_order"));
    }

    [Theory]
    [InlineData("UPDATE source_rows SET value = 20 WHERE id = 1", "UPDATE")]
    [InlineData("DELETE FROM source_rows WHERE id = 1", "DELETE")]
    public void Journal_IncompleteCompletion_RestoresExactRowsAndUniqueIndexes(string dml, string triggerEvent)
    {
        using (var db = Open())
        {
            SqlExecutor.Execute(db, "CREATE TABLE source_rows (id INT, value INT, PRIMARY KEY (id))");
            SqlExecutor.Execute(db, "CREATE UNIQUE INDEX value_index ON source_rows (value)");
            SqlExecutor.Execute(db, "CREATE TABLE audit_rows (id INT, PRIMARY KEY (id))");
            SqlExecutor.Execute(db, "INSERT INTO source_rows (id, value) VALUES (1, 10)");
            SqlExecutor.Execute(db, $"""
                CREATE TRIGGER track_change AFTER {triggerEvent} ON source_rows FOR EACH ROW LANGUAGE SQL AS BEGIN
                    INSERT INTO audit_rows (id) VALUES (OLD.id);
                END
                """);
            SqlExecutor.Execute(db, dml);
        }
        string journalPath = Path.Combine(_root, "tables", "transaction.sdbtxn");
        using (var file = new FileStream(journalPath, FileMode.Open, FileAccess.Write))
            file.SetLength(file.Length - 4);
        using (var reopened = Open())
        {
            Assert.Equal(10L, Assert.Single(Select(reopened, "SELECT value FROM source_rows WHERE id = 1").Rows)[0]);
            Assert.Empty(Select(reopened, "SELECT * FROM audit_rows").Rows);
            Assert.Single(Select(reopened, "SELECT * FROM source_rows WHERE value = 10").Rows);
            Assert.Empty(Select(reopened, "SELECT * FROM source_rows WHERE value = 20").Rows);
        }
        using var second = Open();
        Assert.Single(Select(second, "SELECT * FROM source_rows WHERE value = 10").Rows);
        Assert.Empty(Select(second, "SELECT * FROM audit_rows").Rows);
    }

    [Fact]
    public void Journal_CorruptedPayload_RejectsOpenBeforeApplyingRecovery()
    {
        using (var db = Open()) { Setup(db); SqlExecutor.Execute(db, "CALL add_order(1)"); }
        string path = Path.Combine(_root, "tables", "transaction.sdbtxn");
        byte[] bytes = File.ReadAllBytes(path);
        bytes[32] ^= 1;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => Open());
    }

    [Fact]
    public void Trigger_ConcurrentSummaryWithoutRowVersion_RejectsStaleCommitAndAllowsRetry()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE source_rows (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE totals (id INT, count INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO totals (id, count) VALUES (1, 0)");
        SqlExecutor.Execute(db, """
            CREATE TRIGGER summarize AFTER INSERT ON source_rows FOR EACH ROW LANGUAGE SQL AS BEGIN
                UPDATE totals SET count = count + 1 WHERE id = 1;
            END
            """);
        var first = new SqlTransactionContext();
        var second = new SqlTransactionContext();
        Execute(db, "INSERT INTO source_rows (id) VALUES (1)", first);
        Execute(db, "INSERT INTO source_rows (id) VALUES (2)", second);
        Execute(db, "COMMIT", first);
        Assert.Equal(TableConstraintException.ConcurrencyConflict,
            Assert.Throws<TableConstraintException>(() => Execute(db, "COMMIT", second)).ErrorCode);
        Assert.True(second.IsCompleted);
        Assert.Single(Select(db, "SELECT * FROM source_rows").Rows);
        Assert.Equal(1L, Select(db, "SELECT count FROM totals").Rows[0][0]);
        Assert.Equal(TableConstraintException.ConcurrencyConflict,
            db.Routines.Diagnostics.SnapshotAudit()[1].ErrorCode);
        SqlExecutor.Execute(db, "INSERT INTO source_rows (id) VALUES (2)");
        Assert.Equal(2, Select(db, "SELECT * FROM source_rows").Rows.Count);
        Assert.Equal(2L, Select(db, "SELECT count FROM totals").Rows[0][0]);
    }

    [Fact]
    public void Call_InsertReturningBeyondBudget_RollsBackWrites()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE source_rows (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, """
            CREATE PROCEDURE return_too_many() LANGUAGE SQL AS BEGIN
                INSERT INTO source_rows (id) VALUES (1), (2) RETURNING id;
            END
            """);
        Assert.Equal(RoutineErrorCodes.ResultRowLimit,
            Assert.Throws<RoutineExecutionException>(() => Execute(db, "CALL return_too_many()", null,
                new SqlExecutionOptions { MaxRoutineResultRows = 1 })).Code);
        Assert.Empty(Select(db, "SELECT * FROM source_rows").Rows);
    }

    [Fact]
    public void Trigger_InsertReturningBeyondBudget_RollsBackSourceAndOutbox()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE orders (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE audit_outbox (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, """
            CREATE TRIGGER order_audit AFTER INSERT ON orders FOR EACH ROW LANGUAGE SQL AS BEGIN
                INSERT INTO audit_outbox (id) VALUES (NEW.id) RETURNING id;
            END
            """);
        var error = Assert.Throws<RoutineExecutionException>(() => Execute(
            db, "INSERT INTO orders (id) VALUES (1), (2)", null, new SqlExecutionOptions { MaxRoutineResultRows = 1 }));
        Assert.Equal(RoutineErrorCodes.ResultRowLimit, error.Code);
        Assert.Empty(Select(db, "SELECT * FROM orders").Rows);
        Assert.Empty(Select(db, "SELECT * FROM audit_outbox").Rows);
        Assert.Collection(db.Routines.Diagnostics.SnapshotAudit(),
            record => Assert.Equal("rolled_back", record.Outcome),
            record => Assert.Equal("failed", record.Outcome));
    }

    [Theory]
    [InlineData("INSERT INTO orders (missing_column) VALUES (1)")]
    [InlineData("UPDATE orders SET missing_column = 2 WHERE id = 1")]
    [InlineData("DELETE FROM orders WHERE id IN (SELECT id FROM missing_table)")]
    public void CreateProcedure_InvalidWriteDependency_RejectsBeforePublishing(string body)
    {
        using var db = Open();
        Setup(db);
        Assert.Equal(RoutineErrorCodes.Dependency, Assert.Throws<RoutineExecutionException>(() =>
            SqlExecutor.Execute(db, $"CREATE PROCEDURE invalid_body() LANGUAGE SQL AS BEGIN {body}; END")).Code);
        Assert.Null(db.Routines.TryGetProcedure("invalid_body"));
    }

    [Fact]
    public void Call_CommitDecisionAcknowledgementFailure_RequiresReopenAndReportsUnknown()
    {
        using (var db = Open())
        {
            Setup(db);
            var heldStore = db.Tables.Open("orders");
            db.Tables.ApplyTransactionAfterCompleteTestHook = () => throw new IOException("injected acknowledgement failure");
            Assert.Equal(RoutineErrorCodes.CommitUnknown,
                Assert.Throws<RoutineExecutionException>(() => SqlExecutor.Execute(db, "CALL add_order(1)")).Code);
            Assert.All(db.Routines.Diagnostics.SnapshotAudit(), record => Assert.Equal("unknown", record.Outcome));
            Assert.Throws<TableTransactionRecoveryException>(() => heldStore.GetByPrimaryKey([1L]));
        }
        using var reopened = Open();
        Assert.Single(Select(reopened, "SELECT * FROM orders").Rows);
        Assert.Single(Select(reopened, "SELECT * FROM audit_outbox").Rows);
    }

    [Fact]
    public async Task Backup_ConcurrentTriggerCommit_CapturesConsistentPairAndLifecycle()
    {
        string backupPath = _root + "-backup";
        string restoredPath = _root + "-restored";
        Task? writer = null;
        using var started = new ManualResetEventSlim();
        try
        {
            using var db = Open();
            Setup(db);
            SqlExecutor.Execute(db, "CALL add_order(1)");
            SqlExecutor.Execute(db, "ALTER TRIGGER order_audit RENAME TO archived_audit");
            var backup = new BackupService
            {
                AfterFileCopiedTestHook = _ =>
                {
                    if (writer is not null) return;
                    writer = Task.Run(() =>
                    {
                        started.Set();
                        SqlExecutor.Execute(db, "CALL add_order(2)");
                    });
                    Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
                    Assert.False(writer.Wait(TimeSpan.FromMilliseconds(100)));
                },
            };
            backup.Create(db, new BackupCreateOptions { DestinationDirectory = backupPath });
            Assert.NotNull(writer);
            await writer.WaitAsync(TimeSpan.FromSeconds(10));
            backup.Restore(new BackupRestoreOptions { BackupDirectory = backupPath, TargetDirectory = restoredPath });
            using var restored = Tsdb.Open(new TsdbOptions { RootDirectory = restoredPath });
            Assert.Single(Select(restored, "SELECT * FROM orders").Rows);
            Assert.Single(Select(restored, "SELECT * FROM audit_outbox").Rows);
            Assert.NotNull(restored.Routines.TryGetTrigger("archived_audit"));
        }
        finally
        {
            if (writer is not null) await writer.WaitAsync(TimeSpan.FromSeconds(10));
            if (Directory.Exists(backupPath)) Directory.Delete(backupPath, recursive: true);
            if (Directory.Exists(restoredPath)) Directory.Delete(restoredPath, recursive: true);
        }
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
    });

    private static void Setup(Tsdb db)
    {
        SqlExecutor.Execute(db, "CREATE TABLE orders (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE audit_outbox (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, """
            CREATE TRIGGER order_audit AFTER INSERT ON orders FOR EACH ROW LANGUAGE SQL AS BEGIN
                INSERT INTO audit_outbox (id) VALUES (NEW.id);
            END
            """);
        SqlExecutor.Execute(db, """
            CREATE PROCEDURE add_order(IN order_id INT) LANGUAGE SQL AS BEGIN
                INSERT INTO orders (id) VALUES (@order_id);
            END
            """);
    }

    private static object? Execute(Tsdb db, string sql, SqlTransactionContext? transaction,
        SqlExecutionOptions? options = null)
        => SqlExecutor.ExecuteStatement(db, null, SqlParser.Parse(sql), null, transaction,
            options ?? SqlExecutionOptions.Default);

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
}
