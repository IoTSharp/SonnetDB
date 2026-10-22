using System.Diagnostics;
using System.Text;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>通过真实 SQL 入口验证连接临时表、目录事务与集合更新。</summary>
public sealed class SqlTemporaryTableTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-temp-sql-" + Guid.NewGuid().ToString("N"));

    /// <summary>两连接同名隔离、未引用名称大小写与关闭后的目录边界真实成立。</summary>
    [Fact]
    public void Sql_TwoSessionSameName_PreservesCaseAndIndependentData()
    {
        using var database = Open();
        using var first = new SqlSessionContext(database);
        using var second = new SqlSessionContext(database);
        Run(database, first, "CREATE TEMP TABLE Stage(DeviceID INT,Value INT,PRIMARY KEY(DeviceID))");
        Assert.Throws<InvalidOperationException>(() => Run(database, second, "SELECT * FROM Stage"));
        Run(database, second, "CREATE TEMPORARY TABLE Stage(DeviceID INT,Value INT,PRIMARY KEY(DeviceID))");
        Run(database, first, "INSERT INTO STAGE(deviceid,value) VALUES(1,10)");
        Run(database, second, "INSERT INTO Stage(DeviceID,Value) VALUES(1,20)");
        Assert.Equal(10L, Scalar(database, first, "SELECT Value FROM Stage"));
        Assert.Equal(20L, Scalar(database, second, "SELECT Value FROM Stage"));
        Assert.Throws<InvalidOperationException>(() => Run(database, first, "SELECT \"deviceid\" FROM Stage"));
        Assert.Throws<InvalidOperationException>(() => Run(database, first, "CREATE TEMP TABLE \"stage\"(Id INT,PRIMARY KEY(Id))"));
        Assert.Throws<InvalidOperationException>(() => Run(database, first, "CREATE TABLE stage(Id INT,PRIMARY KEY(Id))"));
        Assert.Null(database.Tables.Catalog.TryGet("Stage"));
        first.Dispose();
        Assert.Equal(20L, Scalar(database, second, "SELECT Value FROM Stage"));
        Run(database, second, "DROP TABLE Stage");
        Assert.Empty(second.GetTemporaryTableSchemas());
    }

    /// <summary>临时 CREATE/DROP 与其数据在提交/回滚后分别发布或恢复。</summary>
    [Fact]
    public void Sql_TemporaryCreateDropAndData_FollowTransactionCompletion()
    {
        using var database = Open();
        using var session = new SqlSessionContext(database);
        Run(database, session, "CREATE TEMP TABLE Existing(Id INT,Value INT,PRIMARY KEY(Id))");
        Run(database, session, "INSERT INTO Existing(Id,Value) VALUES(1,10)");
        var rollback = new SqlTransactionContext();
        Run(database, session, "UPDATE Existing SET Value=20", rollback);
        Run(database, session, "DROP TABLE Existing", rollback);
        Run(database, session, "CREATE TEMP TABLE NewObject(Id INT,PRIMARY KEY(Id))", rollback);
        Run(database, session, "ROLLBACK", rollback);
        Assert.Equal(10L, Scalar(database, session, "SELECT Value FROM Existing"));
        Assert.Throws<InvalidOperationException>(() => Run(database, session, "SELECT * FROM NewObject"));
        var commit = new SqlTransactionContext();
        Run(database, session, "DROP TABLE Existing", commit);
        Run(database, session, "CREATE TEMP TABLE Committed(Id INT,PRIMARY KEY(Id))", commit);
        Run(database, session, "INSERT INTO Committed(Id) VALUES(7)", commit);
        Run(database, session, "COMMIT", commit);
        Assert.Equal(7L, Scalar(database, session, "SELECT Id FROM Committed"));
        Assert.Throws<InvalidOperationException>(() => Run(database, session, "SELECT * FROM Existing"));
        Assert.Empty(database.Tables.Catalog.Snapshot());
    }

    /// <summary>已有事务导入一万行临时 CTAS 源并集合更新，外部观察者只看到最终持久提交。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sql_TenThousandRowTemporarySource_SetUpdateCommitsOrRollsBack(bool commit)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var database = Open();
        using var session = new SqlSessionContext(database);
        Run(database, session, "CREATE TABLE Target(Id INT,Value INT,PRIMARY KEY(Id))", cancellationToken: deadline.Token);
        InsertBatches(database, session, "Target", 0, null, deadline.Token);
        var transaction = new SqlTransactionContext();
        Run(database, session, "CREATE TEMP TABLE Staged AS SELECT Id,Value FROM Target WHERE Id<0", transaction, deadline.Token);
        InsertBatches(database, session, "Staged", 5, transaction, deadline.Token);
        var updated = Assert.IsType<RowsAffectedExecutionResult>(Run(database, session,
            "UPDATE Target AS T SET Value=S.Value FROM Staged AS S WHERE T.Id=S.Id", transaction, deadline.Token));
        Assert.Equal(10_000, updated.RowsAffected);
        Assert.Equal(50_000L, Scalar(database, session, "SELECT SUM(Value) FROM Target", transaction));
        Assert.Equal(0L, Scalar(database, null, "SELECT SUM(Value) FROM Target"));
        Run(database, session, "DROP TABLE Staged", transaction, deadline.Token);
        Run(database, session, commit ? "COMMIT" : "ROLLBACK", transaction, deadline.Token);
        Assert.Equal(commit ? 50_000L : 0L, Scalar(database, null, "SELECT SUM(Value) FROM Target"));
        Assert.Throws<InvalidOperationException>(() => Run(database, session, "SELECT * FROM Staged"));
        Assert.Equal("Target", Assert.Single(database.Tables.Catalog.Snapshot()).Name);
    }

    private static void InsertBatches(Tsdb database, SqlSessionContext session, string name, long value,
        SqlTransactionContext? transaction, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        for (int batch = 0; batch < 10; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(60))
                throw new TimeoutException("一万行 SQL 夹具导入超时。");
            var sql = new StringBuilder($"INSERT INTO {name}(Id,Value) VALUES ");
            for (int index = 1; index <= 1_000; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index != 1) sql.Append(',');
                sql.Append('(').Append(batch * 1_000 + index).Append(',').Append(value).Append(')');
            }
            Assert.Equal(1_000, Assert.IsType<InsertExecutionResult>(Run(database, session, sql.ToString(),
                transaction, cancellationToken)).RowsInserted);
        }
    }

    private static object? Run(Tsdb database, SqlSessionContext? session, string sql,
        SqlTransactionContext? transaction = null, CancellationToken cancellationToken = default)
    {
        using IDisposable? scope = session?.EnterScope(cancellationToken);
        return SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse(sql), null, transaction,
            new SqlExecutionOptions { CancellationToken = cancellationToken });
    }

    private static object? Scalar(Tsdb database, SqlSessionContext? session, string sql,
        SqlTransactionContext? transaction = null)
        => Assert.Single(Assert.IsType<SelectExecutionResult>(Run(database, session, sql, transaction)).Rows)[0];

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    /// <summary>回收本测试独占的持久数据库目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
