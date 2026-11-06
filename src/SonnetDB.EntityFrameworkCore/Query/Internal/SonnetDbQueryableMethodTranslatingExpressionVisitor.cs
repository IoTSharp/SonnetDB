using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using SonnetDB.EntityFrameworkCore.Storage.Internal;

namespace SonnetDB.EntityFrameworkCore.Query.Internal;

/// <summary>
/// 将受支持的 JSON 标量批量更新翻译为服务器端原子表达式。
/// </summary>
public sealed class SonnetDbQueryableMethodTranslatingExpressionVisitor : RelationalQueryableMethodTranslatingExpressionVisitor
{
    private int _pathParameter;

    /// <summary>创建 SonnetDB 关系型查询翻译器。</summary>
    /// <param name="dependencies">查询翻译器依赖。</param>
    /// <param name="relationalDependencies">关系型查询翻译器依赖。</param>
    /// <param name="queryCompilationContext">当前查询的编译上下文。</param>
    public SonnetDbQueryableMethodTranslatingExpressionVisitor(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
        RelationalQueryCompilationContext queryCompilationContext)
        : base(dependencies, relationalDependencies, queryCompilationContext)
    {
    }

    private SonnetDbQueryableMethodTranslatingExpressionVisitor(SonnetDbQueryableMethodTranslatingExpressionVisitor parent)
        : base(parent)
    {
    }

    /// <inheritdoc />
    protected override QueryableMethodTranslatingExpressionVisitor CreateSubqueryVisitor()
        => new SonnetDbQueryableMethodTranslatingExpressionVisitor(this);

    /// <inheritdoc />
    protected override UpdateExpression? TranslateExecuteUpdate(
        ShapedQueryExpression source,
        IReadOnlyList<ExecuteUpdateSetter> setters)
    {
        source = source.UpdateShaperExpression(new IncludePruner().Visit(source.ShaperExpression));
        if (setters.Count > 64)
            return base.TranslateExecuteUpdate(source, setters);
        foreach (var setter in setters)
            new SetterShapeValidator().Visit(setter.PropertySelector.Body);
        var targets = setters.Select(setter => TranslateLambdaExpression(source, setter.PropertySelector)).ToArray();
        if (!targets.Any(static target => target is JsonScalarExpression))
            return base.TranslateExecuteUpdate(source, setters);
        if (source.QueryExpression is not SelectExpression { Tables: [TableExpression table] } select
            || !IsValidSelectExpressionForExecuteUpdate(select, table, out _))
            throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);

        List<ColumnValueSetter> translated = [];
        var factory = RelationalDependencies.SqlExpressionFactory;
        for (var setterIndex = 0; setterIndex < setters.Count; setterIndex++)
        {
            var setter = setters[setterIndex];
            var (propertySelector, valueSelector) = setter;
            var value = valueSelector is LambdaExpression lambda
                ? TranslateLambdaExpression(source, lambda)
                : TranslateExpression(valueSelector, applyDefaultTypeMapping: false);
            if (value is null)
                throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
            if (targets[setterIndex] is ColumnExpression ordinaryColumn)
            {
                if (!string.Equals(ordinaryColumn.TableAlias, table.Alias, StringComparison.Ordinal)
                    || ordinaryColumn.TypeMapping is null or SonnetDbJsonTypeMapping)
                    throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
                translated.Add(new ColumnValueSetter(ordinaryColumn, factory.ApplyTypeMapping(value, ordinaryColumn.TypeMapping)));
                continue;
            }

            if (targets[setterIndex] is not JsonScalarExpression json
                || json.Json is not ColumnExpression { TypeMapping: SonnetDbJsonTypeMapping } column
                || json.TypeMapping is not { Converter: null } mapping
                || !IsSupportedScalar(json.Type)
                || !string.Equals(column.TableAlias, table.Alias, StringComparison.Ordinal))
            {
                throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
            }

            var path = BuildPath(json);
            var pathParameter = QueryCompilationContext.RegisterRuntimeParameter(
                "sonnetdb_json_path_" + _pathParameter++,
                Expression.Lambda(Expression.Constant(path), QueryCompilationContext.QueryContextParameter));
            var translatedPath = TranslateExpression(pathParameter)!;
            value = factory.ApplyTypeMapping(value, mapping);

            var index = translated.FindIndex(previous => previous.Column.Equals(column));
            var previousValue = index < 0 ? column : translated[index].Value;
            var updatedValue = factory.Function("json_set_value", [previousValue, translatedPath, value],
                nullable: true, argumentsPropagateNullability: [true, false, false], column.Type, column.TypeMapping);
            var translatedSetter = new ColumnValueSetter(column, updatedValue);
            if (index < 0)
                translated.Add(translatedSetter);
            else
                translated[index] = translatedSetter;
        }

        select.ReplaceProjection([]);
        select.ApplyProjection();
        return new UpdateExpression(table, select, translated);
    }

    private static bool IsSupportedScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(string) || type == typeof(bool) || type == typeof(byte)
            || type == typeof(short) || type == typeof(int) || type == typeof(long)
            || type == typeof(float) || type == typeof(double);
    }

    private static string BuildPath(JsonScalarExpression json)
    {
        if (json.Path.Count is 0 or > 64)
            throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
        var builder = new StringBuilder("$");
        foreach (var segment in json.Path)
        {
            if (segment.ArrayIndex is not null)
            {
                if (segment.ArrayIndex is not SqlConstantExpression { Value: int index } || index < 0)
                    throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
                builder.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']');
            }
            else
            {
                if (string.IsNullOrWhiteSpace(segment.PropertyName)
                    || segment.PropertyName.Length > 1024 - builder.Length - 4)
                    throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
                builder.Append("['").Append(segment.PropertyName.Replace("'", "''", StringComparison.Ordinal)).Append("']");
            }
            if (builder.Length > 1024)
                throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
        }
        return builder.ToString();
    }

    private sealed class IncludePruner : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
            => node is IncludeExpression include ? Visit(include.EntityExpression) : base.VisitExtension(node);
    }

    private sealed class SetterShapeValidator : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.Name is "get_Item" or "ElementAt" or "ElementAtOrDefault"
                && node.Arguments.LastOrDefault() is not ConstantExpression { Value: int and >= 0 })
                throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
            return base.VisitMethodCall(node);
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.NodeType == ExpressionType.ArrayIndex
                && node.Right is not ConstantExpression { Value: int and >= 0 })
                throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
            return base.VisitBinary(node);
        }
    }
}
