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

/// <summary>单个索引候选的确定性页访问估算。</summary>
internal readonly record struct TableIndexPageCostEstimate(
    long RootSeekPages,
    long LeafRangePages,
    long LogicalReads,
    bool UsedFallback);

internal static class TableCostPlanner
{
    private const int MinimumIndexFanout = 2;
    private const int MaximumIndexFanout = 256;
    private const int MaximumIndexTreeLevels = 32;

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
        var estimates = new List<IndexCandidateEstimate>(candidates.Count);
        foreach (TableIndexAccessPlan candidate in candidates)
        {
            long rows = EstimateIndexRows(store.RowCount, schema, candidate, statistics);
            TableIndexStatistics? indexStatistics = statistics.TryGetIndex(candidate.Index.Name);
            double averageEntryWidth = indexStatistics?.AverageEntryWidth ?? double.NaN;
            if (!double.IsFinite(averageEntryWidth) || averageEntryWidth <= 0)
                averageEntryWidth = statistics.AverageRowWidth;
            if (!double.IsFinite(averageEntryWidth) || averageEntryWidth <= 0)
                averageEntryWidth = 1;
            TableIndexPageCostEstimate pageCost = EstimateIndexPageCost(
                rows,
                indexStatistics,
                statistics.LogicalPageBytes);
            double cost = EstimateIndexCost(rows, pageCost.LogicalReads, averageEntryWidth);
            estimates.Add(new IndexCandidateEstimate(candidate, rows, pageCost, cost));
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
                + $" seek<={estimate.PageCost.RootSeekPages}"
                + $" leaf<={estimate.PageCost.LeafRangePages}"
                + $" pages<={estimate.PageCost.LogicalReads}"
                + (estimate.PageCost.UsedFallback ? " fallback=index_page_stats_unavailable" : string.Empty)
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
            best.PageCost.LogicalReads,
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

    internal static TableIndexPageCostEstimate EstimateIndexPageCost(
        long estimatedRows,
        TableIndexStatistics? statistics,
        int logicalPageBytes)
    {
        if (statistics is not { RowCount: > 0, LogicalPageCount: > 0 }
            || logicalPageBytes <= 0
            || !double.IsFinite(statistics.AverageEntryWidth)
            || statistics.AverageEntryWidth <= 0)
        {
            return new TableIndexPageCostEstimate(
                RootSeekPages: 0,
                LeafRangePages: Math.Max(1, estimatedRows),
                LogicalReads: Math.Max(1, estimatedRows),
                UsedFallback: true);
        }

        long rowCount = statistics.RowCount;
        long totalPages = statistics.LogicalPageCount;
        long boundedRows = Math.Clamp(estimatedRows, 0, rowCount);
        long leafRangePages = EstimateLeafRangePages(boundedRows, rowCount, totalPages);
        long rootSeekPages = EstimateRootSeekPages(
            totalPages,
            statistics.AverageEntryWidth,
            logicalPageBytes);
        long logicalReads = SaturatingAdd(rootSeekPages, leafRangePages);
        logicalReads = Math.Clamp(logicalReads, 1, totalPages);
        return new TableIndexPageCostEstimate(
            rootSeekPages,
            leafRangePages,
            logicalReads,
            UsedFallback: false);
    }

    internal static double EstimateIndexCost(long estimatedRows, long logicalReads, double averageEntryWidth)
    {
        long rows = Math.Max(0, estimatedRows);
        long reads = Math.Max(0, logicalReads);
        double width = double.IsFinite(averageEntryWidth) && averageEntryWidth > 0
            ? averageEntryWidth
            : 1;
        double rowCost = rows * (1d + width / 4096d);
        if (!double.IsFinite(rowCost))
            return double.MaxValue;

        double cost = 4d + reads * 0.5d + rowCost;
        return double.IsFinite(cost) ? cost : double.MaxValue;
    }

    private static long EstimateLeafRangePages(
        long estimatedRows,
        long rowCount,
        long totalPages)
    {
        if (estimatedRows <= 0)
            // Zero denotes an empty estimated matching span; the physical
            // terminal-leaf miss probe is intentionally outside this helper.
            return 0;

        double scaledPages = (double)estimatedRows * totalPages / rowCount;
        if (!double.IsFinite(scaledPages) || scaledPages >= long.MaxValue)
            return totalPages;

        long pages = Math.Max(1, (long)Math.Ceiling(scaledPages));
        return Math.Clamp(pages, 1, totalPages);
    }

    private static long EstimateRootSeekPages(
        long totalPages,
        double averageEntryWidth,
        int logicalPageBytes)
    {
        double entriesPerPageValue = logicalPageBytes / Math.Max(1d, averageEntryWidth);
        if (!double.IsFinite(entriesPerPageValue) || entriesPerPageValue < MinimumIndexFanout)
            entriesPerPageValue = MinimumIndexFanout;

        long fanout = Math.Clamp(
            (long)Math.Floor(entriesPerPageValue),
            MinimumIndexFanout,
            MaximumIndexFanout);
        long levelPages = totalPages;
        int levels = 1;
        for (int level = 0; level < MaximumIndexTreeLevels && levelPages > 1; level++)
        {
            levelPages = CeilingDivide(levelPages, fanout);
            levels++;
        }

        // Leaf pages are charged separately; only internal/root pages belong
        // to the seek component. A one-page index therefore costs one leaf read.
        return Math.Max(0, levels - 1);
    }

    private static long CeilingDivide(long value, long divisor)
        => value / divisor + (value % divisor == 0 ? 0 : 1);

    private static long SaturatingAdd(long left, long right)
        => right > long.MaxValue - left ? long.MaxValue : left + right;

    private readonly record struct IndexCandidateEstimate(
        TableIndexAccessPlan Plan,
        long Rows,
        TableIndexPageCostEstimate PageCost,
        double Cost);

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
