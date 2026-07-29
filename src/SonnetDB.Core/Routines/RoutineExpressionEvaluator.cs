using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Routines;

internal static class RoutineExpressionEvaluator
{
    public static object? Evaluate(SqlExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return expression switch
        {
            LiteralExpression literal => EvaluateLiteral(literal),
            DurationLiteralExpression duration => duration.Milliseconds,
            UnaryExpression { Operator: SqlUnaryOperator.Negate } unary =>
                SqlScalarOperations.Negate(Evaluate(unary.Operand)),
            UnaryExpression { Operator: SqlUnaryOperator.Not } unary => NegateBoolean(EvaluateBoolean(unary.Operand)),
            BinaryExpression binary when binary.Operator is
                SqlBinaryOperator.Add or SqlBinaryOperator.Subtract or SqlBinaryOperator.Multiply or
                SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo =>
                SqlScalarOperations.EvaluateArithmetic(binary.Operator, Evaluate(binary.Left), Evaluate(binary.Right)),
            BinaryExpression binary when binary.Operator is SqlBinaryOperator.And or SqlBinaryOperator.Or =>
                EvaluateLogical(binary),
            BinaryExpression binary => EvaluateComparison(binary),
            IsNullExpression isNull => isNull.Negated
                ? Evaluate(isNull.Operand) is not null
                : Evaluate(isNull.Operand) is null,
            InExpression @in => EvaluateIn(@in),
            CaseExpression @case => EvaluateCase(@case),
            FunctionCallExpression function => EvaluateFunction(function),
            _ => throw new InvalidOperationException(
                $"例程常量表达式不支持 '{expression.GetType().Name}'。"),
        };
    }

    public static bool EvaluateWhen(SqlExpression expression)
        => EvaluateBoolean(expression) == true;

    private static object? EvaluateLiteral(LiteralExpression literal) => literal.Kind switch
    {
        SqlLiteralKind.Null => null,
        SqlLiteralKind.Boolean => literal.BooleanValue,
        SqlLiteralKind.Integer => literal.IntegerValue,
        SqlLiteralKind.Float => literal.FloatValue,
        SqlLiteralKind.String => literal.StringValue,
        _ => throw new InvalidOperationException($"未知 SQL 字面量 {literal.Kind}。"),
    };

    private static bool? EvaluateBoolean(SqlExpression expression)
    {
        object? value = Evaluate(expression);
        return value switch
        {
            null => null,
            bool boolean => boolean,
            byte number => number != 0,
            short number => number != 0,
            int number => number != 0,
            long number => number != 0,
            float number => number != 0,
            double number => number != 0,
            decimal number => number != 0,
            _ => throw new InvalidOperationException("例程布尔表达式必须计算为布尔值。"),
        };
    }

    private static bool? EvaluateLogical(BinaryExpression binary)
    {
        bool? left = EvaluateBoolean(binary.Left);
        if (binary.Operator == SqlBinaryOperator.And)
        {
            if (left == false)
                return false;
            bool? right = EvaluateBoolean(binary.Right);
            return right == false ? false : left is null || right is null ? null : true;
        }

        if (left == true)
            return true;
        bool? other = EvaluateBoolean(binary.Right);
        return other == true ? true : left is null || other is null ? null : false;
    }

    private static bool? EvaluateComparison(BinaryExpression binary)
    {
        object? left = Evaluate(binary.Left);
        object? right = Evaluate(binary.Right);
        if (left is null || right is null)
            return null;
        int? comparison = SqlScalarComparer.Compare(left, right);
        return binary.Operator switch
        {
            SqlBinaryOperator.Equal => SqlScalarComparer.ValuesEqual(left, right),
            SqlBinaryOperator.NotEqual => !SqlScalarComparer.ValuesEqual(left, right),
            SqlBinaryOperator.LessThan => comparison < 0,
            SqlBinaryOperator.LessThanOrEqual => comparison <= 0,
            SqlBinaryOperator.GreaterThan => comparison > 0,
            SqlBinaryOperator.GreaterThanOrEqual => comparison >= 0,
            SqlBinaryOperator.Like => LikePatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.NotLike => !LikePatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.Regex => RegexPatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.NotRegex => !RegexPatternMatcher.IsMatch(left, right),
            _ => throw new InvalidOperationException($"例程表达式不支持运算符 {binary.Operator}。"),
        };
    }

    private static bool? EvaluateIn(InExpression expression)
    {
        if (expression.Subquery is not null)
            throw new InvalidOperationException("例程常量表达式不支持 IN 子查询。");
        object? value = Evaluate(expression.Value);
        bool sawNull = value is null;
        foreach (var item in expression.Values)
        {
            object? candidate = Evaluate(item);
            if (candidate is null || value is null)
            {
                sawNull = true;
                continue;
            }
            if (SqlScalarComparer.ValuesEqual(value, candidate))
                return expression.Negated ? false : true;
        }
        return sawNull ? null : expression.Negated;
    }

    private static object? EvaluateCase(CaseExpression expression)
    {
        foreach (var clause in expression.WhenClauses)
        {
            if (EvaluateBoolean(clause.Condition) == true)
                return Evaluate(clause.Result);
        }
        return expression.Else is null ? null : Evaluate(expression.Else);
    }

    private static object? EvaluateFunction(FunctionCallExpression function)
    {
        if (function.IsStar)
            throw new InvalidOperationException($"例程常量函数 {function.Name}(*) 非法。");
        if (string.Equals(function.Name, "lower", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == 1)
            return Evaluate(function.Arguments[0])?.ToString()?.ToLowerInvariant();
        if (string.Equals(function.Name, "upper", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == 1)
            return Evaluate(function.Arguments[0])?.ToString()?.ToUpperInvariant();
        if (string.Equals(function.Name, "coalesce", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count > 0)
        {
            foreach (var argument in function.Arguments)
            {
                object? value = Evaluate(argument);
                if (value is not null)
                    return value;
            }
            return null;
        }
        throw new InvalidOperationException($"例程常量表达式不支持函数 '{function.Name}'。");
    }

    private static bool? NegateBoolean(bool? value)
        => value is null ? null : !value.Value;
}
