using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Query.Functions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 #314 的精确 DECIMAL 舍入、声明及关系表达式合同。</summary>
public sealed class SqlDecimalRoundTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-decimal-round-" + Guid.NewGuid().ToString("N"));

    /// <summary>舍入使用十进制及 ToEven，包括大整数、正负中点和 Decimal 完整值域。</summary>
    [Theory]
    [InlineData("9007199254740993.125", 2, "9007199254740993.12")]
    [InlineData("-9007199254740993.125", 2, "-9007199254740993.12")]
    [InlineData("1.015", 2, "1.02")]
    [InlineData("-1.015", 2, "-1.02")]
    [InlineData("2.5", 0, "2")]
    [InlineData("3.5", 0, "4")]
    [InlineData("-2.5", 0, "-2")]
    [InlineData("-3.5", 0, "-4")]
    [InlineData("1.1234567890123456789012345678", 20, "1.12345678901234567890")]
    [InlineData("1.1234567890123456789012345678", 28, "1.1234567890123456789012345678")]
    [InlineData("0.0000000000000000000000000001", 28, "0.0000000000000000000000000001")]
    [InlineData("79228162514264337593543950335", 0, "79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335", 28, "-79228162514264337593543950335")]
    public void Round_DecimalParameterAndLiteral_PreservesExactToEvenResult(string text, int digits, string rounded)
    {
        using var db = Open();
        decimal value = decimal.Parse(text, CultureInfo.InvariantCulture);
        decimal expected = decimal.Parse(rounded, CultureInfo.InvariantCulture);
        var parameters = new SqlParameters().AddNamed("value", value).AddNamed("digits", digits);
        var parameterResult = Query(db, "SELECT round(@value, @digits), round(CAST(@value AS DECIMAL), @digits)", parameters);
        Assert.All(Assert.Single(parameterResult.Rows), actual => Assert.Equal(expected, Assert.IsType<decimal>(actual)));
        Assert.All(parameterResult.ColumnInfo!, info => Assert.Equal(TableColumnType.Decimal, info.DataType));
        Assert.Equal(expected, Assert.IsType<decimal>(Assert.Single(Query(db, $"SELECT round(DECIMAL '{text}', {digits})").Rows)[0]));
        if (digits == 0)
            Assert.Equal(expected, Assert.IsType<decimal>(Assert.Single(Query(db, "SELECT round(@value)", parameters).Rows)[0]));
    }

    /// <summary>全部 29 个合法小数位以及动态整数表示均使用 Decimal 计算。</summary>
    [Fact]
    public void Round_AllDecimalDigits_PreservesDecimalAndDoesNotEvaluateTwice()
    {
        using var db = Open();
        const decimal value = 1.1234567890123456789012345678m;
        int calls = 0;
        db.Functions.RegisterScalar("observe_decimal", args => { calls++; return args[0]; }, 1, 1);
        for (int digits = 0; digits <= 28; digits++)
        {
            var parameters = new SqlParameters().AddNamed("value", value).AddNamed("digits", digits);
            var result = Query(db, "SELECT round(observe_decimal(@value), @digits)", parameters);
            Assert.Equal(Math.Round(value, digits, MidpointRounding.ToEven), Assert.IsType<decimal>(Assert.Single(result.Rows)[0]));
        }
        Assert.Equal(29, calls);
        Assert.True(FunctionRegistry.TryGetScalar("round", out var round));
        Assert.Equal(1.02m, Assert.IsType<decimal>(round.Evaluate([1.015m, 2.0m])));
        Assert.Equal(1.02m, Assert.IsType<decimal>(round.Evaluate([1.015m, 2.0d])));
    }

    /// <summary>动态越界、小数、非有限位数和 NULL 明确拒绝，避免截断或转换绕过。</summary>
    [Fact]
    public void Round_InvalidDecimalDigits_RejectsWithoutChangingFloatContract()
    {
        Assert.True(FunctionRegistry.TryGetScalar("round", out var round));
        object?[] invalidDigits = [-1L, 29L, long.MaxValue, ulong.MaxValue, 1.5m, -0.5m, 28.1m,
            1.5d, double.NaN, double.PositiveInfinity, double.NegativeInfinity, "2", null];
        foreach (object? digits in invalidDigits)
            Assert.Throws<InvalidOperationException>(() => round.Evaluate([1.015m, digits]));

        Assert.Throws<InvalidOperationException>(() => round.Evaluate([null]));
        Assert.Throws<InvalidOperationException>(() => round.Evaluate([1.015d, null]));
        Assert.Equal(1.01d, Assert.IsType<double>(round.Evaluate([1.015d, 2])));
        Assert.Equal(1.01d, Assert.IsType<double>(round.Evaluate([1.015d, 2.9d])));
        Assert.Equal(3d, Assert.IsType<double>(round.Evaluate([3L])));
        Assert.Equal(double.PositiveInfinity, Assert.IsType<double>(round.Evaluate([double.PositiveInfinity])));
        Assert.True(double.IsNaN(Assert.IsType<double>(round.Evaluate([double.NaN]))));
        Assert.Throws<ArgumentOutOfRangeException>(() => round.Evaluate([1.015d, 16]));
    }

    /// <summary>列与表达式在 SELECT、WHERE、UPDATE 一致，后续行失败保持全部更新原子。</summary>
    [Fact]
    public void Round_RelationalExpressionsAndInvalidUpdate_PreservePrecisionAndAtomicity()
    {
        using (var db = Open())
        {
            SqlExecutor.Execute(db, "CREATE TABLE ledger (id INT, amount DECIMAL, digits INT, PRIMARY KEY(id))");
            SqlExecutor.Execute(db, "INSERT INTO ledger(id,amount,digits) VALUES (1,9007199254740993.125,2),(2,1.015,29),(3,-1.015,2)");
            var parameters = new SqlParameters().AddNamed("expected", 9007199254740993.12m);
            Assert.Equal(1L, Assert.Single(Query(db, "SELECT id FROM ledger WHERE round(amount,2)=@expected", parameters).Rows)[0]);
            Assert.Equal(9007199254740993.22m, Assert.IsType<decimal>(Assert.Single(Query(db,
                "SELECT round(amount + DECIMAL '0.1',2) FROM ledger WHERE id=1").Rows)[0]));
            Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
                "UPDATE ledger SET amount=round(amount,digits) WHERE id>=1"));
            Assert.Equal(new object?[] { 9007199254740993.125m, 1.015m, -1.015m },
                Query(db, "SELECT amount FROM ledger ORDER BY id").Rows.Select(row => row[0]).ToArray());
            SqlExecutor.Execute(db, "UPDATE ledger SET amount=round(amount,2) WHERE id>=1");
        }
        using var reopened = Open();
        Assert.Equal(new object?[] { 9007199254740993.12m, 1.02m, -1.02m },
            Query(reopened, "SELECT amount FROM ledger ORDER BY id").Rows.Select(row => row[0]).ToArray());
    }

    /// <summary>空表、过滤零行、嵌套表达式和视图声明保留 Decimal，不伪造输入精度或键属性。</summary>
    [Fact]
    public void Round_EmptyAndViewMetadata_PreservesDecimalWithUnknownPrecisionAndScale()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE ledger(id INT, amount DECIMAL(28,8) NULL, required DECIMAL(28,8) NOT NULL, PRIMARY KEY(id))");
        SqlExecutor.Execute(db, "CREATE VIEW rounded AS SELECT round(amount,2) AS amount FROM ledger");
        string[] queries =
        [
            "SELECT round(amount,2), round(amount), round(amount+DECIMAL '0.1',2), round(round(amount,2),1) FROM ledger",
            "SELECT round(a.amount,2) FROM ledger a INNER JOIN ledger b ON a.id=b.id",
            "SELECT amount FROM rounded",
            "SELECT round(CAST(NULL AS DECIMAL),2) FROM ledger",
        ];
        foreach (string sql in queries)
        {
            var result = Query(db, sql);
            Assert.Empty(result.Rows);
            Assert.All(result.ColumnInfo!, info =>
            {
                Assert.Equal(TableColumnType.Decimal, info.DataType);
                Assert.True(info.IsNullable);
                Assert.Null(info.NumericPrecision);
                Assert.Null(info.NumericScale);
                Assert.False(info.IsKey);
            });
        }
        var emptyParameters = Query(db, "SELECT round(@value,@digits), round(id,2), round(CAST(id AS FLOAT),2), round(required,2) FROM ledger",
            new SqlParameters().AddNamed("value", 1.015m).AddNamed("digits", 2));
        Assert.Equal(new TableColumnType?[] { TableColumnType.Decimal, TableColumnType.Float64, TableColumnType.Float64, TableColumnType.Decimal },
            emptyParameters.ColumnInfo!.Select(info => info.DataType).ToArray());
        Assert.False(emptyParameters.ColumnInfo![0].IsNullable);
        Assert.False(emptyParameters.ColumnInfo![3].IsNullable);

        SqlExecutor.Execute(db, "INSERT INTO ledger(id,amount,required) VALUES(1,NULL,1.015)");
        Assert.Throws<InvalidOperationException>(() => Query(db, "SELECT round(amount,2) FROM ledger"));
        Assert.Empty(Query(db, "SELECT round(amount,2) FROM ledger WHERE id=2").Rows);
        Assert.Equal(TableColumnType.Decimal, Assert.Single(Query(db,
            "SELECT round(amount,2) FROM ledger WHERE id=2").ColumnInfo!).DataType);
    }

    /// <summary>名为 round 的自定义函数不继承内置函数声明，也不会在推断元数据时执行。</summary>
    [Fact]
    public void Round_UserOverride_LeavesStaticTypeUnknownAndEvaluatesOnce()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE ledger(id INT, amount DECIMAL, PRIMARY KEY(id))");
        int calls = 0;
        db.Functions.RegisterScalar("round", _ => { calls++; return "custom"; }, 1, 2);
        Assert.Null(Assert.Single(Query(db, "SELECT round(amount,2) FROM ledger").ColumnInfo!).DataType);
        Assert.Equal(0, calls);
        Assert.Equal("custom", Assert.Single(Query(db, "SELECT round(DECIMAL '1.015',2)").Rows)[0]);
        Assert.Equal(1, calls);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static SelectExecutionResult Query(Tsdb db, string sql, SqlParameters? parameters = null)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, sql, parameters));

    /// <summary>回收本测试创建的数据库目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
