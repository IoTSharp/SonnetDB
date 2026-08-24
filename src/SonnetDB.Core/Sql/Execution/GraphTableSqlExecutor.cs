using System.Diagnostics;
using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Graphs;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>SQL/PGQ <c>GRAPH_TABLE MATCH COLUMNS</c> 固定一跳模式执行器。</summary>
internal static class GraphTableSqlExecutor
{
    private const int MaxAnchorRows = 10_000;
    private const int MaxMatchedRows = 100_000;
    private const int MaxRelationScanRows = 10_000;
    private static readonly TimeSpan MaxRelationScanDuration = TimeSpan.FromMilliseconds(50);

    internal static SelectExecutionResult Execute(Tsdb tsdb, SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        return ExecuteCore(tsdb, statement).Result;
    }

    internal static SelectExecutionResult Explain(Tsdb tsdb, SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        GraphTableSource source = statement.GraphTable
            ?? throw new InvalidOperationException("GRAPH_TABLE EXPLAIN 缺少 typed source。");
        EnsureSupportedShape(statement);
        ValidatePathPattern(source);
        GraphTableExecutionPlan plan = CreateExecutionPlan(tsdb, source);
        return new SelectExecutionResult(["key", "value"], BuildExplainRows(tsdb, source, plan));
    }

    internal static SelectExecutionResult ExplainAnalyze(Tsdb tsdb, SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        GraphTableExecutionOutcome outcome = ExecuteCore(tsdb, statement);
        GraphTableSource source = statement.GraphTable!;
        List<IReadOnlyList<object?>> rows = BuildExplainRows(tsdb, source, outcome.Plan);
        rows.Add(new object?[] { "analyze", true });
        rows.Add(new object?[] { "actual_rows", outcome.Metrics.OutputRows });
        rows.Add(new object?[] { "actual_matched_rows", outcome.Metrics.MatchedRows });
        rows.Add(new object?[] { "actual_anchor_rows", outcome.Metrics.AnchorRows });
        rows.Add(new object?[] { "actual_expansions", outcome.Metrics.Expansions });
        rows.Add(new object?[] { "actual_generated_paths", outcome.Metrics.GeneratedPaths });
        rows.Add(new object?[] { "actual_peak_frontier", outcome.Metrics.PeakFrontier });
        rows.Add(new object?[] { "actual_fallback_rows", outcome.Metrics.FallbackRows });
        rows.Add(new object?[] { "actual_fallback_ms", outcome.Metrics.FallbackDuration.TotalMilliseconds });
        rows.Add(new object?[] { "actual_read_consistency", outcome.Metrics.ReadConsistency });
        rows.Add(new object?[] { "actual_snapshot_sequence", outcome.Metrics.SnapshotSequence });
        rows.Add(new object?[] { "actual_snapshot_sequences", outcome.Metrics.SnapshotSequences });
        rows.Add(new object?[] { "actual_anchor_access_path", outcome.Plan.NativeAnchor?.AccessPath });
        rows.Add(new object?[] { "actual_anchor_index", outcome.Plan.NativeAnchor?.Index });
        rows.Add(new object?[] { "actual_elapsed_ms", outcome.Metrics.Elapsed.TotalMilliseconds });
        return new SelectExecutionResult(["key", "value"], rows);
    }

    private static GraphTableExecutionOutcome ExecuteCore(Tsdb tsdb, SelectStatement statement)
    {
        GraphTableSource source = statement.GraphTable
            ?? throw new InvalidOperationException("GRAPH_TABLE SELECT 缺少 typed source。");
        EnsureSupportedShape(statement);
        ValidatePathPattern(source);
        GraphTableExecutionPlan plan = CreateExecutionPlan(tsdb, source);
        var metrics = new GraphTableExecutionMetrics();
        long started = Stopwatch.GetTimestamp();
        (IReadOnlyList<string> Columns, IEnumerable<IReadOnlyList<object?>> Rows) relation =
            plan.IsRelational
                ? ExecuteRelational(tsdb, plan.Source, plan.ReversePathProjection, metrics)
                : ExecuteNative(tsdb, plan, metrics);
        IEnumerable<IReadOnlyList<object?>> matchedRows = CountMatchedRows(relation.Rows, metrics);
        SelectExecutionResult result = GraphSqlExecutor.ApplySelectShape(
            statement,
            relation.Columns,
            matchedRows,
            "GRAPH_TABLE");
        metrics.OutputRows = result.Rows.Count;
        metrics.Elapsed = Stopwatch.GetElapsedTime(started);
        return new GraphTableExecutionOutcome(result, plan, metrics);
    }

    private static List<IReadOnlyList<object?>> BuildExplainRows(
        Tsdb tsdb,
        GraphTableSource originalSource,
        GraphTableExecutionPlan plan)
    {
        GraphTableSource source = plan.Source;
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[] { "statement_type", "select" },
            new object?[]
            {
                "logical_plan",
                originalSource.Path is { IsAnyShortest: true }
                    ? "GraphShortestPath"
                    : originalSource.Path is null ? "GraphExpand" : "GraphPath",
            },
            new object?[] { "pattern_hops", 1 },
            new object?[] { "direction", originalSource.Direction.ToString().ToLowerInvariant() },
            new object?[] { "execution_direction", source.Direction.ToString().ToLowerInvariant() },
            new object?[] { "bounded", true },
            new object?[] { "max_anchor_rows", MaxAnchorRows },
            new object?[] { "max_matched_rows", MaxMatchedRows },
            new object?[] { "planner", "graph_cost_v1" },
            new object?[] { "anchor_side", plan.AnchorSide },
            new object?[] { "anchor_variable", source.LeftVertex.Variable },
            new object?[] { "estimated_anchor_rows", plan.EstimatedAnchorRows },
            new object?[] { "estimated_expansions", plan.EstimatedExpansions },
            new object?[] { "estimated_cost", plan.EstimatedCost },
            new object?[] { "estimate_source", plan.EstimateSource },
            new object?[] { "bidirectional_bfs_admitted", plan.BidirectionalBfsAdmitted },
            new object?[] { "bidirectional_bfs_reason", plan.BidirectionalBfsReason },
        };
        if (originalSource.Path is { } path)
        {
            rows.Add(new object?[] { "path_min_depth", path.MinDepth });
            rows.Add(new object?[] { "path_max_depth", path.MaxDepth });
            rows.Add(new object?[] { "path_uniqueness", path.Uniqueness.ToString().ToLowerInvariant() });
            rows.Add(new object?[] { "path_search_mode", path.IsAnyShortest ? "breadth_first" : "depth_first" });
            rows.Add(new object?[] { "max_frontier", MaxAnchorRows });
        }

        if (plan.IsRelational)
        {
            RelationalPattern pattern = ResolveRelationalPattern(tsdb, source);
            rows.Add(new object?[] { "graph_kind", "relational_mapping" });
            rows.Add(new object?[] { "accessor", "RelationalGraphAccessor" });
            rows.Add(new object?[] { "read_consistency", "statement_snapshot" });
            rows.Add(new object?[] { "snapshot_scope", "property_graph_mapped_tables" });
            rows.Add(new object?[] { "copies_relational_rows", false });
            rows.Add(new object?[] { "pull_operator", "paged_cursor" });
            rows.Add(new object?[] { "binding_storage", "fixed_slots" });
            rows.Add(new object?[] { "mapping_branch_count", pattern.Branches.Count });
            RelationalGraphAccessor accessor = tsdb.Graphs.OpenPropertyGraph(source.GraphName);
            foreach (RelationalPatternBranch branch in pattern.Branches)
                foreach (RelationalPatternOrientation orientation in branch.Orientations)
                {
                    bool anchorSeek = TryExtractKeyValues(
                        source.Predicate,
                        source.LeftVertex.Variable,
                        orientation.Left.KeyColumns,
                        out _);
                    RelationalGraphAccessPlan anchorPlan = anchorSeek
                        ? accessor.ExplainVertexAccess(orientation.Left.TableName)
                        : new RelationalGraphAccessPlan("relation_scan_fallback", null, null);
                    string anchorKey = $"anchor.{orientation.Left.TableName}.access_path";
                    if (!rows.Any(row => Equals(row[0], anchorKey)))
                    {
                        rows.Add(new object?[] { anchorKey, anchorPlan.AccessPath });
                    }
                    foreach (RelationalGraphAccessPlan edgePlan in accessor.ExplainEdgeAccess(
                        branch.Edge.TableName,
                        orientation.Direction))
                    {
                        rows.Add(new object?[]
                        {
                        $"edge.{branch.Edge.TableName}.{edgePlan.Direction!.Value.ToString().ToLowerInvariant()}.access_path",
                        edgePlan.AccessPath,
                        });
                        rows.Add(new object?[]
                        {
                        $"edge.{branch.Edge.TableName}.{edgePlan.Direction!.Value.ToString().ToLowerInvariant()}.index",
                        edgePlan.IndexName,
                        });
                    }
                }
            rows.Add(new object?[] { "scan_fallback_max_rows", MaxRelationScanRows });
            rows.Add(new object?[] { "scan_fallback_max_ms", MaxRelationScanDuration.TotalMilliseconds });
        }
        else
        {
            _ = ResolveNativeLabels(tsdb, source);
            GraphNativeAnchorAccess anchor = plan.NativeAnchor
                ?? throw new InvalidOperationException("Native graph plan 缺少 anchor access path。");
            rows.Add(new object?[] { "graph_kind", "native" });
            rows.Add(new object?[] { "accessor", "NativeGraphAccessor" });
            rows.Add(new object?[] { "read_consistency", "statement_snapshot" });
            rows.Add(new object?[] { "anchor_access_path", anchor.AccessPath });
            rows.Add(new object?[] { "anchor_index", anchor.Index });
            rows.Add(new object?[] { "anchor_property_id", anchor.PropertyId });
            rows.Add(new object?[] { "statistics_sequence", anchor.StatisticsSequence });
            rows.Add(new object?[] { "statistics_freshness", anchor.StatisticsFreshness });
            rows.Add(new object?[] { "anchor_expand_order", "anchor_then_expand_then_residual_filter" });
            rows.Add(new object?[] { "edge_access_path", "native_adjacency" });
            rows.Add(new object?[] { "fallback_reason", anchor.FallbackReason });
            rows.Add(new object?[] { "pull_operator", "paged_cursor" });
            rows.Add(new object?[] { "binding_storage", "fixed_slots" });
        }
        rows.Add(new object?[]
        {
            "blocking_operator",
            originalSource.Path is not null
                ? "bounded_path_frontier"
                : "none",
        });
        return rows;
    }

    private static GraphTableExecutionPlan CreateExecutionPlan(Tsdb tsdb, GraphTableSource source)
    {
        bool isRelational = tsdb.Graphs.PropertyGraphs.TryGet(source.GraphName) is not null;
        if (!isRelational)
            _ = ResolveNativeLabels(tsdb, source);

        GraphAnchorEstimate left = isRelational
            ? EstimateRelationalAnchor(tsdb, source)
            : EstimateNativeAnchor(tsdb, source);
        GraphTableSource reversed = ReverseSource(source);
        GraphAnchorEstimate right = isRelational
            ? EstimateRelationalAnchor(tsdb, reversed)
            : EstimateNativeAnchor(tsdb, reversed);
        bool useRight = right.Cost < left.Cost;
        GraphAnchorEstimate selected = useRight ? right : left;
        bool bothEndpointsBound = HasBoundAnchor(tsdb, source, isRelational)
            && HasBoundAnchor(tsdb, reversed, isRelational);
        string bidirectionalReason = source.Path is not { IsAnyShortest: true }
            ? "not_any_shortest"
            : isRelational
                ? "relational_mapping_not_admitted"
                : !bothEndpointsBound
                    ? "requires_both_endpoint_id_predicates"
                    : "benchmark_evidence_missing";
        return new GraphTableExecutionPlan(
            useRight ? reversed : source,
            IsRelational: isRelational,
            ReversePathProjection: useRight && source.Path is not null,
            AnchorSide: useRight ? "right" : "left",
            selected.AnchorRows,
            selected.Expansions,
            selected.Cost,
            selected.Source,
            selected.NativeAccess,
            BidirectionalBfsAdmitted: false,
            bidirectionalReason);
    }

    private static GraphAnchorEstimate EstimateNativeAnchor(Tsdb tsdb, GraphTableSource source)
    {
        (LabelId Label, _, _) = ResolveNativeLabels(tsdb, source);
        GraphStore store = tsdb.Graphs.Open(source.GraphName);
        GraphStatistics? statistics = store.GetCachedStatistics();
        bool statisticsCurrent = statistics?.Sequence == store.CurrentSequence;
        bool idBound = TryExtractKeyValues(
            source.Predicate,
            source.LeftVertex.Variable,
            ["id"],
            out _);
        GraphNativeAnchorAccess access;
        long anchorRows;
        string estimateSource;
        if (idBound)
        {
            anchorRows = 1;
            estimateSource = "endpoint_id_predicate";
            access = new GraphNativeAnchorAccess(
                "native_vertex_id_seek",
                "vertex_record_id",
                null,
                null,
                statistics?.Sequence,
                StatisticsFreshness(statistics, statisticsCurrent),
                null);
        }
        else
        {
            IReadOnlyList<NativePropertyPredicate> predicates = ExtractNativePropertyPredicates(
                source.Predicate,
                source.LeftVertex.Variable);
            NativePropertyPredicate? selected = predicates
                .Select(predicate => new
                {
                    Predicate = predicate,
                    Rows = statistics?.EstimateSeekRows(
                        GraphElementType.Vertex,
                        Label,
                        predicate.PropertyId,
                        predicate.Value) ?? Math.Min(MaxAnchorRows, 64),
                })
                .OrderBy(static candidate => candidate.Rows)
                .ThenBy(static candidate => candidate.Predicate.PropertyId)
                .Select(static candidate => candidate.Predicate)
                .FirstOrDefault();
            if (selected is not null)
            {
                anchorRows = statistics?.EstimateSeekRows(
                    GraphElementType.Vertex,
                    Label,
                    selected.PropertyId,
                    selected.Value) ?? Math.Min(MaxAnchorRows, 64);
                estimateSource = statistics is null
                    ? "property_index_bounded_heuristic"
                    : statisticsCurrent ? "property_value_statistics_refreshed" : "property_value_statistics_stale";
                access = new GraphNativeAnchorAccess(
                    "native_property_index_seek",
                    $"vertex_label_{Label.Value}_property_{selected.PropertyId}",
                    selected.PropertyId,
                    selected.Value,
                    statistics?.Sequence,
                    StatisticsFreshness(statistics, statisticsCurrent),
                    null);
            }
            else
            {
                anchorRows = statistics?.LabelCardinality.GetValueOrDefault(Label) ?? MaxAnchorRows;
                estimateSource = statistics is null
                    ? "statistics_missing_bounded_cap"
                    : statisticsCurrent ? "label_statistics_refreshed" : "label_statistics_stale";
                access = new GraphNativeAnchorAccess(
                    "native_label_index",
                    $"vertex_label_{Label.Value}",
                    null,
                    null,
                    statistics?.Sequence,
                    StatisticsFreshness(statistics, statisticsCurrent),
                    ContainsPropertyReference(source.Predicate, source.LeftVertex.Variable)
                        ? "property_predicate_not_exact_or_unsupported"
                        : null);
            }
        }
        int directionFactor = source.Direction == GraphDirection.Both ? 2 : 1;
        int depthFactor = source.Path?.MaxDepth ?? 1;
        long averageDegree = EstimateAverageDegree(statistics);
        long expansions = Math.Min(
            MaxMatchedRows,
            SaturatingMultiply(
                anchorRows,
                SaturatingMultiply(averageDegree, checked(directionFactor * depthFactor))));
        return new GraphAnchorEstimate(
            anchorRows,
            expansions,
            anchorRows + (double)expansions,
            estimateSource,
            access);
    }

    private static IReadOnlyList<NativePropertyPredicate> ExtractNativePropertyPredicates(
        SqlExpression? predicate,
        string variable)
    {
        var result = new List<NativePropertyPredicate>();
        Visit(predicate);
        return result;

        void Visit(SqlExpression? expression)
        {
            if (expression is BinaryExpression { Operator: SqlBinaryOperator.And } conjunction)
            {
                Visit(conjunction.Left);
                Visit(conjunction.Right);
                return;
            }
            if (expression is not BinaryExpression { Operator: SqlBinaryOperator.Equal } equality)
                return;
            if (TryGetPropertyComparison(equality.Left, equality.Right, variable, out NativePropertyPredicate? item)
                || TryGetPropertyComparison(equality.Right, equality.Left, variable, out item))
            {
                if (!result.Any(existing => existing.PropertyId == item!.PropertyId && existing.Value == item.Value))
                    result.Add(item!);
            }
        }
    }

    private static bool TryGetPropertyComparison(
        SqlExpression identifierExpression,
        SqlExpression valueExpression,
        string variable,
        out NativePropertyPredicate? predicate)
    {
        predicate = null;
        if (identifierExpression is not IdentifierExpression identifier
            || !string.Equals(identifier.Qualifier, variable, StringComparison.OrdinalIgnoreCase)
            || !GraphSqlContract.TryParsePropertyColumn(identifier.Name, out int propertyId)
            || !TryConvertNativePropertyValue(valueExpression, out GraphPropertyValue value))
        {
            return false;
        }
        predicate = new NativePropertyPredicate(propertyId, value);
        return true;
    }

    private static bool TryConvertNativePropertyValue(
        SqlExpression expression,
        out GraphPropertyValue value)
    {
        value = default;
        if (expression is not LiteralExpression and not UnaryExpression)
            return false;
        try
        {
            SqlProjectionExpressionEvaluator.Validate(
                expression,
                static _ => false,
                "GRAPH_TABLE property index predicate");
            object? scalar = SqlProjectionExpressionEvaluator.Evaluate(
                expression,
                static _ => null,
                "GRAPH_TABLE property index predicate");
            value = scalar switch
            {
                long integer => GraphPropertyValue.FromInt64(integer),
                double number when double.IsFinite(number) => GraphPropertyValue.FromFloat64(number),
                bool boolean => GraphPropertyValue.FromBoolean(boolean),
                string text => GraphPropertyValue.FromString(text),
                _ => default,
            };
            return scalar is long or bool or string || scalar is double finite && double.IsFinite(finite);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static bool ContainsPropertyReference(SqlExpression? expression, string variable)
        => expression switch
        {
            IdentifierExpression identifier =>
                string.Equals(identifier.Qualifier, variable, StringComparison.OrdinalIgnoreCase)
                && GraphSqlContract.TryParsePropertyColumn(identifier.Name, out _),
            BinaryExpression binary =>
                ContainsPropertyReference(binary.Left, variable)
                || ContainsPropertyReference(binary.Right, variable),
            UnaryExpression unary => ContainsPropertyReference(unary.Operand, variable),
            IsNullExpression isNull => ContainsPropertyReference(isNull.Operand, variable),
            InExpression inExpression =>
                ContainsPropertyReference(inExpression.Value, variable)
                || inExpression.Values.Any(value => ContainsPropertyReference(value, variable)),
            FunctionCallExpression function =>
                function.Arguments.Any(argument => ContainsPropertyReference(argument, variable)),
            _ => false,
        };

    private static long EstimateAverageDegree(GraphStatistics? statistics)
    {
        if (statistics is null)
            return 1;
        if (statistics.VertexCount == 0 || statistics.EdgeCount == 0)
            return 0;
        return Math.Max(1, DivideRoundUp(statistics.EdgeCount, statistics.VertexCount));
    }

    private static string StatisticsFreshness(GraphStatistics? statistics, bool current)
        => statistics is null ? "missing" : current ? "refreshed" : "stale";

    private static GraphAnchorEstimate EstimateRelationalAnchor(Tsdb tsdb, GraphTableSource source)
    {
        RelationalPattern pattern = ResolveRelationalPattern(tsdb, source);
        RelationalGraphAccessor accessor = tsdb.Graphs.OpenPropertyGraph(source.GraphName);
        PropertyGraphVertexTable[] anchorMappings = pattern.Branches
            .SelectMany(static branch => branch.Orientations)
            .Select(static orientation => orientation.Left)
            .DistinctBy(static mapping => mapping.TableName, StringComparer.Ordinal)
            .ToArray();
        long anchorRows = 0;
        long expansions = 0;
        bool allBound = true;
        foreach (PropertyGraphVertexTable mapping in anchorMappings)
        {
            int tableRows = tsdb.Tables.Open(mapping.TableName).RowCount;
            bool bound = TryExtractKeyValues(
                source.Predicate,
                source.LeftVertex.Variable,
                mapping.KeyColumns,
                out _);
            allBound &= bound;
            long mappingAnchors = bound ? Math.Min(1, tableRows) : tableRows;
            anchorRows = SaturatingAdd(anchorRows, mappingAnchors);
            foreach (RelationalPatternBranch branch in pattern.Branches)
                foreach (RelationalPatternOrientation orientation in branch.Orientations)
                {
                    if (!string.Equals(orientation.Left.TableName, mapping.TableName, StringComparison.Ordinal))
                        continue;
                    int edgeRows = tsdb.Tables.Open(branch.Edge.TableName).RowCount;
                    bool fallback = accessor.ExplainEdgeAccess(branch.Edge.TableName, orientation.Direction)
                        .Any(static plan => plan.AccessPath == "relation_scan_fallback");
                    long perAnchor = fallback
                        ? edgeRows
                        : Math.Max(1, DivideRoundUp(edgeRows, Math.Max(1, tableRows)));
                    expansions = SaturatingAdd(
                        expansions,
                        SaturatingMultiply(mappingAnchors, perAnchor));
                }
        }
        if (source.Path is { } path)
            expansions = SaturatingMultiply(expansions, path.MaxDepth);
        return new GraphAnchorEstimate(
            anchorRows,
            expansions,
            anchorRows + (double)expansions,
            allBound ? "endpoint_key_predicate+relational_row_count" : "relational_row_count",
            null);
    }

    private static bool HasBoundAnchor(Tsdb tsdb, GraphTableSource source, bool isRelational)
    {
        if (!isRelational)
        {
            return TryExtractKeyValues(
                source.Predicate,
                source.LeftVertex.Variable,
                ["id"],
                out _);
        }
        RelationalPattern pattern = ResolveRelationalPattern(tsdb, source);
        return pattern.Branches
            .SelectMany(static branch => branch.Orientations)
            .Select(static orientation => orientation.Left)
            .DistinctBy(static mapping => mapping.TableName, StringComparer.Ordinal)
            .All(mapping => TryExtractKeyValues(
                source.Predicate,
                source.LeftVertex.Variable,
                mapping.KeyColumns,
                out _));
    }

    private static GraphTableSource ReverseSource(GraphTableSource source)
        => new(
            source.GraphName,
            source.RightVertex,
            source.Edge,
            source.LeftVertex,
            source.Direction switch
            {
                GraphDirection.Outgoing => GraphDirection.Incoming,
                GraphDirection.Incoming => GraphDirection.Outgoing,
                GraphDirection.Both => GraphDirection.Both,
                _ => throw new ArgumentOutOfRangeException(nameof(source), "GRAPH_TABLE direction 无效。"),
            },
            source.Predicate,
            source.Columns)
        {
            Path = source.Path,
        };

    private static long DivideRoundUp(long value, long divisor)
        => value == 0 ? 0 : checked(((value - 1) / divisor) + 1);

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private static long SaturatingMultiply(long left, long right)
        => left == 0 || right == 0
            ? 0
            : left > long.MaxValue / right ? long.MaxValue : left * right;

    private static (IReadOnlyList<string> Columns, IEnumerable<IReadOnlyList<object?>> Rows) ExecuteRelational(
        Tsdb tsdb,
        GraphTableSource source,
        bool reversePathProjection,
        GraphTableExecutionMetrics metrics)
    {
        if (source.Path is not null)
            return ExecuteRelationalPaths(tsdb, source, reversePathProjection, metrics);
        RelationalPattern pattern = ResolveRelationalPattern(tsdb, source);
        ValidateRelationalExpressions(tsdb, source, pattern);
        IReadOnlyList<string> columns = BuildOutputColumns(source.Columns);
        RelationalGraphAccessor accessor = tsdb.Graphs.OpenPropertyGraph(source.GraphName);
        return (columns, PullRows());

        IEnumerable<IReadOnlyList<object?>> PullRows()
        {
            int remainingAnchorRows = MaxAnchorRows;
            int remainingMatchedRows = MaxMatchedRows;
            int remainingScanRows = MaxRelationScanRows;
            TimeSpan remainingScanDuration = MaxRelationScanDuration;
            using RelationalGraphReadSession session = accessor.BeginRead();
            metrics.ReadConsistency = "statement_snapshot";
            metrics.SnapshotSequences = FormatSnapshotSequences(session.SnapshotSequences);
            var bindings = new RelationalMatchBindings();
            Func<IdentifierExpression, object?> resolveBindings = bindings.Resolve;

            foreach (RelationalPatternBranch branch in pattern.Branches)
                foreach (RelationalPatternOrientation orientation in branch.Orientations)
                {
                    IReadOnlyList<object?>? anchorKey = TryExtractKeyValues(
                        source.Predicate,
                        source.LeftVertex.Variable,
                        orientation.Left.KeyColumns,
                        out IReadOnlyList<object?> extractedAnchorKey)
                        ? extractedAnchorKey
                        : null;
                    if (anchorKey is null && (remainingScanRows <= 0 || remainingAnchorRows <= 0))
                    {
                        throw new GraphTraversalLimitExceededException(
                            "GRAPH_TABLE relation anchor 或 scan fallback 超过整条查询预算。");
                    }
                    using RelationalGraphCursor anchors = GraphPlanExecutor.Execute(
                        session,
                        new RelationalGraphNodePlan(
                            orientation.Left.TableName,
                            anchorKey,
                            new RelationalGraphAccessOptions
                            {
                                PageSize = 256,
                                MaxScanRows = Math.Max(1, remainingScanRows),
                                MaxResults = remainingAnchorRows,
                                MaxScanDuration = remainingScanDuration,
                            }));
                    bool anchorFallback = anchors.AccessPlans.Any(
                        static plan => plan.AccessPath == "relation_scan_fallback");
                    try
                    {
                        while (true)
                        {
                            IReadOnlyList<TableRow> anchorPage = anchors.ReadNextPage();
                            if (anchorPage.Count == 0)
                                break;
                            foreach (TableRow anchor in anchorPage)
                            {
                                SqlExecutor.ThrowIfCancellationRequested();
                                metrics.AnchorRows = checked(metrics.AnchorRows + 1);
                                if (--remainingAnchorRows < 0)
                                    throw new GraphTraversalLimitExceededException(
                                        $"GRAPH_TABLE anchor 超过上限 {MaxAnchorRows} 行。");
                                TableSchema leftSchema = session.GetSchema(orientation.Left.TableName);
                                IReadOnlyList<object?> anchorKeyValues = ReadValues(
                                    leftSchema,
                                    anchor,
                                    orientation.Left.KeyColumns);
                                IReadOnlyList<RelationalGraphAccessPlan> edgePlans = accessor.ExplainEdgeAccess(
                                    branch.Edge.TableName,
                                    orientation.Direction);
                                bool edgeUsesFallback = edgePlans.Any(
                                    static plan => plan.AccessPath == "relation_scan_fallback");
                                if (remainingScanRows <= 0 && edgeUsesFallback)
                                {
                                    throw new GraphTraversalLimitExceededException(
                                        $"GRAPH_TABLE relation scan fallback 超过总预算 {MaxRelationScanRows} 行。");
                                }
                                using RelationalGraphCursor edges = GraphPlanExecutor.Execute(
                                    session,
                                    new RelationalGraphExpandPlan(
                                        branch.Edge.TableName,
                                        orientation.Direction,
                                        anchorKeyValues,
                                        new RelationalGraphAccessOptions
                                        {
                                            PageSize = 256,
                                            MaxScanRows = Math.Max(1, remainingScanRows),
                                            MaxResults = Math.Max(1, remainingMatchedRows),
                                            MaxScanDuration = edgeUsesFallback
                                                ? remainingScanDuration
                                                : MaxRelationScanDuration,
                                        }));
                                try
                                {
                                    while (true)
                                    {
                                        IReadOnlyList<TableRow> edgePage = edges.ReadNextPage();
                                        if (edgePage.Count == 0)
                                            break;
                                        metrics.Expansions = checked(metrics.Expansions + edgePage.Count);
                                        TableSchema edgeSchema = session.GetSchema(branch.Edge.TableName);
                                        foreach (TableRow edgeRow in edgePage)
                                            foreach ((PropertyGraphVertexTable Right, IReadOnlyList<object?> Key) neighbor in
                                                ResolveRelationalNeighbors(
                                                    branch.Edge,
                                                    orientation,
                                                    leftSchema,
                                                    anchor,
                                                    edgeSchema,
                                                    edgeRow,
                                                    anchorKeyValues))
                                            {
                                                using RelationalGraphCursor neighborCursor = GraphPlanExecutor.Execute(
                                                    session,
                                                    new RelationalGraphNodePlan(
                                                        neighbor.Right.TableName,
                                                        neighbor.Key,
                                                        new RelationalGraphAccessOptions { PageSize = 1, MaxResults = 1 }));
                                                IReadOnlyList<TableRow> neighborPage = neighborCursor.ReadNextPage();
                                                if (neighborPage.Count == 0)
                                                    continue;
                                                TableSchema rightSchema = session.GetSchema(neighbor.Right.TableName);
                                                bindings.Update(
                                                    source.LeftVertex.Variable,
                                                    new RelationalBinding(leftSchema, anchor, orientation.Left.PropertyColumns),
                                                    source.Edge.Variable,
                                                    new RelationalBinding(edgeSchema, edgeRow, branch.Edge.PropertyColumns),
                                                    source.RightVertex.Variable,
                                                    new RelationalBinding(
                                                        rightSchema,
                                                        neighborPage[0],
                                                        neighbor.Right.PropertyColumns));
                                                if (source.Predicate is not null
                                                    && SqlProjectionExpressionEvaluator.Evaluate(
                                                        source.Predicate,
                                                        resolveBindings,
                                                        "GRAPH_TABLE MATCH WHERE") is not true)
                                                {
                                                    continue;
                                                }
                                                if (remainingMatchedRows-- <= 0)
                                                {
                                                    throw new GraphTraversalLimitExceededException(
                                                        $"GRAPH_TABLE 匹配结果超过上限 {MaxMatchedRows} 行。");
                                                }
                                                yield return Project(
                                                    source.Columns,
                                                    resolveBindings,
                                                    "GRAPH_TABLE COLUMNS");
                                            }
                                    }
                                }
                                finally
                                {
                                    if (edgeUsesFallback)
                                    {
                                        remainingScanRows = checked(remainingScanRows - edges.ExaminedRows);
                                        remainingScanDuration -= edges.FallbackDuration;
                                        metrics.FallbackRows = checked(metrics.FallbackRows + edges.ExaminedRows);
                                        metrics.FallbackDuration += edges.FallbackDuration;
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (anchorFallback)
                        {
                            remainingScanRows = checked(remainingScanRows - anchors.ExaminedRows);
                            remainingScanDuration -= anchors.FallbackDuration;
                            metrics.FallbackRows = checked(metrics.FallbackRows + anchors.ExaminedRows);
                            metrics.FallbackDuration += anchors.FallbackDuration;
                        }
                    }
                }
        }
    }

    private static (IReadOnlyList<string> Columns, IEnumerable<IReadOnlyList<object?>> Rows) ExecuteRelationalPaths(
        Tsdb tsdb,
        GraphTableSource source,
        bool reversePathProjection,
        GraphTableExecutionMetrics metrics)
    {
        GraphPathPattern pathPattern = source.Path
            ?? throw new InvalidOperationException("GRAPH_TABLE relation path 缺少 typed path pattern。");
        RelationalPattern pattern = ResolveRelationalPattern(tsdb, source);
        ValidateRelationalPathExpressions(tsdb, source, pathPattern, pattern);
        IReadOnlyList<string> columns = BuildOutputColumns(source.Columns);
        RelationalGraphAccessor accessor = tsdb.Graphs.OpenPropertyGraph(source.GraphName);
        PropertyGraphVertexTable[] anchorMappings = pattern.Branches
            .SelectMany(static branch => branch.Orientations)
            .Select(static orientation => orientation.Left)
            .DistinctBy(static mapping => mapping.TableName, StringComparer.Ordinal)
            .ToArray();
        return (columns, PullRows());

        IEnumerable<IReadOnlyList<object?>> PullRows()
        {
            int remainingAnchors = MaxAnchorRows;
            var state = new RelationalPathExecutionState();
            using RelationalGraphReadSession session = accessor.BeginRead();
            metrics.ReadConsistency = "statement_snapshot";
            metrics.SnapshotSequences = FormatSnapshotSequences(session.SnapshotSequences);

            foreach (PropertyGraphVertexTable anchorMapping in anchorMappings)
            {
                IReadOnlyList<object?>? anchorKey = TryExtractKeyValues(
                    source.Predicate,
                    source.LeftVertex.Variable,
                    anchorMapping.KeyColumns,
                    out IReadOnlyList<object?> extractedAnchorKey)
                    ? extractedAnchorKey
                    : null;
                if (anchorKey is null && (remainingAnchors <= 0 || state.RemainingScanRows <= 0))
                    throw new GraphTraversalLimitExceededException("GRAPH_TABLE relation path anchor 超过整条查询预算。");
                using RelationalGraphCursor anchors = GraphPlanExecutor.Execute(
                    session,
                    new RelationalGraphNodePlan(
                        anchorMapping.TableName,
                        anchorKey,
                        new RelationalGraphAccessOptions
                        {
                            PageSize = 256,
                            MaxScanRows = Math.Max(1, state.RemainingScanRows),
                            MaxResults = remainingAnchors,
                            MaxScanDuration = state.RemainingScanDuration,
                        }));
                bool anchorFallback = anchors.AccessPlans.Any(
                    static plan => plan.AccessPath == "relation_scan_fallback");
                try
                {
                    while (true)
                    {
                        IReadOnlyList<TableRow> page = anchors.ReadNextPage();
                        if (page.Count == 0)
                            break;
                        foreach (TableRow anchorRow in page)
                        {
                            if (--remainingAnchors < 0)
                            {
                                throw new GraphTraversalLimitExceededException(
                                    $"GRAPH_TABLE path anchor 超过上限 {MaxAnchorRows} 行。");
                            }
                            metrics.AnchorRows = checked(metrics.AnchorRows + 1);
                            TableSchema anchorSchema = session.GetSchema(anchorMapping.TableName);
                            var anchor = new RelationalTraversalVertex(
                                anchorMapping,
                                anchorSchema,
                                anchorRow,
                                FormatRelationalIdentity(anchorMapping.TableName, anchorRow));
                            foreach (IReadOnlyList<object?> row in ExecuteRelationalPathFromAnchor(
                                source,
                                pathPattern,
                                pattern,
                                session,
                                anchor,
                                state,
                                reversePathProjection,
                                metrics))
                            {
                                yield return row;
                            }
                        }
                    }
                }
                finally
                {
                    if (anchorFallback)
                        ConsumeRelationalCursorBudget(anchors, state, metrics);
                }
            }
        }
    }

    private static IEnumerable<IReadOnlyList<object?>> ExecuteRelationalPathFromAnchor(
        GraphTableSource source,
        GraphPathPattern pathPattern,
        RelationalPattern pattern,
        RelationalGraphReadSession session,
        RelationalTraversalVertex anchor,
        RelationalPathExecutionState state,
        bool reversePathProjection,
        GraphTableExecutionMetrics metrics)
    {
        var start = new RelationalTraversalPath([anchor], []);
        var queue = new Queue<RelationalTraversalPath>();
        var stack = new Stack<RelationalTraversalPath>();
        var shortestEndpoints = new HashSet<string>(StringComparer.Ordinal);
        var bindings = new RelationalPathBindings();
        Func<IdentifierExpression, object?> resolveBindings = bindings.Resolve;
        if (pathPattern.IsAnyShortest)
            queue.Enqueue(start);
        else
            stack.Push(start);
        metrics.PeakFrontier = Math.Max(metrics.PeakFrontier, 1);

        while (queue.Count != 0 || stack.Count != 0)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            RelationalTraversalPath current = pathPattern.IsAnyShortest
                ? queue.Dequeue()
                : stack.Pop();
            if (current.EdgeIdentities.Count >= pathPattern.MaxDepth)
                continue;

            RelationalTraversalVertex currentVertex = current.Vertices[^1];
            var children = new List<RelationalTraversalPath>();
            foreach (RelationalPatternBranch branch in pattern.Branches)
                foreach (RelationalPatternOrientation orientation in branch.Orientations)
                {
                    if (!string.Equals(
                        orientation.Left.TableName,
                        currentVertex.Mapping.TableName,
                        StringComparison.Ordinal))
                    {
                        continue;
                    }
                    IReadOnlyList<object?> endpointKey = ReadValues(
                        currentVertex.Schema,
                        currentVertex.Row,
                        orientation.Left.KeyColumns);
                    if (state.RemainingScanRows <= 0)
                        throw new GraphTraversalLimitExceededException(
                            $"GRAPH_TABLE relation path scan fallback 超过总预算 {MaxRelationScanRows} 行。");
                    using RelationalGraphCursor edges = GraphPlanExecutor.Execute(
                        session,
                        new RelationalGraphExpandPlan(
                            branch.Edge.TableName,
                            orientation.Direction,
                            endpointKey,
                            new RelationalGraphAccessOptions
                            {
                                PageSize = 256,
                                MaxScanRows = Math.Max(1, state.RemainingScanRows),
                                MaxResults = Math.Max(1, Math.Min(state.RemainingPaths, MaxAnchorRows + 1)),
                                MaxScanDuration = state.RemainingScanDuration,
                            }));
                    bool usesFallback = edges.AccessPlans.Any(
                        static plan => plan.AccessPath == "relation_scan_fallback");
                    try
                    {
                        while (true)
                        {
                            IReadOnlyList<TableRow> edgePage = edges.ReadNextPage();
                            if (edgePage.Count == 0)
                                break;
                            metrics.Expansions = checked(metrics.Expansions + edgePage.Count);
                            TableSchema edgeSchema = session.GetSchema(branch.Edge.TableName);
                            foreach (TableRow edgeRow in edgePage)
                                foreach ((PropertyGraphVertexTable Right, IReadOnlyList<object?> Key) neighbor in
                                    ResolveRelationalNeighbors(
                                        branch.Edge,
                                        orientation,
                                        currentVertex.Schema,
                                        currentVertex.Row,
                                        edgeSchema,
                                        edgeRow,
                                        endpointKey))
                                {
                                    string edgeIdentity = FormatRelationalIdentity(branch.Edge.TableName, edgeRow);
                                    if (pathPattern.Uniqueness == GraphPathUniqueness.Edge
                                        && current.EdgeIdentities.Contains(edgeIdentity, StringComparer.Ordinal))
                                    {
                                        continue;
                                    }
                                    using RelationalGraphCursor neighborCursor = GraphPlanExecutor.Execute(
                                        session,
                                        new RelationalGraphNodePlan(
                                            neighbor.Right.TableName,
                                            neighbor.Key,
                                            new RelationalGraphAccessOptions { PageSize = 1, MaxResults = 1 }));
                                    IReadOnlyList<TableRow> neighborPage = neighborCursor.ReadNextPage();
                                    if (neighborPage.Count == 0)
                                        continue;
                                    TableSchema neighborSchema = session.GetSchema(neighbor.Right.TableName);
                                    TableRow neighborRow = neighborPage[0];
                                    string neighborIdentity = FormatRelationalIdentity(neighbor.Right.TableName, neighborRow);
                                    if (pathPattern.Uniqueness == GraphPathUniqueness.Vertex
                                        && current.Vertices.Any(vertex => string.Equals(
                                            vertex.Identity,
                                            neighborIdentity,
                                            StringComparison.Ordinal)))
                                    {
                                        continue;
                                    }
                                    var neighborVertex = new RelationalTraversalVertex(
                                        neighbor.Right,
                                        neighborSchema,
                                        neighborRow,
                                        neighborIdentity);
                                    var child = current.Extend(neighborVertex, edgeIdentity);
                                    metrics.GeneratedPaths++;
                                    children.Add(child);

                                    if (child.EdgeIdentities.Count < pathPattern.MinDepth)
                                        continue;
                                    if (state.RemainingPaths-- <= 0)
                                    {
                                        throw new GraphTraversalLimitExceededException(
                                            $"GRAPH_TABLE relation path 生成数量超过上限 {MaxMatchedRows}。");
                                    }
                                    if (pathPattern.IsAnyShortest && !shortestEndpoints.Add(neighborIdentity))
                                        continue;
                                    RelationalTraversalPath projectedPath = reversePathProjection
                                        ? ReversePath(child)
                                        : child;
                                    bindings.Update(
                                        source.LeftVertex.Variable,
                                        new RelationalBinding(
                                            anchor.Schema,
                                            anchor.Row,
                                            anchor.Mapping.PropertyColumns),
                                        source.RightVertex.Variable,
                                        new RelationalBinding(
                                            neighborSchema,
                                            neighborRow,
                                            neighbor.Right.PropertyColumns),
                                        pathPattern.Variable,
                                        projectedPath);
                                    if (source.Predicate is not null
                                        && SqlProjectionExpressionEvaluator.Evaluate(
                                            source.Predicate,
                                            resolveBindings,
                                            "GRAPH_TABLE relation path MATCH WHERE") is not true)
                                    {
                                        continue;
                                    }
                                    if (state.RemainingMatchedRows-- <= 0)
                                    {
                                        throw new GraphTraversalLimitExceededException(
                                            $"GRAPH_TABLE relation path 匹配结果超过上限 {MaxMatchedRows} 行。");
                                    }
                                    yield return Project(
                                        source.Columns,
                                        resolveBindings,
                                        "GRAPH_TABLE relation path COLUMNS");
                                }
                        }
                    }
                    finally
                    {
                        if (usesFallback)
                            ConsumeRelationalCursorBudget(edges, state, metrics);
                    }
                }

            foreach (RelationalTraversalPath child in pathPattern.IsAnyShortest
                ? children
                : children.AsEnumerable().Reverse())
            {
                if (child.EdgeIdentities.Count >= pathPattern.MaxDepth)
                    continue;
                int frontier = pathPattern.IsAnyShortest ? queue.Count : stack.Count;
                if (frontier >= MaxAnchorRows)
                {
                    throw new GraphTraversalLimitExceededException(
                        $"GRAPH_TABLE relation path frontier 超过上限 {MaxAnchorRows}。");
                }
                if (pathPattern.IsAnyShortest)
                    queue.Enqueue(child);
                else
                    stack.Push(child);
                metrics.PeakFrontier = Math.Max(
                    metrics.PeakFrontier,
                    pathPattern.IsAnyShortest ? queue.Count : stack.Count);
            }
        }
    }

    private static (IReadOnlyList<string> Columns, IEnumerable<IReadOnlyList<object?>> Rows) ExecuteNative(
        Tsdb tsdb,
        GraphTableExecutionPlan plan,
        GraphTableExecutionMetrics metrics)
    {
        GraphTableSource source = plan.Source;
        if (source.Path is not null)
            return ExecuteNativePaths(tsdb, plan, metrics);

        (LabelId Left, LabelId Edge, LabelId Right) labels = ResolveNativeLabels(tsdb, source);
        ValidateNativeExpressions(source);
        IReadOnlyList<string> columns = BuildOutputColumns(source.Columns);
        return (columns, PullRows());

        IEnumerable<IReadOnlyList<object?>> PullRows()
        {
            using GraphReadSession session = tsdb.Graphs.Open(source.GraphName).BeginRead();
            metrics.ReadConsistency = "statement_snapshot";
            metrics.SnapshotSequence = session.Sequence;
            int anchorCount = 0;
            int remainingMatchedRows = MaxMatchedRows;
            var bindings = new NativeMatchBindings();
            Func<IdentifierExpression, object?> resolveBindings = bindings.Resolve;
            foreach (GraphVertex anchor in EnumerateNativeAnchors(
                session,
                source,
                labels.Left,
                "GRAPH_TABLE native anchor id",
                plan.NativeAnchor))
            {
                SqlExecutor.ThrowIfCancellationRequested();
                if (++anchorCount > MaxAnchorRows)
                    throw new GraphTraversalLimitExceededException($"GRAPH_TABLE anchor 超过上限 {MaxAnchorRows} 行。");
                metrics.AnchorRows = checked(metrics.AnchorRows + 1);
                using GraphCursor<GraphExpansion> cursor = GraphPlanExecutor.Execute(
                    session,
                    new GraphExpandPlan(
                        anchor.Id,
                        source.Direction,
                        labels.Edge,
                        new GraphCursorOptions
                        {
                            PageSize = 256,
                            MaxResults = Math.Max(1, remainingMatchedRows + 1),
                        }));
                while (true)
                {
                    IReadOnlyList<GraphExpansion> page = cursor.ReadNextPage();
                    if (page.Count == 0)
                        break;
                    metrics.Expansions = checked(metrics.Expansions + page.Count);
                    foreach (GraphExpansion expansion in page)
                    {
                        GraphVertex? neighbor = session.GetVertex(expansion.NeighborId);
                        if (neighbor is null || !neighbor.Labels.Contains(labels.Right))
                            continue;
                        bindings.Update(
                            source.LeftVertex.Variable,
                            new NativeBinding(anchor, null),
                            source.Edge.Variable,
                            new NativeBinding(null, expansion.Edge),
                            source.RightVertex.Variable,
                            new NativeBinding(neighbor, null));
                        if (source.Predicate is not null
                            && SqlProjectionExpressionEvaluator.Evaluate(
                                source.Predicate,
                                resolveBindings,
                                "GRAPH_TABLE MATCH WHERE") is not true)
                        {
                            continue;
                        }
                        if (remainingMatchedRows-- <= 0)
                        {
                            throw new GraphTraversalLimitExceededException(
                                $"GRAPH_TABLE 匹配结果超过上限 {MaxMatchedRows} 行。");
                        }
                        yield return Project(
                            source.Columns,
                            resolveBindings,
                            "GRAPH_TABLE COLUMNS");
                    }
                }
            }
        }
    }

    private static (IReadOnlyList<string> Columns, IEnumerable<IReadOnlyList<object?>> Rows) ExecuteNativePaths(
        Tsdb tsdb,
        GraphTableExecutionPlan plan,
        GraphTableExecutionMetrics metrics)
    {
        GraphTableSource source = plan.Source;
        GraphPathPattern pathPattern = source.Path
            ?? throw new InvalidOperationException("GRAPH_TABLE path source 缺少 typed path pattern。");
        (LabelId Left, LabelId Edge, LabelId Right) labels = ResolveNativeLabels(tsdb, source);
        ValidateNativePathExpressions(source, pathPattern);
        IReadOnlyList<string> columns = BuildOutputColumns(source.Columns);
        return (columns, PullRows());

        IEnumerable<IReadOnlyList<object?>> PullRows()
        {
            int remainingPaths = MaxMatchedRows;
            int remainingMatchedRows = MaxMatchedRows;
            int anchorCount = 0;
            using GraphReadSession session = tsdb.Graphs.Open(source.GraphName).BeginRead();
            metrics.ReadConsistency = "statement_snapshot";
            metrics.SnapshotSequence = session.Sequence;
            var bindings = new NativeMatchBindings();
            Func<IdentifierExpression, object?> resolveBindings = bindings.Resolve;

            foreach (GraphVertex anchor in EnumerateNativeAnchors(
                session,
                source,
                labels.Left,
                "GRAPH_TABLE native path anchor id",
                plan.NativeAnchor))
            {
                SqlExecutor.ThrowIfCancellationRequested();
                if (++anchorCount > MaxAnchorRows)
                    throw new GraphTraversalLimitExceededException($"GRAPH_TABLE path anchor 超过上限 {MaxAnchorRows} 行。");
                metrics.AnchorRows = checked(metrics.AnchorRows + 1);
                if (remainingPaths <= 0)
                {
                    throw new GraphTraversalLimitExceededException(
                        $"GRAPH_TABLE path 生成数量超过上限 {MaxMatchedRows}。");
                }
                var options = new GraphTraversalOptions
                {
                    MaxDepth = pathPattern.MaxDepth,
                    MaxFrontier = MaxAnchorRows,
                    MaxPaths = remainingPaths,
                    PathUniqueness = pathPattern.Uniqueness,
                    PageSize = 256,
                };
                var shortestEndpoints = new HashSet<GraphElementId>();
                var diagnostics = new GraphTraversalDiagnostics();
                using GraphCursor<GraphPath> cursor = GraphPlanExecutor.Execute(
                    session,
                    new GraphPathPlan(
                        anchor.Id,
                        pathPattern.IsAnyShortest
                            ? GraphPathSearchMode.BreadthFirst
                            : GraphPathSearchMode.DepthFirst,
                        pathPattern.MinDepth,
                        pathPattern.MaxDepth,
                        source.Direction,
                        labels.Edge,
                        options)
                    {
                        DeduplicateBreadthFirstEndpoints = !pathPattern.IsAnyShortest,
                    },
                    diagnostics);
                try
                {
                    while (true)
                    {
                        IReadOnlyList<GraphPath> page = cursor.ReadNextPage();
                        if (page.Count == 0)
                            break;
                        foreach (GraphPath path in page)
                        {
                            SqlExecutor.ThrowIfCancellationRequested();
                            remainingPaths--;
                            GraphVertex? endpoint = session.GetVertex(path.VertexIds[^1]);
                            if (endpoint is null || !endpoint.Labels.Contains(labels.Right))
                                continue;
                            if (pathPattern.IsAnyShortest && !shortestEndpoints.Add(endpoint.Id))
                                continue;
                            GraphPath projectedPath = plan.ReversePathProjection ? ReversePath(path) : path;
                            bindings.UpdatePath(
                                source.LeftVertex.Variable,
                                new NativeBinding(anchor, null),
                                source.RightVertex.Variable,
                                new NativeBinding(endpoint, null),
                                pathPattern.Variable,
                                projectedPath);
                            if (source.Predicate is not null
                                && SqlProjectionExpressionEvaluator.Evaluate(
                                    source.Predicate,
                                    resolveBindings,
                                    "GRAPH_TABLE path MATCH WHERE") is not true)
                            {
                                continue;
                            }
                            if (remainingMatchedRows-- <= 0)
                            {
                                throw new GraphTraversalLimitExceededException(
                                    $"GRAPH_TABLE path 匹配结果超过上限 {MaxMatchedRows} 行。");
                            }
                            yield return Project(
                                source.Columns,
                                resolveBindings,
                                "GRAPH_TABLE path COLUMNS");
                        }
                    }
                }
                finally
                {
                    metrics.Expansions = checked(metrics.Expansions + diagnostics.ExpansionCount);
                    metrics.GeneratedPaths = checked(metrics.GeneratedPaths + diagnostics.GeneratedPathCount);
                    metrics.PeakFrontier = Math.Max(metrics.PeakFrontier, diagnostics.PeakFrontier);
                }
            }
        }
    }

    private static RelationalPattern ResolveRelationalPattern(Tsdb tsdb, GraphTableSource source)
    {
        PropertyGraphDefinition definition = tsdb.Graphs.PropertyGraphs.TryGet(source.GraphName)
            ?? throw new InvalidOperationException($"property graph '{source.GraphName}' 不存在。");
        PropertyGraphEdgeTable[] edges = definition.EdgeTables
            .Where(edge => string.Equals(edge.Label, source.Edge.Label, StringComparison.Ordinal))
            .ToArray();
        if (edges.Length == 0)
        {
            throw new InvalidOperationException(
                $"GRAPH_TABLE edge label '{source.Edge.Label}' 没有命中 edge table。");
        }

        var branches = new List<RelationalPatternBranch>(edges.Length);
        foreach (PropertyGraphEdgeTable edge in edges)
        {
            PropertyGraphVertexTable sourceVertex = definition.TryGetVertexTable(edge.SourceTable)!;
            PropertyGraphVertexTable destinationVertex = definition.TryGetVertexTable(edge.DestinationTable)!;
            var orientations = new List<RelationalPatternOrientation>();
            if (source.Direction is GraphDirection.Outgoing or GraphDirection.Both
                && LabelMatches(sourceVertex, source.LeftVertex.Label)
                && LabelMatches(destinationVertex, source.RightVertex.Label))
            {
                orientations.Add(new RelationalPatternOrientation(
                    sourceVertex,
                    destinationVertex,
                    source.Direction == GraphDirection.Both
                        && string.Equals(sourceVertex.TableName, destinationVertex.TableName, StringComparison.Ordinal)
                            ? GraphDirection.Both
                            : GraphDirection.Outgoing));
            }
            if (source.Direction is GraphDirection.Incoming or GraphDirection.Both
                && LabelMatches(destinationVertex, source.LeftVertex.Label)
                && LabelMatches(sourceVertex, source.RightVertex.Label)
                && !orientations.Any(static item => item.Direction == GraphDirection.Both))
            {
                orientations.Add(new RelationalPatternOrientation(
                    destinationVertex,
                    sourceVertex,
                    GraphDirection.Incoming));
            }
            if (orientations.Count != 0)
                branches.Add(new RelationalPatternBranch(edge, orientations));
        }
        if (branches.Count == 0)
            throw new InvalidOperationException("GRAPH_TABLE pattern 的方向和 vertex labels 与 edge mapping 不匹配。");
        return new RelationalPattern(branches);
    }

    private static IEnumerable<(PropertyGraphVertexTable Right, IReadOnlyList<object?> Key)>
        ResolveRelationalNeighbors(
            PropertyGraphEdgeTable edge,
            RelationalPatternOrientation orientation,
            TableSchema leftSchema,
            TableRow anchor,
            TableSchema edgeSchema,
            TableRow edgeRow,
            IReadOnlyList<object?> anchorKey)
    {
        if (orientation.Direction == GraphDirection.Outgoing)
        {
            yield return (orientation.Right, ReadValues(edgeSchema, edgeRow, edge.DestinationColumns));
            yield break;
        }
        if (orientation.Direction == GraphDirection.Incoming)
        {
            yield return (orientation.Right, ReadValues(edgeSchema, edgeRow, edge.SourceColumns));
            yield break;
        }

        IReadOnlyList<object?> sourceKey = ReadValues(edgeSchema, edgeRow, edge.SourceColumns);
        IReadOnlyList<object?> destinationKey = ReadValues(edgeSchema, edgeRow, edge.DestinationColumns);
        bool sourceMatches = ValuesEqual(sourceKey, anchorKey);
        bool destinationMatches = ValuesEqual(destinationKey, anchorKey);
        if (sourceMatches)
            yield return (orientation.Right, destinationKey);
        if (destinationMatches && !sourceMatches)
            yield return (orientation.Right, sourceKey);
    }

    private static void ValidateRelationalExpressions(
        Tsdb tsdb,
        GraphTableSource source,
        RelationalPattern pattern)
    {
        var schemas = new Dictionary<string, List<(TableSchema Schema, IReadOnlyList<string> Properties)>>(
            StringComparer.OrdinalIgnoreCase);
        schemas[source.LeftVertex.Variable] = [];
        schemas[source.Edge.Variable] = [];
        schemas[source.RightVertex.Variable] = [];
        foreach (RelationalPatternBranch branch in pattern.Branches)
        {
            schemas[source.Edge.Variable].Add((
                tsdb.Tables.Catalog.TryGet(branch.Edge.TableName)!,
                branch.Edge.PropertyColumns));
            foreach (RelationalPatternOrientation orientation in branch.Orientations)
            {
                schemas[source.LeftVertex.Variable].Add((
                    tsdb.Tables.Catalog.TryGet(orientation.Left.TableName)!,
                    orientation.Left.PropertyColumns));
                schemas[source.RightVertex.Variable].Add((
                    tsdb.Tables.Catalog.TryGet(orientation.Right.TableName)!,
                    orientation.Right.PropertyColumns));
            }
        }
        bool Exists(IdentifierExpression identifier)
            => identifier.Qualifier is not null
                && schemas.TryGetValue(identifier.Qualifier, out var bindings)
                && bindings.Any(binding =>
                    binding.Properties.Contains(identifier.Name, StringComparer.Ordinal)
                    && binding.Schema.TryGetColumn(identifier.Name) is not null);
        if (source.Predicate is not null)
            SqlProjectionExpressionEvaluator.Validate(source.Predicate, Exists, "GRAPH_TABLE MATCH WHERE");
        foreach (SelectItem item in source.Columns)
        {
            if (item.Expression is StarExpression)
                throw new NotSupportedException("GRAPH_TABLE COLUMNS 当前不支持 '*'，请显式投影变量属性。");
            SqlProjectionExpressionEvaluator.Validate(item.Expression, Exists, "GRAPH_TABLE COLUMNS");
        }
    }

    private static void ValidateRelationalPathExpressions(
        Tsdb tsdb,
        GraphTableSource source,
        GraphPathPattern pathPattern,
        RelationalPattern pattern)
    {
        var schemas = new Dictionary<string, List<(TableSchema Schema, IReadOnlyList<string> Properties)>>(
            StringComparer.OrdinalIgnoreCase)
        {
            [source.LeftVertex.Variable] = [],
            [source.RightVertex.Variable] = [],
        };
        foreach (RelationalPatternBranch branch in pattern.Branches)
            foreach (RelationalPatternOrientation orientation in branch.Orientations)
            {
                schemas[source.LeftVertex.Variable].Add((
                    tsdb.Tables.Catalog.TryGet(orientation.Left.TableName)!,
                    orientation.Left.PropertyColumns));
                schemas[source.RightVertex.Variable].Add((
                    tsdb.Tables.Catalog.TryGet(orientation.Right.TableName)!,
                    orientation.Right.PropertyColumns));
            }

        bool Exists(IdentifierExpression identifier)
        {
            if (identifier.Qualifier is null)
                return false;
            if (pathPattern.Variable is not null
                && string.Equals(identifier.Qualifier, pathPattern.Variable, StringComparison.OrdinalIgnoreCase))
            {
                return IsNativePathColumn(identifier.Name);
            }
            return schemas.TryGetValue(identifier.Qualifier, out var bindings)
                && bindings.Any(binding =>
                    binding.Properties.Contains(identifier.Name, StringComparer.Ordinal)
                    && binding.Schema.TryGetColumn(identifier.Name) is not null);
        }

        if (source.Predicate is not null)
            SqlProjectionExpressionEvaluator.Validate(source.Predicate, Exists, "GRAPH_TABLE relation path MATCH WHERE");
        foreach (SelectItem item in source.Columns)
        {
            if (item.Expression is StarExpression)
                throw new NotSupportedException("GRAPH_TABLE relation path COLUMNS 不支持 '*'，请显式投影端点或路径属性。");
            SqlProjectionExpressionEvaluator.Validate(item.Expression, Exists, "GRAPH_TABLE relation path COLUMNS");
        }
    }

    private static void ValidateNativeExpressions(GraphTableSource source)
    {
        var kinds = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [source.LeftVertex.Variable] = true,
            [source.Edge.Variable] = false,
            [source.RightVertex.Variable] = true,
        };
        bool Exists(IdentifierExpression identifier)
            => identifier.Qualifier is not null
                && kinds.TryGetValue(identifier.Qualifier, out bool vertex)
                && IsNativeColumn(identifier.Name, vertex);
        if (source.Predicate is not null)
            SqlProjectionExpressionEvaluator.Validate(source.Predicate, Exists, "GRAPH_TABLE MATCH WHERE");
        foreach (SelectItem item in source.Columns)
        {
            if (item.Expression is StarExpression)
                throw new NotSupportedException("GRAPH_TABLE COLUMNS 当前不支持 '*'，请显式投影变量属性。");
            SqlProjectionExpressionEvaluator.Validate(item.Expression, Exists, "GRAPH_TABLE COLUMNS");
        }
    }

    private static void ValidateNativePathExpressions(
        GraphTableSource source,
        GraphPathPattern pathPattern)
    {
        var kinds = new Dictionary<string, NativeBindingKind>(StringComparer.OrdinalIgnoreCase)
        {
            [source.LeftVertex.Variable] = NativeBindingKind.Vertex,
            [source.RightVertex.Variable] = NativeBindingKind.Vertex,
        };
        if (pathPattern.Variable is not null)
            kinds[pathPattern.Variable] = NativeBindingKind.Path;
        bool Exists(IdentifierExpression identifier)
            => identifier.Qualifier is not null
                && kinds.TryGetValue(identifier.Qualifier, out NativeBindingKind kind)
                && (kind == NativeBindingKind.Vertex
                    ? IsNativeColumn(identifier.Name, vertex: true)
                    : IsNativePathColumn(identifier.Name));
        if (source.Predicate is not null)
            SqlProjectionExpressionEvaluator.Validate(source.Predicate, Exists, "GRAPH_TABLE path MATCH WHERE");
        foreach (SelectItem item in source.Columns)
        {
            if (item.Expression is StarExpression)
                throw new NotSupportedException("GRAPH_TABLE path COLUMNS 不支持 '*'，请显式投影端点或路径属性。");
            SqlProjectionExpressionEvaluator.Validate(item.Expression, Exists, "GRAPH_TABLE path COLUMNS");
        }
    }

    private static object? ResolveNativeBinding(
        NativeBinding? binding,
        IdentifierExpression identifier)
    {
        if (binding is null)
            throw new InvalidOperationException($"GRAPH_TABLE native 变量 '{identifier.Qualifier}' 不存在。");
        if (binding.Vertex is { } vertex)
        {
            return identifier.Name.ToLowerInvariant() switch
            {
                "id" => vertex.Id.Value,
                "element_version" => vertex.ElementVersion,
                "labels" => string.Join(',', vertex.Labels.Select(static item => item.Value)),
                "property_count" => vertex.Properties.Count,
                _ => ResolveNativeProperty(vertex.Properties, identifier.Name),
            };
        }
        if (binding.Path is { } path)
            return ResolveNativePath(path, identifier);
        GraphEdge edge = binding.Edge!;
        return identifier.Name.ToLowerInvariant() switch
        {
            "id" => edge.Id.Value,
            "element_version" => edge.ElementVersion,
            "source_id" => edge.SourceId.Value,
            "target_id" => edge.TargetId.Value,
            "label_id" => edge.LabelId.Value,
            "property_count" => edge.Properties.Count,
            _ => ResolveNativeProperty(edge.Properties, identifier.Name),
        };
    }

    private static object? ResolveNativePath(GraphPath path, IdentifierExpression identifier)
        => identifier.Name.ToLowerInvariant() switch
        {
            "length" => (long)path.Depth,
            "vertex_ids" => string.Join(',', path.VertexIds.Select(static item => item.Value)),
            "edge_ids" => string.Join(',', path.EdgeIds.Select(static item => item.Value)),
            "start_id" => path.VertexIds[0].Value,
            "end_id" => path.VertexIds[^1].Value,
            _ => throw new InvalidOperationException(
                $"GRAPH_TABLE path 属性列 '{identifier.Name}' 不存在。"),
        };

    private static object? ResolveNativeProperty(IReadOnlyList<GraphProperty> properties, string name)
    {
        if (!TryParsePropertyId(name, out int propertyId))
            throw new InvalidOperationException($"GRAPH_TABLE native 属性列 '{name}' 不存在。");
        GraphProperty property = properties.FirstOrDefault(item => item.PropertyId == propertyId);
        return property.PropertyId == 0 ? null : ToSqlValue(property.Value);
    }

    private static object? ToSqlValue(GraphPropertyValue value)
        => value.Kind switch
        {
            GraphPropertyKind.Null => null,
            GraphPropertyKind.Int64 => value.AsInt64(),
            GraphPropertyKind.Float64 => value.AsFloat64(),
            GraphPropertyKind.Boolean => value.AsBoolean(),
            GraphPropertyKind.String => value.AsString(),
            GraphPropertyKind.DateTime => value.AsDateTime().UtcDateTime,
            GraphPropertyKind.Blob => value.AsBlob(),
            GraphPropertyKind.Json => value.AsJson(),
            _ => throw new InvalidOperationException($"未知 Graph property kind '{value.Kind}'。"),
        };

    private static bool IsNativeColumn(string name, bool vertex)
    {
        string normalized = name.ToLowerInvariant();
        return vertex
            ? normalized is "id" or "element_version" or "labels" or "property_count"
                || TryParsePropertyId(name, out _)
            : normalized is "id" or "element_version" or "source_id" or "target_id" or "label_id" or "property_count"
                || TryParsePropertyId(name, out _);
    }

    private static bool IsNativePathColumn(string name)
        => name.ToLowerInvariant() is "length" or "vertex_ids" or "edge_ids" or "start_id" or "end_id";

    private static bool TryParsePropertyId(string name, out int propertyId)
    {
        const string prefix = "property_";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out propertyId)
            && propertyId > 0)
        {
            return true;
        }
        propertyId = 0;
        return false;
    }

    private static (LabelId Left, LabelId Edge, LabelId Right) ResolveNativeLabels(
        Tsdb tsdb,
        GraphTableSource source)
    {
        if (tsdb.Graphs.Catalog.TryGet(source.GraphName) is null)
            throw new InvalidOperationException($"graph '{source.GraphName}' 不存在。");
        return (
            ParseNativeLabel(source.LeftVertex.Label, "left vertex"),
            ParseNativeLabel(source.Edge.Label, "edge"),
            ParseNativeLabel(source.RightVertex.Label, "right vertex"));
    }

    private static LabelId ParseNativeLabel(string text, string description)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value <= 0)
        {
            throw new InvalidOperationException(
                $"GRAPH_TABLE native {description} label 必须是正整数 label ID。");
        }
        return new LabelId(value);
    }

    private static IReadOnlyList<string> BuildOutputColumns(IReadOnlyList<SelectItem> projections)
    {
        var columns = new List<string>(projections.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SelectItem item in projections)
        {
            string name = item.Alias ?? (item.Expression as IdentifierExpression)?.Name ?? "expression";
            if (!seen.Add(name))
                throw new InvalidOperationException($"GRAPH_TABLE COLUMNS 输出列 '{name}' 重复，请使用 AS 区分。");
            columns.Add(name);
        }
        return columns;
    }

    private static IReadOnlyList<object?> Project(
        IReadOnlyList<SelectItem> projections,
        Func<IdentifierExpression, object?> resolver,
        string context)
        => projections.Select(item =>
            SqlProjectionExpressionEvaluator.Evaluate(item.Expression, resolver, context)).ToArray();

    private static IEnumerable<IReadOnlyList<object?>> CountMatchedRows(
        IEnumerable<IReadOnlyList<object?>> rows,
        GraphTableExecutionMetrics metrics)
    {
        foreach (IReadOnlyList<object?> row in rows)
        {
            metrics.MatchedRows = checked(metrics.MatchedRows + 1);
            yield return row;
        }
    }

    private static string FormatSnapshotSequences(IReadOnlyDictionary<string, long> sequences)
        => string.Join(
            ',',
            sequences.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"{pair.Key}:{pair.Value.ToString(CultureInfo.InvariantCulture)}"));

    private static bool TryExtractKeyValues(
        SqlExpression? predicate,
        string variable,
        IReadOnlyList<string> keyColumns,
        out IReadOnlyList<object?> values)
    {
        var equalities = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        CollectLiteralEqualities(predicate, variable, equalities);
        if (keyColumns.All(equalities.ContainsKey))
        {
            values = keyColumns.Select(column => equalities[column]).ToArray();
            return true;
        }
        values = [];
        return false;
    }

    private static void CollectLiteralEqualities(
        SqlExpression? expression,
        string variable,
        Dictionary<string, object?> equalities)
    {
        if (expression is BinaryExpression { Operator: SqlBinaryOperator.And } andExpression)
        {
            CollectLiteralEqualities(andExpression.Left, variable, equalities);
            CollectLiteralEqualities(andExpression.Right, variable, equalities);
            return;
        }
        if (expression is not BinaryExpression { Operator: SqlBinaryOperator.Equal } equality)
            return;
        if (TryReadVariableEquality(equality.Left, equality.Right, variable, out string? column, out object? value)
            || TryReadVariableEquality(equality.Right, equality.Left, variable, out column, out value))
        {
            equalities[column!] = value;
        }
    }

    private static bool TryReadVariableEquality(
        SqlExpression identifierExpression,
        SqlExpression valueExpression,
        string variable,
        out string? column,
        out object? value)
    {
        if (identifierExpression is not IdentifierExpression identifier
            || !string.Equals(identifier.Qualifier, variable, StringComparison.OrdinalIgnoreCase))
        {
            column = null;
            value = null;
            return false;
        }
        try
        {
            SqlProjectionExpressionEvaluator.Validate(valueExpression, static _ => false, "GRAPH_TABLE anchor seek");
            value = SqlProjectionExpressionEvaluator.Evaluate(
                valueExpression,
                static _ => throw new InvalidOperationException(),
                "GRAPH_TABLE anchor seek");
            column = identifier.Name;
            return true;
        }
        catch (InvalidOperationException)
        {
            column = null;
            value = null;
            return false;
        }
    }

    private static IReadOnlyList<object?> ReadValues(
        TableSchema schema,
        TableRow row,
        IReadOnlyList<string> columns)
        => columns.Select(column => row.Values[schema.TryGetColumn(column)!.Ordinal]).ToArray();

    private static bool ValuesEqual(IReadOnlyList<object?> left, IReadOnlyList<object?> right)
    {
        if (left.Count != right.Count)
            return false;
        for (int index = 0; index < left.Count; index++)
            if (!SqlScalarComparer.ValuesEqual(left[index], right[index]))
                return false;
        return true;
    }

    private static bool LabelMatches(PropertyGraphVertexTable mapping, string label)
        => string.Equals(mapping.Label, label, StringComparison.Ordinal);

    private static long ConvertPositiveInt64(object? value, string description)
    {
        try
        {
            long result = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (result > 0)
                return result;
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
        }
        throw new InvalidOperationException($"{description} 必须是正整数。");
    }

    private static IEnumerable<GraphVertex> EnumerateNativeAnchors(
        GraphReadSession session,
        GraphTableSource source,
        LabelId label,
        string idDescription,
        GraphNativeAnchorAccess? access)
    {
        if (TryExtractKeyValues(
            source.Predicate,
            source.LeftVertex.Variable,
            ["id"],
            out IReadOnlyList<object?> key))
        {
            long id = ConvertPositiveInt64(key[0], idDescription);
            GraphVertex? vertex = session.GetVertex(new GraphElementId(id));
            if (vertex is not null && vertex.Labels.Contains(label))
                yield return vertex;
            yield break;
        }

        if (access is { PropertyId: int propertyId, PropertyValue: GraphPropertyValue propertyValue })
        {
            using GraphCursor<GraphVertex> propertyCursor = GraphPlanExecutor.Execute(
                session,
                new GraphNodeScanPlan(
                    label,
                    propertyId,
                    propertyValue,
                    new GraphCursorOptions { PageSize = 256, MaxResults = MaxAnchorRows + 1 }));
            while (true)
            {
                IReadOnlyList<GraphVertex> page = propertyCursor.ReadNextPage();
                if (page.Count == 0)
                    yield break;
                foreach (GraphVertex vertex in page)
                    yield return vertex;
            }
        }

        using GraphCursor<GraphVertex> cursor = GraphPlanExecutor.Execute(
            session,
            new GraphNodeScanPlan(
                label,
                Options: new GraphCursorOptions { PageSize = 256, MaxResults = MaxAnchorRows + 1 }));
        while (true)
        {
            IReadOnlyList<GraphVertex> page = cursor.ReadNextPage();
            if (page.Count == 0)
                yield break;
            foreach (GraphVertex vertex in page)
                yield return vertex;
        }
    }

    private static string FormatIdentifier(IdentifierExpression identifier)
        => identifier.Qualifier is null ? identifier.Name : identifier.Qualifier + "." + identifier.Name;

    private static string FormatRelationalIdentity(string tableName, TableRow row)
        => tableName + ":" + Convert.ToHexString(row.PrimaryKey.Span);

    private static GraphPath ReversePath(GraphPath path)
        => new(
            path.VertexIds.Reverse().ToArray(),
            path.EdgeIds.Reverse().ToArray());

    private static RelationalTraversalPath ReversePath(RelationalTraversalPath path)
        => new(
            path.Vertices.Reverse().ToArray(),
            path.EdgeIdentities.Reverse().ToArray());

    private static void ConsumeRelationalCursorBudget(
        RelationalGraphCursor cursor,
        RelationalPathExecutionState state,
        GraphTableExecutionMetrics metrics)
    {
        state.RemainingScanRows = checked(state.RemainingScanRows - cursor.ExaminedRows);
        state.RemainingScanDuration -= cursor.FallbackDuration;
        metrics.FallbackRows = checked(metrics.FallbackRows + cursor.ExaminedRows);
        metrics.FallbackDuration += cursor.FallbackDuration;
        if (state.RemainingScanRows < 0 || state.RemainingScanDuration <= TimeSpan.Zero)
        {
            throw new GraphTraversalLimitExceededException(
                "GRAPH_TABLE relation path scan fallback 超过整条查询预算。");
        }
    }

    private static void EnsureSupportedShape(SelectStatement statement)
    {
        if (statement.JoinClauses.Count != 0 || statement.GroupBy.Count != 0 || statement.Having is not null)
        {
            throw new NotSupportedException(
                "GRAPH_TABLE 当前支持固定一跳 MATCH、变量谓词、COLUMNS、外层 WHERE/投影/排序/分页；JOIN/GROUP BY 在 M40 #359 接入。");
        }
    }

    private static void ValidatePathPattern(GraphTableSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source.LeftVertex.Variable);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Edge.Variable);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.RightVertex.Variable);
        if (string.Equals(source.LeftVertex.Variable, source.Edge.Variable, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source.LeftVertex.Variable, source.RightVertex.Variable, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source.Edge.Variable, source.RightVertex.Variable, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("GRAPH_TABLE vertex/edge 变量名必须互不相同。");
        }
        if (source.Path is not { } path)
            return;
        if (path.MinDepth < 1 || path.MaxDepth < path.MinDepth || path.MaxDepth > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                "GRAPH_TABLE path 深度必须满足 1 <= min <= max <= 64。");
        }
        if (!Enum.IsDefined(path.Uniqueness))
            throw new ArgumentOutOfRangeException(nameof(source), "GRAPH_TABLE path uniqueness 无效。");
        if (path.Variable is null)
            return;
        ArgumentException.ThrowIfNullOrWhiteSpace(path.Variable);
        if (string.Equals(path.Variable, source.LeftVertex.Variable, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Variable, source.Edge.Variable, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.Variable, source.RightVertex.Variable, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("GRAPH_TABLE path/vertex/edge 变量名必须互不相同。");
        }
    }

    private sealed record GraphAnchorEstimate(
        long AnchorRows,
        long Expansions,
        double Cost,
        string Source,
        GraphNativeAnchorAccess? NativeAccess);

    private sealed record NativePropertyPredicate(int PropertyId, GraphPropertyValue Value);

    private sealed record GraphNativeAnchorAccess(
        string AccessPath,
        string Index,
        int? PropertyId,
        GraphPropertyValue? PropertyValue,
        long? StatisticsSequence,
        string StatisticsFreshness,
        string? FallbackReason);

    private sealed record GraphTableExecutionPlan(
        GraphTableSource Source,
        bool IsRelational,
        bool ReversePathProjection,
        string AnchorSide,
        long EstimatedAnchorRows,
        long EstimatedExpansions,
        double EstimatedCost,
        string EstimateSource,
        GraphNativeAnchorAccess? NativeAnchor,
        bool BidirectionalBfsAdmitted,
        string BidirectionalBfsReason);

    private sealed record GraphTableExecutionOutcome(
        SelectExecutionResult Result,
        GraphTableExecutionPlan Plan,
        GraphTableExecutionMetrics Metrics);

    private sealed class GraphTableExecutionMetrics
    {
        internal string ReadConsistency { get; set; } = "relation_accessor_current";

        internal long? SnapshotSequence { get; set; }

        internal string? SnapshotSequences { get; set; }

        internal long AnchorRows { get; set; }

        internal long Expansions { get; set; }

        internal long GeneratedPaths { get; set; }

        internal int PeakFrontier { get; set; }

        internal long FallbackRows { get; set; }

        internal TimeSpan FallbackDuration { get; set; }

        internal long MatchedRows { get; set; }

        internal long OutputRows { get; set; }

        internal TimeSpan Elapsed { get; set; }
    }

    private sealed record RelationalPattern(
        IReadOnlyList<RelationalPatternBranch> Branches);

    private sealed record RelationalPatternBranch(
        PropertyGraphEdgeTable Edge,
        IReadOnlyList<RelationalPatternOrientation> Orientations);

    private sealed record RelationalPatternOrientation(
        PropertyGraphVertexTable Left,
        PropertyGraphVertexTable Right,
        GraphDirection Direction);

    private sealed record RelationalBinding(
        TableSchema Schema,
        TableRow Row,
        IReadOnlyList<string> Properties);

    private sealed class RelationalPathExecutionState
    {
        internal int RemainingPaths { get; set; } = MaxMatchedRows;

        internal int RemainingMatchedRows { get; set; } = MaxMatchedRows;

        internal int RemainingScanRows { get; set; } = MaxRelationScanRows;

        internal TimeSpan RemainingScanDuration { get; set; } = MaxRelationScanDuration;
    }

    private sealed class RelationalPathBindings
    {
        private string _leftVariable = string.Empty;
        private string _rightVariable = string.Empty;
        private string? _pathVariable;
        private RelationalBinding? _left;
        private RelationalBinding? _right;
        private RelationalTraversalPath? _path;

        internal void Update(
            string leftVariable,
            RelationalBinding left,
            string rightVariable,
            RelationalBinding right,
            string? pathVariable,
            RelationalTraversalPath path)
        {
            _leftVariable = leftVariable;
            _left = left;
            _rightVariable = rightVariable;
            _right = right;
            _pathVariable = pathVariable;
            _path = path;
        }

        internal object? Resolve(IdentifierExpression identifier)
        {
            if (_pathVariable is not null
                && string.Equals(identifier.Qualifier, _pathVariable, StringComparison.OrdinalIgnoreCase))
            {
                RelationalTraversalPath path = _path
                    ?? throw new InvalidOperationException("GRAPH_TABLE relation path binding 未初始化。");
                return identifier.Name.ToLowerInvariant() switch
                {
                    "length" => (long)path.EdgeIdentities.Count,
                    "vertex_ids" => string.Join(',', path.Vertices.Select(static item => item.Identity)),
                    "edge_ids" => string.Join(',', path.EdgeIdentities),
                    "start_id" => path.Vertices[0].Identity,
                    "end_id" => path.Vertices[^1].Identity,
                    _ => throw new InvalidOperationException(
                        $"GRAPH_TABLE relation path 属性列 '{identifier.Name}' 不存在。"),
                };
            }

            RelationalBinding? binding = identifier.Qualifier switch
            {
                { } qualifier when string.Equals(qualifier, _leftVariable, StringComparison.OrdinalIgnoreCase) => _left,
                { } qualifier when string.Equals(qualifier, _rightVariable, StringComparison.OrdinalIgnoreCase) => _right,
                _ => null,
            };
            if (binding is null)
            {
                throw new InvalidOperationException(
                    $"GRAPH_TABLE 变量属性 '{FormatIdentifier(identifier)}' 未在 property graph 中公开。");
            }
            if (!binding.Properties.Contains(identifier.Name, StringComparer.Ordinal))
                return null;
            TableColumn column = binding.Schema.TryGetColumn(identifier.Name)
                ?? throw new InvalidOperationException($"关系列 '{FormatIdentifier(identifier)}' 不存在。");
            return binding.Row.Values[column.Ordinal];
        }
    }

    private sealed class RelationalMatchBindings
    {
        private string _leftVariable = string.Empty;
        private string _edgeVariable = string.Empty;
        private string _rightVariable = string.Empty;
        private RelationalBinding? _left;
        private RelationalBinding? _edge;
        private RelationalBinding? _right;

        internal void Update(
            string leftVariable,
            RelationalBinding left,
            string edgeVariable,
            RelationalBinding edge,
            string rightVariable,
            RelationalBinding right)
        {
            _leftVariable = leftVariable;
            _left = left;
            _edgeVariable = edgeVariable;
            _edge = edge;
            _rightVariable = rightVariable;
            _right = right;
        }

        internal object? Resolve(IdentifierExpression identifier)
        {
            RelationalBinding? binding = identifier.Qualifier switch
            {
                { } qualifier when string.Equals(qualifier, _leftVariable, StringComparison.OrdinalIgnoreCase) => _left,
                { } qualifier when string.Equals(qualifier, _edgeVariable, StringComparison.OrdinalIgnoreCase) => _edge,
                { } qualifier when string.Equals(qualifier, _rightVariable, StringComparison.OrdinalIgnoreCase) => _right,
                _ => null,
            };
            if (binding is null)
            {
                throw new InvalidOperationException(
                    $"GRAPH_TABLE 变量属性 '{FormatIdentifier(identifier)}' 未在 property graph 中公开。");
            }
            if (!binding.Properties.Contains(identifier.Name, StringComparer.Ordinal))
                return null;
            TableColumn column = binding.Schema.TryGetColumn(identifier.Name)
                ?? throw new InvalidOperationException($"关系列 '{FormatIdentifier(identifier)}' 不存在。");
            return binding.Row.Values[column.Ordinal];
        }
    }

    private sealed record RelationalTraversalVertex(
        PropertyGraphVertexTable Mapping,
        TableSchema Schema,
        TableRow Row,
        string Identity);

    private sealed record RelationalTraversalPath(
        IReadOnlyList<RelationalTraversalVertex> Vertices,
        IReadOnlyList<string> EdgeIdentities)
    {
        public RelationalTraversalPath Extend(RelationalTraversalVertex vertex, string edgeIdentity)
            => new(
                Vertices.Append(vertex).ToArray(),
                EdgeIdentities.Append(edgeIdentity).ToArray());
    }

    private enum NativeBindingKind : byte
    {
        Vertex = 1,
        Path = 2,
    }

    private sealed record NativeBinding(GraphVertex? Vertex, GraphEdge? Edge, GraphPath? Path = null);

    private sealed class NativeMatchBindings
    {
        private string _leftVariable = string.Empty;
        private string _edgeVariable = string.Empty;
        private string _rightVariable = string.Empty;
        private string? _pathVariable;
        private NativeBinding? _left;
        private NativeBinding? _edge;
        private NativeBinding? _right;
        private GraphPath? _path;

        internal void Update(
            string leftVariable,
            NativeBinding left,
            string edgeVariable,
            NativeBinding edge,
            string rightVariable,
            NativeBinding right)
        {
            _leftVariable = leftVariable;
            _left = left;
            _edgeVariable = edgeVariable;
            _edge = edge;
            _rightVariable = rightVariable;
            _right = right;
            _pathVariable = null;
            _path = null;
        }

        internal void UpdatePath(
            string leftVariable,
            NativeBinding left,
            string rightVariable,
            NativeBinding right,
            string? pathVariable,
            GraphPath path)
        {
            _leftVariable = leftVariable;
            _left = left;
            _edgeVariable = string.Empty;
            _edge = null;
            _rightVariable = rightVariable;
            _right = right;
            _pathVariable = pathVariable;
            _path = path;
        }

        internal object? Resolve(IdentifierExpression identifier)
        {
            if (_pathVariable is not null
                && string.Equals(identifier.Qualifier, _pathVariable, StringComparison.OrdinalIgnoreCase))
            {
                GraphPath path = _path
                    ?? throw new InvalidOperationException("GRAPH_TABLE native path binding 未初始化。");
                return ResolveNativePath(path, identifier);
            }
            NativeBinding? binding = identifier.Qualifier switch
            {
                { } qualifier when string.Equals(qualifier, _leftVariable, StringComparison.OrdinalIgnoreCase) => _left,
                { } qualifier when string.Equals(qualifier, _edgeVariable, StringComparison.OrdinalIgnoreCase) => _edge,
                { } qualifier when string.Equals(qualifier, _rightVariable, StringComparison.OrdinalIgnoreCase) => _right,
                _ => null,
            };
            return ResolveNativeBinding(binding, identifier);
        }
    }
}
