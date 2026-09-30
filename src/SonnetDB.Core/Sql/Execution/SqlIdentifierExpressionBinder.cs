using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

internal static class SqlIdentifierExpressionBinder
{
    internal static SqlExpression Rewrite(
        SqlExpression expression,
        Func<IdentifierExpression, IdentifierExpression> bindIdentifier,
        Func<SelectStatement, SelectStatement>? bindSelect = null)
    {
        return expression switch
        {
            IdentifierExpression identifier => bindIdentifier(identifier),
            BinaryExpression binary => binary with
            {
                Left = Rewrite(binary.Left, bindIdentifier, bindSelect),
                Right = Rewrite(binary.Right, bindIdentifier, bindSelect),
            },
            UnaryExpression unary => unary with
            {
                Operand = Rewrite(unary.Operand, bindIdentifier, bindSelect),
            },
            CastExpression cast => cast with
            {
                Operand = Rewrite(cast.Operand, bindIdentifier, bindSelect),
            },
            IsNullExpression isNull => isNull with
            {
                Operand = Rewrite(isNull.Operand, bindIdentifier, bindSelect),
            },
            InExpression inExpression => inExpression with
            {
                Value = Rewrite(inExpression.Value, bindIdentifier, bindSelect),
                Values = RewriteList(inExpression.Values, bindIdentifier, bindSelect),
                Subquery = inExpression.Subquery is null || bindSelect is null
                    ? inExpression.Subquery
                    : bindSelect(inExpression.Subquery),
            },
            FunctionCallExpression function => function with
            {
                Arguments = RewriteList(function.Arguments, bindIdentifier, bindSelect),
                Over = function.Over is null
                    ? null
                    : function.Over with
                    {
                        PartitionBy = RewriteList(function.Over.PartitionBy, bindIdentifier, bindSelect),
                        OrderBy = function.Over.OrderBy
                            .Select(item => item with
                            {
                                Expression = Rewrite(item.Expression, bindIdentifier, bindSelect),
                            })
                            .ToArray(),
                    },
            },
            NamedArgumentExpression named => named with
            {
                Value = Rewrite(named.Value, bindIdentifier, bindSelect),
            },
            CaseExpression caseExpression => caseExpression with
            {
                WhenClauses = caseExpression.WhenClauses
                    .Select(clause => clause with
                    {
                        Condition = Rewrite(clause.Condition, bindIdentifier, bindSelect),
                        Result = Rewrite(clause.Result, bindIdentifier, bindSelect),
                    })
                    .ToArray(),
                Else = caseExpression.Else is null
                    ? null
                    : Rewrite(caseExpression.Else, bindIdentifier, bindSelect),
            },
            SubqueryExpression subquery when bindSelect is not null => subquery with
            {
                Select = bindSelect(subquery.Select),
            },
            ExistsExpression exists when bindSelect is not null => exists with
            {
                Select = bindSelect(exists.Select),
            },
            LiteralExpression or DefaultValueExpression or DurationLiteralExpression
                or VectorLiteralExpression or GeoPointLiteralExpression or ParameterExpression
                or StarExpression or SubqueryExpression or ExistsExpression
                or MaterializedSubqueryValueExpression => expression,
            _ => throw new NotSupportedException(
                $"标识符绑定不支持表达式类型 '{expression.GetType().Name}'。"),
        };
    }

    internal static IReadOnlyList<SqlExpression> RewriteList(
        IReadOnlyList<SqlExpression> expressions,
        Func<IdentifierExpression, IdentifierExpression> bindIdentifier,
        Func<SelectStatement, SelectStatement>? bindSelect = null)
        => expressions.Select(item => Rewrite(item, bindIdentifier, bindSelect)).ToArray();
}
