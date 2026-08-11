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
    private readonly Queue<GraphPath> _pendingResults = new();
    private readonly HashSet<GraphElementId> _visitedVertices = [];
    private GraphExpansionCursorSource? _activeExpansion;
    private bool _emitStart;
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

        var start = new TraversalPath([startId], []);
        AddPending(start);
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
            if (_pendingResults.TryDequeue(out GraphPath? pendingResult))
            {
                result.Add(pendingResult);
                continue;
            }
            if (_emitStart)
            {
                _emitStart = false;
                result.Add(ToPublicPath(new TraversalPath(
                    _mode == GraphTraversalMode.BreadthFirst
                        ? _queue.Peek().Vertices
                        : _stack.Peek().Vertices,
                    _mode == GraphTraversalMode.BreadthFirst
                        ? _queue.Peek().Edges
                        : _stack.Peek().Edges)));
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
                if (currentPath.Edges.Count >= _maxDepth)
                    continue;
                _activeExpansion = new GraphExpansionCursorSource(
                    _snapshot.AcquireLease(),
                    currentPath.Vertices[^1],
                    _direction,
                    _edgeLabelId,
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
                if (child.Edges.Count >= _minDepth)
                    _pendingResults.Enqueue(ToPublicPath(child));
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
        _visitedVertices.Add(path.Vertices[^1]);
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
            && path.Vertices.Skip(0).Take(path.Vertices.Count - 1).Contains(path.Vertices[^1]))
            return false;
        if (_options.PathUniqueness == GraphPathUniqueness.Edge
            && path.Edges.Count != path.Edges.Distinct().Count())
            return false;
        if (_mode == GraphTraversalMode.BreadthFirst
            && _options.PathUniqueness == GraphPathUniqueness.Vertex
            && _deduplicateBreadthFirstEndpoints
            && !_visitedVertices.Add(path.Vertices[^1]))
            return false;
        return true;
    }

    private void AddChildren(IReadOnlyList<TraversalPath> children)
    {
        foreach (TraversalPath child in _mode == GraphTraversalMode.DepthFirst
            ? children.Reverse()
            : children)
        {
            if (child.Edges.Count >= _maxDepth)
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
        => new(path.Vertices.ToArray(), path.Edges.ToArray());

    private sealed class TraversalPath
    {
        internal TraversalPath(IReadOnlyList<GraphElementId> vertices, IReadOnlyList<GraphElementId> edges)
        {
            Vertices = vertices;
            Edges = edges;
        }

        internal IReadOnlyList<GraphElementId> Vertices { get; }

        internal IReadOnlyList<GraphElementId> Edges { get; }

        internal TraversalPath Extend(GraphElementId vertexId, GraphElementId edgeId)
        {
            var vertices = new GraphElementId[Vertices.Count + 1];
            for (int index = 0; index < Vertices.Count; index++)
                vertices[index] = Vertices[index];
            vertices[^1] = vertexId;
            var edges = new GraphElementId[Edges.Count + 1];
            for (int index = 0; index < Edges.Count; index++)
                edges[index] = Edges[index];
            edges[^1] = edgeId;
            return new TraversalPath(vertices, edges);
        }
    }
}
