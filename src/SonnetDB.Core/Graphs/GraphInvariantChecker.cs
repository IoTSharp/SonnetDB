using SonnetDB.Graphs.Storage;
using SonnetDB.Kv;

namespace SonnetDB.Graphs;

internal sealed record GraphInvariantCheckOptions
{
    internal int PageSize { get; init; } = 512;

    internal long MaxScannedEntries { get; init; } = 250_000_000;

    internal long MaxPointLookups { get; init; } = 500_000_000;

    internal int MaxIssues { get; init; } = 100;
}

internal enum GraphInvariantIssueKind
{
    ScanLimitExceeded,
    PointLookupLimitExceeded,
    MalformedKey,
    MalformedRecord,
    RecordKeyMismatch,
    MissingVertexEndpoint,
    MissingOutgoingAdjacency,
    MissingIncomingAdjacency,
    OrphanOutgoingAdjacency,
    OrphanIncomingAdjacency,
    MissingLabelIndex,
    OrphanLabelIndex,
    MissingPropertyIndex,
    OrphanPropertyIndex,
    OrphanUniquePropertyIndex,
    UniquePropertyCollision,
    MissingHighWater,
    HighWaterBehind,
    UnknownMetadata,
}

internal sealed record GraphInvariantIssue(
    GraphInvariantIssueKind Kind,
    string Key,
    string Message);

internal sealed record GraphHighWaterSnapshot(
    long? VertexId,
    long? EdgeId,
    long? LabelId,
    long? PropertyId);

internal sealed record GraphInvariantReport(
    bool IsValid,
    bool IsComplete,
    long SnapshotSequence,
    long ScannedEntries,
    long PointLookupCount,
    long VertexCount,
    long EdgeCount,
    long OutgoingAdjacencyCount,
    long IncomingAdjacencyCount,
    long LabelIndexCount,
    long PropertyIndexCount,
    GraphHighWaterSnapshot HighWater,
    long TotalIssueCount,
    int SuppressedIssueCount,
    IReadOnlyList<GraphInvariantIssue> Issues);

internal static class GraphInvariantChecker
{
    private const int MaximumPageSize = 65_536;
    private const int MaximumIssueTextLength = 256;
    private const int KeyPreviewBytes = 32;

    internal static GraphInvariantReport Check(
        GraphStore store,
        GraphInvariantCheckOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        options ??= new GraphInvariantCheckOptions();
        ValidateOptions(options);

        var state = new CheckState(options.MaxIssues, options.MaxPointLookups);
        using KvReadSnapshot snapshot = store.Keyspace.AcquireReadSnapshot();
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            PageSize = options.PageSize,
        });

        ScanResult scanResult = Scan(
            snapshot,
            cursor,
            options.MaxScannedEntries,
            state,
            cancellationToken);
        bool complete = scanResult == ScanResult.Complete;
        switch (scanResult)
        {
            case ScanResult.ScanLimitExceeded:
                state.AddIssue(
                    GraphInvariantIssueKind.ScanLimitExceeded,
                    string.Empty,
                    $"Graph invariant scan exceeded MaxScannedEntries ({options.MaxScannedEntries}).");
                break;
            case ScanResult.PointLookupLimitExceeded:
                state.AddIssue(
                    GraphInvariantIssueKind.PointLookupLimitExceeded,
                    string.Empty,
                    $"Graph invariant scan exceeded MaxPointLookups ({options.MaxPointLookups}).");
                break;
        }

        if (complete)
            ValidateHighWater(state);

        return state.CreateReport(snapshot.Sequence, complete);
    }

    private static ScanResult Scan(
        KvReadSnapshot snapshot,
        KvRangeCursor cursor,
        long maximumEntries,
        CheckState state,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            IReadOnlyList<KvEntry> page = cursor.ReadNextPage(cancellationToken);
            if (page.Count == 0)
                return ScanResult.Complete;

            foreach (KvEntry entry in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (state.ScannedEntries >= maximumEntries)
                    return ScanResult.ScanLimitExceeded;

                state.ScannedEntries++;
                if (!ProcessEntry(snapshot, entry, state, cancellationToken))
                    return ScanResult.PointLookupLimitExceeded;
            }

            if (state.ScannedEntries == maximumEntries)
            {
                return cursor.ReadNextPage(cancellationToken).Count == 0
                    ? ScanResult.Complete
                    : ScanResult.ScanLimitExceeded;
            }
        }
    }

    private static bool ProcessEntry(
        KvReadSnapshot snapshot,
        KvEntry entry,
        CheckState state,
        CancellationToken cancellationToken)
    {
        GraphStorageKey key;
        try
        {
            key = GraphKeyCodec.Decode(entry.Key.Span);
        }
        catch (Exception exception) when (IsCorruptionException(exception))
        {
            state.AddIssue(
                GraphInvariantIssueKind.MalformedKey,
                PreviewKey(entry.Key.Span),
                LimitText(exception.Message));
            return true;
        }

        switch (key.Kind)
        {
            case GraphKeyKind.VertexRecord:
                return ProcessVertex(snapshot, entry, key, state, cancellationToken);
            case GraphKeyKind.EdgeRecord:
                return ProcessEdge(snapshot, entry, key, state, cancellationToken);
            case GraphKeyKind.OutgoingAdjacency:
            case GraphKeyKind.IncomingAdjacency:
                ValidateProjectionValue(entry, key, state);
                state.ObserveAdjacency(key);
                return ValidateAdjacency(snapshot, entry, key, state);
            case GraphKeyKind.VertexLabel:
            case GraphKeyKind.EdgeLabel:
                ValidateProjectionValue(entry, key, state);
                state.ObserveLabelIndex(key);
                return ValidateLabelIndex(snapshot, entry, key, state);
            case GraphKeyKind.VertexPropertyIndex:
            case GraphKeyKind.EdgePropertyIndex:
                ValidateProjectionValue(entry, key, state);
                state.ObservePropertyIndex(key);
                return ValidatePropertyIndex(snapshot, entry, key, state);
            case GraphKeyKind.VertexUniqueProperty:
            case GraphKeyKind.EdgeUniqueProperty:
                state.ObservePropertyIndex(key);
                return ValidateUniquePropertyIndex(snapshot, entry, key, state, cancellationToken);
            case GraphKeyKind.Metadata:
                ProcessMetadata(entry, key, state);
                return true;
            case GraphKeyKind.TransactionRequest:
                ValidateTransactionRequest(entry, state);
                return true;
            default:
                state.AddIssue(
                    GraphInvariantIssueKind.MalformedKey,
                    PreviewKey(entry.Key.Span),
                    $"Unknown graph key kind {(byte)key.Kind}.");
                return true;
        }
    }

    private static void ValidateProjectionValue(
        KvEntry entry,
        GraphStorageKey key,
        CheckState state)
    {
        if (!entry.Value.IsEmpty)
        {
            state.AddIssue(
                GraphInvariantIssueKind.MalformedRecord,
                PreviewKey(entry.Key.Span),
                $"Graph projection key {key.Kind} must have an empty value.");
        }
    }

    private static bool ProcessVertex(
        KvReadSnapshot snapshot,
        KvEntry entry,
        GraphStorageKey key,
        CheckState state,
        CancellationToken cancellationToken)
    {
        GraphVertexRecord record;
        try
        {
            record = GraphElementRecordCodec.DecodeVertex(entry.Value.Span);
            if (record.Id != key.ElementId)
            {
                state.AddIssue(
                    GraphInvariantIssueKind.RecordKeyMismatch,
                    PreviewKey(entry.Key.Span),
                    $"Vertex key ID {key.ElementId.Value} does not match record ID {record.Id.Value}.");
                return true;
            }
        }
        catch (Exception exception) when (IsCorruptionException(exception))
        {
            state.AddIssue(
                GraphInvariantIssueKind.MalformedRecord,
                PreviewKey(entry.Key.Span),
                LimitText(exception.Message));
            return true;
        }

        state.ObserveVertex(record);
        foreach (LabelId label in record.Labels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] labelKey = GraphKeyCodec.EncodeLabelMembership(
                GraphElementKind.Vertex,
                label,
                record.Id);
            if (!TryGetEntry(snapshot, labelKey, state, out KvEntry? labelEntry))
                return false;
            if (labelEntry is null)
            {
                state.AddIssue(
                    GraphInvariantIssueKind.MissingLabelIndex,
                    ElementKey("vertex", record.Id.Value),
                    $"Vertex {record.Id.Value} is missing label index {label.Value}.");
            }

            foreach (GraphProperty property in record.Properties)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] propertyKey = GraphKeyCodec.EncodePropertyIndex(
                    GraphElementKind.Vertex,
                    label,
                    property.PropertyId,
                    property.Value,
                    record.Id);
                if (!TryGetEntry(snapshot, propertyKey, state, out KvEntry? propertyEntry))
                    return false;
                if (propertyEntry is null)
                {
                    state.AddIssue(
                        GraphInvariantIssueKind.MissingPropertyIndex,
                        ElementKey("vertex", record.Id.Value),
                        $"Vertex {record.Id.Value} is missing property index label={label.Value}, property={property.PropertyId}.");
                }
            }
        }

        return true;
    }

    private static bool ProcessEdge(
        KvReadSnapshot snapshot,
        KvEntry entry,
        GraphStorageKey key,
        CheckState state,
        CancellationToken cancellationToken)
    {
        GraphEdgeRecord record;
        try
        {
            record = GraphElementRecordCodec.DecodeEdge(entry.Value.Span);
            if (record.Id != key.ElementId)
            {
                state.AddIssue(
                    GraphInvariantIssueKind.RecordKeyMismatch,
                    PreviewKey(entry.Key.Span),
                    $"Edge key ID {key.ElementId.Value} does not match record ID {record.Id.Value}.");
                return true;
            }
        }
        catch (Exception exception) when (IsCorruptionException(exception))
        {
            state.AddIssue(
                GraphInvariantIssueKind.MalformedRecord,
                PreviewKey(entry.Key.Span),
                LimitText(exception.Message));
            return true;
        }

        state.ObserveEdge(record);
        if (!ValidateEndpoint(snapshot, record, record.SourceId, "source", state)
            || !ValidateEndpoint(snapshot, record, record.TargetId, "target", state))
        {
            return false;
        }

        byte[] outgoingKey = GraphKeyCodec.EncodeOutgoingAdjacency(
            record.SourceId,
            record.LabelId,
            record.TargetId,
            record.Id);
        if (!TryGetEntry(snapshot, outgoingKey, state, out KvEntry? outgoingEntry))
            return false;
        if (outgoingEntry is null)
        {
            state.AddIssue(
                GraphInvariantIssueKind.MissingOutgoingAdjacency,
                ElementKey("edge", record.Id.Value),
                $"Edge {record.Id.Value} is missing its outgoing adjacency.");
        }

        byte[] incomingKey = GraphKeyCodec.EncodeIncomingAdjacency(
            record.TargetId,
            record.LabelId,
            record.SourceId,
            record.Id);
        if (!TryGetEntry(snapshot, incomingKey, state, out KvEntry? incomingEntry))
            return false;
        if (incomingEntry is null)
        {
            state.AddIssue(
                GraphInvariantIssueKind.MissingIncomingAdjacency,
                ElementKey("edge", record.Id.Value),
                $"Edge {record.Id.Value} is missing its incoming adjacency.");
        }

        byte[] labelKey = GraphKeyCodec.EncodeLabelMembership(
            GraphElementKind.Edge,
            record.LabelId,
            record.Id);
        if (!TryGetEntry(snapshot, labelKey, state, out KvEntry? labelEntry))
            return false;
        if (labelEntry is null)
        {
            state.AddIssue(
                GraphInvariantIssueKind.MissingLabelIndex,
                ElementKey("edge", record.Id.Value),
                $"Edge {record.Id.Value} is missing label index {record.LabelId.Value}.");
        }

        foreach (GraphProperty property in record.Properties)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] propertyKey = GraphKeyCodec.EncodePropertyIndex(
                GraphElementKind.Edge,
                record.LabelId,
                property.PropertyId,
                property.Value,
                record.Id);
            if (!TryGetEntry(snapshot, propertyKey, state, out KvEntry? propertyEntry))
                return false;
            if (propertyEntry is null)
            {
                state.AddIssue(
                    GraphInvariantIssueKind.MissingPropertyIndex,
                    ElementKey("edge", record.Id.Value),
                    $"Edge {record.Id.Value} is missing property index {property.PropertyId}.");
            }
        }

        return true;
    }

    private static bool ValidateEndpoint(
        KvReadSnapshot snapshot,
        GraphEdgeRecord edge,
        GraphElementId endpointId,
        string endpointName,
        CheckState state)
    {
        byte[] vertexKey = GraphKeyCodec.EncodeVertexRecord(endpointId);
        if (!TryGetEntry(snapshot, vertexKey, state, out KvEntry? entry))
            return false;
        if (entry is null)
        {
            state.AddIssue(
                GraphInvariantIssueKind.MissingVertexEndpoint,
                ElementKey("edge", edge.Id.Value),
                $"Edge {edge.Id.Value} {endpointName} vertex {endpointId.Value} is missing.");
        }
        return true;
    }

    private static bool ValidateAdjacency(
        KvReadSnapshot snapshot,
        KvEntry entry,
        GraphStorageKey key,
        CheckState state)
    {
        if (!TryReadEdge(snapshot, key.EdgeId, state, out GraphEdgeRecord? edge))
            return false;
        if (edge is not null
            && edge.Id == key.EdgeId
            && edge.SourceId == key.SourceId
            && edge.TargetId == key.TargetId
            && edge.LabelId == key.LabelId)
        {
            return true;
        }

        GraphInvariantIssueKind kind = key.Kind == GraphKeyKind.OutgoingAdjacency
            ? GraphInvariantIssueKind.OrphanOutgoingAdjacency
            : GraphInvariantIssueKind.OrphanIncomingAdjacency;
        state.AddIssue(
            kind,
            PreviewKey(entry.Key.Span),
            $"{key.Kind} for edge {key.EdgeId.Value} does not match an edge record.");
        return true;
    }

    private static bool ValidateLabelIndex(
        KvReadSnapshot snapshot,
        KvEntry entry,
        GraphStorageKey key,
        CheckState state)
    {
        bool matches;
        if (key.Kind == GraphKeyKind.VertexLabel)
        {
            if (!TryReadVertex(snapshot, key.ElementId, state, out GraphVertexRecord? vertex))
                return false;
            matches = vertex is not null && vertex.Labels.Contains(key.LabelId);
        }
        else
        {
            if (!TryReadEdge(snapshot, key.ElementId, state, out GraphEdgeRecord? edge))
                return false;
            matches = edge is not null && edge.LabelId == key.LabelId;
        }

        if (!matches)
        {
            state.AddIssue(
                GraphInvariantIssueKind.OrphanLabelIndex,
                PreviewKey(entry.Key.Span),
                $"Persisted label index for {ElementDescription(key)} has no matching element record.");
        }
        return true;
    }

    private static bool ValidatePropertyIndex(
        KvReadSnapshot snapshot,
        KvEntry entry,
        GraphStorageKey key,
        CheckState state)
    {
        bool matches;
        if (key.Kind == GraphKeyKind.VertexPropertyIndex)
        {
            if (!TryReadVertex(snapshot, key.ElementId, state, out GraphVertexRecord? vertex))
                return false;
            matches = vertex is not null
                && vertex.Labels.Contains(key.LabelId)
                && ContainsProperty(vertex.Properties, key.PropertyId, key.PropertyValue);
        }
        else
        {
            if (!TryReadEdge(snapshot, key.ElementId, state, out GraphEdgeRecord? edge))
                return false;
            matches = edge is not null
                && edge.LabelId == key.LabelId
                && ContainsProperty(edge.Properties, key.PropertyId, key.PropertyValue);
        }

        if (!matches)
        {
            state.AddIssue(
                GraphInvariantIssueKind.OrphanPropertyIndex,
                PreviewKey(entry.Key.Span),
                $"Persisted property index for {ElementDescription(key)} has no matching element record.");
        }
        return true;
    }

    private static bool ValidateUniquePropertyIndex(
        KvReadSnapshot snapshot,
        KvEntry entry,
        GraphStorageKey key,
        CheckState state,
        CancellationToken cancellationToken)
    {
        GraphElementKind elementKind = key.Kind == GraphKeyKind.VertexUniqueProperty
            ? GraphElementKind.Vertex
            : GraphElementKind.Edge;
        GraphElementId ownerId;
        try
        {
            ownerId = GraphUniquePropertyOwnerCodec.Decode(entry.Value.Span, elementKind);
        }
        catch (Exception exception) when (IsCorruptionException(exception))
        {
            state.AddIssue(
                GraphInvariantIssueKind.MalformedRecord,
                PreviewKey(entry.Key.Span),
                LimitText("Graph unique property owner is malformed: " + exception.Message));
            return true;
        }

        byte[] propertyKey = GraphKeyCodec.EncodePropertyIndex(
            elementKind,
            key.LabelId,
            key.PropertyId,
            key.PropertyValue,
            ownerId);
        if (!TryGetEntry(snapshot, propertyKey, state, out KvEntry? propertyEntry))
            return false;

        bool ownerMatches;
        if (elementKind == GraphElementKind.Vertex)
        {
            if (!TryReadVertex(snapshot, ownerId, state, out GraphVertexRecord? vertex))
                return false;
            ownerMatches = vertex is not null
                && vertex.Labels.Contains(key.LabelId)
                && ContainsProperty(vertex.Properties, key.PropertyId, key.PropertyValue);
        }
        else
        {
            if (!TryReadEdge(snapshot, ownerId, state, out GraphEdgeRecord? edge))
                return false;
            ownerMatches = edge is not null
                && edge.LabelId == key.LabelId
                && ContainsProperty(edge.Properties, key.PropertyId, key.PropertyValue);
        }

        if (!state.TryBeginPointLookup())
            return false;
        byte[] prefix = GraphKeyCodec.PropertyIndexPrefix(
            elementKind,
            key.LabelId,
            key.PropertyId,
            key.PropertyValue);
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = prefix,
            PageSize = 2,
        });
        int matches = cursor.ReadNextPage(cancellationToken).Count;
        if (propertyEntry is null || !ownerMatches || matches == 0)
        {
            state.AddIssue(
                GraphInvariantIssueKind.OrphanUniquePropertyIndex,
                PreviewKey(entry.Key.Span),
                $"Unique property index label={key.LabelId.Value}, property={key.PropertyId}, owner={ownerId.Value} has no matching element.");
        }
        if (matches > 1)
        {
            state.AddIssue(
                GraphInvariantIssueKind.UniquePropertyCollision,
                PreviewKey(entry.Key.Span),
                $"Unique property index label={key.LabelId.Value}, property={key.PropertyId} matches multiple elements.");
        }
        return true;
    }

    private static bool TryReadVertex(
        KvReadSnapshot snapshot,
        GraphElementId id,
        CheckState state,
        out GraphVertexRecord? record)
    {
        if (!TryGetEntry(snapshot, GraphKeyCodec.EncodeVertexRecord(id), state, out KvEntry? entry))
        {
            record = null;
            return false;
        }
        if (entry is null)
        {
            record = null;
            return true;
        }

        try
        {
            GraphVertexRecord decoded = GraphElementRecordCodec.DecodeVertex(entry.Value.Span);
            record = decoded.Id == id ? decoded : null;
        }
        catch (Exception exception) when (IsCorruptionException(exception))
        {
            record = null;
        }
        return true;
    }

    private static bool TryReadEdge(
        KvReadSnapshot snapshot,
        GraphElementId id,
        CheckState state,
        out GraphEdgeRecord? record)
    {
        if (!TryGetEntry(snapshot, GraphKeyCodec.EncodeEdgeRecord(id), state, out KvEntry? entry))
        {
            record = null;
            return false;
        }
        if (entry is null)
        {
            record = null;
            return true;
        }

        try
        {
            GraphEdgeRecord decoded = GraphElementRecordCodec.DecodeEdge(entry.Value.Span);
            record = decoded.Id == id ? decoded : null;
        }
        catch (Exception exception) when (IsCorruptionException(exception))
        {
            record = null;
        }
        return true;
    }

    private static bool TryGetEntry(
        KvReadSnapshot snapshot,
        ReadOnlySpan<byte> key,
        CheckState state,
        out KvEntry? entry)
    {
        if (!state.TryBeginPointLookup())
        {
            entry = null;
            return false;
        }
        entry = snapshot.GetEntry(key);
        return true;
    }

    private static bool ContainsProperty(
        IReadOnlyList<GraphProperty> properties,
        int propertyId,
        GraphPropertyValue value)
    {
        foreach (GraphProperty property in properties)
        {
            if (property.PropertyId == propertyId)
                return property.Value == value;
            if (property.PropertyId > propertyId)
                return false;
        }
        return false;
    }

    private static void ProcessMetadata(KvEntry entry, GraphStorageKey key, CheckState state)
    {
        var kind = (GraphHighWaterKind)key.MetadataKind;
        if (!Enum.IsDefined(kind))
        {
            state.AddIssue(
                GraphInvariantIssueKind.UnknownMetadata,
                PreviewKey(entry.Key.Span),
                $"Unknown graph metadata kind {key.MetadataKind}.");
            return;
        }

        try
        {
            state.HighWater[kind] = GraphHighWaterCodec.Decode(entry.Value.Span, kind);
        }
        catch (Exception exception) when (IsCorruptionException(exception))
        {
            state.AddIssue(
                GraphInvariantIssueKind.MalformedRecord,
                PreviewKey(entry.Key.Span),
                LimitText(exception.Message));
        }
    }

    private static void ValidateTransactionRequest(KvEntry entry, CheckState state)
    {
        try
        {
            _ = GraphTransactionRequestCodec.Decode(entry.Value.Span);
        }
        catch (Exception exception) when (IsCorruptionException(exception))
        {
            state.AddIssue(
                GraphInvariantIssueKind.MalformedRecord,
                PreviewKey(entry.Key.Span),
                LimitText("Graph transaction request marker is malformed: " + exception.Message));
        }
    }

    private static void ValidateHighWater(CheckState state)
    {
        ValidateHighWater(state, GraphHighWaterKind.VertexId, state.MaximumVertexId);
        ValidateHighWater(state, GraphHighWaterKind.EdgeId, state.MaximumEdgeId);
        ValidateHighWater(state, GraphHighWaterKind.LabelId, state.MaximumLabelId);
        ValidateHighWater(state, GraphHighWaterKind.PropertyId, state.MaximumPropertyId);
    }

    private static void ValidateHighWater(CheckState state, GraphHighWaterKind kind, long observedMaximum)
    {
        if (!state.HighWater.TryGetValue(kind, out long persisted))
        {
            if (observedMaximum > 0)
            {
                state.AddIssue(
                    GraphInvariantIssueKind.MissingHighWater,
                    string.Empty,
                    $"Graph {kind} high-water is missing; observed maximum is {observedMaximum}.");
            }
            return;
        }

        if (persisted < observedMaximum)
        {
            state.AddIssue(
                GraphInvariantIssueKind.HighWaterBehind,
                string.Empty,
                $"Graph {kind} high-water {persisted} is behind observed maximum {observedMaximum}.");
        }
    }

    private static void ValidateOptions(GraphInvariantCheckOptions options)
    {
        if (options.PageSize <= 0 || options.PageSize > MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(options), $"PageSize must be between 1 and {MaximumPageSize}.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxScannedEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxPointLookups);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxIssues);
    }

    private static bool IsCorruptionException(Exception exception)
        => exception is InvalidDataException or ArgumentException or OverflowException;

    private static string PreviewKey(ReadOnlySpan<byte> key)
    {
        int count = Math.Min(key.Length, KeyPreviewBytes);
        string preview = Convert.ToHexString(key[..count]);
        return count == key.Length ? preview : preview + $"...({key.Length} bytes)";
    }

    private static string LimitText(string text)
        => text.Length <= MaximumIssueTextLength
            ? text
            : text[..MaximumIssueTextLength];

    private static string ElementKey(string kind, long id) => kind + ":" + id;

    private static string ElementDescription(GraphStorageKey key)
        => (key.Kind is GraphKeyKind.VertexLabel or GraphKeyKind.VertexPropertyIndex ? "vertex " : "edge ")
            + key.ElementId.Value;

    private enum ScanResult
    {
        Complete,
        ScanLimitExceeded,
        PointLookupLimitExceeded,
    }

    private sealed class CheckState
    {
        private readonly int _maximumIssues;
        private readonly long _maximumPointLookups;
        private readonly List<GraphInvariantIssue> _issues;
        private long _totalIssueCount;

        internal CheckState(int maximumIssues, long maximumPointLookups)
        {
            _maximumIssues = maximumIssues;
            _maximumPointLookups = maximumPointLookups;
            _issues = new List<GraphInvariantIssue>(Math.Min(maximumIssues, 100));
        }

        internal Dictionary<GraphHighWaterKind, long> HighWater { get; } = [];
        internal long ScannedEntries { get; set; }
        internal long PointLookupCount { get; private set; }
        internal long VertexCount { get; private set; }
        internal long EdgeCount { get; private set; }
        internal long OutgoingAdjacencyCount { get; private set; }
        internal long IncomingAdjacencyCount { get; private set; }
        internal long LabelIndexCount { get; private set; }
        internal long PropertyIndexCount { get; private set; }
        internal long MaximumVertexId { get; private set; }
        internal long MaximumEdgeId { get; private set; }
        internal long MaximumLabelId { get; private set; }
        internal long MaximumPropertyId { get; private set; }

        internal bool TryBeginPointLookup()
        {
            if (PointLookupCount >= _maximumPointLookups)
                return false;
            PointLookupCount++;
            return true;
        }

        internal void ObserveVertex(GraphVertexRecord vertex)
        {
            VertexCount++;
            MaximumVertexId = Math.Max(MaximumVertexId, vertex.Id.Value);
            foreach (LabelId label in vertex.Labels)
                MaximumLabelId = Math.Max(MaximumLabelId, label.Value);
            foreach (GraphProperty property in vertex.Properties)
                MaximumPropertyId = Math.Max(MaximumPropertyId, property.PropertyId);
        }

        internal void ObserveEdge(GraphEdgeRecord edge)
        {
            EdgeCount++;
            MaximumEdgeId = Math.Max(MaximumEdgeId, edge.Id.Value);
            MaximumVertexId = Math.Max(MaximumVertexId, Math.Max(edge.SourceId.Value, edge.TargetId.Value));
            MaximumLabelId = Math.Max(MaximumLabelId, edge.LabelId.Value);
            foreach (GraphProperty property in edge.Properties)
                MaximumPropertyId = Math.Max(MaximumPropertyId, property.PropertyId);
        }

        internal void ObserveAdjacency(GraphStorageKey key)
        {
            if (key.Kind == GraphKeyKind.OutgoingAdjacency)
                OutgoingAdjacencyCount++;
            else
                IncomingAdjacencyCount++;
            MaximumEdgeId = Math.Max(MaximumEdgeId, key.EdgeId.Value);
            MaximumVertexId = Math.Max(MaximumVertexId, Math.Max(key.SourceId.Value, key.TargetId.Value));
            MaximumLabelId = Math.Max(MaximumLabelId, key.LabelId.Value);
        }

        internal void ObserveLabelIndex(GraphStorageKey key)
        {
            LabelIndexCount++;
            if (key.Kind == GraphKeyKind.VertexLabel)
                MaximumVertexId = Math.Max(MaximumVertexId, key.ElementId.Value);
            else
                MaximumEdgeId = Math.Max(MaximumEdgeId, key.ElementId.Value);
            MaximumLabelId = Math.Max(MaximumLabelId, key.LabelId.Value);
        }

        internal void ObservePropertyIndex(GraphStorageKey key)
        {
            PropertyIndexCount++;
            if (key.Kind == GraphKeyKind.VertexPropertyIndex)
                MaximumVertexId = Math.Max(MaximumVertexId, key.ElementId.Value);
            else if (key.Kind == GraphKeyKind.EdgePropertyIndex)
                MaximumEdgeId = Math.Max(MaximumEdgeId, key.ElementId.Value);
            MaximumLabelId = Math.Max(MaximumLabelId, key.LabelId.Value);
            MaximumPropertyId = Math.Max(MaximumPropertyId, key.PropertyId);
        }

        internal void AddIssue(GraphInvariantIssueKind kind, string key, string message)
        {
            _totalIssueCount++;
            if (_issues.Count >= _maximumIssues)
                return;
            _issues.Add(new GraphInvariantIssue(kind, key, LimitText(message)));
        }

        internal GraphInvariantReport CreateReport(long snapshotSequence, bool complete)
        {
            int suppressed = _totalIssueCount - _issues.Count > int.MaxValue
                ? int.MaxValue
                : (int)(_totalIssueCount - _issues.Count);
            return new GraphInvariantReport(
                complete && _totalIssueCount == 0,
                complete,
                snapshotSequence,
                ScannedEntries,
                PointLookupCount,
                VertexCount,
                EdgeCount,
                OutgoingAdjacencyCount,
                IncomingAdjacencyCount,
                LabelIndexCount,
                PropertyIndexCount,
                new GraphHighWaterSnapshot(
                    GetHighWater(GraphHighWaterKind.VertexId),
                    GetHighWater(GraphHighWaterKind.EdgeId),
                    GetHighWater(GraphHighWaterKind.LabelId),
                    GetHighWater(GraphHighWaterKind.PropertyId)),
                _totalIssueCount,
                suppressed,
                _issues.AsReadOnly());
        }

        private long? GetHighWater(GraphHighWaterKind kind)
            => HighWater.TryGetValue(kind, out long value) ? value : null;
    }
}
