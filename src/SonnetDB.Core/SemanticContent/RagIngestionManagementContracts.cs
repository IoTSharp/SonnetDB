using System.Text.Json.Serialization;

namespace SonnetDB.SemanticContent;

/// <summary>已发布 RAG generation 的脱敏管理元数据，不包含正文或向量。</summary>
/// <param name="GenerationId">generation 身份。</param>
/// <param name="Revision">已发布 revision。</param>
/// <param name="PublishedAtUtc">发布时间。</param>
/// <param name="Profile">不可变完整模型合同。</param>
/// <param name="ManifestCount">内容清单数。</param>
/// <param name="ChunkCount">分块总数。</param>
public sealed record RagGenerationStatus(string GenerationId, long Revision, DateTimeOffset PublishedAtUtc,
    EmbeddingProfile Profile, int ManifestCount, int ChunkCount);

/// <summary>尚未发布的持久 RAG 任务元数据，不包含正文或向量。</summary>
/// <param name="GenerationId">必须用于续跑或丢弃的任务身份。</param>
/// <param name="ExpectedRevision">任务冻结时的 active revision。</param>
/// <param name="Profile">续跑必须保持一致的完整模型合同。</param>
/// <param name="ManifestCount">内容清单数。</param>
/// <param name="ChunkCount">分块总数。</param>
public sealed record RagPendingStatus(string GenerationId, long ExpectedRevision, EmbeddingProfile Profile,
    int ManifestCount, int ChunkCount);

/// <summary>单一 RAG stream 的有界状态快照。</summary>
/// <param name="Stream">stream 名称。</param>
/// <param name="Active">当前已发布版本，未发布时为空。</param>
/// <param name="Pending">真实未发布任务；已发布的任务记录不作为 pending 返回。</param>
public sealed record RagIngestionStatus(string Stream, RagGenerationStatus? Active, RagPendingStatus? Pending);

/// <summary>RAG 管理错误码；通过 DatabaseGenerationException.Code 返回。</summary>
public static class RagIngestionManagementErrorCodes
{
    /// <summary>当前没有可续跑或丢弃的未发布任务。</summary>
    public const string PendingUnavailable = "rag_pending_unavailable";
    /// <summary>未发布任务身份已变化。</summary>
    public const string PendingConflict = "rag_pending_conflict";
    /// <summary>完整模型合同不匹配或复用了不可变 profile ID。</summary>
    public const string ProfileMismatch = "rag_profile_mismatch";
}

/// <summary>RAG 管理元数据的 source-generated JSON 类型信息。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RagIngestionStatus))]
[JsonSerializable(typeof(RagGenerationStatus))]
[JsonSerializable(typeof(RagPendingStatus))]
public sealed partial class RagIngestionManagementJsonContext : JsonSerializerContext;
