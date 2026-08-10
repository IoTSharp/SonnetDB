using System.Buffers.Binary;
using System.Security.Cryptography;
using SonnetDB.Graphs.Storage;
using SonnetDB.Kv;

namespace SonnetDB.Graphs;

/// <summary>Phase 0 graph transaction 的显式写预算。</summary>
internal sealed record GraphTransactionLimits
{
    internal int MaxKvMutations { get; init; } = 10_000;

    internal long MaxEncodedBytes { get; init; } = 64L * 1024 * 1024;

    internal static GraphTransactionLimits Default { get; } = new();

    internal void Validate()
    {
        if (MaxKvMutations <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxKvMutations));
        if (MaxEncodedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxEncodedBytes));
    }
}

/// <summary>Graph transaction 提交结果。</summary>
internal readonly record struct GraphCommitResult(long Sequence, bool IsDuplicate);

/// <summary>Graph element version 或 endpoint 条件冲突。</summary>
internal sealed class GraphConcurrencyException : InvalidOperationException
{
    internal GraphConcurrencyException(string message) : base(message) { }
}

/// <summary>Vertex 仍有 incoming/outgoing adjacency 时的 RESTRICT 错误。</summary>
internal sealed class GraphVertexDeleteRestrictedException : InvalidOperationException
{
    internal GraphVertexDeleteRestrictedException(GraphElementId vertexId)
        : base($"Graph vertex {vertexId} 仍有邻接边；Phase 0 delete 使用 RESTRICT。")
    {
        VertexId = vertexId;
    }

    internal GraphElementId VertexId { get; }
}

/// <summary>Graph transaction 超过显式 mutation/bytes 预算。</summary>
internal sealed class GraphTransactionLimitExceededException : InvalidOperationException
{
    internal GraphTransactionLimitExceededException(string message) : base(message) { }
}

/// <summary>同一 request ID 被不同 transaction 内容复用。</summary>
internal sealed class GraphRequestConflictException : InvalidOperationException
{
    internal GraphRequestConflictException(Guid requestId)
        : base($"Graph transaction request ID '{requestId:D}' 已绑定到不同内容。")
    {
        RequestId = requestId;
    }

    internal Guid RequestId { get; }
}

/// <summary>WAL append/fsync 已开始，必须通过 request ID 重试解析的未知提交结果。</summary>
internal sealed class GraphCommitOutcomeUnknownException : IOException
{
    internal GraphCommitOutcomeUnknownException(Guid requestId, Exception innerException)
        : base(
            $"Graph transaction '{requestId:D}' 的 WAL 提交结果未知；重开 graph 后使用相同 request ID 重试。",
            innerException)
    {
        RequestId = requestId;
    }

    internal Guid RequestId { get; }
}

/// <summary>
/// 单 graph、单 keyspace 的 Phase 0 乐观 transaction。该类型保持 internal，避免在 Phase 1 CRUD
/// 合同冻结前暴露不完整的产品 API。
/// </summary>
internal sealed class GraphTransaction
{
    private readonly GraphStore _store;
    private readonly Guid _requestId;
    private readonly GraphTransactionLimits _limits;
    private readonly List<GraphWriteOperation> _operations = [];
    private readonly HashSet<(GraphElementKind Kind, long Id)> _elements = [];
    private long _bufferedEncodedBytes;
    private bool _completed;

    internal GraphTransaction(
        GraphStore store,
        Guid requestId,
        GraphTransactionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (requestId == Guid.Empty)
            throw new ArgumentException("Graph transaction request ID 不能为空。", nameof(requestId));
        _limits = limits ?? GraphTransactionLimits.Default;
        _limits.Validate();
        _store = store;
        _requestId = requestId;
    }

    internal Guid RequestId => _requestId;

    internal void UpsertVertex(
        GraphElementId vertexId,
        long expectedElementVersion,
        IEnumerable<LabelId> labels,
        IEnumerable<GraphPropertyEntry> properties)
    {
        EnsureMutable();
        EnsureOperationCapacity();
        ArgumentOutOfRangeException.ThrowIfNegative(expectedElementVersion);
        long nextVersion = checked(expectedElementVersion + 1);
        var record = new GraphVertexRecord(vertexId, nextVersion, labels, properties);
        byte[] encodedRecord = GraphElementRecordCodec.EncodeVertex(record);
        long bufferedEncodedBytes = GetBufferedEncodedBytes(encodedRecord.Length);
        AddElement(GraphElementKind.Vertex, vertexId);
        _operations.Add(new UpsertVertexOperation(
            record,
            encodedRecord,
            expectedElementVersion));
        _bufferedEncodedBytes = bufferedEncodedBytes;
    }

    internal void UpsertEdge(
        GraphElementId edgeId,
        long expectedElementVersion,
        GraphElementId sourceId,
        GraphElementId targetId,
        LabelId labelId,
        IEnumerable<GraphPropertyEntry> properties)
    {
        EnsureMutable();
        EnsureOperationCapacity();
        ArgumentOutOfRangeException.ThrowIfNegative(expectedElementVersion);
        long nextVersion = checked(expectedElementVersion + 1);
        var record = new GraphEdgeRecord(
            edgeId,
            nextVersion,
            sourceId,
            targetId,
            labelId,
            properties);
        byte[] encodedRecord = GraphElementRecordCodec.EncodeEdge(record);
        long bufferedEncodedBytes = GetBufferedEncodedBytes(encodedRecord.Length);
        AddElement(GraphElementKind.Edge, edgeId);
        _operations.Add(new UpsertEdgeOperation(
            record,
            encodedRecord,
            expectedElementVersion));
        _bufferedEncodedBytes = bufferedEncodedBytes;
    }

    internal void DeleteVertex(GraphElementId vertexId, long expectedElementVersion)
    {
        EnsureMutable();
        EnsureOperationCapacity();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedElementVersion);
        AddElement(GraphElementKind.Vertex, vertexId);
        _operations.Add(new DeleteVertexOperation(vertexId, expectedElementVersion));
    }

    internal void DeleteEdge(GraphElementId edgeId, long expectedElementVersion)
    {
        EnsureMutable();
        EnsureOperationCapacity();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedElementVersion);
        AddElement(GraphElementKind.Edge, edgeId);
        _operations.Add(new DeleteEdgeOperation(edgeId, expectedElementVersion));
    }

    internal GraphCommitResult Commit(CancellationToken cancellationToken = default)
    {
        EnsureMutable();
        _completed = true;
        if (_operations.Count == 0)
            throw new InvalidOperationException("Graph transaction 至少需要一个 mutation。");
        cancellationToken.ThrowIfCancellationRequested();

        if (_operations.Count > _limits.MaxKvMutations)
        {
            throw new GraphTransactionLimitExceededException(
                $"Graph transaction 包含 {_operations.Count} 个 graph mutations，超过上限 {_limits.MaxKvMutations}。");
        }

        byte[] digest = ComputeDigest(_operations);
        KvKeyspace keyspace = _store.Keyspace;
        byte[] requestKey = GraphKeyCodec.EncodeTransactionRequest(_requestId);
        if (TryResolveDuplicate(keyspace, requestKey, digest, out GraphCommitResult duplicate))
            return duplicate;

        GraphCommitPlan plan = BuildCommitPlan(keyspace, requestKey, digest);
        EnforceLimits(plan.Mutations);
        _store.BeforeTransactionConditionalCommitTestHook?.Invoke();

        KvConditionalBatchResult result;
        try
        {
            result = _store.ApplyTransactionBatch(
                plan.Mutations,
                plan.Preconditions,
                cancellationToken);
        }
        catch (Exception exception) when (keyspace.IsWriteCommitOutcomeUnknown(exception))
        {
            throw new GraphCommitOutcomeUnknownException(_requestId, exception);
        }

        if (result.Applied)
            return new GraphCommitResult(result.Sequence, IsDuplicate: false);
        if (TryResolveDuplicate(keyspace, requestKey, digest, out duplicate))
            return duplicate;

        if (result.FailedPreconditionIndex >= 0
            && result.FailedPreconditionIndex < plan.Bindings.Count
            && plan.Bindings[result.FailedPreconditionIndex] is RestrictConditionBinding restrict)
        {
            throw new GraphVertexDeleteRestrictedException(restrict.VertexId);
        }

        throw new GraphConcurrencyException(
            "Graph transaction 的 element version、endpoint、metadata 或 request 条件已被并发写入改变。");
    }

    private GraphCommitPlan BuildCommitPlan(
        KvKeyspace keyspace,
        byte[] requestKey,
        byte[] digest)
    {
        var builder = new CommitPlanBuilder(_limits);
        var desiredVertices = _operations
            .OfType<UpsertVertexOperation>()
            .Select(static operation => operation.Record.Id.Value)
            .ToHashSet();
        var deletedVertices = _operations
            .OfType<DeleteVertexOperation>()
            .Select(static operation => operation.VertexId.Value)
            .ToHashSet();

        long vertexHighWater = 0;
        long edgeHighWater = 0;
        long labelHighWater = 0;
        long propertyHighWater = 0;

        foreach (GraphWriteOperation operation in _operations)
        {
            switch (operation)
            {
                case UpsertVertexOperation vertex:
                    ApplyVertexUpsert(keyspace, builder, vertex);
                    vertexHighWater = Math.Max(vertexHighWater, vertex.Record.Id.Value);
                    foreach (LabelId label in vertex.Record.Labels)
                        labelHighWater = Math.Max(labelHighWater, label.Value);
                    foreach (GraphPropertyEntry property in vertex.Record.Properties)
                        propertyHighWater = Math.Max(propertyHighWater, property.PropertyId);
                    break;
                case UpsertEdgeOperation edge:
                    EnsureEndpoint(
                        keyspace,
                        builder,
                        edge.Record.SourceId,
                        desiredVertices,
                        deletedVertices);
                    EnsureEndpoint(
                        keyspace,
                        builder,
                        edge.Record.TargetId,
                        desiredVertices,
                        deletedVertices);
                    ApplyEdgeUpsert(keyspace, builder, edge);
                    edgeHighWater = Math.Max(edgeHighWater, edge.Record.Id.Value);
                    labelHighWater = Math.Max(labelHighWater, edge.Record.LabelId.Value);
                    foreach (GraphPropertyEntry property in edge.Record.Properties)
                        propertyHighWater = Math.Max(propertyHighWater, property.PropertyId);
                    break;
                case DeleteVertexOperation deleteVertex:
                    ApplyVertexDelete(keyspace, builder, deleteVertex);
                    break;
                case DeleteEdgeOperation deleteEdge:
                    ApplyEdgeDelete(keyspace, builder, deleteEdge);
                    break;
                default:
                    throw new InvalidOperationException("未知 Graph transaction operation。");
            }
        }

        EnsureHighWater(keyspace, builder, GraphHighWaterKind.VertexId, vertexHighWater);
        EnsureHighWater(keyspace, builder, GraphHighWaterKind.EdgeId, edgeHighWater);
        EnsureHighWater(keyspace, builder, GraphHighWaterKind.LabelId, labelHighWater);
        EnsureHighWater(keyspace, builder, GraphHighWaterKind.PropertyId, propertyHighWater);

        builder.Put(requestKey, GraphTransactionRequestCodec.Encode(digest));
        builder.AddCondition(
            KvBatchPrecondition.KeyVersion(requestKey, expectedVersion: 0),
            new RequestConditionBinding());
        return builder.Build();
    }

    private static void ApplyVertexUpsert(
        KvKeyspace keyspace,
        CommitPlanBuilder builder,
        UpsertVertexOperation operation)
    {
        byte[] recordKey = GraphKeyCodec.EncodeVertexRecord(operation.Record.Id);
        GraphVertexRecord? current = ReadVertex(
            keyspace,
            recordKey,
            operation.ExpectedElementVersion,
            out long currentKvVersion);
        builder.AddKeyVersion(recordKey, currentKvVersion);
        if (current is not null)
            RemoveVertexIndexes(builder, current);
        builder.Put(recordKey, operation.EncodedRecord);
        AddVertexIndexes(builder, operation.Record);
    }

    private static void ApplyEdgeUpsert(
        KvKeyspace keyspace,
        CommitPlanBuilder builder,
        UpsertEdgeOperation operation)
    {
        byte[] recordKey = GraphKeyCodec.EncodeEdgeRecord(operation.Record.Id);
        GraphEdgeRecord? current = ReadEdge(
            keyspace,
            recordKey,
            operation.ExpectedElementVersion,
            out long currentKvVersion);
        builder.AddKeyVersion(recordKey, currentKvVersion);
        if (current is not null)
            RemoveEdgeProjection(builder, current);
        builder.Put(recordKey, operation.EncodedRecord);
        AddEdgeProjection(builder, operation.Record);
    }

    private static void ApplyVertexDelete(
        KvKeyspace keyspace,
        CommitPlanBuilder builder,
        DeleteVertexOperation operation)
    {
        byte[] recordKey = GraphKeyCodec.EncodeVertexRecord(operation.VertexId);
        GraphVertexRecord current = ReadVertex(
            keyspace,
            recordKey,
            operation.ExpectedElementVersion,
            out long currentKvVersion)
            ?? throw VersionConflict("vertex", operation.VertexId, operation.ExpectedElementVersion, 0);
        builder.AddKeyVersion(recordKey, currentKvVersion);
        builder.AddCondition(
            KvBatchPrecondition.PrefixEmpty(GraphKeyCodec.OutgoingPrefix(operation.VertexId)),
            new RestrictConditionBinding(operation.VertexId));
        builder.AddCondition(
            KvBatchPrecondition.PrefixEmpty(GraphKeyCodec.IncomingPrefix(operation.VertexId)),
            new RestrictConditionBinding(operation.VertexId));
        builder.Delete(recordKey);
        RemoveVertexIndexes(builder, current);
    }

    private static void ApplyEdgeDelete(
        KvKeyspace keyspace,
        CommitPlanBuilder builder,
        DeleteEdgeOperation operation)
    {
        byte[] recordKey = GraphKeyCodec.EncodeEdgeRecord(operation.EdgeId);
        GraphEdgeRecord current = ReadEdge(
            keyspace,
            recordKey,
            operation.ExpectedElementVersion,
            out long currentKvVersion)
            ?? throw VersionConflict("edge", operation.EdgeId, operation.ExpectedElementVersion, 0);
        builder.AddKeyVersion(recordKey, currentKvVersion);
        builder.Delete(recordKey);
        RemoveEdgeProjection(builder, current);
    }

    private static void EnsureEndpoint(
        KvKeyspace keyspace,
        CommitPlanBuilder builder,
        GraphElementId vertexId,
        IReadOnlySet<long> desiredVertices,
        IReadOnlySet<long> deletedVertices)
    {
        if (deletedVertices.Contains(vertexId.Value))
            throw new InvalidOperationException($"Graph edge endpoint {vertexId} 在同一 transaction 中被删除。");
        if (desiredVertices.Contains(vertexId.Value))
            return;

        byte[] key = GraphKeyCodec.EncodeVertexRecord(vertexId);
        KvEntry? entry = keyspace.GetEntry(key);
        if (entry is null)
            throw new InvalidOperationException($"Graph edge endpoint vertex {vertexId} 不存在。");
        GraphVertexRecord record = GraphElementRecordCodec.DecodeVertex(entry.Value.Span);
        if (record.Id != vertexId)
            throw new InvalidDataException("Graph vertex record key 与 payload ID 不一致。");
        builder.AddKeyVersion(key, entry.Version);
    }

    private static GraphVertexRecord? ReadVertex(
        KvKeyspace keyspace,
        byte[] key,
        long expectedElementVersion,
        out long currentKvVersion)
    {
        KvEntry? entry = keyspace.GetEntry(key);
        currentKvVersion = entry?.Version ?? 0;
        if (entry is null)
        {
            if (expectedElementVersion != 0)
                throw VersionConflict("vertex", GraphKeyCodec.Decode(key).ElementId, expectedElementVersion, 0);
            return null;
        }
        GraphVertexRecord record = GraphElementRecordCodec.DecodeVertex(entry.Value.Span);
        GraphElementId keyId = GraphKeyCodec.Decode(key).ElementId;
        if (record.Id != keyId)
            throw new InvalidDataException("Graph vertex record key 与 payload ID 不一致。");
        if (record.ElementVersion != expectedElementVersion)
            throw VersionConflict("vertex", record.Id, expectedElementVersion, record.ElementVersion);
        return record;
    }

    private static GraphEdgeRecord? ReadEdge(
        KvKeyspace keyspace,
        byte[] key,
        long expectedElementVersion,
        out long currentKvVersion)
    {
        KvEntry? entry = keyspace.GetEntry(key);
        currentKvVersion = entry?.Version ?? 0;
        if (entry is null)
        {
            if (expectedElementVersion != 0)
                throw VersionConflict("edge", GraphKeyCodec.Decode(key).ElementId, expectedElementVersion, 0);
            return null;
        }
        GraphEdgeRecord record = GraphElementRecordCodec.DecodeEdge(entry.Value.Span);
        GraphElementId keyId = GraphKeyCodec.Decode(key).ElementId;
        if (record.Id != keyId)
            throw new InvalidDataException("Graph edge record key 与 payload ID 不一致。");
        if (record.ElementVersion != expectedElementVersion)
            throw VersionConflict("edge", record.Id, expectedElementVersion, record.ElementVersion);
        return record;
    }

    private static GraphConcurrencyException VersionConflict(
        string kind,
        GraphElementId id,
        long expected,
        long actual)
        => new($"Graph {kind} {id} element version 冲突：expected={expected}, actual={actual}。");

    private static void AddVertexIndexes(CommitPlanBuilder builder, GraphVertexRecord record)
    {
        foreach (LabelId label in record.Labels)
        {
            builder.Put(
                GraphKeyCodec.EncodeLabelMembership(GraphElementKind.Vertex, label, record.Id),
                []);
            foreach (GraphPropertyEntry property in record.Properties)
            {
                builder.Put(
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

    private static void RemoveVertexIndexes(CommitPlanBuilder builder, GraphVertexRecord record)
    {
        foreach (LabelId label in record.Labels)
        {
            builder.Delete(GraphKeyCodec.EncodeLabelMembership(GraphElementKind.Vertex, label, record.Id));
            foreach (GraphPropertyEntry property in record.Properties)
            {
                builder.Delete(GraphKeyCodec.EncodePropertyIndex(
                    GraphElementKind.Vertex,
                    label,
                    property.PropertyId,
                    property.Value,
                    record.Id));
            }
        }
    }

    private static void AddEdgeProjection(CommitPlanBuilder builder, GraphEdgeRecord record)
    {
        builder.Put(
            GraphKeyCodec.EncodeOutgoingAdjacency(
                record.SourceId,
                record.LabelId,
                record.TargetId,
                record.Id),
            []);
        builder.Put(
            GraphKeyCodec.EncodeIncomingAdjacency(
                record.TargetId,
                record.LabelId,
                record.SourceId,
                record.Id),
            []);
        builder.Put(
            GraphKeyCodec.EncodeLabelMembership(GraphElementKind.Edge, record.LabelId, record.Id),
            []);
        foreach (GraphPropertyEntry property in record.Properties)
        {
            builder.Put(
                GraphKeyCodec.EncodePropertyIndex(
                    GraphElementKind.Edge,
                    record.LabelId,
                    property.PropertyId,
                    property.Value,
                    record.Id),
                []);
        }
    }

    private static void RemoveEdgeProjection(CommitPlanBuilder builder, GraphEdgeRecord record)
    {
        builder.Delete(GraphKeyCodec.EncodeOutgoingAdjacency(
            record.SourceId,
            record.LabelId,
            record.TargetId,
            record.Id));
        builder.Delete(GraphKeyCodec.EncodeIncomingAdjacency(
            record.TargetId,
            record.LabelId,
            record.SourceId,
            record.Id));
        builder.Delete(GraphKeyCodec.EncodeLabelMembership(
            GraphElementKind.Edge,
            record.LabelId,
            record.Id));
        foreach (GraphPropertyEntry property in record.Properties)
        {
            builder.Delete(GraphKeyCodec.EncodePropertyIndex(
                GraphElementKind.Edge,
                record.LabelId,
                property.PropertyId,
                property.Value,
                record.Id));
        }
    }

    private static void EnsureHighWater(
        KvKeyspace keyspace,
        CommitPlanBuilder builder,
        GraphHighWaterKind kind,
        long requiredValue)
    {
        if (requiredValue <= 0)
            return;
        byte[] key = GraphKeyCodec.EncodeMetadata((byte)kind);
        KvEntry? entry = keyspace.GetEntry(key);
        long currentValue = entry is null ? 0 : GraphHighWaterCodec.Decode(entry.Value.Span, kind);
        if (currentValue >= requiredValue)
            return;
        builder.AddKeyVersion(key, entry?.Version ?? 0);
        builder.Put(key, GraphHighWaterCodec.Encode(kind, requiredValue));
    }

    private static bool TryResolveDuplicate(
        KvKeyspace keyspace,
        byte[] requestKey,
        ReadOnlySpan<byte> digest,
        out GraphCommitResult result)
    {
        KvEntry? marker = keyspace.GetEntry(requestKey);
        if (marker is null)
        {
            result = default;
            return false;
        }
        byte[] storedDigest = GraphTransactionRequestCodec.Decode(marker.Value.Span);
        if (!CryptographicOperations.FixedTimeEquals(storedDigest, digest))
            throw new GraphRequestConflictException(GraphKeyCodec.Decode(requestKey).TransactionRequestId);
        result = new GraphCommitResult(marker.Version, IsDuplicate: true);
        return true;
    }

    private void EnforceLimits(IReadOnlyList<KvBatchMutation> mutations)
    {
        if (mutations.Count > _limits.MaxKvMutations)
        {
            throw new GraphTransactionLimitExceededException(
                $"Graph transaction 需要 {mutations.Count} 个 KV mutations，超过上限 {_limits.MaxKvMutations}。");
        }
        long encodedBytes = 0;
        foreach (KvBatchMutation mutation in mutations)
        {
            encodedBytes = checked(encodedBytes + mutation.Key.Length + (mutation.Value?.Length ?? 0));
            if (encodedBytes > _limits.MaxEncodedBytes)
            {
                throw new GraphTransactionLimitExceededException(
                    $"Graph transaction 编码后为 {encodedBytes} 字节，超过上限 {_limits.MaxEncodedBytes}。");
            }
        }
    }

    private byte[] ComputeDigest(IReadOnlyList<GraphWriteOperation> operations)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> integer = stackalloc byte[sizeof(long)];
        Span<byte> kind = stackalloc byte[1];
        long encodedBytes = 0;
        foreach (GraphWriteOperation operation in operations)
        {
            kind[0] = (byte)operation.Kind;
            hash.AppendData(kind);
            BinaryPrimitives.WriteInt64LittleEndian(integer, operation.ElementId.Value);
            hash.AppendData(integer);
            BinaryPrimitives.WriteInt64LittleEndian(integer, operation.ExpectedElementVersion);
            hash.AppendData(integer);
            byte[]? record = operation switch
            {
                UpsertVertexOperation vertex => vertex.EncodedRecord,
                UpsertEdgeOperation edge => edge.EncodedRecord,
                _ => null,
            };
            BinaryPrimitives.WriteInt64LittleEndian(integer, record?.Length ?? 0);
            hash.AppendData(integer);
            if (record is not null)
            {
                encodedBytes = checked(encodedBytes + record.Length);
                if (encodedBytes > _limits.MaxEncodedBytes)
                {
                    throw new GraphTransactionLimitExceededException(
                        $"Graph transaction digest 已超过编码预算 {_limits.MaxEncodedBytes} 字节。");
                }
                hash.AppendData(record);
            }
        }
        return hash.GetHashAndReset();
    }

    private void AddElement(GraphElementKind kind, GraphElementId id)
    {
        if (id.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        if (!_elements.Add((kind, id.Value)))
            throw new InvalidOperationException($"Graph transaction 对 {kind} {id} 包含重复 mutation。");
    }

    private void EnsureOperationCapacity()
    {
        if (_operations.Count >= _limits.MaxKvMutations)
        {
            throw new GraphTransactionLimitExceededException(
                $"Graph transaction graph mutation 数量不能超过 {_limits.MaxKvMutations}。 ");
        }
    }

    private long GetBufferedEncodedBytes(int recordLength)
    {
        if (recordLength > _limits.MaxEncodedBytes - _bufferedEncodedBytes)
        {
            throw new GraphTransactionLimitExceededException(
                $"Graph transaction 缓冲 record 编码超过上限 {_limits.MaxEncodedBytes} 字节。");
        }
        return _bufferedEncodedBytes + recordLength;
    }

    private void EnsureMutable()
    {
        if (_completed)
            throw new InvalidOperationException("Graph transaction 已提交或结束，不能再次修改。");
    }

    private enum GraphWriteOperationKind : byte
    {
        UpsertVertex = 1,
        UpsertEdge = 2,
        DeleteVertex = 3,
        DeleteEdge = 4,
    }

    private abstract record GraphWriteOperation(
        GraphWriteOperationKind Kind,
        GraphElementId ElementId,
        long ExpectedElementVersion);

    private sealed record UpsertVertexOperation(
        GraphVertexRecord Record,
        byte[] EncodedRecord,
        long ExpectedVersion)
        : GraphWriteOperation(
            GraphWriteOperationKind.UpsertVertex,
            Record.Id,
            ExpectedVersion);

    private sealed record UpsertEdgeOperation(
        GraphEdgeRecord Record,
        byte[] EncodedRecord,
        long ExpectedVersion)
        : GraphWriteOperation(
            GraphWriteOperationKind.UpsertEdge,
            Record.Id,
            ExpectedVersion);

    private sealed record DeleteVertexOperation(
        GraphElementId VertexId,
        long ExpectedVersion)
        : GraphWriteOperation(
            GraphWriteOperationKind.DeleteVertex,
            VertexId,
            ExpectedVersion);

    private sealed record DeleteEdgeOperation(
        GraphElementId EdgeId,
        long ExpectedVersion)
        : GraphWriteOperation(
            GraphWriteOperationKind.DeleteEdge,
            EdgeId,
            ExpectedVersion);

    private abstract record ConditionBinding;

    private sealed record OptimisticConditionBinding : ConditionBinding;

    private sealed record RequestConditionBinding : ConditionBinding;

    private sealed record RestrictConditionBinding(GraphElementId VertexId) : ConditionBinding;

    private sealed record GraphCommitPlan(
        IReadOnlyList<KvBatchMutation> Mutations,
        IReadOnlyList<KvBatchPrecondition> Preconditions,
        IReadOnlyList<ConditionBinding> Bindings);

    private sealed class CommitPlanBuilder
    {
        private readonly GraphTransactionLimits _limits;
        private readonly Dictionary<byte[], KvBatchMutation> _mutations = new(KvKeyComparer.Instance);
        private readonly List<KvBatchPrecondition> _preconditions = [];
        private readonly List<ConditionBinding> _bindings = [];
        private readonly Dictionary<byte[], long> _keyVersions = new(KvKeyComparer.Instance);

        internal CommitPlanBuilder(GraphTransactionLimits limits) => _limits = limits;

        internal void Put(byte[] key, byte[] value)
        {
            KvBatchMutation mutation = KvBatchMutation.Put(key, value);
            ReplaceMutation(mutation);
        }

        internal void Delete(byte[] key)
            => ReplaceMutation(KvBatchMutation.Delete(key));

        private void ReplaceMutation(KvBatchMutation mutation)
        {
            long encodedBytes = mutation.Key.Length + (mutation.Value?.Length ?? 0);
            bool replacing = _mutations.TryGetValue(mutation.Key, out KvBatchMutation? existing);
            if (replacing)
                encodedBytes -= existing!.Key.Length + (existing.Value?.Length ?? 0);
            else if (_mutations.Count >= _limits.MaxKvMutations)
            {
                throw new GraphTransactionLimitExceededException(
                    $"Graph transaction KV mutations 超过上限 {_limits.MaxKvMutations}。");
            }
            long total = checked(CurrentEncodedBytes + encodedBytes);
            if (total > _limits.MaxEncodedBytes)
            {
                throw new GraphTransactionLimitExceededException(
                    $"Graph transaction mutations 已超过编码预算 {_limits.MaxEncodedBytes} 字节。");
            }
            _mutations[mutation.Key] = mutation;
            CurrentEncodedBytes = total;
        }

        private long CurrentEncodedBytes { get; set; }

        internal void AddKeyVersion(byte[] key, long expectedVersion)
        {
            if (_keyVersions.TryGetValue(key, out long existing))
            {
                if (existing != expectedVersion)
                    throw new InvalidOperationException("Graph transaction 为同一个 key 生成了冲突的 version 条件。");
                return;
            }
            _keyVersions.Add(key, expectedVersion);
            AddCondition(
                KvBatchPrecondition.KeyVersion(key, expectedVersion),
                new OptimisticConditionBinding());
        }

        internal void AddCondition(KvBatchPrecondition condition, ConditionBinding binding)
        {
            _preconditions.Add(condition);
            _bindings.Add(binding);
        }

        internal GraphCommitPlan Build()
        {
            KvBatchMutation[] mutations = _mutations.Values
                .OrderBy(static mutation => mutation.Key, KvKeyComparer.Instance)
                .ToArray();
            return new GraphCommitPlan(mutations, _preconditions, _bindings);
        }
    }
}
