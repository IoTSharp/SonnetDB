using SonnetDB.Catalog;
using SonnetDB.Query;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

[Flags]
internal enum CrossModelFilterSource
{
    None = 0,
    Measurement = 1,
    Table = 2,
    Document = 4,
}

internal sealed record CrossModelFilterPlan(
    IReadOnlyList<SqlExpression> MeasurementPushdown,
    IReadOnlyList<SqlExpression> TablePushdown,
    IReadOnlyList<SqlExpression> Residual,
    SqlExpression? MeasurementExpression,
    SqlExpression? TableExpression,
    SqlExpression? ResidualExpression,
    WhereClause MeasurementWhere);

internal static class CrossModelFilterPlanner
{
    public static CrossModelFilterPlan Plan(
        SqlExpression? where,
        MeasurementSchema measurementSchema,
        Func<SqlExpression, CrossModelFilterSource> resolveSources)
    {
        ArgumentNullException.ThrowIfNull(measurementSchema);
        ArgumentNullException.ThrowIfNull(resolveSources);

        if (where is null)
        {
            var emptyWhere = new WhereClause(
                new Dictionary<string, string>(StringComparer.Ordinal),
                TimeRange.All,
                []);
            return new CrossModelFilterPlan([], [], [], null, null, null, emptyWhere);
        }

        var measurementPushdown = new List<SqlExpression>();
        var tablePushdown = new List<SqlExpression>();
        var residual = new List<SqlExpression>();

        foreach (var leaf in FlattenAnd(where))
        {
            var sources = resolveSources(leaf);
            if (sources == CrossModelFilterSource.Measurement
                && TryDecomposeMeasurementTimeTagLeaf(leaf, measurementSchema, out _))
            {
                measurementPushdown.Add(leaf);
                continue;
            }

            if (sources == CrossModelFilterSource.Table)
            {
                tablePushdown.Add(leaf);
                continue;
            }

            residual.Add(leaf);
        }

        var measurementExpression = BuildAnd(measurementPushdown);
        var measurementWhere = WhereClauseDecomposer.Decompose(measurementExpression, measurementSchema);
        return new CrossModelFilterPlan(
            measurementPushdown,
            tablePushdown,
            residual,
            measurementExpression,
            BuildAnd(tablePushdown),
            BuildAnd(residual),
            measurementWhere);
    }

    public static IEnumerable<SqlExpression> FlattenAnd(SqlExpression expression)
    {
        if (expression is BinaryExpression { Operator: SqlBinaryOperator.And } binary)
        {
            foreach (var left in FlattenAnd(binary.Left))
                yield return left;
            foreach (var right in FlattenAnd(binary.Right))
                yield return right;
            yield break;
        }

        yield return expression;
    }

    public static SqlExpression? BuildAnd(IReadOnlyList<SqlExpression> expressions)
    {
        if (expressions.Count == 0)
            return null;

        var current = expressions[0];
        for (int i = 1; i < expressions.Count; i++)
            current = new BinaryExpression(SqlBinaryOperator.And, current, expressions[i]);
        return current;
    }

    private static bool TryDecomposeMeasurementTimeTagLeaf(
        SqlExpression leaf,
        MeasurementSchema schema,
        out WhereClause clause)
    {
        try
        {
            clause = WhereClauseDecomposer.Decompose(leaf, schema);
            return clause.TagFilter.Count > 0 || clause.TimeRange != TimeRange.All;
        }
        catch (InvalidOperationException)
        {
            clause = default;
            return false;
        }
    }
}
