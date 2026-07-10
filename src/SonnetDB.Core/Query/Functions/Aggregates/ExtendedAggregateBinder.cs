using SonnetDB.Catalog;
using SonnetDB.Sql.Ast;
using SonnetDB.Storage.Format;

namespace SonnetDB.Query.Functions.Aggregates;

/// <summary>
/// 扩展聚合函数共享的参数解析与列校验工具。
/// </summary>
internal static class ExtendedAggregateBinder
{
    /// <summary>
    /// 校验 <c>fn(field)</c> 形式（单参数、字段必须为数值列），返回字段名。
    /// </summary>
    public static string ResolveSingleNumericField(
        FunctionCallExpression call, MeasurementSchema schema, string functionName)
        => ResolveSingleField(
            call,
            schema,
            functionName,
            AggregateFieldTypes.Numeric | AggregateFieldTypes.Boolean,
            "需要数值字段");

    /// <summary>
    /// 校验 <c>fn(field)</c> 形式（单参数、字段必须为可分类标量列），返回字段名。
    /// </summary>
    public static string ResolveSingleCategoricalField(
        FunctionCallExpression call, MeasurementSchema schema, string functionName)
        => ResolveSingleField(
            call,
            schema,
            functionName,
            AggregateFieldTypes.Categorical,
            "仅支持数值、布尔或字符串字段");

    private static string ResolveSingleField(
        FunctionCallExpression call,
        MeasurementSchema schema,
        string functionName,
        AggregateFieldTypes acceptedFieldTypes,
        string typeRequirement)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(schema);

        if (call.IsStar)
            throw new InvalidOperationException($"{functionName}(*) 非法。");
        if (call.Arguments.Count != 1)
            throw new InvalidOperationException(
                $"{functionName}(...) 需要 1 个参数（字段名），实际 {call.Arguments.Count}。");
        if (call.Arguments[0] is not IdentifierExpression id)
            throw new InvalidOperationException(
                $"{functionName}(...) 第一个参数必须是字段名。");

        var col = schema.TryGetColumn(id.Name)
            ?? throw new InvalidOperationException($"{functionName}({id.Name}) 引用了未知列。");
        if (col.Role != MeasurementColumnRole.Field)
            throw new InvalidOperationException(
                $"{functionName} 只能作用于 FIELD 列（'{id.Name}' 是 {col.Role}）。");
        if (!acceptedFieldTypes.Supports(col.DataType))
            throw new InvalidOperationException(
                $"{functionName} {typeRequirement}，'{id.Name}' 的类型为 {col.DataType}。");
        return col.Name;
    }

    /// <summary>
    /// 校验 <c>fn(field, numeric_literal)</c> 形式，返回 (字段名, 第二参数数值)。
    /// </summary>
    public static (string FieldName, double NumericArgument) ResolveFieldAndNumeric(
        FunctionCallExpression call, MeasurementSchema schema, string functionName)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(schema);

        if (call.IsStar)
            throw new InvalidOperationException($"{functionName}(*) 非法。");
        if (call.Arguments.Count != 2)
            throw new InvalidOperationException(
                $"{functionName}(...) 需要 2 个参数（字段名，数值常量），实际 {call.Arguments.Count}。");

        if (call.Arguments[0] is not IdentifierExpression id)
            throw new InvalidOperationException(
                $"{functionName}(...) 第一个参数必须是字段名。");
        var col = schema.TryGetColumn(id.Name)
            ?? throw new InvalidOperationException($"{functionName}({id.Name}, ...) 引用了未知列。");
        if (col.Role != MeasurementColumnRole.Field)
            throw new InvalidOperationException(
                $"{functionName} 只能作用于 FIELD 列（'{id.Name}' 是 {col.Role}）。");
        if (col.DataType is FieldType.String or FieldType.Vector or FieldType.GeoPoint)
            throw new InvalidOperationException(
                $"{functionName} 仅支持数值字段，'{id.Name}' 的类型为 {col.DataType}。");

        double numeric = call.Arguments[1] switch
        {
            LiteralExpression { Kind: SqlLiteralKind.Integer } lit => lit.IntegerValue,
            LiteralExpression { Kind: SqlLiteralKind.Float } lit => lit.FloatValue,
            _ => throw new InvalidOperationException(
                $"{functionName}(...) 第二个参数必须是数值常量。"),
        };
        return (col.Name, numeric);
    }

    /// <summary>
    /// 校验 <c>fn(field)</c> 形式（单参数、字段必须为向量列），返回字段名。
    /// </summary>
    public static string ResolveSingleVectorField(
        FunctionCallExpression call, MeasurementSchema schema, string functionName)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(schema);

        if (call.IsStar)
            throw new InvalidOperationException($"{functionName}(*) 非法。");
        if (call.Arguments.Count != 1)
            throw new InvalidOperationException(
                $"{functionName}(...) 需要 1 个参数（字段名），实际 {call.Arguments.Count}。");
        if (call.Arguments[0] is not IdentifierExpression id)
            throw new InvalidOperationException(
                $"{functionName}(...) 第一个参数必须是字段名。");

        var col = schema.TryGetColumn(id.Name)
            ?? throw new InvalidOperationException($"{functionName}({id.Name}) 引用了未知列。");
        if (col.Role != MeasurementColumnRole.Field)
            throw new InvalidOperationException(
                $"{functionName} 只能作用于 FIELD 列（'{id.Name}' 是 {col.Role}）。");
        if (col.DataType != FieldType.Vector)
            throw new InvalidOperationException(
                $"{functionName} 仅支持 VECTOR 字段，'{id.Name}' 的类型为 {col.DataType}。");
        return col.Name;
    }

    /// <summary>
    /// 校验 <c>fn(field)</c> 形式（单参数、字段必须为地理点列），返回字段名。
    /// </summary>
    public static string ResolveSingleGeoPointField(
        FunctionCallExpression call, MeasurementSchema schema, string functionName)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(schema);

        if (call.IsStar)
            throw new InvalidOperationException($"{functionName}(*) 非法。");
        if (call.Arguments.Count != 1)
            throw new InvalidOperationException(
                $"{functionName}(...) 需要 1 个参数（字段名），实际 {call.Arguments.Count}。");
        if (call.Arguments[0] is not IdentifierExpression id)
            throw new InvalidOperationException(
                $"{functionName}(...) 第一个参数必须是字段名。");

        var col = schema.TryGetColumn(id.Name)
            ?? throw new InvalidOperationException($"{functionName}({id.Name}) 引用了未知列。");
        if (col.Role != MeasurementColumnRole.Field)
            throw new InvalidOperationException(
                $"{functionName} 只能作用于 FIELD 列（'{id.Name}' 是 {col.Role}）。");
        if (col.DataType != FieldType.GeoPoint)
            throw new InvalidOperationException(
                $"{functionName} 仅支持 GEOPOINT 字段，'{id.Name}' 的类型为 {col.DataType}。");
        return col.Name;
    }

    /// <summary>
    /// 校验 <c>fn(field, time)</c> 形式（字段必须为地理点列，第二参数必须为 time）。
    /// </summary>
    public static string ResolveGeoPointFieldAndTime(
        FunctionCallExpression call, MeasurementSchema schema, string functionName)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(schema);

        if (call.IsStar)
            throw new InvalidOperationException($"{functionName}(*) 非法。");
        if (call.Arguments.Count != 2)
            throw new InvalidOperationException(
                $"{functionName}(...) 需要 2 个参数（GEOPOINT 字段名，time），实际 {call.Arguments.Count}。");
        if (call.Arguments[1] is not IdentifierExpression timeId
            || !string.Equals(timeId.Name, "time", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{functionName}(...) 第二个参数必须是 time。");

        return ResolveSingleGeoPointField(
            call with { Arguments = new[] { call.Arguments[0] } }, schema, functionName);
    }
}
