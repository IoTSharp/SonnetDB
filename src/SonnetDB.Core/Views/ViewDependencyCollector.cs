using SonnetDB.Sql.Ast;

namespace SonnetDB.Views;

internal readonly record struct ViewDependencyAnalysis(
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> GraphDependencies,
    bool HasParameters);

internal static class ViewDependencyCollector
{
    public static ViewDependencyAnalysis Analyze(SelectStatement query)
        => Analyze(query, bindSourceName: null);

    internal static ViewDependencyAnalysis Analyze(
        SelectStatement query, Func<string, bool, string>? bindSourceName)
    {
        ArgumentNullException.ThrowIfNull(query);
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        var graphDependencies = new HashSet<string>(StringComparer.Ordinal);
        var hasParameters = false;
        VisitSelect(query, dependencies, graphDependencies, bindSourceName, ref hasParameters);
        return new ViewDependencyAnalysis(
            dependencies.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
            graphDependencies.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
            hasParameters);
    }

    private static void VisitSelect(
        SelectStatement select,
        HashSet<string> dependencies,
        HashSet<string> graphDependencies,
        Func<string, bool, string>? bindSourceName,
        ref bool hasParameters)
    {
        if (bindSourceName is not null && select.CommonTableExpressions.Count != 0)
        {
            var visibleNames = new List<string>();
            var outerBinder = bindSourceName;
            string BindSource(string name, bool quoted)
                => visibleNames.Any(candidate => string.Equals(candidate, name,
                    quoted ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                    ? string.Empty
                    : outerBinder(name, quoted);
            foreach (var cte in select.CommonTableExpressions)
            {
                if (select.IsRecursive)
                    visibleNames.Add(cte.Name);
                VisitSelect(cte.Query, dependencies, graphDependencies, BindSource, ref hasParameters);
                if (!select.IsRecursive)
                    visibleNames.Add(cte.Name);
            }
            bindSourceName = BindSource;
        }

        if (select.FromSubquery is not null)
        {
            VisitSelect(select.FromSubquery, dependencies, graphDependencies, bindSourceName, ref hasParameters);
        }
        else if (select.GraphTable is { } graphTable)
        {
            string graphName = bindSourceName?.Invoke(graphTable.GraphName, true) ?? graphTable.GraphName;
            dependencies.Add(graphName);
            graphDependencies.Add(graphName);
        }
        else if (!string.IsNullOrEmpty(select.Measurement)
                 && !string.Equals(select.Measurement, "__json_file__", StringComparison.Ordinal))
        {
            string name = bindSourceName?.Invoke(select.Measurement, select.MeasurementIsQuoted) ?? select.Measurement;
            if (!string.IsNullOrEmpty(name))
                dependencies.Add(name);
        }

        foreach (var projection in select.Projections)
            VisitExpression(projection.Expression, dependencies, graphDependencies, bindSourceName, ref hasParameters);
        if (select.Where is not null)
            VisitExpression(select.Where, dependencies, graphDependencies, bindSourceName, ref hasParameters);
        foreach (var expression in select.GroupBy)
            VisitExpression(expression, dependencies, graphDependencies, bindSourceName, ref hasParameters);
        if (select.Having is not null)
            VisitExpression(select.Having, dependencies, graphDependencies, bindSourceName, ref hasParameters);
        foreach (var orderBy in select.OrderByList)
            VisitExpression(orderBy.Expression, dependencies, graphDependencies, bindSourceName, ref hasParameters);
        if (select.Pagination is { } pagination)
        {
            VisitExpression(pagination.OffsetExpression, dependencies, graphDependencies, bindSourceName, ref hasParameters);
            if (pagination.FetchExpression is not null)
                VisitExpression(pagination.FetchExpression, dependencies, graphDependencies, bindSourceName, ref hasParameters);
        }

        if (select.TableValuedFunction is not null)
            VisitExpression(select.TableValuedFunction, dependencies, graphDependencies, bindSourceName, ref hasParameters);
        if (select.GraphTable is { } graphSource)
        {
            if (graphSource.Predicate is not null)
                VisitExpression(graphSource.Predicate, dependencies, graphDependencies, bindSourceName, ref hasParameters);
            foreach (var column in graphSource.Columns)
                VisitExpression(column.Expression, dependencies, graphDependencies, bindSourceName, ref hasParameters);
        }

        foreach (var join in select.JoinClauses)
        {
            if (join.Subquery is null)
            {
                string name = bindSourceName?.Invoke(join.TableName, join.TableNameIsQuoted) ?? join.TableName;
                if (!string.IsNullOrEmpty(name))
                    dependencies.Add(name);
            }
            else
                VisitSelect(join.Subquery, dependencies, graphDependencies, bindSourceName, ref hasParameters);
            VisitExpression(join.On, dependencies, graphDependencies, bindSourceName, ref hasParameters);
        }

        foreach (var union in select.UnionStatements)
            VisitSelect(union, dependencies, graphDependencies, bindSourceName, ref hasParameters);
    }

    private static void VisitExpression(
        SqlExpression expression,
        HashSet<string> dependencies,
        HashSet<string> graphDependencies,
        Func<string, bool, string>? bindSourceName,
        ref bool hasParameters)
    {
        switch (expression)
        {
            case ParameterExpression:
                hasParameters = true;
                break;
            case BinaryExpression binary:
                VisitExpression(binary.Left, dependencies, graphDependencies, bindSourceName, ref hasParameters);
                VisitExpression(binary.Right, dependencies, graphDependencies, bindSourceName, ref hasParameters);
                break;
            case UnaryExpression unary:
                VisitExpression(unary.Operand, dependencies, graphDependencies, bindSourceName, ref hasParameters);
                break;
            case CastExpression cast when bindSourceName is not null:
                VisitExpression(cast.Operand, dependencies, graphDependencies, bindSourceName, ref hasParameters);
                break;
            case IsNullExpression isNull:
                VisitExpression(isNull.Operand, dependencies, graphDependencies, bindSourceName, ref hasParameters);
                break;
            case InExpression inExpression:
                VisitExpression(inExpression.Value, dependencies, graphDependencies, bindSourceName, ref hasParameters);
                foreach (var value in inExpression.Values)
                    VisitExpression(value, dependencies, graphDependencies, bindSourceName, ref hasParameters);
                if (inExpression.Subquery is not null)
                    VisitSelect(inExpression.Subquery, dependencies, graphDependencies, bindSourceName, ref hasParameters);
                break;
            case FunctionCallExpression function:
                foreach (var argument in function.Arguments)
                    VisitExpression(argument, dependencies, graphDependencies, bindSourceName, ref hasParameters);
                if (bindSourceName is not null && function.Over is { } over)
                {
                    foreach (var partition in over.PartitionBy)
                        VisitExpression(partition, dependencies, graphDependencies, bindSourceName, ref hasParameters);
                    foreach (var order in over.OrderBy)
                        VisitExpression(order.Expression, dependencies, graphDependencies, bindSourceName, ref hasParameters);
                }
                break;
            case NamedArgumentExpression named:
                VisitExpression(named.Value, dependencies, graphDependencies, bindSourceName, ref hasParameters);
                break;
            case CaseExpression @case:
                foreach (var when in @case.WhenClauses)
                {
                    VisitExpression(when.Condition, dependencies, graphDependencies, bindSourceName, ref hasParameters);
                    VisitExpression(when.Result, dependencies, graphDependencies, bindSourceName, ref hasParameters);
                }
                if (@case.Else is not null)
                    VisitExpression(@case.Else, dependencies, graphDependencies, bindSourceName, ref hasParameters);
                break;
            case SubqueryExpression subquery:
                VisitSelect(subquery.Select, dependencies, graphDependencies, bindSourceName, ref hasParameters);
                break;
            case ExistsExpression exists:
                VisitSelect(exists.Select, dependencies, graphDependencies, bindSourceName, ref hasParameters);
                break;
        }
    }
}
