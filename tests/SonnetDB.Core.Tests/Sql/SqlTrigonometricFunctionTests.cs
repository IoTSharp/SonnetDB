using SonnetDB.Engine;
using SonnetDB.Query.Functions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// 验证三角函数的弧度、有限数值、NULL 和结果元数据合同。
/// </summary>
public sealed class SqlTrigonometricFunctionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-sql-trigonometry-" + Guid.NewGuid().ToString("N"));

    public SqlTrigonometricFunctionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Select_MinimumTrigonometricQuery_ReturnsDoubleResults()
    {
        using var database = Open();
        var row = Query(database,
            "SELECT sin(0), cos(0), tan(0), asin(0), acos(1), atan(0), atan2(1, 1)").Rows.Single();
        Assert.Equal(new object?[] { 0d, 1d, 0d, 0d, 0d, 0d, Math.PI / 4d }, row);
    }

    [Theory]
    [InlineData("sin", 1.5707963267948966, 1d)]
    [InlineData("cos", 3.141592653589793, -1d)]
    [InlineData("tan", 0.7853981633974483, 1d)]
    [InlineData("asin", -1d, -1.5707963267948966)]
    [InlineData("asin", 1d, 1.5707963267948966)]
    [InlineData("acos", -1d, 3.141592653589793)]
    [InlineData("acos", 1d, 0d)]
    [InlineData("atan", 1d, 0.7853981633974483)]
    public void ScalarFunction_RadiansAndDomainBoundaries_ReturnExpectedResults(
        string name, double input, double expected)
    {
        Assert.Equal(expected, Assert.IsType<double>(GetScalar(name).Evaluate([input])), 12);
    }

    [Theory]
    [InlineData(1d, 1d, 0.25d)]
    [InlineData(1d, -1d, 0.75d)]
    [InlineData(-1d, -1d, -0.75d)]
    [InlineData(-1d, 1d, -0.25d)]
    [InlineData(1d, 0d, 0.5d)]
    [InlineData(-1d, 0d, -0.5d)]
    [InlineData(0d, 1d, 0d)]
    [InlineData(0d, -1d, 1d)]
    [InlineData(0d, 0d, 0d)]
    public void Select_Atan2QuadrantsAxesAndZero_UsesYThenX(double y, double x, double piFactor)
    {
        using var database = Open();
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database, databaseName: null, "SELECT atan2(?, ?)",
            new SqlParameters().AddPositional(y).AddPositional(x), controlPlane: null));
        Assert.Equal(piFactor * Math.PI, Assert.IsType<double>(result.Rows.Single()[0]), 12);
    }

    [Theory]
    [InlineData("sin")]
    [InlineData("cos")]
    [InlineData("tan")]
    [InlineData("asin")]
    [InlineData("acos")]
    [InlineData("atan")]
    [InlineData("atan2")]
    public void ScalarFunction_NullArityTypesAndNonFiniteInputs_EnforcesContract(string name)
    {
        var function = GetScalar(name.ToUpperInvariant());
        int arity = name == "atan2" ? 2 : 1;
        Assert.Equal(FunctionKind.Scalar, FunctionRegistry.GetFunctionKind(name));
        Assert.Equal(arity, function.MinArgumentCount);
        Assert.Equal(arity, function.MaxArgumentCount);
        Assert.Throws<InvalidOperationException>(() => function.Evaluate([]));
        Assert.Throws<InvalidOperationException>(() => function.Evaluate([0d, 0d, 0d]));
        if (arity == 2)
        {
            Assert.Throws<InvalidOperationException>(() => function.Evaluate([0d]));
            Assert.Null(function.Evaluate([null, "invalid"]));
            Assert.Null(function.Evaluate([double.NaN, null]));
            Assert.Throws<InvalidOperationException>(() => function.Evaluate([0d, double.PositiveInfinity]));
            Assert.Throws<InvalidOperationException>(() => function.Evaluate([0d, "1"]));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => function.Evaluate([0d, 0d]));
            Assert.Null(function.Evaluate([null]));
        }
        Assert.All(new object?[] { "1", true, new object() }, value =>
            Assert.Contains("需要数值参数", Assert.Throws<InvalidOperationException>(() =>
                function.Evaluate(arity == 2 ? [value, 1d] : [value])).Message, StringComparison.Ordinal));
        Assert.All(new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity }, value =>
            Assert.Contains("需要有限数值参数", Assert.Throws<InvalidOperationException>(() =>
                function.Evaluate(arity == 2 ? [value, 1d] : [value])).Message, StringComparison.Ordinal));
    }

    [Fact]
    public void ScalarFunction_NumericConversionsAndHugeFiniteInputs_ReturnFiniteDouble()
    {
        var sine = GetScalar("sin");
        Assert.All(new object?[]
        {
            (byte)1, (sbyte)1, (short)1, (ushort)1, 1, 1u, 1L, 1UL, 1f, 1d, 1m,
        }, value => Assert.Equal(Math.Sin(1d), Assert.IsType<double>(sine.Evaluate([value]))));
        Assert.Equal(Math.Sin(double.MaxValue), sine.Evaluate([double.MaxValue]));
        Assert.Equal(Math.Cos(double.MaxValue), GetScalar("cos").Evaluate([double.MaxValue]));
        Assert.Equal(Math.Tan(double.MaxValue), GetScalar("tan").Evaluate([double.MaxValue]));
        Assert.Equal(Math.Atan(double.MaxValue), GetScalar("atan").Evaluate([double.MaxValue]));
        Assert.Equal(Math.PI / 4d, GetScalar("atan2").Evaluate([double.MaxValue, double.MaxValue]));
        Assert.Equal(Math.Tan(Math.PI / 2d), GetScalar("tan").Evaluate([Math.PI / 2d]));
        Assert.Equal(-Math.PI, GetScalar("atan2").Evaluate([-0d, -1d]));
    }

    [Theory]
    [InlineData("asin(2)", "[-1, 1]")]
    [InlineData("asin(-2)", "[-1, 1]")]
    [InlineData("acos(2)", "[-1, 1]")]
    [InlineData("acos(-2)", "[-1, 1]")]
    [InlineData("sin(exp(1000))", "有限数值参数")]
    [InlineData("cos(sqrt(-1))", "有限数值参数")]
    [InlineData("tan(exp(1000))", "有限数值参数")]
    [InlineData("atan2(1, exp(1000))", "有限数值参数")]
    [InlineData("sin('0')", "数值参数")]
    [InlineData("atan2(1)", "2 个参数")]
    public void Select_InvalidInputs_ThrowsStableError(string expression, string message)
    {
        using var database = Open();
        Assert.Contains(message, Assert.Throws<InvalidOperationException>(() =>
            Query(database, "SELECT " + expression)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_WhereAndParameters_PreservesExistingMathFunctions()
    {
        using var database = OpenWithTable();
        SqlExecutor.Execute(database, "INSERT INTO angles (id, angle, y, x) VALUES "
            + "(1, 0, 0, 1), (2, 1.5707963267948966, 1, 0), (3, NULL, NULL, NULL)");
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database, databaseName: null,
            "SELECT id, sin(angle), atan2(y, x) FROM angles "
            + "WHERE cos(angle) < ? AND atan2(y, x) > ? ORDER BY id",
            new SqlParameters().AddPositional(0.1d).AddPositional(0d), controlPlane: null));
        Assert.Equal(new object?[] { 2L, 1d, Math.PI / 2d }, result.Rows.Single());
        Assert.Equal(new object?[] { 2d, 1.23d, 2d, 2d }, Query(database,
            "SELECT abs(-2), round(1.234, 2), sqrt(4), log(100, 10)").Rows.Single());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Select_EmptyAndAllNullResults_DeclareNullableFloat64(bool join)
    {
        using var database = OpenWithTable();
        string sql = "SELECT sin(a.angle), cos(a.angle), tan(a.angle), asin(a.angle), "
            + "acos(a.angle), atan(a.angle), atan2(a.y, a.x) FROM angles a"
            + (join ? " JOIN angles b ON a.id = b.id" : string.Empty);
        var empty = Query(database, sql);
        Assert.Empty(empty.Rows);
        AssertFloat64Metadata(empty);
        SqlExecutor.Execute(database,
            "INSERT INTO angles (id, angle, y, x) VALUES (1, NULL, NULL, NULL)");
        var nulls = Query(database, sql);
        Assert.All(nulls.Rows.Single(), Assert.Null);
        AssertFloat64Metadata(nulls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Select_ScalarUdfOverride_PreservesUnknownDeclaredType(bool join)
    {
        using var database = OpenWithTable();
        database.Functions.RegisterScalar("sin", static _ => "override", 1, 1);
        string sql = "SELECT sin(a.angle) FROM angles a"
            + (join ? " JOIN angles b ON a.id = b.id" : string.Empty);
        Assert.Null(Assert.IsAssignableFrom<IReadOnlyList<SelectColumnInfo>>(
            Query(database, sql).ColumnInfo).Single().DataType);
        SqlExecutor.Execute(database, "INSERT INTO angles (id, angle) VALUES (1, 0)");
        var result = Query(database, sql);
        Assert.Equal("override", result.Rows.Single()[0]);
        Assert.Null(Assert.IsAssignableFrom<IReadOnlyList<SelectColumnInfo>>(
            result.ColumnInfo).Single().DataType);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private Tsdb OpenWithTable()
    {
        var database = Open();
        SqlExecutor.Execute(database,
            "CREATE TABLE angles (id INT, angle FLOAT, y FLOAT, x FLOAT, PRIMARY KEY (id))");
        return database;
    }

    private static void AssertFloat64Metadata(SelectExecutionResult result)
    {
        var columns = Assert.IsAssignableFrom<IReadOnlyList<SelectColumnInfo>>(result.ColumnInfo);
        Assert.Equal(7, columns.Count);
        Assert.All(columns, column =>
        {
            Assert.Equal(TableColumnType.Float64, column.DataType);
            Assert.True(column.IsNullable);
        });
    }

    private static IScalarFunction GetScalar(string name)
    {
        Assert.True(FunctionRegistry.TryGetScalar(name, out var function));
        return function;
    }

    private static SelectExecutionResult Query(Tsdb database, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql));
}
