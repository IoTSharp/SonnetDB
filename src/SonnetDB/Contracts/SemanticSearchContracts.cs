namespace SonnetDB.Contracts;

/// <summary>
/// 多模态语义检索运行状态。
/// </summary>
/// <param name="Enabled">配置是否启用。</param>
/// <param name="Ready">provider 是否具备运行所需的模型文件和参数。</param>
/// <param name="Provider">provider 名称。</param>
/// <param name="Profile">embedding profile 标识。</param>
/// <param name="Dimensions">embedding 维度。</param>
/// <param name="ConfiguredBackend">配置的 ANN 后端。</param>
/// <param name="EffectiveBackend">当前平台实际使用的 ANN 后端。</param>
/// <param name="Capabilities">provider 能力列表。</param>
/// <param name="Reason">未就绪或回退时的原因。</param>
public sealed record SemanticSearchStatusResponse(
    bool Enabled,
    bool Ready,
    string Provider,
    string Profile,
    int Dimensions,
    string ConfiguredBackend,
    string EffectiveBackend,
    IReadOnlyList<string> Capabilities,
    string? Reason = null);

/// <summary>
/// 文搜图请求。
/// </summary>
/// <param name="Text">用于生成查询向量的自然语言文本。</param>
/// <param name="TopK">返回结果上限；为空时使用服务器默认值。</param>
/// <param name="MinScore">可选的最小余弦相似度，范围 -1 到 1。</param>
public sealed record ImageTextSearchRequest(string Text, int? TopK = null, double? MinScore = null)
{
    /// <summary>可选的源对象 metadata/tag 精确过滤条件。</summary>
    public ImageSearchFilter? Filter { get; init; }

    /// <summary>是否返回候选数量和检索模式解释字段。</summary>
    public bool Explain { get; init; }
}

/// <summary>
/// 图片检索过滤条件；键值条件采用全部匹配语义。
/// </summary>
/// <param name="SourceBucket">源 Bucket 精确匹配条件。</param>
/// <param name="SourceKeyPrefix">源对象 Key 前缀条件。</param>
/// <param name="ContentType">图片媒体类型精确匹配条件。</param>
/// <param name="Metadata">源对象 metadata 全部匹配条件。</param>
/// <param name="Tags">源对象标签全部匹配条件。</param>
public sealed record ImageSearchFilter(
    string? SourceBucket = null,
    string? SourceKeyPrefix = null,
    string? ContentType = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyDictionary<string, string>? Tags = null);

/// <summary>
/// 基于已摄取图片执行 similar-by-id 的请求。
/// </summary>
/// <param name="TopK">返回结果上限；为空时使用服务器默认值。</param>
/// <param name="MinScore">可选的最小余弦相似度，范围 -1 到 1。</param>
public sealed record SimilarImageSearchRequest(int? TopK = null, double? MinScore = null)
{
    /// <summary>可选的源对象 metadata/tag 精确过滤条件。</summary>
    public ImageSearchFilter? Filter { get; init; }

    /// <summary>是否返回候选数量和检索模式解释字段。</summary>
    public bool Explain { get; init; }
}

/// <summary>
/// 图片摄取结果。
/// </summary>
/// <param name="Id">图片业务标识。</param>
/// <param name="FileName">原始文件名。</param>
/// <param name="ContentType">图片媒体类型。</param>
/// <param name="SizeBytes">图片字节数。</param>
/// <param name="Sha256">图片内容 SHA-256。</param>
/// <param name="Profile">生成向量所用的 embedding profile。</param>
/// <param name="Dimensions">向量维度。</param>
/// <param name="CreatedUtc">首次写入时间。</param>
/// <param name="UpdatedUtc">最近更新时间。</param>
public sealed record ImageIngestResponse(
    string Id,
    string? FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string Profile,
    int Dimensions,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// 图片元数据响应。
/// </summary>
/// <param name="Id">图片业务标识。</param>
/// <param name="FileName">原始文件名。</param>
/// <param name="ContentType">图片媒体类型。</param>
/// <param name="SizeBytes">图片字节数。</param>
/// <param name="Sha256">图片内容 SHA-256。</param>
/// <param name="SourceUri">可选来源地址，仅作为元数据保存。</param>
/// <param name="Profile">embedding profile 标识。</param>
/// <param name="Dimensions">向量维度。</param>
/// <param name="ContentUrl">读取原始图片内容的相对 URL。</param>
/// <param name="CreatedUtc">首次写入时间。</param>
/// <param name="UpdatedUtc">最近更新时间。</param>
public sealed record ImageInfoResponse(
    string Id,
    string? FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string? SourceUri,
    string Profile,
    int Dimensions,
    string ContentUrl,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    /// <summary>由 Bucket 异步摄取时的源 Bucket。</summary>
    public string? SourceBucket { get; init; }

    /// <summary>由 Bucket 异步摄取时的源对象 Key。</summary>
    public string? SourceKey { get; init; }

    /// <summary>源对象版本。</summary>
    public string? SourceVersionId { get; init; }

    /// <summary>已生成缩略图时的读取地址。</summary>
    public string? ThumbnailUrl { get; init; }

    /// <summary>源对象自定义 metadata。</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>源对象标签。</summary>
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
}

/// <summary>
/// 单条图片语义检索命中。
/// </summary>
/// <param name="Id">图片业务标识。</param>
/// <param name="Score">余弦相似度，越大越相似。</param>
/// <param name="Distance">余弦距离，越小越相似。</param>
/// <param name="FileName">原始文件名。</param>
/// <param name="ContentType">图片媒体类型。</param>
/// <param name="SizeBytes">图片字节数。</param>
/// <param name="Sha256">图片内容 SHA-256。</param>
/// <param name="SourceUri">可选来源地址。</param>
/// <param name="ContentUrl">读取原始图片内容的相对 URL。</param>
/// <param name="UpdatedUtc">图片最近更新时间。</param>
public sealed record ImageSearchHit(
    string Id,
    double Score,
    double Distance,
    string? FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string? SourceUri,
    string ContentUrl,
    DateTimeOffset UpdatedUtc)
{
    /// <summary>由 Bucket 异步摄取时的源 Bucket。</summary>
    public string? SourceBucket { get; init; }

    /// <summary>由 Bucket 异步摄取时的源对象 Key。</summary>
    public string? SourceKey { get; init; }

    /// <summary>源对象版本。</summary>
    public string? SourceVersionId { get; init; }

    /// <summary>已生成缩略图时的读取地址。</summary>
    public string? ThumbnailUrl { get; init; }

    /// <summary>源对象自定义 metadata。</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>源对象标签。</summary>
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
}

/// <summary>
/// 文搜图或图搜图响应。
/// </summary>
/// <param name="QueryKind">查询类型：<c>text</c> 或 <c>image</c>。</param>
/// <param name="Profile">本次查询使用的 embedding profile。</param>
/// <param name="Backend">本次查询实际使用的 ANN 后端；预过滤 ANN 为 <c>managed</c>，精确回退为 <c>exact-filtered</c>。</param>
/// <param name="Hits">按相似度降序排列的图片命中。</param>
public sealed record ImageSearchResponse(
    string QueryKind,
    string Profile,
    string Backend,
    IReadOnlyList<ImageSearchHit> Hits)
{
    /// <summary>解释模式下返回实际执行模式。</summary>
    public string? SearchMode { get; init; }

    /// <summary>解释模式下返回 ANN 或精确扫描产生的候选数量。</summary>
    public int? CandidateCount { get; init; }

    /// <summary>解释模式下返回通过 profile 与 metadata 过滤的候选数量。</summary>
    public int? FilteredCandidateCount { get; init; }
}

/// <summary>
/// 图片删除结果。
/// </summary>
/// <param name="Id">图片业务标识。</param>
/// <param name="Deleted">图片存在并删除时为 true。</param>
public sealed record ImageDeleteResponse(string Id, bool Deleted);

/// <summary>
/// Bucket 对象异步语义摄取与缩略图任务状态。
/// </summary>
public sealed record ObjectProcessingStatusResponse(
    string JobId,
    string Bucket,
    string Key,
    string VersionId,
    string Operation,
    string Status,
    bool SemanticRequested,
    bool ThumbnailRequested,
    int Attempts,
    string? Error,
    string? SemanticImageId,
    string? ThumbnailUrl,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? NextAttemptUtc);
