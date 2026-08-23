using SonnetDB.Graphs.Storage;
using SonnetDB.Kv;

namespace SonnetDB.Graphs;

/// <summary>
/// 单个 Graph 稳定读快照上的点读、索引 seek 和邻接扩展入口。
/// </summary>
/// <remarks>
/// 会话提供 statement snapshot：点读、会话创建前后打开的游标以及长路径遍历共享同一
/// <see cref="Sequence"/>。并发提交不会改变当前会话的可见结果，也不会被当前会话阻塞。
/// 这不是跨多个读会话或读写语句的 snapshot-isolation transaction。
/// </remarks>
public sealed class GraphReadSession : IDisposable
{
    private readonly object _sync = new();
    private readonly GraphStore _store;
    private KvReadSnapshot? _snapshot;

    internal GraphReadSession(GraphStore store, KvReadSnapshot snapshot)
    {
        _store = store;
        _snapshot = snapshot;
        Sequence = snapshot.Sequence;
    }

    /// <summary>创建快照时图的 KV 单调序列号。</summary>
    public long Sequence { get; }

    /// <summary>按标识符读取顶点。</summary>
    /// <param name="id">顶点标识符。</param>
    /// <returns>存在时返回顶点快照，否则返回 null。</returns>
    public GraphVertex? GetVertex(GraphElementId id)
    {
        ValidateElementId(id, nameof(id));
        KvReadSnapshot snapshot = Snapshot;
        KvEntry? entry = snapshot.GetEntry(GraphKeyCodec.EncodeVertexRecord(id));
        return entry is null ? null : DecodeVertex(entry.Value.Span, id);
    }

    /// <summary>按标识符读取边。</summary>
    /// <param name="id">边标识符。</param>
    /// <returns>存在时返回边快照，否则返回 null。</returns>
    public GraphEdge? GetEdge(GraphElementId id)
    {
        ValidateElementId(id, nameof(id));
        KvReadSnapshot snapshot = Snapshot;
        KvEntry? entry = snapshot.GetEntry(GraphKeyCodec.EncodeEdgeRecord(id));
        return entry is null ? null : DecodeEdge(entry.Value.Span, id);
    }

    /// <summary>按标识符批量读取顶点；结果保持输入顺序并跳过不存在的元素。</summary>
    /// <param name="ids">顶点标识符序列，最多 10,000 个。</param>
    /// <returns>找到的顶点快照。</returns>
    public IReadOnlyList<GraphVertex> GetVertices(IEnumerable<GraphElementId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var result = new List<GraphVertex>();
        int count = 0;
        foreach (GraphElementId id in ids)
        {
            if (++count > 10_000)
                throw new ArgumentOutOfRangeException(nameof(ids), "一次批量读取不能超过 10,000 个顶点。");
            GraphVertex? vertex = GetVertex(id);
            if (vertex is not null)
                result.Add(vertex);
        }
        return result;
    }

    internal IReadOnlyList<int> GetOwnedUniquePropertyIds(GraphVertex vertex)
    {
        ArgumentNullException.ThrowIfNull(vertex);
        return GetOwnedUniquePropertyIds(
            GraphElementKind.Vertex,
            vertex.Id,
            vertex.Labels,
            vertex.Properties);
    }

    internal IReadOnlyList<int> GetOwnedUniquePropertyIds(GraphEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return GetOwnedUniquePropertyIds(
            GraphElementKind.Edge,
            edge.Id,
            [edge.LabelId],
            edge.Properties);
    }

    private IReadOnlyList<int> GetOwnedUniquePropertyIds(
        GraphElementKind kind,
        GraphElementId ownerId,
        IReadOnlyList<LabelId> labels,
        IReadOnlyList<GraphProperty> properties)
    {
        KvReadSnapshot snapshot = Snapshot;
        var result = new List<int>();
        foreach (GraphProperty property in properties)
        {
            bool owned = false;
            foreach (LabelId label in labels)
            {
                KvEntry? entry = snapshot.GetEntry(GraphKeyCodec.EncodeUniqueProperty(
                    kind,
                    label,
                    property.PropertyId,
                    property.Value));
                if (entry is not null
                    && GraphUniquePropertyOwnerCodec.Decode(entry.Value.Span, kind) == ownerId)
                {
                    owned = true;
                    break;
                }
            }
            if (owned)
                result.Add(property.PropertyId);
        }
        return result;
    }

    /// <summary>按稳定内部 ID 顺序扫描全部顶点。</summary>
    /// <param name="options">读取分页和结果预算。</param>
    /// <returns>顶点结果游标。</returns>
    public GraphCursor<GraphVertex> ScanVertices(GraphCursorOptions? options = null)
        => GraphPlanExecutor.Execute(this, new GraphNodeScanPlan(Options: options));

    internal GraphCursor<GraphVertex> ScanVerticesCore(GraphCursorOptions? options)
        => OpenElementCursor(
            GraphKeyCodec.VertexRecordPrefix(),
            isVertex: true,
            options,
            GraphKeyKind.VertexRecord);

    /// <summary>
    /// 按 label/property 索引 seek 顶点。
    /// </summary>
    /// <param name="labelId">标签标识符。</param>
    /// <param name="propertyId">属性标识符。</param>
    /// <param name="value">精确匹配的属性值。</param>
    /// <param name="options">读取预算。</param>
    /// <returns>按稳定 key 顺序返回的顶点游标。</returns>
    public GraphCursor<GraphVertex> SeekVertices(
        LabelId labelId,
        int propertyId,
        GraphPropertyValue value,
        GraphCursorOptions? options = null)
        => GraphPlanExecutor.Execute(this, new GraphNodeScanPlan(labelId, propertyId, value, options));

    internal GraphCursor<GraphVertex> SeekVerticesCore(
        LabelId labelId,
        int propertyId,
        GraphPropertyValue value,
        GraphCursorOptions? options)
    {
        ValidateLabelId(labelId, nameof(labelId));
        return OpenElementCursor(
            GraphKeyCodec.PropertyIndexPrefix(GraphElementKind.Vertex, labelId, propertyId, value),
            isVertex: true,
            options,
            GraphKeyKind.VertexPropertyIndex);
    }

    /// <summary>按 label 索引读取顶点。</summary>
    /// <param name="labelId">标签标识符。</param>
    /// <param name="options">读取预算。</param>
    /// <returns>顶点游标。</returns>
    public GraphCursor<GraphVertex> SeekVerticesByLabel(
        LabelId labelId,
        GraphCursorOptions? options = null)
        => GraphPlanExecutor.Execute(this, new GraphNodeScanPlan(LabelId: labelId, Options: options));

    internal GraphCursor<GraphVertex> SeekVerticesByLabelCore(
        LabelId labelId,
        GraphCursorOptions? options)
    {
        ValidateLabelId(labelId, nameof(labelId));
        return OpenElementCursor(
            GraphKeyCodec.LabelPrefix(GraphElementKind.Vertex, labelId),
            isVertex: true,
            options,
            GraphKeyKind.VertexLabel);
    }

    /// <summary>按稳定内部 ID 顺序扫描全部边。</summary>
    /// <param name="options">读取分页和结果预算。</param>
    /// <returns>边结果游标。</returns>
    public GraphCursor<GraphEdge> ScanEdges(GraphCursorOptions? options = null)
        => GraphPlanExecutor.Execute(this, new GraphEdgeScanPlan(Options: options));

    internal GraphCursor<GraphEdge> ScanEdgesCore(GraphCursorOptions? options)
        => OpenEdgeCursor(
            GraphKeyCodec.EdgeRecordPrefix(),
            options,
            GraphKeyKind.EdgeRecord);

    /// <summary>按 label/property 索引 seek 边。</summary>
    /// <param name="labelId">边标签标识符。</param>
    /// <param name="propertyId">属性标识符。</param>
    /// <param name="value">精确匹配的属性值。</param>
    /// <param name="options">读取预算。</param>
    /// <returns>边游标。</returns>
    public GraphCursor<GraphEdge> SeekEdges(
        LabelId labelId,
        int propertyId,
        GraphPropertyValue value,
        GraphCursorOptions? options = null)
        => GraphPlanExecutor.Execute(this, new GraphEdgeScanPlan(labelId, propertyId, value, options));

    internal GraphCursor<GraphEdge> SeekEdgesCore(
        LabelId labelId,
        int propertyId,
        GraphPropertyValue value,
        GraphCursorOptions? options)
    {
        ValidateLabelId(labelId, nameof(labelId));
        return OpenEdgeCursor(
            GraphKeyCodec.PropertyIndexPrefix(GraphElementKind.Edge, labelId, propertyId, value),
            options,
            GraphKeyKind.EdgePropertyIndex);
    }

    /// <summary>按 label 索引读取边。</summary>
    /// <param name="labelId">边标签标识符。</param>
    /// <param name="options">读取预算。</param>
    /// <returns>边游标。</returns>
    public GraphCursor<GraphEdge> SeekEdgesByLabel(
        LabelId labelId,
        GraphCursorOptions? options = null)
        => GraphPlanExecutor.Execute(this, new GraphEdgeScanPlan(LabelId: labelId, Options: options));

    internal GraphCursor<GraphEdge> SeekEdgesByLabelCore(
        LabelId labelId,
        GraphCursorOptions? options)
    {
        ValidateLabelId(labelId, nameof(labelId));
        return OpenEdgeCursor(
            GraphKeyCodec.LabelPrefix(GraphElementKind.Edge, labelId),
            options,
            GraphKeyKind.EdgeLabel);
    }

    /// <summary>从一个顶点流式扩展出边、入边或双向边。</summary>
    /// <param name="vertexId">扩展锚点。</param>
    /// <param name="direction">扩展方向。</param>
    /// <param name="edgeLabelId">可选边标签过滤。</param>
    /// <param name="options">读取预算。</param>
    /// <returns>邻接命中游标。</returns>
    public GraphCursor<GraphExpansion> Expand(
        GraphElementId vertexId,
        GraphDirection direction = GraphDirection.Outgoing,
        LabelId? edgeLabelId = null,
        GraphCursorOptions? options = null)
        => GraphPlanExecutor.Execute(this, new GraphExpandPlan(vertexId, direction, edgeLabelId, options));

    /// <summary>从一个顶点流式扩展，并按目标顶点 label/property 谓词过滤。</summary>
    /// <param name="vertexId">扩展锚点。</param>
    /// <param name="targetPredicate">目标顶点精确匹配谓词。</param>
    /// <param name="direction">扩展方向。</param>
    /// <param name="edgeLabelId">可选边标签过滤。</param>
    /// <param name="options">读取预算。</param>
    /// <returns>仅包含目标顶点匹配项的邻接命中游标。</returns>
    public GraphCursor<GraphExpansion> Expand(
        GraphElementId vertexId,
        GraphVertexPredicate targetPredicate,
        GraphDirection direction = GraphDirection.Outgoing,
        LabelId? edgeLabelId = null,
        GraphCursorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(targetPredicate);
        return GraphPlanExecutor.Execute(
            this,
            new GraphExpandPlan(vertexId, direction, edgeLabelId, options)
            {
                TargetPredicate = targetPredicate,
            });
    }

    internal GraphCursor<GraphExpansion> ExpandCore(
        GraphElementId vertexId,
        GraphDirection direction,
        LabelId? edgeLabelId,
        GraphVertexPredicate? targetPredicate,
        GraphCursorOptions? options)
    {
        ValidateElementId(vertexId, nameof(vertexId));
        if (edgeLabelId is { } label)
            ValidateLabelId(label, nameof(edgeLabelId));
        GraphCursorOptions cursorOptions = options ?? new GraphCursorOptions();
        cursorOptions.Validate();
        KvReadSnapshot snapshot = AcquireCursorSnapshot();
        try
        {
            return new GraphCursor<GraphExpansion>(
                new GraphExpansionCursorSource(
                    snapshot,
                    vertexId,
                    direction,
                    edgeLabelId,
                    targetPredicate,
                    cursorOptions),
                cursorOptions.MaxResults);
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    /// <summary>在当前稳定快照上重建内存统计。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>不包含原始属性值的统计结果。</returns>
    public GraphStatistics RefreshStatistics(CancellationToken cancellationToken = default)
    {
        GraphStatistics statistics = GraphStatisticsCalculator.Refresh(Snapshot, cancellationToken);
        _store.PublishStatistics(statistics);
        return statistics;
    }

    /// <summary>在当前稳定快照上按显式扫描和分组预算重建内存统计。</summary>
    /// <param name="options">页、扫描条目和统计分组预算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>不包含原始属性值的统计结果。</returns>
    public GraphStatistics RefreshStatistics(
        GraphStatisticsRefreshOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        GraphStatistics statistics = GraphStatisticsCalculator.Refresh(
            Snapshot,
            options,
            cancellationToken);
        _store.PublishStatistics(statistics);
        return statistics;
    }

    /// <summary>解释一次原生邻接扩展计划。</summary>
    /// <param name="vertexId">扩展锚点。</param>
    /// <param name="direction">扩展方向。</param>
    /// <param name="edgeLabelId">可选边标签。</param>
    /// <returns>明确标记 native adjacency 的基础计划。</returns>
    public GraphExplain ExplainExpand(
        GraphElementId vertexId,
        GraphDirection direction = GraphDirection.Outgoing,
        LabelId? edgeLabelId = null)
    {
        ValidateElementId(vertexId, nameof(vertexId));
        if (edgeLabelId is { } label)
            ValidateLabelId(label, nameof(edgeLabelId));
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        return new GraphExplain
        {
            Operation = edgeLabelId is null ? "Expand" : "Expand(label)",
            AccessPath = GraphAccessPath.NativeAdjacency,
            EstimatedRows = null,
            IsNative = true,
            SnapshotSequence = Sequence,
        };
    }

    /// <summary>解释一次顶点 label/property 索引 seek。</summary>
    /// <param name="labelId">标签标识符。</param>
    /// <param name="propertyId">属性标识符。</param>
    /// <param name="value">精确匹配值。</param>
    /// <returns>明确标记 native index seek 的基础计划。</returns>
    public GraphExplain ExplainVertexSeek(
        LabelId labelId,
        int propertyId,
        GraphPropertyValue value)
    {
        ValidateLabelId(labelId, nameof(labelId));
        _ = GraphKeyCodec.PropertyIndexPrefix(GraphElementKind.Vertex, labelId, propertyId, value);
        return new GraphExplain
        {
            Operation = "SeekVertices",
            AccessPath = GraphAccessPath.NativeIndexSeek,
            EstimatedRows = null,
            IsNative = true,
            SnapshotSequence = Sequence,
            EstimateSource = "statistics_missing",
        };
    }

    /// <summary>使用已刷新统计解释一次顶点 label/property 索引 seek。</summary>
    /// <param name="labelId">标签标识符。</param>
    /// <param name="propertyId">属性标识符。</param>
    /// <param name="value">精确匹配值。</param>
    /// <param name="statistics">可重建图统计。</param>
    /// <returns>带估计行数与统计新鲜度来源的原生索引计划。</returns>
    public GraphExplain ExplainVertexSeek(
        LabelId labelId,
        int propertyId,
        GraphPropertyValue value,
        GraphStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        ValidateLabelId(labelId, nameof(labelId));
        _ = GraphKeyCodec.PropertyIndexPrefix(GraphElementKind.Vertex, labelId, propertyId, value);
        bool isCurrent = statistics.Sequence == Sequence;
        return new GraphExplain
        {
            Operation = "SeekVertices",
            AccessPath = GraphAccessPath.NativeIndexSeek,
            EstimatedRows = statistics.EstimateSeekRows(
                GraphElementType.Vertex,
                labelId,
                propertyId,
                value),
            IsNative = true,
            SnapshotSequence = Sequence,
            EstimateSource = isCurrent ? "refreshed" : "stale",
            StatisticsSequence = statistics.Sequence,
        };
    }

    /// <summary>释放读快照。</summary>
    public void Dispose()
    {
        KvReadSnapshot? snapshot;
        lock (_sync)
        {
            snapshot = _snapshot;
            _snapshot = null;
        }
        snapshot?.Dispose();
    }

    private GraphCursor<GraphVertex> OpenElementCursor(
        byte[] prefix,
        bool isVertex,
        GraphCursorOptions? options,
        GraphKeyKind expectedKeyKind)
    {
        GraphCursorOptions cursorOptions = options ?? new GraphCursorOptions();
        cursorOptions.Validate();
        KvReadSnapshot snapshot = AcquireCursorSnapshot();
        try
        {
            KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
            {
                Prefix = prefix,
                PageSize = cursorOptions.PageSize,
                MaxPageBytes = cursorOptions.MaxPageBytes,
            });
            IGraphCursorSource<GraphVertex> source = new GraphMappedCursorSource<GraphVertex>(
                snapshot,
                cursor,
                entry =>
                {
                    GraphStorageKey key = GraphKeyCodec.Decode(entry.Key.Span);
                    if (key.Kind != expectedKeyKind)
                        throw new InvalidDataException("Graph vertex scan 返回了错误的 key family。");
                    return ReadVertex(snapshot, key.ElementId);
                });
            return new GraphCursor<GraphVertex>(source, cursorOptions.MaxResults);
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    private GraphCursor<GraphEdge> OpenEdgeCursor(
        byte[] prefix,
        GraphCursorOptions? options,
        GraphKeyKind expectedKeyKind)
    {
        GraphCursorOptions cursorOptions = options ?? new GraphCursorOptions();
        cursorOptions.Validate();
        KvReadSnapshot snapshot = AcquireCursorSnapshot();
        try
        {
            KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
            {
                Prefix = prefix,
                PageSize = cursorOptions.PageSize,
                MaxPageBytes = cursorOptions.MaxPageBytes,
            });
            IGraphCursorSource<GraphEdge> source = new GraphMappedCursorSource<GraphEdge>(
                snapshot,
                cursor,
                entry =>
                {
                    GraphStorageKey key = GraphKeyCodec.Decode(entry.Key.Span);
                    if (key.Kind != expectedKeyKind)
                        throw new InvalidDataException("Graph edge scan 返回了错误的 key family。");
                    return ReadEdge(snapshot, key.ElementId);
                });
            return new GraphCursor<GraphEdge>(source, cursorOptions.MaxResults);
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    private KvReadSnapshot AcquireCursorSnapshot()
    {
        lock (_sync)
        {
            KvReadSnapshot snapshot = _snapshot
                ?? throw new ObjectDisposedException(nameof(GraphReadSession));
            return snapshot.AcquireLease();
        }
    }

    internal KvReadSnapshot AcquireTraversalSnapshot() => AcquireCursorSnapshot();

    internal GraphCursor<GraphPath> OpenPathPlan(
        GraphPathPlan plan,
        GraphTraversalOptions options,
        GraphTraversalDiagnostics? diagnostics = null)
    {
        if (plan.StartId.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(plan));
        if (!Enum.IsDefined(plan.Direction) || !Enum.IsDefined(plan.Mode))
            throw new ArgumentOutOfRangeException(nameof(plan));
        if (plan.EdgeLabelId is { Value: <= 0 })
            throw new ArgumentOutOfRangeException(nameof(plan));
        ArgumentOutOfRangeException.ThrowIfNegative(plan.MinDepth);
        if (plan.MaxDepth < plan.MinDepth)
            throw new ArgumentOutOfRangeException(nameof(plan));
        options.Validate();
        if (options.MaxDepth < plan.MaxDepth)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDepth 不能小于计划的 MaxDepth。");

        KvReadSnapshot snapshot = AcquireTraversalSnapshot();
        try
        {
            return new GraphCursor<GraphPath>(
                new GraphTraversalCursorSource(
                    snapshot,
                    plan.StartId,
                    plan.Mode == GraphPathSearchMode.BreadthFirst
                        ? GraphTraversalMode.BreadthFirst
                        : GraphTraversalMode.DepthFirst,
                    plan.MinDepth,
                    plan.MaxDepth,
                    plan.Direction,
                    plan.EdgeLabelId,
                    options,
                    plan.DeduplicateBreadthFirstEndpoints,
                    diagnostics),
                options.MaxPaths);
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    private KvReadSnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return _snapshot ?? throw new ObjectDisposedException(nameof(GraphReadSession));
        }
    }

    private static void ValidateElementId(GraphElementId id, string parameterName)
    {
        if (id.Value <= 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void ValidateLabelId(LabelId id, string parameterName)
    {
        if (id.Value <= 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    internal static GraphVertex ReadVertex(KvReadSnapshot snapshot, GraphElementId id)
    {
        KvEntry? entry = snapshot.GetEntry(GraphKeyCodec.EncodeVertexRecord(id));
        return entry is null
            ? throw new InvalidDataException($"Graph adjacency 引用了不存在的 vertex {id}。")
            : DecodeVertex(entry.Value.Span, id);
    }

    internal static GraphEdge ReadEdge(KvReadSnapshot snapshot, GraphElementId id)
    {
        KvEntry? entry = snapshot.GetEntry(GraphKeyCodec.EncodeEdgeRecord(id));
        return entry is null
            ? throw new InvalidDataException($"Graph adjacency 引用了不存在的 edge {id}。")
            : DecodeEdge(entry.Value.Span, id);
    }

    private static GraphVertex DecodeVertex(ReadOnlySpan<byte> encoded, GraphElementId expectedId)
    {
        Storage.GraphVertexRecord record = Storage.GraphElementRecordCodec.DecodeVertex(encoded);
        if (record.Id != expectedId)
            throw new InvalidDataException("Graph vertex key 与 payload ID 不一致。");
        return new GraphVertex(
            record.Id,
            record.ElementVersion,
            record.Labels.ToArray(),
            record.Properties.ToArray());
    }

    private static GraphEdge DecodeEdge(ReadOnlySpan<byte> encoded, GraphElementId expectedId)
    {
        Storage.GraphEdgeRecord record = Storage.GraphElementRecordCodec.DecodeEdge(encoded);
        if (record.Id != expectedId)
            throw new InvalidDataException("Graph edge key 与 payload ID 不一致。");
        return new GraphEdge(
            record.Id,
            record.ElementVersion,
            record.SourceId,
            record.TargetId,
            record.LabelId,
            record.Properties.ToArray());
    }
}

internal sealed class GraphMappedCursorSource<T> : IGraphCursorSource<T> where T : class
{
    private readonly KvReadSnapshot _snapshot;
    private readonly KvRangeCursor _cursor;
    private readonly Func<KvEntry, T?> _project;
    private bool _disposed;

    internal GraphMappedCursorSource(
        KvReadSnapshot snapshot,
        KvRangeCursor cursor,
        Func<KvEntry, T?> project)
    {
        _snapshot = snapshot;
        _cursor = cursor;
        _project = project;
        SnapshotSequence = cursor.SnapshotSequence;
    }

    public long SnapshotSequence { get; }

    public bool IsExhausted => _cursor.IsExhausted;

    public IReadOnlyList<T> ReadNextPage(CancellationToken cancellationToken)
    {
        while (!_cursor.IsExhausted)
        {
            IReadOnlyList<KvEntry> entries = _cursor.ReadNextPage(cancellationToken);
            if (entries.Count == 0)
                break;
            var result = new List<T>(entries.Count);
            foreach (KvEntry entry in entries)
            {
                T? value = _project(entry);
                if (value is not null)
                    result.Add(value);
            }
            if (result.Count > 0)
                return result;
        }
        return Array.Empty<T>();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cursor.Dispose();
        _snapshot.Dispose();
    }
}
