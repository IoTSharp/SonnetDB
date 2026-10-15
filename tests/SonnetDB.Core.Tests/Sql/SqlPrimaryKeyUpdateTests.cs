using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证普通 INT 主键更新的数据、索引、事务和恢复合同。</summary>
public sealed class SqlPrimaryKeyUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-pk312-" + Guid.NewGuid().ToString("N"));

    /// <summary>多行改键同时保持普通索引、唯一索引、返回值和正常重开结果。</summary>
    [Fact]
    public void Update_PrimaryKeys_PreservesIndexesReturningAndReopen()
    {
        using (var database = Open())
        {
            Create(database);
            var result = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(database,
                "UPDATE \"Items\" SET \"Id\"=\"Id\"+100 RETURNING \"Id\",\"Name\",\"Version\""));
            Assert.Equal(2, result.RowsAffected);
            Assert.Equal([101L, 102L], result.Returning!.Rows.Select(row => (long)row[0]!));
            Assert.All(result.Returning.Rows, row => Assert.Equal(2L, row[2]));
            Assert.Empty(Query(database, "SELECT Id FROM Items WHERE Id IN (1,2)").Rows);
            AssertIndexes(database, 101, 102);
        }
        using var reopened = Open();
        AssertIndexes(reopened, 101, 102);
        SqlExecutor.Execute(reopened, "INSERT INTO Items(Id,Name,GroupId) VALUES(1,'replacement',3)");
        Assert.Equal(3L, Query(reopened, "SELECT COUNT(*) FROM Items").Rows[0][0]);
    }

    /// <summary>目标重复、交换、顺延、NULL、CHECK 和唯一索引失败不改变任何行或索引。</summary>
    [Theory]
    [InlineData("UPDATE Items SET Id=2 WHERE Id=1")]
    [InlineData("UPDATE Items SET Id=101")]
    [InlineData("UPDATE Items SET Id=3-Id")]
    [InlineData("UPDATE Items SET Id=Id+1")]
    [InlineData("UPDATE Items SET Id=NULL WHERE Id=1")]
    [InlineData("UPDATE Items SET Id=-Id")]
    [InlineData("UPDATE Items SET Id=Id+100,Name='same'")]
    public void Update_InvalidTarget_RollsBackAllRowsAndIndexes(string sql)
    {
        using var database = Open();
        Create(database);
        Assert.ThrowsAny<InvalidOperationException>(() => SqlExecutor.Execute(database, sql));
        AssertIndexes(database, 1, 2);
        Assert.All(Query(database, "SELECT Version FROM Items").Rows, row => Assert.Equal(1L, row[0]));
    }

    /// <summary>事务按当前键连续归并，点查和扫描都看到最终行，回滚与提交正常恢复。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Update_TransactionChain_PreservesVisibleKeysAndReopen(bool commit)
    {
        using (var connection = Connection())
        {
            Execute(connection, "CREATE TABLE Items(Id INT,Name STRING,PRIMARY KEY(Id))");
            Execute(connection, "CREATE UNIQUE INDEX ux_name ON Items(Name)");
            Execute(connection, "INSERT INTO Items(Id,Name) VALUES(1,'a'),(2,'b')");
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE Items SET Id=101 WHERE Id=1";
            Assert.Equal(1, command.ExecuteNonQuery());
            command.CommandText = "SELECT COUNT(*) FROM Items WHERE Id=1";
            Assert.Equal(0L, command.ExecuteScalar());
            command.CommandText = "UPDATE Items SET Id=201,Name='changed' WHERE Id=101";
            Assert.Equal(1, command.ExecuteNonQuery());
            command.CommandText = "UPDATE Items SET Name='final' WHERE Id=201";
            Assert.Equal(1, command.ExecuteNonQuery());
            command.CommandText = "SELECT COUNT(*) FROM Items WHERE Id IN (1,101)";
            Assert.Equal(0L, command.ExecuteScalar());
            command.CommandText = "SELECT Name FROM Items WHERE Id=201";
            Assert.Equal("final", command.ExecuteScalar());
            command.CommandText = "SELECT COUNT(*) FROM Items";
            Assert.Equal(2L, command.ExecuteScalar());
            if (commit) transaction.Commit();
            else transaction.Rollback();
        }
        using var reopened = Connection();
        Assert.Equal(commit ? "final" : "a", Scalar(reopened,
            $"SELECT Name FROM Items WHERE Id={(commit ? 201 : 1)}"));
        Assert.Equal(commit ? 201L : 1L, Scalar(reopened,
            $"SELECT Id FROM Items WHERE Name='{(commit ? "final" : "a")}'"));
        Assert.Equal(2L, Scalar(reopened, "SELECT COUNT(*) FROM Items"));
    }

    /// <summary>新插入行改键后删除消除净写入，已有行改键后删除移除原始持久键。</summary>
    [Fact]
    public void Update_ThenDeleteAndInsertedRowUpdate_MergesFinalWriteSet()
    {
        using var connection = Connection();
        Execute(connection, "CREATE TABLE Items(Id INT,Name STRING,PRIMARY KEY(Id))");
        Execute(connection, "INSERT INTO Items(Id,Name) VALUES(1,'a')");
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        foreach (string sql in new[]
        {
            "UPDATE Items SET Id=101 WHERE Id=1",
            "DELETE FROM Items WHERE Id=101",
            "INSERT INTO Items(Id,Name) VALUES(2,'b')",
            "UPDATE Items SET Id=102 WHERE Id=2",
            "DELETE FROM Items WHERE Id=102",
            "INSERT INTO Items(Id,Name) VALUES(3,'c')",
            "UPDATE Items SET Id=103 WHERE Id=3",
        })
        {
            command.CommandText = sql;
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        command.CommandText = "SELECT COUNT(*) FROM Items";
        Assert.Equal(1L, command.ExecuteScalar());
        transaction.Commit();
        Assert.Equal("c", Scalar(connection, "SELECT Name FROM Items WHERE Id=103"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM Items"));
    }

    /// <summary>事务内目标冲突整句回滚缓冲；已成功的前一条语句仍能提交。</summary>
    [Fact]
    public void Update_TransactionCollision_PreservesEarlierStatement()
    {
        using var connection = Connection();
        Execute(connection, "CREATE TABLE Items(Id INT,Name STRING,PRIMARY KEY(Id))");
        Execute(connection, "INSERT INTO Items(Id,Name) VALUES(1,'a'),(2,'b')");
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE Items SET Id=101 WHERE Id=1";
        command.ExecuteNonQuery();
        command.CommandText = "UPDATE Items SET Id=201";
        Assert.ThrowsAny<InvalidOperationException>(() => command.ExecuteNonQuery());
        command.CommandText = "UPDATE Items SET Id=101 WHERE Id=2";
        Assert.ThrowsAny<InvalidOperationException>(() => command.ExecuteNonQuery());
        command.CommandText = "SELECT COUNT(*) FROM Items WHERE Id=101";
        Assert.Equal(1L, command.ExecuteScalar());
        transaction.Commit();
        Assert.Equal("a", Scalar(connection, "SELECT Name FROM Items WHERE Id=101"));
        Assert.Equal("b", Scalar(connection, "SELECT Name FROM Items WHERE Id=2"));
    }

    /// <summary>同事务重新插入仍持久化的旧键明确失败，不能覆盖已改键行。</summary>
    [Fact]
    public void Update_ReinsertPersistedOldKey_RejectsCommitWithoutPublishing()
    {
        using var connection = Connection();
        Execute(connection, "CREATE TABLE Items(Id INT,Name STRING,PRIMARY KEY(Id))");
        Execute(connection, "INSERT INTO Items(Id,Name) VALUES(1,'a')");
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE Items SET Id=101 WHERE Id=1";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO Items(Id,Name) VALUES(1,'replacement')";
        command.ExecuteNonQuery();
        Assert.ThrowsAny<InvalidOperationException>(() => transaction.Commit());
        Assert.Equal("a", Scalar(connection, "SELECT Name FROM Items WHERE Id=1"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM Items WHERE Id=101"));
    }

    /// <summary>入站或出站外键，包括无数据的组合，都在改键前拒绝。</summary>
    [Theory]
    [InlineData("Parent")]
    [InlineData("Child")]
    public void Update_ForeignKeys_RejectsBeforeMutation(string table)
    {
        using var database = Open();
        SqlExecutor.Execute(database, "CREATE TABLE Parent(Id INT,PRIMARY KEY(Id))");
        SqlExecutor.Execute(database,
            "CREATE TABLE Child(Id INT,ParentId INT,PRIMARY KEY(Id),FOREIGN KEY(ParentId) REFERENCES Parent(Id))");
        Assert.Throws<NotSupportedException>(() => SqlExecutor.Execute(database, $"UPDATE {table} SET Id=101 WHERE Id=1"));
        SqlExecutor.Execute(database, "INSERT INTO Parent(Id) VALUES(1)");
        SqlExecutor.Execute(database, "INSERT INTO Child(Id,ParentId) VALUES(1,1)");
        Assert.Throws<NotSupportedException>(() => SqlExecutor.Execute(database, $"UPDATE {table} SET Id=101 WHERE Id=1"));
        Assert.Equal(1L, Query(database, "SELECT Id FROM Parent").Rows[0][0]);
        Assert.Equal(1L, Query(database, "SELECT Id FROM Child").Rows[0][0]);
    }

    /// <summary>提交前新增外键后重新校验当前目录，不能提交已缓冲的非法改键。</summary>
    [Fact]
    public void Update_ForeignKeyAddedBeforeCommit_RevalidatesCurrentSchema()
    {
        using var database = Open();
        SqlExecutor.Execute(database, "CREATE TABLE Parent(Id INT,PRIMARY KEY(Id))");
        SqlExecutor.Execute(database, "INSERT INTO Parent(Id) VALUES(1)");
        var transaction = new SqlTransactionContext();
        SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse("UPDATE Parent SET Id=101 WHERE Id=1"), null, transaction);
        SqlExecutor.Execute(database,
            "CREATE TABLE Child(Id INT,ParentId INT,PRIMARY KEY(Id),FOREIGN KEY(ParentId) REFERENCES Parent(Id))");
        Assert.Throws<NotSupportedException>(() => TableSqlExecutor.CommitTransaction(database, transaction));
        Assert.Equal(1L, Query(database, "SELECT Id FROM Parent").Rows[0][0]);
    }

    /// <summary>复合键、非 INT 键以及数据库生成的列赋值继续明确拒绝。</summary>
    [Theory]
    [InlineData("CREATE TABLE T(Id INT,Other INT,PRIMARY KEY(Id,Other))", "UPDATE T SET Id=101 WHERE Id=1")]
    [InlineData("CREATE TABLE T(Id STRING,PRIMARY KEY(Id))", "UPDATE T SET Id='next' WHERE Id='old'")]
    [InlineData("CREATE TABLE T(Id INT AUTO_INCREMENT,PRIMARY KEY(Id))", "UPDATE T SET Id=101 WHERE Id=1")]
    [InlineData("CREATE TABLE T(Id INT,Version INT ROWVERSION,PRIMARY KEY(Id))", "UPDATE T SET Version=101 WHERE Id=1")]
    public void Update_UnsupportedColumns_RejectsBeforeScanning(string ddl, string update)
    {
        using var database = Open();
        SqlExecutor.Execute(database, ddl);
        Assert.ThrowsAny<Exception>(() => SqlExecutor.Execute(database, update));
    }

    /// <summary>提交时再次验证原行版本和目标主键，不能覆盖并发提交的行。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Update_ConcurrentWriteBeforeCommit_PreservesBothCommittedRows(bool targetInserted)
    {
        using var database = Open();
        Create(database);
        var transaction = new SqlTransactionContext();
        SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse("UPDATE Items SET Id=101 WHERE Id=1"), null, transaction);
        SqlExecutor.Execute(database, targetInserted
            ? "INSERT INTO Items(Id,Name,GroupId) VALUES(101,'other',8)"
            : "UPDATE Items SET Name='other' WHERE Id=1");
        Assert.Throws<TableConstraintException>(() => TableSqlExecutor.CommitTransaction(database, transaction));
        Assert.Equal(targetInserted ? "a" : "other", Query(database, "SELECT Name FROM Items WHERE Id=1").Rows[0][0]);
        Assert.Equal(targetInserted ? 1 : 0, Query(database, "SELECT Id FROM Items WHERE Id=101").Rows.Count);
        Assert.Equal(2L, Query(database, "SELECT Id FROM Items WHERE Name='b'").Rows[0][0]);
    }

    /// <summary>多表持久事务的失败补偿覆盖新旧主键，重开后不能留下新键或索引。</summary>
    [Fact]
    public void Update_MultiTableCommitFailure_RestoresOldAndNewKeys()
    {
        using (var database = Open())
        {
            Create(database);
            SqlExecutor.Execute(database, "CREATE TABLE Other(Id INT,PRIMARY KEY(Id))");
            SqlExecutor.Execute(database, "INSERT INTO Other(Id) VALUES(1)");
            var transaction = new SqlTransactionContext();
            SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse("UPDATE Items SET Id=101 WHERE Id=1"), null, transaction);
            SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse("UPDATE Other SET Id=101 WHERE Id=1"), null, transaction);
            database.Tables.ApplyTransactionAfterTableTestHook = _ => throw new IOException("injected commit failure");
            Assert.Throws<IOException>(() => TableSqlExecutor.CommitTransaction(database, transaction));
            database.Tables.ApplyTransactionAfterTableTestHook = null;
            AssertIndexes(database, 1, 2);
            Assert.Empty(Query(database, "SELECT Id FROM Items WHERE Id=101").Rows);
        }
        using var reopened = Open();
        AssertIndexes(reopened, 1, 2);
        Assert.Equal(1L, Query(reopened, "SELECT Id FROM Other").Rows[0][0]);
    }

    /// <summary>持久恢复日志提交标记前后重开，分别撤销部分表批次或保留完整改键。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Update_JournalRecovery_RestoresAtomicPrimaryKeys(bool completed)
    {
        using (var database = Open())
        {
            Create(database);
            SqlExecutor.Execute(database, "CREATE TABLE Other(Id INT,PRIMARY KEY(Id))");
            SqlExecutor.Execute(database, "INSERT INTO Other(Id) VALUES(1)");
            var items = database.Tables.Open("Items");
            var other = database.Tables.Open("Other");
            var itemBatch = items.PrepareBatch([new TableRowMutation([1L], [101L, "a", 9L, 2L])]);
            var otherBatch = other.PrepareBatch([new TableRowMutation([1L], [101L])]);
            string journal = Path.Combine(Path.GetDirectoryName(database.Tables.SchemaPath)!, TableTransactionJournal.FileName);
            TableTransactionJournal.Prepare(journal,
                [items.CaptureTransactionUndo(itemBatch), other.CaptureTransactionUndo(otherBatch)]);
            items.ApplyPreparedBatch(itemBatch);
            items.SyncTransactionWal();
            if (completed)
            {
                other.ApplyPreparedBatch(otherBatch);
                other.SyncTransactionWal();
                TableTransactionJournal.Complete(journal);
            }
            database.CrashSimulationCloseWal();
        }
        using var reopened = Open();
        AssertIndexes(reopened, completed ? 101 : 1, 2);
        Assert.Equal(completed ? 101L : 1L, Query(reopened, "SELECT Id FROM Other").Rows[0][0]);
        Assert.Empty(Query(reopened, $"SELECT Id FROM Items WHERE Id={(completed ? 1 : 101)}").Rows);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private SndbConnection Connection()
    {
        var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        return connection;
    }

    private static void Execute(SndbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SndbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static SelectExecutionResult Query(Tsdb database, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql));

    private static void Create(Tsdb database)
    {
        SqlExecutor.Execute(database,
            "CREATE TABLE Items(Id INT,Name STRING,GroupId INT,Version INT ROWVERSION,PRIMARY KEY(Id),CHECK(Id>0))");
        SqlExecutor.Execute(database, "CREATE UNIQUE INDEX ux_name ON Items(Name)");
        SqlExecutor.Execute(database, "CREATE INDEX ix_group ON Items(GroupId)");
        SqlExecutor.Execute(database, "INSERT INTO Items(Id,Name,GroupId) VALUES(1,'a',9),(2,'b',9)");
    }

    private static void AssertIndexes(Tsdb database, long first, long second)
    {
        Assert.Equal(first, Assert.Single(Query(database, "SELECT Id FROM Items WHERE Name='a'").Rows)[0]);
        Assert.Equal(second, Assert.Single(Query(database, "SELECT Id FROM Items WHERE Name='b'").Rows)[0]);
        Assert.Equal(new[] { first, second }.Order(), Query(database, "SELECT Id FROM Items WHERE GroupId=9 ORDER BY Id")
            .Rows.Select(row => (long)row[0]!));
        Assert.Equal(2L, Query(database, "SELECT COUNT(*) FROM Items").Rows[0][0]);
    }

    /// <summary>回收本夹具创建的唯一目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
