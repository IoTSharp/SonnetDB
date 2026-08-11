using SonnetDB.Graphs.Storage;
using SonnetDB.Kv;

namespace SonnetDB.Graphs;

internal static class GraphMaintenanceRunner
{
    private static readonly GraphMaintenancePhase[] RemovalPhases =
    [
        GraphMaintenancePhase.RemoveOutgoingAdjacency,
        GraphMaintenancePhase.RemoveIncomingAdjacency,
        GraphMaintenancePhase.RemoveVertexLabels,
        GraphMaintenancePhase.RemoveEdgeLabels,
        GraphMaintenancePhase.RemoveVertexProperties,
        GraphMaintenancePhase.RemoveEdgeProperties,
    ];

    internal static GraphMaintenanceState LoadOrCreate(
        string manifestPath,
        Guid storageId,
        KvKeyspace keyspace,
        GraphMaintenanceOptions options,
        out bool resumed)
    {
        GraphMaintenanceState? state = GraphMaintenanceManifestCodec.Load(manifestPath, storageId);
        resumed = state is not null;
        if (state is not null)
        {
            if (options.UniqueIndexes.Count > 0)
            {
                List<GraphUniqueIndexDefinition> supplied = options.UniqueIndexes.Distinct().ToList();
                GraphMaintenanceManifestCodec.SortDefinitions(supplied);
                if (supplied.Any(item => !state.UniqueDefinitions.Contains(item)))
                {
                    throw new InvalidOperationException(
                        "Graph maintenance 已有未完成任务；续作时不能更改 durable unique index 声明。");
                }
            }
            return state;
        }

        List<GraphUniqueIndexDefinition> definitions = options.UniqueIndexes.Distinct().ToList();
        GraphMaintenanceManifestCodec.SortDefinitions(definitions);
        state = new GraphMaintenanceState
        {
            StorageId = storageId,
            OperationId = Guid.NewGuid(),
            Phase = GraphMaintenancePhase.RepairVertices,
            SourceSequence = keyspace.LastSequence,
            LastSequence = keyspace.LastSequence,
            UniqueDefinitions = definitions,
            MaxUniqueIndexDefinitions = options.MaxUniqueIndexDefinitions,
            CompactOnCompletion = options.CompactOnCompletion,
        };
        GraphMaintenanceManifestCodec.Save(manifestPath, state);
        return state;
    }

    internal static void RunNextWorkUnit(
        KvKeyspace keyspace,
        GraphMaintenanceState state,
        GraphMaintenanceOptions options,
        CancellationToken cancellationToken)
    {
        while (state.Phase != GraphMaintenancePhase.Completed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool completedWork = state.Phase switch
            {
                GraphMaintenancePhase.RepairVertices => RepairRecordPage(keyspace, state, options, isVertex: true, cancellationToken),
                GraphMaintenancePhase.RepairEdges => RepairRecordPage(keyspace, state, options, isVertex: false, cancellationToken),
                GraphMaintenancePhase.RemoveOutgoingAdjacency
                    or GraphMaintenancePhase.RemoveIncomingAdjacency
                    or GraphMaintenancePhase.RemoveVertexLabels
                    or GraphMaintenancePhase.RemoveEdgeLabels
                    or GraphMaintenancePhase.RemoveVertexProperties
                    or GraphMaintenancePhase.RemoveEdgeProperties
                    => RemoveDerivedPage(keyspace, state, options, cancellationToken),
                GraphMaintenancePhase.CollectVertexUniqueDefinitions
                    => CollectUniqueDefinitionPage(keyspace, state, options, GraphKeyKind.VertexUniqueProperty, cancellationToken),
                GraphMaintenancePhase.CollectEdgeUniqueDefinitions
                    => CollectUniqueDefinitionPage(keyspace, state, options, GraphKeyKind.EdgeUniqueProperty, cancellationToken),
                GraphMaintenancePhase.ValidateUniqueIndexes
                    => ValidateUniquePage(keyspace, state, options, cancellationToken),
                GraphMaintenancePhase.RepairUniqueIndexes
                    => RepairUniquePage(keyspace, state, options, cancellationToken),
                GraphMaintenancePhase.RemoveVertexUniqueIndexes
                    => RemoveUniquePage(keyspace, state, options, GraphKeyKind.VertexUniqueProperty, cancellationToken),
                GraphMaintenancePhase.RemoveEdgeUniqueIndexes
                    => RemoveUniquePage(keyspace, state, options, GraphKeyKind.EdgeUniqueProperty, cancellationToken),
                GraphMaintenancePhase.Checkpoint => RunFinalCheckpoint(keyspace, state),
                GraphMaintenancePhase.Compaction => RunCompaction(keyspace, state),
                _ => throw new InvalidDataException($"Graph maintenance phase {state.Phase} 无效。"),
            };
            if (completedWork)
            {
                state.WorkUnits = checked(state.WorkUnits + 1);
                return;
            }
        }
    }

    internal static void RunPeriodicCheckpointIfDue(
        KvKeyspace keyspace,
        GraphMaintenanceState state,
        GraphMaintenanceOptions options)
    {
        if (options.CheckpointEveryWorkUnits == 0
            || state.WorkUnits == 0
            || state.WorkUnits % options.CheckpointEveryWorkUnits != 0
            || state.Phase is GraphMaintenancePhase.Checkpoint
                or GraphMaintenancePhase.Compaction
                or GraphMaintenancePhase.Completed)
        {
            return;
        }

        state.LastSequence = Math.Max(state.LastSequence, keyspace.CreateSnapshot());
        state.CheckpointCount = checked(state.CheckpointCount + 1);
    }

    private static bool RepairRecordPage(
        KvKeyspace keyspace,
        GraphMaintenanceState state,
        GraphMaintenanceOptions options,
        bool isVertex,
        CancellationToken cancellationToken)
    {
        byte[] prefix = isVertex
            ? GraphKeyCodec.VertexRecordPrefix()
            : GraphKeyCodec.EdgeRecordPrefix();
        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        IReadOnlyList<KvEntry> page = ReadPage(snapshot, prefix, state.AfterKey, options, cancellationToken);
        if (page.Count == 0)
        {
            Advance(state, isVertex
                ? GraphMaintenancePhase.RepairEdges
                : GraphMaintenancePhase.RemoveOutgoingAdjacency);
            return false;
        }

        var mutations = new List<KvBatchMutation>();
        int processedEntries = 0;
        foreach (KvEntry entry in page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isVertex)
            {
                GraphVertexRecord record = GraphElementRecordCodec.DecodeVertex(entry.Value.Span);
                if (GraphKeyCodec.Decode(entry.Key.Span).ElementId != record.Id)
                    throw new InvalidDataException("Graph vertex record key 与 payload ID 不一致，不能维护索引。");
                int maximumMutations = checked(record.Labels.Count * checked(record.Properties.Count + 1));
                if (!CanAppendRecord(maximumMutations, processedEntries, mutations.Count, options))
                    break;
                AppendVertexEntries(snapshot, mutations, record);
            }
            else
            {
                GraphEdgeRecord record = GraphElementRecordCodec.DecodeEdge(entry.Value.Span);
                if (GraphKeyCodec.Decode(entry.Key.Span).ElementId != record.Id)
                    throw new InvalidDataException("Graph edge record key 与 payload ID 不一致，不能维护索引。");
                int maximumMutations = checked(record.Properties.Count + 3);
                if (!CanAppendRecord(maximumMutations, processedEntries, mutations.Count, options))
                    break;
                AppendEdgeEntries(snapshot, mutations, record);
            }
            processedEntries++;
            if (mutations.Count >= options.MaxMutationsPerWorkUnit)
                break;
        }

        ApplyMutations(keyspace, state, mutations, options, repaired: true);
        state.ScannedRecords = checked(state.ScannedRecords + processedEntries);
        state.AfterKey = page[processedEntries - 1].Key.ToArray();
        return true;
    }

    private static bool RemoveDerivedPage(
        KvKeyspace keyspace,
        GraphMaintenanceState state,
        GraphMaintenanceOptions options,
        CancellationToken cancellationToken)
    {
        GraphKeyKind family = ToDerivedFamily(state.Phase);
        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        IReadOnlyList<KvEntry> page = ReadPage(
            snapshot,
            GraphKeyCodec.FamilyPrefix(family),
            state.AfterKey,
            options,
            cancellationToken,
            options.MaxMutationsPerWorkUnit);
        if (page.Count == 0)
        {
            int index = Array.IndexOf(RemovalPhases, state.Phase);
            Advance(state, index == RemovalPhases.Length - 1
                ? GraphMaintenancePhase.CollectVertexUniqueDefinitions
                : RemovalPhases[index + 1]);
            return false;
        }

        var mutations = new List<KvBatchMutation>();
        foreach (KvEntry entry in page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentDerivedEntry(snapshot, entry))
                mutations.Add(KvBatchMutation.Delete(entry.Key.ToArray()));
        }
        ApplyMutations(keyspace, state, mutations, options, repaired: false);
        state.AfterKey = page[^1].Key.ToArray();
        return true;
    }

    private static bool CollectUniqueDefinitionPage(
        KvKeyspace keyspace,
        GraphMaintenanceState state,
        GraphMaintenanceOptions options,
        GraphKeyKind family,
        CancellationToken cancellationToken)
    {
        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        IReadOnlyList<KvEntry> page = ReadPage(
            snapshot,
            GraphKeyCodec.FamilyPrefix(family),
            state.AfterKey,
            options,
            cancellationToken);
        if (page.Count == 0)
        {
            if (family == GraphKeyKind.VertexUniqueProperty)
            {
                Advance(state, GraphMaintenancePhase.CollectEdgeUniqueDefinitions);
            }
            else
            {
                GraphMaintenanceManifestCodec.SortDefinitions(state.UniqueDefinitions);
                Advance(state, GraphMaintenancePhase.ValidateUniqueIndexes);
            }
            return false;
        }

        foreach (KvEntry entry in page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                GraphStorageKey key = GraphKeyCodec.Decode(entry.Key.Span);
                var definition = new GraphUniqueIndexDefinition(
                    family == GraphKeyKind.VertexUniqueProperty
                        ? GraphElementType.Vertex
                        : GraphElementType.Edge,
                    key.LabelId,
                    key.PropertyId);
                if (!state.UniqueDefinitions.Contains(definition))
                {
                    if (state.UniqueDefinitions.Count >= state.MaxUniqueIndexDefinitions)
                        throw new InvalidOperationException("Graph unique index 声明超过维护任务的 durable 预算。");
                    state.UniqueDefinitions.Add(definition);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                // Malformed unique entries are removed in the final unique cleanup phases.
            }
        }
        state.AfterKey = page[^1].Key.ToArray();
        return true;
    }

    private static bool ValidateUniquePage(
        KvKeyspace keyspace,
        GraphMaintenanceState state,
        GraphMaintenanceOptions options,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentDefinition(state, GraphMaintenancePhase.RepairUniqueIndexes, out GraphUniqueIndexDefinition definition))
            return false;

        GraphElementKind elementKind = ToElementKind(definition.ElementType);
        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        IReadOnlyList<KvEntry> page = ReadPage(
            snapshot,
            GraphKeyCodec.PropertyIndexFamilyPrefix(elementKind, definition.LabelId, definition.PropertyId),
            state.AfterKey,
            options,
            cancellationToken);
        if (page.Count == 0)
        {
            state.UniqueDefinitionIndex++;
            state.AfterKey = [];
            state.PreviousUniqueKey = [];
            return false;
        }

        GraphStorageKey? previous = state.PreviousUniqueKey.Length == 0
            ? null
            : GraphKeyCodec.Decode(state.PreviousUniqueKey);
        foreach (KvEntry entry in page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphStorageKey current = GraphKeyCodec.Decode(entry.Key.Span);
            if (previous is { } prior
                && prior.PropertyValue == current.PropertyValue
                && prior.ElementId != current.ElementId)
            {
                throw new GraphUniqueConstraintException(definition.LabelId, definition.PropertyId, prior.ElementId);
            }
            previous = current;
            state.PreviousUniqueKey = entry.Key.ToArray();
        }
        state.AfterKey = page[^1].Key.ToArray();
        return true;
    }

    private static bool RepairUniquePage(
        KvKeyspace keyspace,
        GraphMaintenanceState state,
        GraphMaintenanceOptions options,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentDefinition(state, GraphMaintenancePhase.RemoveVertexUniqueIndexes, out GraphUniqueIndexDefinition definition))
            return false;

        GraphElementKind elementKind = ToElementKind(definition.ElementType);
        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        IReadOnlyList<KvEntry> page = ReadPage(
            snapshot,
            GraphKeyCodec.PropertyIndexFamilyPrefix(elementKind, definition.LabelId, definition.PropertyId),
            state.AfterKey,
            options,
            cancellationToken,
            options.MaxMutationsPerWorkUnit);
        if (page.Count == 0)
        {
            state.UniqueDefinitionIndex++;
            state.AfterKey = [];
            return false;
        }

        var mutations = new List<KvBatchMutation>(page.Count);
        foreach (KvEntry entry in page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphStorageKey key = GraphKeyCodec.Decode(entry.Key.Span);
            AppendIfMissing(
                snapshot,
                mutations,
                GraphKeyCodec.EncodeUniqueProperty(elementKind, key.LabelId, key.PropertyId, key.PropertyValue),
                GraphUniquePropertyOwnerCodec.Encode(elementKind, key.ElementId));
        }
        ApplyMutations(keyspace, state, mutations, options, repaired: true);
        state.AfterKey = page[^1].Key.ToArray();
        return true;
    }

    private static bool RemoveUniquePage(
        KvKeyspace keyspace,
        GraphMaintenanceState state,
        GraphMaintenanceOptions options,
        GraphKeyKind family,
        CancellationToken cancellationToken)
    {
        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        IReadOnlyList<KvEntry> page = ReadPage(
            snapshot,
            GraphKeyCodec.FamilyPrefix(family),
            state.AfterKey,
            options,
            cancellationToken,
            options.MaxMutationsPerWorkUnit);
        if (page.Count == 0)
        {
            Advance(state, family == GraphKeyKind.VertexUniqueProperty
                ? GraphMaintenancePhase.RemoveEdgeUniqueIndexes
                : GraphMaintenancePhase.Checkpoint);
            return false;
        }

        var definitions = new HashSet<GraphUniqueIndexDefinition>(state.UniqueDefinitions);
        var mutations = new List<KvBatchMutation>();
        foreach (KvEntry entry in page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool valid = false;
            try
            {
                GraphStorageKey key = GraphKeyCodec.Decode(entry.Key.Span);
                GraphElementType type = family == GraphKeyKind.VertexUniqueProperty
                    ? GraphElementType.Vertex
                    : GraphElementType.Edge;
                var definition = new GraphUniqueIndexDefinition(type, key.LabelId, key.PropertyId);
                GraphElementKind elementKind = ToElementKind(type);
                GraphElementId owner = GraphUniquePropertyOwnerCodec.Decode(entry.Value.Span, elementKind);
                valid = snapshot.GetEntry(GraphKeyCodec.EncodePropertyIndex(
                    elementKind,
                    key.LabelId,
                    key.PropertyId,
                    key.PropertyValue,
                    owner)) is not null;
                if (valid && !definitions.Contains(definition))
                {
                    if (state.UniqueDefinitions.Count >= state.MaxUniqueIndexDefinitions)
                        throw new InvalidOperationException("Graph unique index 声明超过维护任务的 durable 预算。");
                    state.UniqueDefinitions.Add(definition);
                    definitions.Add(definition);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                valid = false;
            }
            if (!valid)
                mutations.Add(KvBatchMutation.Delete(entry.Key.ToArray()));
        }
        ApplyMutations(keyspace, state, mutations, options, repaired: false);
        state.AfterKey = page[^1].Key.ToArray();
        return true;
    }

    private static bool RunFinalCheckpoint(KvKeyspace keyspace, GraphMaintenanceState state)
    {
        state.LastSequence = Math.Max(state.LastSequence, keyspace.CreateSnapshot());
        state.CheckpointCount = checked(state.CheckpointCount + 1);
        Advance(state, state.CompactOnCompletion
            ? GraphMaintenancePhase.Compaction
            : GraphMaintenancePhase.Completed);
        return true;
    }

    private static bool RunCompaction(KvKeyspace keyspace, GraphMaintenanceState state)
    {
        state.LastSequence = Math.Max(state.LastSequence, keyspace.Compact());
        Advance(state, GraphMaintenancePhase.Completed);
        return true;
    }

    private static IReadOnlyList<KvEntry> ReadPage(
        KvReadSnapshot snapshot,
        byte[] prefix,
        byte[] afterKey,
        GraphMaintenanceOptions options,
        CancellationToken cancellationToken,
        int? maximumEntries = null)
    {
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = prefix,
            AfterKey = afterKey,
            PageSize = Math.Min(options.PageSize, maximumEntries ?? options.PageSize),
            MaxPageBytes = options.MaxPageBytes,
        });
        return cursor.ReadNextPage(cancellationToken);
    }

    private static void ApplyMutations(
        KvKeyspace keyspace,
        GraphMaintenanceState state,
        List<KvBatchMutation> mutations,
        GraphMaintenanceOptions options,
        bool repaired)
    {
        if (mutations.Count == 0)
            return;
        if (mutations.Count > options.MaxMutationsPerWorkUnit)
        {
            throw new GraphMaintenanceLimitExceededException(
                $"Graph maintenance work unit 生成 {mutations.Count} 个 mutation，超过 MaxMutationsPerWorkUnit={options.MaxMutationsPerWorkUnit}。");
        }
        using IDisposable budgetScope = keyspace.EnterIndexRebuildBudgetScope();
        long sequence = keyspace.ApplyIndexRebuildBatch(mutations);
        keyspace.SyncWalForMaintenance();
        state.LastSequence = Math.Max(state.LastSequence, sequence);
        if (repaired)
            state.RepairedEntries = checked(state.RepairedEntries + mutations.Count);
        else
            state.RemovedEntries = checked(state.RemovedEntries + mutations.Count);
    }

    private static bool CanAppendRecord(
        int maximumRecordMutations,
        int processedEntries,
        int currentMutations,
        GraphMaintenanceOptions options)
    {
        if (maximumRecordMutations > options.MaxMutationsPerWorkUnit)
        {
            throw new GraphMaintenanceLimitExceededException(
                $"单条 Graph record 最多可展开 {maximumRecordMutations} 个派生 mutation，超过 MaxMutationsPerWorkUnit={options.MaxMutationsPerWorkUnit}。");
        }
        return processedEntries == 0
            || currentMutations <= options.MaxMutationsPerWorkUnit - maximumRecordMutations;
    }

    private static bool TryGetCurrentDefinition(
        GraphMaintenanceState state,
        GraphMaintenancePhase nextPhase,
        out GraphUniqueIndexDefinition definition)
    {
        if (state.UniqueDefinitionIndex < state.UniqueDefinitions.Count)
        {
            definition = state.UniqueDefinitions[state.UniqueDefinitionIndex];
            return true;
        }

        state.UniqueDefinitionIndex = 0;
        state.AfterKey = [];
        state.PreviousUniqueKey = [];
        state.Phase = nextPhase;
        definition = default;
        return false;
    }

    private static void AppendVertexEntries(
        KvReadSnapshot snapshot,
        List<KvBatchMutation> mutations,
        GraphVertexRecord record)
    {
        foreach (LabelId label in record.Labels)
        {
            AppendIfMissing(
                snapshot,
                mutations,
                GraphKeyCodec.EncodeLabelMembership(GraphElementKind.Vertex, label, record.Id),
                []);
            foreach (GraphProperty property in record.Properties)
            {
                AppendIfMissing(
                    snapshot,
                    mutations,
                    GraphKeyCodec.EncodePropertyIndex(
                        GraphElementKind.Vertex,
                        label,
                        property.PropertyId,
                        property.Value,
                        record.Id),
                    []);
            }
        }
    }

    private static void AppendEdgeEntries(
        KvReadSnapshot snapshot,
        List<KvBatchMutation> mutations,
        GraphEdgeRecord record)
    {
        AppendIfMissing(
            snapshot,
            mutations,
            GraphKeyCodec.EncodeOutgoingAdjacency(record.SourceId, record.LabelId, record.TargetId, record.Id),
            []);
        AppendIfMissing(
            snapshot,
            mutations,
            GraphKeyCodec.EncodeIncomingAdjacency(record.TargetId, record.LabelId, record.SourceId, record.Id),
            []);
        AppendIfMissing(
            snapshot,
            mutations,
            GraphKeyCodec.EncodeLabelMembership(GraphElementKind.Edge, record.LabelId, record.Id),
            []);
        foreach (GraphProperty property in record.Properties)
        {
            AppendIfMissing(
                snapshot,
                mutations,
                GraphKeyCodec.EncodePropertyIndex(
                    GraphElementKind.Edge,
                    record.LabelId,
                    property.PropertyId,
                    property.Value,
                    record.Id),
                []);
        }
    }

    private static void AppendIfMissing(
        KvReadSnapshot snapshot,
        List<KvBatchMutation> mutations,
        byte[] key,
        byte[] value)
    {
        KvEntry? existing = snapshot.GetEntry(key);
        if (existing is not null && existing.Value.Span.SequenceEqual(value))
            return;
        mutations.Add(KvBatchMutation.Put(key, value));
    }

    private static bool IsCurrentDerivedEntry(KvReadSnapshot snapshot, KvEntry entry)
    {
        GraphStorageKey key;
        try
        {
            key = GraphKeyCodec.Decode(entry.Key.Span);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        return key.Kind switch
        {
            GraphKeyKind.OutgoingAdjacency or GraphKeyKind.IncomingAdjacency
                => IsCurrentAdjacency(snapshot, key),
            GraphKeyKind.VertexLabel
                => ReadVertexIfPresent(snapshot, key.ElementId)?.Labels.Contains(key.LabelId) == true,
            GraphKeyKind.EdgeLabel
                => ReadEdgeIfPresent(snapshot, key.ElementId)?.LabelId == key.LabelId,
            GraphKeyKind.VertexPropertyIndex
                => HasVertexProperty(snapshot, key),
            GraphKeyKind.EdgePropertyIndex
                => HasEdgeProperty(snapshot, key),
            _ => false,
        };
    }

    private static bool IsCurrentAdjacency(KvReadSnapshot snapshot, GraphStorageKey key)
    {
        GraphEdgeRecord? edge = ReadEdgeIfPresent(snapshot, key.EdgeId);
        return edge is not null
            && edge.SourceId == key.SourceId
            && edge.TargetId == key.TargetId
            && edge.LabelId == key.LabelId;
    }

    private static bool HasVertexProperty(KvReadSnapshot snapshot, GraphStorageKey key)
    {
        GraphVertexRecord? record = ReadVertexIfPresent(snapshot, key.ElementId);
        return record is not null
            && record.Labels.Contains(key.LabelId)
            && record.Properties.Any(property =>
                property.PropertyId == key.PropertyId && property.Value == key.PropertyValue);
    }

    private static bool HasEdgeProperty(KvReadSnapshot snapshot, GraphStorageKey key)
    {
        GraphEdgeRecord? record = ReadEdgeIfPresent(snapshot, key.ElementId);
        return record is not null
            && record.LabelId == key.LabelId
            && record.Properties.Any(property =>
                property.PropertyId == key.PropertyId && property.Value == key.PropertyValue);
    }

    private static GraphVertexRecord? ReadVertexIfPresent(KvReadSnapshot snapshot, GraphElementId id)
    {
        KvEntry? entry = snapshot.GetEntry(GraphKeyCodec.EncodeVertexRecord(id));
        return entry is null ? null : GraphElementRecordCodec.DecodeVertex(entry.Value.Span);
    }

    private static GraphEdgeRecord? ReadEdgeIfPresent(KvReadSnapshot snapshot, GraphElementId id)
    {
        KvEntry? entry = snapshot.GetEntry(GraphKeyCodec.EncodeEdgeRecord(id));
        return entry is null ? null : GraphElementRecordCodec.DecodeEdge(entry.Value.Span);
    }

    private static GraphKeyKind ToDerivedFamily(GraphMaintenancePhase phase)
        => phase switch
        {
            GraphMaintenancePhase.RemoveOutgoingAdjacency => GraphKeyKind.OutgoingAdjacency,
            GraphMaintenancePhase.RemoveIncomingAdjacency => GraphKeyKind.IncomingAdjacency,
            GraphMaintenancePhase.RemoveVertexLabels => GraphKeyKind.VertexLabel,
            GraphMaintenancePhase.RemoveEdgeLabels => GraphKeyKind.EdgeLabel,
            GraphMaintenancePhase.RemoveVertexProperties => GraphKeyKind.VertexPropertyIndex,
            GraphMaintenancePhase.RemoveEdgeProperties => GraphKeyKind.EdgePropertyIndex,
            _ => throw new ArgumentOutOfRangeException(nameof(phase)),
        };

    private static GraphElementKind ToElementKind(GraphElementType elementType)
        => elementType switch
        {
            GraphElementType.Vertex => GraphElementKind.Vertex,
            GraphElementType.Edge => GraphElementKind.Edge,
            _ => throw new ArgumentOutOfRangeException(nameof(elementType)),
        };

    private static void Advance(GraphMaintenanceState state, GraphMaintenancePhase nextPhase)
    {
        state.Phase = nextPhase;
        state.AfterKey = [];
        state.PreviousUniqueKey = [];
        state.UniqueDefinitionIndex = 0;
    }
}
