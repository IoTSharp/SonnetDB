using SonnetDB.Kv;

namespace SonnetDB.Graphs;

internal sealed class GraphTraversalCursorSource : IGraphCursorSource<GraphPath>
{
    private readonly KvReadSnapshot _snapshot;
    private readonly GraphTraversalMode _mode;
    private readonly int _minDepth;
    private readonly int _maxDepth;
    private readonly GraphDirection _direction;
    private readonly LabelId? _edgeLabelId;
    private readonly GraphTraversalOptions _options;
    private readonly bool _deduplicateBreadthFirstEndpoints;
    private readonly GraphTraversalDiagnostics? _diagnostics;
    private readonly Queue<TraversalPath> _queue = new();
    private readonly Stack<TraversalPath> _stack = new();
    private readonly Queue<TraversalPath> _pendingResults = new();
    private readonly HashSet<GraphElementId> _visitedVertices = [];
    private GraphExpansionCursorSource? _activeExpansion;
    private bool _emitStart;
    private readonly TraversalPath _startPath;
    private bool _ended;
    private bool _disposed;

    internal GraphTraversalCursorSource(
        KvReadSnapshot snapshot,
        GraphElementId startId,
        GraphTraversalMode mode,
        int minDepth,
        int maxDepth,
        GraphDirection direction,
        LabelId? edgeLabelId,
        GraphTraversalOptions options,
        bool deduplicateBreadthFirstEndpoints,
        GraphTraversalDiagnostics? diagnostics)
    {
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        _snapshot = snapshot;
        _mode = mode;
        _minDepth = minDepth;
        _maxDepth = maxDepth;
        _direction = direction;
        _edgeLabelId = edgeLabelId;
        _options = options;
        _deduplicateBreadthFirstEndpoints = deduplicateBreadthFirstEndpoints;
        _diagnostics = diagnostics;
        SnapshotSequence = snapshot.Sequence;
        if (GraphReadSession.ReadVertex(snapshot, startId) is null)
            throw new InvalidOperationException($"Graph traversal 起点 vertex {startId} 不存在。");

        _startPath = TraversalPath.Start(startId);
        AddPending(_startPath);
        _emitStart = minDepth == 0;
    }

    public long SnapshotSequence { get; }

    public bool IsExhausted => _ended;

    public IReadOnlyList<GraphPath> ReadNextPage(CancellationToken cancellationToken)
    {
        var result = new List<GraphPath>(_options.PageSize);
        while (result.Count < _options.PageSize && !_ended)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_pendingResults.TryDequeue(out TraversalPath? pendingResult))
            {
                result.Add(ToPublicPath(pendingResult));
                continue;
            }
            if (_emitStart)
            {
                _emitStart = false;
                result.Add(ToPublicPath(_startPath));
                continue;
            }

            if (_activeExpansion is null)
            {
                if (!TryTakePending(out TraversalPath? path))
                {
                    _ended = true;
                    break;
                }
                TraversalPath currentPath = path!;
                if (currentPath.Depth >= _maxDepth)
                    continue;
                _activeExpansion = new GraphExpansionCursorSource(
                    _snapshot.AcquireLease(),
                    currentPath.VertexId,
                    _direction,
                    _edgeLabelId,
                    null,
                    new GraphCursorOptions
                    {
                        PageSize = _options.PageSize,
                        MaxPageBytes = _options.MaxPageBytes,
                        MaxResults = _options.MaxFrontier,
                    });
                _activePath = currentPath;
            }

            IReadOnlyList<GraphExpansion> expansions = _activeExpansion.ReadNextPage(cancellationToken);
            if (_diagnostics is not null)
                _diagnostics.ExpansionCount = checked(_diagnostics.ExpansionCount + expansions.Count);
            if (expansions.Count == 0)
            {
                _activeExpansion.Dispose();
                _activeExpansion = null;
                continue;
            }

            var children = new List<TraversalPath>(expansions.Count);
            foreach (GraphExpansion expansion in expansions)
            {
                TraversalPath child = _activePath.Extend(expansion.NeighborId, expansion.Edge.Id);
                if (!IsAllowed(child))
                    continue;
                if (_diagnostics is not null)
                    _diagnostics.GeneratedPathCount++;
                children.Add(child);
                if (child.Depth >= _minDepth)
                    _pendingResults.Enqueue(child);
            }

            AddChildren(children);
            if (_activeExpansion.IsExhausted)
            {
                _activeExpansion.Dispose();
                _activeExpansion = null;
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _activeExpansion?.Dispose();
        _activeExpansion = null;
        _snapshot.Dispose();
        _queue.Clear();
        _stack.Clear();
        _pendingResults.Clear();
        _ended = true;
    }

    private TraversalPath _activePath = null!;

    private void AddPending(TraversalPath path)
    {
        if (_mode == GraphTraversalMode.BreadthFirst)
            _queue.Enqueue(path);
        else
            _stack.Push(path);
        _visitedVertices.Add(path.VertexId);
        if (_diagnostics is not null)
            _diagnostics.PeakFrontier = Math.Max(_diagnostics.PeakFrontier, 1);
    }

    private bool TryTakePending(out TraversalPath? path)
    {
        if (_mode == GraphTraversalMode.BreadthFirst)
        {
            if (_queue.Count == 0)
            {
                path = null;
                return false;
            }
            path = _queue.Dequeue();
            return true;
        }
        if (_stack.Count == 0)
        {
            path = null;
            return false;
        }
        path = _stack.Pop();
        return true;
    }

    private bool IsAllowed(TraversalPath path)
    {
        if (_options.PathUniqueness == GraphPathUniqueness.Vertex
            && path.ContainsVertex(path.VertexId, includeCurrent: false))
            return false;
        if (_options.PathUniqueness == GraphPathUniqueness.Edge
            && path.ContainsEdge(path.EdgeId, includeCurrent: false))
            return false;
        if (_mode == GraphTraversalMode.BreadthFirst
            && _options.PathUniqueness == GraphPathUniqueness.Vertex
            && _deduplicateBreadthFirstEndpoints
            && !_visitedVertices.Add(path.VertexId))
            return false;
        return true;
    }

    private void AddChildren(IReadOnlyList<TraversalPath> children)
    {
        foreach (TraversalPath child in _mode == GraphTraversalMode.DepthFirst
            ? children.Reverse()
            : children)
        {
            if (child.Depth >= _maxDepth)
                continue;
            int frontier = _mode == GraphTraversalMode.BreadthFirst ? _queue.Count : _stack.Count;
            if (frontier >= _options.MaxFrontier)
            {
                throw new GraphTraversalLimitExceededException(
                    $"Graph traversal frontier 超过上限 {_options.MaxFrontier}。");
            }
            if (_mode == GraphTraversalMode.BreadthFirst)
                _queue.Enqueue(child);
            else
                _stack.Push(child);
            if (_diagnostics is not null)
            {
                int updatedFrontier = _mode == GraphTraversalMode.BreadthFirst ? _queue.Count : _stack.Count;
                _diagnostics.PeakFrontier = Math.Max(_diagnostics.PeakFrontier, updatedFrontier);
            }
        }
    }

    private static GraphPath ToPublicPath(TraversalPath path)
    {
        var vertices = new GraphElementId[path.Depth + 1];
        var edges = new GraphElementId[path.Depth];
        TraversalPath? current = path;
        for (int index = path.Depth; index >= 0; index--)
        {
            vertices[index] = current!.VertexId;
            if (index > 0)
                edges[index - 1] = current.EdgeId;
            current = current.Parent;
        }
        return new GraphPath(vertices, edges);
    }

    private sealed class TraversalPath
    {
        private TraversalPath(
            TraversalPath? parent,
            GraphElementId vertexId,
            GraphElementId edgeId,
            int depth)
        {
            Parent = parent;
            VertexId = vertexId;
            EdgeId = edgeId;
            Depth = depth;
        }

        internal TraversalPath? Parent { get; }

        internal GraphElementId VertexId { get; }

        internal GraphElementId EdgeId { get; }

        internal int Depth { get; }

        internal static TraversalPath Start(GraphElementId vertexId)
            => new(null, vertexId, default, 0);

        internal TraversalPath Extend(GraphElementId vertexId, GraphElementId edgeId)
            => new(this, vertexId, edgeId, checked(Depth + 1));

        internal bool ContainsVertex(GraphElementId vertexId, bool includeCurrent)
        {
            TraversalPath? current = includeCurrent ? this : Parent;
            while (current is not null)
            {
                if (current.VertexId == vertexId)
                    return true;
                current = current.Parent;
            }
            return false;
        }

        internal bool ContainsEdge(GraphElementId edgeId, bool includeCurrent)
        {
            TraversalPath? current = includeCurrent ? this : Parent;
            while (current is not null && current.Depth > 0)
            {
                if (current.EdgeId == edgeId)
                    return true;
                current = current.Parent;
            }
            return false;
        }
    }
}
