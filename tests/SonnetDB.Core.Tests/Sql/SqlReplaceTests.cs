using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 REPLACE 的完整新行、真实删除阶段及整批事务行为。</summary>
public sealed class SqlReplaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-sql-replace-" + Guid.NewGuid().ToString("N"));

    /// <summary>两种入口均按 INSERT 默认值构造完整替换行，而不是只更新输入列。</summary>
    [Theory]
    [InlineData("REPLACE INTO")]
    [InlineData("INSERT OR REPLACE INTO")]
    public void Execute_ReplacementWithOmittedColumn_RestoresDefault(string prefix)
    {
        using var database = Open();
        Execute(database, "CREATE TABLE items (id INT, name STRING DEFAULT 'default', PRIMARY KEY(id))");
        Execute(database, "INSERT INTO items(id,name) VALUES (1, 'old')");

        var result = Assert.IsType<InsertExecutionResult>(Execute(database, $"{prefix} items(id) VALUES(1) RETURNING id,name"));

        Assert.Equal(1, result.RowsInserted);
        Assert.Equal(new object?[] { 1L, "default" }, Assert.Single(result.Returning!.Rows));
        Assert.Equal(new object?[] { 1L, "default" }, Assert.Single(Select(database, "SELECT id,name FROM items").Rows));
    }

    /// <summary>不同唯一索引命中不同旧行时，全部冲突所有者被原子替换。</summary>
    [Fact]
    public void Execute_MultipleUniqueOwners_RemovesEveryConflictingRow()
    {
        using var database = Open();
        CreateUniqueItems(database);
        Execute(database, "INSERT INTO items(id,email,phone) VALUES (1,'a','p1'),(2,'b','p2'),(3,'c','p3')");

        var result = Assert.IsType<InsertExecutionResult>(Execute(database, "REPLACE INTO items(id,email,phone) VALUES (1,'b','p3')"));

        Assert.Equal(1, result.RowsInserted);
        Assert.Equal(new object?[] { 1L, "b", "p3" }, Assert.Single(Select(database, "SELECT * FROM items").Rows));
        Assert.Equal(1L, Assert.Single(Select(database, "SELECT id FROM items WHERE email='b'").Rows)[0]);
        Assert.Equal(1L, Assert.Single(Select(database, "SELECT id FROM items WHERE phone='p3'").Rows)[0]);
        Assert.Empty(Select(database, "SELECT id FROM items WHERE email='a' OR phone='p2'").Rows);
    }

    /// <summary>整批重建交叉唯一键后，所有新索引及重开读取都保持正确。</summary>
    [Fact]
    public void Execute_BatchWithCrossedUniqueKeys_PreservesFinalIndexesAfterReopen()
    {
        using (var database = Open())
        {
            CreateUniqueItems(database);
            Execute(database, "INSERT INTO items(id,email,phone) VALUES (1,'a','p1'),(2,'b','p2')");
            var result = Assert.IsType<InsertExecutionResult>(Execute(database,
                "REPLACE INTO items(id,email,phone) VALUES (1,'b','p2'),(2,'a','p1')"));
            Assert.Equal(2, result.RowsInserted);
        }
        using var reopened = Open();
        Assert.Equal(1L, Assert.Single(Select(reopened, "SELECT id FROM items WHERE email='b'").Rows)[0]);
        Assert.Equal(2L, Assert.Single(Select(reopened, "SELECT id FROM items WHERE email='a'").Rows)[0]);
        Assert.Equal(1L, Assert.Single(Select(reopened, "SELECT id FROM items WHERE phone='p2'").Rows)[0]);
        Assert.Equal(2L, Assert.Single(Select(reopened, "SELECT id FROM items WHERE phone='p1'").Rows)[0]);
        Assert.Equal(2, Select(reopened, "SELECT * FROM items").Rows.Count);
    }

    /// <summary>批次输入自身的重复主键或唯一键失败，旧数据和索引不变。</summary>
    [Theory]
    [InlineData("(1,'x','p4'),(1,'y','p5')")]
    [InlineData("(1,'same','p4'),(2,'same','p5')")]
    public void Execute_DuplicateInputKeys_RollsBackWholeBatch(string rows)
    {
        using var database = Open();
        CreateUniqueItems(database);
        Execute(database, "INSERT INTO items(id,email,phone) VALUES (1,'a','p1'),(2,'b','p2')");

        Assert.Throws<TableConstraintException>(() => Execute(database, "REPLACE INTO items(id,email,phone) VALUES " + rows));

        Assert.Equal(new object?[] { 1L, "a", "p1" }, Select(database, "SELECT * FROM items ORDER BY id").Rows[0]);
        Assert.Equal(new object?[] { 2L, "b", "p2" }, Select(database, "SELECT * FROM items ORDER BY id").Rows[1]);
        Assert.Equal(2L, Assert.Single(Select(database, "SELECT id FROM items WHERE email='b'").Rows)[0]);
    }

    /// <summary>后续输入 CHECK 失败时，前面计划的替换也不能持久化。</summary>
    [Fact]
    public void Execute_LaterCheckViolation_PreservesEveryOldRow()
    {
        using var database = Open();
        Execute(database, "CREATE TABLE items (id INT,value INT,PRIMARY KEY(id),CHECK(value>0))");
        Execute(database, "INSERT INTO items(id,value) VALUES (1,10),(2,20)");

        Assert.Throws<TableConstraintException>(() => Execute(database, "REPLACE INTO items(id,value) VALUES (1,100),(2,-1)"));

        Assert.Equal(new object?[] { 1L, 10L }, Select(database, "SELECT * FROM items ORDER BY id").Rows[0]);
        Assert.Equal(new object?[] { 2L, 20L }, Select(database, "SELECT * FROM items ORDER BY id").Rows[1]);
    }

    /// <summary>删除和插入触发器看到独立阶段，UPDATE 触发器不能冒充替换事件。</summary>
    [Fact]
    public void Execute_TriggerPhases_ObserveDeleteBeforeInsert()
    {
        using var database = Open();
        Execute(database, "CREATE TABLE items (id INT,name STRING,PRIMARY KEY(id))");
        Execute(database, "CREATE TABLE audit (id INT,kind STRING,seen INT,PRIMARY KEY(id))");
        Execute(database, "INSERT INTO items(id,name) VALUES (1,'old')");
        Execute(database, """
            CREATE TRIGGER replaced_delete AFTER DELETE ON items FOR EACH ROW LANGUAGE SQL AS BEGIN
                INSERT INTO audit(id,kind,seen)
                SELECT 1,'delete',c.n FROM (SELECT COUNT(*) AS n FROM items) AS c;
            END
            """);
        Execute(database, """
            CREATE TRIGGER replaced_insert AFTER INSERT ON items FOR EACH ROW LANGUAGE SQL AS BEGIN
                INSERT INTO audit(id,kind,seen)
                SELECT 2,'insert',c.n FROM (SELECT COUNT(*) AS n FROM items) AS c;
            END
            """);
        Execute(database, """
            CREATE TRIGGER replaced_update AFTER UPDATE ON items FOR EACH ROW LANGUAGE SQL AS BEGIN
                INSERT INTO audit(id,kind,seen) VALUES(3,'update',-1);
            END
            """);

        Execute(database, "REPLACE INTO items(id,name) VALUES (1,'new')");

        var audit = Select(database, "SELECT kind,seen FROM audit ORDER BY id");
        Assert.Equal(2, audit.Rows.Count);
        Assert.Equal(new object?[] { "delete", 0L }, audit.Rows[0]);
        Assert.Equal(new object?[] { "insert", 1L }, audit.Rows[1]);
    }

    /// <summary>插入触发器失败必须撤销删除阶段及其审计副作用。</summary>
    [Fact]
    public void Execute_InsertTriggerFailure_RollsBackDeletesAndAudit()
    {
        using var database = Open();
        Execute(database, "CREATE TABLE items(id INT,name STRING,PRIMARY KEY(id))");
        Execute(database, "CREATE TABLE audit(id INT,PRIMARY KEY(id))");
        Execute(database, "INSERT INTO items(id,name) VALUES(1,'old')");
        Execute(database, "INSERT INTO audit(id) VALUES(1)");
        Execute(database, """
            CREATE TRIGGER replaced_delete AFTER DELETE ON items FOR EACH ROW LANGUAGE SQL AS BEGIN
                INSERT INTO audit(id) VALUES(2);
            END
            """);
        Execute(database, """
            CREATE TRIGGER replaced_insert AFTER INSERT ON items FOR EACH ROW LANGUAGE SQL AS BEGIN
                INSERT INTO audit(id) VALUES(1);
            END
            """);

        Assert.Throws<RoutineExecutionException>(() => Execute(database, "REPLACE INTO items(id,name) VALUES(1,'new')"));

        Assert.Equal("old", Assert.Single(Select(database, "SELECT name FROM items").Rows)[0]);
        Assert.Equal(1L, Assert.Single(Select(database, "SELECT id FROM audit").Rows)[0]);
    }

    /// <summary>RESTRICT 在真实删除阶段阻止替换，即使新行计划重用同一个主键。</summary>
    [Fact]
    public void Execute_RestrictedParent_RejectsDeleteAndPreservesChild()
    {
        using var database = Open();
        Execute(database, "CREATE TABLE parents(id INT,name STRING,PRIMARY KEY(id))");
        Execute(database, "CREATE TABLE children(id INT,parent_id INT,PRIMARY KEY(id),FOREIGN KEY(parent_id) REFERENCES parents(id))");
        Execute(database, "INSERT INTO parents(id,name) VALUES(1,'old')");
        Execute(database, "INSERT INTO children(id,parent_id) VALUES(10,1)");

        Assert.Throws<TableConstraintException>(() => Execute(database, "REPLACE INTO parents(id,name) VALUES(1,'new')"));

        Assert.Equal("old", Assert.Single(Select(database, "SELECT name FROM parents").Rows)[0]);
        Assert.Equal(1L, Assert.Single(Select(database, "SELECT parent_id FROM children").Rows)[0]);
    }

    /// <summary>同主键重插也必须执行 CASCADE 和 SET NULL，不能因净变化合并而漏掉删除动作。</summary>
    [Fact]
    public void Execute_ParentReplacement_AppliesCascadeAndSetNull()
    {
        using var database = Open();
        Execute(database, "CREATE TABLE parents(id INT,name STRING,PRIMARY KEY(id))");
        Execute(database, "CREATE TABLE cascaded(id INT,parent_id INT,PRIMARY KEY(id),FOREIGN KEY(parent_id) REFERENCES parents(id) ON DELETE CASCADE)");
        Execute(database, "CREATE TABLE nulled(id INT,parent_id INT,PRIMARY KEY(id),FOREIGN KEY(parent_id) REFERENCES parents(id) ON DELETE SET NULL)");
        Execute(database, "INSERT INTO parents(id,name) VALUES(1,'old')");
        Execute(database, "INSERT INTO cascaded(id,parent_id) VALUES(10,1)");
        Execute(database, "INSERT INTO nulled(id,parent_id) VALUES(20,1)");

        Execute(database, "REPLACE INTO parents(id,name) VALUES(1,'new')");

        Assert.Equal("new", Assert.Single(Select(database, "SELECT name FROM parents").Rows)[0]);
        Assert.Empty(Select(database, "SELECT * FROM cascaded").Rows);
        Assert.Null(Assert.Single(Select(database, "SELECT parent_id FROM nulled").Rows)[0]);
    }

    /// <summary>外层事务回滚会撤销整个替换，并恢复此前的唯一索引。</summary>
    [Fact]
    public void Execute_ExplicitRollback_RestoresOldRowsAndIndex()
    {
        using var database = Open();
        CreateUniqueItems(database);
        Execute(database, "INSERT INTO items(id,email,phone) VALUES(1,'a','p1')");
        var transaction = Assert.IsType<SqlTransactionContext>(Execute(database, "BEGIN"));
        try
        {
            Execute(database, "REPLACE INTO items(id,email,phone) VALUES(1,'b','p2')", transaction);
            Assert.Equal("b", Assert.Single(Select(database, "SELECT email FROM items", transaction).Rows)[0]);
            Execute(database, "ROLLBACK", transaction);
        }
        finally
        {
            if (!transaction.IsCompleted)
                Execute(database, "ROLLBACK", transaction);
        }
        Assert.Equal("a", Assert.Single(Select(database, "SELECT email FROM items").Rows)[0]);
        Assert.Equal(1L, Assert.Single(Select(database, "SELECT id FROM items WHERE email='a'").Rows)[0]);
        Assert.Empty(Select(database, "SELECT id FROM items WHERE email='b'").Rows);
    }

    /// <summary>省略自增键时分配新键，唯一冲突旧行被删除且 ROWVERSION 重新初始化。</summary>
    [Fact]
    public void Execute_UniqueConflictWithGeneratedIdentity_CreatesFreshRowVersion()
    {
        using var database = Open();
        Execute(database, "CREATE TABLE items(id INT AUTO_INCREMENT,email STRING,name STRING DEFAULT 'fresh',version INT ROWVERSION,PRIMARY KEY(id))");
        Execute(database, "CREATE UNIQUE INDEX by_email ON items(email)");
        Execute(database, "INSERT INTO items(email,name) VALUES('same','old')");
        Execute(database, "UPDATE items SET name='edited' WHERE id=1");

        var result = Assert.IsType<InsertExecutionResult>(Execute(database,
            "REPLACE INTO items(email) VALUES('same') RETURNING id,name,version"));

        Assert.Equal(1, result.RowsInserted);
        Assert.Equal(new object?[] { 2L, "fresh", 1L }, Assert.Single(result.Returning!.Rows));
        Assert.Empty(Select(database, "SELECT * FROM items WHERE id=1").Rows);
    }

    /// <summary>无显式主键时按唯一索引替换旧物理行，并重新应用遗漏列的默认值。</summary>
    [Fact]
    public void Execute_WithoutPrimaryKey_ReplacesUniqueOwnerAndDefaults()
    {
        using var database = Open();
        Execute(database, "CREATE TABLE items(name STRING,value INT DEFAULT 7)");
        Execute(database, "CREATE UNIQUE INDEX by_name ON items(name)");
        Execute(database, "INSERT INTO items(name,value) VALUES('same',11)");

        var result = Assert.IsType<InsertExecutionResult>(Execute(database, "REPLACE INTO items(name) VALUES('same')"));

        Assert.Equal(1, result.RowsInserted);
        Assert.Equal(new object?[] { "same", 7L }, Assert.Single(Select(database, "SELECT * FROM items").Rows));
    }

    /// <summary>失败批次回滚行与索引，预留的 identity 号码遵循原 INSERT 合同不复用。</summary>
    [Fact]
    public void Execute_FailedReplacement_RollsBackRowsButRetainsIdentityGap()
    {
        using var database = Open();
        Execute(database, "CREATE TABLE items(id INT AUTO_INCREMENT,name STRING,value INT,PRIMARY KEY(id),CHECK(value>0))");
        Execute(database, "CREATE UNIQUE INDEX by_name ON items(name)");
        Execute(database, "INSERT INTO items(name,value) VALUES('old',1)");

        Assert.Throws<TableConstraintException>(() => Execute(database, "REPLACE INTO items(name,value) VALUES('old',-1)"));
        Execute(database, "INSERT INTO items(name,value) VALUES('next',2)");

        Assert.Equal(1L, Assert.Single(Select(database, "SELECT id FROM items WHERE name='old'").Rows)[0]);
        Assert.Equal(3L, Assert.Single(Select(database, "SELECT id FROM items WHERE name='next'").Rows)[0]);
    }

    /// <summary>替换事务保留数据库写门禁，防止删除阶段的外键或唯一键检查之后插入并发幻影。</summary>
    [Fact]
    public void Execute_ExplicitReplacement_RetainsWriteGateUntilRollback()
    {
        using var database = Open();
        Execute(database, "CREATE TABLE items(id INT,name STRING,PRIMARY KEY(id))");
        Execute(database, "INSERT INTO items(id,name) VALUES(1,'old'),(2,'other')");
        var first = Assert.IsType<SqlTransactionContext>(Execute(database, "BEGIN"));
        var second = Assert.IsType<SqlTransactionContext>(Execute(database, "BEGIN"));
        try
        {
            Execute(database, "REPLACE INTO items(id,name) VALUES(1,'new')", first);
            var error = Assert.Throws<SqlRowLockException>(() =>
                Execute(database, "SELECT id FROM items WHERE id=2 FOR UPDATE NOWAIT", second));
            Assert.Equal(SqlRowLockException.NotAvailable, error.Code);
            Execute(database, "ROLLBACK", first);
            Assert.Equal(2L, Assert.Single(Select(database,
                "SELECT id FROM items WHERE id=2 FOR UPDATE NOWAIT", second).Rows)[0]);
        }
        finally
        {
            if (!first.IsCompleted)
                Execute(database, "ROLLBACK", first);
            if (!second.IsCompleted)
                Execute(database, "ROLLBACK", second);
        }
    }

    /// <summary>DEFAULT VALUES 保持原生生成列语义，并返回实际生成的新行。</summary>
    [Fact]
    public void Execute_DefaultValues_InsertsGeneratedRow()
    {
        using var database = Open();
        Execute(database, "CREATE TABLE items(id INT AUTO_INCREMENT,name STRING DEFAULT 'fresh',PRIMARY KEY(id))");

        var result = Assert.IsType<InsertExecutionResult>(Execute(database, "REPLACE INTO items DEFAULT VALUES RETURNING id,name"));

        Assert.Equal(1, result.RowsInserted);
        Assert.Equal(new object?[] { 1L, "fresh" }, Assert.Single(result.Returning!.Rows));
    }

    /// <summary>事务先持有自身行锁时能够升级为替换写门禁，提交后全部锁归还。</summary>
    [Fact]
    public void Execute_AfterOwnRowLock_UpgradesAndCommitsWithoutDeadlock()
    {
        using var database = Open();
        CreateLockItems(database);
        var transaction = new SqlTransactionContext();
        try
        {
            Execute(database, "SELECT id FROM items WHERE id=1 FOR UPDATE", transaction);
            Execute(database, "REPLACE INTO items(id,name) VALUES(2,'replacement')", transaction);
            Execute(database, "COMMIT", transaction);
            Assert.Equal("replacement", Assert.Single(Select(database, "SELECT name FROM items WHERE id=2").Rows)[0]);
        }
        finally { Finish(transaction); }
        Assert.Equal((0, 0, 0), database.Tables.RowLocks.Inspect());
    }

    /// <summary>升级等另一事务的旧行锁时，持锁者仍可提交；替换在取门禁后读取新提交行。</summary>
    [Fact]
    public async Task Execute_UpgradeWait_ReleasesMonitorsAndRechecksAfterHolderCommit()
    {
        using var database = Open();
        CreateLockItems(database);
        var replacing = new SqlTransactionContext();
        var holder = new SqlTransactionContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<object?>? pending = null;
        try
        {
            Execute(database, "SELECT id FROM items WHERE id=1 FOR UPDATE", replacing);
            Execute(database, "SELECT id FROM items WHERE id=2 FOR UPDATE", holder);
            pending = Start(() => SqlExecutor.ExecuteStatement(database, null,
                SqlParser.Parse("REPLACE INTO items(id,name) VALUES(2,'replacement')"), null, replacing,
                new SqlExecutionOptions { CancellationToken = cancellation.Token, TransactionCommitTimeoutMilliseconds = 5_000 }));
            await WaitQueued(database, cancellation.Token);
            Execute(database, "UPDATE items SET name='committed' WHERE id=2", holder);
            Execute(database, "COMMIT", holder);
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("committed", Assert.Single(Select(database, "SELECT name FROM items WHERE id=2").Rows)[0]);
            Execute(database, "COMMIT", replacing);
            Assert.Equal("replacement", Assert.Single(Select(database, "SELECT name FROM items WHERE id=2").Rows)[0]);
        }
        finally
        {
            cancellation.Cancel();
            Finish(holder);
            try
            {
                if (pending is not null)
                {
                    try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                }
            }
            finally { Finish(replacing); }
        }
        Assert.Equal((0, 0, 0), database.Tables.RowLocks.Inspect());
    }

    /// <summary>普通并发写确实进入等待队列，替换回滚后重新取锁并完成写入。</summary>
    [Fact]
    public async Task Execute_ConcurrentOrdinaryWrite_WaitsUntilReplacementRollsBack()
    {
        using var database = Open();
        CreateLockItems(database);
        var replacing = new SqlTransactionContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<object?>? pending = null;
        try
        {
            Execute(database, "REPLACE INTO items(id,name) VALUES(1,'replacement')", replacing);
            pending = Start(() => SqlExecutor.ExecuteStatement(database, null,
                SqlParser.Parse("UPDATE items SET name='ordinary' WHERE id=2"), null, null,
                new SqlExecutionOptions { CancellationToken = cancellation.Token, TransactionCommitTimeoutMilliseconds = 5_000 }));
            await WaitQueued(database, cancellation.Token);
            Assert.Equal("other", Assert.Single(Select(database, "SELECT name FROM items WHERE id=2").Rows)[0]);
            Execute(database, "ROLLBACK", replacing);
            Assert.Equal(1, Assert.IsType<RowsAffectedExecutionResult>(await pending.WaitAsync(TimeSpan.FromSeconds(5))).RowsAffected);
            Assert.Equal("ordinary", Assert.Single(Select(database, "SELECT name FROM items WHERE id=2").Rows)[0]);
            Assert.Equal("old", Assert.Single(Select(database, "SELECT name FROM items WHERE id=1").Rows)[0]);
        }
        finally
        {
            cancellation.Cancel();
            Finish(replacing);
            if (pending is not null)
            {
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
        }
        Assert.Equal((0, 0, 0), database.Tables.RowLocks.Inspect());
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static object? Execute(Tsdb database, string sql, SqlTransactionContext? transaction = null)
        => transaction is null ? SqlExecutor.Execute(database, sql)
            : SqlExecutor.ExecuteStatement(database, databaseName: null, SqlParser.Parse(sql), controlPlane: null, transaction: transaction);

    private static SelectExecutionResult Select(Tsdb database, string sql, SqlTransactionContext? transaction = null)
        => Assert.IsType<SelectExecutionResult>(Execute(database, sql, transaction));

    private static void CreateUniqueItems(Tsdb database)
    {
        Execute(database, "CREATE TABLE items(id INT,email STRING,phone STRING,PRIMARY KEY(id))");
        Execute(database, "CREATE UNIQUE INDEX by_email ON items(email)");
        Execute(database, "CREATE UNIQUE INDEX by_phone ON items(phone)");
    }

    private static void CreateLockItems(Tsdb database)
    {
        Execute(database, "CREATE TABLE items(id INT,name STRING,PRIMARY KEY(id))");
        Execute(database, "INSERT INTO items(id,name) VALUES(1,'old'),(2,'other')");
    }

    private static Task<T> Start<T>(Func<T> operation)
        => Task.Factory.StartNew(operation, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static async Task WaitQueued(Tsdb database, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        for (int attempt = 0; attempt < 200 && clock.Elapsed < TimeSpan.FromSeconds(3); attempt++)
        {
            if (database.Tables.RowLocks.Inspect().Waiting > 0)
                return;
            await Task.Delay(10, cancellationToken);
        }
        Assert.Fail("真实写门禁等待队列未建立。");
    }

    private static void Finish(SqlTransactionContext transaction)
    {
        if (!transaction.IsCompleted)
            transaction.MarkCompleted();
    }

    /// <summary>删除测试独占数据库目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
