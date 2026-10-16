using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>GH-Issue #248：受支持锁定读保留 AST 信息，未支持组合不得退化为普通 SELECT。</summary>
public sealed class SqlLockingReadContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-locking-read-" + Guid.NewGuid().ToString("N"));

    public SqlLockingReadContractTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试清理不覆盖断言。 */ }
    }

    [Theory]
    [InlineData("SELECT id FROM jobs ORDER BY id LIMIT 1 FOR UPDATE SKIP LOCKED")]
    [InlineData("SELECT id FROM jobs NOWAIT")]
    [InlineData("SELECT id FROM jobs SKIP LOCKED")]
    [InlineData("SELECT id FROM jobs FOR SHARE")]
    public void Parse_LockingReadClause_ReturnsStableUnsupportedCode(string sql)
    {
        var error = Assert.Throws<SqlParseException>(() => SqlParser.Parse(sql));

        Assert.Equal(SqlErrorCodes.LockingReadUnsupported, error.Code);
        Assert.Equal("select_locking_read", error.Operation);
        Assert.Contains("ROWVERSION", error.Hint, StringComparison.Ordinal);
        Assert.Equal(SqlErrorCodes.LockingReadUnsupported,
            SqlErrorMapper.Map(error, "parse").Code);
    }

    [Theory]
    [InlineData("SELECT id FROM jobs FOR UPDATE", false)]
    [InlineData("SELECT id FROM jobs WHERE id = @id ORDER BY id LIMIT 1 FOR UPDATE NOWAIT", true)]
    public void Parse_SupportedLockingRead_PreservesModifiers(string sql, bool noWait)
    {
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(sql));
        Assert.True(statement.ForUpdate);
        Assert.Equal(noWait, statement.NoWait);
    }

    [Fact]
    public void Execute_ManuallyConstructedBareNoWait_DoesNotSilentlyRead()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE jobs(id INT,PRIMARY KEY(id))");
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse("SELECT id FROM jobs")) with { NoWait = true };
        Assert.Equal(SqlErrorCodes.LockingReadUnsupported, Assert.Throws<SqlParseException>(() =>
            SqlExecutor.ExecuteSelect(db, statement)).Code);
    }

    [Fact]
    public void Execute_LockingReadOnExistingAndMissingRows_LeavesDataUnchanged()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db,
            "CREATE TABLE jobs (id INT, status STRING, version INT ROWVERSION, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO jobs (id, status) VALUES (1, 'ready')");

        foreach (long id in new long[] { 1, 404 })
        {
            var error = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
                db, $"SELECT id, version FROM jobs WHERE id = {id} FOR UPDATE"));
            Assert.Contains("显式事务", error.Message, StringComparison.Ordinal);
        }

        var row = Assert.Single(Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SELECT status, version FROM jobs WHERE id = 1")).Rows);
        Assert.Equal(new object?[] { "ready", 1L }, row);
    }

    [Theory]
    [InlineData("SELECT COUNT(*) FROM jobs FOR UPDATE")]
    [InlineData("SELECT DISTINCT id FROM jobs FOR UPDATE")]
    [InlineData("SELECT a.id FROM jobs a JOIN jobs b ON a.id=b.id FOR UPDATE")]
    [InlineData("SELECT * FROM (SELECT id FROM jobs FOR UPDATE) q")]
    [InlineData("SELECT (SELECT id FROM jobs FOR UPDATE) FROM jobs")]
    [InlineData("WITH q AS (SELECT id FROM jobs FOR UPDATE) SELECT * FROM q")]
    [InlineData("EXPLAIN SELECT id FROM jobs FOR UPDATE")]
    [InlineData("CREATE VIEW locked AS SELECT id FROM jobs FOR UPDATE")]
    public void Execute_UnsupportedCombination_ExplicitlyRejects(string sql)
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE jobs(id INT,PRIMARY KEY(id))");
        var transaction = new SqlTransactionContext();
        try
        {
            var statement = SqlParser.Parse(sql);
            var error = Assert.Throws<SqlParseException>(() =>
                SqlExecutor.ExecuteStatement(db, null, statement, null, transaction));
            Assert.Equal(SqlErrorCodes.LockingReadUnsupported, error.Code);
        }
        finally { transaction.MarkCompleted(); }
    }
}
