using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>GH-Issue #190：锁定读语法必须稳定拒绝，不得退化为普通 SELECT。</summary>
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
    [InlineData("SELECT id FROM jobs FOR UPDATE")]
    [InlineData("SELECT id FROM jobs WHERE id = @id FOR UPDATE NOWAIT")]
    [InlineData("SELECT id FROM jobs ORDER BY id LIMIT 1 FOR UPDATE SKIP LOCKED")]
    [InlineData("SELECT id FROM jobs NOWAIT")]
    [InlineData("SELECT id FROM jobs SKIP LOCKED")]
    [InlineData("SELECT id FROM jobs FOR SHARE")]
    [InlineData("EXPLAIN SELECT id FROM jobs FOR UPDATE")]
    public void Parse_LockingReadClause_ReturnsStableUnsupportedCode(string sql)
    {
        var error = Assert.Throws<SqlParseException>(() => SqlParser.Parse(sql));

        Assert.Equal(SqlErrorCodes.LockingReadUnsupported, error.Code);
        Assert.Equal("select_locking_read", error.Operation);
        Assert.Contains("ROWVERSION", error.Hint, StringComparison.Ordinal);
        Assert.Equal(SqlErrorCodes.LockingReadUnsupported,
            SqlErrorMapper.Map(error, "parse").Code);
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
            var error = Assert.Throws<SqlParseException>(() => SqlExecutor.Execute(
                db, $"SELECT id, version FROM jobs WHERE id = {id} FOR UPDATE"));
            Assert.Equal(SqlErrorCodes.LockingReadUnsupported, error.Code);
        }

        var row = Assert.Single(Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SELECT status, version FROM jobs WHERE id = 1")).Rows);
        Assert.Equal(new object?[] { "ready", 1L }, row);
    }
}
