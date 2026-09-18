using System.Text.Json.Serialization;
using SonnetDB.Documents.Vector;
using SonnetDB.SemanticContent;
using SonnetDB.Query;

namespace SonnetDB.Data.VectorData;

/// <summary>向量预检的稳定诊断项。</summary>
/// <param name="Code">机器可读错误码。</param>
/// <param name="Message">中文诊断说明。</param>
public sealed record SndbVectorPreflightIssue(string Code, string Message);

/// <summary>基于实际 catalog 与可选持久 RAG profile 的预检结果，不代表查询结果或模型质量证据。</summary>
/// <param name="Collection">实际文档集合。</param>
/// <param name="Index">实际向量索引。</param>
/// <param name="Dimensions">catalog 声明的向量维度。</param>
/// <param name="Metric">catalog 声明的距离度量。</param>
/// <param name="IsIndexCompatible">查询向量与索引维度和度量是否兼容。</param>
/// <param name="IsProfileCompatible">完整持久 profile 是否一致；未绑定 profile 时为空。</param>
/// <param name="ProfileStatus">profile_unbound、verified 或 mismatch。</param>
/// <param name="GenerationId">核验的 RAG generation 身份；普通集合为空。</param>
/// <param name="ProfileId">持久 profile 标识；普通集合为空。</param>
/// <param name="Issues">预检发现的诊断项。</param>
public sealed record SndbVectorPreflightResult(
    string Collection,
    string Index,
    int Dimensions,
    KnnMetric Metric,
    bool IsIndexCompatible,
    bool? IsProfileCompatible,
    string ProfileStatus,
    string? GenerationId,
    string? ProfileId,
    IReadOnlyList<SndbVectorPreflightIssue> Issues);

/// <summary>向量生命周期诊断使用的 source-generated JSON 元数据。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SndbVectorPreflightResult))]
[JsonSerializable(typeof(DocumentVectorIndexHealth))]
[JsonSerializable(typeof(EmbeddingProfile))]
[JsonSerializable(typeof(KnnMetric), TypeInfoPropertyName = "DocumentKnnMetric")]
[JsonSerializable(typeof(SonnetDB.Vector.Primitives.KnnMetric), TypeInfoPropertyName = "ProfileKnnMetric")]
public sealed partial class SndbVectorLifecycleJsonContext : JsonSerializerContext;
