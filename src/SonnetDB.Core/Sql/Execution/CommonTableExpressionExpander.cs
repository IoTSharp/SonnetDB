using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// 把非递归 CTE 展开为关系执行器已经支持的派生表/子查询节点。
/// </summary>
internal static class CommonTableExpressionExpander
{
    /// <summary>展开查询树中的 CTE；没有 CTE 时返回原 AST 实例。</summary>
    public static SelectStatement Expand(SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (statement.CommonTableExpressions.Count == 0)
            return statement;

        return ExpandSelect(statement, new Dictionary<string, SelectStatement>(StringComparer.OrdinalIgnoreCase));
    }

    private static SelectStatement ExpandSelect(
        SelectStatement statement,
        IReadOnlyDictionary<string, SelectStatement> inheritedDefinitions)
    {
        var definitions = new Dictionary<string, SelectStatement>(StringComparer.OrdinalIgnoreCase);
        foreach (var inherited in inheritedDefinitions)
            definitions.Add(inherited.Key, inherited.Value);

        // CTE 按声明顺序可引用之前的 CTE；在加入当前定义前展开其查询，
        // 因而自然拒绝递归自引用（未解析的名称最终按普通表处理并给出表不存在错误）。
        foreach (CommonTableExpression cte in statement.CommonTableExpressions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(cte.Name);
            ArgumentNullException.ThrowIfNull(cte.Query);
            if (cte.ColumnNames is { Count: > 0 })
            {
                throw new NotSupportedException(
                    "CTE 输出列名列表尚未支持；请使用 SELECT 投影别名声明输出列名。");
            }

            if (definitions.ContainsKey(cte.Name))
                throw new InvalidOperationException($"CTE 名称重复：'{cte.Name}'。");

            definitions.Add(cte.Name, ExpandSelect(cte.Query, definitions));
        }

        SelectStatement? fromSubquery = statement.FromSubquery is null
            ? null
            : ExpandSelect(statement.FromSubquery, definitions);

        SelectStatement expanded = statement with
        {
            CommonTableExpressions = Array.Empty<CommonTableExpression>(),
            FromSubquery = fromSubquery,
            Projections = statement.Projections
                .Select(item => item with { Expression = ExpandExpression(item.Expression, definitions) })
                .ToArray(),
            Where = statement.Where is null ? null : ExpandExpression(statement.Where, definitions),
            GroupBy = statement.GroupBy.Select(item => ExpandExpression(item, definitions)).ToArray(),
            Having = statement.Having is null ? null : ExpandExpression(statement.Having, definitions),
            OrderBy = statement.OrderBy is null
                ? null
                : statement.OrderBy with { Expression = ExpandExpression(statement.OrderBy.Expression, definitions) },
            OrderByItems = statement.OrderByList
                .Select(item => item with { Expression = ExpandExpression(item.Expression, definitions) })
                .ToArray(),
            Pagination = statement.Pagination is null
                ? null
                : statement.Pagination with
                {
                    OffsetExpression = ExpandExpression(statement.Pagination.OffsetExpression, definitions),
                    FetchExpression = statement.Pagination.FetchExpression is null
                        ? null
                        : ExpandExpression(statement.Pagination.FetchExpression, definitions),
                },
            Joins = ExpandJoins(statement.JoinClauses, definitions),
            Join = null,
            Unions = statement.SetOperationList.Select(static item => item.Query).Select(item => ExpandSelect(item, definitions)).ToArray(),
            SetOperations = statement.SetOperationList
                .Select(item => item with { Query = ExpandSelect(item.Query, definitions) })
                .ToArray(),
        };

        if (definitions.TryGetValue(statement.Measurement, out SelectStatement? cteQuery)
            && statement.FromSubquery is null
            && statement.TableValuedFunction is null)
        {
            expanded = expanded with
            {
                FromSubquery = cteQuery,
                TableAlias = statement.TableAlias ?? statement.Measurement,
            };
        }

        return expanded;
    }

    private static IReadOnlyList<JoinClause> ExpandJoins(
        IReadOnlyList<JoinClause> joins,
        IReadOnlyDictionary<string, SelectStatement> definitions)
    {
        if (joins.Count == 0)
            return Array.Empty<JoinClause>();

        var expanded = new JoinClause[joins.Count];
        for (int index = 0; index < joins.Count; index++)
        {
            JoinClause join = joins[index];
            SelectStatement? subquery = join.Subquery is null
                ? null
                : ExpandSelect(join.Subquery, definitions);
            if (subquery is null && definitions.TryGetValue(join.TableName, out SelectStatement? cteQuery))
                subquery = cteQuery;

            expanded[index] = join with
            {
                On = ExpandExpression(join.On, definitions),
                Subquery = subquery,
            };
        }

        return expanded;
    }

    private static SqlExpression ExpandExpression(
        SqlExpression expression,
        IReadOnlyDictionary<string, SelectStatement> definitions)
        => expression switch
        {
            BinaryExpression binary => binary with
            {
                Left = ExpandExpression(binary.Left, definitions),
                Right = ExpandExpression(binary.Right, definitions),
            },
            UnaryExpression unary => unary with
            {
                Operand = ExpandExpression(unary.Operand, definitions),
            },
            CastExpression cast => cast with
            {
                Operand = ExpandExpression(cast.Operand, definitions),
            },
            IsNullExpression isNull => isNull with
            {
                Operand = ExpandExpression(isNull.Operand, definitions),
            },
            FunctionCallExpression function => function with
            {
                Arguments = function.Arguments.Select(item => ExpandExpression(item, definitions)).ToArray(),
            },
            NamedArgumentExpression named => named with
            {
                Value = ExpandExpression(named.Value, definitions),
            },
            SubqueryExpression subquery => subquery with
            {
                Select = ExpandSelect(subquery.Select, definitions),
            },
            ExistsExpression exists => exists with
            {
                Select = ExpandSelect(exists.Select, definitions),
            },
            InExpression inExpression => inExpression with
            {
                Value = ExpandExpression(inExpression.Value, definitions),
                Values = inExpression.Values.Select(item => ExpandExpression(item, definitions)).ToArray(),
                Subquery = inExpression.Subquery is null
                    ? null
                    : ExpandSelect(inExpression.Subquery, definitions),
            },
            CaseExpression @case => @case with
            {
                WhenClauses = @case.WhenClauses
                    .Select(item => item with
                    {
                        Condition = ExpandExpression(item.Condition, definitions),
                        Result = ExpandExpression(item.Result, definitions),
                    })
                    .ToArray(),
                Else = @case.Else is null ? null : ExpandExpression(@case.Else, definitions),
            },
            _ => expression,
        };
}
