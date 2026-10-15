using System.Buffers.Binary;
using SonnetDB.Backup;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>无主键原生存储、兼容屏障和原子恢复边界。</summary>
public sealed class SqlKeylessRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-keyless-recovery-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void NativeInsert_DuplicatesPersist_PrimaryKeyApisRemainExplicit()
    {
        string[] ids;
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE logs(value INT NULL)");
            var store = db.Tables.Open("logs");
            store.Insert([1L]);
            store.InsertMany([[1L], [null]]);
            Assert.Equal(3, store.RowCount);
            Assert.Throws<TableConstraintException>(() => store.Upsert([1L]));
            Assert.Throws<TableConstraintException>(() => store.GetByPrimaryKey([]));
            Assert.Throws<TableConstraintException>(() => store.DeleteByPrimaryKey([]));
            ids = Ids(store);
            Assert.Equal(3, ids.Distinct().Count());
            Assert.All(store.Scan(), row => Assert.True(TableRowIdentity.IsEncoded(row.PrimaryKey.Span)));
        }
        using var reopened = Open();
        Assert.Equal(ids, Ids(reopened.Tables.Open("logs")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdentityCollision_NewOrExisting_RejectsBatchAtomically(bool existing)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE logs(value INT)");
        Execute(db, "CREATE INDEX ix_logs ON logs(value)");
        var store = db.Tables.Open("logs");
        byte[] identity = TableRowIdentity.Create();
        var row = new TableRowMutation(null, [1L]) { EncodedRowIdentity = identity };
        if (existing) store.ApplyBatch([row]);
        Assert.Throws<TableConstraintException>(() => store.ApplyBatch([
            new TableRowMutation(null, [2L]) { EncodedRowIdentity = identity },
            new TableRowMutation(null, [3L]) { EncodedRowIdentity = existing ? TableRowIdentity.Create() : identity },
        ]));
        Assert.Equal(existing ? 1 : 0, store.RowCount);
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM logs WHERE value>=2"));
        Assert.Throws<InvalidOperationException>(() => store.ApplyBatch([
            new TableRowMutation([], [4L]) { EncodedRowIdentity = [72, 99] },
        ]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transaction_QueuedInsertThenTruncate_RejectsOldGeneration(bool serializable)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE logs(value INT)");
        var tx = new SqlTransactionContext(serializable ? SqlTransactionIsolationLevel.Serializable : SqlTransactionIsolationLevel.ReadCommitted);
        Execute(db, "INSERT INTO logs(value) VALUES(1),(1)", tx);
        Execute(db, "TRUNCATE TABLE logs");
        Execute(db, "INSERT INTO logs(value) VALUES(2)");
        if (serializable)
            Assert.Equal(TableConstraintException.SerializationConflict,
                Assert.Throws<TableConstraintException>(() => Execute(db, "COMMIT", tx)).ErrorCode);
        else
            Assert.Throws<InvalidOperationException>(() => Execute(db, "COMMIT", tx));
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM logs"));
        Assert.Equal(2L, Scalar(db, "SELECT value FROM logs"));
    }

    [Fact]
    public void Transaction_FailedStatement_RestoresOnlyItsKeylessMutations()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE logs(value INT,CHECK(value>=0))");
        Execute(db, "INSERT INTO logs(value) VALUES(1),(2)");
        var tx = new SqlTransactionContext();
        Execute(db, "INSERT INTO logs(value) VALUES(3),(3)", tx);
        Assert.ThrowsAny<Exception>(() => Execute(db, "UPDATE logs SET value=1/(value-2) WHERE value<3", tx));
        Assert.Equal(4L, Scalar(db, "SELECT COUNT(*) FROM logs", tx));
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM logs WHERE value=1", tx));
        Execute(db, "COMMIT", tx);
        Assert.Equal(4L, Scalar(db, "SELECT COUNT(*) FROM logs"));
    }

    [Fact]
    public void Commit_InjectedFailure_RestoresPhysicalIdentityAndBothTablesAfterReopen()
    {
        string[] ids;
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE logs(value INT)");
            Execute(db, "CREATE INDEX ix_logs ON logs(value)");
            Execute(db, "CREATE TABLE keyed(id INT,value INT,PRIMARY KEY(id))");
            Execute(db, "INSERT INTO logs(value) VALUES(1),(1)");
            Execute(db, "INSERT INTO keyed(id,value) VALUES(1,1)");
            ids = Ids(db.Tables.Open("logs"));
            var tx = new SqlTransactionContext();
            Execute(db, "DELETE FROM logs WHERE TRUE LIMIT 1", tx);
            Execute(db, "INSERT INTO logs(value) VALUES(2)", tx);
            Execute(db, "UPDATE keyed SET value=2 WHERE id=1", tx);
            int applied = 0;
            db.Tables.ApplyTransactionAfterTableTestHook = _ =>
            {
                if (++applied == 1) throw new IOException("injected table commit failure");
            };
            Assert.Throws<IOException>(() => Execute(db, "COMMIT", tx));
            db.Tables.ApplyTransactionAfterTableTestHook = null;
            Assert.Equal(ids, Ids(db.Tables.Open("logs")));
            Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM logs WHERE value=1"));
            Assert.Equal(1L, Scalar(db, "SELECT value FROM keyed WHERE id=1"));
        }
        using var reopened = Open();
        Assert.Equal(ids, Ids(reopened.Tables.Open("logs")));
        Assert.Equal(2L, Scalar(reopened, "SELECT COUNT(*) FROM logs WHERE value=1"));
        Assert.Equal(1L, Scalar(reopened, "SELECT value FROM keyed WHERE id=1"));
    }

    [Fact]
    public void Catalog_LegacyEmptyKeylessTable_UpgradesAndRetainsRecoveryFloor()
    {
        string schemaPath;
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE logs(value INT)");
            schemaPath = db.Tables.SchemaPath;
        }
        // v12 与 v13 payload 相同；模拟前一引擎可创建但不能写入的空无主键表。
        byte[] catalog = File.ReadAllBytes(schemaPath);
        BinaryPrimitives.WriteInt32LittleEndian(catalog.AsSpan(8), 12);
        File.WriteAllBytes(schemaPath, catalog);
        using (var db = Open())
        {
            Assert.Equal(13, TableSchemaCodec.ReadFormatVersion(schemaPath));
            Execute(db, "INSERT INTO logs(value) VALUES(1),(1)");
            Execute(db, "DROP TABLE logs");
            Execute(db, "CREATE TABLE keyed(id INT,PRIMARY KEY(id))");
            Assert.Equal(13, TableSchemaCodec.ReadFormatVersion(schemaPath));
        }
        using var reopened = Open();
        Assert.Equal(13, TableSchemaCodec.ReadFormatVersion(schemaPath));
        Assert.Empty(reopened.Tables.Open("keyed").Scan());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_DurableJournal_RestoresOrCommitsPhysicalIdentity(bool completed)
    {
        string[] oldIds;
        string[] committedIds;
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE logs(value INT)");
            Execute(db, "CREATE INDEX ix_logs ON logs(value)");
            Execute(db, "INSERT INTO logs(value) VALUES(1),(1)");
            var store = db.Tables.Open("logs");
            oldIds = Ids(store);
            var row = store.Scan().First();
            var batch = store.PrepareBatch([
                new TableRowMutation([], null) { EncodedRowIdentity = row.PrimaryKey.ToArray() },
                new TableRowMutation(null, [2L]),
            ]);
            string journal = Path.Combine(Path.GetDirectoryName(db.Tables.SchemaPath)!, TableTransactionJournal.FileName);
            TableTransactionJournal.Prepare(journal, [store.CaptureTransactionUndo(batch)]);
            store.ApplyPreparedBatch(batch);
            store.SyncTransactionWal();
            committedIds = Ids(store);
            if (completed) TableTransactionJournal.Complete(journal);
        }
        using var reopened = Open();
        Assert.Equal(completed ? committedIds : oldIds, Ids(reopened.Tables.Open("logs")));
        Assert.Equal(completed ? 1L : 2L, Scalar(reopened, "SELECT COUNT(*) FROM logs WHERE value=1"));
        Assert.Equal(completed ? 1L : 0L, Scalar(reopened, "SELECT COUNT(*) FROM logs WHERE value=2"));
    }

    [Fact]
    public void BackupRestore_KeylessRows_PreservesIdentityCatalogAndIndexes()
    {
        string backup = _root + "-backup";
        string restoredRoot = _root + "-restored";
        try
        {
            using var db = Open();
            Execute(db, "CREATE TABLE logs(value INT)");
            Execute(db, "CREATE INDEX ix_logs ON logs(value)");
            Execute(db, "INSERT INTO logs(value) VALUES(1),(1)");
            string[] ids = Ids(db.Tables.Open("logs"));
            var service = new BackupService();
            service.Create(db, new BackupCreateOptions { DestinationDirectory = backup });
            service.Restore(new BackupRestoreOptions { BackupDirectory = backup, TargetDirectory = restoredRoot });
            using var restored = Tsdb.Open(new TsdbOptions { RootDirectory = restoredRoot });
            Assert.Equal(ids, Ids(restored.Tables.Open("logs")));
            Assert.Equal(13, TableSchemaCodec.ReadFormatVersion(restored.Tables.SchemaPath));
            Assert.Equal(2, Assert.IsType<SelectExecutionResult>(Execute(restored, "SELECT value FROM logs WHERE value=1")).Rows.Count);
            Assert.Equal(1, Assert.IsType<DeleteExecutionResult>(Execute(restored, "DELETE FROM logs WHERE value=1 LIMIT 1")).SeriesAffected);
        }
        finally
        {
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            if (Directory.Exists(restoredRoot)) Directory.Delete(restoredRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Delete_ConcurrentDuplicates_EachStatementHonorsItsBound()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE logs(value INT)");
        Execute(db, "INSERT INTO logs(value) VALUES(1),(1),(1),(1)");
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            Assert.IsType<DeleteExecutionResult>(Execute(db, "DELETE FROM logs WHERE TRUE LIMIT 1 RETURNING value")))));
        Assert.All(results, result =>
        {
            Assert.Equal(1, result.SeriesAffected);
            Assert.Single(result.Returning!.Rows);
        });
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM logs"));
    }

    [Fact]
    public void RowVersionAndTriggers_DuplicatesKeepSeparateTransitions()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE logs(value INT,version INT ROWVERSION)");
        Execute(db, "CREATE TABLE audit(value INT)");
        Execute(db, "CREATE TRIGGER deleted AFTER DELETE ON logs FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO audit(value) VALUES(OLD.value); END");
        Execute(db, "INSERT INTO logs(value) VALUES(1),(1)");
        Execute(db, "UPDATE logs SET value=2 WHERE value=1");
        Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM logs WHERE version=2"));
        var tx = new SqlTransactionContext();
        Execute(db, "DELETE FROM logs WHERE TRUE LIMIT 1", tx);
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM audit", tx));
        Execute(db, "ROLLBACK", tx);
        Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM logs"));
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM audit"));
        Execute(db, "DELETE FROM logs WHERE TRUE LIMIT 2");
        Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM audit"));
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    private static string[] Ids(TableStore store) => store.Scan().Select(row => Convert.ToHexString(row.PrimaryKey.Span)).Order().ToArray();
    private static object? Execute(Tsdb db, string sql, SqlTransactionContext? tx = null)
        => SqlExecutor.ExecuteStatement(db, null, SqlParser.Parse(sql), null, tx, SqlExecutionOptions.Default);
    private static object? Scalar(Tsdb db, string sql, SqlTransactionContext? tx = null)
        => Assert.Single(Assert.IsType<SelectExecutionResult>(Execute(db, sql, tx)).Rows)[0];
}
