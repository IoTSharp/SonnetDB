using System.Globalization;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证直接 Document SQL 查询的累计物化预算、惰性读取及兼容边界。</summary>
public sealed class DocumentSelectMaterializationBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sdb-docb-{Guid.NewGuid():N}");

    /// <summary>大量文档遇到小行数预算时只读取失败前沿，不全量保留候选文档。</summary>
    [Fact]
    public void Execute_ManyDocumentsOverRowBudget_StopsAtFailureFrontier()
    {
        using Tsdb db = OpenDocuments(4096);
        var metrics = new SqlExecutionMetrics();
        DocumentCollectionStore store = db.Documents.Open("docs");
        long scans = store.FullScanCount;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT id, document FROM docs", new SqlExecutionOptions { MaxMaterializedRows = 2, Metrics = metrics }));

        Assert.Contains("累计物化", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, metrics.Complete().CandidateRows);
        Assert.Equal(scans, store.FullScanCount);
        AssertReleased(db);
        Assert.Equal(4096, Select(db, "SELECT id FROM docs").Rows.Count);
    }

    /// <summary>只有过滤和 OFFSET 后保留的行计费，成功结果保持完整 SQL 页。</summary>
    [Theory]
    [InlineData("SELECT json_value(document, '$.n') AS n FROM docs WHERE json_value(document, '$.n') >= 4 LIMIT 2 OFFSET 3", 7d, 8d)]
    [InlineData("SELECT json_value(document, '$.n') AS n FROM docs WHERE json_value(document, '$.n') >= 4 OFFSET 3 ROWS FETCH NEXT 2 ROWS ONLY", 7d, 8d)]
    [InlineData("SELECT json_value(document, '$.n') AS n FROM docs OFFSET 8", 8d, 9d)]
    public void Execute_FilterAndPagination_CountsOnlyCompletePage(string sql, double first, double second)
    {
        using Tsdb db = OpenDocuments(10);

        SelectExecutionResult result = Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 2, MaxMaterializedBytes = 192 });

        Assert.Equal([first, second], result.Rows.Select(static row => Assert.IsType<double>(row[0])));
        AssertEquivalent(Select(db, sql), result);
        AssertReleased(db);
    }

    /// <summary>UTF-16 字符串载荷使用既有字节估算，预算等号允许而少一字节拒绝。</summary>
    [Theory]
    [InlineData(144, true)]
    [InlineData(143, false)]
    public void Execute_UnicodeProjection_EnforcesExactEstimatedBytes(long bytes, bool succeeds)
    {
        using Tsdb db = OpenDocuments(0);
        db.Documents.Open("docs").Insert("d0000", "{\"text\":\"雪😀\"}");
        var options = new SqlExecutionOptions { MaxMaterializedRows = 1, MaxMaterializedBytes = bytes };

        if (succeeds)
        {
            SelectExecutionResult result = Select(db,
                "SELECT id, json_value(document, '$.text') AS text FROM docs", options);
            Assert.Equal("雪😀", Assert.Single(result.Rows)[1]);
            Assert.False(result.Truncated);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => Select(db,
                "SELECT id, json_value(document, '$.text') AS text FROM docs", options));
        }
        AssertReleased(db);
    }

    /// <summary>文档全文 JSON 也计入保留估算，单行超界不能返回成功前缀。</summary>
    [Fact]
    public void Execute_FullDocumentProjection_AccountsForJsonPayload()
    {
        using Tsdb db = OpenDocuments(0);
        DocumentCollectionStore store = db.Documents.Open("docs");
        store.Insert("a", "{\"text\":\"" + new string('x', 1024) + "\"}");
        string json = store.Get("a")!.Json;
        long bytes = 64 + 8 + 24 + (json.Length * 2L);

        Assert.Equal(json, Assert.Single(Select(db, "SELECT document FROM docs",
            new SqlExecutionOptions { MaxMaterializedBytes = bytes }).Rows)[0]);
        Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT document FROM docs",
            new SqlExecutionOptions { MaxMaterializedBytes = bytes - 1 }));
        AssertReleased(db);
    }

    /// <summary>LIMIT 0 不读取候选文档，预算小于一行仍返回完整空页。</summary>
    [Fact]
    public void Execute_ZeroLimit_DoesNotReadDocuments()
    {
        using Tsdb db = OpenDocuments(20);
        var metrics = new SqlExecutionMetrics();

        SelectExecutionResult result = Select(db, "SELECT * FROM docs LIMIT 0",
            new SqlExecutionOptions { MaxMaterializedBytes = 1, Metrics = metrics });

        Assert.Empty(result.Rows);
        Assert.False(result.Truncated);
        Assert.Equal(0, metrics.Complete().CandidateRows);
        AssertReleased(db);
    }

    /// <summary>按 ID 读取只消费一个文档，完整 WHERE 仍在投影前求值。</summary>
    [Theory]
    [InlineData("SELECT id FROM docs WHERE id = 'd0017'", 1)]
    [InlineData("SELECT id FROM docs WHERE id = 'missing'", 0)]
    [InlineData("SELECT id FROM docs WHERE id = 'd0017' AND json_value(document, '$.n') = 0", 0)]
    public void Execute_IdPredicate_ReadsSingleCandidate(string sql, int rows)
    {
        using Tsdb db = OpenDocuments(20);
        var metrics = new SqlExecutionMetrics();

        SelectExecutionResult result = Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 1, Metrics = metrics });

        Assert.Equal(rows, result.Rows.Count);
        Assert.InRange(metrics.Complete().CandidateRows, rows, 1);
        AssertEquivalent(Select(db, sql), result);
        AssertReleased(db);
    }

    /// <summary>稀疏、大小写敏感 JSON 属性、对象/数组投影以及标量表达式沿用原语义。</summary>
    [Fact]
    public void Execute_SparseJsonAndScalarExpressions_PreservesProjectionSemantics()
    {
        using Tsdb db = OpenDocuments(0);
        db.Documents.Open("docs").Insert("a", "{\"Title\":\"ready\",\"n\":7,\"child\":{\"x\":1},\"tags\":[\"a\",\"b\"]}");
        db.Documents.Open("docs").Insert("b", "{\"n\":null}");
        const string sql = """
            SELECT d.ID AS DocumentID, UPPER(json_value(d.document, '$.Title')) AS Title,
                   json_value(d.document, '$.title') AS missing,
                   json_value(d.document, '$.child') AS child,
                   json_value(d.document, '$.tags') AS tags,
                   CASE WHEN json_value(d.document, '$.n') IS NULL THEN 0 ELSE json_value(d.document, '$.n') + 1 END AS next_n
            FROM docs AS d
            """;

        SelectExecutionResult result = Select(db, sql, new SqlExecutionOptions { MaxMaterializedRows = 2 });

        AssertEquivalent(Select(db, sql), result);
        Assert.Equal("DocumentID", result.Columns[0]);
        Assert.Equal("READY", result.Rows[0][1]);
        Assert.Null(result.Rows[0][2]);
        Assert.Equal("{\"x\":1}", result.Rows[0][3]);
        Assert.Equal("[\"a\",\"b\"]", result.Rows[0][4]);
        Assert.Equal(8d, Assert.IsType<double>(result.Rows[0][5]));
        Assert.Equal(0L, Assert.IsType<long>(result.Rows[1][5]));
        AssertReleased(db);
    }

    /// <summary>既有 JSON 索引存在时预算路径仍逐文档过滤，不加载全部索引结果。</summary>
    [Fact]
    public void Execute_IndexedFilter_PreservesCompleteFilteredPage()
    {
        using Tsdb db = OpenDocuments(20);
        SqlExecutor.Execute(db, "CREATE JSON INDEX idx_n ON docs ('$.n')");
        const string sql = "SELECT id FROM docs WHERE json_value(document, '$.n') >= 17 LIMIT 2";

        AssertEquivalent(Select(db, sql), Select(db, sql, new SqlExecutionOptions { MaxMaterializedRows = 2 }));
        AssertReleased(db);
    }

    /// <summary>LIMIT 后的无效 CAST 不求值，完整查询仍暴露该错误。</summary>
    [Fact]
    public void Execute_LimitBeforeInvalidLaterProjection_EvaluatesOnlySqlPage()
    {
        using Tsdb db = OpenDocuments(0);
        db.Documents.Open("docs").Insert("a", "{\"value\":\"7\"}");
        db.Documents.Open("docs").Insert("b", "{\"value\":\"invalid\"}");

        SelectExecutionResult result = Select(db,
            "SELECT CAST(json_value(document, '$.value') AS INT) AS parsed FROM docs LIMIT 1",
            new SqlExecutionOptions { MaxMaterializedRows = 1 });

        Assert.Equal(7L, Assert.Single(result.Rows)[0]);
        Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT CAST(json_value(document, '$.value') AS INT) FROM docs"));
        AssertReleased(db);
    }

    /// <summary>Document 嵌套或阻塞形状在任何候选读取前拒绝。</summary>
    [Theory]
    [InlineData("SELECT COUNT(*) FROM docs")]
    [InlineData("SELECT CAST(SUM(json_value(document, '$.n')) AS INT) FROM docs")]
    [InlineData("SELECT json_value(document, '$.n') FROM docs GROUP BY json_value(document, '$.n')")]
    [InlineData("SELECT ROW_NUMBER() OVER (ORDER BY id) FROM docs")]
    [InlineData("SELECT DISTINCT id FROM docs")]
    [InlineData("SELECT id FROM docs UNION ALL SELECT 'x'")]
    [InlineData("SELECT 'x' UNION ALL SELECT id FROM docs")]
    [InlineData("SELECT id FROM (SELECT id FROM docs) AS source")]
    [InlineData("WITH source AS (SELECT id FROM docs) SELECT id FROM source")]
    [InlineData("SELECT id FROM docs WHERE EXISTS (SELECT 1)")]
    [InlineData("SELECT id FROM docs WHERE id IN (SELECT 'a')")]
    [InlineData("SELECT d.id FROM docs AS d JOIN items AS i ON d.id = i.id")]
    [InlineData("SELECT id FROM docs WHERE match('index', 'query')")]
    [InlineData("SELECT bm25_score() FROM docs")]
    public void Execute_UnsupportedDocumentShape_RejectsBeforeCandidateRead(string sql)
    {
        using Tsdb db = OpenDocuments(4);
        SqlExecutor.Execute(db, "CREATE TABLE items (id STRING)");
        var metrics = new SqlExecutionMetrics();
        long scans = db.Documents.Open("docs").FullScanCount;

        Assert.Throws<NotSupportedException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 100, Metrics = metrics }));

        Assert.Equal(0, metrics.Complete().CandidateRows);
        Assert.Equal(scans, db.Documents.Open("docs").FullScanCount);
        AssertReleased(db);
    }

    /// <summary>TTL 集合通过逐文档可见性检查读取，没有时间字段的文档仍可见。</summary>
    [Fact]
    public void Execute_TtlCollection_ReadsWithoutExpiryPurge()
    {
        using Tsdb db = OpenDocuments(1);
        SqlExecutor.Execute(db, "CREATE TTL INDEX idx_expiry ON docs ('$.expiresAt') WITH ttl_seconds = 1");
        var metrics = new SqlExecutionMetrics();

        SelectExecutionResult result = Select(db,
            "SELECT id FROM docs LIMIT 1", new SqlExecutionOptions { MaxMaterializedRows = 1, Metrics = metrics });

        Assert.Single(result.Rows);
        Assert.Equal(1, metrics.Complete().CandidateRows);
        AssertReleased(db);
        Assert.Single(Select(db, "SELECT id FROM docs").Rows);
    }

    /// <summary>预检之后新增 TTL 索引时，锁内使用最新 schema，读取不会转入全量回收。</summary>
    [Fact]
    public void Execute_StaleSchemaAfterTtlAddition_ReadsWithoutExpiryPurge()
    {
        using Tsdb db = OpenDocuments(1);
        DocumentCollectionSchema schema = db.Documents.Catalog.TryGet("docs")!;
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse("SELECT id FROM docs"));
        DocumentSqlExecutor.ValidateMaterializationSupported(schema, statement);
        SqlExecutor.Execute(db, "CREATE TTL INDEX idx_expiry ON docs ('$.expiresAt') WITH ttl_seconds = 1");

        using (SqlRowRetentionBudget.EnterExecution(new SqlExecutionOptions { MaxMaterializedRows = 1 }))
        {
            Assert.Single(DocumentSqlExecutor.ExecuteSelect(db, statement, schema).Rows);
        }
        AssertReleased(db);
        Assert.Single(Select(db, "SELECT id FROM docs").Rows);
    }

    /// <summary>用户函数在扫描和回调前拒绝，默认查询仍可以调用。</summary>
    [Fact]
    public void Execute_UserCallback_RejectsBeforeInvocation()
    {
        using Tsdb db = OpenDocuments(2);
        int called = 0;
        db.Functions.RegisterScalar("document_callback", _ => { called++; return 1L; });

        Assert.Throws<NotSupportedException>(() => Select(db, "SELECT document_callback() FROM docs",
            new SqlExecutionOptions { MaxMaterializedRows = 10 }));

        Assert.Equal(0, called);
        Assert.Equal(2, Select(db, "SELECT document_callback() FROM docs").Rows.Count);
        Assert.Equal(2, called);
        AssertReleased(db);
    }

    /// <summary>EXPLAIN ANALYZE 的 Document 行与解释行计入同一根预算。</summary>
    [Fact]
    public void Execute_ExplainAnalyze_SharesRootBudgetWithExplainResult()
    {
        using Tsdb db = OpenDocuments(2);
        const string sql = "EXPLAIN ANALYZE SELECT id FROM docs";
        int explainRows = Select(db, sql).Rows.Count;

        Assert.Throws<InvalidOperationException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 2 + explainRows - 1 }));
        Assert.Equal(explainRows, Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 2 + explainRows }).Rows.Count);
        AssertReleased(db);
    }

    /// <summary>取消和已过期截止时间不读取候选，后续根调用恢复独立预算。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_CancelledRoot_ReleasesBudgetAndAllowsNextQuery(bool expiredDeadline)
    {
        using Tsdb db = OpenDocuments(10);
        using var cancellation = new CancellationTokenSource();
        if (!expiredDeadline)
            cancellation.Cancel();
        var options = new SqlExecutionOptions
        {
            MaxMaterializedRows = 1,
            CancellationToken = cancellation.Token,
            DeadlineUtc = expiredDeadline ? DateTimeOffset.UtcNow.AddSeconds(-1) : null,
        };

        RoutineExecutionException error = Assert.Throws<RoutineExecutionException>(() => Select(db,
            "SELECT document FROM docs", options));

        Assert.Equal("routine_cancelled", error.Code);
        AssertReleased(db);
        Assert.Single(Select(db, "SELECT id FROM docs LIMIT 1", new SqlExecutionOptions { MaxMaterializedRows = 1 }).Rows);
    }

    /// <summary>超界失败没有修改持久数据，重开后默认排序及聚合仍完整可用。</summary>
    [Fact]
    public void Execute_BudgetRejection_PreservesPersistentDataAndDefaultCapabilities()
    {
        using (Tsdb db = OpenDocuments(20))
        {
            Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT document FROM docs",
                new SqlExecutionOptions { MaxMaterializedRows = 2 }));
            AssertReleased(db);
        }
        using Tsdb reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });

        SelectExecutionResult result = Select(reopened, "SELECT id FROM docs ORDER BY id DESC");
        Assert.Equal(20, result.Rows.Count);
        Assert.Equal("d0019", result.Rows[0][0]);
        Assert.Equal(20L, Assert.Single(Select(reopened, "SELECT COUNT(*) FROM docs").Rows)[0]);
        Assert.False(result.Truncated);
        AssertReleased(reopened);
    }

    /// <summary>清理本测试创建的独占数据库目录。</summary>
    public void Dispose()
    {
        string target = Path.GetFullPath(_root);
        Assert.StartsWith("sdb-docb-", Path.GetFileName(target), StringComparison.Ordinal);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }

    private Tsdb OpenDocuments(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 4096);
        Tsdb db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION docs");
        if (count != 0)
        {
            DocumentWriteResult result = db.Documents.Open("docs").InsertMany(Enumerable.Range(0, count)
                .Select(static number => new DocumentWriteRequest(
                    "d" + number.ToString("D4", CultureInfo.InvariantCulture),
                    "{\"n\":" + number.ToString(CultureInfo.InvariantCulture) + "}")));
            Assert.False(result.HasErrors);
        }
        return db;
    }

    private static SelectExecutionResult Select(Tsdb db, string sql, SqlExecutionOptions? options = null)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, sql, null, null,
            options ?? SqlExecutionOptions.Default));

    private static void AssertReleased(Tsdb db)
    {
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        Assert.False(SqlRowRetentionBudget.HasExecutionBudget);
    }

    private static void AssertEquivalent(SelectExecutionResult expected, SelectExecutionResult actual)
    {
        Assert.Equal(expected.Columns, actual.Columns);
        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        for (int i = 0; i < expected.Rows.Count; i++)
            Assert.Equal(expected.Rows[i], actual.Rows[i]);
        Assert.False(actual.Truncated);
    }
}
