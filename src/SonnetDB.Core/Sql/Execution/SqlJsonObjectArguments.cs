using System.Globalization;
using SonnetDB.Query.Functions;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>在 JSON_OBJECT 的参数边界保留 decimal，避免旧模型标量求值器先转成 double。</summary>
internal static class SqlJsonObjectArguments
{
    internal static IReadOnlyList<SqlExpression> Prepare(FunctionCallExpression function)
    {
        if (!function.Name.Equals("json_object", StringComparison.OrdinalIgnoreCase)
            || UserFunctionRegistry.Current?.TryGetScalar(function.Name, out _) == true)
            return function.Arguments;

        int remaining = 4096;
        return function.Arguments.Select(argument => Rewrite(argument, 0)).ToArray();

        SqlExpression Rewrite(SqlExpression expression, int depth)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            if (--remaining < 0 || depth > 64)
                throw new InvalidOperationException("json_object 的参数表达式超过 4096 节点或 64 层上限。");
            // CAST 从 invariant 字符串直接读取 decimal；仅此构造器参数子树采用该精确路径。
            // JSON_VALUE 的 JSON 数字转换以及其它查询表达式的既有类型合同保持不变。
            return expression switch
            {
                LiteralExpression { Kind: SqlLiteralKind.Decimal } literal => new CastExpression(
                    LiteralExpression.String(literal.DecimalValue.ToString(CultureInfo.InvariantCulture)), SqlDataType.Decimal),
                CastExpression cast => cast with { Operand = Rewrite(cast.Operand, depth + 1) },
                UnaryExpression unary => unary with { Operand = Rewrite(unary.Operand, depth + 1) },
                BinaryExpression binary => binary with
                {
                    Left = Rewrite(binary.Left, depth + 1),
                    Right = Rewrite(binary.Right, depth + 1),
                },
                IsNullExpression isNull => isNull with { Operand = Rewrite(isNull.Operand, depth + 1) },
                InExpression { Subquery: null } list => list with
                {
                    Value = Rewrite(list.Value, depth + 1),
                    Values = list.Values.Select(value => Rewrite(value, depth + 1)).ToArray(),
                },
                CaseExpression conditional => conditional with
                {
                    WhenClauses = conditional.WhenClauses.Select(clause => clause with
                    {
                        Condition = Rewrite(clause.Condition, depth + 1),
                        Result = Rewrite(clause.Result, depth + 1),
                    }).ToArray(),
                    Else = conditional.Else is null ? null : Rewrite(conditional.Else, depth + 1),
                },
                FunctionCallExpression nested => nested with
                {
                    Arguments = nested.Arguments.Select(argument => Rewrite(argument, depth + 1)).ToArray(),
                },
                _ => expression,
            };
        }
    }
}
