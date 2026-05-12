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
    [InlineData("coalesce")]
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
