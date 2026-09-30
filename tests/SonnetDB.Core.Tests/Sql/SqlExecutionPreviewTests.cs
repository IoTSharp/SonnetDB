using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证显式预览在关系表 SELECT 执行阶段的早停和边界语义。</summary>
public sealed class SqlExecutionPreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-sql-execution-preview-" + Guid.NewGuid().ToString("N"));

    /// <summary>恰好 N 行为完整结果，N+1 行触发截断且扫描在前视行停止。</summary>
    [Fact]
    public void ExecuteSelect_ExactAndOverflowRows_StopsAfterLookahead()
    {
        using Tsdb db = OpenItems(20);
        var options = new SqlExecutionOptions { PreviewMaxRows = 2, PreviewMaxBytes = 1_024 };
        int decoded = 0;
        db.Tables.Open("items").RowDecodedTestHook = _ => decoded++;

        SelectExecutionResult preview = Select(db, "SELECT id FROM items", options);

        Assert.Equal([1L, 2L], preview.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.True(preview.Truncated);
        Assert.Equal(3, decoded);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);

        db.Tables.Open("items").RowDecodedTestHook = null;
        SelectExecutionResult exact = Select(db, "SELECT id FROM items LIMIT 2", options);
        Assert.Equal([1L, 2L], exact.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.False(exact.Truncated);
        Assert.Equal(20, Select(db, "SELECT id FROM items").Rows.Count);
    }

    /// <summary>过滤和 SQL 分页先执行，预览只截取查询结果的前缀。</summary>
    [Fact]
    public void ExecuteSelect_WithWhereOffsetAndLimit_PreservesSqlResultPrefix()
    {
        using Tsdb db = OpenItems(8);
        const string sql = "SELECT id FROM items WHERE category = 1 LIMIT 3 OFFSET 1";
        SelectExecutionResult complete = Select(db, sql);
        SelectExecutionResult preview = Select(db, sql,
            new SqlExecutionOptions { PreviewMaxRows = 2, PreviewMaxBytes = 1_024 });

        Assert.Equal([3L, 5L, 7L], complete.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.Equal(complete.Rows.Take(2).Select(static row => row[0]),
            preview.Rows.Select(static row => row[0]));
        Assert.True(preview.Truncated);

        SelectExecutionResult exact = Select(db,
            "SELECT id FROM items WHERE category = 1 LIMIT 2 OFFSET 1",
            new SqlExecutionOptions { PreviewMaxRows = 2, PreviewMaxBytes = 1_024 });
        Assert.Equal([3L, 5L], exact.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.False(exact.Truncated);
    }

    /// <summary>UTF-16 字符串按行估算计费，超大首行不会作为部分结果返回。</summary>
    [Theory]
    [InlineData(264L, 1, false)]
    [InlineData(263L, 0, true)]
    public void ExecuteSelect_StringByteBoundary_EnforcesExactEstimate(
        long maxBytes, int expectedRows, bool truncated)
    {
        using Tsdb db = OpenItems(1);
        SelectExecutionResult preview = Select(db, "SELECT payload FROM items",
            new SqlExecutionOptions { PreviewMaxRows = 10, PreviewMaxBytes = maxBytes });

        Assert.Equal(expectedRows, preview.Rows.Count);
        Assert.Equal(truncated, preview.Truncated);
        if (expectedRows == 1)
            Assert.Equal(new string('x', 100), preview.Rows[0][0]);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>字节预算可在行数预算之前截断，前视宽行不得进入结果集。</summary>
    [Fact]
    public void ExecuteSelect_SecondWideRowOverByteLimit_KeepsOnlyFirstRow()
    {
        using Tsdb db = OpenItems(10);
        int decoded = 0;
        db.Tables.Open("items").RowDecodedTestHook = _ => decoded++;

        SelectExecutionResult preview = Select(db, "SELECT id, payload FROM items",
            new SqlExecutionOptions { PreviewMaxRows = 10, PreviewMaxBytes = 296 });

        Assert.Single(preview.Rows);
        Assert.Equal(1L, preview.Rows[0][0]);
        Assert.True(preview.Truncated);
        Assert.Equal(2, decoded);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>扫描期间取消不会返回成功的截断结果，后续查询仍可完整运行。</summary>
    [Fact]
    public void ExecuteSelect_CancelledDuringPreview_ReleasesQueryResources()
    {
        using Tsdb db = OpenItems(20);
        using var cancellation = new CancellationTokenSource();
        db.Tables.Open("items").RowDecodedTestHook = count =>
        {
            if (count == 3)
                cancellation.Cancel();
        };

        RoutineExecutionException error = Assert.Throws<RoutineExecutionException>(() =>
            Select(db, "SELECT id FROM items", new SqlExecutionOptions
            {
                PreviewMaxRows = 10,
                PreviewMaxBytes = 1_024,
                CancellationToken = cancellation.Token,
            }));

        Assert.Equal("routine_cancelled", error.Code);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        db.Tables.Open("items").RowDecodedTestHook = null;
        Assert.Equal(20, Select(db, "SELECT id FROM items").Rows.Count);
    }

    /// <summary>预览只求值已扫描前缀，完整查询仍暴露后段行的转换错误。</summary>
    [Fact]
    public void ExecuteSelect_CastFailureBeyondLookahead_DoesNotInvalidatePreviewPrefix()
    {
        using Tsdb db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE casts (id INT, raw STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO casts (id, raw) VALUES (1, '1'), (2, '2'), (3, 'bad')");
        const string sql = "SELECT CAST(raw AS INT) AS value FROM casts";

        SelectExecutionResult preview = Select(db, sql,
            new SqlExecutionOptions { PreviewMaxRows = 1, PreviewMaxBytes = 1_024 });

        Assert.Equal(1L, Assert.Single(preview.Rows)[0]);
        Assert.True(preview.Truncated);
        Assert.Throws<InvalidOperationException>(() => Select(db, sql));
    }

    /// <summary>用户函数查询沿用完整执行路径，传输预览保留其原始结果前缀。</summary>
    [Fact]
    public void ExecuteSelect_WithUserFunction_FallsBackToCompleteExecution()
    {
        using Tsdb db = OpenItems(8);
        int calls = 0;
        db.Functions.RegisterScalar("preview_identity", arguments =>
        {
            calls++;
            return arguments[0];
        });
        const string sql = "SELECT preview_identity(id) FROM items";
        SelectExecutionResult complete = Select(db, sql);
        Assert.Equal(8, calls);

        calls = 0;
        SelectExecutionResult executed = Select(db, sql,
            new SqlExecutionOptions { PreviewMaxRows = 2, PreviewMaxBytes = 1_024 });
        Assert.Equal(8, calls);
        Assert.Equal(8, executed.Rows.Count);
        Assert.False(executed.Truncated);

        var preview = Assert.IsType<SelectExecutionResult>(SqlResultBounds.Apply(executed, 2));
        Assert.Equal(complete.Rows.Take(2).Select(static row => row[0]),
            preview.Rows.Select(static row => row[0]));
        Assert.True(preview.Truncated);
    }

    /// <summary>删除本测试创建的数据库目录。</summary>
    public void Dispose()
    {
        string target = Path.GetFullPath(_root);
        string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(target), tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(target).StartsWith("sndb-sql-execution-preview-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("测试数据库清理路径不在本测试的临时目录范围内。");
        }
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }

    private Tsdb OpenItems(int count)
    {
        Tsdb db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db,
            "CREATE TABLE items (id INT, category INT, payload STRING, PRIMARY KEY (id))");
        string payload = new('x', 100);
        string rows = string.Join(", ", Enumerable.Range(1, count)
            .Select(id => $"({id}, {id % 2}, '{payload}')"));
        SqlExecutor.Execute(db, $"INSERT INTO items (id, category, payload) VALUES {rows}");
        return db;
    }

    private static SelectExecutionResult Select(Tsdb db, string sql, SqlExecutionOptions? options = null)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.ExecuteStatement(
            db, null, SqlParser.Parse(sql), null, null, options ?? SqlExecutionOptions.Default));
}
