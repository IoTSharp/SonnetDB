using SonnetDB.Catalog;
using SonnetDB.Query;
using SonnetDB.Query.Functions;
using SonnetDB.Sql.Ast;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.Query.Functions;

public sealed class FunctionRegistryTests
{
    private static readonly MeasurementSchema _schema = MeasurementSchema.Create(
        "cpu",
        new[]
        {
            new MeasurementColumn("host", MeasurementColumnRole.Tag, FieldType.String),
            new MeasurementColumn("usage", MeasurementColumnRole.Field, FieldType.Float64),
            new MeasurementColumn("label", MeasurementColumnRole.Field, FieldType.String),
            new MeasurementColumn("embedding", MeasurementColumnRole.Field, FieldType.Vector, 3),
            new MeasurementColumn("position", MeasurementColumnRole.Field, FieldType.GeoPoint),
        });

    [Theory]
    [InlineData("count", Aggregator.Count)]
    [InlineData("sum", Aggregator.Sum)]
    [InlineData("min", Aggregator.Min)]
    [InlineData("max", Aggregator.Max)]
    [InlineData("avg", Aggregator.Avg)]
    [InlineData("first", Aggregator.First)]
    [InlineData("last", Aggregator.Last)]
    public void TryGetAggregate_ResolvesBuiltIns(string name, Aggregator aggregator)
    {
        Assert.True(FunctionRegistry.TryGetAggregate(name.ToUpperInvariant(), out var function));
        Assert.Equal(name, function.Name);
        Assert.Equal(aggregator, function.LegacyAggregator);
    }

    [Theory]
    [InlineData("abs")]
    [InlineData("round")]
    [InlineData("sqrt")]
    [InlineData("log")]
    [InlineData("ceil")]
    [InlineData("ceiling")]
    [InlineData("floor")]
    [InlineData("exp")]
    [InlineData("power")]
    [InlineData("pow")]
    [InlineData("coalesce")]
    [InlineData("concat")]
    [InlineData("trim")]
    [InlineData("ltrim")]
    [InlineData("rtrim")]
    [InlineData("length")]
    [InlineData("char_length")]
    [InlineData("substring")]
    [InlineData("substr")]
    [InlineData("position")]
    [InlineData("replace")]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("starts_with")]
    [InlineData("ends_with")]
    [InlineData("contains")]
    [InlineData("date_diff")]
    [InlineData("datediff")]
    [InlineData("date_format")]
    [InlineData("format_datetime")]
    [InlineData("strftime")]
    [InlineData("to_char")]
    [InlineData("modbus_int32")]
    [InlineData("modbus_uint32")]
    [InlineData("modbus_float32")]
    [InlineData("cosine_distance")]
    [InlineData("l2_distance")]
    [InlineData("inner_product")]
    [InlineData("vector_norm")]
    [InlineData("geo_transform")]
    [InlineData("geo_wgs84_to_gcj02")]
    [InlineData("geo_gcj02_to_wgs84")]
    [InlineData("geo_gcj02_to_bd09")]
    [InlineData("geo_bd09_to_gcj02")]
    [InlineData("geo_wgs84_to_bd09")]
    [InlineData("geo_bd09_to_wgs84")]
    public void TryGetScalar_ResolvesBuiltIns(string name)
    {
        Assert.True(FunctionRegistry.TryGetScalar(name.ToUpperInvariant(), out var function));
        Assert.Equal(name, function.Name);
    }

    [Theory]
    [InlineData("count", FunctionKind.Aggregate)]
    [InlineData("sqrt", FunctionKind.Scalar)]
    [InlineData("cosine_distance", FunctionKind.Scalar)]
    [InlineData("stddev", FunctionKind.Aggregate)]
    [InlineData("centroid", FunctionKind.Aggregate)]
    [InlineData("p95", FunctionKind.Aggregate)]
    [InlineData("histogram", FunctionKind.Aggregate)]
    [InlineData("trajectory_length", FunctionKind.Aggregate)]
    [InlineData("trajectory_centroid", FunctionKind.Aggregate)]
    [InlineData("trajectory_bbox", FunctionKind.Aggregate)]
    [InlineData("trajectory_speed_max", FunctionKind.Aggregate)]
    [InlineData("trajectory_speed_avg", FunctionKind.Aggregate)]
    [InlineData("trajectory_speed_p95", FunctionKind.Aggregate)]
    [InlineData("geo_transform", FunctionKind.Scalar)]
    [InlineData("geo_wgs84_to_gcj02", FunctionKind.Scalar)]
    [InlineData("derivative", FunctionKind.Window)]
    [InlineData("ewma", FunctionKind.Window)]
    [InlineData("interpolate", FunctionKind.Window)]
    [InlineData("moving_average", FunctionKind.Window)]
    [InlineData("running_min", FunctionKind.Window)]
    [InlineData("running_max", FunctionKind.Window)]
    [InlineData("state_changes", FunctionKind.Window)]
    [InlineData("nonexistent_xyz", FunctionKind.Unknown)]
    public void GetFunctionKind_ReturnsRegisteredKind(string name, FunctionKind kind)
    {
        Assert.Equal(kind, FunctionRegistry.GetFunctionKind(name));
    }

    [Fact]
    public void TryGetAggregate_UnknownFunction_ReturnsFalse()
    {
        Assert.False(FunctionRegistry.TryGetAggregate("nonexistent_xyz", out _));
    }

    [Theory]
    [InlineData("count", FieldType.Vector, true)]
    [InlineData("first", FieldType.GeoPoint, true)]
    [InlineData("last", FieldType.String, true)]
    [InlineData("min", FieldType.String, true)]
    [InlineData("max", FieldType.Boolean, true)]
    [InlineData("mode", FieldType.String, true)]
    [InlineData("distinct_count", FieldType.Boolean, true)]
    [InlineData("sum", FieldType.String, false)]
    [InlineData("avg", FieldType.Vector, false)]
    [InlineData("centroid", FieldType.Vector, true)]
    [InlineData("centroid", FieldType.Float64, false)]
    [InlineData("trajectory_length", FieldType.GeoPoint, true)]
    public void Aggregate_AcceptedFieldTypes_DeclareFunctionCapability(
        string name, FieldType fieldType, bool expected)
    {
        Assert.True(FunctionRegistry.TryGetAggregate(name, out var function));
        Assert.Equal(expected, function.AcceptedFieldTypes.Supports(fieldType));
    }

    [Fact]
    public void TryGetScalar_UnknownFunction_ReturnsFalse()
    {
        Assert.False(FunctionRegistry.TryGetScalar("stddev", out _));
    }

    [Fact]
    public void GetAggregate_MapsEveryLegacyBuiltIn()
    {
        foreach (var aggregator in new[]
                 {
                     Aggregator.Count, Aggregator.Sum, Aggregator.Min, Aggregator.Max,
                     Aggregator.Avg, Aggregator.First, Aggregator.Last,
                 })
        {
            var function = FunctionRegistry.GetAggregate(aggregator);
            Assert.Equal(aggregator, function.LegacyAggregator);
        }
    }

    [Fact]
    public void ResolveFieldName_CountStar_ReturnsNull()
    {
        var function = FunctionRegistry.GetAggregate(Aggregator.Count);
        var fieldName = function.ResolveFieldName(new FunctionCallExpression("count", [], true), _schema);
        Assert.Null(fieldName);
    }

    [Fact]
    public void ResolveFieldName_SumStar_Throws()
    {
        var function = FunctionRegistry.GetAggregate(Aggregator.Sum);
        Assert.Throws<InvalidOperationException>(() =>
            function.ResolveFieldName(new FunctionCallExpression("sum", [], true), _schema));
    }

    [Fact]
    public void ResolveFieldName_TagColumn_Throws()
    {
        var function = FunctionRegistry.GetAggregate(Aggregator.Sum);
        Assert.Throws<InvalidOperationException>(() =>
            function.ResolveFieldName(
                new FunctionCallExpression("sum", new[] { new IdentifierExpression("host") }),
                _schema));
    }

    [Fact]
    public void ResolveFieldName_StringField_ThrowsForNonCount()
    {
        var function = FunctionRegistry.GetAggregate(Aggregator.Sum);
        Assert.Throws<InvalidOperationException>(() =>
            function.ResolveFieldName(
                new FunctionCallExpression("sum", new[] { new IdentifierExpression("label") }),
                _schema));
    }

    [Fact]
    public void ResolveFieldName_ValidField_ReturnsColumnName()
    {
        var function = FunctionRegistry.GetAggregate(Aggregator.Avg);
        var fieldName = function.ResolveFieldName(
            new FunctionCallExpression("avg", new[] { new IdentifierExpression("usage") }),
            _schema);
        Assert.Equal("usage", fieldName);
    }

    [Fact]
    public void ScalarFunction_EvaluateRoundAndCoalesce_ReturnExpectedResults()
    {
        var round = Assert.IsAssignableFrom<IScalarFunction>(GetScalar("round"));
        var coalesce = Assert.IsAssignableFrom<IScalarFunction>(GetScalar("coalesce"));

        Assert.Equal(1.23, round.Evaluate(new object?[] { 1.234, 2 }));
        Assert.Equal("fallback", coalesce.Evaluate(new object?[] { null, "fallback" }));
    }

    [Fact]
    public void ScalarFunction_MathFunctions_ReturnExpectedResults()
    {
        var ceil = GetScalar("ceil");
        var floor = GetScalar("floor");
        var exp = GetScalar("exp");
        var power = GetScalar("power");

        Assert.Equal(-1d, ceil.Evaluate(new object?[] { -1.2d }));
        Assert.Equal(-2d, floor.Evaluate(new object?[] { -1.2d }));
        Assert.Equal(Math.E, Convert.ToDouble(exp.Evaluate(new object?[] { 1 }))!, 12);
        Assert.Equal(1024d, power.Evaluate(new object?[] { 2, 10 }));
        Assert.Equal(1024d, GetScalar("pow").Evaluate(new object?[] { 2, 10 }));
    }

    [Fact]
    public void ScalarFunction_MathFunctions_PropagateNullAndPreserveIeeeResults()
    {
        Assert.Null(GetScalar("ceil").Evaluate(new object?[] { null }));
        Assert.Null(GetScalar("floor").Evaluate(new object?[] { null }));
        Assert.Null(GetScalar("exp").Evaluate(new object?[] { null }));
        Assert.Null(GetScalar("power").Evaluate(new object?[] { null, 2 }));
        Assert.Null(GetScalar("power").Evaluate(new object?[] { 2, null }));

        Assert.True(double.IsNaN(Convert.ToDouble(GetScalar("ceil").Evaluate(new object?[] { double.NaN }))));
        Assert.Equal(double.NegativeInfinity, GetScalar("floor").Evaluate(new object?[] { double.NegativeInfinity }));
        Assert.Equal(0d, GetScalar("exp").Evaluate(new object?[] { double.NegativeInfinity }));
        Assert.Equal(double.PositiveInfinity, GetScalar("exp").Evaluate(new object?[] { 1000d }));
        Assert.True(double.IsNaN(Convert.ToDouble(GetScalar("power").Evaluate(new object?[] { -1d, 0.5d }))));
        Assert.Equal(double.PositiveInfinity, GetScalar("power").Evaluate(new object?[] { 10d, 309d }));
    }

    [Fact]
    public void ScalarFunction_MathFunctions_RejectNonNumericArguments()
    {
        var power = GetScalar("power");

        var exception = Assert.Throws<InvalidOperationException>(
            () => power.Evaluate(new object?[] { "2", 10 }));

        Assert.Contains("函数 power 需要数值参数", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScalarFunction_StringFunctions_ReturnExpectedResults()
    {
        Assert.Equal("hello", GetScalar("trim").Evaluate(new object?[] { "  hello  " }));
        Assert.Equal("hello  ", GetScalar("ltrim").Evaluate(new object?[] { "  hello  " }));
        Assert.Equal("  hello", GetScalar("rtrim").Evaluate(new object?[] { "  hello  " }));
        Assert.Equal("hello", GetScalar("trim").Evaluate(new object?[] { "xxhellox", "x" }));
        Assert.Equal(5L, GetScalar("length").Evaluate(new object?[] { "hello" }));
        Assert.Equal(5L, GetScalar("char_length").Evaluate(new object?[] { "hello" }));
        Assert.Equal("ell", GetScalar("substring").Evaluate(new object?[] { "hello", 2L, 3L }));
        Assert.Equal("ello", GetScalar("substr").Evaluate(new object?[] { "hello", 2L }));
        Assert.Equal("he", GetScalar("left").Evaluate(new object?[] { "hello", 2L }));
        Assert.Equal("lo", GetScalar("right").Evaluate(new object?[] { "hello", 2L }));
        Assert.Equal("hello", GetScalar("left").Evaluate(new object?[] { "hello", 100L }));
        Assert.Equal("hello", GetScalar("replace").Evaluate(new object?[] { "hello", "x", "y" }));
        Assert.Equal("heLLo", GetScalar("replace").Evaluate(new object?[] { "hello", "l", "L" }));
        Assert.True((bool)GetScalar("starts_with").Evaluate(new object?[] { "hello", "he" })!);
        Assert.True((bool)GetScalar("endswith").Evaluate(new object?[] { "hello", "lo" })!);
        Assert.True((bool)GetScalar("contains").Evaluate(new object?[] { "hello", "ell" })!);
    }

    [Fact]
    public void ScalarFunction_StringFunctions_PropagateNull()
    {
        Assert.Null(GetScalar("lower").Evaluate(new object?[] { null }));
        Assert.Null(GetScalar("trim").Evaluate(new object?[] { null }));
        Assert.Null(GetScalar("trim").Evaluate(new object?[] { "x", null }));
        Assert.Null(GetScalar("length").Evaluate(new object?[] { null }));
        Assert.Null(GetScalar("substring").Evaluate(new object?[] { null, 1L }));
        Assert.Null(GetScalar("substring").Evaluate(new object?[] { "x", null }));
        Assert.Null(GetScalar("replace").Evaluate(new object?[] { "x", null, "y" }));
        Assert.Null(GetScalar("left").Evaluate(new object?[] { "x", null }));
        Assert.Null(GetScalar("contains").Evaluate(new object?[] { "x", null }));
    }

    [Theory]
    [InlineData("trim")]
    [InlineData("substring")]
    [InlineData("replace")]
    [InlineData("left")]
    [InlineData("right")]
    public void ScalarFunction_StringFunctions_RejectNonStringArguments(string functionName)
    {
        object?[] args = functionName switch
        {
            "trim" => new object?[] { 1L },
            "substring" => new object?[] { 1L, 1L },
            "replace" => new object?[] { "x", 1L, "y" },
            _ => new object?[] { 1L, 1L },
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => GetScalar(functionName).Evaluate(args));
        Assert.Contains("需要字符串参数", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("substring", "start")]
    [InlineData("left", "count")]
    [InlineData("right", "count")]
    public void ScalarFunction_StringFunctions_RejectInvalidIntegerArguments(
        string functionName, string parameterName)
    {
        object?[] args = functionName == "substring"
            ? new object?[] { "hello", 0L }
            : new object?[] { "hello", -1L };

        var exception = Assert.Throws<InvalidOperationException>(
            () => GetScalar(functionName).Evaluate(args));
        Assert.Contains(parameterName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScalarFunction_DateFunctions_ReturnExpectedResults()
    {
        var from = new DateTime(2026, 1, 2, 3, 4, 5, 123, DateTimeKind.Utc);
        var to = from.AddDays(2).AddHours(3).AddMinutes(4).AddSeconds(5);

        Assert.Equal(2L, GetScalar("date_diff").Evaluate(new object?[] { "day", from, to }));
        Assert.Equal(2L, GetScalar("datediff").Evaluate(new object?[] { from, to, "day" }));
        Assert.Equal(51L, GetScalar("date_diff").Evaluate(new object?[] { "hour", from, to }));
        Assert.Equal("2026-01-04 06:08:10", GetScalar("date_format").Evaluate(
            new object?[] { to, "yyyy-MM-dd HH:mm:ss" }));
        Assert.Equal("2026-01-04", GetScalar("strftime").Evaluate(
            new object?[] { "%Y-%m-%d", to }));
    }

    [Fact]
    public void ScalarFunction_DateFunctions_PropagateNullAndRejectInvalidFormats()
    {
        Assert.Null(GetScalar("date_diff").Evaluate(new object?[] { "day", null, DateTime.UtcNow }));
        Assert.Null(GetScalar("date_format").Evaluate(new object?[] { null, "yyyy" }));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            GetScalar("date_diff").Evaluate(new object?[] { "fortnight", DateTime.UtcNow, DateTime.UtcNow }));
        Assert.Contains("不支持日期分量", exception.Message, StringComparison.Ordinal);

        exception = Assert.Throws<InvalidOperationException>(() =>
            GetScalar("date_format").Evaluate(new object?[] { DateTime.UtcNow, "%Q" }));
        Assert.Contains("不支持日期格式标记", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScalarFunction_VectorFunctions_ReturnExpectedResults()
    {
        var cosine = GetScalar("cosine_distance");
        var l2 = GetScalar("l2_distance");
        var inner = GetScalar("inner_product");
        var norm = GetScalar("vector_norm");

        var a = new float[] { 1, 0, 0 };
        var b = new float[] { 0, 1, 0 };
        var c = new float[] { 3, 4 };

        Assert.Equal(1.0, (double)cosine.Evaluate(new object?[] { a, b })!, 6);
        Assert.Equal(Math.Sqrt(2.0), Convert.ToDouble(l2.Evaluate(new object?[] { a, b })), 6);
        Assert.Equal(0.0, Convert.ToDouble(inner.Evaluate(new object?[] { a, b })), 6);
        Assert.Equal(5.0, Convert.ToDouble(norm.Evaluate(new object?[] { c })), 6);
    }

    [Fact]
    public void ScalarFunction_InvalidArgumentCount_Throws()
    {
        var abs = GetScalar("abs");
        Assert.Throws<InvalidOperationException>(() => abs.Evaluate([]));
    }

    [Fact]
    public void ResolveFieldName_Centroid_VectorField_ReturnsColumnName()
    {
        Assert.True(FunctionRegistry.TryGetAggregate("centroid", out var function));
        var fieldName = function.ResolveFieldName(
            new FunctionCallExpression("centroid", new[] { new IdentifierExpression("embedding") }),
            _schema);
        Assert.Equal("embedding", fieldName);
    }


    [Fact]
    public void ResolveFieldName_Trajectory_GeoPointField_ReturnsColumnName()
    {
        Assert.True(FunctionRegistry.TryGetAggregate("trajectory_length", out var length));
        Assert.Equal("position", length.ResolveFieldName(
            new FunctionCallExpression("trajectory_length", new[] { new IdentifierExpression("position") }),
            _schema));

        Assert.True(FunctionRegistry.TryGetAggregate("trajectory_speed_avg", out var speed));
        Assert.Equal("position", speed.ResolveFieldName(
            new FunctionCallExpression("trajectory_speed_avg", new SqlExpression[]
            {
                new IdentifierExpression("position"),
                new IdentifierExpression("time"),
            }),
            _schema));
    }

    private static IScalarFunction GetScalar(string name)
    {
        Assert.True(FunctionRegistry.TryGetScalar(name, out var scalar));
        return scalar;
    }
}
