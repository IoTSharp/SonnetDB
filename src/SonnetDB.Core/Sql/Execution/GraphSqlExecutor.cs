using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Graphs;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>原生属性图 SQL/关系化读取的执行器。</summary>
internal static class GraphSqlExecutor
{
    private static readonly IReadOnlyList<string> GraphColumns =
    [
        "name", "storage_id", "record_format_version", "created_utc", "sql_contract",
        "label_index_policy", "property_index_policy",
    ];
    private static readonly IReadOnlyList<string> PropertyGraphColumns =
        ["name", "vertex_table_count", "edge_table_count", "created_utc"];
    private static readonly IReadOnlyList<string> PropertyGraphMappingColumns =
    [
        "kind", "table_name", "label", "key_columns", "source_table", "destination_table",
        "property_columns", "source_access_path", "source_index", "destination_access_path", "destination_index",
    ];

    /// <summary>判断 SELECT 是否来自 graph_nodes/graph_edges 计划源。</summary>
    internal static bool IsGraphSelect(SelectStatement statement)
        => statement.GraphTable is not null
            || statement.TableValuedFunction is { Name: var name }
                && IsGraphFunction(name);

    internal static bool IsGraphFunction(string name)
        => string.Equals(name, "graph_nodes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "graph_edges", StringComparison.OrdinalIgnoreCase);

    internal static RowsAffectedExecutionResult CreateGraph(Tsdb tsdb, CreateGraphStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        if (tsdb.Graphs.Catalog.TryGet(statement.Name) is not null)
        {
            if (statement.IfNotExists)
                return new RowsAffectedExecutionResult(statement.Name, 0, "create_graph");
            throw new InvalidOperationException($"graph '{statement.Name}' 已存在。");
        }

        _ = tsdb.Graphs.Create(statement.Name);
        return new RowsAffectedExecutionResult(statement.Name, 1, "create_graph");
    }

    internal static RowsAffectedExecutionResult DropGraph(Tsdb tsdb, DropGraphStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        bool dropped = tsdb.Graphs.Drop(statement.Name);
        if (!dropped && !statement.IfExists)
            throw new InvalidOperationException($"graph '{statement.Name}' 不存在。");
        return new RowsAffectedExecutionResult(statement.Name, dropped ? 1 : 0, "drop_graph");
    }

    internal static RowsAffectedExecutionResult CreatePropertyGraph(
        Tsdb tsdb,
        CreatePropertyGraphStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        if (tsdb.Graphs.PropertyGraphs.TryGet(statement.Name) is not null)
        {
            if (statement.IfNotExists)
                return new RowsAffectedExecutionResult(statement.Name, 0, "create_property_graph");
            throw new InvalidOperationException($"property graph '{statement.Name}' 已存在。");
        }

        PropertyGraphDefinition definition = PropertyGraphDefinition.Create(
            statement.Name,
            statement.VertexTables.Select(static item => new PropertyGraphVertexTable(
                item.TableName,
                item.KeyColumns,
                item.Label,
                item.PropertyColumns)).ToArray(),
            statement.EdgeTables.Select(static item => new PropertyGraphEdgeTable(
                item.TableName,
                item.KeyColumns,
                item.SourceTable,
                item.SourceColumns,
                item.SourceReferenceColumns,
                item.DestinationTable,
                item.DestinationColumns,
                item.DestinationReferenceColumns,
                item.Label,
                item.PropertyColumns)).ToArray());
        _ = tsdb.Graphs.CreatePropertyGraph(definition);
        return new RowsAffectedExecutionResult(statement.Name, 1, "create_property_graph");
    }

    internal static RowsAffectedExecutionResult DropPropertyGraph(
        Tsdb tsdb,
        DropPropertyGraphStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        bool dropped = tsdb.Graphs.DropPropertyGraph(statement.Name);
        if (!dropped && !statement.IfExists)
            throw new InvalidOperationException($"property graph '{statement.Name}' 不存在。");
        return new RowsAffectedExecutionResult(statement.Name, dropped ? 1 : 0, "drop_property_graph");
    }

    internal static RowsAffectedExecutionResult InsertGraph(Tsdb tsdb, InsertGraphStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        EnsureMutationKind(statement.Kind);
        EnsureValuesMutationMode(statement.Mode);
        IReadOnlyDictionary<string, int> ordinals = BuildColumnOrdinals(statement.Columns);
        EnsureColumns(statement, ordinals);
        if (statement.Rows.Count == 0)
            return new RowsAffectedExecutionResult(statement.GraphName, 0, ValuesOperation(statement.Mode));

        GraphStore store = tsdb.Graphs.Open(statement.GraphName);
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        foreach (IReadOnlyList<SqlExpression> row in statement.Rows)
        {
            if (row.Count != statement.Columns.Count)
                throw new InvalidOperationException("Graph values mutation 的值数量必须与列数量一致。");
            object?[] values = row.Select(static expression => EvaluateDmlExpression(expression)).ToArray();
            IReadOnlyList<GraphProperty> properties = ParseProperties(statement.Columns, values);
            IReadOnlyList<int> uniquePropertyIds = ParseUniquePropertyIds(ordinals, values, properties);
            long expectedElementVersion = statement.Mode == GraphValuesMutationMode.Insert
                ? 0
                : ToNonNegativeInt64(values[ordinals["element_version"]], "element_version");
            if (statement.Kind == GraphMutationKind.Vertex)
            {
                transaction.UpsertVertex(
                    new GraphElementId(ToPositiveInt64(values[ordinals["id"]], "id")),
                    expectedElementVersion,
                    ParseLabels(values[ordinals["labels"]]),
                    properties,
                    uniquePropertyIds);
            }
            else
            {
                transaction.UpsertEdge(
                    new GraphElementId(ToPositiveInt64(values[ordinals["id"]], "id")),
                    expectedElementVersion,
                    new GraphElementId(ToPositiveInt64(values[ordinals["source_id"]], "source_id")),
                    new GraphElementId(ToPositiveInt64(values[ordinals["target_id"]], "target_id")),
                    new LabelId(ToPositiveInt32(values[ordinals["label_id"]], "label_id")),
                    properties,
                    uniquePropertyIds);
            }
        }
        transaction.Commit();
        return new RowsAffectedExecutionResult(
            statement.GraphName,
            statement.Rows.Count,
            ValuesOperation(statement.Mode));
    }

    internal static RowsAffectedExecutionResult UpdateGraph(Tsdb tsdb, UpdateGraphStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        EnsureMutationKind(statement.Kind);
        IReadOnlyDictionary<string, UpdateAssignment> assignments = BuildAssignments(statement.Assignments);
        EnsureUpdateColumns(statement.Kind, assignments.Keys);
        (GraphElementId Id, long Version) key = ParseMutationKey(statement.Where);
        GraphStore store = tsdb.Graphs.Open(statement.GraphName);
        using GraphReadSession read = store.BeginRead();
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        if (statement.Kind == GraphMutationKind.Vertex)
        {
            GraphVertex current = read.GetVertex(key.Id)
                ?? throw VersionConflict("vertex", key.Id, key.Version, 0);
            EnsureExpectedVersion("vertex", key, current.ElementVersion);
            IReadOnlyList<LabelId> labels = assignments.TryGetValue("labels", out UpdateAssignment? labelsAssignment)
                ? ParseLabels(EvaluateUpdateValue(labelsAssignment.Value, "labels"))
                : current.Labels;
            IReadOnlyList<GraphProperty> properties = ApplyPropertyAssignments(current.Properties, assignments);
            IReadOnlyList<int> uniquePropertyIds = ResolveUpdatedUniquePropertyIds(
                read.GetOwnedUniquePropertyIds(current),
                assignments,
                properties);
            transaction.UpsertVertex(
                key.Id,
                key.Version,
                labels,
                properties,
                uniquePropertyIds);
        }
        else
        {
            GraphEdge current = read.GetEdge(key.Id)
                ?? throw VersionConflict("edge", key.Id, key.Version, 0);
            EnsureExpectedVersion("edge", key, current.ElementVersion);
            GraphElementId sourceId = assignments.TryGetValue("source_id", out UpdateAssignment? sourceAssignment)
                ? new GraphElementId(ToPositiveInt64(EvaluateUpdateValue(sourceAssignment.Value, "source_id"), "source_id"))
                : current.SourceId;
            GraphElementId targetId = assignments.TryGetValue("target_id", out UpdateAssignment? targetAssignment)
                ? new GraphElementId(ToPositiveInt64(EvaluateUpdateValue(targetAssignment.Value, "target_id"), "target_id"))
                : current.TargetId;
            LabelId labelId = assignments.TryGetValue("label_id", out UpdateAssignment? labelAssignment)
                ? new LabelId(ToPositiveInt32(EvaluateUpdateValue(labelAssignment.Value, "label_id"), "label_id"))
                : current.LabelId;
            IReadOnlyList<GraphProperty> properties = ApplyPropertyAssignments(current.Properties, assignments);
            IReadOnlyList<int> uniquePropertyIds = ResolveUpdatedUniquePropertyIds(
                read.GetOwnedUniquePropertyIds(current),
                assignments,
                properties);
            transaction.UpsertEdge(
                key.Id,
                key.Version,
                sourceId,
                targetId,
                labelId,
                properties,
                uniquePropertyIds);
        }
        transaction.Commit();
        return new RowsAffectedExecutionResult(statement.GraphName, 1, "update_graph");
    }

    internal static RowsAffectedExecutionResult DeleteGraph(Tsdb tsdb, DeleteGraphStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        EnsureMutationKind(statement.Kind);
        (GraphElementId Id, long Version) key = ParseMutationKey(statement.Where);
        GraphTransaction transaction = tsdb.Graphs.Open(statement.GraphName).BeginTransaction(Guid.NewGuid());
        if (statement.Kind == GraphMutationKind.Vertex)
            transaction.DeleteVertex(key.Id, key.Version);
        else
            transaction.DeleteEdge(key.Id, key.Version);
        transaction.Commit();
        return new RowsAffectedExecutionResult(statement.GraphName, 1, "delete_graph");
    }

    internal static SelectExecutionResult AnalyzeGraph(Tsdb tsdb, AnalyzeGraphStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        using GraphReadSession read = tsdb.Graphs.Open(statement.GraphName).BeginRead();
        GraphStatistics statistics = read.RefreshStatistics();
        return new SelectExecutionResult(
            ["graph", "statistics_sequence", "vertex_count", "edge_count", "value_group_count"],
            [[
                statement.GraphName,
                statistics.Sequence,
                statistics.VertexCount,
                statistics.EdgeCount,
                statistics.ValueCardinality.Count,
            ]]);
    }

    internal static SelectExecutionResult ShowGraphs(Tsdb tsdb)
        => new(
            GraphColumns,
            tsdb.Graphs.Catalog.Snapshot().Select(static definition =>
                (IReadOnlyList<object?>)[
                    definition.Name,
                    definition.StorageId.ToString("D", CultureInfo.InvariantCulture),
                    definition.RecordFormatVersion,
                    new DateTime(definition.CreatedAtUtcTicks, DateTimeKind.Utc),
                    GraphSqlContract.CurrentName,
                    GraphSqlContract.LabelIndexPolicy,
                    GraphSqlContract.PropertyIndexPolicy,
                ]).ToArray());

    internal static SelectExecutionResult ShowPropertyGraphs(Tsdb tsdb)
        => new(
            PropertyGraphColumns,
            tsdb.Graphs.PropertyGraphs.Snapshot().Select(static definition =>
                (IReadOnlyList<object?>)[
                    definition.Name,
                    definition.VertexTables.Count,
                    definition.EdgeTables.Count,
                    new DateTime(definition.CreatedAtUtcTicks, DateTimeKind.Utc),
                ]).ToArray());

    internal static SelectExecutionResult DescribeGraph(Tsdb tsdb, string name)
    {
        GraphDefinition definition = tsdb.Graphs.Catalog.TryGet(name)
            ?? throw new InvalidOperationException($"graph '{name}' 不存在。");
        return new SelectExecutionResult(
            GraphColumns,
            [
                [
                    definition.Name,
                    definition.StorageId.ToString("D", CultureInfo.InvariantCulture),
                    definition.RecordFormatVersion,
                    new DateTime(definition.CreatedAtUtcTicks, DateTimeKind.Utc),
                    GraphSqlContract.CurrentName,
                    GraphSqlContract.LabelIndexPolicy,
                    GraphSqlContract.PropertyIndexPolicy,
                ],
            ]);
    }

    internal static SelectExecutionResult DescribePropertyGraph(Tsdb tsdb, string name)
    {
        PropertyGraphDefinition definition = tsdb.Graphs.PropertyGraphs.TryGet(name)
            ?? throw new InvalidOperationException($"property graph '{name}' 不存在。");
        RelationalGraphAccessor accessor = tsdb.Graphs.OpenPropertyGraph(name);
        var rows = new List<IReadOnlyList<object?>>(
            definition.VertexTables.Count + definition.EdgeTables.Count);
        foreach (PropertyGraphVertexTable vertex in definition.VertexTables)
        {
            RelationalGraphAccessPlan keyPlan = accessor.ExplainVertexAccess(vertex.TableName);
            rows.Add([
                "vertex",
                vertex.TableName,
                vertex.Label,
                string.Join(',', vertex.KeyColumns),
                null,
                null,
                string.Join(',', vertex.PropertyColumns),
                keyPlan.AccessPath,
                keyPlan.IndexName,
                null,
                null,
            ]);
        }
        foreach (PropertyGraphEdgeTable edge in definition.EdgeTables)
        {
            IReadOnlyList<RelationalGraphAccessPlan> outgoing =
                accessor.ExplainEdgeAccess(edge.TableName, GraphDirection.Outgoing);
            IReadOnlyList<RelationalGraphAccessPlan> incoming =
                accessor.ExplainEdgeAccess(edge.TableName, GraphDirection.Incoming);
            rows.Add([
                "edge",
                edge.TableName,
                edge.Label,
                string.Join(',', edge.KeyColumns),
                edge.SourceTable,
                edge.DestinationTable,
                string.Join(',', edge.PropertyColumns),
                outgoing[0].AccessPath,
                outgoing[0].IndexName,
                incoming[0].AccessPath,
                incoming[0].IndexName,
            ]);
        }
        return new SelectExecutionResult(PropertyGraphMappingColumns, rows);
    }

    internal static SelectExecutionResult ExplainPropertyGraph(Tsdb tsdb, string name)
    {
        PropertyGraphDefinition definition = tsdb.Graphs.PropertyGraphs.TryGet(name)
            ?? throw new InvalidOperationException($"property graph '{name}' 不存在。");
        RelationalGraphAccessor accessor = tsdb.Graphs.OpenPropertyGraph(name);
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[] { "graph_kind", "relational_mapping" },
            new object?[] { "logical_plan", "RelationalGraphAccessor" },
            new object?[] { "copies_relational_rows", false },
            new object?[] { "scan_fallback_max_rows", 10_000 },
            new object?[] { "scan_fallback_max_ms", 50 },
        };
        foreach (PropertyGraphEdgeTable edge in definition.EdgeTables)
        {
            foreach (GraphDirection direction in new[] { GraphDirection.Outgoing, GraphDirection.Incoming })
            {
                RelationalGraphAccessPlan plan = accessor.ExplainEdgeAccess(edge.TableName, direction)[0];
                string prefix = $"edge.{edge.TableName}.{direction.ToString().ToLowerInvariant()}";
                rows.Add(new object?[] { prefix + ".access_path", plan.AccessPath });
                rows.Add(new object?[] { prefix + ".index", plan.IndexName });
            }
        }
        return new SelectExecutionResult(["key", "value"], rows);
    }

    internal static SelectExecutionResult ExplainShowPropertyGraphs(Tsdb tsdb)
        => new(
            ["key", "value"],
            [
                ["statement_type", "show_property_graphs"],
                ["access_path", "catalog"],
                ["estimated_scanned_rows", tsdb.Graphs.PropertyGraphs.Count],
            ]);

    internal static SelectExecutionResult ExecuteSelect(Tsdb tsdb, SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        if (statement.GraphTable is not null)
            return GraphTableSqlExecutor.Execute(tsdb, statement);
        FunctionCallExpression call = statement.TableValuedFunction
            ?? throw new InvalidOperationException("图 SQL 缺少表值函数调用。");
        if (statement.JoinClauses.Count != 0 || statement.GroupBy.Count != 0 || statement.Having is not null)
        {
            throw new NotSupportedException(
                "graph_nodes/graph_edges 当前支持 WHERE、投影、DISTINCT、ORDER BY 与分页；JOIN/GROUP BY 在 M40 #359 接入。");
        }
        string graphName = ResolveGraphName(call);
        GraphReadSession session = tsdb.Graphs.Open(graphName).BeginRead();
        try
        {
            bool edges = string.Equals(call.Name, "graph_edges", StringComparison.OrdinalIgnoreCase);
            string[] sourceColumns = edges
                ? ["id", "element_version", "source_id", "target_id", "label_id", "property_count"]
                : ["id", "element_version", "labels", "property_count"];
            var sourceRows = new List<IReadOnlyList<object?>>();
            if (edges)
            {
                using GraphCursor<GraphEdge> cursor = GraphPlanExecutor.Execute(
                    session,
                    new GraphEdgeScanPlan(ToOptionalLabel(call), Options: OptionsForSql()));
                while (true)
                {
                    IReadOnlyList<GraphEdge> page = cursor.ReadNextPage();
                    if (page.Count == 0)
                        break;
                    foreach (GraphEdge edge in page)
                    {
                        sourceRows.Add([
                            edge.Id.Value,
                            edge.ElementVersion,
                            edge.SourceId.Value,
                            edge.TargetId.Value,
                            edge.LabelId.Value,
                            edge.Properties.Count,
                        ]);
                    }
                }
            }
            else
            {
                using GraphCursor<GraphVertex> cursor = GraphPlanExecutor.Execute(
                    session,
                    new GraphNodeScanPlan(ToOptionalLabel(call), Options: OptionsForSql()));
                while (true)
                {
                    IReadOnlyList<GraphVertex> page = cursor.ReadNextPage();
                    if (page.Count == 0)
                        break;
                    foreach (GraphVertex vertex in page)
                    {
                        sourceRows.Add([
                            vertex.Id.Value,
                            vertex.ElementVersion,
                            string.Join(',', vertex.Labels.Select(static label => label.Value.ToString(CultureInfo.InvariantCulture))),
                            vertex.Properties.Count,
                        ]);
                    }
                }
            }

            return ApplySelectShape(statement, sourceColumns, sourceRows, "图");
        }
        finally
        {
            session.Dispose();
        }
    }

    internal static SelectExecutionResult ExplainSelect(SelectStatement statement)
    {
        if (statement.GraphTable is not null)
            throw new InvalidOperationException("GRAPH_TABLE EXPLAIN 需要数据库 catalog 上下文。");
        FunctionCallExpression call = statement.TableValuedFunction
            ?? throw new InvalidOperationException("图 EXPLAIN 缺少表值函数调用。");
        bool edges = string.Equals(call.Name, "graph_edges", StringComparison.OrdinalIgnoreCase);
        return new SelectExecutionResult(
            ["key", "value"],
            [
                ["statement_type", "select"],
                ["logical_plan", edges ? "GraphEdgeScan" : "GraphNodeScan"],
                ["access_path", "native_adjacency_or_index"],
                ["source", edges ? "graph_edges" : "graph_nodes"],
                ["bounded", true],
                ["fallback_reason", null],
            ]);
    }

    internal static SelectExecutionResult ApplySelectShape(
        SelectStatement statement,
        IReadOnlyList<string> sourceColumns,
        IEnumerable<IReadOnlyList<object?>> sourceRows,
        string context)
    {
        ArgumentNullException.ThrowIfNull(sourceRows);
        var lookup = sourceColumns
            .Select((name, ordinal) => (name, ordinal))
            .ToDictionary(static item => item.name, static item => item.ordinal, StringComparer.OrdinalIgnoreCase);
        if (statement.Where is not null)
            SqlProjectionExpressionEvaluator.Validate(statement.Where, id => lookup.ContainsKey(id.Name), context + " WHERE");
        foreach (SelectItem item in statement.Projections)
            if (item.Expression is not StarExpression)
                SqlProjectionExpressionEvaluator.Validate(item.Expression, id => lookup.ContainsKey(id.Name), context + " 投影");

        var columns = new List<string>();
        foreach (SelectItem item in statement.Projections)
        {
            if (item.Expression is StarExpression)
            {
                if (item.Alias is not null)
                    throw new InvalidOperationException($"{context} SELECT 的 '*' 不允许带 alias。");
                columns.AddRange(sourceColumns);
            }
            else
            {
                columns.Add(item.Alias ?? (item.Expression as IdentifierExpression)?.Name ?? "expression");
            }
        }

        IEnumerable<IReadOnlyList<object?>> ProjectRows()
        {
            foreach (IReadOnlyList<object?> row in sourceRows)
            {
                SqlExecutor.ThrowIfCancellationRequested();
                if (statement.Where is not null
                    && SqlProjectionExpressionEvaluator.Evaluate(
                        statement.Where,
                        id => row[lookup[id.Name]],
                        context + " WHERE") is not true)
                {
                    continue;
                }

                var output = new List<object?>(columns.Count);
                foreach (SelectItem item in statement.Projections)
                {
                    if (item.Expression is StarExpression)
                        output.AddRange(row);
                    else
                        output.Add(SqlProjectionExpressionEvaluator.Evaluate(
                            item.Expression,
                            id => row[lookup[id.Name]],
                            context + " 投影"));
                }
                yield return output;
            }
        }

        IEnumerable<IReadOnlyList<object?>> projected = ProjectRows();
        if (statement.OrderByList.Count != 0)
        {
            var outputLookup = columns
                .Select((name, ordinal) => (name, ordinal))
                .ToDictionary(static item => item.name, static item => item.ordinal, StringComparer.OrdinalIgnoreCase);
            var ordering = statement.OrderByList.Select(item =>
            {
                if (item.Expression is not IdentifierExpression identifier
                    || !outputLookup.TryGetValue(identifier.Name, out int ordinal))
                {
                    throw new InvalidOperationException(
                        $"{context} SELECT 的 ORDER BY 当前必须引用输出列名或 alias。");
                }
                return (Ordinal: ordinal, item.Direction);
            }).ToArray();
            var comparer = Comparer<IReadOnlyList<object?>>.Create((left, right) =>
            {
                foreach (var item in ordering)
                {
                    int comparison = SqlScalarComparer.Compare(left[item.Ordinal], right[item.Ordinal]) ?? 0;
                    if (comparison != 0)
                        return item.Direction == SortDirection.Descending ? -comparison : comparison;
                }
                return 0;
            });
            IReadOnlyList<IReadOnlyList<object?>> sorted = TopN.OrderByThenPaginate(
                projected,
                comparer,
                statement.Pagination?.Offset ?? 0,
                statement.Pagination?.Fetch,
                SqlSpillCodecs.ReadOnlyRows);
            return new SelectExecutionResult(columns, sorted);
        }

        int offset = statement.Pagination?.Offset ?? 0;
        int? fetch = statement.Pagination?.Fetch;
        IEnumerable<IReadOnlyList<object?>> paged = projected.Skip(offset);
        if (fetch is not null)
            paged = paged.Take(fetch.Value);
        return new SelectExecutionResult(columns, paged.ToArray());
    }

    private static string ResolveGraphName(FunctionCallExpression call)
    {
        if (call.Arguments.Count is < 1 or > 2)
            throw new InvalidOperationException($"{call.Name}(...) 必须提供 graph 名称。");
        return call.Arguments[0] switch
        {
            IdentifierExpression identifier => identifier.Name,
            LiteralExpression { Kind: SqlLiteralKind.String, StringValue: not null } literal => literal.StringValue,
            _ => throw new InvalidOperationException($"{call.Name}(...) 的 graph 名称必须是字符串或标识符。"),
        };
    }

    private static LabelId? ToOptionalLabel(FunctionCallExpression call)
    {
        if (call.Arguments.Count < 2)
            return null;
        if (call.Arguments[1] is not LiteralExpression { Kind: SqlLiteralKind.Integer, IntegerValue: > 0 } label)
            throw new InvalidOperationException("图表值函数的第二个参数必须是正整数 label_id。");
        return new LabelId(checked((int)label.IntegerValue));
    }

    private static void EnsureColumns(
        InsertGraphStatement statement,
        IReadOnlyDictionary<string, int> columns)
    {
        var required = new List<string>(statement.Kind == GraphMutationKind.Vertex
            ? ["id", "labels"]
            : ["id", "source_id", "target_id", "label_id"]);
        if (statement.Mode == GraphValuesMutationMode.Upsert)
            required.Add("element_version");
        foreach (string column in required)
            if (!columns.ContainsKey(column))
                throw new InvalidOperationException(
                    $"Graph {statement.Mode} {statement.Kind} 缺少必需列 '{column}'。");
        foreach (string column in columns.Keys)
        {
            bool common = required.Contains(column, StringComparer.OrdinalIgnoreCase)
                || string.Equals(column, "unique_property_ids", StringComparison.OrdinalIgnoreCase)
                || GraphSqlContract.TryParsePropertyColumn(column, out _);
            if (!common)
            {
                throw new NotSupportedException(
                    $"Graph {statement.Mode} {statement.Kind} 当前不支持列 '{column}'；"
                    + "属性列必须使用 property_<positive-id>。");
            }
        }
    }

    private static void EnsureUpdateColumns(GraphMutationKind kind, IEnumerable<string> columns)
    {
        string[] fixedColumns = kind == GraphMutationKind.Vertex
            ? ["labels", "unique_property_ids"]
            : ["source_id", "target_id", "label_id", "unique_property_ids"];
        foreach (string column in columns)
        {
            if (!fixedColumns.Contains(column, StringComparer.OrdinalIgnoreCase)
                && !GraphSqlContract.TryParsePropertyColumn(column, out _))
            {
                throw new NotSupportedException(
                    $"UPDATE GRAPH {kind} 不支持列 '{column}'；属性列必须使用 property_<positive-id>。");
            }
        }
    }

    private static void EnsureMutationKind(GraphMutationKind kind)
    {
        if (kind is not GraphMutationKind.Vertex and not GraphMutationKind.Edge)
            throw new InvalidOperationException($"图 INSERT 不支持 mutation kind 值 '{(byte)kind}'。");
    }

    private static void EnsureValuesMutationMode(GraphValuesMutationMode mode)
    {
        if (mode is not GraphValuesMutationMode.Insert and not GraphValuesMutationMode.Upsert)
            throw new InvalidOperationException($"Graph values mutation mode '{(byte)mode}' 无效。");
    }

    private static string ValuesOperation(GraphValuesMutationMode mode)
        => mode == GraphValuesMutationMode.Insert ? "insert_graph" : "upsert_graph";

    private static IReadOnlyDictionary<string, int> BuildColumnOrdinals(IReadOnlyList<string> columns)
    {
        var ordinals = new Dictionary<string, int>(columns.Count, StringComparer.OrdinalIgnoreCase);
        for (int ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            string column = columns[ordinal];
            if (!ordinals.TryAdd(column, ordinal))
                throw new InvalidOperationException($"图 INSERT 列 '{column}' 重复声明。");
        }
        return ordinals;
    }

    private static IReadOnlyDictionary<string, UpdateAssignment> BuildAssignments(
        IReadOnlyList<UpdateAssignment> assignments)
    {
        if (assignments.Count == 0)
            throw new InvalidOperationException("UPDATE GRAPH 至少需要一个 assignment。");
        var result = new Dictionary<string, UpdateAssignment>(assignments.Count, StringComparer.OrdinalIgnoreCase);
        foreach (UpdateAssignment assignment in assignments)
        {
            if (!result.TryAdd(assignment.ColumnName, assignment))
                throw new InvalidOperationException($"UPDATE GRAPH 列 '{assignment.ColumnName}' 重复声明。");
        }
        return result;
    }

    private static IReadOnlyList<GraphProperty> ParseProperties(
        IReadOnlyList<string> columns,
        IReadOnlyList<object?> values)
    {
        var properties = new List<GraphProperty>();
        var ids = new HashSet<int>();
        for (int ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            if (!GraphSqlContract.TryParsePropertyColumn(columns[ordinal], out int propertyId))
                continue;
            if (!ids.Add(propertyId))
                throw new InvalidOperationException($"Graph property {propertyId} 重复声明。");
            if (values[ordinal] is not null)
                properties.Add(new GraphProperty(propertyId, ToGraphPropertyValue(values[ordinal]!)));
        }
        properties.Sort(static (left, right) => left.PropertyId.CompareTo(right.PropertyId));
        return properties;
    }

    private static IReadOnlyList<int> ParseUniquePropertyIds(
        IReadOnlyDictionary<string, int> ordinals,
        IReadOnlyList<object?> values,
        IReadOnlyList<GraphProperty> properties)
    {
        if (!ordinals.TryGetValue("unique_property_ids", out int ordinal))
            return [];
        IReadOnlyList<int> ids = ParsePositiveInt32List(values[ordinal], "unique_property_ids", allowEmpty: true);
        HashSet<int> available = properties.Select(static property => property.PropertyId).ToHashSet();
        foreach (int id in ids)
            if (!available.Contains(id))
                throw new InvalidOperationException($"unique property {id} 必须同时出现在当前 property 列集合中。");
        return ids;
    }

    private static IReadOnlyList<GraphProperty> ApplyPropertyAssignments(
        IReadOnlyList<GraphProperty> current,
        IReadOnlyDictionary<string, UpdateAssignment> assignments)
    {
        var properties = current.ToDictionary(static property => property.PropertyId);
        foreach ((string column, UpdateAssignment assignment) in assignments)
        {
            if (!GraphSqlContract.TryParsePropertyColumn(column, out int propertyId))
                continue;
            object? value = EvaluateUpdateValue(assignment.Value, column, allowDefault: true);
            if (value is null)
                properties.Remove(propertyId);
            else
                properties[propertyId] = new GraphProperty(propertyId, ToGraphPropertyValue(value));
        }
        return properties.Values.OrderBy(static property => property.PropertyId).ToArray();
    }

    private static IReadOnlyList<int> ResolveUpdatedUniquePropertyIds(
        IReadOnlyList<int> current,
        IReadOnlyDictionary<string, UpdateAssignment> assignments,
        IReadOnlyList<GraphProperty> properties)
    {
        IReadOnlyList<int> result = current;
        if (assignments.TryGetValue("unique_property_ids", out UpdateAssignment? assignment))
        {
            result = ParsePositiveInt32List(
                EvaluateUpdateValue(assignment.Value, "unique_property_ids", allowDefault: true),
                "unique_property_ids",
                allowEmpty: true);
        }
        HashSet<int> available = properties.Select(static property => property.PropertyId).ToHashSet();
        return result.Where(available.Contains).Distinct().Order().ToArray();
    }

    private static object? EvaluateDmlExpression(SqlExpression expression)
    {
        if (expression is DefaultValueExpression)
            return null;

        SqlProjectionExpressionEvaluator.Validate(
            expression,
            static _ => false,
            "图 INSERT");
        return SqlProjectionExpressionEvaluator.Evaluate(
            expression,
            static _ => throw new InvalidOperationException("图 INSERT 不允许列引用。"),
            "图 INSERT");
    }

    private static object? EvaluateUpdateValue(
        SqlExpression expression,
        string column,
        bool allowDefault = false)
    {
        if (expression is DefaultValueExpression)
        {
            if (allowDefault)
                return null;
            throw new InvalidOperationException($"UPDATE GRAPH 列 '{column}' 不支持 DEFAULT。");
        }
        SqlProjectionExpressionEvaluator.Validate(
            expression,
            static _ => false,
            "UPDATE GRAPH");
        return SqlProjectionExpressionEvaluator.Evaluate(
            expression,
            static _ => throw new InvalidOperationException("UPDATE GRAPH assignment 不允许列引用。"),
            "UPDATE GRAPH");
    }

    private static IReadOnlyList<LabelId> ParseLabels(object? value)
    {
        if (value is long single)
            return [new LabelId(ToPositiveInt32(single, "labels"))];
        if (value is string text)
        {
            var labels = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(label => new LabelId(ToPositiveInt32(label, "labels")))
                .ToArray();
            if (labels.Length != 0)
                return labels;
        }
        throw new InvalidOperationException("图顶点 labels 必须是正整数或逗号分隔的正整数文本。");
    }

    private static IReadOnlyList<int> ParsePositiveInt32List(
        object? value,
        string column,
        bool allowEmpty)
    {
        if (value is null && allowEmpty)
            return [];
        if (value is long single)
            return [ToPositiveInt32(single, column)];
        if (value is string text)
        {
            int[] result = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => ToPositiveInt32(item, column))
                .Distinct()
                .Order()
                .ToArray();
            if (result.Length != 0 || allowEmpty)
                return result;
        }
        throw new InvalidOperationException($"Graph 列 '{column}' 必须是正整数或逗号分隔的正整数文本。");
    }

    private static GraphPropertyValue ToGraphPropertyValue(object value)
        => value switch
        {
            long integer => GraphPropertyValue.FromInt64(integer),
            double number when double.IsFinite(number) => GraphPropertyValue.FromFloat64(number),
            double => throw new InvalidOperationException("Graph Float64 property 必须是有限值。"),
            bool boolean => GraphPropertyValue.FromBoolean(boolean),
            string text => GraphPropertyValue.FromString(text),
            DateTimeOffset timestamp => GraphPropertyValue.FromDateTime(timestamp),
            DateTime timestamp => GraphPropertyValue.FromDateTime(new DateTimeOffset(
                timestamp.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
                    : timestamp.ToUniversalTime())),
            byte[] blob => GraphPropertyValue.FromBlob(blob),
            _ => throw new NotSupportedException(
                $"Graph SQL property 不支持 CLR 类型 {value.GetType().FullName}。"),
        };

    private static (GraphElementId Id, long Version) ParseMutationKey(SqlExpression where)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        Visit(where);
        if (!values.TryGetValue("id", out object? idValue)
            || !values.TryGetValue("element_version", out object? versionValue)
            || values.Count != 2)
        {
            throw new InvalidOperationException(
                "Graph UPDATE/DELETE WHERE 必须且只能包含 id = ... AND element_version = ...。");
        }
        return (
            new GraphElementId(ToPositiveInt64(idValue, "id")),
            ToPositiveInt64(versionValue, "element_version"));

        void Visit(SqlExpression expression)
        {
            if (expression is BinaryExpression { Operator: SqlBinaryOperator.And } conjunction)
            {
                Visit(conjunction.Left);
                Visit(conjunction.Right);
                return;
            }
            if (expression is not BinaryExpression { Operator: SqlBinaryOperator.Equal } equality)
                throw new InvalidOperationException("Graph UPDATE/DELETE WHERE 只支持 AND 连接的精确等值条件。");
            IdentifierExpression? identifier = equality.Left as IdentifierExpression;
            SqlExpression valueExpression = equality.Right;
            if (identifier is null)
            {
                identifier = equality.Right as IdentifierExpression;
                valueExpression = equality.Left;
            }
            if (identifier is null || identifier.Qualifier is not null)
                throw new InvalidOperationException("Graph UPDATE/DELETE WHERE 条件必须直接引用 id 或 element_version。");
            if (!values.TryAdd(identifier.Name, EvaluateDmlExpression(valueExpression)))
                throw new InvalidOperationException($"Graph UPDATE/DELETE WHERE 条件 '{identifier.Name}' 重复声明。");
        }
    }

    private static void EnsureExpectedVersion(
        string kind,
        (GraphElementId Id, long Version) key,
        long actual)
    {
        if (key.Version != actual)
            throw VersionConflict(kind, key.Id, key.Version, actual);
    }

    private static GraphConcurrencyException VersionConflict(
        string kind,
        GraphElementId id,
        long expected,
        long actual)
        => new($"Graph {kind} {id} element version 冲突：期望 {expected}，实际 {actual}。");

    private static long ToPositiveInt64(object? value, string column)
    {
        try
        {
            long result = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (result > 0)
                return result;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
        }
        throw new InvalidOperationException($"图 INSERT 列 '{column}' 必须是正整数。");
    }

    private static long ToNonNegativeInt64(object? value, string column)
    {
        try
        {
            long result = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (result >= 0)
                return result;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
        }
        throw new InvalidOperationException($"Graph 列 '{column}' 必须是非负整数。");
    }

    private static int ToPositiveInt32(object? value, string column)
    {
        long result = ToPositiveInt64(value, column);
        if (result <= int.MaxValue)
            return (int)result;
        throw new InvalidOperationException($"图 INSERT 列 '{column}' 必须是 Int32 范围内的正整数。");
    }

    private static int ToPositiveInt32(string value, string column)
        => ToPositiveInt32((object)value, column);

    private static GraphCursorOptions OptionsForSql()
        => new() { PageSize = 256, MaxResults = 100_000 };
}
