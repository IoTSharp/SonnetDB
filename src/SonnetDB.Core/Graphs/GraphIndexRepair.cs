using SonnetDB.Graphs.Storage;
using SonnetDB.Kv;

namespace SonnetDB.Graphs;

/// <summary>用于重建 Graph 派生索引的唯一索引声明。</summary>
public readonly record struct GraphUniqueIndexDefinition
{
    /// <summary>创建唯一索引声明。</summary>
    /// <param name="elementType">声明作用于顶点或边。</param>
    /// <param name="labelId">元素上的标签标识符。</param>
    /// <param name="propertyId">必须唯一的属性标识符。</param>
    public GraphUniqueIndexDefinition(GraphElementType elementType, LabelId labelId, int propertyId)
    {
        if (!Enum.IsDefined(elementType))
            throw new ArgumentOutOfRangeException(nameof(elementType));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(propertyId);
        ElementType = elementType;
        LabelId = labelId;
        PropertyId = propertyId;
    }

    /// <summary>唯一索引作用的元素类别。</summary>
    public GraphElementType ElementType { get; }

    /// <summary>唯一索引作用的标签。</summary>
    public LabelId LabelId { get; }

    /// <summary>唯一索引作用的属性。</summary>
    public int PropertyId { get; }
}

/// <summary>Graph 派生索引重建选项。</summary>
public sealed record GraphIndexRebuildOptions
{
    /// <summary>
    /// 可选的唯一索引声明。冻结 V1 元素记录不保存声明，因此缺失的全部 unique key 只能由调用方重新提供。
    /// </summary>
    public IReadOnlyList<GraphUniqueIndexDefinition> UniqueIndexes { get; init; } = [];

    /// <summary>每页扫描和维护写入的首选条目数。</summary>
    public int PageSize { get; init; } = 256;

    /// <summary>允许从调用方或现存 unique key 收集的唯一索引声明上限。</summary>
    public int MaxUniqueIndexDefinitions { get; init; } = 10_000;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(UniqueIndexes);
        if (PageSize is <= 0 or > 4_096)
            throw new ArgumentOutOfRangeException(nameof(PageSize), "Graph index rebuild page size 必须在 1 到 4,096 之间。");
        if (MaxUniqueIndexDefinitions is <= 0 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(MaxUniqueIndexDefinitions));
        if (UniqueIndexes.Count > MaxUniqueIndexDefinitions)
            throw new ArgumentOutOfRangeException(nameof(UniqueIndexes), "Graph unique index 声明超过重建预算。");
        var seen = new HashSet<GraphUniqueIndexDefinition>();
        foreach (GraphUniqueIndexDefinition definition in UniqueIndexes)
        {
            if (!Enum.IsDefined(definition.ElementType)
                || definition.LabelId.Value <= 0
                || definition.PropertyId <= 0)
            {
                throw new ArgumentException("Graph unique index 声明无效。", nameof(UniqueIndexes));
            }
            if (!seen.Add(definition))
                throw new ArgumentException("Graph unique index 声明不能重复。", nameof(UniqueIndexes));
        }
    }
}

/// <summary>Graph 派生索引重建结果。</summary>
public sealed record GraphIndexRebuildResult
{
    internal GraphIndexRebuildResult(
        long sequence,
        long scannedRecords,
        long repairedEntries,
        long removedEntries,
        int uniqueIndexDefinitionCount,
        bool uniqueDeclarationsWereSupplied)
    {
        Sequence = sequence;
        ScannedRecords = scannedRecords;
        RepairedEntries = repairedEntries;
        RemovedEntries = removedEntries;
        UniqueIndexDefinitionCount = uniqueIndexDefinitionCount;
        UniqueDeclarationsWereSupplied = uniqueDeclarationsWereSupplied;
    }

    /// <summary>最后一次维护批次的序列号。</summary>
    public long Sequence { get; }

    /// <summary>扫描的 vertex/edge 主记录数。</summary>
    public long ScannedRecords { get; }

    /// <summary>补写或覆盖的派生索引/邻接条目数。</summary>
    public long RepairedEntries { get; }

    /// <summary>删除的 orphan/stale 派生条目数。</summary>
    public long RemovedEntries { get; }

    /// <summary>本次使用的唯一索引声明数量。</summary>
    public int UniqueIndexDefinitionCount { get; }

    /// <summary>是否由调用方提供了唯一索引声明；否则只可修复仍可从现存 unique key 推断的声明。</summary>
    public bool UniqueDeclarationsWereSupplied { get; }
}

internal static class GraphIndexRepair
{
    private static readonly GraphKeyKind[] DerivedFamilies =
    [
        GraphKeyKind.OutgoingAdjacency,
        GraphKeyKind.IncomingAdjacency,
        GraphKeyKind.VertexLabel,
        GraphKeyKind.EdgeLabel,
        GraphKeyKind.VertexPropertyIndex,
        GraphKeyKind.EdgePropertyIndex,
    ];

    internal static GraphIndexRebuildResult Rebuild(
        KvKeyspace keyspace,
        GraphIndexRebuildOptions options,
        CancellationToken cancellationToken)
    {
        using IDisposable budgetScope = keyspace.EnterIndexRebuildBudgetScope();
        int mutationPageSize = keyspace.GetIndexRebuildBatchEntryLimit(options.PageSize);
        long scannedRecords = 0;
        long repairedEntries = 0;
        long removedEntries = 0;
        long sequence;
        using (KvReadSnapshot initialSnapshot = keyspace.AcquireReadSnapshot())
            sequence = initialSnapshot.Sequence;

        RepairExpectedDerivedEntries(
            keyspace,
            mutationPageSize,
            cancellationToken,
            ref scannedRecords,
            ref repairedEntries,
            ref sequence);
        RemoveUnexpectedDerivedEntries(
            keyspace,
            mutationPageSize,
            cancellationToken,
            ref removedEntries,
            ref sequence);

        HashSet<GraphUniqueIndexDefinition> definitions = CollectUniqueDefinitions(
            keyspace,
            cancellationToken,
            options.UniqueIndexes,
            options.MaxUniqueIndexDefinitions);
        ValidateUniqueDefinitions(keyspace, definitions, cancellationToken);
        RepairUniqueEntries(
            keyspace,
            mutationPageSize,
            definitions,
            cancellationToken,
            ref repairedEntries,
            ref sequence);
        RemoveUnexpectedUniqueEntries(
            keyspace,
            mutationPageSize,
            definitions,
            cancellationToken,
            ref removedEntries,
            ref sequence);

        return new GraphIndexRebuildResult(
            sequence,
            scannedRecords,
            repairedEntries,
            removedEntries,
            definitions.Count,
            options.UniqueIndexes.Count > 0);
    }

    private static void RepairExpectedDerivedEntries(
        KvKeyspace keyspace,
        int mutationPageSize,
        CancellationToken cancellationToken,
        ref long scannedRecords,
        ref long repairedEntries,
        ref long sequence)
    {
        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        RepairRecordFamily(
            snapshot,
            keyspace,
            GraphKeyCodec.VertexRecordPrefix(),
            isVertex: true,
            mutationPageSize,
            cancellationToken,
            ref scannedRecords,
            ref repairedEntries,
            ref sequence);
        RepairRecordFamily(
            snapshot,
            keyspace,
            GraphKeyCodec.EdgeRecordPrefix(),
            isVertex: false,
            mutationPageSize,
            cancellationToken,
            ref scannedRecords,
            ref repairedEntries,
            ref sequence);
    }

    private static void RepairRecordFamily(
        KvReadSnapshot snapshot,
        KvKeyspace keyspace,
        byte[] prefix,
        bool isVertex,
        int mutationPageSize,
        CancellationToken cancellationToken,
        ref long scannedRecords,
        ref long repairedEntries,
        ref long sequence)
    {
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = prefix,
            PageSize = mutationPageSize,
            MaxPageBytes = 32 * 1024 * 1024,
        });
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<KvEntry> page = cursor.ReadNextPage(cancellationToken);
            if (page.Count == 0)
                return;
            var mutations = new List<KvBatchMutation>();
            foreach (KvEntry entry in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (isVertex)
                {
                    GraphVertexRecord record = GraphElementRecordCodec.DecodeVertex(entry.Value.Span);
                    if (GraphKeyCodec.Decode(entry.Key.Span).ElementId != record.Id)
                        throw new InvalidDataException("Graph vertex record key 与 payload ID 不一致，不能重建索引。");
                    AppendVertexEntries(snapshot, mutations, record);
                }
                else
                {
                    GraphEdgeRecord record = GraphElementRecordCodec.DecodeEdge(entry.Value.Span);
                    if (GraphKeyCodec.Decode(entry.Key.Span).ElementId != record.Id)
                        throw new InvalidDataException("Graph edge record key 与 payload ID 不一致，不能重建索引。");
                    AppendEdgeEntries(snapshot, mutations, record);
                }
                scannedRecords++;
                FlushMutationsIfNeeded(keyspace, mutations, mutationPageSize, ref repairedEntries, ref sequence);
            }
            FlushMutations(keyspace, mutations, ref repairedEntries, ref sequence);
        }
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
                        GraphElementKind.Vertex, label, property.PropertyId, property.Value, record.Id),
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

    private static void RemoveUnexpectedDerivedEntries(
        KvKeyspace keyspace,
        int mutationPageSize,
        CancellationToken cancellationToken,
        ref long removedEntries,
        ref long sequence)
    {
        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        foreach (GraphKeyKind family in DerivedFamilies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
            {
                Prefix = GraphKeyCodec.FamilyPrefix(family),
                PageSize = mutationPageSize,
                MaxPageBytes = 32 * 1024 * 1024,
            });
            while (true)
            {
                IReadOnlyList<KvEntry> page = cursor.ReadNextPage(cancellationToken);
                if (page.Count == 0)
                    break;
                var mutations = new List<KvBatchMutation>();
                foreach (KvEntry entry in page)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentDerivedEntry(snapshot, entry))
                    {
                        mutations.Add(KvBatchMutation.Delete(entry.Key.ToArray()));
                        removedEntries++;
                    }
                }
                FlushMutations(keyspace, mutations, ref removedEntries, ref sequence, countAsRepair: false);
            }
        }
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
            GraphKeyKind.OutgoingAdjacency => IsCurrentOutgoing(snapshot, key),
            GraphKeyKind.IncomingAdjacency => IsCurrentIncoming(snapshot, key),
            GraphKeyKind.VertexLabel => HasVertexLabel(snapshot, key.ElementId, key.LabelId),
            GraphKeyKind.EdgeLabel => HasEdgeLabel(snapshot, key.ElementId, key.LabelId),
            GraphKeyKind.VertexPropertyIndex => HasVertexProperty(snapshot, key),
            GraphKeyKind.EdgePropertyIndex => HasEdgeProperty(snapshot, key),
            _ => false,
        };
    }

    private static bool IsCurrentOutgoing(KvReadSnapshot snapshot, GraphStorageKey key)
    {
        GraphEdgeRecord? edge = ReadEdgeIfPresent(snapshot, key.EdgeId);
        return edge is not null
            && edge.SourceId == key.SourceId
            && edge.TargetId == key.TargetId
            && edge.LabelId == key.LabelId;
    }

    private static bool IsCurrentIncoming(KvReadSnapshot snapshot, GraphStorageKey key)
    {
        GraphEdgeRecord? edge = ReadEdgeIfPresent(snapshot, key.EdgeId);
        return edge is not null
            && edge.SourceId == key.SourceId
            && edge.TargetId == key.TargetId
            && edge.LabelId == key.LabelId;
    }

    private static bool HasVertexLabel(KvReadSnapshot snapshot, GraphElementId id, LabelId label)
    {
        GraphVertexRecord? record = ReadVertexIfPresent(snapshot, id);
        return record is not null && record.Labels.Contains(label);
    }

    private static bool HasEdgeLabel(KvReadSnapshot snapshot, GraphElementId id, LabelId label)
    {
        GraphEdgeRecord? record = ReadEdgeIfPresent(snapshot, id);
        return record is not null && record.LabelId == label;
    }

    private static bool HasVertexProperty(KvReadSnapshot snapshot, GraphStorageKey key)
    {
        GraphVertexRecord? record = ReadVertexIfPresent(snapshot, key.ElementId);
        return record is not null
            && record.Labels.Contains(key.LabelId)
            && record.Properties.Any(property => property.PropertyId == key.PropertyId && property.Value == key.PropertyValue);
    }

    private static bool HasEdgeProperty(KvReadSnapshot snapshot, GraphStorageKey key)
    {
        GraphEdgeRecord? record = ReadEdgeIfPresent(snapshot, key.ElementId);
        return record is not null
            && record.LabelId == key.LabelId
            && record.Properties.Any(property => property.PropertyId == key.PropertyId && property.Value == key.PropertyValue);
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

    private static HashSet<GraphUniqueIndexDefinition> CollectUniqueDefinitions(
        KvKeyspace keyspace,
        CancellationToken cancellationToken,
        IReadOnlyList<GraphUniqueIndexDefinition> supplied,
        int maximumDefinitions)
    {
        var result = new HashSet<GraphUniqueIndexDefinition>(supplied);
        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        foreach (GraphKeyKind family in new[] { GraphKeyKind.VertexUniqueProperty, GraphKeyKind.EdgeUniqueProperty })
        {
            using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
            {
                Prefix = GraphKeyCodec.FamilyPrefix(family),
                PageSize = 512,
                MaxPageBytes = 32 * 1024 * 1024,
            });
            while (true)
            {
                IReadOnlyList<KvEntry> page = cursor.ReadNextPage(cancellationToken);
                if (page.Count == 0)
                    break;
                foreach (KvEntry entry in page)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        GraphStorageKey key = GraphKeyCodec.Decode(entry.Key.Span);
                        result.Add(new GraphUniqueIndexDefinition(
                            key.Kind == GraphKeyKind.VertexUniqueProperty ? GraphElementType.Vertex : GraphElementType.Edge,
                            key.LabelId,
                            key.PropertyId));
                        if (result.Count > maximumDefinitions)
                            throw new InvalidOperationException("Graph unique index 声明超过重建预算。");
                    }
                    catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
                    {
                        // malformed unique keys are removed in the final stale-key pass
                    }
                }
            }
        }
        return result;
    }

    private static void ValidateUniqueDefinitions(
        KvKeyspace keyspace,
        IReadOnlySet<GraphUniqueIndexDefinition> definitions,
        CancellationToken cancellationToken)
    {
        foreach (GraphUniqueIndexDefinition definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
            GraphElementKind elementKind = ToElementKind(definition.ElementType);
            using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
            {
                Prefix = GraphKeyCodec.PropertyIndexFamilyPrefix(elementKind, definition.LabelId, definition.PropertyId),
                PageSize = 512,
                MaxPageBytes = 32 * 1024 * 1024,
            });
            bool hasPrevious = false;
            GraphPropertyValue previousValue = default;
            GraphElementId previousOwner = default;
            while (true)
            {
                IReadOnlyList<KvEntry> page = cursor.ReadNextPage(cancellationToken);
                if (page.Count == 0)
                    break;
                foreach (KvEntry entry in page)
                {
                    GraphStorageKey key = GraphKeyCodec.Decode(entry.Key.Span);
                    if (key.PropertyValue == previousValue && hasPrevious && key.ElementId != previousOwner)
                        throw new GraphUniqueConstraintException(definition.LabelId, definition.PropertyId, previousOwner);
                    previousValue = key.PropertyValue;
                    previousOwner = key.ElementId;
                    hasPrevious = true;
                }
            }
        }
    }

    private static void RepairUniqueEntries(
        KvKeyspace keyspace,
        int mutationPageSize,
        IReadOnlySet<GraphUniqueIndexDefinition> definitions,
        CancellationToken cancellationToken,
        ref long repairedEntries,
        ref long sequence)
    {
        foreach (GraphUniqueIndexDefinition definition in definitions)
        {
            using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
            GraphElementKind elementKind = ToElementKind(definition.ElementType);
            using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
            {
                Prefix = GraphKeyCodec.PropertyIndexFamilyPrefix(elementKind, definition.LabelId, definition.PropertyId),
                PageSize = mutationPageSize,
                MaxPageBytes = 32 * 1024 * 1024,
            });
            while (true)
            {
                IReadOnlyList<KvEntry> page = cursor.ReadNextPage(cancellationToken);
                if (page.Count == 0)
                    break;
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
                FlushMutations(keyspace, mutations, ref repairedEntries, ref sequence);
            }
        }
    }

    private static void RemoveUnexpectedUniqueEntries(
        KvKeyspace keyspace,
        int mutationPageSize,
        IReadOnlySet<GraphUniqueIndexDefinition> definitions,
        CancellationToken cancellationToken,
        ref long removedEntries,
        ref long sequence)
    {
        using KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        foreach (GraphKeyKind family in new[] { GraphKeyKind.VertexUniqueProperty, GraphKeyKind.EdgeUniqueProperty })
        {
            using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
            {
                Prefix = GraphKeyCodec.FamilyPrefix(family),
                PageSize = mutationPageSize,
                MaxPageBytes = 32 * 1024 * 1024,
            });
            while (true)
            {
                IReadOnlyList<KvEntry> page = cursor.ReadNextPage(cancellationToken);
                if (page.Count == 0)
                    break;
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
                        if (definitions.Contains(definition))
                        {
                            GraphElementKind elementKind = ToElementKind(type);
                            GraphElementId owner = GraphUniquePropertyOwnerCodec.Decode(entry.Value.Span, elementKind);
                            byte[] propertyKey = GraphKeyCodec.EncodePropertyIndex(
                                elementKind,
                                key.LabelId,
                                key.PropertyId,
                                key.PropertyValue,
                                owner);
                            valid = snapshot.GetEntry(propertyKey) is not null;
                        }
                    }
                    catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
                    {
                        valid = false;
                    }
                    if (!valid)
                    {
                        mutations.Add(KvBatchMutation.Delete(entry.Key.ToArray()));
                        removedEntries++;
                    }
                }
                FlushMutations(keyspace, mutations, ref removedEntries, ref sequence, countAsRepair: false);
            }
        }
    }

    private static void FlushMutationsIfNeeded(
        KvKeyspace keyspace,
        List<KvBatchMutation> mutations,
        int mutationPageSize,
        ref long repairedEntries,
        ref long sequence)
    {
        if (mutations.Count >= mutationPageSize)
            FlushMutations(keyspace, mutations, ref repairedEntries, ref sequence);
    }

    private static void FlushMutations(
        KvKeyspace keyspace,
        List<KvBatchMutation> mutations,
        ref long count,
        ref long sequence,
        bool countAsRepair = true)
    {
        if (mutations.Count == 0)
            return;
        long appliedSequence = keyspace.ApplyIndexRebuildBatch(mutations);
        sequence = Math.Max(sequence, appliedSequence);
        if (countAsRepair)
            count = checked(count + mutations.Count);
        mutations.Clear();
    }

    private static GraphElementKind ToElementKind(GraphElementType elementType)
        => elementType switch
        {
            GraphElementType.Vertex => GraphElementKind.Vertex,
            GraphElementType.Edge => GraphElementKind.Edge,
            _ => throw new ArgumentOutOfRangeException(nameof(elementType)),
        };
}
