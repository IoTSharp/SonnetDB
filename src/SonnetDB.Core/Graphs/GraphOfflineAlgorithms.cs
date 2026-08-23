using SonnetDB.Tables;

namespace SonnetDB.Graphs;

/// <summary>离线 Graph 算法任务的可恢复阶段。</summary>
public enum GraphOfflineAlgorithmPhase : byte
{
    /// <summary>从 statement snapshot 采集顶点。</summary>
    ScanVertices = 1,

    /// <summary>从同一 statement snapshot 采集边。</summary>
    ScanEdges = 2,

    /// <summary>计算入度、出度和总度数。</summary>
    Degree = 3,

    /// <summary>计算弱连通分量。</summary>
    ConnectedComponents = 4,

    /// <summary>迭代计算 PageRank。</summary>
    PageRank = 5,

    /// <summary>执行确定性 label-propagation community。</summary>
    Community = 6,

    /// <summary>把版本化结果发布到 Graph 或 Table。</summary>
    Publish = 7,

    /// <summary>结果已经完整发布。</summary>
    Completed = 8,
}

/// <summary>离线算法结果的发布目标类别。</summary>
public enum GraphOfflineAlgorithmOutputKind : byte
{
    /// <summary>把结果写入源图顶点属性。</summary>
    GraphProperties = 1,

    /// <summary>把结果写入标准关系结果表。</summary>
    Table = 2,
}

/// <summary>离线算法结果发布目标。</summary>
public abstract record GraphOfflineAlgorithmOutput
{
    private protected GraphOfflineAlgorithmOutput() { }

    /// <summary>发布目标类别。</summary>
    public abstract GraphOfflineAlgorithmOutputKind Kind { get; }
}

/// <summary>把离线算法结果写入源图顶点属性的映射。</summary>
public sealed record GraphOfflineAlgorithmGraphOutput : GraphOfflineAlgorithmOutput
{
    /// <summary>
    /// 创建 Graph 属性输出映射。
    /// </summary>
    /// <param name="componentPropertyId">弱连通分量属性 ID。</param>
    /// <param name="pageRankPropertyId">PageRank 属性 ID。</param>
    /// <param name="inDegreePropertyId">入度属性 ID。</param>
    /// <param name="outDegreePropertyId">出度属性 ID。</param>
    /// <param name="totalDegreePropertyId">总度数属性 ID。</param>
    /// <param name="communityPropertyId">community 属性 ID。</param>
    /// <param name="resultVersionPropertyId">结果版本属性 ID。</param>
    public GraphOfflineAlgorithmGraphOutput(
        int componentPropertyId,
        int pageRankPropertyId,
        int inDegreePropertyId,
        int outDegreePropertyId,
        int totalDegreePropertyId,
        int communityPropertyId,
        int resultVersionPropertyId)
    {
        ComponentPropertyId = componentPropertyId;
        PageRankPropertyId = pageRankPropertyId;
        InDegreePropertyId = inDegreePropertyId;
        OutDegreePropertyId = outDegreePropertyId;
        TotalDegreePropertyId = totalDegreePropertyId;
        CommunityPropertyId = communityPropertyId;
        ResultVersionPropertyId = resultVersionPropertyId;
    }

    /// <inheritdoc />
    public override GraphOfflineAlgorithmOutputKind Kind => GraphOfflineAlgorithmOutputKind.GraphProperties;

    /// <summary>弱连通分量属性 ID。</summary>
    public int ComponentPropertyId { get; }

    /// <summary>PageRank 属性 ID。</summary>
    public int PageRankPropertyId { get; }

    /// <summary>入度属性 ID。</summary>
    public int InDegreePropertyId { get; }

    /// <summary>出度属性 ID。</summary>
    public int OutDegreePropertyId { get; }

    /// <summary>总度数属性 ID。</summary>
    public int TotalDegreePropertyId { get; }

    /// <summary>community 属性 ID。</summary>
    public int CommunityPropertyId { get; }

    /// <summary>结果版本属性 ID。</summary>
    public int ResultVersionPropertyId { get; }

    /// <summary>
    /// 源图已有的 vertex unique 属性声明。发布时会原样维护这些声明；冻结 V1 record 不保存声明，
    /// 因此调用方必须提供完整集合。
    /// </summary>
    public IReadOnlyList<GraphUniqueIndexDefinition> UniqueIndexes { get; init; } = [];
}

/// <summary>把离线算法结果写入标准关系结果表。</summary>
public sealed record GraphOfflineAlgorithmTableOutput : GraphOfflineAlgorithmOutput
{
    /// <summary>创建关系结果表输出。</summary>
    /// <param name="table">使用 <see cref="GraphOfflineAlgorithmTable.CreateSchema"/> 创建或匹配的表。</param>
    public GraphOfflineAlgorithmTableOutput(TableStore table)
    {
        ArgumentNullException.ThrowIfNull(table);
        Table = table;
    }

    /// <inheritdoc />
    public override GraphOfflineAlgorithmOutputKind Kind => GraphOfflineAlgorithmOutputKind.Table;

    /// <summary>接收版本化算法行的关系表。</summary>
    public TableStore Table { get; }
}

/// <summary>一次可恢复离线 Graph 算法请求。</summary>
public sealed record GraphOfflineAlgorithmRequest
{
    /// <summary>创建离线算法请求。</summary>
    /// <param name="operationId">跨取消、重开和幂等发布保持稳定的任务 ID。</param>
    /// <param name="output">Graph property 或 Table 输出目标。</param>
    public GraphOfflineAlgorithmRequest(Guid operationId, GraphOfflineAlgorithmOutput output)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("Graph offline algorithm operation ID 不能为空。", nameof(operationId));
        ArgumentNullException.ThrowIfNull(output);
        OperationId = operationId;
        Output = output;
    }

    /// <summary>跨取消、重开和幂等发布保持稳定的任务 ID。</summary>
    public Guid OperationId { get; }

    /// <summary>结果发布目标。</summary>
    public GraphOfflineAlgorithmOutput Output { get; }
}

/// <summary>离线 Graph 算法的分页、内存、迭代与续作预算。</summary>
public sealed record GraphOfflineAlgorithmOptions
{
    /// <summary>采集 statement snapshot 时每页最多读取的元素数。</summary>
    public int PageSize { get; init; } = 256;

    /// <summary>采集 statement snapshot 时单页 key/value payload 上限，默认 4 MiB。</summary>
    public int MaxPageBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>单次调用最多完成的可恢复 work unit 数。</summary>
    public int MaxWorkUnits { get; init; } = 64;

    /// <summary>算法状态和 community sort run 可使用的内存上限，默认 64 MiB。</summary>
    public long MaxMemoryBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>PageRank 最大迭代次数。</summary>
    public int MaxPageRankIterations { get; init; } = 50;

    /// <summary>PageRank damping factor，必须大于 0 且小于 1。</summary>
    public double PageRankDampingFactor { get; init; } = 0.85;

    /// <summary>PageRank L1 收敛阈值。</summary>
    public double PageRankTolerance { get; init; } = 1e-9;

    /// <summary>确定性 label propagation community 最大迭代次数。</summary>
    public int MaxCommunityIterations { get; init; } = 50;

    /// <summary>单个幂等发布批次最多包含的顶点数。</summary>
    public int OutputBatchSize { get; init; } = 128;

    internal void Validate()
    {
        if (PageSize is <= 0 or > 4_096)
            throw new ArgumentOutOfRangeException(nameof(PageSize));
        if (MaxPageBytes is <= 0 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxPageBytes));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxWorkUnits);
        if (MaxMemoryBytes is < 256 * 1024 or > 16L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxMemoryBytes));
        if (MaxPageRankIterations is <= 0 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(MaxPageRankIterations));
        if (!double.IsFinite(PageRankDampingFactor)
            || PageRankDampingFactor is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(PageRankDampingFactor));
        }
        if (!double.IsFinite(PageRankTolerance) || PageRankTolerance <= 0)
            throw new ArgumentOutOfRangeException(nameof(PageRankTolerance));
        if (MaxCommunityIterations is <= 0 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(MaxCommunityIterations));
        if (OutputBatchSize is <= 0 or > 2_048)
            throw new ArgumentOutOfRangeException(nameof(OutputBatchSize));
    }
}

/// <summary>离线 Graph 算法任务的当前或最终结果。</summary>
public sealed class GraphOfflineAlgorithmResult
{
    internal GraphOfflineAlgorithmResult(GraphOfflineAlgorithmState state, bool resumed)
    {
        OperationId = state.OperationId;
        Phase = state.Phase;
        IsComplete = state.Phase == GraphOfflineAlgorithmPhase.Completed;
        WasResumed = resumed;
        SourceSequence = state.SourceSequence;
        ResultVersion = GraphOfflineAlgorithmRunner.CreateResultVersion(state.OperationId, state.SourceSequence);
        VertexCount = state.VertexCount;
        EdgeCount = state.EdgeCount;
        WorkUnits = state.WorkUnits;
        PageRankIterations = state.PageRankIterations;
        PageRankConverged = state.PageRankConverged;
        CommunityIterations = state.CommunityIterations;
        CommunityConverged = state.CommunityConverged;
        PublishedVertices = state.PublishedVertices;
        MemoryBudgetBytes = state.MemoryBudgetBytes;
        SpillBytes = state.SpillBytes;
    }

    /// <summary>稳定任务 ID。</summary>
    public Guid OperationId { get; }

    /// <summary>下一次续作阶段或 Completed。</summary>
    public GraphOfflineAlgorithmPhase Phase { get; }

    /// <summary>结果是否已经完整发布。</summary>
    public bool IsComplete { get; }

    /// <summary>本次调用是否从 durable sidecar 续作。</summary>
    public bool WasResumed { get; }

    /// <summary>算法输入固定的 Graph statement snapshot sequence。</summary>
    public long SourceSequence { get; }

    /// <summary>由 operation ID 与 source sequence 组成的稳定结果版本。</summary>
    public string ResultVersion { get; }

    /// <summary>输入顶点数。</summary>
    public long VertexCount { get; }

    /// <summary>输入边数。</summary>
    public long EdgeCount { get; }

    /// <summary>累计完成的 durable work unit 数。</summary>
    public long WorkUnits { get; }

    /// <summary>PageRank 已完成的迭代数。</summary>
    public int PageRankIterations { get; }

    /// <summary>PageRank 是否在预算内达到 L1 阈值。</summary>
    public bool PageRankConverged { get; }

    /// <summary>community label propagation 已完成的迭代数。</summary>
    public int CommunityIterations { get; }

    /// <summary>community label propagation 是否在预算内稳定。</summary>
    public bool CommunityConverged { get; }

    /// <summary>已经幂等发布的顶点结果数。</summary>
    public long PublishedVertices { get; }

    /// <summary>本次任务冻结的算法内存预算。</summary>
    public long MemoryBudgetBytes { get; }

    /// <summary>完成前算法 workspace 的最大持久化 spill 字节数。</summary>
    public long SpillBytes { get; }
}

/// <summary>恢复离线算法时源图 sequence 已与未完成输入不一致。</summary>
public sealed class GraphOfflineAlgorithmSourceChangedException : InvalidOperationException
{
    internal GraphOfflineAlgorithmSourceChangedException(long expectedSequence, long actualSequence)
        : base($"Graph offline algorithm 的 source sequence 已变化：expected={expectedSequence}, actual={actualSequence}。")
    {
        ExpectedSequence = expectedSequence;
        ActualSequence = actualSequence;
    }

    /// <summary>sidecar 中冻结的输入 sequence。</summary>
    public long ExpectedSequence { get; }

    /// <summary>续作时观察到的当前 sequence。</summary>
    public long ActualSequence { get; }
}

/// <summary>标准离线 Graph 算法结果表 schema。</summary>
public static class GraphOfflineAlgorithmTable
{
    /// <summary>创建版本化结果表 schema。</summary>
    /// <param name="tableName">结果表名称。</param>
    /// <returns>以 operation_id + vertex_id 为主键的固定 schema。</returns>
    public static TableSchema CreateSchema(string tableName)
        => TableSchema.Create(
            tableName,
            [
                ("operation_id", TableColumnType.String, false),
                ("vertex_id", TableColumnType.Int64, false),
                ("source_sequence", TableColumnType.Int64, false),
                ("component_id", TableColumnType.Int64, false),
                ("page_rank", TableColumnType.Float64, false),
                ("in_degree", TableColumnType.Int64, false),
                ("out_degree", TableColumnType.Int64, false),
                ("total_degree", TableColumnType.Int64, false),
                ("community_id", TableColumnType.Int64, false),
            ],
            ["operation_id", "vertex_id"]);
}

internal sealed class GraphOfflineAlgorithmState
{
    internal required Guid StorageId { get; init; }

    internal required Guid OperationId { get; init; }

    internal required byte[] ConfigurationHash { get; init; }

    internal required GraphOfflineAlgorithmPhase Phase { get; set; }

    internal required long SourceSequence { get; init; }

    internal required long MemoryBudgetBytes { get; init; }

    internal byte[] AfterKey { get; set; } = [];

    internal long VertexCount { get; set; }

    internal long EdgeCount { get; set; }

    internal long VertexRecordsLength { get; set; }

    internal long WorkUnits { get; set; }

    internal bool PageRankInitialized { get; set; }

    internal int PageRankGeneration { get; set; }

    internal int PageRankIterations { get; set; }

    internal bool PageRankConverged { get; set; }

    internal bool CommunityInitialized { get; set; }

    internal int CommunityGeneration { get; set; }

    internal int CommunityIterations { get; set; }

    internal bool CommunityConverged { get; set; }

    internal long PublishedVertices { get; set; }

    internal long SpillBytes { get; set; }
}
