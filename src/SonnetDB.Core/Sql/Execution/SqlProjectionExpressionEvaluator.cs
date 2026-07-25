using System.Globalization;
using SonnetDB.Model;
using SonnetDB.Query.Functions;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// 为没有专用 schema 执行器的行集提供基础 SQL 投影表达式校验与求值。
/// </summary>
internal static class SqlProjectionExpressionEvaluator
{
    /// <summary>
    /// 在读取结果行之前校验表达式结构、列引用及标量函数参数个数。
    /// </summary>
    internal static void Validate(
        SqlExpression expression,
        Func<IdentifierExpression, bool> identifierExists,
        string context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(identifierExists);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        switch (expression)
        {
            case LiteralExpression or DurationLiteralExpression
                or VectorLiteralExpression or GeoPointLiteralExpression:
                return;
            case IdentifierExpression identifier:
                if (!identifierExists(identifier))
                    throw new InvalidOperationException($"{context} 没有输出列 '{identifier.Name}'。");
                return;
            case UnaryExpression unary when unary.Operator is SqlUnaryOperator.Negate or SqlUnaryOperator.Not:
                Validate(unary.Operand, identifierExists, context);
                return;
            case BinaryExpression binary when IsArithmeticOperator(binary.Operator)
                || IsLogicalOperator(binary.Operator)
                || IsComparisonOperator(binary.Operator):
                Validate(binary.Left, identifierExists, context);
                Validate(binary.Right, identifierExists, context);
                return;
            case IsNullExpression isNull:
                Validate(isNull.Operand, identifierExists, context);
                return;
            case InExpression { Subquery: null } inExpression:
                Validate(inExpression.Value, identifierExists, context);
                foreach (var value in inExpression.Values)
                    Validate(value, identifierExists, context);
                return;
            case CaseExpression caseExpression:
                foreach (var clause in caseExpression.WhenClauses)
                {
                    Validate(clause.Condition, identifierExists, context);
                    Validate(clause.Result, identifierExists, context);
                }
                if (caseExpression.Else is not null)
                    Validate(caseExpression.Else, identifierExists, context);
                return;
            case FunctionCallExpression function:
                ValidateFunction(function, identifierExists, context);
                return;
            default:
                throw new InvalidOperationException(
                    $"{context} 不支持投影表达式 '{expression.GetType().Name}'。");
        }
    }

    /// <summary>
    /// 在一行列值上计算已校验的基础投影表达式。
    /// </summary>
    internal static object? Evaluate(
        SqlExpression expression,
        Func<IdentifierExpression, object?> resolveIdentifier,
        string context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(resolveIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        return expression switch
        {
            LiteralExpression literal => EvaluateLiteral(literal),
            DurationLiteralExpression duration => duration.Milliseconds,
            VectorLiteralExpression vector => EvaluateVectorLiteral(vector),
            GeoPointLiteralExpression point => GeoPoint.Create(point.Lat, point.Lon),
            IdentifierExpression identifier => resolveIdentifier(identifier),
            UnaryExpression { Operator: SqlUnaryOperator.Negate } unary =>
                SqlScalarOperations.Negate(Evaluate(unary.Operand, resolveIdentifier, context)),
            UnaryExpression { Operator: SqlUnaryOperator.Not } unary =>
                NegatePredicate(EvaluatePredicate(unary.Operand, resolveIdentifier, context)),
            BinaryExpression binary when IsArithmeticOperator(binary.Operator) =>
                SqlScalarOperations.EvaluateArithmetic(
                    binary.Operator,
                    Evaluate(binary.Left, resolveIdentifier, context),
                    Evaluate(binary.Right, resolveIdentifier, context)),
            BinaryExpression binary when IsLogicalOperator(binary.Operator)
                || IsComparisonOperator(binary.Operator) =>
                EvaluatePredicate(binary, resolveIdentifier, context),
            IsNullExpression isNull => EvaluatePredicate(isNull, resolveIdentifier, context),
            InExpression inExpression => EvaluatePredicate(inExpression, resolveIdentifier, context),
            CaseExpression caseExpression => EvaluateCase(caseExpression, resolveIdentifier, context),
            FunctionCallExpression function => EvaluateFunction(function, resolveIdentifier, context),
            _ => throw new InvalidOperationException(
                $"{context} 不支持投影表达式 '{expression.GetType().Name}'。"),
        };
    }

    /// <summary>
    /// 校验标量函数存在、不是星号调用、参数个数正确，并递归校验全部实参。
    /// </summary>
    private static void ValidateFunction(
        FunctionCallExpression function,
        Func<IdentifierExpression, bool> identifierExists,
        string context)
    {
        if (function.IsStar || !FunctionRegistry.TryGetScalar(function.Name, out var scalarFunction))
            throw new InvalidOperationException($"{context} 不支持标量函数 '{function.Name}'。");

        if (function.Arguments.Count < scalarFunction.MinArgumentCount
            || function.Arguments.Count > scalarFunction.MaxArgumentCount)
        {
            string expected = scalarFunction.MinArgumentCount == scalarFunction.MaxArgumentCount
                ? scalarFunction.MinArgumentCount.ToString(CultureInfo.InvariantCulture)
                : $"{scalarFunction.MinArgumentCount}~{scalarFunction.MaxArgumentCount}";
            throw new InvalidOperationException(
                $"函数 {function.Name} 需要 {expected} 个参数，实际为 {function.Arguments.Count}。");
        }

        foreach (var argument in function.Arguments)
            Validate(argument, identifierExists, context);
    }

    /// <summary>
    /// 计算已注册标量函数的参数并调用函数实现。
    /// </summary>
    private static object? EvaluateFunction(
        FunctionCallExpression function,
        Func<IdentifierExpression, object?> resolveIdentifier,
        string context)
    {
        if (function.IsStar || !FunctionRegistry.TryGetScalar(function.Name, out var scalarFunction))
            throw new InvalidOperationException($"{context} 不支持标量函数 '{function.Name}'。");

        var arguments = function.Arguments
            .Select(argument => Evaluate(argument, resolveIdentifier, context))
            .ToArray();
        return scalarFunction.Evaluate(arguments);
    }

    /// <summary>
    /// 按 SQL 三值逻辑计算基础布尔谓词。
    /// </summary>
    private static bool? EvaluatePredicate(
        SqlExpression expression,
        Func<IdentifierExpression, object?> resolveIdentifier,
        string context)
    {
        switch (expression)
        {
            case BinaryExpression { Operator: SqlBinaryOperator.And } binary:
                {
                    var left = EvaluatePredicate(binary.Left, resolveIdentifier, context);
                    if (left == false) return false;
                    var right = EvaluatePredicate(binary.Right, resolveIdentifier, context);
                    if (right == false) return false;
                    return left is null || right is null ? null : true;
                }
            case BinaryExpression { Operator: SqlBinaryOperator.Or } binary:
                {
                    var left = EvaluatePredicate(binary.Left, resolveIdentifier, context);
                    if (left == true) return true;
                    var right = EvaluatePredicate(binary.Right, resolveIdentifier, context);
                    if (right == true) return true;
                    return left is null || right is null ? null : false;
                }
            case BinaryExpression binary when IsComparisonOperator(binary.Operator):
                return EvaluateComparison(binary, resolveIdentifier, context);
            case UnaryExpression { Operator: SqlUnaryOperator.Not } unary:
                return NegatePredicate(EvaluatePredicate(unary.Operand, resolveIdentifier, context));
            case IsNullExpression isNull:
                var isNullValue = Evaluate(isNull.Operand, resolveIdentifier, context) is null;
                return isNull.Negated ? !isNullValue : isNullValue;
            case InExpression inExpression:
                return EvaluateIn(inExpression, resolveIdentifier, context);
            default:
                var value = Evaluate(expression, resolveIdentifier, context);
                return value switch
                {
                    null => null,
                    bool boolean => boolean,
                    _ => throw new InvalidOperationException($"{context} 的谓词必须计算为布尔值。"),
                };
        }
    }

    /// <summary>
    /// 计算 CASE WHEN 分支，UNKNOWN 与 FALSE 都继续匹配后续分支。
    /// </summary>
    private static object? EvaluateCase(
        CaseExpression expression,
        Func<IdentifierExpression, object?> resolveIdentifier,
        string context)
    {
        foreach (var clause in expression.WhenClauses)
        {
            if (EvaluatePredicate(clause.Condition, resolveIdentifier, context) == true)
                return Evaluate(clause.Result, resolveIdentifier, context);
        }

        return expression.Else is null
            ? null
            : Evaluate(expression.Else, resolveIdentifier, context);
    }

    /// <summary>
    /// 计算基础比较表达式；任一操作数为 NULL 时返回 UNKNOWN。
    /// </summary>
    private static bool? EvaluateComparison(
        BinaryExpression expression,
        Func<IdentifierExpression, object?> resolveIdentifier,
        string context)
    {
        var left = Evaluate(expression.Left, resolveIdentifier, context);
        var right = Evaluate(expression.Right, resolveIdentifier, context);
        if (left is null || right is null)
            return null;

        int? comparison = SqlScalarComparer.Compare(left, right);
        return expression.Operator switch
        {
            SqlBinaryOperator.Equal => SqlScalarComparer.ValuesEqual(left, right),
            SqlBinaryOperator.NotEqual => !SqlScalarComparer.ValuesEqual(left, right),
            SqlBinaryOperator.LessThan => comparison is < 0,
            SqlBinaryOperator.LessThanOrEqual => comparison is <= 0,
            SqlBinaryOperator.GreaterThan => comparison is > 0,
            SqlBinaryOperator.GreaterThanOrEqual => comparison is >= 0,
            SqlBinaryOperator.Like => LikePatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.NotLike => !LikePatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.Regex => RegexPatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.NotRegex => !RegexPatternMatcher.IsMatch(left, right),
            _ => throw new InvalidOperationException(
                $"{context} 不支持比较运算符 {expression.Operator}。"),
        };
    }

    /// <summary>
    /// 计算不含子查询的 IN 谓词，并保留列表 NULL 导致的 UNKNOWN。
    /// </summary>
    private static bool? EvaluateIn(
        InExpression expression,
        Func<IdentifierExpression, object?> resolveIdentifier,
        string context)
    {
        if (expression.Subquery is not null)
            throw new InvalidOperationException($"{context} 的投影表达式不支持 IN 子查询。");

        var value = Evaluate(expression.Value, resolveIdentifier, context);
        if (value is null)
            return null;

        bool sawNull = false;
        foreach (var item in expression.Values)
        {
            var candidate = Evaluate(item, resolveIdentifier, context);
            if (candidate is null)
            {
                sawNull = true;
                continue;
            }
            if (SqlScalarComparer.ValuesEqual(value, candidate))
                return expression.Negated ? false : true;
        }

        if (sawNull)
            return null;
        return expression.Negated ? true : false;
    }

    /// <summary>
    /// 对可空布尔值应用 SQL NOT。
    /// </summary>
    private static bool? NegatePredicate(bool? value)
        => value is null ? null : !value.Value;

    /// <summary>
    /// 将 SQL 字面量转换为执行期值。
    /// </summary>
    private static object? EvaluateLiteral(LiteralExpression literal) => literal.Kind switch
    {
        SqlLiteralKind.Null => null,
        SqlLiteralKind.Boolean => literal.BooleanValue,
        SqlLiteralKind.Integer => literal.IntegerValue,
        SqlLiteralKind.Float => literal.FloatValue,
        SqlLiteralKind.String => literal.StringValue,
        _ => throw new InvalidOperationException($"不支持的字面量类型 {literal.Kind}。"),
    };

    /// <summary>
    /// 将向量字面量转换为标量函数使用的单精度数组。
    /// </summary>
    private static float[] EvaluateVectorLiteral(VectorLiteralExpression vector)
    {
        var result = new float[vector.Components.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = checked((float)vector.Components[i]);
        return result;
    }

    /// <summary>
    /// 判断基础数值算术运算符。
    /// </summary>
    private static bool IsArithmeticOperator(SqlBinaryOperator value) => value is
        SqlBinaryOperator.Add or SqlBinaryOperator.Subtract or SqlBinaryOperator.Multiply
        or SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo;

    /// <summary>
    /// 判断 SQL 逻辑连接运算符。
    /// </summary>
    private static bool IsLogicalOperator(SqlBinaryOperator value)
        => value is SqlBinaryOperator.And or SqlBinaryOperator.Or;

    /// <summary>
    /// 判断基础比较运算符。
    /// </summary>
    private static bool IsComparisonOperator(SqlBinaryOperator value) => value is
        SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual
        or SqlBinaryOperator.LessThan or SqlBinaryOperator.LessThanOrEqual
        or SqlBinaryOperator.GreaterThan or SqlBinaryOperator.GreaterThanOrEqual
        or SqlBinaryOperator.Like or SqlBinaryOperator.NotLike
        or SqlBinaryOperator.Regex or SqlBinaryOperator.NotRegex;
}
