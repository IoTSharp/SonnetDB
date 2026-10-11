using System.Globalization;
using System.Text;
using System.Text.Json;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 JSON 文件 TVF 的逐记录读取、候选和结果累计预算以及预检边界。</summary>
public sealed class JsonFileMaterializationBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sdb-jsonb-{Guid.NewGuid():N}");
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(30));

    /// <summary>小行预算只消费失败前沿，未生成整个 JSON 数组候选列表。</summary>
    [Fact]
    public void Execute_ManyArrayRecords_StopsAtCandidateBudgetFrontier()
    {
        using Tsdb db = Open();
        string file = Write("records.json", "[" + string.Join(",", Enumerable.Range(0, 4096)
            .Select(static n => "{\"id\":\"d" + n.ToString(CultureInfo.InvariantCulture) + "\"}")) + "]");
        var metrics = new SqlExecutionMetrics();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Select(db,
            $"SELECT id FROM {Source(file)}", new SqlExecutionOptions { MaxMaterializedRows = 2, Metrics = metrics }));

        Assert.Contains("累计物化", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, metrics.Complete().CandidateRows);
        Assert.Equal(4096, Select(db, $"SELECT id FROM {Source(file)}").Rows.Count);
        AssertReleased(db);
    }

    /// <summary>过滤和 OFFSET 不免除候选成本，LIMIT 返回页只投影一次。</summary>
    [Theory]
    [InlineData("LIMIT 2 OFFSET 1")]
    [InlineData("OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY")]
    public void Execute_FilterAndPagination_ChargesFiveCandidatesAndTwoResults(string pagination)
    {
        using Tsdb db = Open();
        string file = Write("page.json", "[{\"id\":\"a\"},{\"id\":\"b\"},{\"id\":\"c\"},{\"id\":\"d\"},{\"id\":\"e\"}]");
        string sql = $"SELECT id FROM {Source(file)} WHERE ordinal >= 2 {pagination}";
        var metrics = new SqlExecutionMetrics();

        SelectExecutionResult result = Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 7, Metrics = metrics });

        Assert.Equal(["d", "e"], result.Rows.Select(static row => Assert.IsType<string>(row[0])));
        Assert.Equal(5, metrics.Complete().CandidateRows);
        AssertEquivalent(Select(db, sql), result);
        Assert.Throws<InvalidOperationException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 6 }));
        AssertReleased(db);
    }

    /// <summary>全部不匹配的谓词仍然为已解析的候选 JSON 计费。</summary>
    [Fact]
    public void Execute_FilteredOutRecords_StillChargeCandidatePayloads()
    {
        using Tsdb db = Open();
        string file = Write("filtered.json", "[{\"id\":\"a\"},{\"id\":\"b\"}]");
        Assert.Empty(Select(db, $"SELECT id FROM {Source(file)} WHERE ordinal < 0",
            new SqlExecutionOptions { MaxMaterializedRows = 2 }).Rows);
        Assert.Throws<InvalidOperationException>(() => Select(db,
            $"SELECT id FROM {Source(file)} WHERE ordinal < 0", new SqlExecutionOptions { MaxMaterializedRows = 1 }));
        AssertReleased(db);
    }

    /// <summary>候选规范化 JSON 与 Unicode 投影使用精确的既有 UTF-16 字节估算。</summary>
    [Fact]
    public void Execute_UnicodeProjection_EnforcesExactCandidateAndResultBytes()
    {
        using Tsdb db = Open();
        string file = Write("unicode.json", "[{\"id\":\"a\",\"text\":\"雪😀\"}]");
        IReadOnlyList<object?> source = Assert.Single(Select(db, $"SELECT * FROM {Source(file)}").Rows);
        string id = Assert.IsType<string>(source[1]);
        string json = Assert.IsType<string>(source[2]);
        long bytes = 160 + (id.Length * 2L) + (json.Length * 2L) + 96 + ("雪😀".Length * 2L);
        string sql = $"SELECT json_value(document, '$.text') AS text FROM {Source(file)}";

        Assert.Equal("雪😀", Assert.Single(Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 2, MaxMaterializedBytes = bytes }).Rows)[0]);
        Assert.Throws<InvalidOperationException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 2, MaxMaterializedBytes = bytes - 1 }));
        AssertReleased(db);
    }

    /// <summary>UTF-8 跨缓冲的多字节、嵌套数组和转义字符串不改变默认解析结果。</summary>
    [Theory]
    [InlineData("json_each", "array", false)]
    [InlineData("json_table", "auto", false)]
    [InlineData("json_each", "lines", true)]
    public void Execute_NestedUnicodeAcrossBuffer_PreservesDefaultResults(string function, string format, bool bom)
    {
        using Tsdb db = Open();
        string record = "{\"key\":\"雪😀\",\"long\":\"" + new string('x', 4093)
            + "é😀\",\"nested\":[{\"text\":\"a,]\\\"b\\\\c\"},[1,true,null]]}";
        string text = format == "lines" ? "\n \r\n" + record + "\r\n\n" : "[" + record + "]";
        string file = Write("nested.json", text, bom);
        string sql = $"SELECT ordinal, id AS RecordID, json_value(document, '$.nested') AS nested, document FROM {Source(file, format, function, "$.key")}";

        SelectExecutionResult bounded = Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 2, MaxMaterializedBytes = 100_000 });

        Assert.Equal("雪😀", Assert.Single(bounded.Rows)[1]);
        AssertEquivalent(Select(db, sql), bounded);
        AssertReleased(db);
    }

    /// <summary>预算 auto 路径跳过 UTF-8 BOM 后识别数组；默认 auto 保留既有 JSONL 探测行为。</summary>
    [Fact]
    public void Execute_AutoWithUtf8Bom_RecognizesArrayElementsOnlyInBudgetedPath()
    {
        using Tsdb db = Open();
        string file = Write("auto-bom.json", "[{\"id\":\"a\"},{\"id\":\"b\"}]", bom: true);
        string sql = $"SELECT ordinal, id FROM {Source(file, "auto", "json_table")}";

        SelectExecutionResult bounded = Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 4, MaxMaterializedBytes = 10_000 });

        Assert.Equal([0L, 1L], bounded.Rows.Select(static row => Assert.IsType<long>(row[0])));
        Assert.Equal(["a", "b"], bounded.Rows.Select(static row => Assert.IsType<string>(row[1])));
        Assert.False(bounded.Truncated);
        Assert.Equal(new object?[] { 0L, "0" }, Assert.Single(Select(db, sql).Rows));
        AssertReleased(db);
    }

    /// <summary>顶层单对象的自动和数组格式仍输出一条规范化记录。</summary>
    [Theory]
    [InlineData("array")]
    [InlineData("auto")]
    public void Execute_SingleObject_PreservesRootObjectContract(string format)
    {
        using Tsdb db = Open();
        string file = Write("object.json", " { \"id\": 7, \"Name\": \"pump\" } ");
        string sql = $"SELECT *, json_value(document, '$.Name') AS Name FROM {Source(file, format)}";
        AssertEquivalent(Select(db, sql), Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 2, MaxMaterializedBytes = 10_000 }));
        AssertReleased(db);
    }

    /// <summary>空数组及空 NDJSON 文件不占行预算。</summary>
    [Theory]
    [InlineData("[]", "array")]
    [InlineData("", "lines")]
    [InlineData("\n\r\n\t ", "lines")]
    public void Execute_EmptySource_ReturnsUntruncatedEmptyResult(string text, string format)
    {
        using Tsdb db = Open();
        string file = Write("empty.json", text);
        Assert.Empty(Select(db, $"SELECT * FROM {Source(file, format)}",
            new SqlExecutionOptions { MaxMaterializedRows = 1, MaxMaterializedBytes = 1 }).Rows);
        AssertReleased(db);
    }

    /// <summary>LIMIT 0 不打开文件或解析 JSON。</summary>
    [Fact]
    public void Execute_ZeroLimit_DoesNotOpenMissingFile()
    {
        using Tsdb db = Open();
        string file = Path.Combine(_root, "missing.json");
        var metrics = new SqlExecutionMetrics();
        SelectExecutionResult result = Select(db, $"SELECT * FROM {Source(file)} LIMIT 0",
            new SqlExecutionOptions { MaxMaterializedBytes = 1, Metrics = metrics });
        Assert.Empty(result.Rows);
        Assert.False(result.Truncated);
        Assert.Equal(0, metrics.Complete().CandidateRows);
        AssertReleased(db);
    }

    /// <summary>不支持的查询形状在文件访问前拒绝。</summary>
    [Theory]
    [InlineData("SELECT id FROM {0} ORDER BY id")]
    [InlineData("SELECT COUNT(*) FROM {0}")]
    [InlineData("SELECT DISTINCT id FROM {0}")]
    [InlineData("SELECT id FROM {0} GROUP BY id")]
    [InlineData("SELECT (SELECT 1) FROM {0}")]
    [InlineData("SELECT ROW_NUMBER() OVER () FROM {0}")]
    [InlineData("SELECT id IS NULL FROM {0}")]
    [InlineData("SELECT ordinal > 0 FROM {0}")]
    [InlineData("SELECT id FROM (SELECT id FROM {0}) AS j")]
    [InlineData("WITH j AS (SELECT id FROM {0}) SELECT id FROM j")]
    [InlineData("SELECT id FROM {0} UNION SELECT 'x'")]
    public void Execute_UnsupportedShape_RejectsBeforeMissingFileAccess(string template)
    {
        using Tsdb db = Open();
        string sql = string.Format(CultureInfo.InvariantCulture, template, Source(Path.Combine(_root, "missing.json")));
        Assert.Throws<NotSupportedException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 100 }));
        AssertReleased(db);
    }

    /// <summary>用户函数不能覆盖文件 TVF 或内置标量并绕过预算预检。</summary>
    [Fact]
    public void Execute_UserCallbacks_RejectsBeforeFileOrCallback()
    {
        using Tsdb db = Open();
        int called = 0;
        db.Functions.RegisterScalar("upper", _ => { called++; return "x"; });
        string file = Path.Combine(_root, "missing.json");
        Assert.Throws<NotSupportedException>(() => Select(db, $"SELECT upper(id) FROM {Source(file)}",
            new SqlExecutionOptions { MaxMaterializedRows = 100 }));
        Assert.Equal(0, called);
        AssertReleased(db);
    }

    /// <summary>显式格式未知及 JSON path 错误在读取之前失败。</summary>
    [Theory]
    [InlineData("SELECT id FROM json_each('{0}', 'xml')")]
    [InlineData("SELECT json_value(document, 'broken[') FROM json_each('{0}')")]
    [InlineData("SELECT id FROM json_each('{0}', 'array', 'broken[')")]
    public void Execute_InvalidOptions_RejectsBeforeMissingFileAccess(string template)
    {
        using Tsdb db = Open();
        string sql = string.Format(CultureInfo.InvariantCulture, template, Path.Combine(_root, "missing.json"));
        Exception error = Assert.ThrowsAny<Exception>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 100 }));
        Assert.IsNotType<FileNotFoundException>(error);
        AssertReleased(db);
    }

    /// <summary>完整扫描必须验证数组尾部、分隔符和顶层额外内容。</summary>
    [Theory]
    [InlineData("[{\"id\":\"a\"},]")]
    [InlineData("[{\"id\":\"a\"}] true")]
    [InlineData("[{\"id\":\"a\"} {\"id\":\"b\"}]")]
    [InlineData("[{\"id\":\"a\"}")]
    [InlineData("{\"id\":\"a\"} {\"id\":\"b\"}")]
    public void Execute_MalformedTailWithoutLimit_RejectsWholeQuery(string text)
    {
        using Tsdb db = Open();
        string file = Write("bad.json", text);
        Assert.Throws<JsonException>(() => Select(db, $"SELECT id FROM {Source(file)}",
            new SqlExecutionOptions { MaxMaterializedRows = 100 }));
        AssertReleased(db);
    }

    /// <summary>预算路径 LIMIT 仅验证已消费前缀，未预算默认路径仍验证整个文件。</summary>
    [Fact]
    public void Execute_LimitBeforeMalformedTail_ValidatesOnlyConsumedPrefix()
    {
        using Tsdb db = Open();
        string file = Write("prefix.json", "[{\"id\":\"a\"},not-json]");
        string sql = $"SELECT id FROM {Source(file)} LIMIT 1";
        Assert.Equal("a", Assert.Single(Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 2 }).Rows)[0]);
        Assert.ThrowsAny<JsonException>(() => Select(db, sql));
        AssertReleased(db);
    }

    /// <summary>预算拒绝先于尚未消费的损坏记录，证明不全文件 DOM 解析。</summary>
    [Fact]
    public void Execute_BudgetBeforeMalformedRecord_StopsAtFailureFrontier()
    {
        using Tsdb db = Open();
        string file = Write("frontier.json", "[{\"id\":\"a\"},{\"id\":\"b\"},not-json]");
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Select(db,
            $"SELECT id FROM {Source(file)}", new SqlExecutionOptions { MaxMaterializedRows = 2 }));
        Assert.Contains("累计物化", error.Message, StringComparison.Ordinal);
        AssertReleased(db);
    }

    /// <summary>单条记录和物理行超固定界时拒绝，并关闭源文件句柄。</summary>
    [Theory]
    [InlineData("array")]
    [InlineData("lines")]
    public void Execute_OversizedRecord_RejectsAndReleasesFile(string format)
    {
        using Tsdb db = Open();
        string record = "{\"text\":\"" + new string('x', 4 * 1024 * 1024) + "\"}";
        string file = Write("large.json", format == "array" ? "[" + record + "]" : record);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Select(db,
            $"SELECT id FROM {Source(file, format)}", new SqlExecutionOptions { MaxMaterializedRows = 100 }));
        Assert.Contains("4 MiB", error.Message, StringComparison.Ordinal);
        using FileStream exclusive = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        AssertReleased(db);
    }

    /// <summary>首个物理行的空白不能绕过单行读取字节上限。</summary>
    [Theory]
    [InlineData("lines", "{}")]
    [InlineData("auto", "true")]
    public void Execute_FirstLineOversizedLeadingWhitespace_EnforcesPhysicalLineBound(string format, string record)
    {
        using Tsdb db = Open();
        string file = Write("spaces.jsonl", new string(' ', 4 * 1024 * 1024) + record);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Select(db,
            $"SELECT id FROM {Source(file, format)}", new SqlExecutionOptions { MaxMaterializedRows = 100 }));
        Assert.Contains("4 MiB", error.Message, StringComparison.Ordinal);
        AssertReleased(db);
    }

    /// <summary>过大的源文件在解析之前拒绝。</summary>
    [Fact]
    public void Execute_OversizedFile_RejectsBeforeReadingRecords()
    {
        using Tsdb db = Open();
        string file = Write("oversized.json", "[]");
        using (FileStream stream = File.OpenWrite(file))
            stream.SetLength((256L * 1024 * 1024) + 1);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Select(db,
            $"SELECT id FROM {Source(file)}", new SqlExecutionOptions { MaxMaterializedRows = 100 }));
        Assert.Contains("256 MiB", error.Message, StringComparison.Ordinal);
        AssertReleased(db);
    }

    /// <summary>只支持 UTF-8 的显式路径不影响默认 NDJSON 编码兼容。</summary>
    [Fact]
    public void Execute_Utf16Lines_RejectsOnlyBudgetedPath()
    {
        using Tsdb db = Open();
        string file = Path.Combine(_root, "utf16.jsonl");
        File.WriteAllText(file, "{\"id\":\"a\"}\n", Encoding.Unicode);
        string sql = $"SELECT id FROM {Source(file, "lines")}";
        Assert.Throws<NotSupportedException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 100 }));
        Assert.Equal("a", Assert.Single(Select(db, sql).Rows)[0]);
        AssertReleased(db);
    }

    /// <summary>取消和期限会在文件访问前终止预算执行。</summary>
    [Fact]
    public void Execute_CancelledAndExpiredDeadline_ReleasesBudget()
    {
        using Tsdb db = Open();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        string sql = $"SELECT id FROM {Source(Path.Combine(_root, "missing.json"))}";
        RoutineExecutionException cancelledError = Assert.Throws<RoutineExecutionException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 100, CancellationToken = cancelled.Token }));
        RoutineExecutionException deadlineError = Assert.Throws<RoutineExecutionException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 100, DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }));
        Assert.Equal("routine_cancelled", cancelledError.Code);
        Assert.Equal("routine_cancelled", deadlineError.Code);
        AssertReleased(db);
    }

    /// <summary>EXPLAIN 流式计数也累计候选预算，不先生成文件候选列表。</summary>
    [Fact]
    public void Execute_Explain_ChargesStreamedCandidateRows()
    {
        using Tsdb db = Open();
        string file = Write("explain.json", "[{\"id\":\"a\"},{\"id\":\"b\"},{\"id\":\"c\"}]");
        var metrics = new SqlExecutionMetrics();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            null, $"EXPLAIN SELECT id FROM {Source(file)}", null, null,
            new SqlExecutionOptions { MaxMaterializedRows = 2, Metrics = metrics, CancellationToken = _deadline.Token }));
        Assert.Contains("累计物化", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, metrics.Complete().CandidateRows);
        AssertReleased(db);
    }

    /// <summary>深表达式在文件访问之前有界预检拒绝。</summary>
    [Fact]
    public void Execute_DeepExpression_RejectsBeforeOpeningFile()
    {
        using Tsdb db = Open();
        SqlExpression expression = new IdentifierExpression("id");
        for (int depth = 0; depth < 66; depth++)
            expression = new CastExpression(expression, SqlDataType.String);
        var statement = new SelectStatement([new SelectItem(expression, null)], "", null, [],
            TableValuedFunction: new FunctionCallExpression("json_each",
                [LiteralExpression.String(Path.Combine(_root, "missing.json"))]));
        Assert.Throws<NotSupportedException>(() => JsonFileSqlExecutor.ValidateMaterializationSupported(db, statement));
        AssertReleased(db);
    }

    /// <summary>清理本测试创建的独占临时目录和取消源。</summary>
    public void Dispose()
    {
        _deadline.Dispose();
        string target = Path.GetFullPath(_root);
        Assert.StartsWith("sdb-jsonb-", Path.GetFileName(target), StringComparison.Ordinal);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private string Write(string name, string text, bool bom = false)
    {
        string file = Path.Combine(_root, name);
        File.WriteAllText(file, text, new UTF8Encoding(bom));
        return file;
    }

    private static string Source(string file, string format = "array", string function = "json_each", string? idPath = null)
        => function + "('" + file.Replace("'", "''", StringComparison.Ordinal) + "', '" + format + "'"
            + (idPath is null ? "" : ", '" + idPath + "'") + ")";

    private SelectExecutionResult Select(Tsdb db, string sql, SqlExecutionOptions? options = null)
    {
        SqlExecutionOptions effective = options ?? SqlExecutionOptions.Default;
        if (!effective.CancellationToken.CanBeCanceled)
            effective = effective with { CancellationToken = _deadline.Token };
        return Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, sql, null, null, effective));
    }

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
