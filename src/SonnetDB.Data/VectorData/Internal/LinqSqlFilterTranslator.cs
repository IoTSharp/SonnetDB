using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace SonnetDB.Data.VectorData.Internal;

internal interface ISqlFilterFieldResolver
{
    bool TryResolveField(string propertyName, [NotNullWhen(true)] out string? sqlExpression);
}

internal sealed record SqlParameterValue(string Name, object? Value);

internal sealed record SqlWhereClause(string Sql, IReadOnlyList<SqlParameterValue> Parameters)
{
    public static SqlWhereClause Empty { get; } = new(string.Empty, Array.Empty<SqlParameterValue>());
}

[RequiresUnreferencedCode("LINQ Filter 翻译依赖反射访问记录属性。")]
[RequiresDynamicCode("LINQ Filter 翻译会编译子表达式以求值常量。")]
internal static class LinqSqlFilterTranslator
{
    public static SqlWhereClause Translate<TRecord>(
        Expression<Func<TRecord, bool>> expression,
        ISqlFilterFieldResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(resolver);
        if (expression.Parameters.Count != 1)
            throw new NotSupportedException("SonnetDB VectorData Filter 仅支持单参数 lambda。");

        var parameters = new List<SqlParameterValue>();
        var sql = Visit(expression.Body, expression.Parameters[0], resolver, parameters);
        return new SqlWhereClause(sql, parameters);
    }

    private static string Visit(
        Expression node,
        ParameterExpression parameter,
        ISqlFilterFieldResolver resolver,
        List<SqlParameterValue> parameters)
    {
        node = StripConvert(node);
        return node switch
        {
            BinaryExpression { NodeType: ExpressionType.AndAlso } and =>
                "(" + Visit(and.Left, parameter, resolver, parameters) + " AND " +
                Visit(and.Right, parameter, resolver, parameters) + ")",
            BinaryExpression { NodeType: ExpressionType.Equal } equal =>
                TranslateEqual(equal, parameter, resolver, parameters),
            _ => throw new NotSupportedException(
                $"SonnetDB VectorData Filter 当前仅支持等值过滤和 && 组合；不支持表达式：{node}."),
        };
    }

    private static string TranslateEqual(
        BinaryExpression expression,
        ParameterExpression parameter,
        ISqlFilterFieldResolver resolver,
        List<SqlParameterValue> parameters)
    {
        var left = StripConvert(expression.Left);
        var right = StripConvert(expression.Right);
        MemberExpression member;
        Expression valueExpression;
        if (left is MemberExpression leftMember && IsParameterMember(leftMember, parameter))
        {
            member = leftMember;
            valueExpression = right;
        }
        else if (right is MemberExpression rightMember && IsParameterMember(rightMember, parameter))
        {
            member = rightMember;
            valueExpression = left;
        }
        else
        {
            throw new NotSupportedException("SonnetDB VectorData Filter 的等值比较必须包含一个记录属性。");
        }

        if (!resolver.TryResolveField(member.Member.Name, out var sqlExpression))
            throw new NotSupportedException($"属性 {member.Member.Name} 未映射为可过滤字段。");

        var value = EvaluateConstant(valueExpression);
        var parameterName = "@f" + parameters.Count;
        parameters.Add(new SqlParameterValue(parameterName, value));
        return $"{sqlExpression} = {parameterName}";
    }

    private static object? EvaluateConstant(Expression expression)
    {
        expression = StripConvert(expression);
        if (expression is ConstantExpression constant)
            return constant.Value;
        var lambda = Expression.Lambda(Expression.Convert(expression, typeof(object)));
        var compiled = (Func<object?>)lambda.Compile();
        return compiled();
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            expression = unary.Operand;
        return expression;
    }

    private static bool IsParameterMember(MemberExpression member, ParameterExpression parameter)
        => member.Expression == parameter;
}
