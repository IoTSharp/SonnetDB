using System.Data;
using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlSerializableTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-serial225-core-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("SELECT value FROM items WHERE id = 1", "UPDATE items SET value = 20 WHERE id = 1")]
    [InlineData("SELECT value FROM items WHERE id = 99", "INSERT INTO items(id,value) VALUES(99,20)")]
    [InlineData("SELECT COUNT(*) FROM items WHERE value = 99", "INSERT INTO items(id,value) VALUES(2,99)")]
    [InlineData("SELECT COUNT(*) FROM items", "INSERT INTO items(id,value) VALUES(2,20)")]
    [InlineData("SELECT value FROM items WHERE value = 10", "UPDATE items SET value = 20 WHERE id = 1")]
    [InlineData("SELECT id FROM items WHERE value = 10 OR value = 99", "INSERT INTO items(id,value) VALUES(2,99)")]
    [InlineData("SELECT id FROM items WHERE id IN (SELECT id FROM items WHERE value = 10)", "UPDATE items SET value = 20 WHERE id = 1")]
    [InlineData("SELECT (SELECT COUNT(*) FROM items)", "INSERT INTO items(id,value) VALUES(2,20)")]
    [InlineData("WITH selected AS (SELECT id FROM items) SELECT * FROM selected", "INSERT INTO items(id,value) VALUES(2,20)")]
    [InlineData("SELECT a.id FROM items a JOIN items b ON a.id = b.id", "INSERT INTO items(id,value) VALUES(2,20)")]
    [InlineData("UPDATE items SET value = 11 WHERE value = 99", "INSERT INTO items(id,value) VALUES(2,99)")]
    [InlineData("DELETE FROM items WHERE value = 99", "INSERT INTO items(id,value) VALUES(2,99)")]
    public void Commit_ChangedReadSource_AbortsEntireTransaction(string read, string competingWrite)
    {
        using var db = Open();
        Prepare(db);
        var transaction = Serializable();
        Execute(db, read, transaction);
        Execute(db, competingWrite);
        AssertConflict(db, transaction);
    }

    [Fact]
    public void Commit_RepeatedChangingRead_DoesNotReplaceFirstToken()
    {
        using var db = Open();
        Prepare(db);
        var transaction = Serializable();
        Assert.Equal(10L, Scalar(db, "SELECT value FROM items WHERE id = 1", transaction));
        Execute(db, "UPDATE items SET value = 20 WHERE id = 1");
        Assert.Equal(20L, Scalar(db, "SELECT value FROM items WHERE id = 1", transaction));
        Execute(db, "UPDATE items SET value = 30 WHERE id = 1", transaction);
        AssertConflict(db, transaction);
        Assert.Equal(20L, Scalar(db, "SELECT value FROM items WHERE id = 1"));
    }

    [Fact]
    public void Commit_WriteSkew_OnlyOneTransactionSucceeds()
    {
        using var db = Open();
        Prepare(db);
        Execute(db, "INSERT INTO items(id,value) VALUES(2,10)");
        var first = Serializable();
        var second = Serializable();
        Assert.Equal(20L, Scalar(db, "SELECT SUM(value) FROM items", first));
        Assert.Equal(20L, Scalar(db, "SELECT SUM(value) FROM items", second));
        Execute(db, "UPDATE items SET value = 0 WHERE id = 1", first);
        Execute(db, "UPDATE items SET value = 0 WHERE id = 2", second);
        Execute(db, "COMMIT", first);
        AssertConflict(db, second);
        Assert.Equal(10L, Scalar(db, "SELECT SUM(value) FROM items"));
    }

    [Theory]
    [InlineData("TRUNCATE TABLE items")]
    [InlineData("ALTER TABLE items ADD extra INT")]
    [InlineData("DROP TABLE items; CREATE TABLE items(id INT,value INT,PRIMARY KEY(id))")]
    public void Commit_SchemaGenerationOrStoreReplaced_RejectsReadOnlyTransaction(string change)
    {
        using var db = Open();
        Prepare(db);
        var transaction = Serializable();
        Execute(db, "SELECT * FROM items", transaction);
        SqlExecutor.ExecuteScript(db, change);
        AssertConflict(db, transaction);
    }

    [Fact]
    public void Commit_DirectStoreWrite_ConflictsWithoutManagerWriter()
    {
        using var db = Open();
        Prepare(db);
        var transaction = Serializable();
        Execute(db, "SELECT * FROM items", transaction);
        db.Tables.Open("items").Upsert([2L, 20L]);
        AssertConflict(db, transaction);
    }

    [Fact]
    public void Embedded_TwoConnections_AbortChangingReadsWithStableCode()
    {
        using var first = new SndbConnection($"Data Source={_root}");
        using var second = new SndbConnection($"Data Source={_root}");
        first.Open();
        second.Open();
        using var a = first.CreateCommand();
        using var b = second.CreateCommand();
        a.CommandText = "CREATE TABLE items(id INT,value INT,PRIMARY KEY(id))";
        a.ExecuteNonQuery();
        a.CommandText = "INSERT INTO items(id,value) VALUES(1,10)";
        a.ExecuteNonQuery();
        using var transaction = first.BeginTransaction(IsolationLevel.Serializable);
        a.Transaction = transaction;
        a.CommandText = "SELECT value FROM items WHERE id = 1";
        Assert.Equal(10L, a.ExecuteScalar());
        b.CommandText = "UPDATE items SET value = 20 WHERE id = 1";
        b.ExecuteNonQuery();
        Assert.Equal(20L, a.ExecuteScalar());
        var error = Assert.Throws<TableConstraintException>(() => transaction.Commit());
        Assert.Equal(TableConstraintException.SerializationConflict, error.ErrorCode);
        Assert.Throws<InvalidOperationException>(() => transaction.Commit());
        a.Transaction = null;
        Assert.Equal(20L, a.ExecuteScalar());
    }

    [Fact]
    public async Task Commit_OppositeTableReadOrder_UsesUniformLocksAndAbortsOne()
    {
        using var db = Open();
        Prepare(db);
        Execute(db, "CREATE TABLE other(id INT,value INT,PRIMARY KEY(id))");
        Execute(db, "INSERT INTO other(id,value) VALUES(1,10)");
        var first = Serializable();
        var second = Serializable();
        Execute(db, "SELECT * FROM items", first);
        Execute(db, "SELECT * FROM other", first);
        Execute(db, "SELECT * FROM other", second);
        Execute(db, "SELECT * FROM items", second);
        Execute(db, "UPDATE items SET value = 0 WHERE id = 1", first);
        Execute(db, "UPDATE other SET value = 0 WHERE id = 1", second);
        using var start = new Barrier(2);
        Task<Exception?> Commit(SqlTransactionContext transaction) => Task.Run<Exception?>(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
            return Record.Exception(() => Execute(db, "COMMIT", transaction));
        });
        var results = await Task.WhenAll(Commit(first), Commit(second)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(results, error => error is null);
        Assert.Single(results, error => error is TableConstraintException
        { ErrorCode: TableConstraintException.SerializationConflict });
        Assert.Equal(10L, (long)Scalar(db, "SELECT value FROM items")! + (long)Scalar(db, "SELECT value FROM other")!);
    }

    [Fact]
    public async Task Commit_ReadStoreLockHeldThroughApply_BlocksDirectCompetingWriter()
    {
        using var db = Open();
        Prepare(db);
        Execute(db, "CREATE TABLE read_source(id INT,value INT,PRIMARY KEY(id))");
        Execute(db, "INSERT INTO read_source(id,value) VALUES(1,10)");
        var transaction = Serializable();
        Execute(db, "SELECT * FROM read_source", transaction);
        Execute(db, "UPDATE items SET value = 11 WHERE id = 1", transaction);
        TableStore readStore = db.Tables.Open("read_source");
        using var applying = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var writerStarted = new ManualResetEventSlim();
        db.Tables.ApplyTransactionAfterTableTestHook = _ =>
        {
            applying.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        var commit = Task.Run(() => Execute(db, "COMMIT", transaction));
        Task? writer = null;
        try
        {
            Assert.True(applying.Wait(TimeSpan.FromSeconds(5)));
            writer = Task.Run(() => { writerStarted.Set(); readStore.Upsert([1L, 20L]); });
            Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotSame(writer, await Task.WhenAny(writer, Task.Delay(100)));
        }
        finally
        {
            release.Set();
            await commit.WaitAsync(TimeSpan.FromSeconds(5));
            if (writer is not null) await writer.WaitAsync(TimeSpan.FromSeconds(5));
            db.Tables.ApplyTransactionAfterTableTestHook = null;
        }
        Assert.True(transaction.WasCommitted);
        Assert.Equal(20L, Scalar(db, "SELECT value FROM read_source"));
    }

    [Fact]
    public void Commit_DeferredTriggerReadsAndCascadeWrites_AreCovered()
    {
        using var db = Open();
        Prepare(db);
        Execute(db, "CREATE TABLE read_source(id INT,value INT,PRIMARY KEY(id))");
        Execute(db, "INSERT INTO read_source(id,value) VALUES(1,3)");
        Execute(db, "CREATE TABLE audit_rows(id INT,value INT,PRIMARY KEY(id))");
        Execute(db, "CREATE CONSTRAINT TRIGGER audited AFTER UPDATE ON items DEFERRABLE INITIALLY DEFERRED FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO audit_rows(id,value) SELECT NEW.id,total FROM (SELECT SUM(value) AS total FROM read_source) AS totals; END");
        var transaction = Serializable();
        Execute(db, "UPDATE items SET value = 11 WHERE id = 1", transaction);
        Execute(db, "COMMIT", transaction);
        Assert.Equal(3L, Scalar(db, "SELECT value FROM audit_rows"));
        Execute(db, "CREATE TABLE child(id INT,parent_id INT,PRIMARY KEY(id),FOREIGN KEY(parent_id) REFERENCES items(id) ON DELETE CASCADE)");
        Execute(db, "INSERT INTO child(id,parent_id) VALUES(1,1)");
        var deletion = Serializable();
        Execute(db, "SELECT * FROM child", deletion);
        Execute(db, "DELETE FROM items WHERE id = 1", deletion);
        Execute(db, "INSERT INTO child(id,parent_id) VALUES(2,1)");
        AssertConflict(db, deletion);
        Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM child"));
        Assert.Equal(11L, Scalar(db, "SELECT value FROM items"));
    }

    [Fact]
    public void Commit_ExternalAutoIncrementAdvance_IsNotAbsorbedByOwnReservation()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE items(id INT AUTO_INCREMENT,value INT,PRIMARY KEY(id))");
        var transaction = Serializable();
        Execute(db, "SELECT COUNT(*) FROM items", transaction);
        Execute(db, "INSERT INTO items(value) VALUES(10)");
        Execute(db, "INSERT INTO items(value) VALUES(20)", transaction);
        AssertConflict(db, transaction);
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM items"));
    }

    [Fact]
    public void Commit_AutoIncrementReservationThenTruncate_UsesStableSerializationConflict()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE items(id INT AUTO_INCREMENT,value INT,PRIMARY KEY(id))");
        var transaction = Serializable();
        Execute(db, "INSERT INTO items(value) VALUES(10)", transaction);
        Execute(db, "TRUNCATE TABLE items");
        AssertConflict(db, transaction);
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM items"));
    }

    [Fact]
    public void Commit_UnchangedReadOnlyAndReadYourWrites_Succeed()
    {
        using var db = Open();
        Prepare(db);
        var readOnly = Serializable();
        Execute(db, "SELECT * FROM items", readOnly);
        Execute(db, "COMMIT", readOnly);
        Assert.True(readOnly.WasCommitted);
        var empty = Serializable();
        Execute(db, "COMMIT", empty);
        Assert.True(empty.WasCommitted);
        var transaction = Serializable();
        Execute(db, "UPDATE items SET value = 11 WHERE id = 1", transaction);
        Assert.Equal(11L, Scalar(db, "SELECT value FROM items WHERE id = 1", transaction));
        Execute(db, "COMMIT", transaction);
        Assert.Equal(11L, Scalar(db, "SELECT value FROM items WHERE id = 1"));
    }

    [Fact]
    public void Commit_OwnAutoIncrementReservations_PreserveFirstReadAndSucceed()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE items(id INT AUTO_INCREMENT,value INT,PRIMARY KEY(id))");
        var transaction = Serializable();
        Execute(db, "SELECT COUNT(*) FROM items", transaction);
        Execute(db, "INSERT INTO items(value) VALUES(10),(20)", transaction);
        Execute(db, "INSERT INTO items(value) VALUES(30)", transaction);
        Assert.Equal(3L, Scalar(db, "SELECT COUNT(*) FROM items", transaction));
        Execute(db, "COMMIT", transaction);
        Assert.Equal(3L, Scalar(db, "SELECT COUNT(*) FROM items"));
    }

    [Fact]
    public void ReadCommitted_ConcurrentChange_PreservesExistingBehavior()
    {
        using var db = Open();
        Prepare(db);
        var transaction = new SqlTransactionContext();
        Assert.Equal(SqlTransactionIsolationLevel.ReadCommitted, transaction.IsolationLevel);
        Assert.Equal(10L, Scalar(db, "SELECT value FROM items", transaction));
        Execute(db, "UPDATE items SET value = 20 WHERE id = 1");
        Assert.Equal(20L, Scalar(db, "SELECT value FROM items", transaction));
        Execute(db, "COMMIT", transaction);
        Assert.True(transaction.WasCommitted);
    }

    [Theory]
    [InlineData("CREATE MEASUREMENT events(value FIELD INT)", "SELECT * FROM events")]
    [InlineData("CREATE DOCUMENT COLLECTION docs", "SELECT * FROM docs")]
    [InlineData("CREATE VIEW source_view AS SELECT * FROM items", "SELECT * FROM source_view")]
    [InlineData("CREATE MATERIALIZED VIEW source_view AS SELECT * FROM items", "SELECT * FROM source_view")]
    [InlineData("CREATE MATERIALIZED VIEW source_view AS SELECT * FROM items", "SELECT * FROM items WHERE id IN(SELECT id FROM source_view)")]
    [InlineData("CREATE MEASUREMENT events(value FIELD INT)", "WITH x AS (SELECT * FROM events) SELECT * FROM x")]
    public void Execute_UnprotectedSourceIncludingNestedView_IsExplicitlyRejected(string setup, string query)
    {
        using var db = Open();
        Prepare(db);
        Execute(db, setup);
        var transaction = Serializable();
        Assert.Throws<NotSupportedException>(() => Execute(db, query, transaction));
        Execute(db, "ROLLBACK", transaction);
        Assert.False(transaction.WasCommitted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commit_StoreLockTimeoutOrCancellation_AbortsAndReleasesLocks(bool cancel)
    {
        using var db = Open();
        Prepare(db);
        var transaction = Serializable();
        Execute(db, "SELECT * FROM items", transaction);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var store = db.Tables.Open("items");
        var blocker = Task.Run(() =>
        {
            lock (store.SynchronizationRoot)
            {
                held.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            }
        });
        Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        try
        {
            var error = Assert.Throws<RoutineExecutionException>(() => Execute(db, "COMMIT", transaction,
                new SqlExecutionOptions
                {
                    TransactionCommitTimeoutMilliseconds = cancel ? 2_000 : 200,
                    CancellationToken = cancellation.Token
                }));
            Assert.Equal(cancel ? RoutineErrorCodes.Cancelled : RoutineErrorCodes.CommitTimeout, error.Code);
            Assert.True(transaction.IsCompleted);
            Assert.False(transaction.WasCommitted);
        }
        finally
        {
            release.Set();
        }
        await blocker.WaitAsync(TimeSpan.FromSeconds(5));
        Execute(db, "UPDATE items SET value = 11 WHERE id = 1");
    }

    private static SqlTransactionContext Serializable() => new(SqlTransactionIsolationLevel.Serializable);
    private Tsdb Open() => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false }
    });
    private static void Prepare(Tsdb db)
    {
        Execute(db, "CREATE TABLE items(id INT,value INT,PRIMARY KEY(id))");
        Execute(db, "CREATE INDEX value_idx ON items(value)");
        Execute(db, "INSERT INTO items(id,value) VALUES(1,10)");
    }
    private static object? Execute(Tsdb db, string sql, SqlTransactionContext? transaction = null,
        SqlExecutionOptions? options = null)
        => SqlExecutor.ExecuteStatement(db, null, SqlParser.Parse(sql), null, transaction, options ?? SqlExecutionOptions.Default);
    private static object? Scalar(Tsdb db, string sql, SqlTransactionContext? transaction = null)
        => Assert.IsType<SelectExecutionResult>(Execute(db, sql, transaction)).Rows[0][0];
    private static void AssertConflict(Tsdb db, SqlTransactionContext transaction)
    {
        var error = Assert.Throws<TableConstraintException>(() => Execute(db, "COMMIT", transaction));
        Assert.Equal(TableConstraintException.SerializationConflict, error.ErrorCode);
        Assert.Contains("整个事务", error.Hint, StringComparison.Ordinal);
        Assert.True(transaction.IsCompleted);
        Assert.False(transaction.WasCommitted);
        Assert.Throws<InvalidOperationException>(() => Execute(db, "COMMIT", transaction));
    }
}
