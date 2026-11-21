using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Exceptions;
using SonnetDB.Kv;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>共享 LSM 扫描按逻辑表版本验证 Serializable，保留真实同表写冲突。</summary>
public sealed class SqlSharedLsmSerializableSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-shared-serial-snapshot-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(30));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Commit_FirstSnapshotAfterOtherTableWrite_PreservesOwnBufferedInsert(bool compoundSnapshot)
    {
        using Tsdb db = Open();
        Prepare(db);
        TableStore items = db.Tables.Open("items");
        TableSchema schema = items.Schema;
        var transaction = new SqlTransactionContext(SqlTransactionIsolationLevel.Serializable);

        // A plain INSERT with an explicit primary key does not first read the
        // target. Its subsequent snapshot must establish the correct baseline.
        Execute(db, "INSERT INTO items(id,value) VALUES(2,20)", transaction);
        using (SqlTransactionContext.EnterScope(transaction))
            ReadFirstSnapshot(items, schema, compoundSnapshot);

        // Another logical table may advance the shared engine after the read
        // without changing the serializable dependency on items.
        Execute(db, "UPDATE other SET value=30 WHERE id=1");
        Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM items", transaction));
        Execute(db, "COMMIT", transaction);

        Assert.True(transaction.WasCommitted);
        Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM items"));
        Assert.Equal(20L, Scalar(db, "SELECT value FROM items WHERE id=2"));
        Assert.Equal(30L, Scalar(db, "SELECT value FROM other WHERE id=1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Commit_FirstSnapshotThenSameTableWrite_RejectsWholeTransaction(bool compoundSnapshot)
    {
        using Tsdb db = Open();
        Prepare(db);
        TableStore items = db.Tables.Open("items");
        TableSchema schema = items.Schema;
        var transaction = new SqlTransactionContext(SqlTransactionIsolationLevel.Serializable);
        Execute(db, "INSERT INTO items(id,value) VALUES(2,20)", transaction);
        using (SqlTransactionContext.EnterScope(transaction))
            ReadFirstSnapshot(items, schema, compoundSnapshot);

        Execute(db, "UPDATE items SET value=11 WHERE id=1");
        var error = Assert.Throws<TableConstraintException>(() => Execute(db, "COMMIT", transaction));

        Assert.Equal(TableConstraintException.SerializationConflict, error.ErrorCode);
        Assert.True(transaction.IsCompleted);
        Assert.False(transaction.WasCommitted);
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM items"));
        Assert.Equal(11L, Scalar(db, "SELECT value FROM items WHERE id=1"));
    }

    private static void ReadFirstSnapshot(TableStore items, TableSchema schema, bool compoundSnapshot)
    {
        if (compoundSnapshot)
        {
            using KvReadSnapshot snapshot = items.AcquireReadSnapshot();
            TableRow row = Assert.IsType<TableRow>(items.GetByPrimaryKey(snapshot, schema, [1L]));
            Assert.Equal(10L, row.Values[1]);
        }
        else
        {
            Assert.Equal(10L, Assert.Single(items.Scan()).Values[1]);
        }
    }

    private void Prepare(Tsdb db)
    {
        Execute(db, "CREATE TABLE items(id INT,value INT,PRIMARY KEY(id))");
        Execute(db, "CREATE TABLE other(id INT,value INT,PRIMARY KEY(id))");
        Execute(db, "INSERT INTO items(id,value) VALUES(1,10)");
        Execute(db, "INSERT INTO other(id,value) VALUES(1,20)");
        Assert.NotNull(db.Tables.Open("items").SharedStore);
        Assert.Same(db.Tables.Open("items").SharedStore, db.Tables.Open("other").SharedStore);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        Kv = KvOptions.Default with
        {
            StorageEngine = KvStorageEngine.Lsm,
            AutoCheckpointEnabled = false,
            SyncWalOnEveryWrite = true,
            ExpirerEnabled = false
        },
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false }
    });

    private object? Execute(Tsdb db, string sql, SqlTransactionContext? transaction = null)
        => SqlExecutor.ExecuteStatement(db, null, SqlParser.Parse(sql), null, transaction,
            new SqlExecutionOptions
            {
                CancellationToken = _deadline.Token,
                TransactionCommitTimeoutMilliseconds = 5_000
            });

    private object? Scalar(Tsdb db, string sql, SqlTransactionContext? transaction = null)
        => Assert.Single(Assert.IsType<SelectExecutionResult>(Execute(db, sql, transaction)).Rows)[0];

    public void Dispose()
    {
        _deadline.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
