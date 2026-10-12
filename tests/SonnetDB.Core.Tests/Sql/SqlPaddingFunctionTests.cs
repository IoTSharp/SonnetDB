using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Query.Functions;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 GH-Issue #220 的 UTF-16 字符串填充、输出预算、参数和声明元数据。</summary>
public sealed class SqlPaddingFunctionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-sql-padding-" + Guid.NewGuid().ToString("N"));

    /// <summary>创建本测试独占的数据库目录。</summary>
    public SqlPaddingFunctionTests() => Directory.CreateDirectory(_root);

    /// <summary>回收本测试创建的独占目录。</summary>
    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>默认空格、循环填充、空文本和较小宽度使用固定的 UTF-16 合同。</summary>
    [Theory]
    [InlineData("lpad('7', 4, '0')", "0007")]
    [InlineData("rpad('7', 4, '0')", "7000")]
    [InlineData("LPAD('7', 4)", "   7")]
    [InlineData("RPAD('7', 4)", "7   ")]
    [InlineData("lpad('7', 6, 'ab')", "ababa7")]
    [InlineData("rpad('7', 6, 'ab')", "7ababa")]
    [InlineData("lpad('7', 3, 'abcdef')", "ab7")]
    [InlineData("rpad('7', 3, 'abcdef')", "7ab")]
    [InlineData("lpad('abcd', 2, '0')", "abcd")]
    [InlineData("rpad('abcd', 0, '0')", "abcd")]
    [InlineData("lpad('abcd', 4, '0')", "abcd")]
    [InlineData("lpad('', 3, 'ab')", "aba")]
    [InlineData("rpad('', 3)", "   ")]
    [InlineData("lpad('', 0)", "")]
    [InlineData("lpad('😀', 4, '界')", "界界😀")]
    [InlineData("rpad('😀', 3, '界')", "😀界")]
    [InlineData("lpad('é', 3, '0')", "0é")]
    [InlineData("rpad('7', 4, '界A')", "7界A界")]
    [InlineData("lpad('7', 3, '😀')", "😀7")]
    [InlineData("rpad('7', 5, '😀')", "7😀😀")]
    public void Select_WidthAndPadding_ReturnsExpectedText(string expression, string expected)
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        Assert.Equal(expected, Assert.IsType<string>(Select(database, "SELECT " + expression).Rows.Single()[0]));
    }

    /// <summary>宽度必须是非负整数；空填充和超过结果预算的调用明确拒绝。</summary>
    [Theory]
    [InlineData("SELECT lpad('7', -1)")]
    [InlineData("SELECT rpad('7', 1048577)")]
    [InlineData("SELECT lpad('7', 9223372036854775807, '0')")]
    [InlineData("SELECT lpad('7', 4, '')")]
    [InlineData("SELECT rpad('abcd', 2, '')")]
    [InlineData("SELECT lpad('7', 4.0)")]
    [InlineData("SELECT rpad('7', CAST('4' AS DECIMAL))")]
    [InlineData("SELECT lpad('7', '4')")]
    [InlineData("SELECT rpad('7', true)")]
    [InlineData("SELECT lpad(7, 4)")]
    [InlineData("SELECT rpad('7', 4, 0)")]
    [InlineData("SELECT lpad('7')")]
    [InlineData("SELECT rpad('7', 4, '0', 'x')")]
    [InlineData("SELECT lpad('7', 2, '😀')")]
    [InlineData("SELECT rpad('7', 2, '😀')")]
    [InlineData("SELECT lpad('7', 4, '😀')")]
    [InlineData("SELECT rpad('7', 4, '😀')")]
    public void Select_InvalidArguments_ThrowsExecutionError(string sql)
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database, sql));
    }

    /// <summary>任一 NULL 参数传播，在其它参数无效时也不分配填充结果。</summary>
    [Theory]
    [InlineData("lpad(NULL, 4)")]
    [InlineData("rpad('7', NULL)")]
    [InlineData("lpad('7', 4, NULL)")]
    [InlineData("rpad(NULL, -1, '')")]
    [InlineData("lpad(7, NULL, '')")]
    [InlineData("rpad('7', -1, NULL)")]
    public void Select_NullArgument_ReturnsNull(string expression)
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        Assert.Null(Select(database, "SELECT " + expression).Rows.Single()[0]);
    }

    /// <summary>最大输出实际可用；超限宽度、原串或 UInt64 在分配结果前被拒绝。</summary>
    [Theory]
    [InlineData("lpad")]
    [InlineData("rpad")]
    public void Evaluate_OutputBudget_EnforcesActualResultLength(string name)
    {
        Assert.True(FunctionRegistry.TryGetScalar(name, out var function));
        var result = Assert.IsType<string>(function.Evaluate(["7", 1_048_576L, "xy"]));
        Assert.Equal(1_048_576, result.Length);
        Assert.Equal(name == "lpad" ? "xyxy" : "7xyx", result[..4]);
        Assert.Equal(name == "lpad" ? '7' : 'x', result[^1]);
        Assert.Throws<InvalidOperationException>(() => function.Evaluate(["7", 1_048_577L]));
        Assert.Throws<InvalidOperationException>(() => function.Evaluate([new string('a', 1_048_577), 0L]));
        Assert.Throws<InvalidOperationException>(() => function.Evaluate(["7", ulong.MaxValue]));
        Assert.Equal(name == "lpad" ? " 7" : "7 ", function.Evaluate(["7", (byte)2]));
        Assert.Throws<InvalidOperationException>(() => function.Evaluate(["\uD800", 1L]));
        Assert.Throws<InvalidOperationException>(() => function.Evaluate(["7", 2L, "\uD800"]));
        Assert.Throws<InvalidOperationException>(() => function.Evaluate(["7", 2L, "\uDC00"]));
        Assert.Equal(name == "lpad" ? "x7" : "7x", function.Evaluate(["7", 2L, "x\uD800"]));
    }

    /// <summary>参数化 ADO 的空表、空 JOIN 与全 NULL 结果声明 STRING，谓词保持原值。</summary>
    [Fact]
    public void Ado_SelectWhereAndParameters_PreservesTextAndMetadata()
    {
        using var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE codes (id INT, value STRING, PRIMARY KEY(id))";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT lpad(value, 4) AS left_code, rpad(value, 4) AS right_code FROM codes";
        using (var empty = command.ExecuteReader())
        {
            Assert.Equal(typeof(string), empty.GetFieldType(0));
            Assert.Equal(typeof(string), empty.GetFieldType(1));
            Assert.False(empty.Read());
        }
        command.CommandText = "SELECT lpad(a.value, 4), rpad(b.value, 4) FROM codes a INNER JOIN codes b ON a.id=b.id";
        using (var emptyJoin = command.ExecuteReader())
        {
            Assert.Equal(typeof(string), emptyJoin.GetFieldType(0));
            Assert.Equal(typeof(string), emptyJoin.GetFieldType(1));
            Assert.False(emptyJoin.Read());
        }
        command.CommandText = "INSERT INTO codes(id,value) VALUES(1,'7'),(2,NULL),(3,'abcdef')";
        Assert.Equal(3, command.ExecuteNonQuery());
        var width = command.CreateParameter();
        width.ParameterName = "@width";
        width.Value = 4L;
        command.Parameters.Add(width);
        var padding = command.CreateParameter();
        padding.ParameterName = "@padding";
        padding.Value = "0";
        command.Parameters.Add(padding);
        command.CommandText = "SELECT lpad(value,@width,@padding), rpad(value,@width,@padding) FROM codes WHERE lpad(value,@width,@padding)='0007'";
        using (var row = command.ExecuteReader())
        {
            Assert.True(row.Read());
            Assert.Equal("0007", row.GetString(0));
            Assert.Equal("7000", row.GetString(1));
            Assert.False(row.Read());
        }
        command.CommandText = "SELECT lpad(value,@width,@padding), rpad(value,@width,@padding) FROM codes WHERE value IS NULL";
        using (var missing = command.ExecuteReader())
        {
            Assert.Equal(typeof(string), missing.GetFieldType(0));
            Assert.Equal(typeof(string), missing.GetFieldType(1));
            Assert.True(missing.Read());
            Assert.True(missing.IsDBNull(0));
            Assert.True(missing.IsDBNull(1));
        }
        command.CommandText = "SELECT lpad(value,@width,@padding) FROM codes WHERE id=3";
        Assert.Equal("abcdef", command.ExecuteScalar());
    }

    /// <summary>时序与文档入口共用原生 UTF-16 填充行为。</summary>
    [Fact]
    public void Select_MeasurementAndDocument_UsesSharedPaddingFunctions()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE MEASUREMENT metrics (host TAG, value FIELD INT)");
        SqlExecutor.Execute(database, "INSERT INTO metrics(time,host,value) VALUES(1000,'7',1)");
        Assert.Equal("0007", Select(database, "SELECT lpad(host,4,'0') FROM metrics").Rows.Single()[0]);
        SqlExecutor.Execute(database, "CREATE DOCUMENT COLLECTION notes");
        SqlExecutor.Execute(database, "INSERT INTO notes(id,document) VALUES('n1','{\"code\":\"7\"}')");
        Assert.Equal("7000", Select(database,
            "SELECT rpad(json_value(document,'$.code'),4,'0') FROM notes").Rows.Single()[0]);
    }

    /// <summary>用户覆盖内置函数时，空表与空 JOIN 不得声明内置 STRING 类型。</summary>
    [Theory]
    [InlineData("lpad")]
    [InlineData("rpad")]
    public void Select_UserFunctionOverride_PreservesUnknownDeclaredType(string name)
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root, AllowUserFunctions = true });
        database.Functions.RegisterScalar(name, static _ => 42L, 2, 3);
        SqlExecutor.Execute(database, "CREATE TABLE codes (id INT, value STRING, PRIMARY KEY(id))");
        var simple = Select(database, $"SELECT {name}(value,4) FROM codes");
        Assert.NotNull(simple.ColumnInfo);
        Assert.Null(simple.ColumnInfo[0].DataType);
        var join = Select(database, $"SELECT {name}(a.value,4) FROM codes a INNER JOIN codes b ON a.id=b.id");
        Assert.NotNull(join.ColumnInfo);
        Assert.Null(join.ColumnInfo[0].DataType);
    }

    private static SelectExecutionResult Select(Tsdb database, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql));
}
