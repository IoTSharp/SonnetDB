using System.Diagnostics;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>仅在 measurement 的 time 伪列上下文保留原 DateTime 参数的 Unix 毫秒合同。</summary>
internal static class MeasurementDateTimeParameterBinder
{
    internal static SqlExpression BindInput(SqlExpression expression)
    {
        if (expression is CastExpression
            {
                IsParameterValue: true,
                TargetType: SqlDataType.DateTime2,
                Operand: LiteralExpression { Kind: SqlLiteralKind.Integer } ticks,
            })
        {
            var utc = new DateTime(ticks.IntegerValue, DateTimeKind.Utc);
            return LiteralExpression.Integer(new DateTimeOffset(utc).ToUnixTimeMilliseconds());
        }
        return expression;
    }

    internal static SqlExpression BindPredicate(SqlExpression expression)
    {
        long started = Stopwatch.GetTimestamp();
        int visited = 0;
        return Visit(expression, 0);

        SqlExpression Visit(SqlExpression node, int depth)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            if (++visited > 1_000_000 || depth > 128 || Stopwatch.GetElapsedTime(started).TotalSeconds > 30)
                throw new InvalidOperationException("measurement time 参数绑定超过有界预算。");
            return node switch
            {
                BinaryExpression binary => BindBinary(binary, depth),
                UnaryExpression unary => unary with { Operand = Visit(unary.Operand, depth + 1) },
                CastExpression cast => cast with { Operand = Visit(cast.Operand, depth + 1) },
                IsNullExpression isNull => isNull with { Operand = Visit(isNull.Operand, depth + 1) },
                InExpression membership => membership with
                {
                    Value = Visit(membership.Value, depth + 1),
                    Values = membership.Values.Select(value => IsTime(membership.Value)
                        ? BindInput(Visit(value, depth + 1)) : Visit(value, depth + 1)).ToArray(),
                },
                FunctionCallExpression function => function with
                {
                    Arguments = function.Arguments.Select(argument => Visit(argument, depth + 1)).ToArray(),
                },
                NamedArgumentExpression named => named with { Value = Visit(named.Value, depth + 1) },
                CaseExpression conditional => conditional with
                {
                    WhenClauses = conditional.WhenClauses.Select(clause => clause with
                    {
                        Condition = Visit(clause.Condition, depth + 1),
                        Result = Visit(clause.Result, depth + 1),
                    }).ToArray(),
                    Else = conditional.Else is null ? null : Visit(conditional.Else, depth + 1),
                },
                _ => node,
            };
        }

        SqlExpression BindBinary(BinaryExpression binary, int depth)
        {
            SqlExpression left = Visit(binary.Left, depth + 1);
            SqlExpression right = Visit(binary.Right, depth + 1);
            if (binary.Operator is SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual
                or SqlBinaryOperator.LessThan or SqlBinaryOperator.LessThanOrEqual
                or SqlBinaryOperator.GreaterThan or SqlBinaryOperator.GreaterThanOrEqual)
            {
                if (IsTime(left)) right = BindInput(right);
                if (IsTime(right)) left = BindInput(left);
            }
            return binary with { Left = left, Right = right };
        }
    }

    private static bool IsTime(SqlExpression expression)
        => expression is IdentifierExpression { Name: "time", IsQuoted: false, IsNameBound: true };
}
