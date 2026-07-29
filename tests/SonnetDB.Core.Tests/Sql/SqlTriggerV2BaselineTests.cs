using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// M39 #333 的关系表 golden journey 与事务/恢复证据。
///
/// 这些测试故意固定 V1 AFTER ROW 合同。候选 statement-level 语义只在基准中作为
/// 参考路径出现，不能由本测试暗示已经进入生产 API。
/// </summary>
public sealed class SqlTriggerV2BaselineTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-trigger-v2-baseline-" + Guid.NewGuid().ToString("N"));

    public SqlTriggerV2BaselineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试清理不覆盖断言。 */ }
    }

    [Fact]
    public void GoldenJourney_AuditOutbox_EmitsDurableEventsForEveryRowMutation()
    {
        using var database = Open();
        Execute(database, "CREATE TABLE orders (id INT, status STRING, amount INT, PRIMARY KEY (id))");
        Execute(database, "CREATE TABLE audit_outbox (event_id INT, order_id INT, operation STRING, status STRING, PRIMARY KEY (event_id))");
        Execute(database, """
            CREATE TRIGGER orders_audit_insert AFTER INSERT ON orders FOR EACH ROW
            LANGUAGE SQL AS BEGIN
                INSERT INTO audit_outbox (event_id, order_id, operation, status)
                VALUES (NEW.id * 10 + 1, NEW.id, 'insert', NEW.status);
            END
            """);
        Execute(database, """
            CREATE TRIGGER orders_audit_update AFTER UPDATE ON orders FOR EACH ROW
            LANGUAGE SQL AS BEGIN
                INSERT INTO audit_outbox (event_id, order_id, operation, status)
                VALUES (NEW.id * 10 + 2, NEW.id, 'update', NEW.status);
            END
            """);
        Execute(database, """
            CREATE TRIGGER orders_audit_delete AFTER DELETE ON orders FOR EACH ROW
            LANGUAGE SQL AS BEGIN
                INSERT INTO audit_outbox (event_id, order_id, operation, status)
                VALUES (OLD.id * 10 + 3, OLD.id, 'delete', OLD.status);
            END
            """);

        Execute(database, "INSERT INTO orders (id, status, amount) VALUES (1, 'new', 10), (2, 'new', 20)");
        Execute(database, "UPDATE orders SET status = 'paid' WHERE id = 1");
        Execute(database, "DELETE FROM orders WHERE id = 2");

        var result = Select(database,
            "SELECT operation, order_id, status FROM audit_outbox ORDER BY event_id");
        Assert.Equal(4, result.Rows.Count);
        Assert.Equal(new object?[] { "insert", 1L, "new" }, result.Rows[0]);
        Assert.Equal(new object?[] { "update", 1L, "paid" }, result.Rows[1]);
        Assert.Equal(new object?[] { "insert", 2L, "new" }, result.Rows[2]);
        Assert.Equal(new object?[] { "delete", 2L, "new" }, result.Rows[3]);
    }

    [Fact]
    public void GoldenJourney_DerivedAggregate_TracksInsertUpdateAndDeleteDeltas()
    {
        using var database = Open();
        Execute(database, "CREATE TABLE order_lines (id INT, product_id INT, quantity INT, PRIMARY KEY (id))");
        Execute(database, "CREATE TABLE product_totals (product_id INT, total INT, PRIMARY KEY (product_id))");
        Execute(database, "INSERT INTO product_totals (product_id, total) VALUES (10, 0), (20, 0)");
        Execute(database, """
            CREATE TRIGGER line_total_insert AFTER INSERT ON order_lines FOR EACH ROW
            LANGUAGE SQL AS BEGIN
                UPDATE product_totals
                SET total = total + NEW.quantity
                WHERE product_id = NEW.product_id;
            END
            """);
        Execute(database, """
            CREATE TRIGGER line_total_update AFTER UPDATE ON order_lines FOR EACH ROW
            LANGUAGE SQL AS BEGIN
                UPDATE product_totals
                SET total = total - OLD.quantity + NEW.quantity
                WHERE product_id = NEW.product_id;
            END
            """);
        Execute(database, """
            CREATE TRIGGER line_total_delete AFTER DELETE ON order_lines FOR EACH ROW
            LANGUAGE SQL AS BEGIN
                UPDATE product_totals
                SET total = total - OLD.quantity
                WHERE product_id = OLD.product_id;
            END
            """);

        Execute(database, "INSERT INTO order_lines (id, product_id, quantity) VALUES (1, 10, 2), (2, 10, 3), (3, 20, 4)");
        Execute(database, "UPDATE order_lines SET quantity = 8 WHERE id = 2");
        Execute(database, "DELETE FROM order_lines WHERE id = 1");
        // An empty UPDATE impact set must not invoke the trigger or alter totals.
        Execute(database, "UPDATE order_lines SET quantity = 99 WHERE id = 999");

        var result = Select(database, "SELECT product_id, total FROM product_totals ORDER BY product_id");
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new object?[] { 10L, 8L }, result.Rows[0]);
        Assert.Equal(new object?[] { 20L, 4L }, result.Rows[1]);
    }

    [Fact]
    public void GoldenJourney_StateTransitionProtection_RollsBackForbiddenTransition()
    {
        using var database = Open();
        Execute(database, "CREATE TABLE jobs (id INT, state STRING, PRIMARY KEY (id))");
        Execute(database, "CREATE TABLE transition_guard (event_id INT, job_id INT, PRIMARY KEY (event_id))");
        Execute(database, "INSERT INTO jobs (id, state) VALUES (1, 'open')");
        // The guard row makes an illegal transition fail at the trigger action. This is
        // intentionally an AFTER ROW V1 protection pattern; BEFORE semantics are a
        // separate, evidence-gated M39 item.
        Execute(database, "INSERT INTO transition_guard (event_id, job_id) VALUES (1, 0)");
        Execute(database, """
            CREATE TRIGGER jobs_state_guard AFTER UPDATE ON jobs FOR EACH ROW
            WHEN (OLD.state = 'closed' AND NEW.state != 'closed')
            LANGUAGE SQL AS BEGIN
                INSERT INTO transition_guard (event_id, job_id) VALUES (1, NEW.id);
            END
            """);

        Execute(database, "UPDATE jobs SET state = 'running' WHERE id = 1");
        Execute(database, "UPDATE jobs SET state = 'closed' WHERE id = 1");
        var failure = Assert.Throws<RoutineExecutionException>(() =>
            Execute(database, "UPDATE jobs SET state = 'open' WHERE id = 1"));

        Assert.Equal(RoutineErrorCodes.ExecutionFailed, failure.Code);
        Assert.Equal("closed", Select(database, "SELECT state FROM jobs WHERE id = 1").Rows.Single()[0]);
        Assert.Single(Select(database, "SELECT * FROM transition_guard").Rows);
    }

    [Fact]
    public void CrashEvidence_TriggerActionFailureMidBatch_RollsBackEarlierRows()
    {
        using var database = Open();
        Execute(database, "CREATE TABLE orders (id INT, status STRING, PRIMARY KEY (id))");
        Execute(database, "CREATE TABLE audit_outbox (event_id INT, order_id INT, PRIMARY KEY (event_id))");
        // The final row deliberately collides after the first two trigger actions
        // have already been evaluated and buffered in the same light transaction.
        Execute(database, "INSERT INTO audit_outbox (event_id, order_id) VALUES (3, 0)");
        Execute(database, """
            CREATE TRIGGER orders_audit AFTER INSERT ON orders FOR EACH ROW
            LANGUAGE SQL AS BEGIN
                INSERT INTO audit_outbox (event_id, order_id) VALUES (NEW.id, NEW.id);
            END
            """);

        var failure = Assert.Throws<RoutineExecutionException>(() =>
            Execute(database, "INSERT INTO orders (id, status) VALUES (1, 'new'), (2, 'new'), (3, 'new')"));

        Assert.Equal(RoutineErrorCodes.ExecutionFailed, failure.Code);
        Assert.Empty(Select(database, "SELECT * FROM orders").Rows);
        Assert.Single(Select(database, "SELECT * FROM audit_outbox").Rows);
        Assert.All(
            database.Routines.Diagnostics.SnapshotAudit().Where(static item => item.Kind == "trigger"),
            static item => Assert.False(item.Succeeded));
    }

    [Fact]
    public void TriggerBatch_WithExplicitRoutineBudget_ExecutesBeyondDefaultGuard()
    {
        using var database = Open();
        Execute(database, "CREATE TABLE source_rows (id INT, payload INT, PRIMARY KEY (id))");
        Execute(database, "CREATE TABLE audit_rows (id INT, PRIMARY KEY (id))");
        Execute(database, """
            CREATE TRIGGER source_rows_audit AFTER INSERT ON source_rows FOR EACH ROW
            LANGUAGE SQL AS BEGIN
                INSERT INTO audit_rows (id) VALUES (NEW.id);
            END
            """);

        var sql = new System.Text.StringBuilder("INSERT INTO source_rows (id, payload) VALUES ");
        for (int id = 1; id <= 100; id++)
        {
            if (id > 1)
                sql.Append(", ");
            sql.Append('(').Append(id).Append(", ").Append(id).Append(')');
        }

        var results = SqlExecutor.ExecuteScript(
            database,
            sql.ToString(),
            new SqlExecutionOptions
            {
                Caller = "m39-test",
                MaxRoutineStatements = 128,
                MaxRoutineDepth = 8,
                MaxRoutineResultRows = 128,
            });

        Assert.Equal(100, Assert.IsType<InsertExecutionResult>(results.Single()).RowsInserted);
        Assert.Equal(100, Select(database, "SELECT * FROM source_rows").Rows.Count);
        Assert.Equal(100, Select(database, "SELECT * FROM audit_rows").Rows.Count);
    }

    [Fact]
    public void CrashEvidence_CommitFailure_RollsBackAllTablesAndMarksTriggerFailed()
    {
        using var database = Open();
        Execute(database, "CREATE TABLE orders (id INT, status STRING, PRIMARY KEY (id))");
        Execute(database, "CREATE TABLE audit_outbox (event_id INT, order_id INT, PRIMARY KEY (event_id))");
        Execute(database, """
            CREATE TRIGGER orders_audit AFTER INSERT ON orders FOR EACH ROW
            LANGUAGE SQL AS BEGIN
                INSERT INTO audit_outbox (event_id, order_id) VALUES (NEW.id, NEW.id);
            END
            """);

        database.Tables.ApplyTransactionAfterTableTestHook = static tableName =>
        {
            if (string.Equals(tableName, "audit_outbox", StringComparison.Ordinal))
                throw new IOException("M39 injected commit failure after audit batch apply.");
        };

        var failure = Assert.Throws<RoutineExecutionException>(() =>
            Execute(database, "INSERT INTO orders (id, status) VALUES (1, 'new')"));

        Assert.Equal(RoutineErrorCodes.ExecutionFailed, failure.Code);
        Assert.Empty(Select(database, "SELECT * FROM orders").Rows);
        Assert.Empty(Select(database, "SELECT * FROM audit_outbox").Rows);
        var audit = database.Routines.Diagnostics.SnapshotAudit().Last(record => record.Kind == "trigger");
        Assert.False(audit.Succeeded);
        Assert.Equal(RoutineErrorCodes.ExecutionFailed, audit.ErrorCode);
    }

    [Fact]
    public void CrashEvidence_RestartReplay_PreservesCommittedTriggerOutbox()
    {
        using (var database = Open())
        {
            Execute(database, "CREATE TABLE orders (id INT, status STRING, PRIMARY KEY (id))");
            Execute(database, "CREATE TABLE audit_outbox (event_id INT, order_id INT, PRIMARY KEY (event_id))");
            Execute(database, """
                CREATE TRIGGER orders_audit AFTER INSERT ON orders FOR EACH ROW
                LANGUAGE SQL AS BEGIN
                    INSERT INTO audit_outbox (event_id, order_id) VALUES (NEW.id, NEW.id);
                END
                """);
            Execute(database, "INSERT INTO orders (id, status) VALUES (1, 'new'), (2, 'new')");
            database.CrashSimulationCloseWal();
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        Assert.Equal(2, Select(reopened, "SELECT * FROM orders").Rows.Count);
        Assert.Equal(2, Select(reopened, "SELECT * FROM audit_outbox").Rows.Count);
        Assert.Equal(1, reopened.Routines.TriggerCount);
    }

    private Tsdb Open()
        => Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        });

    private static void Execute(Tsdb database, string sql)
        => _ = SqlExecutor.Execute(database, sql);

    private static SelectExecutionResult Select(Tsdb database, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql));
}
