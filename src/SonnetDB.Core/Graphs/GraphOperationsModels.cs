using System.Text.Json.Serialization;

namespace SonnetDB.Graphs;

/// <summary>Graph 运维面的标签基数摘要。</summary>
/// <param name="LabelId">标签标识符。</param>
/// <param name="ElementCount">具有该标签的顶点与边总数。</param>
public sealed record GraphLabelStatisticDto(int LabelId, long ElementCount);

/// <summary>Graph 运维面的属性索引摘要。</summary>
/// <param name="ElementType">索引作用的元素类型。</param>
/// <param name="LabelId">标签标识符。</param>
/// <param name="PropertyId">属性标识符。</param>
/// <param name="ValueKind">属性值类型。</param>
/// <param name="EntryCount">索引条目数量。</param>
public sealed record GraphIndexStatisticDto(
    string ElementType,
    int LabelId,
    int PropertyId,
    string ValueKind,
    long EntryCount);

/// <summary>Graph 出度直方图桶。</summary>
/// <param name="Degree">顶点出度。</param>
/// <param name="VertexCount">具有该出度的顶点数量。</param>
public sealed record GraphDegreeBucketDto(int Degree, long VertexCount);

/// <summary>与当前 Graph 相关的慢遍历诊断摘要。</summary>
/// <param name="TimestampMs">记录时间（Unix 毫秒，UTC）。</param>
/// <param name="Fingerprint">归一化 SQL 的稳定指纹。</param>
/// <param name="ElapsedMs">执行耗时（毫秒）。</param>
/// <param name="RowCount">返回行数。</param>
/// <param name="AccessPath">实际访问路径。</param>
/// <param name="FallbackReason">回退原因。</param>
/// <param name="Sql">有界截断后的 SQL。</param>
public sealed record GraphSlowTraversalDto(
    long TimestampMs,
    string Fingerprint,
    double ElapsedMs,
    long RowCount,
    string? AccessPath,
    string? FallbackReason,
    string Sql);

/// <summary>Graph 运维产品面的稳定能力矩阵。</summary>
/// <param name="SchemaAndIndexes">是否公开 schema 与索引统计。</param>
/// <param name="DegreeHistogram">是否公开出度直方图。</param>
/// <param name="SlowTraversalDiagnostics">是否公开慢遍历诊断。</param>
/// <param name="BoundedVisualization">是否公开有界可视化快照。</param>
/// <param name="RestrictedEditing">是否公开带版本条件的受限编辑。</param>
/// <param name="JsonImportExport">是否公开兼容导入器的 JSON 导入导出。</param>
/// <param name="StagedMaintenance">危险维护是否必须经过暂存审批。</param>
/// <param name="Audit">是否公开持久化运维审计。</param>
public sealed record GraphOperationsCapabilitiesDto(
    bool SchemaAndIndexes,
    bool DegreeHistogram,
    bool SlowTraversalDiagnostics,
    bool BoundedVisualization,
    bool RestrictedEditing,
    bool JsonImportExport,
    bool StagedMaintenance,
    bool Audit);

/// <summary>单个原生属性图的运维概览。</summary>
/// <param name="Graph">图目录摘要。</param>
/// <param name="SnapshotSequence">统计使用的 statement snapshot 序列。</param>
/// <param name="VertexCount">顶点数量。</param>
/// <param name="EdgeCount">边数量。</param>
/// <param name="Labels">标签基数。</param>
/// <param name="Indexes">属性索引摘要。</param>
/// <param name="DegreeHistogram">出度直方图。</param>
/// <param name="SlowTraversals">最近的慢遍历。</param>
/// <param name="SlowTraversalSource">慢遍历数据来源或不可用原因。</param>
/// <param name="Capabilities">Web、Studio、CLI 与 SDK 共享的能力矩阵。</param>
public sealed record GraphOperationsOverviewDto(
    GraphInfoDto Graph,
    long SnapshotSequence,
    long VertexCount,
    long EdgeCount,
    IReadOnlyList<GraphLabelStatisticDto> Labels,
    IReadOnlyList<GraphIndexStatisticDto> Indexes,
    IReadOnlyList<GraphDegreeBucketDto> DegreeHistogram,
    IReadOnlyList<GraphSlowTraversalDto> SlowTraversals,
    string SlowTraversalSource,
    GraphOperationsCapabilitiesDto Capabilities);

/// <summary>有界 Graph 可视化快照。</summary>
/// <param name="SnapshotSequence">所有元素共享的 statement snapshot 序列。</param>
/// <param name="Truncated">是否因元素预算截断。</param>
/// <param name="Vertices">按稳定 ID 排列的顶点。</param>
/// <param name="Edges">端点均位于当前顶点集合中的边。</param>
public sealed record GraphVisualizationDto(
    long SnapshotSequence,
    bool Truncated,
    IReadOnlyList<GraphVertexDto> Vertices,
    IReadOnlyList<GraphEdgeDto> Edges);

/// <summary>需要暂存审批的 Graph 维护动作。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GraphMaintenanceAction>))]
public enum GraphMaintenanceAction
{
    /// <summary>可恢复地修复邻接、标签、属性与可识别的唯一索引。</summary>
    RepairRebuild = 1,

    /// <summary>创建完整 Graph KV checkpoint。</summary>
    Checkpoint = 2,

    /// <summary>压实 Graph KV generation。</summary>
    Compact = 3,
}

/// <summary>暂存 Graph 危险维护的请求。</summary>
public sealed record GraphMaintenanceStageRequest
{
    /// <summary>需要审批的维护动作。</summary>
    public GraphMaintenanceAction Action { get; init; }

    /// <summary>repair 完成后是否继续执行 compaction。</summary>
    public bool CompactOnCompletion { get; init; }

    /// <summary>单次 repair 审批最多执行的可恢复 work unit 数。</summary>
    public int MaxWorkUnits { get; init; } = 64;
}

/// <summary>拒绝 Graph 维护审批时的可选说明。</summary>
/// <param name="Reason">不超过 512 个字符的拒绝原因。</param>
public sealed record GraphMaintenanceDecisionRequest(string? Reason = null);

/// <summary>Graph 维护执行结果。</summary>
public sealed record GraphMaintenanceExecutionDto
{
    /// <summary>维护动作。</summary>
    public GraphMaintenanceAction Action { get; init; }

    /// <summary>动作是否已经完整完成。</summary>
    public bool IsComplete { get; init; }

    /// <summary>可恢复 repair 的稳定 operation ID。</summary>
    public Guid? OperationId { get; init; }

    /// <summary>可恢复 repair 当前阶段。</summary>
    public string? Phase { get; init; }

    /// <summary>动作结束时的 KV sequence。</summary>
    public long Sequence { get; init; }

    /// <summary>扫描的主记录数。</summary>
    public long ScannedRecords { get; init; }

    /// <summary>补写或覆盖的派生条目数。</summary>
    public long RepairedEntries { get; init; }

    /// <summary>删除的失效派生条目数。</summary>
    public long RemovedEntries { get; init; }

    /// <summary>累计完成的 work unit 数。</summary>
    public long WorkUnits { get; init; }
}

/// <summary>Graph 维护审批与审计事件。</summary>
public sealed record GraphMaintenanceApprovalDto
{
    /// <summary>审批标识符。</summary>
    public Guid ApprovalId { get; init; }

    /// <summary>事件时间（UTC）。</summary>
    public DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>数据库名。</summary>
    public required string Database { get; init; }

    /// <summary>图名称。</summary>
    public required string Graph { get; init; }

    /// <summary>维护动作。</summary>
    public GraphMaintenanceAction Action { get; init; }

    /// <summary>当前状态：staged、applying、interrupted、completed、paused、rejected、expired 或 failed。</summary>
    public required string State { get; init; }

    /// <summary>发起或决策主体。</summary>
    public required string Principal { get; init; }

    /// <summary>审批过期时间（UTC）。</summary>
    public DateTimeOffset ExpiresAtUtc { get; init; }

    /// <summary>repair 完成后是否执行 compaction。</summary>
    public bool CompactOnCompletion { get; init; }

    /// <summary>单次 repair 的 work unit 上限。</summary>
    public int MaxWorkUnits { get; init; }

    /// <summary>成功或暂停时的执行结果。</summary>
    public GraphMaintenanceExecutionDto? Result { get; init; }

    /// <summary>失败时的稳定错误码。</summary>
    public string? ErrorCode { get; init; }

    /// <summary>拒绝或失败说明。</summary>
    public string? Reason { get; init; }
}

/// <summary>Graph 维护审计列表。</summary>
/// <param name="Items">按时间倒序排列的审批与执行事件。</param>
public sealed record GraphMaintenanceAuditListDto(
    IReadOnlyList<GraphMaintenanceApprovalDto> Items);
