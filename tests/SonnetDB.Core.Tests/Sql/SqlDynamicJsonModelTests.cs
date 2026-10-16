using System.Text.Json;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>动态 JSON 在文档、JSON 文件和文档向量入口中的精度及显式预算合同。</summary>
public sealed class SqlDynamicJsonModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sdb-djson-" + Guid.NewGuid().ToString("N"));

    /// <summary>新准入的动态路径逐行求值；对象参数在旧标量执行器中也不经过 double。</summary>
    [Theory]
    [InlineData("document")]
    [InlineData("file")]
    [InlineData("vector")]
    public void Query_DynamicPathAndExactObject_AgreeWithAndWithoutBudget(string model)
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        string source = Seed(db, model);
        const decimal amount = 9007199254740993.01m;
        string sql = "SELECT json_value(document,json_value(document,'$.path')) AS selected, "
            + "json_object('literal',DECIMAL '9007199254740993.01','parameter',@amount,'expression',@amount+CAST('0.01' AS DECIMAL),"
            + "'negative',-@amount,'coalesced',COALESCE(NULL,@amount),'name',id) AS obj FROM " + source;
        var parameters = new SqlParameters().AddNamed("amount", amount);
        var normal = Query(db, sql, parameters);
        var bounded = Query(db, sql, parameters, new SqlExecutionOptions
        {
            MaxMaterializedRows = 10,
            MaxMaterializedBytes = 1024 * 1024,
        });

        Assert.Equal(new[] { "one", "two" }, normal.Rows.Select(row => Assert.IsType<string>(row[0])).Order());
        Assert.Equal(normal.Rows.Count, bounded.Rows.Count);
        for (int index = 0; index < normal.Rows.Count; index++)
        {
            Assert.Equal(normal.Rows[index], bounded.Rows[index]);
            using var json = JsonDocument.Parse(Assert.IsType<string>(bounded.Rows[index][1]));
            Assert.Equal(amount, json.RootElement.GetProperty("literal").GetDecimal());
            Assert.Equal(amount, json.RootElement.GetProperty("parameter").GetDecimal());
            Assert.Equal(amount + 0.01m, json.RootElement.GetProperty("expression").GetDecimal());
            Assert.Equal(-amount, json.RootElement.GetProperty("negative").GetDecimal());
            Assert.Equal(amount, json.RootElement.GetProperty("coalesced").GetDecimal());
        }
        Assert.False(bounded.Truncated);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        Assert.False(SqlRowRetentionBudget.HasExecutionBudget);

        Assert.Throws<InvalidOperationException>(() => Query(db, sql, parameters,
            new SqlExecutionOptions { MaxMaterializedRows = 1 }));
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        Assert.False(SqlRowRetentionBudget.HasExecutionBudget);
        Assert.Equal(2, Query(db, sql, parameters).Rows.Count);
    }

    /// <summary>动态函数的临时分配服从共享 SQL 内存预算，拒绝后不残留保留量。</summary>
    [Theory]
    [InlineData("json_value(document,CONCAT('$.','site'))")]
    [InlineData("json_object('value',id)")]
    public void Query_TemporaryMemoryLimit_RejectsAndReleases(string expression)
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        string source = Seed(db, "document");
        var error = Assert.Throws<InvalidOperationException>(() => Query(db,
            "SELECT " + expression + " FROM " + source, new SqlParameters(),
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 128 }));
        Assert.Contains("内存预算", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        Assert.Equal(2, Query(db, "SELECT " + expression + " FROM " + source, new SqlParameters()).Rows.Count);
    }

    /// <summary>动态路径只有读到本行才可校验；无效路径显式诊断且释放候选和内存。</summary>
    [Theory]
    [InlineData("document")]
    [InlineData("file")]
    [InlineData("vector")]
    public void Query_InvalidRowPath_ReportsPathAndReleasesBudget(string model)
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        string source = Seed(db, model);
        var error = Assert.Throws<InvalidOperationException>(() => Query(db,
            "SELECT json_value(document,id) FROM " + source, new SqlParameters(),
            new SqlExecutionOptions { MaxMaterializedRows = 10 }));
        Assert.Contains("JSON path 无效", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        Assert.False(SqlRowRetentionBudget.HasExecutionBudget);
    }

    /// <summary>时序投影、聚合及 JOIN 的旧标量分发也在对象边界保留 decimal。</summary>
    [Theory]
    [InlineData("SELECT json_object('amount',@amount) FROM readings")]
    [InlineData("SELECT json_object('amount',@amount,'count',COUNT(*)) FROM readings")]
    [InlineData("SELECT json_object('amount',@amount,'column',d.Amount) FROM readings r JOIN devices d ON r.host=d.Host")]
    public void Query_MeasurementScalarRoutes_PreserveExactObjectParameters(string sql)
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE MEASUREMENT readings(host TAG,value FIELD INT)");
        SqlExecutor.Execute(db, "INSERT INTO readings(time,host,value) VALUES(1,'a',1)");
        SqlExecutor.Execute(db, "CREATE TABLE devices(Host STRING,Amount DECIMAL,PRIMARY KEY(Host))");
        SqlExecutor.Execute(db, "INSERT INTO devices(Host,Amount) VALUES('a',DECIMAL '9007199254740993.01')");
        var result = Query(db, sql, new SqlParameters().AddNamed("amount", 9007199254740993.01m));
        using var json = JsonDocument.Parse(Assert.IsType<string>(Assert.Single(result.Rows)[0]));
        Assert.Equal(9007199254740993.01m, json.RootElement.GetProperty("amount").GetDecimal());
        if (sql.Contains("JOIN", StringComparison.Ordinal))
            Assert.Equal(9007199254740993.01m, json.RootElement.GetProperty("column").GetDecimal());
    }

    /// <summary>文档混合检索使用同一对象参数精度与按行路径求值。</summary>
    [Fact]
    public void Query_HybridSearch_PreservesExactObjectAndDynamicPath()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        _ = Seed(db, "document");
        SqlExecutor.Execute(db, "CREATE FULLTEXT INDEX FtSite ON docs('$.site') USING unicode");
        var result = Query(db,
            "SELECT json_object('amount',@amount),json_value(document,json_value(document,'$.path')) "
            + "FROM hybrid_search(source => docs,text_index => FtSite,text_field => '$.site',text => 'one',vector => [1,0,0],k => 1)",
            new SqlParameters().AddNamed("amount", 9007199254740993.01m));
        var row = Assert.Single(result.Rows);
        using var json = JsonDocument.Parse(Assert.IsType<string>(row[0]));
        Assert.Equal(9007199254740993.01m, json.RootElement.GetProperty("amount").GetDecimal());
        Assert.Equal("one", row[1]);
    }

    /// <summary>清理当前测试独占的数据库及输入文件。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Seed(Tsdb db, string model)
    {
        const string first = """{"id":"a","path":"$.site","site":"one","embedding":[1,0,0]}""";
        const string second = """{"id":"b","path":"$.nested.value","nested":{"value":"two"},"embedding":[2,0,0]}""";
        if (model == "file")
        {
            string file = Path.Combine(_root, "rows.json");
            File.WriteAllText(file, "[" + first + "," + second + "]");
            return "json_each('" + file.Replace("'", "''", StringComparison.Ordinal) + "')";
        }
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION docs");
        db.Documents.Open("docs").Insert("a", first);
        db.Documents.Open("docs").Insert("b", second);
        return model == "vector"
            ? "vector_search(source => docs, vector => [0,0,0], k => 2, metric => 'l2')" : "docs";
    }

    private static SelectExecutionResult Query(Tsdb db, string sql, SqlParameters parameters, SqlExecutionOptions? options = null)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, sql, parameters, null,
            options ?? SqlExecutionOptions.Default));
}
