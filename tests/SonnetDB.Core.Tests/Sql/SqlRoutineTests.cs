using SonnetDB.Backup;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Routines;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlRoutineTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-routine-" + Guid.NewGuid().ToString("N"));

    public SqlRoutineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试清理不覆盖断言。 */ }
    }

    private TsdbOptions Options() => new() { RootDirectory = _root };

    [Fact]
    public void ParseProcedureAndTrigger_ContractsCaptureTypedBodies()
    {
        var procedure = Assert.IsType<CreateProcedureStatement>(SqlParser.Parse("""
            CREATE PROCEDURE add_device (IN p_id INT, IN p_name STRING)
            LANGUAGE SQL AS BEGIN
                INSERT INTO devices (id, name) VALUES (@p_id, @p_name);
                SELECT id, name FROM devices WHERE id = @p_id;
            END
            """));
        Assert.Equal("add_device", procedure.Name);
        Assert.Equal(["p_id", "p_name"], procedure.Parameters.Select(static value => value.Name));
        Assert.Equal(2, procedure.Body.Count);
        Assert.IsType<CallProcedureStatement>(SqlParser.Parse("CALL add_device(1, 'pump')"));
        Assert.IsType<DropProcedureStatement>(SqlParser.Parse("DROP PROCEDURE IF EXISTS add_device"));
        Assert.IsType<ShowProceduresStatement>(SqlParser.Parse("SHOW PROCEDURES"));
        Assert.IsType<DescribeProcedureStatement>(SqlParser.Parse("DESCRIBE PROCEDURE add_device"));

        var trigger = Assert.IsType<CreateTriggerStatement>(SqlParser.Parse("""
            CREATE TRIGGER audit_insert AFTER INSERT ON devices FOR EACH ROW
            WHEN (NEW.enabled = TRUE)
            LANGUAGE SQL AS BEGIN
                INSERT INTO device_audit (id, name) VALUES (NEW.id, NEW.name);
            END
            """));
        Assert.Equal(SqlTriggerEvent.Insert, trigger.Event);
        Assert.NotNull(trigger.When);
        Assert.IsType<DropTriggerStatement>(SqlParser.Parse("DROP TRIGGER audit_insert"));
        Assert.IsType<ShowTriggersStatement>(SqlParser.Parse("SHOW TRIGGERS ON devices"));
        Assert.IsType<DescribeTriggerStatement>(SqlParser.Parse("DESCRIBE TRIGGER audit_insert"));

        Assert.Throws<SqlParseException>(() => SqlParser.Parse("""
            CREATE PROCEDURE bad (OUT value INT) LANGUAGE SQL AS BEGIN SELECT 1; END
            """));
        Assert.Throws<SqlParseException>(() => SqlParser.Parse("""
            CREATE PROCEDURE bad (IN value INT DEFAULT 1) LANGUAGE SQL AS BEGIN SELECT 1; END
            """));
    }

    [Fact]
    public void Procedure_CallBindsAstReturnsLastResultAndPersistsAcrossReopen()
    {
        using (var database = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(database,
                "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
            SqlExecutor.Execute(database, """
                CREATE PROCEDURE add_device (IN p_id INT, IN p_name STRING)
                LANGUAGE SQL AS BEGIN
                    INSERT INTO devices (id, name) VALUES (@p_id, @p_name);
                    SELECT id, name FROM devices WHERE id = @p_id;
                END
                """);

            var selected = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(database, "CALL add_device(1, 'pump; DROP TABLE devices')"));
            Assert.Equal(new object?[] { 1L, "pump; DROP TABLE devices" }, selected.Rows.Single());
        }

        using var reopened = Tsdb.Open(Options());
        Assert.Equal(1, reopened.Routines.ProcedureCount);
        var selectedAfterReopen = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(reopened, "CALL add_device(2, 'fan')"));
        Assert.Equal(new object?[] { 2L, "fan" }, selectedAfterReopen.Rows.Single());

        var describe = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(reopened, "DESCRIBE PROCEDURE add_device"));
        Assert.Equal("IN p_id INT, IN p_name STRING", describe.Rows.Single()[1]);
        Assert.Equal("devices", describe.Rows.Single()[4]);
    }

    [Fact]
    public void Procedure_FailurePermissionLimitsAndRecursionUseStableErrorsAndRollback()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, """
            CREATE PROCEDURE duplicate_device (IN p_id INT)
            LANGUAGE SQL AS BEGIN
                INSERT INTO devices (id, name) VALUES (@p_id, 'first');
                INSERT INTO devices (id, name) VALUES (@p_id, 'second');
            END
            """);

        var failed = Assert.Throws<RoutineExecutionException>(() =>
            SqlExecutor.Execute(database, "CALL duplicate_device(1)"));
        Assert.Equal(RoutineErrorCodes.ExecutionFailed, failed.Code);
        Assert.Empty(Select(database, "SELECT * FROM devices").Rows);

        var call = SqlParser.Parse("CALL duplicate_device(2)");
        var forbidden = Assert.Throws<RoutineExecutionException>(() =>
            SqlExecutor.ExecuteStatement(
                database,
                "main",
                call,
                controlPlane: null,
                transaction: null,
                new SqlExecutionOptions { Caller = "reader", CanWrite = false }));
        Assert.Equal(RoutineErrorCodes.Forbidden, forbidden.Code);

        SqlExecutor.Execute(database, """
            CREATE PROCEDURE duplicate_wrapper (IN p_id INT)
            LANGUAGE SQL AS BEGIN
                CALL duplicate_device(@p_id);
            END
            """);
        var wrapperDescription = Select(database, "DESCRIBE PROCEDURE duplicate_wrapper");
        Assert.Equal(true, wrapperDescription.Rows.Single()[6]);
        var transitivelyForbidden = Assert.Throws<RoutineExecutionException>(() =>
            SqlExecutor.ExecuteStatement(
                database,
                "main",
                SqlParser.Parse("CALL duplicate_wrapper(3)"),
                controlPlane: null,
                transaction: null,
                new SqlExecutionOptions { Caller = "reader", CanWrite = false }));
        Assert.Equal(RoutineErrorCodes.Forbidden, transitivelyForbidden.Code);

        var limited = Assert.Throws<RoutineExecutionException>(() =>
            SqlExecutor.ExecuteStatement(
                database,
                "main",
                call,
                controlPlane: null,
                transaction: null,
                new SqlExecutionOptions { MaxRoutineStatements = 1 }));
        Assert.Equal(RoutineErrorCodes.StatementLimit, limited.Code);
        Assert.Empty(Select(database, "SELECT * FROM devices").Rows);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledError = Assert.Throws<RoutineExecutionException>(() =>
            SqlExecutor.ExecuteStatement(
                database,
                "main",
                call,
                controlPlane: null,
                transaction: null,
                new SqlExecutionOptions { CancellationToken = cancelled.Token }));
        Assert.Equal(RoutineErrorCodes.Cancelled, cancelledError.Code);

        var recursive = Assert.Throws<RoutineExecutionException>(() =>
            SqlExecutor.Execute(database, """
                CREATE PROCEDURE recursive () LANGUAGE SQL AS BEGIN CALL recursive(); END
                """));
        Assert.Equal(RoutineErrorCodes.RecursiveCall, recursive.Code);
    }

    [Fact]
    public void Procedure_ResultAndDepthLimitsUseStableErrorsAndRollbackNestedWrites()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE devices (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO devices (id) VALUES (1), (2), (3)");
        SqlExecutor.Execute(database, """
            CREATE PROCEDURE list_devices () LANGUAGE SQL AS BEGIN
                SELECT id FROM devices ORDER BY id;
            END
            """);

        var resultLimit = Assert.Throws<RoutineExecutionException>(() =>
            SqlExecutor.ExecuteStatement(
                database,
                "main",
                SqlParser.Parse("CALL list_devices()"),
                controlPlane: null,
                transaction: null,
                new SqlExecutionOptions { MaxRoutineResultRows = 2 }));
        Assert.Equal(RoutineErrorCodes.ResultRowLimit, resultLimit.Code);

        SqlExecutor.Execute(database, """
            CREATE PROCEDURE depth_one () LANGUAGE SQL AS BEGIN
                SELECT id FROM devices WHERE id = 1;
            END
            """);
        SqlExecutor.Execute(database, """
            CREATE PROCEDURE depth_two () LANGUAGE SQL AS BEGIN
                CALL depth_one();
            END
            """);
        SqlExecutor.Execute(database, """
            CREATE PROCEDURE depth_three () LANGUAGE SQL AS BEGIN
                INSERT INTO devices (id) VALUES (4);
                CALL depth_two();
            END
            """);

        var depthLimit = Assert.Throws<RoutineExecutionException>(() =>
            SqlExecutor.ExecuteStatement(
                database,
                "main",
                SqlParser.Parse("CALL depth_three()"),
                controlPlane: null,
                transaction: null,
                new SqlExecutionOptions { MaxRoutineDepth = 2 }));
        Assert.Equal(RoutineErrorCodes.DepthLimit, depthLimit.Code);
        Assert.Empty(Select(database, "SELECT id FROM devices WHERE id = 4").Rows);
    }

    [Fact]
    public void Trigger_AfterInsertUpdateDeleteUsesOldNewWhenAndPersists()
    {
        using (var database = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(database, """
                CREATE TABLE devices (
                    id INT, name STRING, enabled BOOL, PRIMARY KEY (id))
                """);
            SqlExecutor.Execute(database, """
                CREATE TABLE device_audit (
                    seq INT, device_id INT, action STRING, old_name STRING NULL,
                    new_name STRING NULL, PRIMARY KEY (seq))
                """);
            SqlExecutor.Execute(database, """
                CREATE TRIGGER devices_insert AFTER INSERT ON devices FOR EACH ROW
                WHEN (NEW.enabled = TRUE)
                LANGUAGE SQL AS BEGIN
                    INSERT INTO device_audit (seq, device_id, action, old_name, new_name)
                    VALUES (NEW.id * 10 + 1, NEW.id, 'insert', NULL, NEW.name);
                END
                """);
            SqlExecutor.Execute(database, """
                CREATE TRIGGER devices_update AFTER UPDATE ON devices FOR EACH ROW
                WHEN (OLD.name != NEW.name)
                LANGUAGE SQL AS BEGIN
                    INSERT INTO device_audit (seq, device_id, action, old_name, new_name)
                    VALUES (NEW.id * 10 + 2, NEW.id, 'update', OLD.name, NEW.name);
                END
                """);
            SqlExecutor.Execute(database, """
                CREATE TRIGGER devices_delete AFTER DELETE ON devices FOR EACH ROW
                LANGUAGE SQL AS BEGIN
                    INSERT INTO device_audit (seq, device_id, action, old_name, new_name)
                    VALUES (OLD.id * 10 + 3, OLD.id, 'delete', OLD.name, NULL);
                END
                """);

            SqlExecutor.Execute(database,
                "INSERT INTO devices (id, name, enabled) VALUES (1, 'pump', TRUE), (2, 'fan', FALSE)");
            SqlExecutor.Execute(database,
                "UPDATE devices SET name = 'pump-v2' WHERE id = 1");
            SqlExecutor.Execute(database,
                "DELETE FROM devices WHERE id = 1");

            var audit = Select(database,
                "SELECT action, old_name, new_name FROM device_audit ORDER BY seq");
            Assert.Equal(3, audit.Rows.Count);
            Assert.Equal(new object?[] { "insert", null, "pump" }, audit.Rows[0]);
            Assert.Equal(new object?[] { "update", "pump", "pump-v2" }, audit.Rows[1]);
            Assert.Equal(new object?[] { "delete", "pump-v2", null }, audit.Rows[2]);
        }

        using var reopened = Tsdb.Open(Options());
        Assert.Equal(3, reopened.Routines.TriggerCount);
        SqlExecutor.Execute(reopened,
            "INSERT INTO devices (id, name, enabled) VALUES (3, 'boiler', TRUE)");
        Assert.Single(Select(reopened,
            "SELECT * FROM device_audit WHERE device_id = 3").Rows);
    }

    [Fact]
    public void Trigger_FailureRollsBackOriginalDmlAndDependencyBlocksDrop()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE devices (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "CREATE TABLE audit (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, """
            CREATE TRIGGER duplicate_audit AFTER INSERT ON devices FOR EACH ROW
            LANGUAGE SQL AS BEGIN
                INSERT INTO audit (id) VALUES (1);
            END
            """);

        Assert.Throws<RoutineExecutionException>(() =>
            SqlExecutor.Execute(database, "INSERT INTO devices (id) VALUES (1), (2)"));
        Assert.Empty(Select(database, "SELECT * FROM devices").Rows);
        Assert.Empty(Select(database, "SELECT * FROM audit").Rows);

        var dependency = Assert.Throws<RoutineExecutionException>(() =>
            SqlExecutor.Execute(database, "DROP TABLE devices"));
        Assert.Equal(RoutineErrorCodes.Dependency, dependency.Code);

        SqlExecutor.Execute(database, "DROP TRIGGER duplicate_audit");
        SqlExecutor.Execute(database, "DROP TABLE devices");

        var missing = Assert.Throws<RoutineExecutionException>(() =>
            SqlExecutor.Execute(database, "DESCRIBE TRIGGER duplicate_audit"));
        Assert.Equal(RoutineErrorCodes.TriggerNotFound, missing.Code);
    }

    [Fact]
    public void RoutineCatalog_BackupRestoreAndCorruptionChecksPreserveDefinitions()
    {
        string backupDirectory = _root + "-backup";
        string restoredDirectory = _root + "-restored";
        try
        {
            using (var database = Tsdb.Open(Options()))
            {
                SqlExecutor.Execute(database,
                    "CREATE TABLE devices (id INT, PRIMARY KEY (id))");
                SqlExecutor.Execute(database,
                    "CREATE TABLE audit (id INT, PRIMARY KEY (id))");
                SqlExecutor.Execute(database, """
                    CREATE PROCEDURE list_devices () LANGUAGE SQL AS BEGIN
                        SELECT id FROM devices ORDER BY id;
                    END
                    """);
                SqlExecutor.Execute(database, """
                    CREATE TRIGGER audit_device AFTER INSERT ON devices FOR EACH ROW
                    LANGUAGE SQL AS BEGIN
                        INSERT INTO audit (id) VALUES (NEW.id);
                    END
                    """);
                _ = new BackupService().Create(database, new BackupCreateOptions
                {
                    DestinationDirectory = backupDirectory,
                });
            }

            new BackupService().Restore(new BackupRestoreOptions
            {
                BackupDirectory = backupDirectory,
                TargetDirectory = restoredDirectory,
            });
            using (var restored = Tsdb.Open(new TsdbOptions { RootDirectory = restoredDirectory }))
            {
                Assert.Equal(1, restored.Routines.ProcedureCount);
                Assert.Equal(1, restored.Routines.TriggerCount);
                SqlExecutor.Execute(restored, "INSERT INTO devices (id) VALUES (7)");
                Assert.Single(Select(restored, "CALL list_devices()").Rows);
                Assert.Single(Select(restored, "SELECT * FROM audit").Rows);
            }

            string catalogPath = Path.Combine(restoredDirectory, "routines", "routines.sdbrtn");
            byte[] bytes = File.ReadAllBytes(catalogPath);
            bytes[40] ^= 0x01;
            File.WriteAllBytes(catalogPath, bytes);
            Assert.Throws<InvalidDataException>(() =>
                Tsdb.Open(new TsdbOptions { RootDirectory = restoredDirectory }));
        }
        finally
        {
            try { Directory.Delete(backupDirectory, recursive: true); } catch { /* ignore */ }
            try { Directory.Delete(restoredDirectory, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void RoutineCatalog_CrashReopenRestoresProcedureAndTriggerDefinitions()
    {
        var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE devices (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "CREATE TABLE audit (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, """
            CREATE PROCEDURE list_devices () LANGUAGE SQL AS BEGIN
                SELECT id FROM devices ORDER BY id;
            END
            """);
        SqlExecutor.Execute(database, """
            CREATE TRIGGER audit_device AFTER INSERT ON devices FOR EACH ROW
            LANGUAGE SQL AS BEGIN
                INSERT INTO audit (id) VALUES (NEW.id);
            END
            """);
        database.CrashSimulationCloseWal();

        using var reopened = Tsdb.Open(Options());
        Assert.Equal(1, reopened.Routines.ProcedureCount);
        Assert.Equal(1, reopened.Routines.TriggerCount);
        SqlExecutor.Execute(reopened, "INSERT INTO devices (id) VALUES (9)");
        Assert.Single(Select(reopened, "CALL list_devices()").Rows);
        Assert.Single(Select(reopened, "SELECT id FROM audit WHERE id = 9").Rows);
    }

    [Fact]
    public void RoutineDiagnostics_AuditIsBoundedValueFreeAndMetricsTrackFailures()
    {
        using var database = Tsdb.Open(Options());
        SqlExecutor.Execute(database,
            "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO devices (id, name) VALUES (1, 'sensitive-value')");
        SqlExecutor.Execute(database, """
            CREATE PROCEDURE find_device (IN p_name STRING) LANGUAGE SQL AS BEGIN
                SELECT id FROM devices WHERE name = @p_name;
            END
            """);

        var call = SqlParser.Parse("CALL find_device('sensitive-value')");
        for (int index = 0; index < 260; index++)
        {
            _ = SqlExecutor.ExecuteStatement(
                database,
                "main",
                call,
                controlPlane: null,
                transaction: null,
                new SqlExecutionOptions { Caller = "diagnostics-test" });
        }

        var procedureMetrics = database.Routines.Diagnostics.GetMetrics();
        Assert.Equal(260, procedureMetrics.ProcedureExecutions);
        Assert.Equal(0, procedureMetrics.ProcedureFailures);
        var procedureAudit = database.Routines.Diagnostics.SnapshotAudit();
        Assert.Equal(256, procedureAudit.Count);
        Assert.All(procedureAudit, record =>
        {
            Assert.Equal("diagnostics-test", record.Caller);
            Assert.DoesNotContain("sensitive-value", record.ToString(), StringComparison.Ordinal);
        });

        SqlExecutor.Execute(database,
            "CREATE TABLE events (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "CREATE TABLE event_audit (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, """
            CREATE TRIGGER fail_event AFTER INSERT ON events FOR EACH ROW
            LANGUAGE SQL AS BEGIN
                INSERT INTO event_audit (id) VALUES (NEW.id);
                INSERT INTO event_audit (id) VALUES (NEW.id);
            END
            """);

        var triggerFailure = Assert.Throws<RoutineExecutionException>(() =>
            SqlExecutor.Execute(database, "INSERT INTO events (id) VALUES (1)"));
        Assert.Equal(RoutineErrorCodes.ExecutionFailed, triggerFailure.Code);
        Assert.Empty(Select(database, "SELECT * FROM events").Rows);
        Assert.Empty(Select(database, "SELECT * FROM event_audit").Rows);

        var finalMetrics = database.Routines.Diagnostics.GetMetrics();
        Assert.Equal(1, finalMetrics.TriggerExecutions);
        Assert.Equal(1, finalMetrics.TriggerFailures);
        var triggerAudit = database.Routines.Diagnostics.SnapshotAudit().Last();
        Assert.Equal("trigger", triggerAudit.Kind);
        Assert.Equal("fail_event", triggerAudit.Name);
        Assert.False(triggerAudit.Succeeded);
        Assert.Equal(RoutineErrorCodes.ExecutionFailed, triggerAudit.ErrorCode);
    }

    private static SelectExecutionResult Select(Tsdb database, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql));
}
