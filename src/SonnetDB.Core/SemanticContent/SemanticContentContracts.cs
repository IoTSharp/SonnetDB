using System.Text.Json.Serialization;
using SonnetDB.Vector.Primitives;

namespace SonnetDB.SemanticContent;

/// <summary>
/// 语义内容的输入模态。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SemanticContentModality>))]
public enum SemanticContentModality : byte
{
    /// <summary>未指定模态。</summary>
    Unknown = 0,

    /// <summary>纯文本内容。</summary>
    Text = 1,

    /// <summary>文档内容（例如 Markdown、HTML 或 PDF 的文本表示）。</summary>
    Document = 2,

    /// <summary>图片内容。</summary>
    Image = 3,

    /// <summary>音频内容。</summary>
    Audio = 4,

    /// <summary>视频内容。</summary>
    Video = 5,

    /// <summary>尚未细分的对象内容。</summary>
    Object = 6,
}

/// <summary>
/// embedding 输出的归一化方式。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<EmbeddingNormalization>))]
public enum EmbeddingNormalization : byte
{
    /// <summary>不对 provider 输出做归一化声明。</summary>
    None = 0,

    /// <summary>向量按 L2 范数归一化为单位向量。</summary>
    L2 = 1,
}

/// <summary>
/// embedding provider 可接受的内容外发模式。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SemanticDataEgressMode>))]
public enum SemanticDataEgressMode : byte
{
    /// <summary>只允许在本地处理，不向外部 provider 发送内容。</summary>
    LocalOnly = 0,

    /// <summary>只允许发送到显式配置的 provider。</summary>
    ConfiguredProvider = 1,

    /// <summary>允许发送到外部 provider；仍需经过调用方治理。</summary>
    ExternalProvider = 2,
}

/// <summary>
/// 语义内容派生索引状态。
/// 该状态描述索引/派生数据，不等同于异步任务的调度状态。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SemanticIndexState>))]
public enum SemanticIndexState : byte
{
    /// <summary>尚未开始生成派生数据。</summary>
    Pending = 0,

    /// <summary>正在生成或发布派生数据。</summary>
    Running = 1,

    /// <summary>当前 profile 的派生数据可用于检索。</summary>
    Ready = 2,

    /// <summary>源内容已经变化或删除，派生数据不可再视为新鲜。</summary>
    Stale = 3,

    /// <summary>最近一次派生失败；可通过重新排队恢复。</summary>
    Failed = 4,
}

/// <summary>
/// 内容外发策略。
/// </summary>
public sealed record SemanticDataEgressPolicy
{
    /// <summary>创建默认的本地处理策略。</summary>
    public SemanticDataEgressPolicy()
    {
    }

    /// <summary>
    /// 创建内容外发策略。
    /// </summary>
    /// <param name="mode">外发模式。</param>
    /// <param name="target">允许的 provider 或目标标识。</param>
    /// <param name="auditRequired">是否要求调用方记录审计。</param>
    public SemanticDataEgressPolicy(
        SemanticDataEgressMode mode,
        string? target = null,
        bool auditRequired = true)
    {
        Mode = mode;
        Target = target;
        AuditRequired = auditRequired;
    }

    /// <summary>外发模式。</summary>
    public SemanticDataEgressMode Mode { get; init; }

    /// <summary>允许的 provider 或外部目标；本地模式下为空。</summary>
    public string? Target { get; init; }

    /// <summary>是否要求调用方记录每次 provider 调用。</summary>
    public bool AuditRequired { get; init; } = true;

    /// <summary>本地-only 默认策略。</summary>
    public static SemanticDataEgressPolicy LocalOnly { get; }
        = new(SemanticDataEgressMode.LocalOnly);
}

/// <summary>
/// 原始对象在 Object Bucket 中的稳定引用。
/// 原始字节只允许从该引用读取，语义内容记录不得复制大对象字节。
/// </summary>
public sealed record SemanticObjectReference
{
    /// <summary>创建空引用，供 source-generated JSON 反序列化使用。</summary>
    public SemanticObjectReference()
    {
    }

    /// <summary>
    /// 创建对象引用。
    /// </summary>
    /// <param name="bucket">对象桶名称。</param>
    /// <param name="key">对象键。</param>
    /// <param name="versionId">对象版本标识。</param>
    /// <param name="eTag">对象 ETag。</param>
    public SemanticObjectReference(
        string bucket,
        string key,
        string? versionId = null,
        string? eTag = null)
    {
        Bucket = bucket;
        Key = key;
        VersionId = versionId;
        ETag = eTag;
    }

    /// <summary>对象桶名称。</summary>
    public string Bucket { get; init; } = string.Empty;

    /// <summary>对象键。</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>对象版本标识；不使用版本化对象桶时可以为空。</summary>
    public string? VersionId { get; init; }

    /// <summary>对象 ETag；由存储层提供时用于幂等校验。</summary>
    public string? ETag { get; init; }

    /// <summary>兼容部分调用方使用的版本别名。</summary>
    [JsonIgnore]
    public string? Version => VersionId;
}

/// <summary>
/// 文档或文本内容的稳定分块。
/// Offset 使用源文本的 UTF-16 字符偏移；不提供偏移时两个值均为空。
/// </summary>
public sealed record SemanticContentChunk
{
    /// <summary>创建空分块，供 source-generated JSON 反序列化使用。</summary>
    public SemanticContentChunk()
    {
    }

    /// <summary>
    /// 创建文本分块。
    /// </summary>
    /// <param name="id">稳定分块标识；同一内容版本内唯一。</param>
    /// <param name="ordinal">分块在其父内容中的稳定顺序。</param>
    /// <param name="text">分块文本。</param>
    /// <param name="startOffset">源文本起始偏移（inclusive）。</param>
    /// <param name="endOffset">源文本结束偏移（exclusive）。</param>
    /// <param name="contentHash">分块文本 hash，可用于增量重建。</param>
    public SemanticContentChunk(
        string id,
        int ordinal,
        string text,
        long? startOffset = null,
        long? endOffset = null,
        string? contentHash = null)
    {
        Id = id;
        Ordinal = ordinal;
        Text = text;
        StartOffset = startOffset;
        EndOffset = endOffset;
        ContentHash = contentHash;
    }

    /// <summary>稳定分块标识。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>分块顺序；不要求在内容更新后保持连续。</summary>
    public int Ordinal { get; init; }

    /// <summary>分块文本。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>源文本起始偏移（inclusive）。</summary>
    public long? StartOffset { get; init; }

    /// <summary>源文本结束偏移（exclusive）。</summary>
    public long? EndOffset { get; init; }

    /// <summary>分块文本 hash。</summary>
    public string? ContentHash { get; init; }

    /// <summary>可选的标题或章节标识。</summary>
    public string? Section { get; init; }

    /// <summary>稳定标识别名，便于 RAG 引用代码使用。</summary>
    [JsonIgnore]
    public string StableId => Id;

    /// <summary>ChunkId 别名，便于 SDK 迁移。</summary>
    [JsonIgnore]
    public string ChunkId => Id;
}

/// <summary>
/// 音视频或关键帧内容的时间分段。
/// 时间单位为毫秒，结束时间为 exclusive。
/// </summary>
public sealed record SemanticContentSegment
{
    /// <summary>创建空分段，供 source-generated JSON 反序列化使用。</summary>
    public SemanticContentSegment()
    {
    }

    /// <summary>
    /// 创建时间分段。
    /// </summary>
    /// <param name="id">稳定分段标识；同一内容版本内唯一。</param>
    /// <param name="ordinal">分段在其父内容中的稳定顺序。</param>
    /// <param name="startMs">起始时间（inclusive）。</param>
    /// <param name="endMs">结束时间（exclusive）。</param>
    /// <param name="text">可选的 transcript/OCR 文本。</param>
    public SemanticContentSegment(
        string id,
        int ordinal,
        long startMs,
        long endMs,
        string? text = null)
    {
        Id = id;
        Ordinal = ordinal;
        StartMs = startMs;
        EndMs = endMs;
        Text = text;
    }

    /// <summary>稳定分段标识。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>分段顺序；不要求在内容更新后保持连续。</summary>
    public int Ordinal { get; init; }

    /// <summary>起始时间（毫秒，inclusive）。</summary>
    public long StartMs { get; init; }

    /// <summary>结束时间（毫秒，exclusive）。</summary>
    public long EndMs { get; init; }

    /// <summary>可选的 transcript/OCR 文本。</summary>
    public string? Text { get; init; }

    /// <summary>可选的关键帧编号。</summary>
    public long? FrameIndex { get; init; }

    /// <summary>可选的关键帧对象引用；关键帧仍是派生对象而非主数据。</summary>
    public SemanticObjectReference? KeyFrameRef { get; init; }

    /// <summary>分段文本或关键帧的 hash。</summary>
    public string? ContentHash { get; init; }

    /// <summary>稳定标识别名，便于媒体检索调用方迁移。</summary>
    [JsonIgnore]
    public string StableId => Id;

    /// <summary>SegmentId 别名。</summary>
    [JsonIgnore]
    public string SegmentId => Id;
}

/// <summary>
/// 内容清单中一个命名向量派生的绑定信息。
/// 向量本体和 ANN 结构属于可重建派生数据，不在该合同中内嵌大数组。
/// </summary>
public sealed record SemanticEmbeddingBinding
{
    /// <summary>创建空绑定，供 source-generated JSON 反序列化使用。</summary>
    public SemanticEmbeddingBinding()
    {
    }

    /// <summary>创建命名向量绑定。</summary>
    /// <param name="name">向量字段的逻辑名称。</param>
    /// <param name="profileId">生成该向量的不可变 profile 标识。</param>
    /// <param name="vectorField">Document 中保存向量的字段路径。</param>
    public SemanticEmbeddingBinding(
        string name,
        string profileId,
        string? vectorField = null)
    {
        Name = name;
        ProfileId = profileId;
        VectorField = vectorField;
    }

    /// <summary>向量字段的逻辑名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>生成该向量的 profile 标识。</summary>
    public string ProfileId { get; init; } = string.Empty;

    /// <summary>Document 中保存向量的字段路径。</summary>
    public string? VectorField { get; init; }

    /// <summary>该命名向量自己的派生索引状态。</summary>
    public SemanticIndexStateInfo IndexState { get; init; } = SemanticIndexStateInfo.Pending;
}

/// <summary>
/// Embedding Profile 的不可变兼容边界。
/// </summary>
public sealed record EmbeddingProfile
{
    /// <summary>创建空 profile，供 source-generated JSON 反序列化使用。</summary>
    public EmbeddingProfile()
    {
    }

    /// <summary>
    /// 创建 embedding profile。
    /// </summary>
    /// <param name="id">不可变 profile 标识。</param>
    /// <param name="provider">provider 名称。</param>
    /// <param name="model">模型名称。</param>
    /// <param name="revision">模型或预处理版本。</param>
    /// <param name="dimensions">输出向量维度。</param>
    /// <param name="metric">向量距离度量。</param>
    /// <param name="normalization">输出归一化方式。</param>
    /// <param name="supportedModalities">provider 支持的输入模态。</param>
    /// <param name="dataEgressPolicy">内容外发策略。</param>
    public EmbeddingProfile(
        string id,
        string provider,
        string model,
        string revision,
        int dimensions,
        KnnMetric metric = KnnMetric.Cosine,
        EmbeddingNormalization normalization = EmbeddingNormalization.None,
        IReadOnlyList<SemanticContentModality>? supportedModalities = null,
        SemanticDataEgressPolicy? dataEgressPolicy = null)
    {
        Id = id;
        Provider = provider;
        Model = model;
        Revision = revision;
        Dimensions = dimensions;
        Metric = metric;
        Normalization = normalization;
        SupportedModalities = supportedModalities ?? Array.Empty<SemanticContentModality>();
        DataEgressPolicy = dataEgressPolicy ?? SemanticDataEgressPolicy.LocalOnly;
    }

    /// <summary>不可变 profile 标识。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>provider 名称。</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>模型名称。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>模型或预处理版本。</summary>
    public string Revision { get; init; } = string.Empty;

    /// <summary>输出向量维度。</summary>
    public int Dimensions { get; init; }

    /// <summary>向量距离度量。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<KnnMetric>))]
    public KnnMetric Metric { get; init; } = KnnMetric.Cosine;

    /// <summary>输出归一化方式。</summary>
    public EmbeddingNormalization Normalization { get; init; }

    /// <summary>provider 支持的输入模态。</summary>
    public IReadOnlyList<SemanticContentModality> SupportedModalities { get; init; }
        = Array.Empty<SemanticContentModality>();

    /// <summary>内容外发策略。</summary>
    public SemanticDataEgressPolicy DataEgressPolicy { get; init; }
        = SemanticDataEgressPolicy.LocalOnly;

    /// <summary>ProfileId 别名，便于旧客户端逐步迁移。</summary>
    [JsonIgnore]
    public string ProfileId => Id;

    /// <summary>
    /// 判断两个 profile 是否可以在同一查询空间中比较。
    /// </summary>
    /// <param name="other">待比较的 profile。</param>
    /// <returns>兼容时为 true。</returns>
    public bool IsCompatibleWith(EmbeddingProfile? other)
        => other is not null
            && string.Equals(Id, other.Id, StringComparison.Ordinal)
            && Dimensions == other.Dimensions
            && Metric == other.Metric
            && Normalization == other.Normalization;

    /// <summary>
    /// 判断 profile 是否声明支持指定模态。
    /// </summary>
    /// <param name="modality">待检查的模态。</param>
    /// <returns>支持时为 true。</returns>
    public bool Supports(SemanticContentModality modality)
        => SupportedModalities.Contains(modality);
}

/// <summary>
/// Semantic Content 的索引状态快照。
/// </summary>
public sealed record SemanticIndexStateInfo
{
    /// <summary>创建空状态，供 source-generated JSON 反序列化使用。</summary>
    public SemanticIndexStateInfo()
    {
    }

    /// <summary>创建索引状态快照。</summary>
    /// <param name="state">索引状态。</param>
    /// <param name="attempt">当前尝试次数。</param>
    /// <param name="lastError">最近一次失败信息。</param>
    /// <param name="updatedUtc">状态更新时间。</param>
    public SemanticIndexStateInfo(
        SemanticIndexState state,
        int attempt = 0,
        string? lastError = null,
        DateTimeOffset updatedUtc = default)
    {
        State = state;
        Attempt = attempt;
        LastError = lastError;
        UpdatedUtc = updatedUtc == default ? DateTimeOffset.UtcNow : updatedUtc;
    }

    /// <summary>默认 pending 状态。</summary>
    public static SemanticIndexStateInfo Pending { get; }
        = new(SemanticIndexState.Pending, updatedUtc: DateTimeOffset.UnixEpoch);

    /// <summary>索引状态。</summary>
    public SemanticIndexState State { get; init; }

    /// <summary>当前重建尝试次数。</summary>
    public int Attempt { get; init; }

    /// <summary>最近一次失败信息；非 failed 状态通常为空。</summary>
    public string? LastError { get; init; }

    /// <summary>状态更新时间（UTC）。</summary>
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UnixEpoch;
}

/// <summary>
/// Semantic Content 内容清单。
/// 原始媒体由 <see cref="SemanticObjectReference"/> 唯一指向；文本、分块、分段和向量均为可重建派生数据。
/// </summary>
public sealed record SemanticContentManifest
{
    /// <summary>创建空清单，供 source-generated JSON 反序列化使用。</summary>
    public SemanticContentManifest()
    {
    }

    /// <summary>
    /// 创建最小内容清单。
    /// </summary>
    /// <param name="id">内容清单在数据库内的稳定标识。</param>
    /// <param name="objectRef">原始对象引用。</param>
    /// <param name="contentHash">原始内容 hash。</param>
    /// <param name="mimeType">原始内容 MIME 类型。</param>
    /// <param name="modality">内容模态。</param>
    /// <param name="sizeBytes">原始内容字节数。</param>
    /// <param name="source">可选业务来源标识。</param>
    /// <param name="embeddingProfileId">默认 embedding profile 标识。</param>
    public SemanticContentManifest(
        string id,
        SemanticObjectReference objectRef,
        string contentHash,
        string mimeType,
        SemanticContentModality modality,
        long sizeBytes,
        string? source = null,
        string? embeddingProfileId = null)
    {
        Id = id;
        ObjectRef = objectRef;
        ContentHash = contentHash;
        MimeType = mimeType;
        Modality = modality;
        SizeBytes = sizeBytes;
        Source = source;
        EmbeddingProfileId = embeddingProfileId;
    }

    /// <summary>合同版本；只允许向后兼容地递增。</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>内容清单在数据库内的稳定标识。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>原始对象引用。</summary>
    public SemanticObjectReference? ObjectRef { get; init; }

    /// <summary>原始内容 hash，用于幂等和覆盖检测。</summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>原始内容 MIME 类型。</summary>
    public string MimeType { get; init; } = string.Empty;

    /// <summary>内容模态。</summary>
    public SemanticContentModality Modality { get; init; }

    /// <summary>原始内容字节数。</summary>
    public long SizeBytes { get; init; }

    /// <summary>可选业务来源标识。</summary>
    public string? Source { get; init; }

    /// <summary>默认 embedding profile 标识。</summary>
    public string? EmbeddingProfileId { get; init; }

    /// <summary>内容级派生索引状态。</summary>
    public SemanticIndexStateInfo IndexState { get; init; } = SemanticIndexStateInfo.Pending;

    /// <summary>可选的 OCR、caption 或 transcript 文本。</summary>
    public string? Text { get; init; }

    /// <summary>稳定文本分块。</summary>
    public IReadOnlyList<SemanticContentChunk> Chunks { get; init; }
        = Array.Empty<SemanticContentChunk>();

    /// <summary>稳定时间分段。</summary>
    public IReadOnlyList<SemanticContentSegment> Segments { get; init; }
        = Array.Empty<SemanticContentSegment>();

    /// <summary>命名向量字段与 profile 的绑定。</summary>
    public IReadOnlyList<SemanticEmbeddingBinding> Embeddings { get; init; }
        = Array.Empty<SemanticEmbeddingBinding>();

    /// <summary>首次创建时间（UTC）。</summary>
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UnixEpoch;

    /// <summary>最近更新时间（UTC）。</summary>
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UnixEpoch;

    /// <summary>ProfileId 别名，便于旧客户端迁移。</summary>
    [JsonIgnore]
    public string? ProfileId => EmbeddingProfileId;
}

/// <summary>
/// 内容清单的校验结果。
/// </summary>
/// <param name="IsValid">是否通过校验。</param>
/// <param name="Failures">结构化失败明细。</param>
public sealed record SemanticContentValidationResult(
    bool IsValid,
    IReadOnlyList<SemanticContentValidationFailure> Failures)
{
    /// <summary>无失败的共享结果。</summary>
    public static SemanticContentValidationResult Valid { get; }
        = new(true, Array.Empty<SemanticContentValidationFailure>());
}

/// <summary>
/// 内容清单校验失败明细。
/// </summary>
/// <param name="Path">失败字段路径。</param>
/// <param name="Rule">失败规则标识。</param>
/// <param name="Message">面向调用方的说明。</param>
public sealed record SemanticContentValidationFailure(
    string Path,
    string Rule,
    string Message);
