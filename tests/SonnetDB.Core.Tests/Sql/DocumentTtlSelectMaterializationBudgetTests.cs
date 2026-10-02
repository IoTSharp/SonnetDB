using System.Globalization;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 TTL 文档 SQL 的逐候选可见性、累计物化预算及回收兼容边界。</summary>
public sealed class DocumentTtlSelectMaterializationBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sdb-docttl-{Guid.NewGuid():N}");

    /// <summary>时间数字、数字字符串和日期字符串沿用 TTL 语义，无效或缺失时间不隐藏文档。</summary>
    [Fact]
    public void Execute_TtlValueFormats_PreservesVisibilityWithoutDeletingDocuments()
    {
        long old = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        long future = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
        string oldDate = DateTimeOffset.FromUnixTimeMilliseconds(old).ToString("O", CultureInfo.InvariantCulture);
        using Tsdb db = OpenDocuments([
            new("a", $$"""{"expiresAt":{{old}}}"""),
            new("b", $$"""{"expiresAt":"{{old}}"}"""),
            new("c", $$"""{"expiresAt":"{{oldDate}}"}"""),
            new("d", "{}"),
            new("e", """{"expiresAt":null}"""),
            new("f", """{"expiresAt":"invalid"}"""),
            new("g", """{"expiresAt":true}"""),
            new("h", """{"expiresAt":1.5}"""),
            new("i", $$"""{"expiresAt":{{future}}}"""),
        ]);
        DocumentCollectionStore store = db.Documents.Open("docs");
        long version = store.LastVersion;
        long sequence = store.LatestChangeSequence;
        var metrics = new SqlExecutionMetrics();

        SelectExecutionResult result = Select(db, "SELECT id FROM docs",
            new SqlExecutionOptions { MaxMaterializedRows = 6, Metrics = metrics });

        Assert.Equal(["d", "e", "f", "g", "h", "i"], result.Rows.Select(static row => row[0]));
        Assert.False(result.Truncated);
        Assert.Equal(9, metrics.Complete().CandidateRows);
        Assert.Equal(9, metrics.Complete().LogicalReads);
        Assert.Equal(6, metrics.Complete().ExaminedRows);
        AssertUnchanged(store, version, sequence);
        AssertReleased(db);
        Assert.Equal(result.Rows, Select(db, "SELECT id FROM docs").Rows);
        Assert.Equal(sequence + 3, store.LatestChangeSequence);
    }

    /// <summary>TTL 截止时间等号隐藏文档，查询固定时刻使边界不依赖测试运行耗时。</summary>
    [Theory]
    [InlineData(-1001, true)]
    [InlineData(-1000, true)]
    [InlineData(-999, false)]
    public void ReadForSqlMaterialization_ExpiryBoundary_UsesQueryVisibilityInstant(int offsetMs, bool expectedExpired)
    {
        long visibleAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using Tsdb db = OpenDocuments([new("a", $$"""{"expiresAt":{{visibleAt + offsetMs}}}""")]);
        DocumentCollectionStore store = db.Documents.Open("docs");
        long version = store.LastVersion;
        long sequence = store.LatestChangeSequence;

        IReadOnlyList<DocumentRow> rows = store.ReadForSqlMaterialization(
            afterId: null, visibleAt, out bool expired, id: "a");

        Assert.Single(rows);
        Assert.Equal(expectedExpired, expired);
        AssertUnchanged(store, version, sequence);
    }

    /// <summary>极值时间与正 TTL 不因相加溢出误判；时间零的等号仍视为过期。</summary>
    [Theory]
    [InlineData(long.MaxValue, false)]
    [InlineData(long.MinValue, true)]
    [InlineData(0L, true)]
    [InlineData(1L, false)]
    public void ReadForSqlMaterialization_ExtremeTimestamps_DoesNotOverflowExpiry(long timestamp, bool expectedExpired)
    {
        using Tsdb db = OpenDocuments([new("a", $$"""{"expiresAt":{{timestamp}}}""")]);
        DocumentCollectionStore store = db.Documents.Open("docs");
        long version = store.LastVersion;
        long sequence = store.LatestChangeSequence;

        Assert.Single(store.ReadForSqlMaterialization(
            afterId: null, visibleAtUnixMs: 1000, out bool expired, id: "a"));

        Assert.Equal(expectedExpired, expired);
        AssertUnchanged(store, version, sequence);
        if (timestamp == long.MaxValue)
        {
            Assert.NotNull(store.Get("a"));
            AssertUnchanged(store, version, sequence);
        }
    }

    /// <summary>任一 TTL 索引过期即可隐藏，普通 path 索引不参与过期判断。</summary>
    [Fact]
    public void Execute_MultipleTtlIndexes_HidesRowsExpiredByEitherIndex()
    {
        long old = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        long future = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
        using Tsdb db = OpenDocuments([
            new("a", $$"""{"expiresAt":{{old}},"otherExpiry":{{future}},"n":1}"""),
            new("b", $$"""{"expiresAt":{{future}},"otherExpiry":{{old}},"n":2}"""),
            new("c", $$"""{"expiresAt":{{future}},"otherExpiry":{{future}},"n":3}"""),
            new("d", """{"n":4}"""),
        ], secondTtl: true);
        DocumentCollectionStore store = db.Documents.Open("docs");
        long version = store.LastVersion;
        long sequence = store.LatestChangeSequence;

        SelectExecutionResult result = Select(db, "SELECT id FROM docs",
            new SqlExecutionOptions { MaxMaterializedRows = 2 });

        Assert.Equal(["c", "d"], result.Rows.Select(static row => row[0]));
        AssertUnchanged(store, version, sequence);
        AssertReleased(db);
    }

    /// <summary>过期前缀逐行推进，超界只读取失败前沿，并保留全部持久数据。</summary>
    [Fact]
    public void Execute_ExpiredPrefixThenManyVisibleRows_StopsAtBudgetFailureFrontier()
    {
        DocumentWriteRequest[] requests = Enumerable.Range(0, 4096)
            .Select(static number => Request("d" + number.ToString("D4", CultureInfo.InvariantCulture), number < 1000, number))
            .ToArray();
        using Tsdb db = OpenDocuments(requests);
        DocumentCollectionStore store = db.Documents.Open("docs");
        long version = store.LastVersion;
        long sequence = store.LatestChangeSequence;
        long scans = store.FullScanCount;
        var metrics = new SqlExecutionMetrics();

        Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT id, document FROM docs",
            new SqlExecutionOptions { MaxMaterializedRows = 2, Metrics = metrics }));

        Assert.Equal(1003, metrics.Complete().CandidateRows);
        Assert.Equal(3, metrics.Complete().ExaminedRows);
        Assert.Equal(scans, store.FullScanCount);
        AssertUnchanged(store, version, sequence);
        AssertReleased(db);
    }

    /// <summary>TTL 隐藏不占结果预算，UTF-16 字节等号仍允许而少一字节拒绝。</summary>
    [Theory]
    [InlineData(136, true)]
    [InlineData(135, false)]
    public void Execute_UnicodeProjection_ChargesOnlyVisibleRetainedRow(long bytes, bool succeeds)
    {
        long old = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        using Tsdb db = OpenDocuments([
            new("a", $$"""{"expiresAt":{{old}},"text":"ignored"}"""),
            new("b", """{"text":"雪😀"}"""),
        ]);
        DocumentCollectionStore store = db.Documents.Open("docs");
        long version = store.LastVersion;
        long sequence = store.LatestChangeSequence;
        var options = new SqlExecutionOptions { MaxMaterializedRows = 1, MaxMaterializedBytes = bytes };
        const string sql = "SELECT id, json_value(document, '$.text') AS text FROM docs";

        if (succeeds)
        {
            SelectExecutionResult result = Select(db, sql, options);
            Assert.Equal("雪😀", Assert.Single(result.Rows)[1]);
            Assert.False(result.Truncated);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => Select(db, sql, options));
        }
        AssertUnchanged(store, version, sequence);
        AssertReleased(db);
    }

    /// <summary>WHERE、OFFSET 和 LIMIT 在 TTL 可见文档上执行，页后候选不读取。</summary>
    [Fact]
    public void Execute_TtlFilterOffsetLimit_ReturnsCompleteVisiblePage()
    {
        using Tsdb db = OpenDocuments([
            Request("a", expired: true), Request("b", expired: false, 1),
            Request("c", expired: false, 2), Request("d", expired: false, 3),
            Request("e", expired: false, 4), Request("f", expired: true, 5),
        ]);
        DocumentCollectionStore store = db.Documents.Open("docs");
        long version = store.LastVersion;
        long sequence = store.LatestChangeSequence;
        var metrics = new SqlExecutionMetrics();
        const string sql = "SELECT json_value(document, '$.n') AS n FROM docs WHERE json_value(document, '$.n') >= 2 LIMIT 2 OFFSET 1";

        SelectExecutionResult result = Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 2, MaxMaterializedBytes = 192, Metrics = metrics });

        Assert.Equal([3d, 4d], result.Rows.Select(static row => Assert.IsType<double>(row[0])));
        Assert.False(result.Truncated);
        Assert.Equal(5, metrics.Complete().CandidateRows);
        Assert.Equal(4, metrics.Complete().ExaminedRows);
        AssertUnchanged(store, version, sequence);
        AssertReleased(db);
        Assert.Equal(result.Rows, Select(db, sql).Rows);
    }

    /// <summary>按 ID 读取只检查目标文档，过期目标在求值 WHERE 前隐藏。</summary>
    [Theory]
    [InlineData("a", 0, 1, 0)]
    [InlineData("b", 1, 1, 1)]
    [InlineData("missing", 0, 0, 0)]
    public void Execute_IdPredicate_ChecksOnlyTargetTtl(string id, int expectedRows, int candidates, int examined)
    {
        using Tsdb db = OpenDocuments([Request("a", expired: true), Request("b", expired: false)]);
        DocumentCollectionStore store = db.Documents.Open("docs");
        long version = store.LastVersion;
        long sequence = store.LatestChangeSequence;
        var metrics = new SqlExecutionMetrics();

        SelectExecutionResult result = Select(db, $"SELECT id FROM docs WHERE id = '{id}'",
            new SqlExecutionOptions { MaxMaterializedRows = 1, Metrics = metrics });

        Assert.Equal(expectedRows, result.Rows.Count);
        Assert.Equal(candidates, metrics.Complete().CandidateRows);
        Assert.Equal(examined, metrics.Complete().ExaminedRows);
        AssertUnchanged(store, version, sequence);
        AssertReleased(db);
    }

    /// <summary>扫描到全部过期的尾部会正常结束，空结果不需要一行物化配额。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_ExpiredTail_CompletesWithoutChargingHiddenRows(bool hasVisibleRow)
    {
        using Tsdb db = OpenDocuments([
            Request("a", expired: !hasVisibleRow), Request("b", expired: true), Request("c", expired: true),
        ]);
        var metrics = new SqlExecutionMetrics();

        SelectExecutionResult result = Select(db, "SELECT id FROM docs",
            new SqlExecutionOptions { MaxMaterializedRows = 1, MaxMaterializedBytes = hasVisibleRow ? 98 : 1, Metrics = metrics });

        Assert.Equal(hasVisibleRow ? 1 : 0, result.Rows.Count);
        Assert.Equal(3, metrics.Complete().CandidateRows);
        Assert.Equal(hasVisibleRow ? 1 : 0, metrics.Complete().ExaminedRows);
        Assert.False(result.Truncated);
        AssertReleased(db);
    }

    /// <summary>LIMIT 0 不读取 TTL 候选，也不执行过期回收。</summary>
    [Fact]
    public void Execute_ZeroLimit_DoesNotReadOrPurgeTtlDocuments()
    {
        using Tsdb db = OpenDocuments([Request("a", expired: true)]);
        DocumentCollectionStore store = db.Documents.Open("docs");
        long version = store.LastVersion;
        long sequence = store.LatestChangeSequence;
        var metrics = new SqlExecutionMetrics();

        SelectExecutionResult result = Select(db, "SELECT * FROM docs LIMIT 0",
            new SqlExecutionOptions { MaxMaterializedBytes = 1, Metrics = metrics });

        Assert.Empty(result.Rows);
        Assert.Equal(0, metrics.Complete().CandidateRows);
        AssertUnchanged(store, version, sequence);
        AssertReleased(db);
    }

    /// <summary>过期行及 SQL 页后的错误投影不求值，完整可见结果的错误仍传播。</summary>
    [Fact]
    public void Execute_ExpiredAndLaterInvalidProjection_EvaluatesOnlyVisibleSqlPage()
    {
        long old = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        using Tsdb db = OpenDocuments([
            new("a", $$"""{"expiresAt":{{old}},"value":"invalid"}"""),
            new("b", """{"value":"7"}"""), new("c", """{"value":"invalid"}"""),
        ]);
        DocumentCollectionStore store = db.Documents.Open("docs");
        long version = store.LastVersion;
        long sequence = store.LatestChangeSequence;
        const string projection = "SELECT CAST(json_value(document, '$.value') AS INT) FROM docs";

        Assert.Equal(7L, Assert.Single(Select(db, projection + " LIMIT 1",
            new SqlExecutionOptions { MaxMaterializedRows = 1 }).Rows)[0]);
        Assert.Throws<InvalidOperationException>(() => Select(db, projection,
            new SqlExecutionOptions { MaxMaterializedRows = 10 }));

        AssertUnchanged(store, version, sequence);
        AssertReleased(db);
    }

    /// <summary>取消或过期截止时间不回收文档，失败根调用释放预算。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_CancelledOrExpiredDeadline_DoesNotPurgeTtlDocuments(bool expiredDeadline)
    {
        using Tsdb db = OpenDocuments([Request("a", expired: true), Request("b", expired: false)]);
        DocumentCollectionStore store = db.Documents.Open("docs");
        long version = store.LastVersion;
        long sequence = store.LatestChangeSequence;
        using var cancellation = new CancellationTokenSource();
        if (!expiredDeadline)
            cancellation.Cancel();

        RoutineExecutionException error = Assert.Throws<RoutineExecutionException>(() => Select(db, "SELECT document FROM docs",
            new SqlExecutionOptions
            {
                MaxMaterializedRows = 1,
                CancellationToken = cancellation.Token,
                DeadlineUtc = expiredDeadline ? DateTimeOffset.UtcNow.AddSeconds(-1) : null,
            }));

        Assert.Equal("routine_cancelled", error.Code);
        AssertUnchanged(store, version, sequence);
        AssertReleased(db);
        Assert.Single(Select(db, "SELECT id FROM docs", new SqlExecutionOptions { MaxMaterializedRows = 1 }).Rows);
        AssertUnchanged(store, version, sequence);
    }

    /// <summary>TTL 排序、聚合和嵌套计划仍在候选读取及回收前拒绝。</summary>
    [Theory]
    [InlineData("SELECT id FROM docs ORDER BY id LIMIT 1")]
    [InlineData("SELECT COUNT(*) FROM docs")]
    [InlineData("SELECT DISTINCT id FROM docs")]
    [InlineData("SELECT id FROM (SELECT id FROM docs) AS source")]
    [InlineData("SELECT id FROM docs WHERE EXISTS (SELECT 1)")]
    public void Execute_UnsupportedTtlPlan_RejectsBeforeReadingOrPurge(string sql)
    {
        using Tsdb db = OpenDocuments([Request("a", expired: true), Request("b", expired: false)]);
        DocumentCollectionStore store = db.Documents.Open("docs");
        long version = store.LastVersion;
        long sequence = store.LatestChangeSequence;
        var metrics = new SqlExecutionMetrics();

        Assert.Throws<NotSupportedException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 10, Metrics = metrics }));

        Assert.Equal(0, metrics.Complete().CandidateRows);
        AssertUnchanged(store, version, sequence);
        AssertReleased(db);
    }

    /// <summary>预检后新增 TTL 索引仍通过锁内最新 schema 隐藏过期文档。</summary>
    [Fact]
    public void Execute_StaleSchemaAfterTtlAddition_UsesCurrentTtlVisibility()
    {
        using Tsdb db = OpenDocuments([Request("a", expired: true), Request("b", expired: false)], ttl: false);
        DocumentCollectionSchema schema = db.Documents.Catalog.TryGet("docs")!;
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse("SELECT id FROM docs"));
        DocumentSqlExecutor.ValidateMaterializationSupported(schema, statement);
        SqlExecutor.Execute(db, "CREATE TTL INDEX idx_expiry ON docs ('$.expiresAt') WITH ttl_seconds = 1");
        DocumentCollectionStore store = db.Documents.Open("docs");
        long version = store.LastVersion;
        long sequence = store.LatestChangeSequence;

        using (SqlRowRetentionBudget.EnterExecution(new SqlExecutionOptions { MaxMaterializedRows = 1 }))
        {
            SelectExecutionResult result = DocumentSqlExecutor.ExecuteSelect(db, statement, schema);
            Assert.Equal("b", Assert.Single(result.Rows)[0]);
        }
        AssertUnchanged(store, version, sequence);
        AssertReleased(db);
    }

    /// <summary>实际可见输出与 EXPLAIN ANALYZE 解释输出共享根累计物化预算。</summary>
    [Fact]
    public void Execute_ExplainAnalyze_SharesTtlOutputBudgetWithExplainRows()
    {
        using Tsdb db = OpenDocuments([
            Request("a", expired: true), Request("b", expired: false), Request("c", expired: false),
        ]);
        DocumentCollectionStore store = db.Documents.Open("docs");
        long version = store.LastVersion;
        long sequence = store.LatestChangeSequence;
        const string sql = "EXPLAIN ANALYZE SELECT id FROM docs";
        int explainRows = Select(db, sql, new SqlExecutionOptions { MaxMaterializedRows = 1000 }).Rows.Count;

        Assert.Throws<InvalidOperationException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 2 + explainRows - 1 }));
        Assert.Equal(explainRows, Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 2 + explainRows }).Rows.Count);
        AssertUnchanged(store, version, sequence);
        AssertReleased(db);
    }

    /// <summary>预算 EXPLAIN 不为候选估算扫描或删除过期文档，并明确基数未知。</summary>
    [Fact]
    public void Execute_ExplainTtlSelect_DoesNotScanForCardinality()
    {
        using Tsdb db = OpenDocuments([Request("a", expired: true), Request("b", expired: false)]);
        DocumentCollectionStore store = db.Documents.Open("docs");
        long version = store.LastVersion;
        long sequence = store.LatestChangeSequence;
        var metrics = new SqlExecutionMetrics();

        SelectExecutionResult result = Select(db, "EXPLAIN SELECT id FROM docs",
            new SqlExecutionOptions { MaxMaterializedRows = 1000, Metrics = metrics });

        Assert.Contains(result.Rows, static row => row.Contains("materialization_budget_cardinality_unknown"));
        Assert.Equal(0, metrics.Complete().CandidateRows);
        AssertUnchanged(store, version, sequence);
        AssertReleased(db);
    }

    /// <summary>持久重开后的预算失败不触发构造器 TTL 回收，普通读取仍生成既有 TTL 删除事件。</summary>
    [Fact]
    public void Execute_ReopenedTtlStoreBudgetFailure_SkipsOpenPurgeAndPreservesDefaultGet()
    {
        long version;
        long sequence;
        using (Tsdb db = OpenDocuments([Request("a", expired: true), Request("b", expired: false)]))
        {
            DocumentCollectionStore store = db.Documents.Open("docs");
            version = store.LastVersion;
            sequence = store.LatestChangeSequence;
        }
        using Tsdb reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });

        Assert.Throws<InvalidOperationException>(() => Select(reopened, "SELECT document FROM docs",
            new SqlExecutionOptions { MaxMaterializedBytes = 1 }));

        DocumentCollectionStore cached = reopened.Documents.Open("docs");
        AssertUnchanged(cached, version, sequence);
        AssertReleased(reopened);
        Assert.NotNull(cached.Get("b"));
        DocumentChangeFeedEntry change = Assert.Single(cached.ReadChangeFeed(sequence).Changes);
        Assert.Equal("a", change.DocumentId);
        Assert.Equal(DocumentChangeCauses.Ttl, change.Cause);
        Assert.Equal("delete", change.Operation);
        Assert.Null(cached.Get("a"));
    }

    /// <summary>持久重开后的成功预算查询保持完整结果，默认排序和聚合继续回收且不重复删除。</summary>
    [Fact]
    public void Execute_ReopenedTtlStoreSuccess_PreservesDefaultSortedAndAggregateResults()
    {
        long sequence;
        using (Tsdb db = OpenDocuments([
            Request("a", expired: true), Request("b", expired: false), Request("c", expired: false),
        ]))
        {
            sequence = db.Documents.Open("docs").LatestChangeSequence;
        }
        using Tsdb reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });

        SelectExecutionResult budgeted = Select(reopened, "SELECT id FROM docs",
            new SqlExecutionOptions { MaxMaterializedRows = 2 });

        Assert.Equal(["b", "c"], budgeted.Rows.Select(static row => row[0]));
        Assert.False(budgeted.Truncated);
        DocumentCollectionStore store = reopened.Documents.Open("docs");
        Assert.Equal(sequence, store.LatestChangeSequence);
        Assert.Equal(["c", "b"], Select(reopened, "SELECT id FROM docs ORDER BY id DESC").Rows.Select(static row => row[0]));
        Assert.Equal(2L, Assert.Single(Select(reopened, "SELECT COUNT(*) FROM docs").Rows)[0]);
        Assert.Equal(sequence + 1, store.LatestChangeSequence);
        AssertReleased(reopened);
    }

    /// <summary>仅清理本测试创建的独占临时数据库目录。</summary>
    public void Dispose()
    {
        string target = Path.GetFullPath(_root);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(target));
        Assert.StartsWith("sdb-docttl-", Path.GetFileName(target), StringComparison.Ordinal);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }

    private Tsdb OpenDocuments(IReadOnlyList<DocumentWriteRequest> requests, bool ttl = true, bool secondTtl = false)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requests.Count, 4096);
        Tsdb db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        try
        {
            SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION docs");
            SqlExecutor.Execute(db, "CREATE JSON INDEX idx_n ON docs ('$.n')");
            if (ttl)
                SqlExecutor.Execute(db, "CREATE TTL INDEX idx_expiry ON docs ('$.expiresAt') WITH ttl_seconds = 1");
            if (secondTtl)
                SqlExecutor.Execute(db, "CREATE TTL INDEX idx_other_expiry ON docs ('$.otherExpiry') WITH ttl_seconds = 1");
            Assert.False(db.Documents.Open("docs").InsertMany(requests).HasErrors);
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static DocumentWriteRequest Request(string id, bool expired, int number = 0)
    {
        long timestamp = DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1).ToUnixTimeMilliseconds();
        return new(id, $$"""{"expiresAt":{{timestamp}},"n":{{number}}}""");
    }

    private static SelectExecutionResult Select(Tsdb db, string sql, SqlExecutionOptions options)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, sql, null, null, options));

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));

    private static void AssertUnchanged(DocumentCollectionStore store, long version, long sequence)
    {
        Assert.Equal(version, store.LastVersion);
        Assert.Equal(sequence, store.LatestChangeSequence);
        Assert.Empty(store.ReadChangeFeed(sequence).Changes);
    }

    private static void AssertReleased(Tsdb db)
    {
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        Assert.False(SqlRowRetentionBudget.HasExecutionBudget);
    }
}
