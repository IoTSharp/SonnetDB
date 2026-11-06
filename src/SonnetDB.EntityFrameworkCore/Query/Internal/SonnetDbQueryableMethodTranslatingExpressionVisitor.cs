using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage.Json;
using SonnetDB.EntityFrameworkCore.Storage.Internal;

namespace SonnetDB.EntityFrameworkCore.Query.Internal;

/// <summary>
/// 将受支持的固定 JSON 标量、集合元素和子树批量更新翻译为服务器端原子表达式。
/// </summary>
public sealed class SonnetDbQueryableMethodTranslatingExpressionVisitor : RelationalQueryableMethodTranslatingExpressionVisitor
{
    private int _pathParameter;
    private static readonly Expression<Func<QueryContext, object?, IProperty?, ITypeBase?, JsonValueReaderWriter?, bool, string?>> SerializeValue =
        (context, value, property, type, codec, collection) => SonnetDbJsonUpdateValue.Serialize(value, property, type, codec, collection, context.CancellationToken);

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
        foreach (var setter in setters)
            new SetterShapeValidator().Visit(setter.PropertySelector.Body);
        var targets = setters.Select(setter => BindTarget(source, setter.PropertySelector)).ToArray();
        if (!targets.Any(static target => target.Json is not null))
            return base.TranslateExecuteUpdate(source, setters);
        if (setters.Count > 64)
            throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
        if (source.QueryExpression is not SelectExpression { Tables: [TableExpression table] } select
            || !IsValidSelectExpressionForExecuteUpdate(select, table, out _))
            throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);

        List<ColumnValueSetter> translated = [];
        var factory = RelationalDependencies.SqlExpressionFactory;
        for (var setterIndex = 0; setterIndex < setters.Count; setterIndex++)
        {
            var setter = setters[setterIndex];
            var (propertySelector, valueSelector) = setter;
            new SetterShapeValidator().Visit(valueSelector);
            var target = targets[setterIndex];
            if (target.Json is null && target.Sql is ColumnExpression ordinaryColumn)
            {
                if (!string.Equals(ordinaryColumn.TableAlias, table.Alias, StringComparison.Ordinal)
                    || ordinaryColumn.TypeMapping is null or SonnetDbJsonTypeMapping)
                    throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
                var ordinaryValue = valueSelector is LambdaExpression ordinaryLambda
                    ? TranslateLambdaExpression(source, Expression.Lambda(
                        new JsonValueAccessRewriter(this, source, ordinaryLambda.Parameters[0]).Visit(ordinaryLambda.Body)!, ordinaryLambda.Parameters))
                    : TranslateExpression(valueSelector, applyDefaultTypeMapping: false);
                if (ordinaryValue is null)
                    throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
                translated.Add(new ColumnValueSetter(ordinaryColumn, factory.ApplyTypeMapping(ordinaryValue, ordinaryColumn.TypeMapping)));
                continue;
            }

            if (target.Json is not { } json
                || json.Json is not ColumnExpression { TypeMapping: SonnetDbJsonTypeMapping } column
                || !string.Equals(column.TableAlias, table.Alias, StringComparison.Ordinal))
            {
                throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage + " Cannot bind target: " + propertySelector);
            }

            bool encoded = target.StructuralType is not null || json.TypeMapping?.Converter is not null || !IsSupportedScalar(json.Type);
            SqlExpression value;
            if (encoded)
                value = SerializeParameter(valueSelector, target.Property, target.StructuralType, json.TypeMapping?.JsonValueReaderWriter, target.Collection);
            else
            {
                value = (valueSelector is LambdaExpression lambda
                    ? TranslateLambdaExpression(source, Expression.Lambda(
                        new JsonValueAccessRewriter(this, source, lambda.Parameters[0]).Visit(lambda.Body)!, lambda.Parameters))
                    : TranslateExpression(valueSelector, applyDefaultTypeMapping: false))
                    ?? throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
                value = factory.ApplyTypeMapping(value, json.TypeMapping);
            }

            var index = translated.FindIndex(previous => previous.Column.Equals(column));
            if (json.Path.Count == 0)
            {
                var rootSetter = new ColumnValueSetter(column, factory.ApplyTypeMapping(value, column.TypeMapping));
                if (index < 0)
                    translated.Add(rootSetter);
                else
                    translated[index] = rootSetter;
                continue;
            }
            var path = BuildPath(json);
            var pathParameter = QueryCompilationContext.RegisterRuntimeParameter(
                "sonnetdb_json_path_" + _pathParameter++,
                Expression.Lambda(Expression.Constant(path), QueryCompilationContext.QueryContextParameter));
            var translatedPath = TranslateExpression(pathParameter)!;
            var previousValue = index < 0 ? column : translated[index].Value;
            SqlExpression[] arguments = encoded
                ? [previousValue, translatedPath, value, factory.Constant(true)]
                : [previousValue, translatedPath, value];
            var updatedValue = factory.Function("json_set_value", arguments,
                nullable: false, argumentsPropagateNullability: encoded ? [false, false, false, false] : [false, false, false], column.Type, column.TypeMapping);
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

    private sealed record Target(SqlExpression? Sql, JsonScalarExpression? Json, IProperty? Property = null,
        ITypeBase? StructuralType = null, bool Collection = false);

    private Target BindTarget(ShapedQueryExpression source, LambdaExpression selector)
    {
        if (selector.Body is MaterializeCollectionNavigationExpression materialize)
            return BindTarget(source, Expression.Lambda(materialize.Subquery, selector.Parameters));
        if (selector.Body is MethodCallExpression { Method.Name: nameof(Queryable.AsQueryable), Arguments: [var collectionBody] })
            return BindTarget(source, Expression.Lambda(collectionBody, selector.Parameters));
        var sql = TranslateLambdaExpression(source, selector);
        var model = ResolveModelTarget(source, selector.Body, selector.Parameters[0]);
        if (sql is JsonScalarExpression scalar)
            return new Target(sql, scalar, model.Property, model.Type, model.Collection);
        if (TryGetFixedIndex(selector.Body, out var collectionExpression, out var arrayIndex))
        {
            var collection = BindTarget(source, Expression.Lambda(collectionExpression, selector.Parameters));
            if (collection is { Json: { } array, Property: { IsPrimitiveCollection: true } primitive }
                && primitive.GetElementType() is { } element && IsSupportedScalar(element.ClrType)
                && element.GetTypeMapping().Converter is null)
            {
                var mapping = RelationalDependencies.TypeMappingSource.FindMapping(element.ClrType);
                var indexed = new JsonScalarExpression(array.Json,
                    [.. array.Path, new PathSegment(RelationalDependencies.SqlExpressionFactory.ApplyDefaultTypeMapping(
                        RelationalDependencies.SqlExpressionFactory.Constant(arrayIndex)))],
                    element.ClrType, mapping, true);
                return new Target(indexed, indexed);
            }
        }
        if (model.Type is null || !(model.Type is IEntityType entityType && entityType.IsMappedToJson()
            || model.Type is IComplexType complexType && complexType.IsMappedToJson()))
            return new Target(sql, null);

        // Bind through a real mapped scalar so EF performs its normal ownership/index expansion.
        // Remove only the synthetic probe segments; never infer JSON names from CLR member names.
        Expression probe = selector.Body;
        var removed = 0;
        var probeType = model.Type;
        var many = model.Collection;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool foundScalar = false;
        for (int depth = 0; depth < 64 && clock.Elapsed < TimeSpan.FromSeconds(10); depth++)
        {
            if (many)
            {
                probe = Expression.Call(typeof(Queryable), nameof(Queryable.ElementAt), [probeType.ClrType],
                    Expression.Call(typeof(Queryable), nameof(Queryable.AsQueryable), [probeType.ClrType], probe), Expression.Constant(0));
                removed++;
            }
            var property = probeType.GetProperties().FirstOrDefault(static property => !property.IsShadowProperty()
                && property.GetJsonPropertyName() is not null);
            if (property is not null)
            {
                probe = Access(probe, property);
                removed++;
                foundScalar = true;
                break;
            }
            if (probeType.GetComplexProperties().FirstOrDefault() is { } complex)
            {
                probe = Access(probe, complex);
                probeType = complex.ComplexType;
                many = complex.IsCollection;
            }
            else if (probeType is IEntityType entity && entity.GetNavigations()
                .FirstOrDefault(static navigation => navigation.ForeignKey.IsOwnership && !navigation.IsOnDependent) is { } navigation)
            {
                probe = Access(probe, navigation);
                probeType = navigation.TargetEntityType;
                many = navigation.IsCollection;
            }
            else
                break;
            removed++;
        }
        if (!foundScalar)
            throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
        if (TranslateLambdaExpression(source, Expression.Lambda(probe, selector.Parameters)) is not JsonScalarExpression json)
            throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
        var target = new JsonScalarExpression(json.Json, json.Path.Take(json.Path.Count - removed).ToArray(), selector.ReturnType,
            json.Json.TypeMapping, json.IsNullable);
        return new Target(target, target, null, model.Type, model.Collection);
    }

    private static Expression Access(Expression instance, IPropertyBase property)
        => Expression.MakeMemberAccess(instance, (System.Reflection.MemberInfo?)property.PropertyInfo ?? property.FieldInfo
            ?? throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage));

    private static (IProperty? Property, ITypeBase? Type, bool Collection) ResolveModelTarget(
        ShapedQueryExpression source, Expression expression, ParameterExpression parameter)
    {
        if (expression == parameter && source.ShaperExpression is StructuralTypeShaperExpression shaper)
            return (null, shaper.StructuralType, false);
        if (expression is MaterializeCollectionNavigationExpression materialize)
            return ResolveModelTarget(source, materialize.Subquery, parameter);
        if (expression is UnaryExpression { NodeType: ExpressionType.Convert } convert)
            return ResolveModelTarget(source, convert.Operand, parameter);
        if (expression is MethodCallExpression { Method.Name: nameof(Queryable.AsQueryable), Arguments: [var collectionBody] })
            return ResolveModelTarget(source, collectionBody, parameter);
        Expression? parent = null;
        string? name = null;
        if (expression is MemberExpression member)
        {
            parent = member.Expression;
            name = member.Member.Name;
        }
        else if (expression is MethodCallExpression { Method.Name: nameof(EF.Property), Arguments: [var owner, ConstantExpression { Value: string propertyName }] })
        {
            parent = owner;
            name = propertyName;
        }
        if (parent is not null && name is not null)
        {
            var target = ResolveModelTarget(source, parent, parameter);
            if (target.Type is { } type && !target.Collection)
            {
                if (type.FindProperty(name) is { } property)
                    return (property, null, false);
                if (type.FindComplexProperty(name) is { } complex)
                    return (null, complex.ComplexType, complex.IsCollection);
                if (type is IEntityType entity && entity.FindNavigation(name) is { } navigation
                    && navigation.ForeignKey.IsOwnership && !navigation.IsOnDependent)
                    return (null, navigation.TargetEntityType, navigation.IsCollection);
            }
        }
        if (expression is MethodCallExpression call && call.Method.Name is "get_Item" or "ElementAt"
            && call.Arguments.LastOrDefault() is ConstantExpression { Value: int and >= 0 })
        {
            var collection = ResolveModelTarget(source, call.Object ?? call.Arguments[0], parameter);
            if (collection.Type is not null && collection.Collection)
                return (null, collection.Type, false);
        }
        return default;
    }

    private static bool TryGetFixedIndex(Expression expression, out Expression collection, out int index)
    {
        if (expression is MethodCallExpression call && call.Method.Name is "get_Item" or "ElementAt"
            && call.Arguments.LastOrDefault() is ConstantExpression { Value: int value } && value >= 0)
        {
            collection = call.Object ?? call.Arguments[0];
            index = value;
            return true;
        }
        if (expression is BinaryExpression { NodeType: ExpressionType.ArrayIndex, Right: ConstantExpression { Value: int arrayIndex } } binary
            && arrayIndex >= 0)
        {
            collection = binary.Left;
            index = arrayIndex;
            return true;
        }
        collection = expression;
        index = 0;
        return false;
    }

    private SqlExpression SerializeParameter(Expression value, IProperty? property, ITypeBase? type, JsonValueReaderWriter? codec, bool collection)
    {
        if (property is null && type is null && codec is null)
            throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
        if (value is LambdaExpression lambda)
            value = lambda.Body;
        while (value is UnaryExpression { NodeType: ExpressionType.Convert } convert)
            value = convert.Operand;
        Expression argument;
        if (value is QueryParameterExpression parameter)
        {
            Expression<Func<QueryContext, object?>> accessor = context => context.Parameters[parameter.Name];
            argument = ReplacingExpressionVisitor.Replace(accessor.Parameters[0], QueryCompilationContext.QueryContextParameter, accessor.Body);
        }
        else if (value is ConstantExpression constant)
            argument = Expression.Constant(constant.Value, typeof(object));
        else
            throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
        var body = ReplacingExpressionVisitor.Replace(SerializeValue.Parameters,
            [QueryCompilationContext.QueryContextParameter, argument, Expression.Constant(property, typeof(IProperty)),
                Expression.Constant(type, typeof(ITypeBase)), Expression.Constant(codec, typeof(JsonValueReaderWriter)),
                Expression.Constant(collection)], SerializeValue.Body);
        var runtime = QueryCompilationContext.RegisterRuntimeParameter("sonnetdb_json_value_" + _pathParameter++,
            Expression.Lambda(body, QueryCompilationContext.QueryContextParameter));
        return TranslateExpression(runtime)!;
    }

    private sealed class JsonValueAccessRewriter(SonnetDbQueryableMethodTranslatingExpressionVisitor owner,
        ShapedQueryExpression source, ParameterExpression parameter) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node)
        {
            if (node is MemberExpression or MethodCallExpression or BinaryExpression { NodeType: ExpressionType.ArrayIndex }
                && IsSupportedScalar(node.Type)
                && owner.BindTarget(source, Expression.Lambda(node, parameter)).Json is { TypeMapping.Converter: null } json)
            {
                var path = BuildPath(json);
                var runtime = owner.QueryCompilationContext.RegisterRuntimeParameter("sonnetdb_json_path_" + owner._pathParameter++,
                    Expression.Lambda(Expression.Constant(path), QueryCompilationContext.QueryContextParameter));
                return owner.RelationalDependencies.SqlExpressionFactory.Function("json_value",
                    [json.Json, owner.TranslateExpression(runtime)!], nullable: true,
                    argumentsPropagateNullability: [true, false], json.Type, json.TypeMapping);
            }
            return base.Visit(node);
        }
    }

    private static bool IsSupportedScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(string) || type == typeof(bool) || type == typeof(byte)
            || type == typeof(short) || type == typeof(int) || type == typeof(long)
            || type == typeof(float) || type == typeof(double);
    }

    internal static string BuildPath(JsonScalarExpression json)
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
