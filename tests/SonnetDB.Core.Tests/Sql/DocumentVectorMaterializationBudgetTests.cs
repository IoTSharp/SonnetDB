using System.Globalization;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证文档向量 SQL 的 opt-in 累计候选、排序快照和结果物化准入。</summary>
public sealed class DocumentVectorMaterializationBudgetTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(), "sndb-vector-budget-" + Guid.NewGuid().ToString("N")));

    /// <summary>空集合和零 LIMIT 不需要保留任何候选。</summary>
    [Theory]
    [InlineData(0, "")]
    [InlineData(4, "LIMIT 0")]
    public void Execute_EmptyPage_RetainsNoCandidates(int count, string suffix)
    {
        using Tsdb db = OpenDocuments(count);
        var metrics = new SqlExecutionMetrics();

        SelectExecutionResult result = Select(db, Query("id", suffix: suffix),
            new SqlExecutionOptions { MaxMaterializedBytes = 1, Metrics = metrics });

        Assert.Empty(result.Rows);
        Assert.Equal(0, metrics.Complete().CandidateRows);
        AssertReleased(db);
    }

    /// <summary>精确扫描在第一个不能保留的候选处失败，未生成全集合快照。</summary>
    [Fact]
    public void Execute_SmallCandidateBudget_StopsAtFailureFrontier()
    {
        using Tsdb db = OpenDocuments(64);
        var metrics = new SqlExecutionMetrics();
        long scans = db.Documents.Open("docs").FullScanCount;
        int distances = 0;
        Action? previous = DocumentVectorSearchExecutor.DistanceComputedTestHook;
        DocumentVectorSearchExecutor.DistanceComputedTestHook = () => distances++;
        try
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Select(db, Query("id", k: 17),
                new SqlExecutionOptions { MaxMaterializedRows = 2, Metrics = metrics }));
            Assert.Contains("累计物化", error.Message, StringComparison.Ordinal);
            Assert.Equal(3, distances);
        }
        finally
        {
            DocumentVectorSearchExecutor.DistanceComputedTestHook = previous;
        }
        Assert.Equal(3, metrics.Complete().CandidateRows);
        Assert.Equal(scans, db.Documents.Open("docs").FullScanCount);
        AssertReleased(db);
        Assert.Equal(17, Select(db, Query("id", k: 17)).Rows.Count);
    }

    /// <summary>候选、排序快照和投影各计一次累计行，等号允许而少一行拒绝。</summary>
    [Theory]
    [InlineData(9, true)]
    [InlineData(8, false)]
    public void Execute_ThreeRetentionStages_EnforcesExactRowBoundary(long rows, bool succeeds)
    {
        using Tsdb db = OpenDocuments(3);
        var options = new SqlExecutionOptions { MaxMaterializedRows = rows };
        if (succeeds)
            AssertEquivalent(Select(db, Query("id", k: 3)), Select(db, Query("id", k: 3), options));
        else
            Assert.Throws<InvalidOperationException>(() => Select(db, Query("id", k: 3), options));
        AssertReleased(db);
    }

    /// <summary>未进入 Top-K 的瞬时扫描行不增加累计保留行。</summary>
    [Fact]
    public void Execute_WorseningCandidates_OnlyRetainsTopK()
    {
        using Tsdb db = OpenDocuments(64);
        var metrics = new SqlExecutionMetrics();

        SelectExecutionResult result = Select(db, Query("id", k: 1),
            new SqlExecutionOptions { MaxMaterializedRows = 3, Metrics = metrics });

        Assert.Equal("d000", Assert.Single(result.Rows)[0]);
        Assert.Equal(64, metrics.Complete().CandidateRows);
        AssertEquivalent(Select(db, Query("id", k: 1)), result);
        AssertReleased(db);
    }

    /// <summary>即使 Top-K 只有一行，每个被替换进队列的更优候选也累计计费。</summary>
    [Fact]
    public void Execute_ReplacingCandidates_ChargesEveryAcceptedCandidate()
    {
        using Tsdb db = OpenDocuments(64, descending: true);
        var metrics = new SqlExecutionMetrics();

        Assert.Throws<InvalidOperationException>(() => Select(db, Query("id", k: 1),
            new SqlExecutionOptions { MaxMaterializedRows = 3, Metrics = metrics }));

        Assert.Equal(4, metrics.Complete().CandidateRows);
        AssertEquivalent(Select(db, Query("id", k: 1)), Select(db, Query("id", k: 1),
            new SqlExecutionOptions { MaxMaterializedRows = 66 }));
        AssertReleased(db);
    }

    /// <summary>UTF-16 JSON 快照和结果字符串在每个保留阶段使用相同估算。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_UnicodePayload_EnforcesExactByteBoundary(bool fullDocument)
    {
        using Tsdb db = OpenDocuments(0);
        DocumentCollectionStore store = db.Documents.Open("docs");
        store.Insert("雪😀", """{"embedding":[1,0,0],"text":"雪😀"}""");
        DocumentRow row = store.Get("雪😀")!;
        string value = fullDocument ? row.Json : "雪😀";
        long candidateBytes = 64 + (4 * 8) + StringBytes(row.Id) + StringBytes(row.Json) + (2 * 24);
        long outputBytes = 64 + 8 + StringBytes(value);
        long bytes = (candidateBytes * 2) + outputBytes;
        string sql = Query(fullDocument ? "document" : "json_value(document, '$.text')", k: 1);

        Assert.Equal(value, Assert.Single(Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 3, MaxMaterializedBytes = bytes }).Rows)[0]);
        Assert.Throws<InvalidOperationException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedBytes = bytes - 1 }));
        AssertReleased(db);
    }

    /// <summary>元数据过滤、标量投影、固定路径和距离别名排序保持默认结果。</summary>
    [Theory]
    [InlineData("rank >= 1", "ORDER BY distance")]
    [InlineData("json_value(document, '$.site') = 'north'", "ORDER BY vector_distance()")]
    [InlineData("NOT (rank < 1) AND site IS NOT NULL", "")]
    public void Execute_MetadataAndScalarProjection_PreservesDefaultSemantics(string predicate, string suffix)
    {
        using Tsdb db = OpenDocuments(4);
        string sql = Query("id, upper(site) AS site_name, CAST(rank AS INT) AS n, "
            + "vector_distance() AS distance, vector_score() AS score, vector_distance() + 1 AS adjusted", k: 4,
            predicate: predicate, suffix: suffix);

        AssertEquivalent(Select(db, sql), Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 12, MaxMaterializedBytes = 20_000 }));
        AssertReleased(db);
    }

    /// <summary>分页只对实际投影的页行收费，偏移行仍占候选与排序快照预算。</summary>
    [Theory]
    [InlineData("LIMIT 2 OFFSET 1")]
    [InlineData("OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY")]
    public void Execute_Pagination_ChargesCandidatesBeforePage(string suffix)
    {
        using Tsdb db = OpenDocuments(4);
        string sql = Query("id", k: 4, suffix: suffix);

        SelectExecutionResult result = Select(db, sql, new SqlExecutionOptions { MaxMaterializedRows = 10 });

        Assert.Equal(["d001", "d002"], result.Rows.Select(static row => Assert.IsType<string>(row[0])));
        AssertEquivalent(Select(db, sql), result);
        Assert.Throws<InvalidOperationException>(() => Select(db, sql, new SqlExecutionOptions { MaxMaterializedRows = 9 }));
        AssertReleased(db);
    }

    /// <summary>复杂形状与残余距离谓词在任何文档读取或距离计算之前拒绝。</summary>
    [Theory]
    [InlineData("SELECT id FROM vector_search(docs, [0,0,0], 3) ORDER BY id")]
    [InlineData("SELECT id FROM vector_search(docs, [0,0,0], 3) ORDER BY vector_distance DESC")]
    [InlineData("SELECT id FROM vector_search(docs, [0,0,0], 3) ORDER BY vector_distance, id")]
    [InlineData("SELECT rank AS distance FROM vector_search(docs, [0,0,0], 3) ORDER BY distance")]
    [InlineData("SELECT id FROM vector_search(docs, [0,0,0], 3) ORDER BY distance")]
    [InlineData("SELECT id FROM vector_search(docs, [0,0,0], 3) WHERE vector_distance() < 2")]
    [InlineData("SELECT id FROM vector_search(docs, [0,0,0], 3) WHERE vector_score > 0.5")]
    [InlineData("SELECT id FROM vector_search(docs, [0,0,0], 3) WHERE lower(site) = 'north'")]
    [InlineData("SELECT COUNT(*) FROM vector_search(docs, [0,0,0], 3)")]
    [InlineData("SELECT rank FROM vector_search(docs, [0,0,0], 3) GROUP BY rank")]
    [InlineData("SELECT DISTINCT id FROM vector_search(docs, [0,0,0], 3)")]
    [InlineData("SELECT ROW_NUMBER() OVER (ORDER BY id) FROM vector_search(docs, [0,0,0], 3)")]
    [InlineData("SELECT id FROM vector_search(docs, [0,0,0], 3) WHERE EXISTS (SELECT 1)")]
    [InlineData("SELECT id FROM vector_search(docs, [0,0,0], 3) UNION ALL SELECT 'x'")]
    [InlineData("SELECT id FROM (SELECT id FROM vector_search(docs, [0,0,0], 3)) AS source")]
    [InlineData("WITH source AS (SELECT id FROM vector_search(docs, [0,0,0], 3)) SELECT id FROM source")]
    [InlineData("SELECT json_value(document, id) FROM vector_search(docs, [0,0,0], 3)")]
    [InlineData("SELECT CAST(id AS VECTOR) FROM vector_search(docs, [0,0,0], 3)")]
    [InlineData("SELECT CAST(id AS GEOPOINT) FROM vector_search(docs, [0,0,0], 3)")]
    public void Execute_UnsupportedShape_RejectsBeforeCandidateRead(string sql)
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

    /// <summary>TVF JOIN 尚未提供文本语法；构造 AST 也必须在任何候选读取前拒绝。</summary>
    [Fact]
    public void Execute_JoinAst_RejectsBeforeCandidateRead()
    {
        using Tsdb db = OpenDocuments(4);
        SqlExecutor.Execute(db, "CREATE TABLE items (id STRING)");
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(Query("id", k: 3))) with
        {
            Joins = [new JoinClause("items", "i", LiteralExpression.Bool(true))],
        };
        Assert.Single(statement.JoinClauses);
        var metrics = new SqlExecutionMetrics();

        Assert.Throws<NotSupportedException>(() => SqlExecutor.ExecuteStatement(db, null, statement, null, null,
            new SqlExecutionOptions { MaxMaterializedRows = 100, Metrics = metrics }));

        Assert.Equal(0, metrics.Complete().CandidateRows);
        AssertReleased(db);
    }

    /// <summary>用户回调，包括覆盖内置函数名的回调，均在读取前拒绝。</summary>
    [Theory]
    [InlineData("vector_callback")]
    [InlineData("upper")]
    public void Execute_UserCallback_RejectsBeforeInvocation(string name)
    {
        using Tsdb db = OpenDocuments(2);
        int called = 0;
        db.Functions.RegisterScalar(name, _ => { called++; return "callback"; });
        var metrics = new SqlExecutionMetrics();

        Assert.Throws<NotSupportedException>(() => Select(db, Query($"{name}(site)", k: 2),
            new SqlExecutionOptions { MaxMaterializedRows = 100, Metrics = metrics }));

        Assert.Equal(0, called);
        Assert.Equal(0, metrics.Complete().CandidateRows);
        AssertReleased(db);
    }

    /// <summary>取消在下一次单文档读取前生效，保留预算与内存预留始终释放。</summary>
    [Fact]
    public void Execute_CancelledDuringScan_StopsBeforeNextDocument()
    {
        using Tsdb db = OpenDocuments(64);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var metrics = new SqlExecutionMetrics();
        int distances = 0;
        Action? previous = DocumentVectorSearchExecutor.DistanceComputedTestHook;
        DocumentVectorSearchExecutor.DistanceComputedTestHook = () =>
        {
            if (++distances == 2)
                cancellation.Cancel();
        };
        try
        {
            RoutineExecutionException error = Assert.Throws<RoutineExecutionException>(() => Select(db, Query("id", k: 17),
                new SqlExecutionOptions { MaxMaterializedRows = 100, Metrics = metrics, CancellationToken = cancellation.Token }));
            Assert.Equal(RoutineErrorCodes.Cancelled, error.Code);
            Assert.IsType<OperationCanceledException>(error.InnerException);
            Assert.Equal(2, distances);
        }
        finally
        {
            DocumentVectorSearchExecutor.DistanceComputedTestHook = previous;
        }
        Assert.Equal(2, metrics.Complete().CandidateRows);
        AssertReleased(db);
        Assert.Single(Select(db, Query("id", k: 1), new SqlExecutionOptions { MaxMaterializedRows = 3 }).Rows);
    }

    /// <summary>排序取消沿用稳定调用错误码与取消内因，不暴露 List.Sort 的比较器包装异常。</summary>
    [Fact]
    public void Execute_CancelledDuringSort_PreservesCancellationException()
    {
        using Tsdb db = OpenDocuments(4);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int comparisons = 0;
        Action? previous = DocumentVectorSearchExecutor.SortComparisonTestHook;
        DocumentVectorSearchExecutor.SortComparisonTestHook = () =>
        {
            comparisons++;
            cancellation.Cancel();
        };
        try
        {
            RoutineExecutionException error = Assert.Throws<RoutineExecutionException>(() => Select(db, Query("id", k: 4),
                new SqlExecutionOptions { MaxMaterializedRows = 100, CancellationToken = cancellation.Token }));
            Assert.Equal(RoutineErrorCodes.Cancelled, error.Code);
            Assert.IsType<OperationCanceledException>(error.InnerException);
            Assert.Equal(1, comparisons);
        }
        finally
        {
            DocumentVectorSearchExecutor.SortComparisonTestHook = previous;
        }
        AssertReleased(db);
    }

    /// <summary>直接执行器同样预检，无法借绕过入口恢复不支持的形状。</summary>
    [Fact]
    public void Execute_DirectExecutor_EnforcesMaterializationContract()
    {
        using Tsdb db = OpenDocuments(2);
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(Query("id", suffix: "ORDER BY id")));
        using (SqlRowRetentionBudget.EnterExecution(new SqlExecutionOptions { MaxMaterializedRows = 100 }))
            Assert.Throws<NotSupportedException>(() => DocumentVectorSearchExecutor.Execute(db, statement));
        AssertReleased(db);
    }

    /// <summary>非 TTL ANN 索引结果与 hit/候选/快照/投影四阶段累计预算保持一致。</summary>
    [Fact]
    public void Execute_AnnIndex_ChargesSqlHitsAndResultStages()
    {
        using Tsdb db = OpenDocuments(4);
        SqlExecutor.Execute(db, "CREATE VECTOR INDEX vi ON docs ('$.embedding') WITH (dimensions=3, metric='l2')");
        const string sql = "SELECT id FROM vector_search(source => docs, vector => [0,0,0], k => 2, metric => 'l2')";
        var metrics = new SqlExecutionMetrics();

        SelectExecutionResult result = Select(db, sql, new SqlExecutionOptions { MaxMaterializedRows = 8, Metrics = metrics });

        AssertEquivalent(Select(db, sql), result);
        Assert.Contains("document_vector_index_budgeted", metrics.Complete().AccessPath!, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Select(db, sql, new SqlExecutionOptions { MaxMaterializedRows = 7 }));
        AssertReleased(db);
    }

    /// <summary>已知 TTL 集合使用单行只读精确扫描，避免 ANN 调用触发完整过期清理。</summary>
    [Fact]
    public void Execute_TtlAndAnnIndex_UsesReadOnlyExactScan()
    {
        using Tsdb db = OpenDocuments(4);
        SqlExecutor.Execute(db, "CREATE TTL INDEX expiry ON docs ('$.expiresAt') WITH ttl_seconds = 1");
        SqlExecutor.Execute(db, "CREATE VECTOR INDEX vi ON docs ('$.embedding') WITH (dimensions=3, metric='l2')");
        var metrics = new SqlExecutionMetrics();
        string sql = Query("id", k: 2);

        SelectExecutionResult result = Select(db, sql, new SqlExecutionOptions { MaxMaterializedRows = 6, Metrics = metrics });

        Assert.Contains("document_vector_scan_budgeted", metrics.Complete().AccessPath!, StringComparison.Ordinal);
        Assert.Equal(4, metrics.Complete().CandidateRows);
        AssertEquivalent(Select(db, sql), result);
        AssertReleased(db);
    }

    /// <summary>向量维度检查仍先于元数据短路，不借过滤忽略损坏向量。</summary>
    [Fact]
    public void Execute_InvalidDimensionBeforeFilter_PreservesValidation()
    {
        using Tsdb db = OpenDocuments(0);
        db.Documents.Open("docs").Insert("a", """{"embedding":[1,0],"site":"south"}""");
        string sql = Query("id", predicate: "site = 'north'");

        InvalidOperationException expected = Assert.Throws<InvalidOperationException>(() => Select(db, sql));
        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 3 }));

        Assert.Equal(expected.Message, actual.Message);
        AssertReleased(db);
    }

    /// <summary>预算 EXPLAIN 使用保守估计，不读取或清理 TTL 集合。</summary>
    [Fact]
    public void Execute_Explain_UsesNoDocumentCandidates()
    {
        using Tsdb db = OpenDocuments(2);
        SqlExecutor.Execute(db, "CREATE TTL INDEX expiry ON docs ('$.expiresAt') WITH ttl_seconds = 1");
        SqlExecutor.Execute(db, "CREATE VECTOR INDEX vi ON docs ('$.embedding') WITH (dimensions=3, metric='l2')");
        var metrics = new SqlExecutionMetrics();

        SelectExecutionResult result = Select(db, "EXPLAIN " + Query("id", k: 2),
            new SqlExecutionOptions { MaxMaterializedRows = 100, Metrics = metrics });

        Assert.NotEmpty(result.Rows);
        Assert.Contains(result.Rows, static row => Equals(row[0], "estimate_source")
            && Equals(row[1], "budgeted_estimate_omitted"));
        Assert.Equal(0, metrics.Complete().CandidateRows);
        AssertReleased(db);
    }

    /// <summary>回收此测试实例独占的文档目录。</summary>
    public void Dispose()
    {
        string target = Path.GetFullPath(_root);
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!target.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(target).StartsWith("sndb-vector-budget-", StringComparison.Ordinal))
            throw new InvalidOperationException("文档向量预算测试清理路径不属于当前测试。");
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }

    private Tsdb OpenDocuments(int count, bool descending = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 64);
        Tsdb db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        try
        {
            SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION docs");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var requests = new DocumentWriteRequest[count];
            for (int i = 0; i < count; i++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                int distance = descending ? count - i : i + 1;
                string json = "{\"embedding\":[" + distance.ToString(CultureInfo.InvariantCulture)
                    + ",0,0],\"rank\":" + i.ToString(CultureInfo.InvariantCulture) + ",\"site\":\"north\"}";
                requests[i] = new DocumentWriteRequest("d" + i.ToString("D3", CultureInfo.InvariantCulture), json);
            }
            if (count != 0)
                Assert.False(db.Documents.Open("docs").InsertMany(requests).HasErrors);
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static string Query(string projection, int k = 20, string? predicate = null, string suffix = "")
        => "SELECT " + projection + " FROM vector_search(source => docs, vector => [0,0,0], k => "
            + k.ToString(CultureInfo.InvariantCulture) + ", metric => 'l2')"
            + (predicate is null ? "" : " WHERE " + predicate) + " " + suffix;

    private static SelectExecutionResult Select(Tsdb db, string sql, SqlExecutionOptions? options = null)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, sql, null, null,
            options ?? SqlExecutionOptions.Default));

    private static long StringBytes(string value) => 24 + (value.Length * 2L);

    private static void AssertReleased(Tsdb db)
    {
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        Assert.False(SqlRowRetentionBudget.HasExecutionBudget);
    }

    private static void AssertEquivalent(SelectExecutionResult expected, SelectExecutionResult actual)
    {
        Assert.Equal(expected.Columns, actual.Columns);
        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.InRange(expected.Rows.Count, 0, 64);
        for (int i = 0; i < expected.Rows.Count; i++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            Assert.Equal(expected.Rows[i], actual.Rows[i]);
        }
        Assert.False(actual.Truncated);
    }
}
