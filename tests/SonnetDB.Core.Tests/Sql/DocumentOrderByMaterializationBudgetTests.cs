using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 Document ORDER BY 阻塞排序的完整物化预算语义。</summary>
public sealed class DocumentOrderByMaterializationBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sdb-docsort-{Guid.NewGuid():N}");

    /// <summary>LIMIT 1 也必须先保留全部排序输入，行数预算不能只约束最终页。</summary>
    [Fact]
    public void Execute_OrderByLimitOne_ChargesAllSortInputs()
    {
        using Tsdb db = OpenDocuments([
            new("a", "{\"n\":3}"),
            new("b", "{\"n\":1}"),
            new("c", "{\"n\":2}"),
        ]);

        Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT id FROM docs ORDER BY id DESC LIMIT 1",
            new SqlExecutionOptions { MaxMaterializedRows = 2 }));

        SelectExecutionResult result = Select(db,
            "SELECT id FROM docs ORDER BY id DESC LIMIT 1",
            new SqlExecutionOptions { MaxMaterializedRows = 3 });
        Assert.Equal(["c"], result.Rows.Select(static row => row[0]));
        AssertReleased(db);
    }

    /// <summary>排序完成后再应用 OFFSET/LIMIT，并按结果列值稳定返回。</summary>
    [Fact]
    public void Execute_OrderByOffsetLimit_ReturnsSortedPage()
    {
        using Tsdb db = OpenDocuments([
            new("a", "{\"n\":3}"),
            new("b", "{\"n\":1}"),
            new("c", "{\"n\":2}"),
        ]);

        SelectExecutionResult result = Select(db,
            "SELECT id FROM docs ORDER BY id ASC LIMIT 1 OFFSET 1",
            new SqlExecutionOptions { MaxMaterializedRows = 3 });

        Assert.Equal(["b"], result.Rows.Select(static row => row[0]));
        AssertReleased(db);
    }

    /// <summary>排序输入逐行累计字节，等号预算成功而少一字节拒绝。</summary>
    [Theory]
    [InlineData(294, true)]
    [InlineData(293, false)]
    public void Execute_OrderBy_ChargesCumulativeBytes(long bytes, bool succeeds)
    {
        using Tsdb db = OpenDocuments([
            new("a", "{}"),
            new("b", "{}"),
            new("c", "{}"),
        ]);
        var options = new SqlExecutionOptions { MaxMaterializedRows = 3, MaxMaterializedBytes = bytes };

        if (succeeds)
        {
            SelectExecutionResult result = Select(db, "SELECT id FROM docs ORDER BY id", options);
            Assert.Equal(["a", "b", "c"], result.Rows.Select(static row => row[0]));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT id FROM docs ORDER BY id", options));
        }

        AssertReleased(db);
    }

    /// <summary>重开后的 TTL 排序在固定可见时刻隐藏过期输入，并继续按完整可见集合排序。</summary>
    [Fact]
    public void Execute_ReopenedTtlOrderBy_SortsVisibleRowsOnly()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using (Tsdb db = OpenDocuments([
            new("expired", $"{{\"expiresAt\":{now - 86_400_000},\"n\":9}}"),
            new("b", $"{{\"expiresAt\":{now + 86_400_000},\"n\":1}}"),
            new("c", $"{{\"expiresAt\":{now + 86_400_000},\"n\":2}}"),
        ], ttl: true))
        {
            _ = db.Documents.Open("docs").LatestChangeSequence;
        }

        using Tsdb reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SelectExecutionResult result = Select(reopened,
            "SELECT id FROM docs ORDER BY id LIMIT 1",
            new SqlExecutionOptions { MaxMaterializedRows = 2 });

        Assert.Equal(["b"], result.Rows.Select(static row => row[0]));
        AssertReleased(reopened);
    }

    /// <summary>排序预算查询在根调用已取消时立即返回稳定取消错误并释放预算。</summary>
    [Fact]
    public void Execute_OrderBy_PreCancelled_ReturnsRoutineCancelled()
    {
        using Tsdb db = OpenDocuments([new("a", "{}"), new("b", "{}")]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        RoutineExecutionException error = Assert.Throws<RoutineExecutionException>(() => Select(db,
            "SELECT id FROM docs ORDER BY id LIMIT 1",
            new SqlExecutionOptions { MaxMaterializedRows = 2, CancellationToken = cancellation.Token }));

        Assert.Equal("routine_cancelled", error.Code);
        AssertReleased(db);
    }

    /// <summary>EXPLAIN ANALYZE 通过真实排序预算执行，并保留排序访问路径证据。</summary>
    [Fact]
    public void Execute_ExplainAnalyzeOrderBy_ReportsBudgetedScan()
    {
        using Tsdb db = OpenDocuments([new("a", "{}"), new("b", "{}"), new("c", "{}")]);

        SelectExecutionResult result = Select(db,
            "EXPLAIN ANALYZE SELECT id FROM docs ORDER BY id LIMIT 1",
            new SqlExecutionOptions { MaxMaterializedRows = 100 });

        Assert.Contains(result.Rows, static row => row.Any(value =>
            value is string text && text.Contains("document_budgeted_scan", StringComparison.Ordinal)));
        AssertReleased(db);
    }

    /// <summary>打开本测试创建的 Document 集合并写入固定顺序文档。</summary>
    private Tsdb OpenDocuments(IReadOnlyList<DocumentWriteRequest> requests, bool ttl = false)
    {
        Tsdb db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        try
        {
            SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION docs");
            if (ttl)
                SqlExecutor.Execute(db, "CREATE TTL INDEX idx_expiry ON docs ('$.expiresAt') WITH ttl_seconds = 1");
            Assert.False(db.Documents.Open("docs").InsertMany(requests).HasErrors);
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static SelectExecutionResult Select(Tsdb db, string sql, SqlExecutionOptions options)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, sql, null, null, options));

    private static void AssertReleased(Tsdb db)
    {
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        Assert.False(SqlRowRetentionBudget.HasExecutionBudget);
    }

    /// <summary>仅清理本测试创建的独占临时目录。</summary>
    public void Dispose()
    {
        string target = Path.GetFullPath(_root);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(target));
        Assert.StartsWith("sdb-docsort-", Path.GetFileName(target), StringComparison.Ordinal);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }
}
