using System.Text.Json;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>#249：动态 JSON 字段、数组下标与类型安全对象谓词的 SQL 集成合同。</summary>
public sealed class SqlDynamicJsonFunctionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sql-dynamic-json-" + Guid.NewGuid().ToString("N"));

    /// <summary>清理本测试独占的数据目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>同一查询逐行选择路径，不把首行动态 path 错误缓存到后续行。</summary>
    [Fact]
    public void Query_PerRowPathAndExpression_PreserveEachRowsSelection()
    {
        using var database = Open();
        Seed(database);
        var rows = Query(database, "SELECT id,json_value(payload,path_text) FROM samples ORDER BY id").Rows;
        Assert.Equal(new object?[] { "west", "two", null, null }, rows.Select(row => row[1]));
        Assert.Equal(2L, Assert.Single(Query(database,
            "SELECT id FROM samples WHERE json_value(payload,path_text)='two'").Rows)[0]);
        Assert.Equal("west", Assert.Single(Query(database,
            "SELECT json_value(payload,CONCAT('$.','site')) FROM samples WHERE id=1").Rows)[0]);
    }

    /// <summary>数组下标由本行表达式计算，多层字段与转义字段保持原来的显式 path 语义。</summary>
    [Fact]
    public void Query_DynamicArrayAndEscapedField_UsesEvaluatedPath()
    {
        using var database = Open();
        Seed(database);
        Assert.Equal(new object?[] { "one", "alpha" }, Query(database,
            "SELECT json_value(payload,CONCAT('$.items[',item_index,']')) FROM samples WHERE id<3 ORDER BY id").Rows.Select(row => row[0]));
        Assert.Equal("escaped", Assert.Single(Query(database,
            "SELECT json_value(payload,CONCAT('$[''',REPLACE(field_name,'''',''''''),''']')) FROM samples WHERE id=2").Rows)[0]);
    }

    /// <summary>动态取值保持静态取值的 Int64/Double/Bool/字符串类型，NULL 不伪装成空字符串。</summary>
    [Theory]
    [InlineData("$.integer")]
    [InlineData("$.fraction")]
    [InlineData("$.active")]
    [InlineData("$.nil")]
    [InlineData("$.missing")]
    public void Query_StaticAndDynamicPaths_ReturnIdenticalValuesAndClrTypes(string path)
    {
        using var database = Open();
        Seed(database);
        SqlExecutor.Execute(database, databaseName: null, "UPDATE samples SET path_text=@path WHERE id=1",
            new SqlParameters().AddNamed("path", path), controlPlane: null);
        var result = Query(database, "SELECT json_value(payload,@path),json_value(payload,path_text) FROM samples WHERE id=1",
            new SqlParameters().AddNamed("path", path));
        Assert.Equal(result.Rows[0][0], result.Rows[0][1]);
        Assert.Equal(result.Rows[0][0]?.GetType(), result.Rows[0][1]?.GetType());
    }

    /// <summary>对象谓词使用参数或 SQL 列作为字段和值，保持相邻高精度 decimal、整数及 null。</summary>
    [Fact]
    public void Query_ObjectPredicate_PreservesTypedColumnAndParameterValues()
    {
        using var database = Open();
        Seed(database);
        var parameters = new SqlParameters().AddNamed("key", "amount").AddNamed("value", 1234567890123456.123456789012m);
        Assert.Equal(true, Query(database,
            "SELECT json_contains(payload,'$.objects',json_object(@key,@value)) FROM samples WHERE id=1", parameters).Rows[0][0]);
        parameters = new SqlParameters().AddNamed("key", "amount").AddNamed("value", 1234567890123456.123456789013m);
        Assert.Equal(false, Query(database,
            "SELECT json_contains(payload,'$.objects',json_object(@key,@value)) FROM samples WHERE id=1", parameters).Rows[0][0]);
        Assert.Equal(true, Query(database,
            "SELECT json_contains(payload,'$.objects',json_object('kind',wanted,'amount',amount,'active',true,'integer',9007199254740993,'nil',NULL)) FROM samples WHERE id=1").Rows[0][0]);
    }

    /// <summary>JSON_OBJECT 的字符串不被当作原始 JSON 拼接，结果可作为 JSON 列写入并在重开后读取。</summary>
    [Fact]
    public void Dml_ObjectConstruction_PreservesEscapingAndReopen()
    {
        const string value = "'\"\\\n中文{\"active\":true}";
        using (var database = Open())
        {
            SqlExecutor.Execute(database, "CREATE TABLE objects(id INT,payload JSON,PRIMARY KEY(id))");
            SqlExecutor.Execute(database, databaseName: null,
                "INSERT INTO objects(id,payload) VALUES(1,json_object(@name,@value,'nil',NULL))",
                new SqlParameters().AddNamed("name", "quote'key").AddNamed("value", value), controlPlane: null);
        }
        using var reopened = Open();
        string json = Assert.IsType<string>(Query(reopened, "SELECT payload FROM objects").Rows[0][0]);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(value, document.RootElement.GetProperty("quote'key").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("nil").ValueKind);
    }

    /// <summary>坏动态路径报路径诊断；空结果中的对象构造仍声明为字符串，避免依赖首行元数据。</summary>
    [Fact]
    public void Query_InvalidPathAndEmptyObjectMetadata_AreDeterministic()
    {
        using var database = Open();
        Seed(database);
        SqlExecutor.Execute(database, "UPDATE samples SET path_text='$[-1]' WHERE id=1");
        var exception = Assert.Throws<InvalidOperationException>(() => Query(database,
            "SELECT json_value(payload,path_text) FROM samples WHERE id=1"));
        Assert.Contains("JSON path 无效", exception.Message, StringComparison.Ordinal);
        var empty = Query(database, "SELECT json_object('kind',wanted) FROM samples WHERE id<0");
        Assert.Empty(empty.Rows);
        Assert.Equal(SonnetDB.Tables.TableColumnType.String, Assert.Single(empty.ColumnInfo!).DataType);
    }

    /// <summary>静态/参数化 path、既有数组包含和 JSON 索引在新动态合同加入后仍返回正确值。</summary>
    [Fact]
    public void Query_ExistingJsonIndexAndContains_KeepStaticBehavior()
    {
        using var database = Open();
        Seed(database);
        SqlExecutor.Execute(database, "CREATE JSON INDEX ix_site ON samples(payload,'$.site')");
        Assert.Equal(1L, Assert.Single(Query(database,
            "SELECT id FROM samples WHERE json_value(payload,@path)=@value AND json_contains(payload,'$.items','one')",
            new SqlParameters().AddNamed("path", "$.site").AddNamed("value", "west")).Rows)[0]);
        var staticPlan = Query(database, "EXPLAIN SELECT id FROM samples WHERE json_value(payload,@path)=@value",
            new SqlParameters().AddNamed("path", "$.site").AddNamed("value", "west"))
            .Rows.ToDictionary(row => (string)row[0]!, row => row[1], StringComparer.Ordinal);
        Assert.Equal("json_path_index", staticPlan["access_path"]);
        Assert.Equal("ix_site", staticPlan["index_name"]);
        var dynamicPlan = Query(database, "EXPLAIN SELECT id FROM samples WHERE json_value(payload,path_text)='west'")
            .Rows.ToDictionary(row => (string)row[0]!, row => row[1], StringComparer.Ordinal);
        Assert.NotEqual("json_path_index", dynamicPlan["access_path"]);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    /// <summary>派生表、JOIN、无 FROM 与 UPDATE 使用同一组动态 JSON 标量，不依赖单表快速路径。</summary>
    [Fact]
    public void Query_RelationalRoutes_PreserveDynamicValuesAndExactObjects()
    {
        using var database = Open();
        Seed(database);
        Assert.Equal("two", Assert.Single(Query(database,
            "SELECT json_value(s.payload,s.path_text) FROM samples s JOIN samples t ON s.id=t.id WHERE s.id=2").Rows)[0]);
        Assert.Equal("west", Assert.Single(Query(database,
            "SELECT selected FROM (SELECT json_value(payload,path_text) AS selected FROM samples WHERE id=1) q").Rows)[0]);
        Assert.Equal(9007199254740993L, Assert.Single(Query(database,
            "SELECT json_value(json_object('number',9007199254740993),CONCAT('$.','number'))").Rows)[0]);
        string json = Assert.IsType<string>(Assert.Single(Query(database,
            "SELECT json_object(@key,@amount)", new SqlParameters().AddNamed("key", "amount")
                .AddNamed("amount", 9007199254740993.01m)).Rows)[0]);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(9007199254740993.01m, parsed.RootElement.GetProperty("amount").GetDecimal());
        SqlExecutor.Execute(database,
            "UPDATE samples SET payload=json_object(field_name,wanted),path_text=CONCAT('$.',field_name) WHERE id=1");
        Assert.Equal("pump", Assert.Single(Query(database,
            "SELECT json_value(payload,path_text) FROM samples WHERE id=1").Rows)[0]);
    }

    private static SelectExecutionResult Query(Tsdb database, string sql, SqlParameters? parameters = null)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, databaseName: null, sql,
            parameters ?? new SqlParameters(), controlPlane: null));

    private static void Seed(Tsdb database)
    {
        SqlExecutor.Execute(database, "CREATE TABLE samples(id INT,payload JSON NULL,path_text STRING NULL,field_name STRING,item_index INT,wanted STRING,amount DECIMAL(28,12),PRIMARY KEY(id))");
        SqlExecutor.Execute(database, """
            INSERT INTO samples(id,payload,path_text,field_name,item_index,wanted,amount) VALUES
            (1,'{"site":"west","integer":9007199254740993,"fraction":1.25,"active":true,"nil":null,"items":["zero","one"],"objects":[{"kind":"pump","amount":1234567890123456.123456789012,"integer":9007199254740993,"active":true,"nil":null}]}','$.site','site',1,'pump',CAST('1234567890123456.123456789012' AS DECIMAL)),
            (2,'{"site":"east","nested":{"value":"two"},"quote''key":"escaped","items":["alpha","beta"]}','$.nested.value','quote''key',0,'fan',CAST('0' AS DECIMAL)),
            (3,NULL,'$.site','site',0,'none',CAST('0' AS DECIMAL)),
            (4,'{"site":"four"}',NULL,'site',0,'none',CAST('0' AS DECIMAL))
            """);
    }
}
