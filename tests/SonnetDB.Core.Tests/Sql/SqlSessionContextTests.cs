using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证真实临时 rowstore 的会话所有权、私有工作副本、容量及关闭回收。</summary>
public sealed class SqlSessionContextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-session-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>同库两个会话的同名临时表保存独立数据，且不进入持久目录。</summary>
    [Fact]
    public void TemporaryWorkspaces_SameNameInTwoSessions_KeepDataAndCatalogIsolated()
    {
        using var database = Open();
        using var first = new SqlSessionContext(database);
        using var second = new SqlSessionContext(database);
        Create(first, 10);
        Create(second, 20);
        Assert.Empty(database.Tables.Catalog.Snapshot());
        Assert.Equal(10L, Read(first));
        Assert.Equal(20L, Read(second));
        Assert.NotEqual(first.Id, second.Id);
        first.Dispose();
        Assert.False(Directory.Exists(first.ScratchDirectory));
        Assert.Equal(20L, Read(second));
        Assert.Throws<ObjectDisposedException>(() => first.EnterScope());
    }

    /// <summary>一万行真实存储工作副本只在明确发布后可见，丢弃副本保留原表。</summary>
    [Fact]
    public void TemporaryWorkspace_TenThousandRows_FailedStatementLeavesCommittedRows()
    {
        using var database = Open();
        using var session = new SqlSessionContext(database);
        using (session.EnterScope())
        using (var mutation = session.BeginTemporaryMutation(null))
        {
            mutation.Tables.Create(Schema());
            IReadOnlyList<object?>[] rows = Enumerable.Range(1, 10_000)
                .Select(static number => (IReadOnlyList<object?>)new object?[] { (long)number, 10L }).ToArray();
            mutation.Tables.Open("Stage").InsertMany(rows);
            mutation.Complete();
        }
        using (session.EnterScope())
        using (var mutation = session.BeginTemporaryMutation(null))
        {
            Assert.Equal(10_000, mutation.Tables.Open("Stage").RowCount);
            mutation.Tables.Drop("Stage");
            // 未 Complete 的语句副本必须回滚，不能修改原目录或行数据。
        }
        using (session.EnterScope())
            Assert.Equal(10_000, session.VisibleTables!.Open("Stage").RowCount);
        Assert.Equal("Stage", Assert.Single(session.GetTemporaryTableSchemas()).Name);
    }

    /// <summary>复制行数预算在发布前实际拒绝，原会话对象保持可读。</summary>
    [Fact]
    public void TemporaryWorkspace_CloneLimit_RejectsWithoutReplacingVisibleState()
    {
        using var database = Open();
        using var session = new SqlSessionContext(database, new SqlSessionOptions { MaximumCloneRows = 1 });
        using (session.EnterScope())
        using (var mutation = session.BeginTemporaryMutation(null))
        {
            mutation.Tables.Create(Schema());
            mutation.Tables.Open("Stage").InsertMany([new object?[] { 1L, 10L }, new object?[] { 2L, 20L }]);
            mutation.Complete();
        }
        using (session.EnterScope())
        {
            Assert.Throws<InvalidOperationException>(() => session.BeginTemporaryMutation(null));
            Assert.Equal(2, session.VisibleTables!.Open("Stage").RowCount);
        }
    }

    /// <summary>会话拒绝另一个数据库实例及另一个连接的活动事务。</summary>
    [Fact]
    public void Session_DatabaseAndTransactionOwnership_RejectCrossConnectionUse()
    {
        using var firstDatabase = Open();
        using var otherDatabase = Tsdb.Open(new TsdbOptions { RootDirectory = _root + "-other" });
        using var first = new SqlSessionContext(firstDatabase);
        using var second = new SqlSessionContext(firstDatabase);
        Assert.Throws<InvalidOperationException>(() => first.ValidateDatabase(otherDatabase));
        var transaction = new SqlTransactionContext();
        transaction.AttachSession(first);
        Assert.Throws<InvalidOperationException>(() => transaction.AttachSession(second));
        first.Dispose();
        Assert.True(transaction.IsCompleted);
        Assert.False(transaction.WasCommitted);
    }

    /// <summary>关闭会话按归属标记拒绝删除被替换的目录。</summary>
    [Fact]
    public void Dispose_OwnerMarkerChanged_RefusesDeletionAndCanRetryWithCorrectMarker()
    {
        using var database = Open();
        var session = new SqlSessionContext(database);
        Create(session, 10);
        string marker = Path.Combine(session.ScratchDirectory, "session.owner");
        string owner = File.ReadAllText(marker);
        File.WriteAllText(marker, "foreign");
        try
        {
            Assert.Throws<IOException>(() => session.Dispose());
            Assert.True(Directory.Exists(session.ScratchDirectory));
        }
        finally
        {
            File.WriteAllText(marker, owner);
            session.Dispose();
        }
        Assert.False(Directory.Exists(session.ScratchDirectory));
    }

    /// <summary>另一异步调用在有界 gate 等待时可取消，持有者跨 await 后仍恢复同一个会话。</summary>
    [Fact]
    public async Task SessionGate_AcrossAwaitAndCancelledContender_RestoresAmbientAndAdmission()
    {
        using var database = Open();
        using var session = new SqlSessionContext(database);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task owner = Task.Run(async () =>
        {
            using IDisposable scope = session.EnterScope();
            entered.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(session, SqlSessionContext.Current);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Task.Run(() => Assert.Throws<OperationCanceledException>(() => session.EnterScope(cancel.Token)))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(SqlSessionContext.Current);
        }
        finally
        {
            release.TrySetResult();
            await owner.WaitAsync(TimeSpan.FromSeconds(5));
        }
        using (session.EnterScope()) Assert.Same(session, SqlSessionContext.Current);
        Assert.Null(SqlSessionContext.Current);
    }

    /// <summary>物理 scratch 配额真实到达 rowstore 创建之后拒绝发布，并回收失败候选。</summary>
    [Fact]
    public void TemporaryWorkspace_PhysicalScratchLimit_LeavesNoVisibleCandidate()
    {
        using var database = Open();
        using var session = new SqlSessionContext(database, new SqlSessionOptions { MaximumScratchBytes = 64 });
        Assert.Throws<InvalidOperationException>(() => Create(session, 10));
        Assert.Empty(session.GetTemporaryTableSchemas());
        Assert.Empty(Directory.EnumerateDirectories(session.ScratchDirectory));
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static TableSchema Schema() => TableSchema.Create("Stage",
        [("Id", TableColumnType.Int64, false), ("Value", TableColumnType.Int64, true)], ["Id"]);

    private static void Create(SqlSessionContext session, long value)
    {
        using IDisposable scope = session.EnterScope();
        using var mutation = session.BeginTemporaryMutation(null);
        mutation.Tables.Create(Schema());
        mutation.Tables.Open("Stage").Insert([1L, value]);
        mutation.Complete();
    }

    private static long Read(SqlSessionContext session)
    {
        using IDisposable scope = session.EnterScope();
        return Assert.IsType<long>(Assert.Single(session.VisibleTables!.Open("Stage").Scan()).Values[1]);
    }

    /// <summary>释放本测试创建的精确数据库目录。</summary>
    public void Dispose()
    {
        foreach (string path in new[] { _root, _root + "-other" })
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
    }
}
