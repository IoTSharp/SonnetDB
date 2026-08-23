using SonnetDB.Graphs.Storage;
using SonnetDB.Kv;

namespace SonnetDB.Graphs;

internal sealed class GraphExpansionCursorSource : IGraphCursorSource<GraphExpansion>
{
    private readonly KvReadSnapshot _snapshot;
    private readonly GraphElementId _anchorId;
    private readonly GraphDirection _direction;
    private readonly LabelId? _labelId;
    private readonly GraphVertexPredicate? _targetPredicate;
    private readonly GraphCursorOptions _options;
    private KvRangeCursor? _cursor;
    private IReadOnlyList<KvEntry>? _pendingEntries;
    private int _pendingEntryIndex;
    private GraphDirection _currentDirection;
    private bool _ended;
    private bool _disposed;

    internal GraphExpansionCursorSource(
        KvReadSnapshot snapshot,
        GraphElementId anchorId,
        GraphDirection direction,
        LabelId? labelId,
        GraphVertexPredicate? targetPredicate,
        GraphCursorOptions options)
    {
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        _snapshot = snapshot;
        _anchorId = anchorId;
        _direction = direction;
        _labelId = labelId;
        _targetPredicate = targetPredicate;
        _options = options;
        SnapshotSequence = snapshot.Sequence;
        OpenCursor(direction == GraphDirection.Incoming
            ? GraphDirection.Incoming
            : GraphDirection.Outgoing);
    }

    public long SnapshotSequence { get; }

    public bool IsExhausted => _ended;

    public IReadOnlyList<GraphExpansion> ReadNextPage(CancellationToken cancellationToken)
    {
        var result = new List<GraphExpansion>(_options.PageSize);
        while (result.Count < _options.PageSize && !_ended)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KvRangeCursor cursor = _cursor
                ?? throw new InvalidOperationException("Graph expansion cursor 状态无效。");
            if (_pendingEntries is null || _pendingEntryIndex >= _pendingEntries.Count)
            {
                IReadOnlyList<KvEntry> entries = cursor.ReadNextPage(cancellationToken);
                if (entries.Count == 0)
                {
                    if (!TryAdvanceDirection())
                        break;
                    continue;
                }

                _pendingEntries = entries;
                _pendingEntryIndex = 0;
            }

            KvEntry entry = _pendingEntries[_pendingEntryIndex++];
            if (_pendingEntryIndex >= _pendingEntries.Count)
            {
                _pendingEntries = null;
                _pendingEntryIndex = 0;
            }

            GraphStorageKey key = GraphKeyCodec.Decode(entry.Key.Span);
            if (_currentDirection == GraphDirection.Outgoing
                && key.Kind != GraphKeyKind.OutgoingAdjacency)
                throw new InvalidDataException("Graph outgoing adjacency key family 无效。");
            if (_currentDirection == GraphDirection.Incoming
                && key.Kind != GraphKeyKind.IncomingAdjacency)
                throw new InvalidDataException("Graph incoming adjacency key family 无效。");
            if (key.SourceId != _anchorId && _currentDirection == GraphDirection.Outgoing
                || key.TargetId != _anchorId && _currentDirection == GraphDirection.Incoming)
            {
                throw new InvalidDataException("Graph adjacency key 与 anchor 不一致。");
            }

            // 双向扫描会分别看到自环的出/入投影；对外只暴露一次。
            if (_direction == GraphDirection.Both
                && _currentDirection == GraphDirection.Incoming
                && key.SourceId == _anchorId
                && key.TargetId == _anchorId)
            {
                continue;
            }

            GraphEdge edge = GraphReadSession.ReadEdge(_snapshot, key.EdgeId);
            if (edge.SourceId != key.SourceId
                || edge.TargetId != key.TargetId
                || edge.LabelId != key.LabelId)
            {
                throw new InvalidDataException("Graph adjacency projection 与 edge record 不一致。");
            }
            GraphElementId neighborId = _currentDirection == GraphDirection.Outgoing
                ? key.TargetId
                : key.SourceId;
            if (_targetPredicate is not null
                && !_targetPredicate.Matches(GraphReadSession.ReadVertex(_snapshot, neighborId)))
            {
                continue;
            }

            result.Add(new GraphExpansion(
                _anchorId,
                neighborId,
                _currentDirection,
                edge));
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cursor?.Dispose();
        _snapshot.Dispose();
        _cursor = null;
        _pendingEntries = null;
        _pendingEntryIndex = 0;
        _ended = true;
    }

    private void OpenCursor(GraphDirection direction)
    {
        _cursor?.Dispose();
        byte[] prefix = direction == GraphDirection.Outgoing
            ? _labelId is { } outgoingLabel
                ? GraphKeyCodec.OutgoingPrefix(_anchorId, outgoingLabel)
                : GraphKeyCodec.OutgoingPrefix(_anchorId)
            : _labelId is { } incomingLabel
                ? GraphKeyCodec.IncomingPrefix(_anchorId, incomingLabel)
                : GraphKeyCodec.IncomingPrefix(_anchorId);
        _cursor = _snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = prefix,
            PageSize = _options.PageSize,
            MaxPageBytes = _options.MaxPageBytes,
        });
        _pendingEntries = null;
        _pendingEntryIndex = 0;
        _currentDirection = direction;
    }

    private bool TryAdvanceDirection()
    {
        if (_direction == GraphDirection.Both && _currentDirection == GraphDirection.Outgoing)
        {
            OpenCursor(GraphDirection.Incoming);
            return true;
        }

        _ended = true;
        _cursor?.Dispose();
        _cursor = null;
        return false;
    }
}
