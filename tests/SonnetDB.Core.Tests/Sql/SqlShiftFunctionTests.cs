using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Query.Functions;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// 验证 GH-Issue #219 的有符号 Int64 移位、参数、元数据和各 SQL 标量执行入口。
/// </summary>
public sealed class SqlShiftFunctionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-sql-shift-" + Guid.NewGuid().ToString("N"));

    /// <summary>为测试创建独占数据库目录。</summary>
    public SqlShiftFunctionTests() => Directory.CreateDirectory(_root);

    /// <summary>回收本测试创建的独占目录。</summary>
    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>验证符号位、移出位、零计数和 63 位边界。</summary>
    [Theory]
    [InlineData("shift_left(6, 1)", 12L)]
    [InlineData("shift_right(6, 1)", 3L)]
    [InlineData("shift_right(-8, 2)", -2L)]
    [InlineData("shift_left(1, 63)", long.MinValue)]
    [InlineData("shift_left(9223372036854775807, 1)", -2L)]
    [InlineData("shift_right((-9223372036854775807 - 1), 63)", -1L)]
    [InlineData("shift_left(0, 63)", 0L)]
    [InlineData("shift_right(-1, 0)", -1L)]
    [InlineData("shift_left(1 + 2, 1) + 1", 7L)]
    public void Select_BitBoundaries_ReturnsSignedInt64(string expression, long expected)
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var result = Select(database, "SELECT " + expression);
        Assert.Equal(expected, Assert.IsType<long>(result.Rows.Single()[0]));
    }

    /// <summary>非整数、错误参数数量和越界计数必须明确拒绝。</summary>
    [Theory]
    [InlineData("SELECT shift_left(1.0, 1)")]
    [InlineData("SELECT shift_right(1, 1.0)")]
    [InlineData("SELECT shift_left(CAST('1' AS DECIMAL), 1)")]
    [InlineData("SELECT shift_left('1', 1)")]
    [InlineData("SELECT shift_right(1, -1)")]
    [InlineData("SELECT shift_left(1, 64)")]
    [InlineData("SELECT shift_right(1, 9223372036854775807)")]
    [InlineData("SELECT shift_left(1)")]
    [InlineData("SELECT shift_right(1, 2, 3)")]
    public void Select_InvalidArguments_ThrowsExecutionError(string sql)
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database, sql));
    }

    /// <summary>任一 NULL 参数传播，超范围 UInt64 不得截断成有符号值。</summary>
    [Fact]
    public void Evaluate_NullAndUnsignedOverflow_PropagatesOrRejects()
    {
        Assert.True(FunctionRegistry.TryGetScalar("shift_left", out var left));
        Assert.True(FunctionRegistry.TryGetScalar("shift_right", out var right));
        Assert.Null(left.Evaluate([null, 1L]));
        Assert.Null(right.Evaluate([1L, null]));
        Assert.Equal(2L, left.Evaluate([(uint)1, (byte)1]));
        Assert.Throws<InvalidOperationException>(() => left.Evaluate([ulong.MaxValue, 1L]));
        Assert.Throws<InvalidOperationException>(() => right.Evaluate([1L, ulong.MaxValue]));
    }

    /// <summary>参数化投影、谓词和 UPDATE 保持整数值，空与全 NULL 结果仍声明 Int64。</summary>
    [Fact]
    public void Ado_SelectWhereAndUpdate_PreservesParametersAndMetadata()
    {
        using var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE bits (id INT, flags INT, PRIMARY KEY(id))";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT shift_left(flags, 1) AS shifted FROM bits";
        using (var empty = command.ExecuteReader())
        {
            Assert.Equal(typeof(long), empty.GetFieldType(0));
            Assert.False(empty.Read());
        }
        command.CommandText = "SELECT shift_right(a.flags, 1) FROM bits a INNER JOIN bits b ON a.id=b.id";
        using (var emptyJoin = command.ExecuteReader())
        {
            Assert.Equal(typeof(long), emptyJoin.GetFieldType(0));
            Assert.False(emptyJoin.Read());
        }
        command.CommandText = "INSERT INTO bits(id, flags) VALUES(1, 6), (2, NULL)";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT shift_right(flags, @count) FROM bits WHERE flags IS NULL";
        var count = command.CreateParameter();
        count.ParameterName = "@count";
        count.Value = 1L;
        command.Parameters.Add(count);
        using (var missing = command.ExecuteReader())
        {
            Assert.Equal(typeof(long), missing.GetFieldType(0));
            Assert.True(missing.Read());
            Assert.True(missing.IsDBNull(0));
        }
        command.CommandText = "UPDATE bits SET flags = shift_left(flags, @count) WHERE shift_right(flags, @count) = 3";
        Assert.Equal(1, command.ExecuteNonQuery());
        command.CommandText = "SELECT shift_right(flags, @count) + 1 FROM bits WHERE id = 1";
        Assert.Equal(7L, command.ExecuteScalar());
    }

    /// <summary>任一目标行的计数无效时，UPDATE 不得发布已计算的前序行。</summary>
    [Fact]
    public void Update_InvalidLaterRow_PreservesAllOriginalRows()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE TABLE bits (id INT, flags INT, count INT, PRIMARY KEY(id))");
        SqlExecutor.Execute(database, "INSERT INTO bits(id,flags,count) VALUES(1,6,1),(2,7,64)");
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "UPDATE bits SET flags = shift_left(flags, count) WHERE id > 0"));
        Assert.Equal(new object?[] { 1L, 6L }, Select(database, "SELECT id,flags FROM bits ORDER BY id").Rows[0]);
        Assert.Equal(new object?[] { 2L, 7L }, Select(database, "SELECT id,flags FROM bits ORDER BY id").Rows[1]);
    }

    /// <summary>时序、文档和关系 JOIN 使用同一原生标量调度与整数语义。</summary>
    [Fact]
    public void Select_MeasurementDocumentAndJoin_UsesSharedShiftFunctions()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE MEASUREMENT metrics (host TAG, flags FIELD INT)");
        SqlExecutor.Execute(database, "INSERT INTO metrics(time,host,flags) VALUES(1000,'a',6)");
        Assert.Equal(12L, Select(database, "SELECT shift_left(flags, 1) FROM metrics").Rows.Single()[0]);
        SqlExecutor.Execute(database, "CREATE DOCUMENT COLLECTION notes");
        SqlExecutor.Execute(database, "INSERT INTO notes(id,document) VALUES('n1','{\"flags\":6}')");
        Assert.Equal(3L, Select(database,
            "SELECT shift_right(CAST(json_value(document,'$.flags') AS INT), 1) FROM notes").Rows.Single()[0]);
        SqlExecutor.Execute(database, "CREATE TABLE bits (id INT, flags INT, PRIMARY KEY(id))");
        SqlExecutor.Execute(database, "INSERT INTO bits(id,flags) VALUES(1,6)");
        Assert.Equal(12L, Select(database,
            "SELECT shift_left(a.flags, 1) FROM bits a INNER JOIN bits b ON a.id=b.id").Rows.Single()[0]);
    }

    /// <summary>允许覆盖内置函数的 UDF 不得被错误声明为原生 Int64 返回类型。</summary>
    [Fact]
    public void Select_UserFunctionOverride_PreservesUnknownDeclaredType()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root, AllowUserFunctions = true });
        database.Functions.RegisterScalar("shift_left", static _ => "override", 2, 2);
        SqlExecutor.Execute(database, "CREATE TABLE bits (id INT, flags INT, PRIMARY KEY(id))");
        var simple = Select(database, "SELECT shift_left(flags, 1) FROM bits");
        Assert.NotNull(simple.ColumnInfo);
        Assert.Null(simple.ColumnInfo[0].DataType);
        var join = Select(database, "SELECT shift_left(a.flags, 1) FROM bits a INNER JOIN bits b ON a.id=b.id");
        Assert.NotNull(join.ColumnInfo);
        Assert.Null(join.ColumnInfo[0].DataType);
    }

    private static SelectExecutionResult Select(Tsdb database, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql));
}
