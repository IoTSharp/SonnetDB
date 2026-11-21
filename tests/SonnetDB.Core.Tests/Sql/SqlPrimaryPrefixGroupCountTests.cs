using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Exceptions;
using SonnetDB.Kv;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>连续主键前缀的下一列分组计数，保留 SQL 相等、事务及快照合同。</summary>
public sealed class SqlPrimaryPrefixGroupCountTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-prefix-group-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(30));
    private const string GroupSql = "SELECT issuer,COUNT(*) AS total FROM items WHERE generation='g1' AND medium=2 GROUP BY issuer";

    /// <summary>原执行路径与键路径一致；字符串保留大小写、数字拼写、长度和非 ASCII 差异。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeysOnlyMatchesOriginalAggregationWithAliases(bool aliases)
    {
        using Tsdb db = Open();
        Prepare(db);
        Dictionary<string, long> expected = StringGroups(Query(db,
            GroupSql.Replace(" GROUP BY", " AND payload>=0 GROUP BY", StringComparison.Ordinal)));
        db.Tables.Open("items").RowDecodedTestHook = static _ => throw new InvalidOperationException("Group COUNT decoded a row.");
        string sql = aliases
            ? "SELECT i.issuer AS province,COUNT(*) AS n FROM items AS i WHERE 2=i.medium AND 'g1'=i.generation GROUP BY i.issuer"
            : GroupSql;
        SelectExecutionResult result = Query(db, sql);
        Assert.Equal(expected.OrderBy(x => x.Key, StringComparer.Ordinal),
            StringGroups(result).OrderBy(x => x.Key, StringComparer.Ordinal));
        Assert.Equal(8, result.Rows.Count);
        Assert.Equal(9L, result.Rows.Sum(x => Assert.IsType<long>(x[1])));
        Assert.Equal(aliases ? "province" : "issuer", result.Columns[0]);
        Assert.Equal(aliases ? "n" : "total", result.Columns[1]);
        Assert.Equal(TableColumnType.String, result.ColumnInfo![0].DataType);
        Assert.Equal(TableColumnType.Int64, result.ColumnInfo[1].DataType);
        Assert.False(result.ColumnInfo[1].IsNullable);
    }

    /// <summary>空表或不存在的前缀均返回零组，保留声明元数据。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyPrefixReturnsNoGroups(bool populated)
    {
        using Tsdb db = Open();
        if (populated) Prepare(db); else Create(db);
        db.Tables.Open("items").RowDecodedTestHook = static _ => throw new InvalidOperationException("Empty group decoded a row.");
        SelectExecutionResult result = Query(db, GroupSql.Replace("'g1'", "'absent'", StringComparison.Ordinal));
        Assert.Empty(result.Rows);
        Assert.Equal(new[] { "issuer", "total" }, result.Columns);
        Assert.Equal(TableColumnType.String, result.ColumnInfo![0].DataType);
    }

    /// <summary>删除产生的旧键不可计入重开后的稳定快照。</summary>
    [Fact]
    public void DeletionAndReopenPreserveGroups()
    {
        using (Tsdb db = Open())
        {
            Prepare(db);
            Run(db, "DELETE FROM items WHERE generation='g1' AND medium=2 AND issuer='11' AND id=2");
            Assert.Equal(1L, StringGroups(Query(db, GroupSql))["11"]);
        }
        using Tsdb reopened = Open();
        reopened.Tables.Open("items").RowDecodedTestHook = static _ => throw new InvalidOperationException("Reopened group decoded a row.");
        Assert.Equal(1L, StringGroups(Query(reopened, GroupSql))["11"]);
        Assert.Equal(8L, Query(reopened, GroupSql).Rows.Sum(x => Assert.IsType<long>(x[1])));
    }

    /// <summary>整数主键按完整 Int64 解码，保留负值及超出 double 精度的相邻整数。</summary>
    [Fact]
    public void Int64GroupingUsesExactValues()
    {
        using Tsdb db = Open();
        Run(db, "CREATE TABLE numbers(prefix STRING,value INT,id INT,PRIMARY KEY(prefix,value,id))");
        Run(db, "INSERT INTO numbers(prefix,value,id) VALUES('p',-1,1),('p',-1,2),('p',9007199254740992,1),('p',9007199254740993,1)");
        db.Tables.Open("numbers").RowDecodedTestHook = static _ => throw new InvalidOperationException("Integer group decoded a row.");
        var groups = Query(db, "SELECT value,COUNT(*) FROM numbers WHERE prefix='p' GROUP BY value").Rows
            .ToDictionary(x => Assert.IsType<long>(x[0]), x => Assert.IsType<long>(x[1]));
        Assert.Equal(3, groups.Count);
        Assert.Equal(2L, groups[-1]);
        Assert.Equal(1L, groups[9007199254740992]);
        Assert.Equal(1L, groups[9007199254740993]);
    }

    /// <summary>同表缓冲写入必须继续读事务 overlay。</summary>
    [Fact]
    public void BufferedWritesUseOriginalTransactionView()
    {
        using Tsdb db = Open();
        Prepare(db);
        var transaction = new SqlTransactionContext(SqlTransactionIsolationLevel.Serializable);
        Run(db, "INSERT INTO items(generation,medium,issuer,id,payload) VALUES('g1',2,'11',3,30)", transaction);
        Assert.Equal(3L, StringGroups(Query(db, GroupSql, transaction))["11"]);
        Run(db, "ROLLBACK", transaction);
        Assert.Equal(2L, StringGroups(Query(db, GroupSql))["11"]);
    }

    /// <summary>真实同表并发写入仍使 Serializable 读事务拒绝提交。</summary>
    [Fact]
    public async Task SerializableGroupCountRejectsConcurrentWriter()
    {
        using Tsdb db = Open();
        Prepare(db);
        var transaction = new SqlTransactionContext(SqlTransactionIsolationLevel.Serializable);
        Assert.Equal(2L, StringGroups(Query(db, GroupSql, transaction))["11"]);
        await Task.Run(() => Run(db,
            "INSERT INTO items(generation,medium,issuer,id,payload) VALUES('g1',2,'11',99,99)"))
            .WaitAsync(TimeSpan.FromSeconds(5), _deadline.Token);
        var error = Assert.Throws<TableConstraintException>(() => Run(db, "COMMIT", transaction));
        Assert.Equal(TableConstraintException.SerializationConflict, error.ErrorCode);
        Assert.False(transaction.WasCommitted);
    }

    /// <summary>共享引擎的其他逻辑表写入不可造成假 Serializable 冲突。</summary>
    [Fact]
    public void OtherTableWritesDoNotInvalidateGroupRead()
    {
        using Tsdb db = Open();
        Prepare(db);
        Run(db, "CREATE TABLE other(id INT,PRIMARY KEY(id))");
        var transaction = new SqlTransactionContext(SqlTransactionIsolationLevel.Serializable);
        Assert.Equal(8, Query(db, GroupSql, transaction).Rows.Count);
        Run(db, "INSERT INTO other(id) VALUES(1)");
        Run(db, "COMMIT", transaction);
        Assert.True(transaction.WasCommitted);
    }

    /// <summary>取得快照后提交的新行只在后续查询中可见，遍历期间不持表锁。</summary>
    [Fact]
    public void SnapshotDoesNotSeeLaterCommittedInsert()
    {
        using Tsdb db = Open();
        Prepare(db);
        TableStore store = db.Tables.Open("items");
        store.ReadSnapshotAcquiredTestHook = () =>
        {
            store.ReadSnapshotAcquiredTestHook = null;
            Run(db, "INSERT INTO items(generation,medium,issuer,id,payload) VALUES('g1',2,'11',3,30)");
        };
        try { Assert.Equal(2L, StringGroups(Query(db, GroupSql))["11"]); }
        finally { store.ReadSnapshotAcquiredTestHook = null; }
        Assert.Equal(3L, StringGroups(Query(db, GroupSql))["11"]);
    }

    /// <summary>快照后的取消不得返回部分分组。</summary>
    [Fact]
    public void CancellationAfterSnapshotDoesNotReturnGroups()
    {
        using Tsdb db = Open();
        Prepare(db);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        TableStore store = db.Tables.Open("items");
        store.ReadSnapshotAcquiredTestHook = cancellation.Cancel;
        try { Assert.ThrowsAny<OperationCanceledException>(() => Run(db, GroupSql, token: cancellation.Token)); }
        finally { store.ReadSnapshotAcquiredTestHook = null; }
    }

    /// <summary>残余、重复、浮点、NULL、非连续前缀及子查询保留原语义。</summary>
    [Theory]
    [InlineData("generation='g1' AND medium=2 AND payload>=20", 8L)]
    [InlineData("generation='g1' AND generation='g1' AND medium=2", 9L)]
    [InlineData("generation='g1' AND medium=2.0", 9L)]
    [InlineData("generation='g1' AND medium IS NULL", 0L)]
    [InlineData("medium=2", 10L)]
    [InlineData("generation='g1' AND medium=2 AND issuer IN (SELECT issuer FROM items WHERE generation='g10')", 2L)]
    public void UnsupportedPredicatesKeepOriginalSemantics(string predicate, long expected)
    {
        using Tsdb db = Open();
        Prepare(db);
        var result = Query(db, "SELECT issuer,COUNT(*) FROM items WHERE " + predicate + " GROUP BY issuer");
        Assert.Equal(expected, result.Rows.Sum(x => Assert.IsType<long>(x[1])));
    }

    /// <summary>未声明的投影或筛选限定符继续由原绑定器拒绝。</summary>
    [Theory]
    [InlineData("SELECT missing.issuer,COUNT(*) FROM items AS i WHERE i.generation='g1' AND i.medium=2 GROUP BY i.issuer")]
    [InlineData("SELECT i.issuer,COUNT(*) FROM items AS i WHERE missing.generation='g1' AND i.medium=2 GROUP BY i.issuer")]
    public void UnknownAliasIsRejected(string sql)
    {
        using Tsdb db = Open();
        Prepare(db);
        Assert.Throws<InvalidOperationException>(() => Query(db, sql));
    }

    /// <summary>其他聚合不借用 COUNT 星号路径。</summary>
    [Fact]
    public void OtherAggregateKeepsOriginalPath()
    {
        using Tsdb db = Open();
        Prepare(db);
        var result = Query(db, GroupSql.Replace("COUNT(*)", "SUM(payload)", StringComparison.Ordinal));
        Assert.Equal(450L, result.Rows.Sum(x => Assert.IsType<long>(x[1])));
    }

    /// <summary>键分组无法预留算子内存时回原 spill 路径，释放全部预算且不丢组。</summary>
    [Fact]
    public void OperatorBudgetFallbackUsesOriginalSpill()
    {
        using Tsdb db = Open();
        Prepare(db);
        var metrics = new SqlExecutionMetrics();
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.ExecuteStatement(db, null,
            SqlParser.Parse(GroupSql), null, null, new SqlExecutionOptions
            {
                CancellationToken = _deadline.Token,
                BlockingOperatorMemoryLimitBytes = 96,
                Metrics = metrics
            }));
        Assert.Equal(8, result.Rows.Count);
        Assert.Equal(9L, result.Rows.Sum(x => Assert.IsType<long>(x[1])));
        Assert.True(metrics.Complete().SpillCount > 0);
        Assert.Equal(0L, db.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>Legacy 引擎继续使用原分组执行，不假定存在 LSM 游标。</summary>
    [Fact]
    public void LegacyStorageRetainsOriginalGroupResults()
    {
        using Tsdb db = Open(KvStorageEngine.Legacy);
        Prepare(db);
        Assert.Equal(9L, Query(db, GroupSql).Rows.Sum(x => Assert.IsType<long>(x[1])));
        Assert.Equal(8, Query(db, GroupSql).Rows.Count);
    }

    /// <summary>不支持的布尔分组键回原执行路径，保留原 SQL 布尔语义。</summary>
    [Fact]
    public void BooleanGroupKeyRetainsOriginalResults()
    {
        using Tsdb db = Open();
        Run(db, "CREATE TABLE flags(prefix STRING,flag BOOL,id INT,PRIMARY KEY(prefix,flag,id))");
        Run(db, "INSERT INTO flags(prefix,flag,id) VALUES('p',TRUE,1),('p',TRUE,2),('p',FALSE,1)");
        var groups = Query(db, "SELECT flag,COUNT(*) FROM flags WHERE prefix='p' GROUP BY flag").Rows
            .ToDictionary(x => Assert.IsType<bool>(x[0]), x => Assert.IsType<long>(x[1]));
        Assert.Equal(2, groups.Count);
        Assert.Equal(2L, groups[true]);
        Assert.Equal(1L, groups[false]);
    }

    /// <summary>相反的投影次序回原路径，列名和结果次序不被窄优化改写。</summary>
    [Fact]
    public void ReversedProjectionOrderRetainsOriginalMetadata()
    {
        using Tsdb db = Open();
        Prepare(db);
        var result = Query(db, "SELECT COUNT(*) AS n,issuer AS name FROM items WHERE generation='g1' AND medium=2 GROUP BY issuer");
        Assert.Equal(new[] { "n", "name" }, result.Columns);
        Assert.Equal(9L, result.Rows.Sum(x => Assert.IsType<long>(x[0])));
        Assert.All(result.Rows, row => Assert.IsType<string>(row[1]));
    }

    private static Dictionary<string, long> StringGroups(SelectExecutionResult result)
        => result.Rows.ToDictionary(x => Assert.IsType<string>(x[0]), x => Assert.IsType<long>(x[1]), StringComparer.Ordinal);

    private void Create(Tsdb db) => Run(db, "CREATE TABLE items(generation STRING,medium INT,issuer STRING,id INT,payload INT,PRIMARY KEY(generation,medium,issuer,id))");

    private void Prepare(Tsdb db)
    {
        Create(db);
        Run(db, "INSERT INTO items(generation,medium,issuer,id,payload) VALUES('g1',2,'11',1,10),('g1',2,'11',2,20),('g1',2,'110',1,30),('g1',2,'65',1,40),('g1',2,'A',1,50),('g1',2,'a',1,60),('g1',2,'01',1,70),('g1',2,'1',1,80),('g1',2,'疆',1,90),('g1',1,'11',1,100),('g10',2,'11',1,110)");
    }

    private Tsdb Open(KvStorageEngine engine = KvStorageEngine.Lsm) => Tsdb.Open(new TsdbOptions
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

    /// <summary>释放本轮期限和独占临时数据库目录。</summary>
    public void Dispose()
    {
        _deadline.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
