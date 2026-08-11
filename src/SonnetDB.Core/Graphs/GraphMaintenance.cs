namespace SonnetDB.Graphs;

/// <summary>可恢复 Graph 维护的当前阶段。</summary>
public enum GraphMaintenancePhase : byte
{
    /// <summary>补齐 vertex 主记录对应的派生条目。</summary>
    RepairVertices = 1,

    /// <summary>补齐 edge 主记录对应的邻接和派生条目。</summary>
    RepairEdges = 2,

    /// <summary>清理失效的 outgoing 邻接条目。</summary>
    RemoveOutgoingAdjacency = 3,

    /// <summary>清理失效的 incoming 邻接条目。</summary>
    RemoveIncomingAdjacency = 4,

    /// <summary>清理失效的 vertex label 条目。</summary>
    RemoveVertexLabels = 5,

    /// <summary>清理失效的 edge label 条目。</summary>
    RemoveEdgeLabels = 6,

    /// <summary>清理失效的 vertex property 条目。</summary>
    RemoveVertexProperties = 7,

    /// <summary>清理失效的 edge property 条目。</summary>
    RemoveEdgeProperties = 8,

    /// <summary>从现存 vertex unique owner 条目收集声明。</summary>
    CollectVertexUniqueDefinitions = 9,

    /// <summary>从现存 edge unique owner 条目收集声明。</summary>
    CollectEdgeUniqueDefinitions = 10,

    /// <summary>按 property source 验证 unique 声明没有冲突。</summary>
    ValidateUniqueIndexes = 11,

    /// <summary>按已验证的 property source 补齐 unique owner 条目。</summary>
    RepairUniqueIndexes = 12,

    /// <summary>清理失效的 vertex unique owner 条目。</summary>
    RemoveVertexUniqueIndexes = 13,

    /// <summary>清理失效的 edge unique owner 条目。</summary>
    RemoveEdgeUniqueIndexes = 14,

    /// <summary>将维护写入发布为 KV checkpoint。</summary>
    Checkpoint = 15,

    /// <summary>按显式请求压实 KV generation。</summary>
    Compaction = 16,

    /// <summary>维护已经完成。</summary>
    Completed = 17,
}

/// <summary>Graph 可恢复维护选项。</summary>
public sealed record GraphMaintenanceOptions
{
    /// <summary>
    /// 唯一索引的权威修复声明。声明会在扫描开始前写入 durable sidecar，故障重开后无需再次提供。
    /// </summary>
    public IReadOnlyList<GraphUniqueIndexDefinition> UniqueIndexes { get; init; } = [];

    /// <summary>每个 work unit 最多扫描的 KV 条目数，默认 256。</summary>
    public int PageSize { get; init; } = 256;

    /// <summary>每个 work unit 最多读取的 key/value payload 字节数，默认 4 MiB。</summary>
    public int MaxPageBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>单次调用最多执行的 work unit 数；达到后返回可恢复状态。</summary>
    public int MaxWorkUnits { get; init; } = 64;

    /// <summary>单个 work unit 最多生成的派生 mutation 数。</summary>
    public int MaxMutationsPerWorkUnit { get; init; } = 16_384;

    /// <summary>允许持久化的唯一索引声明上限。</summary>
    public int MaxUniqueIndexDefinitions { get; init; } = 10_000;

    /// <summary>每完成多少个 work unit 主动 checkpoint；0 表示只在完成时 checkpoint。</summary>
    public int CheckpointEveryWorkUnits { get; init; } = 64;

    /// <summary>完成 repair 与 checkpoint 后是否再执行一次显式 compaction。</summary>
    public bool CompactOnCompletion { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(UniqueIndexes);
        if (PageSize is <= 0 or > 4_096)
            throw new ArgumentOutOfRangeException(nameof(PageSize), "Graph maintenance page size 必须在 1 到 4,096 之间。");
        if (MaxPageBytes is <= 0 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxPageBytes));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxWorkUnits);
        if (MaxMutationsPerWorkUnit is <= 0 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(MaxMutationsPerWorkUnit));
        if (MaxUniqueIndexDefinitions is <= 0 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(MaxUniqueIndexDefinitions));
        if (CheckpointEveryWorkUnits < 0)
            throw new ArgumentOutOfRangeException(nameof(CheckpointEveryWorkUnits));
        if (UniqueIndexes.Count > MaxUniqueIndexDefinitions)
            throw new ArgumentOutOfRangeException(nameof(UniqueIndexes), "Graph unique index 声明超过维护预算。");

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

/// <summary>Graph 维护 work unit 超过显式 mutation 预算。</summary>
public sealed class GraphMaintenanceLimitExceededException : InvalidOperationException
{
    internal GraphMaintenanceLimitExceededException(string message) : base(message) { }
}

/// <summary>一次 Graph 可恢复维护调用后的状态。</summary>
public sealed record GraphMaintenanceResult
{
    internal GraphMaintenanceResult(GraphMaintenanceState state, bool resumed)
    {
        OperationId = state.OperationId;
        Phase = state.Phase;
        IsComplete = state.Phase == GraphMaintenancePhase.Completed;
        WasResumed = resumed;
        SourceSequence = state.SourceSequence;
        Sequence = state.LastSequence;
        ScannedRecords = state.ScannedRecords;
        RepairedEntries = state.RepairedEntries;
        RemovedEntries = state.RemovedEntries;
        WorkUnits = state.WorkUnits;
        CheckpointCount = state.CheckpointCount;
        UniqueIndexDefinitionCount = state.UniqueDefinitions.Count;
    }

    /// <summary>跨暂停和进程重开的稳定维护标识符。</summary>
    public Guid OperationId { get; }

    /// <summary>下一次续作将进入的阶段；完成时为 <see cref="GraphMaintenancePhase.Completed"/>。</summary>
    public GraphMaintenancePhase Phase { get; }

    /// <summary>是否已经完成 repair、最终 checkpoint 和可选 compaction。</summary>
    public bool IsComplete { get; }

    /// <summary>本次调用是否从已有 durable sidecar 续作。</summary>
    public bool WasResumed { get; }

    /// <summary>维护首次启动时观察到的 KV sequence。</summary>
    public long SourceSequence { get; }

    /// <summary>最后一个维护写入或 checkpoint 的 KV sequence。</summary>
    public long Sequence { get; }

    /// <summary>已经扫描的 vertex/edge 主记录数。</summary>
    public long ScannedRecords { get; }

    /// <summary>已经补写或覆盖的派生条目数。</summary>
    public long RepairedEntries { get; }

    /// <summary>已经删除的失效派生条目数。</summary>
    public long RemovedEntries { get; }

    /// <summary>从任务开始累计完成的 work unit 数。</summary>
    public long WorkUnits { get; }

    /// <summary>任务累计执行的主动 checkpoint 数。</summary>
    public long CheckpointCount { get; }

    /// <summary>任务 durable sidecar 中保存的 unique 声明数量。</summary>
    public int UniqueIndexDefinitionCount { get; }
}

internal sealed class GraphMaintenanceState
{
    internal required Guid StorageId { get; init; }

    internal required Guid OperationId { get; init; }

    internal required GraphMaintenancePhase Phase { get; set; }

    internal required long SourceSequence { get; init; }

    internal required long LastSequence { get; set; }

    internal long ScannedRecords { get; set; }

    internal long RepairedEntries { get; set; }

    internal long RemovedEntries { get; set; }

    internal long WorkUnits { get; set; }

    internal long CheckpointCount { get; set; }

    internal int UniqueDefinitionIndex { get; set; }

    internal byte[] AfterKey { get; set; } = [];

    internal byte[] PreviousUniqueKey { get; set; } = [];

    internal required List<GraphUniqueIndexDefinition> UniqueDefinitions { get; init; }

    internal required int MaxUniqueIndexDefinitions { get; init; }

    internal required bool CompactOnCompletion { get; init; }
}
