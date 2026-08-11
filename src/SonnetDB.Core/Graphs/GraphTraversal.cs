using SonnetDB.Kv;

namespace SonnetDB.Graphs;

/// <summary>路径元素去重策略。</summary>
public enum GraphPathUniqueness : byte
{
    /// <summary>允许重复顶点和边；最大深度仍然生效。</summary>
    None = 0,

    /// <summary>同一路径中顶点不能重复。</summary>
    Vertex = 1,

    /// <summary>同一路径中边不能重复。</summary>
    Edge = 2,
}

/// <summary>图遍历的有界执行参数。</summary>
public sealed record GraphTraversalOptions
{
    /// <summary>允许的最大 hop 深度。</summary>
    public int MaxDepth { get; init; } = 6;

    /// <summary>允许的最大 frontier 条目数。</summary>
    public int MaxFrontier { get; init; } = 10_000;

    /// <summary>允许生成的最大路径数。</summary>
    public int MaxPaths { get; init; } = 10_000;

    /// <summary>路径内元素去重策略。</summary>
    public GraphPathUniqueness PathUniqueness { get; init; } = GraphPathUniqueness.Vertex;

    /// <summary>遍历结果游标页大小。</summary>
    public int PageSize { get; init; } = 128;

    /// <summary>邻接读取的每页 payload 字节预算。</summary>
    public int MaxPageBytes { get; init; } = 32 * 1024 * 1024;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxFrontier);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPaths);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPageBytes);
        if (!Enum.IsDefined(PathUniqueness))
            throw new ArgumentOutOfRangeException(nameof(PathUniqueness));
    }
}

/// <summary>路径预算耗尽时抛出的异常。</summary>
public sealed class GraphTraversalLimitExceededException : InvalidOperationException
{
    internal GraphTraversalLimitExceededException(string message) : base(message) { }
}

/// <summary>图中的一条不可变路径。</summary>
public sealed class GraphPath
{
    /// <summary>创建一条不可变路径。</summary>
    /// <param name="vertexIds">按顺序排列的顶点，数量必须比边多一。</param>
    /// <param name="edgeIds">按顺序排列的边。</param>
    public GraphPath(
        IReadOnlyList<GraphElementId> vertexIds,
        IReadOnlyList<GraphElementId> edgeIds)
    {
        ArgumentNullException.ThrowIfNull(vertexIds);
        ArgumentNullException.ThrowIfNull(edgeIds);
        if (vertexIds.Count == 0 || vertexIds.Count != edgeIds.Count + 1)
            throw new ArgumentException("Graph path 的顶点数必须比边数多一。", nameof(vertexIds));
        if (vertexIds.Any(static id => id.Value <= 0))
            throw new ArgumentException("Graph path 不能包含默认 vertex ID。", nameof(vertexIds));
        if (edgeIds.Any(static id => id.Value <= 0))
            throw new ArgumentException("Graph path 不能包含默认 edge ID。", nameof(edgeIds));
        VertexIds = vertexIds.ToArray();
        EdgeIds = edgeIds.ToArray();
    }

    /// <summary>按路径顺序排列的顶点，包括起点。</summary>
    public IReadOnlyList<GraphElementId> VertexIds { get; }

    /// <summary>按路径顺序排列的边。</summary>
    public IReadOnlyList<GraphElementId> EdgeIds { get; }

    /// <summary>路径 hop 数。</summary>
    public int Depth => EdgeIds.Count;

    /// <summary>路径是否为空路径（只有起点）。</summary>
    public bool IsZeroLength => EdgeIds.Count == 0;
}

/// <summary>GraphReadSession 的遍历扩展方法。</summary>
public static class GraphTraversalExtensions
{
    /// <summary>从起点按广度优先顺序流式返回发现路径。</summary>
    /// <param name="session">稳定 Graph 读会话。</param>
    /// <param name="startId">起点。</param>
    /// <param name="direction">扩展方向。</param>
    /// <param name="edgeLabelId">可选边标签。</param>
    /// <param name="options">深度、frontier、路径和分页预算。</param>
    /// <returns>广度优先路径游标。</returns>
    public static GraphCursor<GraphPath> Bfs(
        this GraphReadSession session,
        GraphElementId startId,
        GraphDirection direction = GraphDirection.Outgoing,
        LabelId? edgeLabelId = null,
        GraphTraversalOptions? options = null)
        => OpenTraversal(session, startId, GraphTraversalMode.BreadthFirst, 0, null, direction, edgeLabelId, options);

    /// <summary>从起点按深度优先顺序流式返回发现路径。</summary>
    /// <param name="session">稳定 Graph 读会话。</param>
    /// <param name="startId">起点。</param>
    /// <param name="direction">扩展方向。</param>
    /// <param name="edgeLabelId">可选边标签。</param>
    /// <param name="options">深度、frontier、路径和分页预算。</param>
    /// <returns>深度优先路径游标。</returns>
    public static GraphCursor<GraphPath> Dfs(
        this GraphReadSession session,
        GraphElementId startId,
        GraphDirection direction = GraphDirection.Outgoing,
        LabelId? edgeLabelId = null,
        GraphTraversalOptions? options = null)
        => OpenTraversal(session, startId, GraphTraversalMode.DepthFirst, 0, null, direction, edgeLabelId, options);

    /// <summary>流式枚举固定或受限可变长度路径。</summary>
    /// <param name="session">稳定 Graph 读会话。</param>
    /// <param name="startId">路径起点。</param>
    /// <param name="minDepth">最小 hop 数。</param>
    /// <param name="maxDepth">最大 hop 数。</param>
    /// <param name="direction">扩展方向。</param>
    /// <param name="edgeLabelId">可选边标签。</param>
    /// <param name="options">frontier、路径和分页预算；其 MaxDepth 必须不小于 maxDepth。</param>
    /// <returns>路径游标。</returns>
    public static GraphCursor<GraphPath> Paths(
        this GraphReadSession session,
        GraphElementId startId,
        int minDepth,
        int maxDepth,
        GraphDirection direction = GraphDirection.Outgoing,
        LabelId? edgeLabelId = null,
        GraphTraversalOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minDepth);
        if (maxDepth < minDepth)
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        GraphTraversalOptions traversalOptions = options ?? new GraphTraversalOptions();
        if (traversalOptions.MaxDepth < maxDepth)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDepth 不能小于 maxDepth。");
        return OpenTraversal(
            session,
            startId,
            GraphTraversalMode.DepthFirst,
            minDepth,
            maxDepth,
            direction,
            edgeLabelId,
            traversalOptions);
    }

    /// <summary>使用无权广度优先搜索取得起点到目标的第一条最短路径。</summary>
    /// <param name="session">稳定 Graph 读会话。</param>
    /// <param name="startId">起点。</param>
    /// <param name="targetId">目标。</param>
    /// <param name="direction">扩展方向。</param>
    /// <param name="edgeLabelId">可选边标签。</param>
    /// <param name="options">最大深度、frontier 和取消预算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到时返回最短路径，否则返回 null。</returns>
    public static GraphPath? ShortestPath(
        this GraphReadSession session,
        GraphElementId startId,
        GraphElementId targetId,
        GraphDirection direction = GraphDirection.Outgoing,
        LabelId? edgeLabelId = null,
        GraphTraversalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateElementId(startId, nameof(startId));
        ValidateElementId(targetId, nameof(targetId));
        GraphTraversalOptions traversalOptions = options ?? new GraphTraversalOptions();
        traversalOptions.Validate();
        if (startId == targetId)
            return new GraphPath([startId], []);

        using GraphCursor<GraphPath> cursor = session.Bfs(
            startId,
            direction,
            edgeLabelId,
            traversalOptions with { MaxPaths = Math.Min(traversalOptions.MaxPaths, traversalOptions.MaxFrontier) });
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<GraphPath> page = cursor.ReadNextPage(cancellationToken);
            if (page.Count == 0)
                return null;
            foreach (GraphPath path in page)
            {
                if (path.VertexIds[^1] == targetId)
                    return path;
            }
        }
    }

    private static GraphCursor<GraphPath> OpenTraversal(
        GraphReadSession session,
        GraphElementId startId,
        GraphTraversalMode mode,
        int minDepth,
        int? maxDepth,
        GraphDirection direction,
        LabelId? edgeLabelId,
        GraphTraversalOptions? options)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateElementId(startId, nameof(startId));
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (edgeLabelId is { Value: <= 0 })
            throw new ArgumentOutOfRangeException(nameof(edgeLabelId));
        GraphTraversalOptions traversalOptions = options ?? new GraphTraversalOptions();
        traversalOptions.Validate();
        if (maxDepth is not null && traversalOptions.MaxDepth < maxDepth.Value)
            throw new ArgumentOutOfRangeException(nameof(options));
        return session.OpenPathPlan(
            new GraphPathPlan(
                startId,
                mode == GraphTraversalMode.BreadthFirst
                    ? GraphPathSearchMode.BreadthFirst
                    : GraphPathSearchMode.DepthFirst,
                minDepth,
                maxDepth ?? traversalOptions.MaxDepth,
                direction,
                edgeLabelId,
                traversalOptions),
            traversalOptions);
    }

    private static void ValidateElementId(GraphElementId id, string parameterName)
    {
        if (id.Value <= 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }

}

internal enum GraphTraversalMode : byte
{
    BreadthFirst = 1,
    DepthFirst = 2,
}
