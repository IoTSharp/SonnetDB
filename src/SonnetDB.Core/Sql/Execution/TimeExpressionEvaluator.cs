using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// 仅用于 <c>WHERE time</c> 比较的时间表达式求值器。
/// 支持 Unix 毫秒整数字面量、duration 字面量、<c>now()</c>，以及由这些节点组成的算术表达式。
/// </summary>
internal static class TimeExpressionEvaluator
{
    internal static long Evaluate(SqlExpression expression, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(expression);

        return expression switch
        {
            LiteralExpression { Kind: SqlLiteralKind.Integer, IntegerValue: var value } => value,
            DurationLiteralExpression { Milliseconds: var value } => value,
            FunctionCallExpression { Name: var name, IsStar: false, Arguments.Count: 0 }
                when string.Equals(name, "now", StringComparison.OrdinalIgnoreCase) => nowMs,
            UnaryExpression { Operator: SqlUnaryOperator.Negate, Operand: var operand }
                => checked(-Evaluate(operand, nowMs)),
            BinaryExpression { Operator: SqlBinaryOperator.Add, Left: var left, Right: var right }
                => checked(Evaluate(left, nowMs) + Evaluate(right, nowMs)),
            BinaryExpression { Operator: SqlBinaryOperator.Subtract, Left: var left, Right: var right }
                => checked(Evaluate(left, nowMs) - Evaluate(right, nowMs)),
            BinaryExpression { Operator: SqlBinaryOperator.Multiply, Left: var left, Right: var right }
                => checked(Evaluate(left, nowMs) * Evaluate(right, nowMs)),
            BinaryExpression { Operator: SqlBinaryOperator.Divide, Left: var left, Right: var right }
                => checked(Evaluate(left, nowMs) / EvaluateNonZero(right, nowMs)),
            BinaryExpression { Operator: SqlBinaryOperator.Modulo, Left: var left, Right: var right }
                => checked(Evaluate(left, nowMs) % EvaluateNonZero(right, nowMs)),
            _ => throw new InvalidOperationException(
                "WHERE 中 'time' 比较的右值仅支持整数字面量（Unix 毫秒）、duration 字面量，以及 now() 参与的算术表达式。"),
        };
    }

    private static long EvaluateNonZero(SqlExpression expression, long nowMs)
    {
        var value = Evaluate(expression, nowMs);
        if (value == 0)
            throw new InvalidOperationException("WHERE 中 'time' 比较的右值包含除以 0。");
        return value;
    }
}
