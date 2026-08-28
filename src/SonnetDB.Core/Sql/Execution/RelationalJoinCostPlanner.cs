using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

internal enum RelationalHashBuildSide
{
    Left,
    Right,
}

internal readonly record struct RelationalJoinInputEstimate(long Rows, double RowWidth)
{
    public double EstimatedBytes => RelationalJoinCostPlanner.EstimateBytes(this);
}

internal sealed record RelationalJoinOperatorPlan(
    int Ordinal,
    string Operator,
    RelationalHashBuildSide BuildSide,
    RelationalJoinInputEstimate BuildEstimate,
    RelationalJoinInputEstimate ProbeEstimate,
    RelationalJoinInputEstimate OutputEstimate,
    string? FallbackReason = null,
    string? IndexName = null);

internal sealed record RelationalJoinPipelinePlan(
    IReadOnlyList<RelationalJoinOperatorPlan> Operators,
    string JoinOrder,
    int JoinOrderCandidateCount,
    bool JoinOrderReordered,
    string? JoinOrderFallbackReason);

internal static class RelationalJoinCostPlanner
{
    private const double UnknownColumnWidth = 16;

    internal const double MergeJoinMinimumAvoidedBuildBytes = 64 * 1024;

    public static RelationalHashBuildSide ChooseHashBuildSide(
        JoinKind kind,
        RelationalJoinInputEstimate left,
        RelationalJoinInputEstimate right)
    {
        if (kind == JoinKind.Left)
            return RelationalHashBuildSide.Right;

        return EstimateBytes(left) < EstimateBytes(right)
            ? RelationalHashBuildSide.Left
            : RelationalHashBuildSide.Right;
    }

    public static RelationalJoinInputEstimate Combine(
        JoinKind kind,
        RelationalJoinInputEstimate left,
        RelationalJoinInputEstimate right)
    {
        long rows = kind == JoinKind.Left
            ? left.Rows
            : left.Rows == 0 || right.Rows == 0
                ? 0
                : Math.Max(left.Rows, right.Rows);
        double width = SaturatingAdd(left.RowWidth, right.RowWidth);
        return new RelationalJoinInputEstimate(rows, width);
    }

    public static double EstimateProjectedRowWidth(
        IReadOnlyList<TableColumn> allColumns,
        IReadOnlyList<TableColumn> selectedColumns,
        double measuredAverageRowWidth)
    {
        double selectedWeight = SumColumnWeights(selectedColumns);
        if (measuredAverageRowWidth <= 0 || !double.IsFinite(measuredAverageRowWidth))
            return Math.Max(1, selectedWeight);

        double allWeight = SumColumnWeights(allColumns);
        if (allWeight <= 0)
            return Math.Max(1, measuredAverageRowWidth);
        return Math.Max(1, measuredAverageRowWidth * selectedWeight / allWeight);
    }

    public static double EstimateUnknownRowWidth(int columnCount)
        => Math.Max(1, columnCount * UnknownColumnWidth);

    public static double EstimateBytes(RelationalJoinInputEstimate estimate)
    {
        double width = estimate.RowWidth > 0 && double.IsFinite(estimate.RowWidth)
            ? estimate.RowWidth
            : 1;
        double bytes = estimate.Rows * width;
        return double.IsFinite(bytes) ? bytes : double.MaxValue;
    }

    public static double EstimateHashJoinCost(
        RelationalJoinInputEstimate left,
        RelationalJoinInputEstimate right,
        RelationalHashBuildSide buildSide)
    {
        RelationalJoinInputEstimate build = buildSide == RelationalHashBuildSide.Left ? left : right;
        return SaturatingAdd(left.Rows, right.Rows)
            + EstimateBytes(build) / 4096;
    }

    public static double EstimateIndexNestedLoopCost(
        RelationalJoinInputEstimate probe,
        RelationalJoinInputEstimate indexed,
        bool uniqueLookup)
    {
        if (probe.Rows == 0)
            return 0;

        double seekCost = Math.Max(1, Math.Log2(Math.Max(2, indexed.Rows)));
        double matchesPerProbe = uniqueLookup
            ? 1
            : Math.Max(1, indexed.Rows / Math.Max(1d, probe.Rows));
        return probe.Rows * (seekCost + matchesPerProbe);
    }

    public static double EstimateMergeJoinCost(
        RelationalJoinInputEstimate left,
        RelationalJoinInputEstimate right)
        => SaturatingAdd(left.Rows, right.Rows);

    private static double SumColumnWeights(IReadOnlyList<TableColumn> columns)
    {
        double total = 0;
        foreach (TableColumn column in columns)
            total += GetColumnWeight(column.DataType);
        return total;
    }

    private static double GetColumnWeight(TableColumnType type)
        => type switch
        {
            TableColumnType.Boolean => 1,
            TableColumnType.Int64 or TableColumnType.Float64 or TableColumnType.DateTime => 8,
            TableColumnType.String or TableColumnType.Blob or TableColumnType.Json => 32,
            _ => UnknownColumnWidth,
        };

    private static double SaturatingAdd(double left, double right)
    {
        double sum = left + right;
        return double.IsFinite(sum) ? sum : double.MaxValue;
    }
}
