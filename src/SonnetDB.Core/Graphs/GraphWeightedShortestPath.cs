namespace SonnetDB.Graphs;

/// <summary>加权最短路径使用的搜索算法。</summary>
public enum GraphWeightedShortestPathAlgorithm : byte
{
    /// <summary>使用非负边权的 Dijkstra 搜索。</summary>
    Dijkstra = 1,

    /// <summary>使用调用方提供启发式函数的 A* 搜索。</summary>
    AStar = 2,

    /// <summary>从起点和终点同时进行的双向 Dijkstra 搜索。</summary>
    BidirectionalDijkstra = 3,
}

/// <summary>加权图路径的执行参数和资源预算。</summary>
public sealed record GraphWeightedShortestPathOptions
{
    /// <summary>从边属性读取权重时使用的属性标识符。</summary>
    public int WeightPropertyId { get; init; }

    /// <summary>
    /// 从边计算权重的函数。设置此项后不能同时设置 <see cref="WeightPropertyId"/>。
    /// 函数必须返回有限且非负的数值。
    /// </summary>
    public Func<GraphEdge, double>? WeightSelector { get; init; }

    /// <summary>
    /// A* 的启发式函数；省略时 A* 等价于以零启发式运行的 Dijkstra。
    /// 为保持最短路径正确性，调用方必须提供可采纳且一致的启发式；Core 只校验数值边界。
    /// </summary>
    public Func<GraphElementId, double>? Heuristic { get; init; }

    /// <summary>搜索算法。</summary>
    public GraphWeightedShortestPathAlgorithm Algorithm { get; init; } = GraphWeightedShortestPathAlgorithm.Dijkstra;

    /// <summary>扩展方向。</summary>
    public GraphDirection Direction { get; init; } = GraphDirection.Outgoing;

    /// <summary>可选边标签过滤。</summary>
    public LabelId? EdgeLabelId { get; init; }

    /// <summary>允许的最大 hop 数。</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>允许的 frontier 条目总数。</summary>
    public int MaxFrontier { get; init; } = 10_000;

    /// <summary>允许访问的不同顶点总数。</summary>
    public int MaxVisitedVertices { get; init; } = 1_000_000;

    /// <summary>允许检查的邻接边总数。</summary>
    public long MaxExpandedEdges { get; init; } = 10_000_000;

    /// <summary>可选的路径总权重上限；超过上限的候选路径会被跳过。</summary>
    public double MaxTotalWeight { get; init; } = double.PositiveInfinity;

    /// <summary>邻接游标页大小。</summary>
    public int PageSize { get; init; } = 256;

    /// <summary>邻接游标页的 payload 字节上限。</summary>
    public int MaxPageBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>按边属性创建加权路径选项。</summary>
    /// <param name="propertyId">保存非负 Int64 或 Float64 权重的边属性标识符。</param>
    /// <returns>已设置权重属性的选项。</returns>
    public static GraphWeightedShortestPathOptions ForProperty(int propertyId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(propertyId);
        return new GraphWeightedShortestPathOptions { WeightPropertyId = propertyId };
    }

    internal void Validate()
    {
        if (WeightPropertyId <= 0 && WeightSelector is null)
            throw new ArgumentException("必须设置 WeightPropertyId 或 WeightSelector。", nameof(WeightPropertyId));
        if (WeightPropertyId > 0 && WeightSelector is not null)
            throw new ArgumentException("WeightPropertyId 与 WeightSelector 不能同时设置。", nameof(WeightSelector));
        if (!Enum.IsDefined(Algorithm))
            throw new ArgumentOutOfRangeException(nameof(Algorithm));
        if (!Enum.IsDefined(Direction))
            throw new ArgumentOutOfRangeException(nameof(Direction));
        if (EdgeLabelId is { Value: <= 0 })
            throw new ArgumentOutOfRangeException(nameof(EdgeLabelId));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxFrontier);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxVisitedVertices);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxExpandedEdges);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPageBytes);
        if (double.IsNaN(MaxTotalWeight) || MaxTotalWeight < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxTotalWeight));
    }

    internal double ReadWeight(GraphEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        double weight;
        if (WeightSelector is not null)
        {
            weight = WeightSelector(edge);
        }
        else
        {
            bool found = false;
            weight = 0;
            foreach (GraphProperty property in edge.Properties)
            {
                if (property.PropertyId != WeightPropertyId)
                    continue;
                found = true;
                weight = property.Value.Kind switch
                {
                    GraphPropertyKind.Int64 => property.Value.AsInt64(),
                    GraphPropertyKind.Float64 => property.Value.AsFloat64(),
                    _ => throw new GraphWeightTypeException(
                        $"边 {edge.Id} 的权重属性 {WeightPropertyId} 必须是 Int64 或 Float64。"),
                };
                break;
            }

            if (!found)
                throw new GraphMissingWeightException(
                    $"边 {edge.Id} 缺少权重属性 {WeightPropertyId}。");
        }

        if (double.IsNaN(weight) || double.IsInfinity(weight))
            throw new GraphWeightOverflowException($"边 {edge.Id} 的权重必须是有限数值。");
        if (weight < 0)
            throw new GraphNegativeWeightException($"边 {edge.Id} 的权重不能为负数。");
        return weight;
    }
}

/// <summary>带总权重的不可变图路径。</summary>
public sealed class GraphWeightedPath
{
    /// <summary>创建加权路径结果。</summary>
    /// <param name="path">路径顶点和边。</param>
    /// <param name="totalWeight">路径总权重。</param>
    /// <param name="algorithm">实际使用的算法。</param>
    /// <param name="expandedVertices">从 frontier 取出的顶点数。</param>
    /// <param name="expandedEdges">检查过的邻接边数。</param>
    /// <param name="snapshotSequence">查询使用的 Graph statement snapshot sequence。</param>
    public GraphWeightedPath(
        GraphPath path,
        double totalWeight,
        GraphWeightedShortestPathAlgorithm algorithm,
        int expandedVertices,
        long expandedEdges,
        long snapshotSequence = 0)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (double.IsNaN(totalWeight) || double.IsInfinity(totalWeight) || totalWeight < 0)
            throw new ArgumentOutOfRangeException(nameof(totalWeight));
        ArgumentOutOfRangeException.ThrowIfNegative(expandedVertices);
        ArgumentOutOfRangeException.ThrowIfNegative(expandedEdges);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotSequence);
        if (!Enum.IsDefined(algorithm))
            throw new ArgumentOutOfRangeException(nameof(algorithm));
        Path = path;
        TotalWeight = totalWeight;
        Algorithm = algorithm;
        ExpandedVertices = expandedVertices;
        ExpandedEdges = expandedEdges;
        SnapshotSequence = snapshotSequence;
    }

    /// <summary>路径本身。</summary>
    public GraphPath Path { get; }

    /// <summary>路径总权重。</summary>
    public double TotalWeight { get; }

    /// <summary>TotalWeight 的简短别名。</summary>
    public double Cost => TotalWeight;

    /// <summary>实际执行算法。</summary>
    public GraphWeightedShortestPathAlgorithm Algorithm { get; }

    /// <summary>搜索期间从 frontier 取出的顶点数。</summary>
    public int ExpandedVertices { get; }

    /// <summary>搜索期间检查过的邻接边数。</summary>
    public long ExpandedEdges { get; }

    /// <summary>查询使用的 Graph statement snapshot sequence。</summary>
    public long SnapshotSequence { get; }

    /// <summary>按路径顺序排列的顶点。</summary>
    public IReadOnlyList<GraphElementId> VertexIds => Path.VertexIds;

    /// <summary>按路径顺序排列的边。</summary>
    public IReadOnlyList<GraphElementId> EdgeIds => Path.EdgeIds;

    /// <summary>路径 hop 数。</summary>
    public int Depth => Path.Depth;
}

/// <summary>加权路径的通用执行预算异常。</summary>
public sealed class GraphWeightedPathLimitExceededException : InvalidOperationException
{
    internal GraphWeightedPathLimitExceededException(string message) : base(message) { }
}

/// <summary>图中出现负边权时抛出的异常。</summary>
public sealed class GraphNegativeWeightException : InvalidOperationException
{
    internal GraphNegativeWeightException(string message) : base(message) { }
}

/// <summary>图边权重类型不受支持时抛出的异常。</summary>
public sealed class GraphWeightTypeException : InvalidOperationException
{
    internal GraphWeightTypeException(string message) : base(message) { }
}

/// <summary>图边权重缺失时抛出的异常。</summary>
public sealed class GraphMissingWeightException : InvalidOperationException
{
    internal GraphMissingWeightException(string message) : base(message) { }
}

/// <summary>图边权重或路径累加发生非有限数值时抛出的异常。</summary>
public sealed class GraphWeightOverflowException : InvalidOperationException
{
    internal GraphWeightOverflowException(string message) : base(message) { }
}

/// <summary>GraphReadSession 的加权最短路径扩展。</summary>
public static class GraphWeightedShortestPathExtensions
{
    /// <summary>在稳定图快照上执行加权最短路径搜索。</summary>
    /// <param name="session">稳定图读会话。</param>
    /// <param name="startId">起点。</param>
    /// <param name="targetId">目标。</param>
    /// <param name="options">权重、算法和资源预算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到时返回加权路径，否则返回 null。</returns>
    public static GraphWeightedPath? WeightedShortestPath(
        this GraphReadSession session,
        GraphElementId startId,
        GraphElementId targetId,
        GraphWeightedShortestPathOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ValidateElementId(startId, nameof(startId));
        ValidateElementId(targetId, nameof(targetId));
        if (session.GetVertex(startId) is null || session.GetVertex(targetId) is null)
            return null;
        if (startId == targetId)
            return new GraphWeightedPath(
                new GraphPath([startId], []),
                0,
                options.Algorithm,
                0,
                0,
                session.Sequence);

        return options.Algorithm switch
        {
            GraphWeightedShortestPathAlgorithm.Dijkstra =>
                FindOneWay(session, startId, targetId, options, useHeuristic: false, cancellationToken),
            GraphWeightedShortestPathAlgorithm.AStar =>
                FindOneWay(session, startId, targetId, options, useHeuristic: true, cancellationToken),
            GraphWeightedShortestPathAlgorithm.BidirectionalDijkstra =>
                FindBidirectional(session, startId, targetId, options, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

    /// <summary>使用边属性执行 Dijkstra；这是 WeightedShortestPath 的便捷入口。</summary>
    /// <param name="session">稳定图读会话。</param>
    /// <param name="startId">起点。</param>
    /// <param name="targetId">目标。</param>
    /// <param name="weightPropertyId">边权重属性标识符。</param>
    /// <param name="direction">扩展方向。</param>
    /// <param name="edgeLabelId">可选边标签过滤。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到时返回加权路径，否则返回 null。</returns>
    public static GraphWeightedPath? Dijkstra(
        this GraphReadSession session,
        GraphElementId startId,
        GraphElementId targetId,
        int weightPropertyId,
        GraphDirection direction = GraphDirection.Outgoing,
        LabelId? edgeLabelId = null,
        CancellationToken cancellationToken = default)
        => session.WeightedShortestPath(
            startId,
            targetId,
            GraphWeightedShortestPathOptions.ForProperty(weightPropertyId) with
            {
                Direction = direction,
                EdgeLabelId = edgeLabelId,
            },
            cancellationToken);

    /// <summary>WeightedShortestPath 的语义别名，便于与现有 ShortestPath 命名并列使用。</summary>
    /// <param name="session">稳定图读会话。</param>
    /// <param name="startId">起点。</param>
    /// <param name="targetId">目标。</param>
    /// <param name="options">权重、算法和资源预算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到时返回加权路径，否则返回 null。</returns>
    public static GraphWeightedPath? ShortestPathWeighted(
        this GraphReadSession session,
        GraphElementId startId,
        GraphElementId targetId,
        GraphWeightedShortestPathOptions options,
        CancellationToken cancellationToken = default)
        => session.WeightedShortestPath(startId, targetId, options, cancellationToken);

    /// <summary>使用边属性执行 A* 搜索；启发式省略时等价于 Dijkstra。</summary>
    /// <param name="session">稳定图读会话。</param>
    /// <param name="startId">起点。</param>
    /// <param name="targetId">目标。</param>
    /// <param name="weightPropertyId">边权重属性标识符。</param>
    /// <param name="heuristic">可选的非负启发式函数。</param>
    /// <param name="direction">扩展方向。</param>
    /// <param name="edgeLabelId">可选边标签过滤。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到时返回加权路径，否则返回 null。</returns>
    public static GraphWeightedPath? AStar(
        this GraphReadSession session,
        GraphElementId startId,
        GraphElementId targetId,
        int weightPropertyId,
        Func<GraphElementId, double>? heuristic = null,
        GraphDirection direction = GraphDirection.Outgoing,
        LabelId? edgeLabelId = null,
        CancellationToken cancellationToken = default)
        => session.WeightedShortestPath(
            startId,
            targetId,
            GraphWeightedShortestPathOptions.ForProperty(weightPropertyId) with
            {
                Algorithm = GraphWeightedShortestPathAlgorithm.AStar,
                Heuristic = heuristic,
                Direction = direction,
                EdgeLabelId = edgeLabelId,
            },
            cancellationToken);

    /// <summary>使用边属性执行双向 Dijkstra 搜索。</summary>
    /// <param name="session">稳定图读会话。</param>
    /// <param name="startId">起点。</param>
    /// <param name="targetId">目标。</param>
    /// <param name="weightPropertyId">边权重属性标识符。</param>
    /// <param name="direction">扩展方向。</param>
    /// <param name="edgeLabelId">可选边标签过滤。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到时返回加权路径，否则返回 null。</returns>
    public static GraphWeightedPath? BidirectionalDijkstra(
        this GraphReadSession session,
        GraphElementId startId,
        GraphElementId targetId,
        int weightPropertyId,
        GraphDirection direction = GraphDirection.Outgoing,
        LabelId? edgeLabelId = null,
        CancellationToken cancellationToken = default)
        => session.WeightedShortestPath(
            startId,
            targetId,
            GraphWeightedShortestPathOptions.ForProperty(weightPropertyId) with
            {
                Algorithm = GraphWeightedShortestPathAlgorithm.BidirectionalDijkstra,
                Direction = direction,
                EdgeLabelId = edgeLabelId,
            },
            cancellationToken);

    private static GraphWeightedPath? FindOneWay(
        GraphReadSession session,
        GraphElementId startId,
        GraphElementId targetId,
        GraphWeightedShortestPathOptions options,
        bool useHeuristic,
        CancellationToken cancellationToken)
    {
        var frontier = new PriorityQueue<FrontierEntry, (double Priority, long VertexId)>();
        var startState = new SearchState(startId, 0);
        var distances = new Dictionary<SearchState, double> { [startState] = 0 };
        var statesByVertex = new Dictionary<GraphElementId, List<SearchState>>
        {
            [startId] = [startState],
        };
        var predecessors = new Dictionary<SearchState, PathLink>();
        var settled = new HashSet<SearchState>();
        var discoveredVertices = new HashSet<GraphElementId> { startId };
        frontier.Enqueue(new FrontierEntry(startId, 0, 0), (Heuristic(options, startId, useHeuristic), startId.Value));
        long expandedEdges = 0;
        int expandedVertices = 0;

        while (frontier.TryDequeue(out FrontierEntry entry, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchState currentState = entry.State;
            if (!distances.TryGetValue(currentState, out double best)
                || entry.Distance != best
                || !settled.Add(currentState))
                continue;

            expandedVertices = checked(expandedVertices + 1);
            if (entry.VertexId == targetId)
                return BuildResult(
                    startId,
                    currentState,
                    entry.Distance,
                    predecessors,
                    options.Algorithm,
                    expandedVertices,
                    expandedEdges,
                    session.Sequence);
            if (entry.Depth >= options.MaxDepth)
                continue;

            using GraphCursor<GraphExpansion> cursor = session.Expand(
                entry.VertexId,
                options.Direction,
                options.EdgeLabelId,
                CreateExpansionCursorOptions(options, expandedEdges));
            while (true)
            {
                IReadOnlyList<GraphExpansion> page = cursor.ReadNextPage(cancellationToken);
                if (page.Count == 0)
                    break;
                foreach (GraphExpansion expansion in page)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    expandedEdges = checked(expandedEdges + 1);
                    if (expandedEdges > options.MaxExpandedEdges)
                        throw new GraphWeightedPathLimitExceededException(
                            $"加权路径扩展边数超过上限 {options.MaxExpandedEdges}。");
                    double edgeWeight = options.ReadWeight(expansion.Edge);
                    double candidate = AddWeight(entry.Distance, edgeWeight, expansion.Edge.Id);
                    if (candidate > options.MaxTotalWeight)
                        continue;
                    int depth = checked(entry.Depth + 1);
                    var candidateState = new SearchState(expansion.NeighborId, depth);
                    if (distances.TryGetValue(candidateState, out double oldDistance)
                        && candidate >= oldDistance)
                        continue;
                    if (IsDominated(candidateState, candidate, statesByVertex, distances))
                        continue;
                    if (!discoveredVertices.Contains(expansion.NeighborId)
                        && discoveredVertices.Count >= options.MaxVisitedVertices)
                        throw new GraphWeightedPathLimitExceededException(
                            $"加权路径访问顶点数超过上限 {options.MaxVisitedVertices}。");
                    discoveredVertices.Add(expansion.NeighborId);
                    bool newState = !distances.ContainsKey(candidateState);
                    distances[candidateState] = candidate;
                    if (newState)
                        AddState(statesByVertex, candidateState);
                    predecessors[candidateState] = new PathLink(currentState, expansion.Edge.Id);
                    double priority = candidate + Heuristic(options, expansion.NeighborId, useHeuristic);
                    if (!double.IsFinite(priority))
                        throw new GraphWeightOverflowException(
                            $"顶点 {expansion.NeighborId} 的 A* 优先级溢出。");
                    if (frontier.Count >= options.MaxFrontier)
                        throw new GraphWeightedPathLimitExceededException(
                            $"加权路径 frontier 超过上限 {options.MaxFrontier}。");
                    frontier.Enqueue(
                        new FrontierEntry(expansion.NeighborId, candidate, depth),
                        (priority, expansion.NeighborId.Value));
                }
            }
        }

        return null;
    }

    private static GraphWeightedPath? FindBidirectional(
        GraphReadSession session,
        GraphElementId startId,
        GraphElementId targetId,
        GraphWeightedShortestPathOptions options,
        CancellationToken cancellationToken)
    {
        var forwardQueue = new PriorityQueue<FrontierEntry, (double Distance, long VertexId)>();
        var backwardQueue = new PriorityQueue<FrontierEntry, (double Distance, long VertexId)>();
        var startState = new SearchState(startId, 0);
        var targetState = new SearchState(targetId, 0);
        var forwardDistances = new Dictionary<SearchState, double> { [startState] = 0 };
        var backwardDistances = new Dictionary<SearchState, double> { [targetState] = 0 };
        var forwardStatesByVertex = new Dictionary<GraphElementId, List<SearchState>>
        {
            [startId] = [startState],
        };
        var backwardStatesByVertex = new Dictionary<GraphElementId, List<SearchState>>
        {
            [targetId] = [targetState],
        };
        var forwardPredecessors = new Dictionary<SearchState, PathLink>();
        var backwardSuccessors = new Dictionary<SearchState, PathLink>();
        var forwardSettled = new HashSet<SearchState>();
        var backwardSettled = new HashSet<SearchState>();
        var discoveredVertices = new HashSet<GraphElementId> { startId, targetId };
        if (discoveredVertices.Count > options.MaxVisitedVertices)
            throw new GraphWeightedPathLimitExceededException(
                $"加权路径访问顶点数超过上限 {options.MaxVisitedVertices}。");
        forwardQueue.Enqueue(new FrontierEntry(startId, 0, 0), (0, startId.Value));
        backwardQueue.Enqueue(new FrontierEntry(targetId, 0, 0), (0, targetId.Value));

        double bestDistance = double.PositiveInfinity;
        SearchState forwardMeeting = default;
        SearchState backwardMeeting = default;
        long expandedEdges = 0;
        int expandedVertices = 0;
        GraphDirection backwardDirection = Reverse(options.Direction);

        while (forwardQueue.Count > 0 && backwardQueue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double forwardMinimum = PeekPriority(forwardQueue);
            double backwardMinimum = PeekPriority(backwardQueue);
            if (double.IsFinite(bestDistance)
                && AddWeight(forwardMinimum, backwardMinimum, default) >= bestDistance)
                break;

            bool expandForward = forwardMinimum <= backwardMinimum;
            PriorityQueue<FrontierEntry, (double Distance, long VertexId)> queue = expandForward
                ? forwardQueue
                : backwardQueue;
            if (!queue.TryDequeue(out FrontierEntry current, out _))
                break;
            Dictionary<SearchState, double> distances = expandForward ? forwardDistances : backwardDistances;
            Dictionary<GraphElementId, List<SearchState>> statesByVertex = expandForward
                ? forwardStatesByVertex
                : backwardStatesByVertex;
            HashSet<SearchState> settled = expandForward ? forwardSettled : backwardSettled;
            SearchState currentState = current.State;
            if (!distances.TryGetValue(currentState, out double currentDistance)
                || current.Distance != currentDistance
                || !settled.Add(currentState))
                continue;

            expandedVertices = checked(expandedVertices + 1);
            UpdateMeeting(
                currentState,
                currentDistance,
                expandForward,
                expandForward ? backwardStatesByVertex : forwardStatesByVertex,
                expandForward ? backwardDistances : forwardDistances,
                options,
                ref bestDistance,
                ref forwardMeeting,
                ref backwardMeeting);
            if (current.Depth >= options.MaxDepth)
                continue;

            GraphDirection direction = expandForward ? options.Direction : backwardDirection;
            using GraphCursor<GraphExpansion> cursor = session.Expand(
                current.VertexId,
                direction,
                options.EdgeLabelId,
                CreateExpansionCursorOptions(options, expandedEdges));
            while (true)
            {
                IReadOnlyList<GraphExpansion> page = cursor.ReadNextPage(cancellationToken);
                if (page.Count == 0)
                    break;
                foreach (GraphExpansion expansion in page)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    expandedEdges = checked(expandedEdges + 1);
                    if (expandedEdges > options.MaxExpandedEdges)
                        throw new GraphWeightedPathLimitExceededException(
                            $"加权路径扩展边数超过上限 {options.MaxExpandedEdges}。");
                    double candidate = AddWeight(currentDistance, options.ReadWeight(expansion.Edge), expansion.Edge.Id);
                    if (candidate > options.MaxTotalWeight)
                        continue;
                    int depth = checked(current.Depth + 1);
                    if (depth > options.MaxDepth)
                        continue;
                    var candidateState = new SearchState(expansion.NeighborId, depth);
                    if (distances.TryGetValue(candidateState, out double oldDistance)
                        && candidate >= oldDistance)
                        continue;
                    if (IsDominated(candidateState, candidate, statesByVertex, distances))
                        continue;
                    if (!discoveredVertices.Contains(expansion.NeighborId)
                        && discoveredVertices.Count >= options.MaxVisitedVertices)
                        throw new GraphWeightedPathLimitExceededException(
                            $"加权路径访问顶点数超过上限 {options.MaxVisitedVertices}。");
                    discoveredVertices.Add(expansion.NeighborId);
                    bool newState = !distances.ContainsKey(candidateState);
                    distances[candidateState] = candidate;
                    if (newState)
                        AddState(statesByVertex, candidateState);
                    if (expandForward)
                        forwardPredecessors[candidateState] = new PathLink(currentState, expansion.Edge.Id);
                    else
                        backwardSuccessors[candidateState] = new PathLink(currentState, expansion.Edge.Id);
                    if (forwardQueue.Count + backwardQueue.Count >= options.MaxFrontier)
                        throw new GraphWeightedPathLimitExceededException(
                            $"加权路径 frontier 超过上限 {options.MaxFrontier}。");
                    queue.Enqueue(new FrontierEntry(expansion.NeighborId, candidate, depth), (candidate, expansion.NeighborId.Value));
                    UpdateMeeting(
                        candidateState,
                        candidate,
                        expandForward,
                        expandForward ? backwardStatesByVertex : forwardStatesByVertex,
                        expandForward ? backwardDistances : forwardDistances,
                        options,
                        ref bestDistance,
                        ref forwardMeeting,
                        ref backwardMeeting);
                }
            }
        }

        if (!double.IsFinite(bestDistance))
            return null;
        GraphPath path = BuildBidirectionalPath(
            startState,
            targetState,
            forwardMeeting,
            backwardMeeting,
            forwardPredecessors,
            backwardSuccessors);
        return new GraphWeightedPath(
            path,
            bestDistance,
            options.Algorithm,
            expandedVertices,
            expandedEdges,
            session.Sequence);
    }

    private static void UpdateMeeting(
        SearchState currentState,
        double distance,
        bool currentIsForward,
        IReadOnlyDictionary<GraphElementId, List<SearchState>> otherStatesByVertex,
        IReadOnlyDictionary<SearchState, double> otherDistances,
        GraphWeightedShortestPathOptions options,
        ref double bestDistance,
        ref SearchState forwardMeeting,
        ref SearchState backwardMeeting)
    {
        if (!otherStatesByVertex.TryGetValue(currentState.VertexId, out List<SearchState>? otherStates))
            return;
        foreach (SearchState otherState in otherStates)
        {
            if (currentState.Depth + otherState.Depth > options.MaxDepth
                || !otherDistances.TryGetValue(otherState, out double otherDistance))
                continue;
            double combined = AddWeight(distance, otherDistance, default);
            if (combined > options.MaxTotalWeight)
                continue;
            SearchState candidateForward = currentIsForward ? currentState : otherState;
            SearchState candidateBackward = currentIsForward ? otherState : currentState;
            if (combined < bestDistance
                || (combined == bestDistance
                    && IsEarlierMeeting(candidateForward, candidateBackward, forwardMeeting, backwardMeeting)))
            {
                bestDistance = combined;
                forwardMeeting = candidateForward;
                backwardMeeting = candidateBackward;
            }
        }
    }

    private static GraphWeightedPath BuildResult(
        GraphElementId startId,
        SearchState targetState,
        double distance,
        IReadOnlyDictionary<SearchState, PathLink> predecessors,
        GraphWeightedShortestPathAlgorithm algorithm,
        int expandedVertices,
        long expandedEdges,
        long snapshotSequence)
    {
        var vertices = new GraphElementId[targetState.Depth + 1];
        var edges = new GraphElementId[targetState.Depth];
        int vertexIndex = targetState.Depth;
        int edgeIndex = targetState.Depth - 1;
        vertices[vertexIndex] = targetState.VertexId;
        SearchState current = targetState;
        while (current.VertexId != startId || current.Depth != 0)
        {
            if (!predecessors.TryGetValue(current, out PathLink predecessor))
                throw new InvalidDataException("加权路径 predecessor 链不完整。");
            edges[edgeIndex--] = predecessor.EdgeId;
            current = predecessor.State;
            vertices[--vertexIndex] = current.VertexId;
        }
        return new GraphWeightedPath(
            new GraphPath(vertices, edges),
            distance,
            algorithm,
            expandedVertices,
            expandedEdges,
            snapshotSequence);
    }

    private static GraphPath BuildBidirectionalPath(
        SearchState startState,
        SearchState targetState,
        SearchState forwardMeeting,
        SearchState backwardMeeting,
        IReadOnlyDictionary<SearchState, PathLink> forwardPredecessors,
        IReadOnlyDictionary<SearchState, PathLink> backwardSuccessors)
    {
        var vertices = new List<GraphElementId> { forwardMeeting.VertexId };
        var edges = new List<GraphElementId>();
        SearchState current = forwardMeeting;
        while (current != startState)
        {
            if (!forwardPredecessors.TryGetValue(current, out PathLink predecessor))
                throw new InvalidDataException("双向加权路径的前向 predecessor 链不完整。");
            edges.Add(predecessor.EdgeId);
            current = predecessor.State;
            vertices.Add(current.VertexId);
        }
        vertices.Reverse();
        edges.Reverse();
        current = backwardMeeting;
        while (current != targetState)
        {
            if (!backwardSuccessors.TryGetValue(current, out PathLink successor))
                throw new InvalidDataException("双向加权路径的后向 successor 链不完整。");
            edges.Add(successor.EdgeId);
            current = successor.State;
            vertices.Add(current.VertexId);
        }
        return new GraphPath(vertices, edges);
    }

    private static double Heuristic(
        GraphWeightedShortestPathOptions options,
        GraphElementId vertexId,
        bool enabled)
    {
        if (!enabled || options.Heuristic is null)
            return 0;
        double value = options.Heuristic(vertexId);
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            throw new GraphWeightOverflowException(
                $"顶点 {vertexId} 的 A* 启发式必须是有限且非负数值。");
        return value;
    }

    private static GraphCursorOptions CreateExpansionCursorOptions(
        GraphWeightedShortestPathOptions options,
        long expandedEdges)
    {
        long remaining = options.MaxExpandedEdges - expandedEdges;
        // 额外一条只用于证明仍有未检查邻接；算法循环会在消费其权重前抛出预算异常。
        int probeLimit = remaining >= int.MaxValue
            ? int.MaxValue
            : checked((int)remaining + 1);
        return new GraphCursorOptions
        {
            PageSize = Math.Min(options.PageSize, probeLimit),
            MaxPageBytes = options.MaxPageBytes,
            MaxResults = probeLimit,
        };
    }

    private static double AddWeight(double left, double right, GraphElementId edgeId)
    {
        double result = left + right;
        if (!double.IsFinite(result))
        {
            string suffix = edgeId.Value > 0 ? $"（边 {edgeId}）" : string.Empty;
            throw new GraphWeightOverflowException($"路径权重累加发生溢出{suffix}。");
        }
        return result;
    }

    private static double PeekPriority(PriorityQueue<FrontierEntry, (double Distance, long VertexId)> queue)
    {
        if (!queue.TryPeek(out _, out (double Distance, long VertexId) priority))
            return double.PositiveInfinity;
        return priority.Distance;
    }

    private static void AddState(
        Dictionary<GraphElementId, List<SearchState>> statesByVertex,
        SearchState state)
    {
        if (!statesByVertex.TryGetValue(state.VertexId, out List<SearchState>? states))
        {
            states = [];
            statesByVertex[state.VertexId] = states;
        }
        states.Add(state);
    }

    private static bool IsDominated(
        SearchState candidateState,
        double candidateDistance,
        IReadOnlyDictionary<GraphElementId, List<SearchState>> statesByVertex,
        IReadOnlyDictionary<SearchState, double> distances)
    {
        if (!statesByVertex.TryGetValue(candidateState.VertexId, out List<SearchState>? states))
            return false;
        foreach (SearchState state in states)
        {
            if (state.Depth <= candidateState.Depth
                && distances.TryGetValue(state, out double distance)
                && distance <= candidateDistance)
                return true;
        }
        return false;
    }

    private static bool IsEarlierMeeting(
        SearchState forward,
        SearchState backward,
        SearchState currentForward,
        SearchState currentBackward)
    {
        if (currentForward.VertexId.Value == 0)
            return true;
        int vertexComparison = forward.VertexId.Value.CompareTo(currentForward.VertexId.Value);
        if (vertexComparison != 0)
            return vertexComparison < 0;
        int forwardDepthComparison = forward.Depth.CompareTo(currentForward.Depth);
        return forwardDepthComparison != 0
            ? forwardDepthComparison < 0
            : backward.Depth < currentBackward.Depth;
    }

    private static GraphDirection Reverse(GraphDirection direction)
        => direction switch
        {
            GraphDirection.Outgoing => GraphDirection.Incoming,
            GraphDirection.Incoming => GraphDirection.Outgoing,
            GraphDirection.Both => GraphDirection.Both,
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };

    private static void ValidateElementId(GraphElementId id, string parameterName)
    {
        if (id.Value <= 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private readonly record struct SearchState(
        GraphElementId VertexId,
        int Depth);

    private readonly record struct FrontierEntry(
        GraphElementId VertexId,
        double Distance,
        int Depth)
    {
        internal SearchState State => new(VertexId, Depth);
    }

    private readonly record struct PathLink(
        SearchState State,
        GraphElementId EdgeId);
}
