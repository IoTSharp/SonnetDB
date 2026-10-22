using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证连接会话作用域在异步调用链中正确流动和恢复。</summary>
public sealed class SqlSessionAsyncScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-session-async-" + Guid.NewGuid().ToString("N"));

    /// <summary>跨 await 和线程池切换后仍保持当前会话，退出作用域后恢复为空。</summary>
    [Fact]
    public async Task EnterScope_AcrossAwaitAndTaskRun_RestoresRequestContext()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        using var session = new SqlSessionContext(database);

        Assert.Null(SqlSessionContext.Current);
        using (session.EnterScope())
        {
            Assert.Same(session, SqlSessionContext.Current);
            await Task.Yield();
            Assert.Same(session, SqlSessionContext.Current);
            await Task.Run(async () =>
            {
                await Task.Delay(1);
                Assert.Same(session, SqlSessionContext.Current);
            });
            Assert.Same(session, SqlSessionContext.Current);
        }
        Assert.Null(SqlSessionContext.Current);
    }

    /// <summary>过程可以访问调用连接已有 TEMP，但过程 body 本身仍不开放 TEMP DDL。</summary>
    [Fact]
    public void ProcedureBody_CreateTempTable_IsRejectedByExistingRoutineBoundary()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        using var session = new SqlSessionContext(database);

        var error = Assert.Throws<ArgumentException>(() => SqlExecutor.ExecuteStatement(
            database,
            databaseName: null,
            SqlParser.Parse("CREATE PROCEDURE bad_temp() LANGUAGE SQL AS BEGIN CREATE TEMP TABLE staged(Id INT); END"),
            controlPlane: null,
            transaction: null,
            new SqlExecutionOptions { SessionContext = session }));

        Assert.Contains("只允许 SELECT / INSERT / UPDATE / DELETE / CALL", error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>只回收本测试创建的唯一目录。</summary>
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Assert.StartsWith(temporary, full, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("sndb-session-async-", Path.GetFileName(full), StringComparison.Ordinal);
        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
    }
}
