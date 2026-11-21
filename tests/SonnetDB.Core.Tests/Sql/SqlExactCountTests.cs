using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Exceptions;
using SonnetDB.Kv;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>精确 COUNT 的元数据与主键前缀路径，保留事务、回退及取消语义。</summary>
public sealed class SqlExactCountTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-exact-count-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(30));

    /// <summary>空表仍返回一行 Int64 零值及原投影别名。</summary>
    [Theory]
    [InlineData(KvStorageEngine.Legacy)]
    [InlineData(KvStorageEngine.Lsm)]
    public void EmptyTableReturnsExactZeroAndMetadata(KvStorageEngine engine)
    {
        using Tsdb db = Open(engine);
        Create(db);
        TableStore store = db.Tables.Open("items");
        store.RowDecodedTestHook = static _ => throw new InvalidOperationException("Metadata COUNT decoded a row.");
        SelectExecutionResult result = Query(db, "SELECT COUNT(*) AS total FROM items");
        Assert.Equal(0L, Assert.Single(result.Rows)[0]);
        Assert.Equal("total", Assert.Single(result.Columns));
        Assert.Equal(TableColumnType.Int64, Assert.Single(result.ColumnInfo!).DataType);
        Assert.False(Assert.Single(result.ColumnInfo!).IsNullable);
    }

    /// <summary>删除和重开之后使用精确已提交行数，不解码行。</summary>
    [Theory]
    [InlineData(KvStorageEngine.Legacy)]
    [InlineData(KvStorageEngine.Lsm)]
    public void UnfilteredCountTracksDeletionAndReopen(KvStorageEngine engine)
    {
        using (Tsdb db = Open(engine))
        {
            Prepare(db);
            Run(db, "DELETE FROM items WHERE generation='g1' AND medium=2 AND id=2");
            db.Tables.Open("items").RowDecodedTestHook = static _ => throw new InvalidOperationException("COUNT decoded a row.");
            Assert.Equal(3L, Count(db, "SELECT COUNT(*) FROM items"));
        }
        using Tsdb reopened = Open(engine);
        reopened.Tables.Open("items").RowDecodedTestHook = static _ => throw new InvalidOperationException("Reopened COUNT decoded a row.");
        Assert.Equal(3L, Count(reopened, "SELECT COUNT(*) FROM items"));
    }

    /// <summary>连续主键前缀、交换的 AND/等号和别名保持精确且不解码行。</summary>
    [Theory]
    [InlineData("SELECT COUNT(*) FROM items WHERE generation='g1'", 3L)]
    [InlineData("SELECT COUNT(*) FROM items WHERE generation='g1' AND medium=2", 2L)]
    [InlineData("SELECT COUNT(*) AS n FROM items AS i WHERE 2=i.medium AND 'g1'=i.generation", 2L)]
    [InlineData("SELECT COUNT(*) FROM items WHERE generation='g1' AND medium=2 AND id=1", 1L)]
    [InlineData("SELECT COUNT(*) FROM items WHERE generation='absent' AND medium=2", 0L)]
    public void PrimaryPrefixIsExactWithoutRowDecode(string sql, long expected)
    {
        using Tsdb db = Open(KvStorageEngine.Lsm);
        Prepare(db);
        db.Tables.Open("items").RowDecodedTestHook = static _ => throw new InvalidOperationException("Prefix COUNT decoded a row.");
        Assert.Equal(expected, Count(db, sql));
    }

    /// <summary>残余、重复、NULL、非连续前缀和浮点比较保持原执行语义。</summary>
    [Theory]
    [InlineData("generation='g1' AND medium=2 AND payload>=20", 1L)]
    [InlineData("generation='g1' AND generation='g10'", 0L)]
    [InlineData("generation='g1' AND generation='g1' AND medium=2", 2L)]
    [InlineData("generation IS NULL", 0L)]
    [InlineData("medium=2", 3L)]
    [InlineData("generation='g1' AND medium=2.0", 2L)]
    public void OtherPredicatesKeepOriginalResults(string predicate, long expected)
    {
        using Tsdb db = Open(KvStorageEngine.Lsm);
        Prepare(db);
        Assert.Equal(expected, Count(db, "SELECT COUNT(*) FROM items WHERE " + predicate));
    }

    /// <summary>未知别名仍由原绑定器拒绝。</summary>
    [Fact]
    public void UnknownAliasIsRejected()
    {
        using Tsdb db = Open(KvStorageEngine.Lsm);
        Prepare(db);
        Assert.Throws<InvalidOperationException>(() => Count(db,
            "SELECT COUNT(*) FROM items AS i WHERE missing.generation='g1'"));
    }

    /// <summary>同表未提交写入必须叠加，不能读取已提交元数据冒充事务视图。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BufferedWritesRemainVisible(bool prefix)
    {
        using Tsdb db = Open(KvStorageEngine.Lsm);
        Prepare(db);
        var transaction = new SqlTransactionContext(SqlTransactionIsolationLevel.Serializable);
        Run(db, "INSERT INTO items(generation,medium,id,payload) VALUES('g1',2,3,30)", transaction);
        Assert.Equal(prefix ? 3L : 5L, Count(db, CountSql(prefix), transaction));
        Run(db, "ROLLBACK", transaction);
        Assert.Equal(prefix ? 2L : 4L, Count(db, CountSql(prefix)));
    }

    /// <summary>两个 fast path 均登记 Serializable 读凭据，真实并发写仍拒绝提交。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SerializableCountRejectsConcurrentTableWrite(bool prefix)
    {
        using Tsdb db = Open(KvStorageEngine.Lsm);
        Prepare(db);
        var transaction = new SqlTransactionContext(SqlTransactionIsolationLevel.Serializable);
        Assert.Equal(prefix ? 2L : 4L, Count(db, CountSql(prefix), transaction));
        Task writer = Task.Run(() => Run(db,
            "INSERT INTO items(generation,medium,id,payload) VALUES('g1',2,99,99)"));
        await writer.WaitAsync(TimeSpan.FromSeconds(5), _deadline.Token);
        var error = Assert.Throws<TableConstraintException>(() => Run(db, "COMMIT", transaction));
        Assert.Equal(TableConstraintException.SerializationConflict, error.ErrorCode);
        Assert.False(transaction.WasCommitted);
    }

    /// <summary>快照取得后的取消不得返回部分计数。</summary>
    [Fact]
    public void PrefixCancellationDoesNotReturnPartialCount()
    {
        using Tsdb db = Open(KvStorageEngine.Lsm);
        Prepare(db);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        TableStore store = db.Tables.Open("items");
        store.ReadSnapshotAcquiredTestHook = cancellation.Cancel;
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => Run(db, CountSql(true), token: cancellation.Token));
        }
        finally { store.ReadSnapshotAcquiredTestHook = null; }
    }

    private static string CountSql(bool prefix) => prefix
        ? "SELECT COUNT(*) FROM items WHERE generation='g1' AND medium=2"
        : "SELECT COUNT(*) FROM items";

    private void Create(Tsdb db) => Run(db,
        "CREATE TABLE items(generation STRING,medium INT,id INT,payload INT,PRIMARY KEY(generation,medium,id))");

    private void Prepare(Tsdb db)
    {
        Create(db);
        Run(db, "INSERT INTO items(generation,medium,id,payload) VALUES('g1',2,1,10),('g1',2,2,20),('g1',1,3,30),('g10',2,1,40)");
    }

    private Tsdb Open(KvStorageEngine engine) => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        Kv = KvOptions.Default with
        {
            StorageEngine = engine,
            AutoCheckpointEnabled = false,
            SyncWalOnEveryWrite = true,
            ExpirerEnabled = false
        },
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false }
    });

    private object? Run(Tsdb db, string sql, SqlTransactionContext? transaction = null, CancellationToken? token = null)
        => SqlExecutor.ExecuteStatement(db, null, SqlParser.Parse(sql), null, transaction,
            new SqlExecutionOptions { CancellationToken = token ?? _deadline.Token, TransactionCommitTimeoutMilliseconds = 5_000 });

    private SelectExecutionResult Query(Tsdb db, string sql, SqlTransactionContext? transaction = null)
        => Assert.IsType<SelectExecutionResult>(Run(db, sql, transaction));

    private long Count(Tsdb db, string sql, SqlTransactionContext? transaction = null)
        => Assert.IsType<long>(Assert.Single(Query(db, sql, transaction).Rows)[0]);

    /// <summary>释放本轮测试期限与独占临时目录。</summary>
    public void Dispose()
    {
        _deadline.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
