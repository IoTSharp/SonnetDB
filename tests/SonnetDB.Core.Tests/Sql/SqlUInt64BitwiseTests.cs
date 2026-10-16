using System.Data.Common;
using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Query.Functions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>完整 UInt64 的原生等价按位函数必须保留 Decimal 精度和 SQL 声明。</summary>
public sealed class SqlUInt64BitwiseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "u64bit-" + Guid.NewGuid().ToString("N"));

    /// <summary>所有高位边界与掩码直接对拍 UInt64 运算，不经过 Double 或 Int64。</summary>
    [Fact]
    public void BitwiseUInt64_BoundariesAndMasks_ReturnExactDecimal()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        ulong[] values = [0, 7, long.MaxValue, 1UL << 63, ulong.MaxValue];
        ulong[] masks = [0, 1, 7, long.MaxValue, 1UL << 63, ulong.MaxValue];
        foreach (ulong value in values)
            foreach (ulong mask in masks)
            {
                command.CommandText = FormattableString.Invariant($"SELECT bit_and_uint64(DECIMAL '{value}',DECIMAL '{mask}'),bit_or_uint64(DECIMAL '{value}',DECIMAL '{mask}')");
                using var reader = command.ExecuteReader();
                AssertSchema(reader, 0, false);
                AssertSchema(reader, 1, false);
                Assert.True(reader.Read());
                Assert.Equal((decimal)(value & mask), Assert.IsType<decimal>(reader.GetValue(0)));
                Assert.Equal((decimal)(value | mask), Assert.IsType<decimal>(reader.GetValue(1)));
                Assert.False(reader.Read());
            }
    }

    /// <summary>NULL 与空表的类型声明在第一行之前精确可用。</summary>
    [Fact]
    public void BitwiseUInt64_NullAndEmpty_DeclareDecimal20Scale0()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE bits(id INT, nullable_value DECIMAL(20,0) NULL, required_value DECIMAL(20,0) NOT NULL, PRIMARY KEY(id))";
        command.ExecuteNonQuery();
        foreach (string sql in new[]
        {
            "SELECT bit_and_uint64(nullable_value,7),bit_or_uint64(required_value,7),bit_and_uint64(required_value,NULL) FROM bits",
            "SELECT bit_and_uint64(a.nullable_value,7),bit_or_uint64(a.required_value,7),bit_and_uint64(a.required_value,NULL) FROM bits a INNER JOIN bits b ON a.id=b.id",
        })
        {
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            AssertSchema(reader, 0, true);
            AssertSchema(reader, 1, false);
            AssertSchema(reader, 2, true);
            Assert.False(reader.Read());
        }
        command.CommandText = "SELECT bit_and_uint64(NULL,7),bit_or_uint64(7,NULL)";
        using var missing = command.ExecuteReader();
        AssertSchema(missing, 0, true);
        AssertSchema(missing, 1, true);
        Assert.True(missing.Read());
        Assert.True(missing.IsDBNull(0));
        Assert.True(missing.IsDBNull(1));
    }

    /// <summary>旧有有符号运算符保持 Int64 语义，不默默扩大到 Decimal。</summary>
    [Fact]
    public void BitwiseSigned_ExistingOperators_KeepInt64Contract()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT -1 & 7,-8 | 1,6 & 3,6 | 3";
        using (var reader = command.ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.Equal(7L, reader.GetValue(0));
            Assert.Equal(-7L, reader.GetValue(1));
            Assert.Equal(2L, reader.GetValue(2));
            Assert.Equal(7L, reader.GetValue(3));
        }
        foreach (string sql in new[] { "SELECT CAST('7' AS DECIMAL) & 3", "SELECT CAST('7' AS DECIMAL) | 8" })
        {
            command.CommandText = sql;
            var failure = Assert.ThrowsAny<Exception>(() => command.ExecuteScalar());
            Assert.Contains("只支持整数操作数", failure.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>整数 CLR 输入和任意整数 Decimal scale 直接计算，结果 scale 固定为零。</summary>
    [Fact]
    public void BitwiseUInt64_IntegralClrInputsAndDecimalScale_AreExact()
    {
        object[] values = [(byte)7, (sbyte)7, (short)7, (ushort)7, 7, 7U, 7L, 7UL, 7.000m];
        foreach (object value in values)
        {
            decimal andValue = Assert.IsType<decimal>(SqlUnsignedBitwiseFunctions.BitAnd([value, ulong.MaxValue]));
            decimal orValue = Assert.IsType<decimal>(SqlUnsignedBitwiseFunctions.BitOr([value, 1UL << 63]));
            Assert.Equal(7m, andValue);
            Assert.Equal(9223372036854775815m, orValue);
            Assert.Equal(0, decimal.GetBits(andValue)[3]);
            Assert.Equal(0, decimal.GetBits(orValue)[3]);
        }
        foreach (object? missing in new object?[] { null, DBNull.Value })
        {
            Assert.Null(SqlUnsignedBitwiseFunctions.BitAnd([missing, -1m]));
            Assert.Null(SqlUnsignedBitwiseFunctions.BitOr(["invalid", missing]));
        }
    }

    /// <summary>非整数及范围外输入在两侧均拒绝；不隐式转换浮点、字符串或负号。</summary>
    [Fact]
    public void BitwiseUInt64_InvalidClrInputsOnEitherSide_Reject()
    {
        object[] values = [-1, -1L, -1m, 0.5m, 18446744073709551616m, decimal.MaxValue,
            1.0, 1.0f, double.NaN, double.PositiveInfinity, "7", true, new byte[] { 7 }];
        foreach (object value in values)
            foreach (bool and in new[] { true, false })
                foreach (bool left in new[] { true, false })
                {
                    object?[] arguments = left ? [value, 1L] : [1L, value];
                    var failure = Assert.Throws<InvalidOperationException>(() => and
                        ? SqlUnsignedBitwiseFunctions.BitAnd(arguments) : SqlUnsignedBitwiseFunctions.BitOr(arguments));
                    Assert.Contains(and ? "bit_and_uint64" : "bit_or_uint64", failure.Message, StringComparison.Ordinal);
                    Assert.Contains("0..18446744073709551615", failure.Message, StringComparison.Ordinal);
                }
    }

    /// <summary>SQL 非法类型、值和参数数量不能被隐式截断成有效整数。</summary>
    [Theory]
    [InlineData("SELECT bit_and_uint64(DECIMAL '-1',1)")]
    [InlineData("SELECT bit_or_uint64(1,DECIMAL '18446744073709551616')")]
    [InlineData("SELECT bit_and_uint64(DECIMAL '7.5',1)")]
    [InlineData("SELECT bit_or_uint64(1,7.0)")]
    [InlineData("SELECT bit_and_uint64('7',1)")]
    [InlineData("SELECT bit_or_uint64(TRUE,1)")]
    [InlineData("SELECT bit_and_uint64(1)")]
    [InlineData("SELECT bit_or_uint64(1,2,3)")]
    public void BitwiseUInt64_InvalidSql_RejectsBeforeResult(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Assert.ThrowsAny<Exception>(() => command.ExecuteScalar());
    }

    /// <summary>嵌套及两个 UDF 输入各只执行一次，同名用户函数不会被错误声明为内建返回。</summary>
    [Fact]
    public void BitwiseUInt64_UserFunctions_PreserveOverridesAndSingleEvaluation()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root, AllowUserFunctions = true });
        int calls = 0;
        database.Functions.RegisterScalar("observe_bit_value", args => { calls++; return args[0]; }, 1, 1);
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
            "SELECT bit_and_uint64(observe_bit_value(DECIMAL '18446744073709551615'),observe_bit_value(DECIMAL '9223372036854775808'))"));
        Assert.Equal(9223372036854775808m, result.Rows.Single()[0]);
        Assert.Equal(2, calls);
        SqlExecutor.Execute(database, "CREATE TABLE overrides(id INT, value DECIMAL(20,0), PRIMARY KEY(id))");
        foreach (string name in new[] { "bit_and_uint64", "bit_or_uint64" })
        {
            database.Functions.RegisterScalar(name, static _ => "custom", 2, 2);
            var empty = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, $"SELECT {name}(value,1) FROM overrides"));
            Assert.Null(empty.ColumnInfo![0].DataType);
            Assert.Null(empty.ColumnInfo[0].NumericPrecision);
            Assert.Null(empty.ColumnInfo[0].NumericScale);
            var overridden = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, $"SELECT {name}(1,2)"));
            Assert.Equal("custom", overridden.Rows.Single()[0]);
        }
    }

    private SndbConnection Open()
    {
        var connection = new SndbConnection("Data Source=" + _root);
        connection.Open();
        return connection;
    }

    private static void AssertSchema(DbDataReader reader, int ordinal, bool nullable)
    {
        Assert.Equal(typeof(decimal), reader.GetFieldType(ordinal));
        var column = reader.GetColumnSchema()[ordinal];
        Assert.Equal(typeof(decimal), column.DataType);
        Assert.Equal(20, column.NumericPrecision);
        Assert.Equal(0, column.NumericScale);
        Assert.Equal(nullable, column.AllowDBNull);
        Assert.False(column.IsKey);
        Assert.False(column.IsAutoIncrement);
    }

    /// <summary>释放本测试独占的数据库目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
