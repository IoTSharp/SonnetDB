using SonnetDB.Sql.Ast;

namespace SonnetDB.Views;

internal static class ViewExpander
{
    private const int MaxExpansionDepth = 32;

    public static SelectStatement Expand(ViewCatalog catalog, SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(statement);
        return ExpandSelect(catalog, statement, new List<string>());
    }

    private static SelectStatement ExpandSelect(
        ViewCatalog catalog,
        SelectStatement select,
        List<string> expansionPath)
    {
        string measurement = select.Measurement;
        string? tableAlias = select.TableAlias;
        SelectStatement? fromSubquery;
        if (select.FromSubquery is not null)
        {
            fromSubquery = ExpandSelect(catalog, select.FromSubquery, expansionPath);
        }
        else if (select.TableValuedFunction is null
                 && select.GraphTable is null
                 && catalog.TryGet(select.Measurement) is { } view)
        {
            fromSubquery = ExpandDefinition(catalog, view, expansionPath);
            tableAlias ??= select.Measurement;
            measurement = tableAlias;
        }
        else
        {
            fromSubquery = null;
        }

        var projections = select.Projections
            .Select(item => item with
            {
                Expression = ExpandExpression(catalog, item.Expression, expansionPath),
            })
            .ToArray();
        var where = select.Where is null
            ? null
            : ExpandExpression(catalog, select.Where, expansionPath);
        var groupBy = select.GroupBy
            .Select(expression => ExpandExpression(catalog, expression, expansionPath))
            .ToArray();
        var having = select.Having is null
            ? null
            : ExpandExpression(catalog, select.Having, expansionPath);
        var orderBy = select.OrderByList
            .Select(item => item with
            {
                Expression = ExpandExpression(catalog, item.Expression, expansionPath),
            })
            .ToArray();
        var joins = select.JoinClauses
            .Select(join => ExpandJoin(catalog, join, expansionPath))
            .ToArray();
        var unions = select.UnionStatements
            .Select(union => ExpandSelect(catalog, union, expansionPath))
            .ToArray();
        var tableValuedFunction = select.TableValuedFunction is null
            ? null
            : (FunctionCallExpression)ExpandExpression(
                catalog,
                select.TableValuedFunction,
                expansionPath);
        var graphTable = select.GraphTable is null
            ? null
            : select.GraphTable with
            {
                Predicate = select.GraphTable.Predicate is null
                    ? null
                    : ExpandExpression(catalog, select.GraphTable.Predicate, expansionPath),
                Columns = select.GraphTable.Columns.Select(item => item with
                {
                    Expression = ExpandExpression(catalog, item.Expression, expansionPath),
                }).ToArray(),
            };
        var pagination = select.Pagination is null
            ? null
            : select.Pagination with
            {
                OffsetExpression = ExpandExpression(
                    catalog,
                    select.Pagination.OffsetExpression,
                    expansionPath),
                FetchExpression = select.Pagination.FetchExpression is null
                    ? null
                    : ExpandExpression(
                        catalog,
                        select.Pagination.FetchExpression,
                        expansionPath),
            };

        return select with
        {
            Measurement = measurement,
            TableAlias = tableAlias,
            FromSubquery = fromSubquery,
            Projections = projections,
            Where = where,
            GroupBy = groupBy,
            Having = having,
            OrderBy = null,
            OrderByItems = orderBy,
            Join = null,
            Joins = joins,
            Unions = unions,
            TableValuedFunction = tableValuedFunction,
            GraphTable = graphTable,
            Pagination = pagination,
        };
    }

    private static SelectStatement ExpandDefinition(
        ViewCatalog catalog,
        ViewDefinition definition,
        List<string> expansionPath)
    {
        int existingIndex = expansionPath.IndexOf(definition.Name);
        if (existingIndex >= 0)
        {
            string cycle = string.Join(
                " -> ",
                expansionPath.Skip(existingIndex).Append(definition.Name));
            throw new InvalidOperationException($"逻辑视图存在循环依赖：{cycle}。");
        }
        if (expansionPath.Count >= MaxExpansionDepth)
        {
            throw new InvalidOperationException(
                $"逻辑视图展开深度超过上限 {MaxExpansionDepth}。");
        }

        expansionPath.Add(definition.Name);
        try
        {
            return ExpandSelect(catalog, definition.Query, expansionPath);
        }
        finally
        {
            expansionPath.RemoveAt(expansionPath.Count - 1);
        }
    }

    private static JoinClause ExpandJoin(
        ViewCatalog catalog,
        JoinClause join,
        List<string> expansionPath)
    {
        var on = ExpandExpression(catalog, join.On, expansionPath);
        if (join.Subquery is not null)
        {
            return join with
            {
                On = on,
                Subquery = ExpandSelect(catalog, join.Subquery, expansionPath),
            };
        }

        var view = catalog.TryGet(join.TableName);
        return view is null
            ? join with { On = on }
            : join with
            {
                On = on,
                Subquery = ExpandDefinition(catalog, view, expansionPath),
            };
    }

    private static SqlExpression ExpandExpression(
        ViewCatalog catalog,
        SqlExpression expression,
        List<string> expansionPath)
    {
        return expression switch
        {
            BinaryExpression binary => binary with
            {
                Left = ExpandExpression(catalog, binary.Left, expansionPath),
                Right = ExpandExpression(catalog, binary.Right, expansionPath),
            },
            UnaryExpression unary => unary with
            {
                Operand = ExpandExpression(catalog, unary.Operand, expansionPath),
            },
            IsNullExpression isNull => isNull with
            {
                Operand = ExpandExpression(catalog, isNull.Operand, expansionPath),
            },
            InExpression inExpression => inExpression with
            {
                Value = ExpandExpression(catalog, inExpression.Value, expansionPath),
                Values = inExpression.Values
                    .Select(value => ExpandExpression(catalog, value, expansionPath))
                    .ToArray(),
                Subquery = inExpression.Subquery is null
                    ? null
                    : ExpandSelect(catalog, inExpression.Subquery, expansionPath),
            },
            FunctionCallExpression function => function with
            {
                Arguments = function.Arguments
                    .Select(argument => ExpandExpression(catalog, argument, expansionPath))
                    .ToArray(),
            },
            NamedArgumentExpression named => named with
            {
                Value = ExpandExpression(catalog, named.Value, expansionPath),
            },
            CaseExpression @case => @case with
            {
                WhenClauses = @case.WhenClauses.Select(when => when with
                {
                    Condition = ExpandExpression(catalog, when.Condition, expansionPath),
                    Result = ExpandExpression(catalog, when.Result, expansionPath),
                }).ToArray(),
                Else = @case.Else is null
                    ? null
                    : ExpandExpression(catalog, @case.Else, expansionPath),
            },
            SubqueryExpression subquery => subquery with
            {
                Select = ExpandSelect(catalog, subquery.Select, expansionPath),
            },
            ExistsExpression exists => exists with
            {
                Select = ExpandSelect(catalog, exists.Select, expansionPath),
            },
            _ => expression,
        };
    }
}
