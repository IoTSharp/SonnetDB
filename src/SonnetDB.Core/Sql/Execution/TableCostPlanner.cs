using System.Globalization;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>关系表有限成本模型产生的访问路径估算。</summary>
internal sealed record TableAccessCostEstimate(
    string AccessPath,
    string? IndexName,
    long EstimatedRows,
    long EstimatedLogicalReads,
    double EstimatedRowWidth,
    double EstimatedCost,
    string EstimateSource,
    long? StatisticsSequence,
    long? StatisticsFreshnessMilliseconds,
    string CandidatePlans,
    string? FallbackReason,
    TableIndexAccessPlan? IndexPlan);

internal static class TableCostPlanner
{
    internal static TableAccessCostEstimate Estimate(
        TableStore store,
        TableSchema schema,
        SqlExpression? where,
        bool allowAutomaticRefresh)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schema);

        if (SqlTransactionContext.Current is { } transaction
            && transaction.TryGetBufferedMutations(schema.Name, out _))
        {
            return new TableAccessCostEstimate(
                "table_scan",
                null,
                store.RowCount,
                Math.Max(1, store.RowCount),
                0,
                store.RowCount,
                "transaction_overlay",
                null,
                null,
                $"table_scan rows<={store.RowCount}",
                "transaction_overlay_requires_scan",
                null);
        }

        if (allowAutomaticRefresh && store.RowCount >= 1_024)
            _ = store.TryAutomaticStatisticsRefresh();

        TableStatisticsState state = store.GetStatisticsState();
        TableStatistics? statistics = state.Statistics;
        TableIndexAccessPlan? heuristicPlan = TableSqlExecutor.ChooseBestIndexAccessPlan(schema, where);
        if (heuristicPlan is null)
        {
            return new TableAccessCostEstimate(
                "table_scan",
                null,
                store.RowCount,
                EstimateScanReads(store.RowCount, statistics),
                statistics?.AverageRowWidth ?? 0,
                EstimateScanCost(store.RowCount, statistics),
                state.EstimateSource,
                statistics?.SourceSequence,
                state.FreshnessMilliseconds,
                $"table_scan rows<={store.RowCount}",
                where is null ? null : "no_sargable_predicate",
                null);
        }

        // Small tables stay on the proven access-path contract. Cost-based scan
        // admission is useful only after a full-table read has a measurable cost.
        if (store.RowCount < 1_024 || statistics is null || state.IsStale)
        {
            long rows = HeuristicIndexRows(store.RowCount, heuristicPlan);
            return new TableAccessCostEstimate(
                FormatAccessPath(heuristicPlan),
                heuristicPlan.Index.Name,
                rows,
                rows,
                statistics?.AverageRowWidth ?? 0,
                rows,
                state.EstimateSource,
                statistics?.SourceSequence,
                state.FreshnessMilliseconds,
                $"{FormatAccessPath(heuristicPlan)}:{heuristicPlan.Index.Name} rows<={rows};table_scan rows<={store.RowCount}",
                null,
                heuristicPlan);
        }

        IReadOnlyList<TableIndexAccessPlan> candidates = TableSqlExecutor.CollectIndexAccessPlans(schema, where);
        var estimates = new List<(TableIndexAccessPlan Plan, long Rows, long Reads, double Cost)>(candidates.Count);
        foreach (TableIndexAccessPlan candidate in candidates)
        {
            long rows = EstimateIndexRows(store.RowCount, schema, candidate, statistics);
            TableIndexStatistics? indexStatistics = statistics.TryGetIndex(candidate.Index.Name);
            double averageEntryWidth = indexStatistics?.AverageEntryWidth ?? statistics.AverageRowWidth;
            long reads = Math.Max(1, rows);
            double cost = 4
                + (indexStatistics?.LogicalPageCount ?? 0) * 0.5
                + reads * (1 + averageEntryWidth / 4096);
            estimates.Add((candidate, rows, reads, cost));
        }

        var best = estimates
            .OrderBy(static estimate => estimate.Cost)
            .ThenByDescending(static estimate => estimate.Plan.Index.IsUnique && estimate.Plan.IsFullEquality)
            .ThenByDescending(static estimate => estimate.Plan.MatchedColumnCount)
            .ThenBy(static estimate => estimate.Plan.Index.Name, StringComparer.Ordinal)
            .First();
        long scanReads = EstimateScanReads(store.RowCount, statistics);
        double scanCost = EstimateScanCost(store.RowCount, statistics);
        string scanDescription = $"table_scan rows<={store.RowCount} cost={scanCost.ToString("F2", CultureInfo.InvariantCulture)}";
        string candidateDescriptions = string.Join(
            ";",
            estimates.Select(estimate =>
                $"{(ReferenceEquals(estimate.Plan, best.Plan) && best.Cost <= scanCost ? "*" : string.Empty)}"
                + $"{FormatAccessPath(estimate.Plan)}:{estimate.Plan.Index.Name} rows<={estimate.Rows}"
                + $" cost={estimate.Cost.ToString("F2", CultureInfo.InvariantCulture)}"));

        if (scanCost < best.Cost)
        {
            return new TableAccessCostEstimate(
                "table_scan",
                null,
                store.RowCount,
                scanReads,
                statistics.AverageRowWidth,
                scanCost,
                "refreshed",
                statistics.SourceSequence,
                state.FreshnessMilliseconds,
                candidateDescriptions + ";*" + scanDescription,
                "cost_model_table_scan",
                null);
        }

        return new TableAccessCostEstimate(
            FormatAccessPath(best.Plan),
            best.Plan.Index.Name,
            best.Rows,
            best.Reads,
            statistics.AverageRowWidth,
            best.Cost,
            "refreshed",
            statistics.SourceSequence,
            state.FreshnessMilliseconds,
            candidateDescriptions + ";" + scanDescription,
            null,
            best.Plan);
    }

    internal static long EstimateIndexRows(
        long tableRows,
        TableSchema schema,
        TableIndexAccessPlan plan,
        TableStatistics statistics)
    {
        if (tableRows == 0)
            return 0;
        if (plan.IsFullEquality && plan.Index.IsUnique)
            return 1;

        double estimate = tableRows;
        for (int index = 0; index < plan.EqualityPrefixValues.Count; index++)
        {
            string columnName = plan.Index.Columns[index];
            TableColumn? column = schema.TryGetColumn(columnName);
            TableColumnStatistics? columnStatistics = statistics.TryGetColumn(columnName);
            if (column is null || columnStatistics is null)
                continue;

            ulong fingerprint = TableValueFingerprint.Create(column, plan.EqualityPrefixValues[index]!);
            TableMostCommonValue? mcv = columnStatistics.MostCommonValues
                .FirstOrDefault(item => item.Fingerprint == fingerprint);
            double selectivity = mcv is not null
                ? Math.Clamp((double)mcv.EstimatedRows / tableRows, 1d / tableRows, 1)
                : Math.Clamp(
                    (1 - columnStatistics.NullFraction)
                        / Math.Max(1, columnStatistics.EstimatedDistinctCount),
                    1d / tableRows,
                    1);
            estimate *= selectivity;
        }

        if (plan.Range is { } range)
            estimate *= EstimateRangeSelectivity(tableRows, range, statistics);

        return Math.Clamp((long)Math.Ceiling(estimate), 0, tableRows);
    }

    private static double EstimateRangeSelectivity(
        long tableRows,
        TableIndexRange range,
        TableStatistics statistics)
    {
        TableColumnStatistics? column = statistics.TryGetColumn(range.Column.Name);
        if (column is null || column.Histogram.Count == 0)
            return 0.33;

        long total = column.Histogram.Sum(static bucket => bucket.EstimatedRows);
        if (total <= 0)
            return 0.33;

        long selected = 0;
        foreach (TableHistogramBucket bucket in column.Histogram)
        {
            double? upper = bucket.Int64UpperBound ?? bucket.Float64UpperBound;
            if (upper is null)
                continue;
            if (range.Lower is { } lower && upper.Value < lower.Value)
                continue;
            if (range.Upper is { } upperBound && upper.Value > upperBound.Value)
                break;
            selected = checked(selected + bucket.EstimatedRows);
        }

        return Math.Clamp((double)Math.Max(1, selected) / total, 1d / Math.Max(1, tableRows), 1);
    }

    private static long EstimateScanReads(long rows, TableStatistics? statistics)
        => statistics is { LogicalPageCount: > 0 }
            ? statistics.LogicalPageCount
            : Math.Max(1, rows);

    private static double EstimateScanCost(long rows, TableStatistics? statistics)
    {
        long reads = EstimateScanReads(rows, statistics);
        double width = statistics?.AverageRowWidth ?? 0;
        return reads + rows * (0.25 + width / 16_384);
    }

    private static long HeuristicIndexRows(long tableRows, TableIndexAccessPlan plan)
        => plan.IsFullEquality && plan.Index.IsUnique
            ? Math.Min(1, tableRows)
            : tableRows;

    private static string FormatAccessPath(TableIndexAccessPlan plan)
        => !string.IsNullOrWhiteSpace(plan.Index.JsonPath)
            ? "json_path_index"
            : plan.Range is not null
                ? "secondary_index_range"
                : plan.IsFullEquality ? "secondary_index" : "secondary_index_prefix";
}
