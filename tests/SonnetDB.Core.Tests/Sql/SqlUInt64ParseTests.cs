using System.Buffers.Binary;
using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Query.Functions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 #260 的有界 UInt64 文本解析、精确 Decimal 声明与语句原子性。</summary>
public sealed class SqlUInt64ParseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "u64-" + Guid.NewGuid().ToString("N"));

    /// <summary>常量和参数覆盖完整 UInt64 值域，并返回 scale 为零的精确 Decimal。</summary>
    [Theory]
    [InlineData("0", "0")]
    [InlineData("1", "1")]
    [InlineData("9007199254740993", "9007199254740993")]
    [InlineData("9223372036854775807", "9223372036854775807")]
    [InlineData("9223372036854775808", "9223372036854775808")]
    [InlineData("18446744073709551614", "18446744073709551614")]
    [InlineData("18446744073709551615", "18446744073709551615")]
    [InlineData("+18446744073709551615", "18446744073709551615")]
    [InlineData("00000000000000000000018446744073709551615", "18446744073709551615")]
    [InlineData(" \t\r\n\v\f+00042\f\v\n\r\t ", "42")]
    public void ParseUInt64_LiteralsAndParameters_ReturnExactDecimal(string text, string expectedText)
    {
        using var db = Open();
        decimal expected = decimal.Parse(expectedText, CultureInfo.InvariantCulture);
        foreach (var result in new[]
        {
            Query(db, "SELECT parse_uint64(@text)", new SqlParameters().AddNamed("text", text)),
            Query(db, "SELECT PARSE_UINT64('" + text.Replace("'", "''", StringComparison.Ordinal) + "')"),
        })
        {
            decimal value = Assert.IsType<decimal>(Assert.Single(result.Rows)[0]);
            Assert.Equal(expected, value);
            Assert.Equal(0, decimal.GetBits(value)[3] & 0x00FF0000);
            AssertDeclaration(Assert.Single(result.ColumnInfo!), false);
        }
    }

    /// <summary>拒绝负号、小数、指数、分隔符、非 ASCII 字符、嵌入空白及溢出。</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("-0")]
    [InlineData("-1")]
    [InlineData("-18446744073709551615")]
    [InlineData("2.9")]
    [InlineData("2.0")]
    [InlineData(".0")]
    [InlineData("1.")]
    [InlineData("1e2")]
    [InlineData("1E+2")]
    [InlineData("1,000")]
    [InlineData("1_000")]
    [InlineData("0xFF")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("18446744073709551616")]
    [InlineData("99999999999999999999999999999")]
    [InlineData("++1")]
    [InlineData("+ 1")]
    [InlineData("1 2")]
    [InlineData("1\t2")]
    [InlineData("１")]
    [InlineData("١")]
    [InlineData("\u00a01")]
    [InlineData("1\u2003")]
    [InlineData("1\0")]
    [InlineData("1\0 ")]
    [InlineData("1'2")]
    public void ParseUInt64_InvalidText_RejectsConstantsAndParameters(string text)
    {
        using var db = Open();
        var parameters = new SqlParameters().AddNamed("text", text);
        var parameterError = Assert.Throws<InvalidOperationException>(() => Query(db, "SELECT parse_uint64(@text)", parameters));
        Assert.Contains("parse_uint64", parameterError.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Query(db,
            "SELECT parse_uint64('" + text.Replace("'", "''", StringComparison.Ordinal) + "')"));
    }

    /// <summary>只接受字符串或 NULL，不隐式接受数值、日期、布尔和二进制值。</summary>
    [Fact]
    public void ParseUInt64_TypeAndArity_RejectsImplicitNumericConversions()
    {
        Assert.True(FunctionRegistry.TryGetScalar("parse_uint64", out var parser));
        Assert.Null(parser.Evaluate([null]));
        Assert.Null(parser.Evaluate([DBNull.Value]));
        foreach (object value in new object[] { 1L, 1m, 1d, 1f, ulong.MaxValue, true, DateTime.UnixEpoch, new byte[] { 49 } })
            Assert.Throws<InvalidOperationException>(() => parser.Evaluate([value]));
        Assert.Throws<InvalidOperationException>(() => parser.Evaluate([]));
        Assert.Throws<InvalidOperationException>(() => parser.Evaluate(["1", "2"]));
        using var db = Open();
        foreach (string sql in new[] { "SELECT parse_uint64()", "SELECT parse_uint64('1','2')", "SELECT parse_uint64(1)", "SELECT parse_uint64(DECIMAL '1')", "SELECT parse_uint64(1.0)", "SELECT parse_uint64(*)" })
            Assert.Throws<InvalidOperationException>(() => Query(db, sql));
    }

    /// <summary>1024 字符包含符号和空白；边界内前导零精确保留，1025 字符明确拒绝。</summary>
    [Fact]
    public void ParseUInt64_LengthBudget_Accepts1024AndRejects1025()
    {
        Assert.True(FunctionRegistry.TryGetScalar("parse_uint64", out var parser));
        string zeros = new('0', 1024);
        Assert.Equal(0m, Assert.IsType<decimal>(parser.Evaluate([zeros])));
        string maximum = new string('0', 1004) + "18446744073709551615";
        Assert.Equal(18446744073709551615m, Assert.IsType<decimal>(parser.Evaluate([maximum])));
        Assert.Equal(42m, Assert.IsType<decimal>(parser.Evaluate([" " + new string('0', 1020) + "42 "])));
        foreach (string text in new[] { zeros + "0", maximum + "0", new string(' ', 1025) })
            Assert.Throws<InvalidOperationException>(() => parser.Evaluate([text]));
        using var db = Open();
        Assert.Equal(18446744073709551615m, Query(db, "SELECT parse_uint64(@text)", new SqlParameters().AddNamed("text", maximum)).Rows.Single()[0]);
        Assert.Throws<InvalidOperationException>(() => Query(db, "SELECT parse_uint64(@text)", new SqlParameters().AddNamed("text", zeros + "0")));
    }

    /// <summary>解析与当前区域无关，只接受明确约定的不变 ASCII 语法。</summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    public void ParseUInt64_CurrentCulture_DoesNotChangeGrammar(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.True(FunctionRegistry.TryGetScalar("parse_uint64", out var parser));
            Assert.Equal(18446744073709551615m, parser.Evaluate([" +18446744073709551615 "]));
            foreach (string invalid in new[] { "2,9", "١٢", "-0", "1\0" })
                Assert.Throws<InvalidOperationException>(() => parser.Evaluate([invalid]));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    /// <summary>固定种子的完整 64 位样本经十进制文本往返，不损失低位整数。</summary>
    [Fact]
    public void ParseUInt64_FullWidthSamples_RoundTripExactly()
    {
        Assert.True(FunctionRegistry.TryGetScalar("parse_uint64", out var parser));
        var random = new Random(260);
        Span<byte> buffer = stackalloc byte[8];
        for (int index = 0; index < 256; index++)
        {
            random.NextBytes(buffer);
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
            string text = value.ToString(CultureInfo.InvariantCulture);
            Assert.Equal((decimal)value, Assert.IsType<decimal>(parser.Evaluate([text])));
        }
    }

    /// <summary>空表、JOIN、CTE、视图和全 NULL 结果保持 DECIMAL(20,0) 及输入可空性。</summary>
    [Fact]
    public void ParseUInt64_EmptyAndNullProjections_PreserveDeclaredMetadata()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE text_values(id INT, text STRING NULL, required STRING NOT NULL, PRIMARY KEY(id))");
        foreach (string sql in new[]
        {
            "SELECT parse_uint64(text),parse_uint64(required) FROM text_values",
            "SELECT parse_uint64(a.text),parse_uint64(a.required) FROM text_values a INNER JOIN text_values b ON a.id=b.id",
            "WITH parsed AS (SELECT parse_uint64(text) AS optional,parse_uint64(required) AS required FROM text_values) SELECT optional,required FROM parsed",
        })
        {
            var result = Query(db, sql);
            Assert.Empty(result.Rows);
            AssertDeclaration(result.ColumnInfo![0], true);
            AssertDeclaration(result.ColumnInfo[1], false);
        }
        SqlExecutor.Execute(db, "CREATE VIEW parsed_values AS SELECT parse_uint64(text) AS parsed FROM text_values");
        AssertDeclaration(Query(db, "SELECT parsed FROM parsed_values").ColumnInfo![0], true);
        SqlExecutor.Execute(db, "INSERT INTO text_values(id,text,required) VALUES(1,NULL,'0')");
        var allNull = Query(db, "SELECT parse_uint64(text) FROM text_values");
        Assert.Null(Assert.Single(allNull.Rows)[0]);
        AssertDeclaration(allNull.ColumnInfo![0], true);
        foreach (var result in new[] { Query(db, "SELECT parse_uint64(NULL)"), Query(db, "SELECT parse_uint64(@text)", new SqlParameters().AddNamed("text", null)) })
        {
            Assert.Null(Assert.Single(result.Rows)[0]);
            AssertDeclaration(result.ColumnInfo![0], true);
        }
    }

    /// <summary>后续无效行不发布前序 INSERT/UPDATE，合法列解析、谓词及重开精确保留。</summary>
    [Fact]
    public void ParseUInt64_WritesAndReopen_PreserveStatementAtomicityAndPersistence()
    {
        using (var db = Open())
        {
            SqlExecutor.Execute(db, "CREATE TABLE parsed(id INT, text STRING NULL, value DECIMAL(20,0), PRIMARY KEY(id))");
            Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
                "INSERT INTO parsed(id,text,value) VALUES(1,'18446744073709551615',parse_uint64('18446744073709551615')),(2,'2.9',parse_uint64('2.9'))"));
            Assert.Empty(Query(db, "SELECT * FROM parsed").Rows);
            SqlExecutor.Execute(db, "INSERT INTO parsed(id,text,value) VALUES(1,'18446744073709551615',11),(2,'2.9',22),(3,NULL,NULL)");
            Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, "UPDATE parsed SET value=parse_uint64(text)"));
            Assert.Equal(new object?[] { 11m, 22m, null }, Query(db, "SELECT value FROM parsed ORDER BY id").Rows.Select(row => row[0]).ToArray());
            SqlExecutor.Execute(db, "UPDATE parsed SET text='9223372036854775808' WHERE id=2");
            SqlExecutor.Execute(db, "UPDATE parsed SET value=parse_uint64(text)");
            var parameters = new SqlParameters().AddNamed("text", "18446744073709551615");
            Assert.Equal(1L, Query(db, "SELECT id FROM parsed WHERE value=parse_uint64(@text)", parameters).Rows.Single()[0]);
            Assert.Equal(18446744073709551615m, Query(db, "SELECT parse_uint64(a.text) FROM parsed a INNER JOIN parsed b ON a.id=b.id WHERE a.id=1").Rows.Single()[0]);
            Assert.Equal(18446744073709551616m, Query(db, "SELECT parse_uint64(text)+DECIMAL '1' FROM parsed WHERE id=1").Rows.Single()[0]);
        }
        using var reopened = Open();
        Assert.Equal(new object?[] { 18446744073709551615m, 9223372036854775808m, null },
            Query(reopened, "SELECT value FROM parsed ORDER BY id").Rows.Select(row => row[0]).ToArray());
    }

    /// <summary>同名 UDF 覆盖不能被声明为原生 Decimal，也不重复执行输入表达式。</summary>
    [Fact]
    public void ParseUInt64_UserFunctions_DoNotMisdeclareOrReevaluate()
    {
        using var db = Open();
        int calls = 0;
        db.Functions.RegisterScalar("observe_u64_text", args => { calls++; return args[0]; }, 1, 1);
        Assert.Equal(18446744073709551615m, Query(db, "SELECT parse_uint64(observe_u64_text('18446744073709551615'))").Rows.Single()[0]);
        Assert.Equal(1, calls);
        db.Functions.RegisterScalar("parse_uint64", static _ => "override", 1, 1);
        SqlExecutor.Execute(db, "CREATE TABLE texts(id INT, text STRING, PRIMARY KEY(id))");
        foreach (string sql in new[] { "SELECT parse_uint64(text) FROM texts", "SELECT parse_uint64(a.text) FROM texts a INNER JOIN texts b ON a.id=b.id" })
        {
            var column = Query(db, sql).ColumnInfo![0];
            Assert.Null(column.DataType);
            Assert.Null(column.NumericPrecision);
            Assert.Null(column.NumericScale);
        }
        Assert.Equal("override", Query(db, "SELECT parse_uint64('2.9')").Rows.Single()[0]);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root, AllowUserFunctions = true });

    private static SelectExecutionResult Query(Tsdb db, string sql, SqlParameters? parameters = null)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, sql, parameters));

    private static void AssertDeclaration(SelectColumnInfo column, bool nullable)
    {
        Assert.Equal(TableColumnType.Decimal, column.DataType);
        Assert.Equal((byte)20, column.NumericPrecision);
        Assert.Equal((byte)0, column.NumericScale);
        Assert.Equal(nullable, column.IsNullable);
        Assert.False(column.IsKey);
        Assert.False(column.IsAutoIncrement);
        Assert.False(column.IsRowVersion);
    }

    /// <summary>回收当前测试独占的数据库目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
