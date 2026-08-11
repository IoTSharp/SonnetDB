using SonnetDB.Sql.Ast;

namespace SonnetDB.Views;

internal readonly record struct ViewDependencyAnalysis(
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> GraphDependencies,
    bool HasParameters);

internal static class ViewDependencyCollector
{
    public static ViewDependencyAnalysis Analyze(SelectStatement query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        var graphDependencies = new HashSet<string>(StringComparer.Ordinal);
        var hasParameters = false;
        VisitSelect(query, dependencies, graphDependencies, ref hasParameters);
        return new ViewDependencyAnalysis(
            dependencies.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
            graphDependencies.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
            hasParameters);
    }

    private static void VisitSelect(
        SelectStatement select,
        HashSet<string> dependencies,
        HashSet<string> graphDependencies,
        ref bool hasParameters)
    {
        if (select.FromSubquery is not null)
        {
            VisitSelect(select.FromSubquery, dependencies, graphDependencies, ref hasParameters);
        }
        else if (select.GraphTable is { } graphTable)
        {
            dependencies.Add(graphTable.GraphName);
            graphDependencies.Add(graphTable.GraphName);
        }
        else if (!string.IsNullOrEmpty(select.Measurement)
                 && !string.Equals(select.Measurement, "__json_file__", StringComparison.Ordinal))
        {
            dependencies.Add(select.Measurement);
        }

        foreach (var projection in select.Projections)
            VisitExpression(projection.Expression, dependencies, graphDependencies, ref hasParameters);
        if (select.Where is not null)
            VisitExpression(select.Where, dependencies, graphDependencies, ref hasParameters);
        foreach (var expression in select.GroupBy)
            VisitExpression(expression, dependencies, graphDependencies, ref hasParameters);
        if (select.Having is not null)
            VisitExpression(select.Having, dependencies, graphDependencies, ref hasParameters);
        foreach (var orderBy in select.OrderByList)
            VisitExpression(orderBy.Expression, dependencies, graphDependencies, ref hasParameters);
        if (select.Pagination is { } pagination)
        {
            VisitExpression(pagination.OffsetExpression, dependencies, graphDependencies, ref hasParameters);
            if (pagination.FetchExpression is not null)
                VisitExpression(pagination.FetchExpression, dependencies, graphDependencies, ref hasParameters);
        }

        if (select.TableValuedFunction is not null)
            VisitExpression(select.TableValuedFunction, dependencies, graphDependencies, ref hasParameters);
        if (select.GraphTable is { } graphSource)
        {
            if (graphSource.Predicate is not null)
                VisitExpression(graphSource.Predicate, dependencies, graphDependencies, ref hasParameters);
            foreach (var column in graphSource.Columns)
                VisitExpression(column.Expression, dependencies, graphDependencies, ref hasParameters);
        }

        foreach (var join in select.JoinClauses)
        {
            if (join.Subquery is null)
                dependencies.Add(join.TableName);
            else
                VisitSelect(join.Subquery, dependencies, graphDependencies, ref hasParameters);
            VisitExpression(join.On, dependencies, graphDependencies, ref hasParameters);
        }

        foreach (var union in select.UnionStatements)
            VisitSelect(union, dependencies, graphDependencies, ref hasParameters);
    }

    private static void VisitExpression(
        SqlExpression expression,
        HashSet<string> dependencies,
        HashSet<string> graphDependencies,
        ref bool hasParameters)
    {
        switch (expression)
        {
            case ParameterExpression:
                hasParameters = true;
                break;
            case BinaryExpression binary:
                VisitExpression(binary.Left, dependencies, graphDependencies, ref hasParameters);
                VisitExpression(binary.Right, dependencies, graphDependencies, ref hasParameters);
                break;
            case UnaryExpression unary:
                VisitExpression(unary.Operand, dependencies, graphDependencies, ref hasParameters);
                break;
            case IsNullExpression isNull:
                VisitExpression(isNull.Operand, dependencies, graphDependencies, ref hasParameters);
                break;
            case InExpression inExpression:
                VisitExpression(inExpression.Value, dependencies, graphDependencies, ref hasParameters);
                foreach (var value in inExpression.Values)
                    VisitExpression(value, dependencies, graphDependencies, ref hasParameters);
                if (inExpression.Subquery is not null)
                    VisitSelect(inExpression.Subquery, dependencies, graphDependencies, ref hasParameters);
                break;
            case FunctionCallExpression function:
                foreach (var argument in function.Arguments)
                    VisitExpression(argument, dependencies, graphDependencies, ref hasParameters);
                break;
            case NamedArgumentExpression named:
                VisitExpression(named.Value, dependencies, graphDependencies, ref hasParameters);
                break;
            case CaseExpression @case:
                foreach (var when in @case.WhenClauses)
                {
                    VisitExpression(when.Condition, dependencies, graphDependencies, ref hasParameters);
                    VisitExpression(when.Result, dependencies, graphDependencies, ref hasParameters);
                }
                if (@case.Else is not null)
                    VisitExpression(@case.Else, dependencies, graphDependencies, ref hasParameters);
                break;
            case SubqueryExpression subquery:
                VisitSelect(subquery.Select, dependencies, graphDependencies, ref hasParameters);
                break;
            case ExistsExpression exists:
                VisitSelect(exists.Select, dependencies, graphDependencies, ref hasParameters);
                break;
        }
    }
}
