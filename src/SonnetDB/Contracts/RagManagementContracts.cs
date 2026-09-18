using System.Text.Json.Serialization;

namespace SonnetDB.Contracts;

/// <summary>RAG 管理请求；只接受服务端已配置 profile 的身份，不接受凭据或 provider 地址。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RagManagementRequest
{
    /// <summary>generation stream。</summary>
    public string Stream { get; init; } = string.Empty;
    /// <summary>调用方观察到的 active revision；无 active 时为 0，必须显式提供。</summary>
    public long? ExpectedRevision { get; init; }
    /// <summary>rebuild/resume 使用的已配置 profile ID。</summary>
    public string? ProfileId { get; init; }
    /// <summary>resume/discard 使用的待处理 generation 身份。</summary>
    public string? PendingGenerationId { get; init; }
    /// <summary>cleanup 的 retired 发布时间截止点。</summary>
    public DateTimeOffset? RetiredBeforeUtc { get; init; }
    /// <summary>cleanup 最大处理 generation 数，范围 1 至 100。</summary>
    public int MaxGenerations { get; init; } = 20;
}

/// <summary>不包含凭据、外发地址、文件路径或内容的 profile 摘要。</summary>
/// <param name="Id">profile ID。</param><param name="Provider">provider 名称。</param>
/// <param name="Model">模型名称。</param><param name="Revision">模型版本。</param>
/// <param name="Dimensions">向量维度。</param><param name="Normalization">归一化。</param><param name="Metric">距离度量。</param>
public sealed record RagManagementProfile(string Id, string Provider, string Model, string Revision,
    int Dimensions, string Normalization, string Metric);

/// <summary>当前已配置且可由管理 API 选择的 profile 列表。</summary>
/// <param name="Profiles">可信 profile 摘要。</param>
public sealed record RagManagementProfiles(IReadOnlyList<RagManagementProfile> Profiles);

/// <summary>持久待处理任务的脱敏摘要。</summary>
/// <param name="GenerationId">任务 generation 身份。</param><param name="ProfileId">profile 身份。</param>
/// <param name="ExpectedRevision">任务冻结时的 active revision。</param><param name="Contents">内容数。</param><param name="Chunks">分块数。</param>
public sealed record RagManagementPending(string GenerationId, string ProfileId, long ExpectedRevision, int Contents, int Chunks);

/// <summary>单个 stream 的 active 与 pending 状态，不包含正文或向量。</summary>
/// <param name="Stream">stream。</param><param name="ActiveRevision">active revision，无 active 时为 0。</param>
/// <param name="ActiveProfileId">active profile ID。</param><param name="ActiveContents">active 内容数。</param>
/// <param name="ActiveChunks">active 分块数。</param><param name="Pending">待处理任务。</param>
public sealed record RagManagementStatus(string Stream, long ActiveRevision, string? ActiveProfileId,
    int ActiveContents, int ActiveChunks, RagManagementPending? Pending);

/// <summary>管理操作完成结果；数据库 revision 是并发写入检查依据。</summary>
/// <param name="Operation">操作名称。</param><param name="Stream">stream。</param><param name="Status">completed 或 no_pending_task。</param>
/// <param name="Revision">结果 revision。</param><param name="RemovedRevisions">已清理版本。</param><param name="DeferredRevisions">因租约或保留期延迟的版本。</param>
public sealed record RagManagementResult(string Operation, string Stream, string Status, long Revision,
    IReadOnlyList<long> RemovedRevisions, IReadOnlyList<long> DeferredRevisions);

/// <summary>脱敏的 RAG 管理审计，不保存正文、向量、外发地址或异常正文。</summary>
/// <param name="OperationId">操作身份。</param><param name="StartedUtc">开始时间。</param><param name="Principal">主体。</param>
/// <param name="Operation">操作。</param><param name="StreamHash">stream 摘要。</param><param name="Status">started/succeeded/failed/cancelled。</param>
/// <param name="ExpectedRevision">调用方声明 revision。</param><param name="Revision">结果 revision。</param><param name="ErrorCode">稳定错误码。</param>
public sealed record RagManagementAuditEntry(Guid OperationId, DateTimeOffset StartedUtc, string Principal, string Operation,
    string StreamHash, string Status, long ExpectedRevision, long? Revision, string? ErrorCode);

/// <summary>数据库范围内按开始时间倒序的有界审计页。</summary>
/// <param name="Entries">记录。</param><param name="ContinuationToken">后续游标。</param>
public sealed record RagManagementAuditPage(IReadOnlyList<RagManagementAuditEntry> Entries, string? ContinuationToken);
