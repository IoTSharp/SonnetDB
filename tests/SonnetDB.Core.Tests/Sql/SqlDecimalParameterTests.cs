using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 #235 的 Decimal 参数、原数字文本和实际精度边界。</summary>
public sealed class SqlDecimalParameterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-exact-decimal-" + Guid.NewGuid().ToString("N"));

    /// <summary>最小真实 INSERT 验证原问题数值不经过 Double。</summary>
    [Fact]
    public void Insert_IssueValue_PreservesAllSignificantDigits()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE amounts (id INT, value DECIMAL, PRIMARY KEY (id))");
        const decimal expected = 123456789.1234567890123456789m;
        SqlExecutor.Execute(db, "default", "INSERT INTO amounts (id, value) VALUES (1, @value)",
            new SqlParameters().AddNamed("value", expected));
        Assert.Equal(expected, Value(SqlExecutor.Execute(db, "SELECT value FROM amounts")));
        SqlExecutor.Execute(db, "INSERT INTO amounts (id, value) VALUES (2, -123456789.1234567890123456789)");
        Assert.Equal(-expected, Value(SqlExecutor.Execute(db, "SELECT value FROM amounts WHERE id = 2")));
    }

    /// <summary>正负、指数、尾零以及真实 96 位上界按数值精确性验收。</summary>
    [Theory]
    [InlineData("123456789.1234567890123456789")]
    [InlineData("-123456789.1234567890123456789")]
    [InlineData("9007199254740993.125")]
    [InlineData("0.1234567890123456789012345678")]
    [InlineData("7.9228162514264337593543950335")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335")]
    [InlineData("1.234567891234567890123456789e8")]
    [InlineData("123456789.123456789012345678900000")]
    public void Cast_RawDecimalContext_ParsesExactNumericValue(string text)
    {
        using var db = Open();
        decimal expected = decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        Assert.Equal(expected, Value(SqlExecutor.Execute(db, $"SELECT DECIMAL '{text}'")));
        // 无小数点且超 Int64 的整数仍按原 Int64 词法合同拒绝；typed Decimal 支持全部实际值域。
        if (text.IndexOfAny(['.', 'e', 'E']) >= 0)
            Assert.Equal(expected, Value(SqlExecutor.Execute(db, $"SELECT CAST({text} AS DECIMAL)")));
    }

    /// <summary>不可表示的有效位、下溢及溢出必须拒绝，不能悄悄舍入。</summary>
    [Theory]
    [InlineData("0.12345678901234567890123456789")]
    [InlineData("1e-29")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("1,23")]
    public void Parse_InexactDecimal_Rejects(string text)
        => Assert.Throws<InvalidOperationException>(() => SqlParser.Parse($"SELECT DECIMAL '{text}'"));

    /// <summary>数值文本与表达式预算均有明确上界。</summary>
    [Fact]
    public void Parse_OverBudgetDecimal_Rejects()
        => Assert.Throws<InvalidOperationException>(() => SqlParser.Parse("SELECT DECIMAL '" + new string('0', 1025) + "'"));

    /// <summary>参数绑定保持缓存 AST 隔离，并保留 Decimal、Double 与高位 Int64 的类型。</summary>
    [Fact]
    public void Bind_NumericParameters_PreservesTypesAndCachedPlaceholder()
    {
        const string sql = "SELECT @value";
        var parsed = Assert.IsType<SelectStatement>(SqlParser.Parse(sql));
        var first = Assert.IsType<SelectStatement>(SqlParameterBinder.Bind(parsed, new SqlParameters().AddNamed("value", decimal.MaxValue)));
        var second = Assert.IsType<SelectStatement>(SqlParameterBinder.Bind(parsed, new SqlParameters().AddNamed("value", 1.25d)));
        var third = Assert.IsType<SelectStatement>(SqlParameterBinder.Bind(parsed, new SqlParameters().AddNamed("value", 9007199254740993L)));
        var literal = Assert.IsType<LiteralExpression>(Assert.Single(first.Projections).Expression);
        Assert.Equal(SqlLiteralKind.Decimal, literal.Kind);
        Assert.Equal(decimal.MaxValue, literal.DecimalValue);
        Assert.Equal(SqlLiteralKind.Float, Assert.IsType<LiteralExpression>(Assert.Single(second.Projections).Expression).Kind);
        Assert.Equal(9007199254740993L, Assert.IsType<LiteralExpression>(Assert.Single(third.Projections).Expression).IntegerValue);
        Assert.IsType<ParameterExpression>(Assert.Single(parsed.Projections).Expression);
        Assert.Same(parsed, SqlParser.Parse(sql));
    }

    /// <summary>Decimal 列的原数字比较、IN 与索引候选不会把相邻金额折叠。</summary>
    [Fact]
    public void Query_AdjacentDecimals_PreservesComparisonAndNumericArithmetic()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE ledger (id INT, amount DECIMAL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_amount ON ledger (amount)");
        SqlExecutor.Execute(db, "INSERT INTO ledger (id, amount) VALUES (1, 9007199254740992.125), (2, 9007199254740993.125)");
        Assert.Equal(9007199254740993.125m, Value(SqlExecutor.Execute(db, "SELECT amount FROM ledger WHERE amount = 9007199254740993.125")));
        Assert.Equal(9007199254740993.125m, Value(SqlExecutor.Execute(db, "SELECT amount FROM ledger WHERE amount IN (9007199254740993.125)")));
        Assert.Equal(9007199254740993.225m, Value(SqlExecutor.Execute(db, "SELECT amount + 0.1 FROM ledger WHERE id = 2")));
        Assert.Equal(0.3m, Value(SqlExecutor.Execute(db, "SELECT DECIMAL '0.1' + 0.2")));
        var ordinary = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT 1.25"));
        Assert.IsType<double>(Assert.Single(ordinary.Rows)[0]);
    }

    /// <summary>批量中不可表示的金额必须在写入前拒绝。</summary>
    [Fact]
    public void Insert_InexactBatch_RejectsWithoutPartialRows()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE ledger (id INT, amount DECIMAL, PRIMARY KEY (id))");
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            "INSERT INTO ledger (id, amount) VALUES (1, 0.1), (2, 0.12345678901234567890123456789)"));
        Assert.Empty(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM ledger")).Rows);
    }

    /// <summary>时序写入、投影与关系维表 JOIN 保留已有浮点字段合同。</summary>
    [Fact]
    public void Execute_MeasurementDecimalParameter_PreservesExistingDoubleSemantics()
    {
        using var db = Open();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)");
        var parameters = new SqlParameters().AddNamed("value", 0.1m);
        SqlExecutor.Execute(db, null, "INSERT INTO cpu (time, host, value) VALUES (1, 'meter', @value)", parameters);
        var options = new SqlExecutionOptions { CancellationToken = deadline.Token };
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null,
            "SELECT value, @value + value FROM cpu WHERE value = @value", parameters, null, options));
        var row = Assert.Single(result.Rows);
        Assert.Equal(0.1d, Assert.IsType<double>(row[0]));
        Assert.Equal(0.2d, Assert.IsType<double>(row[1]));
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, name) VALUES (1, 'meter')");
        var joined = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null,
            "SELECT m.value FROM cpu m INNER JOIN devices d ON m.host = d.name WHERE m.value = @value", parameters, null, options));
        Assert.Equal(0.1d, Assert.IsType<double>(Assert.Single(joined.Rows)[0]));
    }

    /// <summary>文档、JSON 文件、向量及 Hybrid 搜索保留既有 Decimal 参数到 Double 的边界。</summary>
    [Fact]
    public void Execute_NonRelationalDecimalParameter_PreservesExistingDoubleSemantics()
    {
        using var db = Open();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION docs");
        SqlExecutor.Execute(db, "CREATE FULLTEXT INDEX ft_docs ON docs ('$.message')");
        db.Documents.Open("docs").Insert("d", """{"value":0.1,"message":"pump","embedding":[1,0]}""");
        var parameters = new SqlParameters().AddNamed("value", 0.1m).AddNamed("weight", 0.9m);
        var options = new SqlExecutionOptions { CancellationToken = deadline.Token };
        var document = Query("SELECT @value FROM docs WHERE json_value(document, '$.value') = @value");
        Assert.Equal(0.1d, Assert.IsType<double>(Assert.Single(document.Rows)[0]));
        string file = Path.Combine(_root, "numbers.json");
        File.WriteAllText(file, """[{"id":"d","value":0.1}]""");
        var json = Query($"SELECT @value FROM json_each('{file.Replace("'", "''", StringComparison.Ordinal)}') WHERE json_value(document, '$.value') = @value");
        Assert.Equal(0.1d, Assert.IsType<double>(Assert.Single(json.Rows)[0]));
        var vector = Query("SELECT @value FROM vector_search(source => docs, vector_field => '$.embedding', vector => [1,0], k => 1)");
        Assert.Equal(0.1d, Assert.IsType<double>(Assert.Single(vector.Rows)[0]));
        var hybrid = Query("SELECT @value FROM hybrid_search(source => docs, text_index => ft_docs, text => 'pump', vector_field => '$.embedding', vector => [1,0], k => 1, text_weight => @value, vector_weight => @weight)");
        Assert.Equal(0.1d, Assert.IsType<double>(Assert.Single(hybrid.Rows)[0]));

        SelectExecutionResult Query(string sql)
            => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, sql, parameters, null, options));
    }

    /// <summary>Decimal 与 Double 的动态 UDF、嵌套算术和比较均按每行一次执行。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_DynamicNumericOperand_InvokesEachUdfOncePerRow(bool decimalResult)
    {
        using var db = Open();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        SqlExecutor.Execute(db, "CREATE TABLE ledger (id INT, amount DECIMAL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO ledger (id, amount) VALUES (1, 1.0), (2, 1.0)");
        int called = 0;
        db.Functions.RegisterScalar("observe_number", args =>
        {
            called++;
            return decimalResult ? args[0] : Convert.ToDouble(args[0], CultureInfo.InvariantCulture);
        }, 1, 1);
        string[] statements =
        [
            "SELECT a.id FROM ledger a INNER JOIN ledger b ON a.id = b.id WHERE observe_number(a.amount) = b.amount",
            "SELECT observe_number(a.amount) + 0.1 FROM ledger a INNER JOIN ledger b ON a.id = b.id",
            "SELECT (observe_number(a.amount) + 0.1) + b.amount FROM ledger a INNER JOIN ledger b ON a.id = b.id",
            "SELECT a.id FROM ledger a INNER JOIN ledger b ON a.id = b.id WHERE (observe_number(a.amount) + 0.1) = b.amount + 0.1",
        ];
        for (int index = 0; index < statements.Length && index < 4; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            called = 0;
            var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, statements[index], null,
                null, new SqlExecutionOptions { CancellationToken = deadline.Token }));
            Assert.Equal(2, result.Rows.Count);
            Assert.Equal(2, called);
        }
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    private static decimal Value(object? result)
        => Assert.IsType<decimal>(Assert.Single(Assert.IsType<SelectExecutionResult>(result).Rows)[0]);

    /// <summary>回收本测试创建的独占数据库目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
