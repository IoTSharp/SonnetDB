using SonnetDB.Engine;
using SonnetDB.Graphs;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证完整 Int64 分页位置、实际返回窗口和独立资源预算。</summary>
[Collection(SqlSpillSerialCollection.Name)]
public sealed class SqlInt64PaginationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "p64-" + Guid.NewGuid().ToString("N"));

    /// <summary>两种 SQL 语法、命名参数及公开 AST 保留完整整数而非 Int32 截断。</summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(2147483647L)]
    [InlineData(2147483648L)]
    [InlineData(long.MaxValue - 1)]
    [InlineData(long.MaxValue)]
    public void Pagination_ConstantsParametersAndAst_PreserveInt64(long count)
    {
        foreach (string sql in new[]
        {
            $"SELECT 1 LIMIT {count} OFFSET {count}",
            $"SELECT 1 OFFSET {count} ROWS FETCH NEXT {count} ROWS ONLY",
        })
        {
            var parsed = Assert.IsType<SelectStatement>(SqlParser.Parse(sql));
            Assert.Equal(count, parsed.Pagination!.Offset);
            Assert.Equal(count, parsed.Pagination.Fetch);
        }
        var template = SqlParser.Parse("SELECT 1 LIMIT @take OFFSET @skip");
        var bound = Assert.IsType<SelectStatement>(SqlParameterBinder.Bind(template,
            new SqlParameters().AddNamed("take", count).AddNamed("skip", count)));
        Assert.Equal(count, bound.Pagination!.Offset);
        Assert.Equal(count, bound.Pagination.Fetch);
        var page = new PaginationSpec(count, count);
        Assert.Equal(count, page.Offset);
        Assert.Equal(count, page.Fetch);
        var oldConstructor = new PaginationSpec(1, (int?)2);
        Assert.Equal(1L, oldConstructor.Offset);
        Assert.Equal(2L, oldConstructor.Fetch);
    }

    /// <summary>单表、排序、JOIN、CTE、视图、聚合和集合查询使用同一长整数窗口。</summary>
    [Theory]
    [InlineData("SELECT id FROM items")]
    [InlineData("SELECT label FROM items WHERE id>0")]
    [InlineData("SELECT id FROM items ORDER BY id DESC")]
    [InlineData("SELECT id FROM items ORDER BY label DESC")]
    [InlineData("SELECT DISTINCT label FROM items ORDER BY label")]
    [InlineData("SELECT label,COUNT(*) FROM items GROUP BY label ORDER BY label")]
    [InlineData("SELECT a.id FROM items a INNER JOIN items b ON a.id=b.id ORDER BY a.id")]
    [InlineData("WITH selected AS (SELECT id FROM items) SELECT id FROM selected ORDER BY id")]
    [InlineData("SELECT id FROM item_view ORDER BY id")]
    [InlineData("SELECT id FROM (SELECT id FROM items) selected ORDER BY id")]
    [InlineData("SELECT id FROM items UNION ALL SELECT id FROM items ORDER BY id")]
    [InlineData("SELECT 42 AS id")]
    public void Pagination_QueryShapes_LargeFetchAndOffsetDoNotWrap(string sql)
    {
        using var db = OpenItems();
        var expected = Query(db, sql + " LIMIT 100 OFFSET 1");
        var actual = Query(db, sql + " LIMIT 9223372036854775807 OFFSET 1");
        Assert.Equal(expected.Columns, actual.Columns);
        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        for (int row = 0; row < expected.Rows.Count; row++) Assert.Equal(expected.Rows[row], actual.Rows[row]);
        foreach (long offset in new[] { 2147483647L, 2147483648L, long.MaxValue })
        {
            Assert.Empty(Query(db, sql + $" LIMIT 1 OFFSET {offset}").Rows);
            Assert.Empty(Query(db, sql + $" LIMIT 9223372036854775807 OFFSET {offset}").Rows);
            Assert.Empty(Query(db, sql + $" OFFSET {offset}").Rows);
        }
    }

    /// <summary>空表及零窗口无需分配大前缀，也不丢失列声明。</summary>
    [Fact]
    public void Pagination_EmptyAndZeroWindows_PreserveMetadata()
    {
        using var db = OpenItems();
        SqlExecutor.Execute(db, "CREATE TABLE empty_items(id INT, PRIMARY KEY(id))");
        foreach (string sql in new[]
        {
            "SELECT id FROM empty_items LIMIT 9223372036854775807",
            "SELECT id FROM empty_items LIMIT 1 OFFSET 9223372036854775807",
            "SELECT id FROM items LIMIT 0 OFFSET 9223372036854775807",
            "SELECT id FROM items ORDER BY label LIMIT 0 OFFSET 9223372036854775807",
        })
        {
            var result = Query(db, sql);
            Assert.Empty(result.Rows);
            Assert.Equal(TableColumnType.Int64, Assert.Single(result.ColumnInfo!).DataType);
        }
        var parameters = new SqlParameters().AddNamed("take", long.MaxValue).AddNamed("skip", long.MaxValue);
        Assert.Empty(Query(db, "SELECT id FROM items LIMIT @take OFFSET @skip", parameters).Rows);
        Assert.Empty(Query(db, "SELECT id FROM items OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY", parameters).Rows);
        Assert.Empty(Query(db, "SELECT id FROM items LIMIT ? OFFSET ?", new SqlParameters().AddPositional(1L).AddPositional(long.MaxValue)).Rows);
    }

    /// <summary>负数、非整数和超过 Int64 的文本不能静默转为可用窗口。</summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("-9223372036854775808")]
    [InlineData("9223372036854775808")]
    [InlineData("18446744073709551615")]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData("NULL")]
    [InlineData("'1'")]
    [InlineData("TRUE")]
    public void Pagination_InvalidLiteral_RejectsBeforeEmptyResult(string literal)
    {
        Assert.Throws<SqlParseException>(() => SqlParser.Parse($"SELECT 1 WHERE FALSE LIMIT {literal}"));
        Assert.Throws<SqlParseException>(() => SqlParser.Parse($"SELECT 1 WHERE FALSE LIMIT 0 OFFSET {literal}"));
    }

    /// <summary>绑定拒绝非法类型和越界值，且缓存 AST 不被失败或大参数污染。</summary>
    [Fact]
    public void Pagination_InvalidParameters_RejectWithoutMutatingCachedAst()
    {
        using var db = OpenItems();
        const string sql = "SELECT id FROM items LIMIT @take OFFSET @skip";
        var template = Assert.IsType<SelectStatement>(SqlParser.Parse(sql));
        foreach (object? invalid in new object?[] { -1L, long.MinValue, ulong.MaxValue, 9223372036854775808m, 1m, 1.0d, "1", true, null, DBNull.Value })
        {
            Type exceptionType = invalid is ulong ? typeof(OverflowException) : typeof(InvalidOperationException);
            Assert.Throws(exceptionType, () => Query(db, sql,
                new SqlParameters().AddNamed("take", invalid).AddNamed("skip", 0L)));
            Assert.Throws(exceptionType, () => Query(db, sql,
                new SqlParameters().AddNamed("take", 0L).AddNamed("skip", invalid)));
        }
        Assert.IsType<ParameterExpression>(template.Pagination!.FetchExpression);
        Assert.IsType<ParameterExpression>(template.Pagination.OffsetExpression);
        Assert.Equal(2L, Assert.Single(Query(db, sql,
            new SqlParameters().AddNamed("take", 1L).AddNamed("skip", 1L)).Rows)[0]);
    }

    /// <summary>大 LIMIT 不扩张返回预算，跳过的小表不被错误的加法或容量转换限制。</summary>
    [Fact]
    public void Pagination_MaterializationBudget_RemainsIndependentOfInt64Window()
    {
        using var db = OpenItems();
        var options = new SqlExecutionOptions { MaxMaterializedRows = 2, MaxMaterializedBytes = 4096 };
        var failure = Assert.Throws<InvalidOperationException>(() => Query(db,
            "SELECT id FROM items LIMIT 9223372036854775807", options: options));
        Assert.Contains("累计物化", failure.Message, StringComparison.Ordinal);
        Assert.Empty(Query(db, "SELECT id FROM items LIMIT 9223372036854775807 OFFSET 9223372036854775807", options: options).Rows);
        Assert.Equal(3, Query(db, "SELECT id FROM items LIMIT 9223372036854775807").Rows.Count);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>超大窗口走受预算外排，归并跳过仍为 Int64，结束后释放临时资源。</summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(2147483648L)]
    [InlineData(long.MaxValue)]
    public void Pagination_ForcedSpill_PreservesWindowAndMemoryBudget(long offset)
    {
        using var db = OpenItems();
        SqlExecutor.Execute(db, "INSERT INTO items(id,label) VALUES " + string.Join(',',
            Enumerable.Range(4, 37).Select(index => $"({index},'{40 - index:D3}')")));
        string sql = $"SELECT id,label FROM items ORDER BY label,id LIMIT 9223372036854775807 OFFSET {offset}";
        var expected = Query(db, sql, options: new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 1024 * 1024 });
        var metrics = new SqlExecutionMetrics();
        var actual = Query(db, sql, options: new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 96, Metrics = metrics });
        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        for (int index = 0; index < expected.Rows.Count; index++) Assert.Equal(expected.Rows[index], actual.Rows[index]);
        var snapshot = metrics.Complete();
        Assert.True(snapshot.SpillCount > 0);
        Assert.InRange(snapshot.PeakMemoryBytes, 0, 96);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        string spillRoot = Path.Combine(_root, SqlSpillWorkspace.DirectoryName);
        if (Directory.Exists(spillRoot)) Assert.Empty(Directory.EnumerateDirectories(spillRoot, "query-*"));
    }

    /// <summary>共享分页也用于时序、文档、图、JSON 文件及有界向量结果，不缩窄逻辑位置。</summary>
    [Theory]
    [InlineData("SELECT value FROM readings")]
    [InlineData("SELECT time,value FROM readings ORDER BY time DESC")]
    [InlineData("SELECT id FROM docs")]
    [InlineData("SELECT id FROM docs ORDER BY id DESC")]
    [InlineData("SELECT id FROM graph_nodes('topology')")]
    [InlineData("SELECT ordinal FROM json_each('{file}', 'array')")]
    [InlineData("SELECT id FROM vector_search(source => docs, vector => [0,0], k => 3, metric => 'l2')")]
    public void Pagination_OtherSqlSources_KeepInt64AndIndependentBudget(string sql)
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE MEASUREMENT readings(value FIELD INT)");
        SqlExecutor.Execute(db, "INSERT INTO readings(time,value) VALUES(1,30),(2,10),(3,20)");
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION docs");
        var documents = db.Documents.Open("docs");
        documents.Insert("a", "{\"embedding\":[1,0]}");
        documents.Insert("b", "{\"embedding\":[2,0]}");
        documents.Insert("c", "{\"embedding\":[3,0]}");
        var graph = db.Graphs.Create("topology");
        var transaction = graph.BeginTransaction(Guid.NewGuid());
        for (int index = 1; index <= 3; index++)
            transaction.UpsertVertex(new GraphElementId(index), 0, [new LabelId(1)], []);
        transaction.Commit();
        string file = Path.Combine(_root, "source.json");
        File.WriteAllText(file, "[{\"n\":1},{\"n\":2},{\"n\":3}]");
        sql = sql.Replace("{file}", file.Replace("'", "''", StringComparison.Ordinal), StringComparison.Ordinal);

        foreach (var options in new[] { SqlExecutionOptions.Default, new SqlExecutionOptions { MaxMaterializedRows = 20, MaxMaterializedBytes = 65536 } })
        {
            var expected = Query(db, sql + " LIMIT 100 OFFSET 1", options: options);
            var actual = Query(db, sql + " LIMIT 9223372036854775807 OFFSET 1", options: options);
            Assert.Equal(2, actual.Rows.Count);
            for (int index = 0; index < expected.Rows.Count; index++) Assert.Equal(expected.Rows[index], actual.Rows[index]);
            Assert.Empty(Query(db, sql + " LIMIT 9223372036854775807 OFFSET 9223372036854775807", options: options).Rows);
            Assert.Empty(Query(db, sql + " LIMIT 1 OFFSET 2147483648", options: options).Rows);
            Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        }
    }

    private Tsdb OpenItems()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE items(id INT,label STRING,PRIMARY KEY(id))");
        SqlExecutor.Execute(db, "INSERT INTO items(id,label) VALUES(1,'c'),(2,'a'),(3,'b')");
        SqlExecutor.Execute(db, "CREATE VIEW item_view AS SELECT id FROM items");
        return db;
    }

    private static SelectExecutionResult Query(Tsdb db, string sql, SqlParameters? parameters = null, SqlExecutionOptions? options = null)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, sql, parameters, null, options ?? new SqlExecutionOptions()));

    /// <summary>释放测试独占目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
