using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>真实关系表行锁与写入、维护、选行竞态及事务生命周期的并发合同。</summary>
public sealed class SqlRowLockConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-lock-concurrency-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Select_OrdinaryWait_ReleasesStoreMonitorAndReadsCommittedValue()
    {
        using var db = Open();
        var holder = new SqlTransactionContext();
        var waiter = new SqlTransactionContext();
        try
        {
            Run(db, "SELECT id FROM jobs WHERE id=1 FOR UPDATE", holder);
            Task<object?> pending = Start(() => Run(db, "SELECT value FROM jobs WHERE id=1 FOR UPDATE", waiter));
            WaitQueued(db);
            Run(db, "UPDATE jobs SET value=21 WHERE id=2");
            Run(db, "UPDATE jobs SET value=11 WHERE id=1", holder);
            Run(db, "COMMIT", holder);
            var result = Assert.IsType<SelectExecutionResult>(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(11L, Assert.Single(result.Rows)[0]);
        }
        finally { Finish(holder); Finish(waiter); }
        Assert.Equal((0, 0, 0), db.Tables.RowLocks.Inspect());
    }

    [Theory]
    [InlineData("UPDATE jobs SET value=value+1 WHERE id=1")]
    [InlineData("DELETE FROM jobs WHERE id=1")]
    [InlineData("UPDATE jobs SET id=4 WHERE id=1")]
    public async Task Write_AutocommitWait_RecomputesAfterHolderCommit(string sql)
    {
        using var db = Open();
        var holder = new SqlTransactionContext();
        try
        {
            Run(db, "SELECT id FROM jobs WHERE id=1 FOR UPDATE", holder);
            Task<object?> pending = Start(() => Run(db, sql));
            WaitQueued(db);
            Run(db, "UPDATE jobs SET value=50 WHERE id=1", holder);
            Run(db, "COMMIT", holder);
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, result is DeleteExecutionResult deleted ? deleted.SeriesAffected
                : Assert.IsType<RowsAffectedExecutionResult>(result).RowsAffected);
            if (sql.StartsWith("UPDATE jobs SET value", StringComparison.Ordinal))
                Assert.Equal(51L, Value(db, "SELECT value FROM jobs WHERE id=1"));
        }
        finally { Finish(holder); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commit_BufferedWriter_WaitsAndRevalidatesOriginalRow(bool holderChangesRow)
    {
        using var db = Open();
        var holder = new SqlTransactionContext();
        var writer = new SqlTransactionContext();
        try
        {
            Run(db, "UPDATE jobs SET value=99 WHERE id=1", writer);
            Run(db, "SELECT id FROM jobs WHERE id=1 FOR UPDATE", holder);
            Task<object?> pending = Start(() => Run(db, "COMMIT", writer));
            WaitQueued(db);
            if (holderChangesRow) Run(db, "UPDATE jobs SET value=51 WHERE id=1", holder);
            Run(db, "COMMIT", holder);
            if (holderChangesRow)
            {
                await Assert.ThrowsAnyAsync<Exception>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(51L, Value(db, "SELECT value FROM jobs WHERE id=1"));
            }
            else
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(99L, Value(db, "SELECT value FROM jobs WHERE id=1"));
            }
            Assert.True(writer.IsCompleted);
        }
        finally { Finish(holder); Finish(writer); }
        Assert.Equal((0, 0, 0), db.Tables.RowLocks.Inspect());
    }

    [Theory]
    [InlineData("SELECT id FROM jobs WHERE id=1 FOR UPDATE")]
    [InlineData("UPDATE jobs SET value=99 WHERE id=1")]
    [InlineData("ALTER TABLE jobs RENAME TO jobs_new")]
    public async Task Execute_CancelledWait_PreservesHolderAndRemovesWaiter(string sql)
    {
        using var db = Open();
        var holder = new SqlTransactionContext();
        var waiter = sql.StartsWith("SELECT", StringComparison.Ordinal) ? new SqlTransactionContext() : null;
        using var cancellation = new CancellationTokenSource();
        try
        {
            Run(db, "SELECT id FROM jobs WHERE id=1 FOR UPDATE", holder);
            Task<object?> pending = Start(() => Run(db, sql, waiter,
                new SqlExecutionOptions { CancellationToken = cancellation.Token }));
            WaitQueued(db);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Equal(1, db.Tables.RowLocks.Inspect().Held);
            Assert.Equal(0, db.Tables.RowLocks.Inspect().Waiting);
        }
        finally { Finish(holder); Finish(waiter); }
        Assert.Equal((0, 0, 0), db.Tables.RowLocks.Inspect());
    }

    [Fact]
    public async Task Select_Timeout_HasStableCodeAndNoNewLocks()
    {
        using var db = Open();
        var holder = new SqlTransactionContext();
        var waiter = new SqlTransactionContext();
        try
        {
            Run(db, "SELECT id FROM jobs WHERE id=1 FOR UPDATE", holder);
            var error = await Assert.ThrowsAsync<SqlRowLockException>(() => Start(() => Run(db,
                "SELECT id FROM jobs WHERE id=1 FOR UPDATE", waiter,
                new SqlExecutionOptions { TransactionCommitTimeoutMilliseconds = 100 })));
            Assert.Equal(SqlRowLockException.WaitTimeout, error.Code);
            Assert.Equal(1, db.Tables.RowLocks.Inspect().Held);
            Assert.Equal(0, db.Tables.RowLocks.Inspect().Waiting);
        }
        finally { Finish(holder); Finish(waiter); }
    }

    [Theory]
    [InlineData("UPDATE jobs SET value=11 WHERE id=1", 1L, 11L)]
    [InlineData("DELETE FROM jobs WHERE id=1; INSERT INTO jobs(id,value) VALUES(1,17)", 1L, 17L)]
    [InlineData("UPDATE jobs SET value=1 WHERE id=2", 2L, 1L)]
    public void Select_CandidateChangesBeforeLock_ReevaluatesValueIdentityAndOrder(string mutation, long id, long value)
    {
        using var db = Open();
        var transaction = new SqlTransactionContext();
        int calls = 0;
        db.Tables.LockCandidatesSelectedTestHook = () =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            using var scope = SqlTransactionContext.EnterScope(null);
            foreach (string sql in mutation.Split(';', StringSplitOptions.RemoveEmptyEntries))
                SqlExecutor.Execute(db, sql);
        };
        try
        {
            var result = Assert.IsType<SelectExecutionResult>(Run(db,
                "SELECT id,value FROM jobs ORDER BY value LIMIT 1 FOR UPDATE NOWAIT", transaction));
            Assert.Equal(new object?[] { id, value }, Assert.Single(result.Rows));
            Assert.True(calls >= 2);
            Assert.Equal(1, db.Tables.RowLocks.Inspect().Held);
        }
        finally { db.Tables.LockCandidatesSelectedTestHook = null; Finish(transaction); }
    }

    [Theory]
    [InlineData("ALTER TABLE jobs RENAME TO jobs_new")]
    [InlineData("ALTER TABLE jobs ADD COLUMN extra STRING")]
    [InlineData("ALTER TABLE jobs ALTER COLUMN id TYPE DECIMAL(18,0)")]
    [InlineData("CREATE INDEX idx_value ON jobs(value)")]
    [InlineData("TRUNCATE TABLE jobs")]
    [InlineData("DROP TABLE jobs")]
    public async Task Maintenance_HeldRow_WaitsWithoutHoldingManagerMonitor(string sql)
    {
        using var db = Open();
        var holder = new SqlTransactionContext();
        try
        {
            Run(db, "SELECT id FROM jobs WHERE id=1 FOR UPDATE", holder);
            Task<object?> pending = Start(() => Run(db, sql));
            WaitQueued(db);
            Assert.Equal(10L, Value(db, "SELECT value FROM jobs WHERE id=1"));
            Run(db, "COMMIT", holder);
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { Finish(holder); }
    }

    [Theory]
    [InlineData("upsert")]
    [InlineData("delete")]
    [InlineData("batch")]
    public async Task DirectStore_HeldRow_UsesSameLockManager(string operation)
    {
        using var db = Open();
        var store = db.Tables.Open("jobs");
        var holder = new SqlTransactionContext();
        try
        {
            Run(db, "SELECT id FROM jobs WHERE id=1 FOR UPDATE", holder);
            Task<bool> pending = Start(() =>
            {
                switch (operation)
                {
                    case "upsert": store.Upsert(new object?[] { 1L, 99L }); break;
                    case "delete": store.DeleteByPrimaryKey(new object?[] { 1L }); break;
                    case "batch": store.ApplyBatch([new TableRowMutation(new object?[] { 1L }, new object?[] { 1L, 99L })]); break;
                }
                return true;
            });
            WaitQueued(db);
            Assert.Equal(10L, Value(db, "SELECT value FROM jobs WHERE id=1"));
            Finish(holder);
            Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { Finish(holder); }
    }

    [Theory]
    [InlineData("CREATE TABLE other(a INT,b INT,value INT,PRIMARY KEY(a,b))", "INSERT INTO other(a,b,value) VALUES(1,1,10),(1,2,20)", "a=1 AND b=1", "a=1 AND b=2")]
    [InlineData("CREATE TABLE other(value INT)", "INSERT INTO other(value) VALUES(10),(20)", "value=10", "value=20")]
    public void Select_CompositeOrHiddenIdentity_LocksOnlyMatchingPhysicalRow(string create, string insert, string held, string free)
    {
        using var db = Open();
        Run(db, create); Run(db, insert);
        var first = new SqlTransactionContext();
        var second = new SqlTransactionContext();
        try
        {
            Run(db, $"SELECT value FROM other WHERE {held} FOR UPDATE", first);
            Run(db, $"SELECT value FROM other WHERE {free} FOR UPDATE NOWAIT", second);
            Assert.Equal(SqlRowLockException.NotAvailable, Assert.Throws<SqlRowLockException>(() =>
                Run(db, $"SELECT value FROM other WHERE {held} FOR UPDATE NOWAIT", second)).Code);
        }
        finally { Finish(first); Finish(second); }
    }

    [Fact]
    public async Task Maintenance_QueuedBehindHolder_AllowsHolderToFinishItsWriteSet()
    {
        using var db = Open();
        var holder = new SqlTransactionContext();
        try
        {
            Run(db, "SELECT id FROM jobs WHERE id=1 FOR UPDATE", holder);
            Task<object?> pending = Start(() => Run(db, "ALTER TABLE jobs ADD COLUMN extra STRING"));
            WaitQueued(db);
            Run(db, "UPDATE jobs SET value=52 WHERE id=2", holder);
            Run(db, "COMMIT", holder);
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(52L, Value(db, "SELECT value FROM jobs WHERE id=2"));
        }
        finally { Finish(holder); }
    }

    [Fact]
    public async Task ApplyTransaction_LaterTableLocked_DoesNotReserveIdentityBeforeAllLocks()
    {
        using var db = Open();
        Run(db, "CREATE TABLE generated(id INT AUTO_INCREMENT,value INT,PRIMARY KEY(id))");
        var generated = db.Tables.Open("generated");
        long initialSequence = generated.RowLockSequence;
        var holder = new SqlTransactionContext();
        try
        {
            Run(db, "SELECT id FROM jobs WHERE id=1 FOR UPDATE", holder);
            var mutations = new Dictionary<string, IReadOnlyList<TableRowMutation>>(StringComparer.Ordinal)
            {
                ["generated"] = [new TableRowMutation(null, new object?[] { null, 90L })],
                ["jobs"] = [new TableRowMutation(new object?[] { 1L }, new object?[] { 1L, 99L })],
            };
            Task<int> pending = Start(() => db.Tables.ApplyTransaction(mutations));
            WaitQueued(db);
            Assert.Equal(initialSequence, generated.RowLockSequence);
            Assert.Equal(0L, Value(db, "SELECT COUNT(*) FROM generated"));
            Finish(holder);
            Assert.Equal(2, await pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(1L, Value(db, "SELECT id FROM generated"));
            Run(db, "INSERT INTO generated(value) VALUES(91)");
            Assert.Equal(2L, Value(db, "SELECT id FROM generated WHERE value=91"));
        }
        finally { Finish(holder); }
    }

    [Fact]
    public async Task Delete_CascadeChildLocked_WritesNeitherTableUntilRelease()
    {
        using var db = Open();
        Run(db, "CREATE TABLE children(id INT,parent_id INT,PRIMARY KEY(id),FOREIGN KEY(parent_id) REFERENCES jobs(id) ON DELETE CASCADE)");
        Run(db, "INSERT INTO children(id,parent_id) VALUES(10,1)");
        var holder = new SqlTransactionContext();
        try
        {
            Run(db, "SELECT id FROM children WHERE id=10 FOR UPDATE", holder);
            Task<object?> pending = Start(() => Run(db, "DELETE FROM jobs WHERE id=1"));
            WaitQueued(db);
            Assert.Equal(1L, Value(db, "SELECT COUNT(*) FROM jobs WHERE id=1"));
            Assert.Equal(1L, Value(db, "SELECT COUNT(*) FROM children"));
            Finish(holder);
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0L, Value(db, "SELECT COUNT(*) FROM jobs WHERE id=1"));
            Assert.Equal(0L, Value(db, "SELECT COUNT(*) FROM children"));
        }
        finally { Finish(holder); }
    }

    [Fact]
    public async Task Commit_DeferredTriggerLockWait_ExecutesTriggerExactlyOnce()
    {
        using var db = Open();
        Run(db, "CREATE TABLE audit_rows(id INT AUTO_INCREMENT,value INT,PRIMARY KEY(id))");
        Run(db, "CREATE CONSTRAINT TRIGGER audit_job AFTER UPDATE ON jobs DEFERRABLE INITIALLY DEFERRED FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO audit_rows(value) VALUES(NEW.value); END");
        var holder = new SqlTransactionContext();
        var writer = new SqlTransactionContext();
        try
        {
            Run(db, "SELECT id FROM jobs WHERE id=1 FOR UPDATE", holder);
            Run(db, "UPDATE jobs SET value=99 WHERE id=1", writer);
            Task<object?> pending = Start(() => Run(db, "COMMIT", writer));
            WaitQueued(db);
            Assert.Equal(0L, db.Routines.Diagnostics.GetMetrics().TriggerExecutions);
            Assert.Equal(0L, Value(db, "SELECT COUNT(*) FROM audit_rows"));
            Finish(holder);
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1L, db.Routines.Diagnostics.GetMetrics().TriggerExecutions);
            Assert.Equal(1L, Value(db, "SELECT id FROM audit_rows"));
            Assert.Equal(99L, Value(db, "SELECT value FROM audit_rows"));
        }
        finally { Finish(holder); Finish(writer); }
    }

    [Fact]
    public void RollbackToSavepoint_PreservesLocksUntilTransactionCompletes()
    {
        using var db = Open();
        var holder = new SqlTransactionContext();
        var observer = new SqlTransactionContext();
        try
        {
            var savepoint = holder.CreateSavepoint();
            Run(db, "SELECT id FROM jobs WHERE id=1 FOR UPDATE", holder);
            Run(db, "UPDATE jobs SET value=88 WHERE id=1", holder);
            holder.RollbackTo(savepoint);
            Assert.Equal(10L, Value(db, "SELECT value FROM jobs WHERE id=1"));
            Assert.Equal(SqlRowLockException.NotAvailable, Assert.Throws<SqlRowLockException>(() =>
                Run(db, "SELECT id FROM jobs WHERE id=1 FOR UPDATE NOWAIT", observer)).Code);
            Finish(holder);
            Run(db, "SELECT id FROM jobs WHERE id=1 FOR UPDATE NOWAIT", observer);
        }
        finally { Finish(holder); Finish(observer); }
    }

    private Tsdb Open()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        try
        {
            SqlExecutor.Execute(db, "CREATE TABLE jobs(id INT,value INT,PRIMARY KEY(id))");
            SqlExecutor.Execute(db, "INSERT INTO jobs(id,value) VALUES(1,10),(2,20),(3,30)");
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    [Theory]
    [InlineData("drop_key")]
    [InlineData("identity")]
    [InlineData("rebuild")]
    public async Task Maintenance_NamedKeyIdentityAndIndex_HonorHeldRows(string operation)
    {
        using var db = Open();
        Run(db, "CREATE TABLE maintenance(id INT AUTO_INCREMENT,value INT,CONSTRAINT pk_maintenance PRIMARY KEY(id))");
        Run(db, "CREATE INDEX idx_maintenance ON maintenance(value)");
        Run(db, "INSERT INTO maintenance(value) VALUES(10)");
        var holder = new SqlTransactionContext();
        try
        {
            Run(db, "SELECT id FROM maintenance FOR UPDATE", holder);
            Task<object?> pending = Start<object?>(() => operation switch
            {
                "drop_key" => Run(db, "ALTER TABLE maintenance DROP CONSTRAINT pk_maintenance"),
                "identity" => Run(db, "ALTER TABLE maintenance AUTO_INCREMENT = 50"),
                _ => db.Tables.RebuildIndex("maintenance", "idx_maintenance"),
            });
            WaitQueued(db);
            Assert.Equal(1L, Value(db, "SELECT id FROM maintenance WHERE value=10"));
            Finish(holder);
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            if (operation == "identity")
            {
                Run(db, "INSERT INTO maintenance(value) VALUES(20)");
                Assert.Equal(50L, Value(db, "SELECT id FROM maintenance WHERE value=20"));
            }
            if (operation == "drop_key") Assert.Empty(db.Tables.Open("maintenance").Schema.PrimaryKey);
        }
        finally { Finish(holder); }
    }

    [Fact]
    public void OnlineIndex_HeldRow_YieldsWithoutPublishingThenResumes()
    {
        using var db = Open();
        var holder = new SqlTransactionContext();
        try
        {
            Run(db, "SELECT id FROM jobs WHERE id=1 FOR UPDATE", holder);
            var definition = new TableIndexDefinition("idx_online", ["value"], false);
            var pending = db.Tables.CreateIndexOnline("jobs", definition, maximumPages: 1);
            Assert.False(pending.Completed);
            Assert.Equal("yielded", pending.Status);
            Assert.Null(db.Tables.Open("jobs").Schema.TryGetIndex("idx_online"));
            Finish(holder);
            Assert.True(db.Tables.CreateIndexOnline("jobs", definition).Completed);
            Assert.Equal(10L, Value(db, "SELECT value FROM jobs WHERE value=10"));
        }
        finally { Finish(holder); }
    }

    [Fact]
    public void Rename_ColdReopenedTable_CreatesStableLockIdentity()
    {
        using (var db = Open()) { }
        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        reopened.Tables.RenameTable("jobs", "renamed");
        var holder = new SqlTransactionContext();
        var observer = new SqlTransactionContext();
        try
        {
            Run(reopened, "SELECT id FROM renamed WHERE id=1 FOR UPDATE", holder);
            Assert.Equal(SqlRowLockException.NotAvailable, Assert.Throws<SqlRowLockException>(() =>
                Run(reopened, "SELECT id FROM renamed WHERE id=1 FOR UPDATE NOWAIT", observer)).Code);
        }
        finally { Finish(holder); Finish(observer); }
    }

    [Fact]
    public void Select_BufferedPrimaryKeyMove_LocksOriginalAndVisibleIdentity()
    {
        using var db = Open();
        var holder = new SqlTransactionContext();
        var observer = new SqlTransactionContext();
        try
        {
            Run(db, "UPDATE jobs SET id=4 WHERE id=1", holder);
            Run(db, "SELECT id FROM jobs WHERE id=4 FOR UPDATE", holder);
            Assert.Equal(SqlRowLockException.NotAvailable, Assert.Throws<SqlRowLockException>(() =>
                Run(db, "SELECT id FROM jobs WHERE id=1 FOR UPDATE NOWAIT", observer)).Code);
            Assert.Equal(2, db.Tables.RowLocks.Inspect().Held);
            Run(db, "COMMIT", holder);
            Assert.Equal(10L, Value(db, "SELECT value FROM jobs WHERE id=4"));
        }
        finally { Finish(holder); Finish(observer); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Select_MaterializationBudget_RejectsWithoutRetainingLocks(bool bytes)
    {
        using var db = Open();
        var transaction = new SqlTransactionContext();
        try
        {
            var options = bytes ? new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 1 }
                : new SqlExecutionOptions { MaxMaterializedRows = 1 };
            Assert.NotNull(Record.Exception(() => Run(db, "SELECT id FROM jobs FOR UPDATE", transaction, options)));
            Assert.Equal(0, db.Tables.RowLocks.Inspect().Held);
            Assert.Empty(Assert.IsType<SelectExecutionResult>(Run(db, "SELECT id FROM jobs LIMIT 0 FOR UPDATE", transaction, options)).Rows);
        }
        finally { Finish(transaction); }
    }

    [Fact]
    public void Commit_SerializableOtherRowChanged_RejectsAndReleasesHeldRow()
    {
        using var db = Open();
        var serializable = new SqlTransactionContext(SqlTransactionIsolationLevel.Serializable);
        var observer = new SqlTransactionContext();
        try
        {
            Run(db, "SELECT id FROM jobs WHERE id=1 FOR UPDATE", serializable);
            Run(db, "UPDATE jobs SET value=51 WHERE id=2");
            var error = Assert.Throws<TableConstraintException>(() => Run(db, "COMMIT", serializable));
            Assert.Equal(TableConstraintException.SerializationConflict, error.ErrorCode);
            Assert.True(serializable.IsCompleted);
            Run(db, "SELECT id FROM jobs WHERE id=1 FOR UPDATE NOWAIT", observer);
        }
        finally { Finish(serializable); Finish(observer); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyPreparedBatch_SeparatePhases_ReacquiresLockAndRejectsChangedBatch(bool changeRow)
    {
        using var db = Open();
        var store = db.Tables.Open("jobs");
        var prepared = store.PrepareBatch([new TableRowMutation([1L], [1L, 99L])]);
        Assert.Equal((0, 0, 0), db.Tables.RowLocks.Inspect());
        var holder = new SqlTransactionContext();
        try
        {
            Run(db, "SELECT id FROM jobs WHERE id=1 FOR UPDATE", holder);
            Task<int> pending = Start(() => store.ApplyPreparedBatch(prepared));
            WaitQueued(db);
            Assert.Equal(10L, Value(db, "SELECT value FROM jobs WHERE id=1"));
            if (changeRow)
            {
                Run(db, "UPDATE jobs SET value=42 WHERE id=1", holder);
                Run(db, "COMMIT", holder);
                var error = await Assert.ThrowsAsync<TableConstraintException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(TableConstraintException.ConcurrencyConflict, error.ErrorCode);
                Assert.Equal(42L, Value(db, "SELECT value FROM jobs WHERE id=1"));
            }
            else
            {
                Finish(holder);
                Assert.Equal(1, await pending.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(99L, Value(db, "SELECT value FROM jobs WHERE id=1"));
            }
        }
        finally { Finish(holder); }
        Assert.Equal((0, 0, 0), db.Tables.RowLocks.Inspect());
    }

    [Fact]
    public void ApplyPreparedBatch_RowChangesBackToOriginal_RejectsStaleBatch()
    {
        using var db = Open();
        var store = db.Tables.Open("jobs");
        var prepared = store.PrepareBatch([new TableRowMutation([1L], [1L, 99L])]);
        Run(db, "UPDATE jobs SET value=42 WHERE id=1");
        Run(db, "UPDATE jobs SET value=10 WHERE id=1");
        var error = Assert.Throws<TableConstraintException>(() => store.ApplyPreparedBatch(prepared));
        Assert.Equal(TableConstraintException.ConcurrencyConflict, error.ErrorCode);
        Assert.Equal(10L, Value(db, "SELECT value FROM jobs WHERE id=1"));
        Assert.Equal((0, 0, 0), db.Tables.RowLocks.Inspect());
    }

    private static object? Run(Tsdb db, string sql, SqlTransactionContext? transaction = null, SqlExecutionOptions? options = null)
        => SqlExecutor.ExecuteStatement(db, null, SqlParser.Parse(sql), null, transaction, options ?? SqlExecutionOptions.Default);

    private static object? Value(Tsdb db, string sql)
        => Assert.Single(Assert.IsType<SelectExecutionResult>(Run(db, sql)).Rows)[0];

    private static void Finish(SqlTransactionContext? transaction)
    {
        if (transaction is { IsCompleted: false }) transaction.MarkCompleted();
    }

    private static Task<T> Start<T>(Func<T> operation)
        => Task.Factory.StartNew(operation, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void WaitQueued(Tsdb db)
        => Assert.True(SpinWait.SpinUntil(() => db.Tables.RowLocks.Inspect().Waiting > 0, TimeSpan.FromSeconds(10)), "实际行锁等待队列未建立。");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
