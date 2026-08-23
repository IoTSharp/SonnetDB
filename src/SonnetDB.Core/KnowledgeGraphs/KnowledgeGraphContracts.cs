using System.Text.Json.Serialization;

namespace SonnetDB.KnowledgeGraphs;

/// <summary>知识图谱节点类别。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeGraphNodeKind>))]
public enum KnowledgeGraphNodeKind : byte
{
    /// <summary>规范化实体。</summary>
    Entity = 1,

    /// <summary>指向规范化实体的别名。</summary>
    Alias = 2,

    /// <summary>带主语、谓词和宾语的声明。</summary>
    Claim = 3,

    /// <summary>Document 或 Object 中的权威来源。</summary>
    Source = 4,

    /// <summary>权威来源中的稳定分块。</summary>
    Chunk = 5,

    /// <summary>带算法结果版本的社区引用。</summary>
    Community = 6,

    /// <summary>存放在 Document/Object 中的社区摘要引用。</summary>
    Summary = 7,
}

/// <summary>知识图谱关系类别。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeGraphRelationKind>))]
public enum KnowledgeGraphRelationKind : byte
{
    /// <summary>实体发出一条声明。</summary>
    Asserts = 1,

    /// <summary>来源或分块支持一条声明。</summary>
    SupportedBy = 2,

    /// <summary>来源或分块反驳一条声明。</summary>
    Contradicts = 3,

    /// <summary>别名归一到实体。</summary>
    AliasOf = 4,

    /// <summary>分块属于权威来源。</summary>
    ChunkOf = 5,

    /// <summary>实体或声明属于社区。</summary>
    MemberOf = 6,

    /// <summary>社区引用一个摘要。</summary>
    SummarizedBy = 7,
}

/// <summary>承载权威内容的 SonnetDB 数据模型。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeContentStoreKind>))]
public enum KnowledgeContentStoreKind : byte
{
    /// <summary>Document collection。</summary>
    Document = 1,

    /// <summary>Object bucket。</summary>
    Object = 2,
}

/// <summary>
/// Document/Object 中权威内容的稳定引用。
/// 合同只保存引用和版本，不保存正文或对象字节。
/// </summary>
public sealed record KnowledgeContentReference
{
    /// <summary>创建空引用，供 source-generated JSON 反序列化使用。</summary>
    public KnowledgeContentReference()
    {
    }

    /// <summary>创建权威内容引用。</summary>
    /// <param name="storeKind">权威内容所在的数据模型。</param>
    /// <param name="container">Document collection 或 Object bucket 名称。</param>
    /// <param name="id">document ID 或 object key。</param>
    /// <param name="version">内容版本、ETag 或不可变 revision。</param>
    /// <param name="chunkId">可选的稳定 chunk ID。</param>
    /// <param name="contentHash">可选的内容 hash。</param>
    public KnowledgeContentReference(
        KnowledgeContentStoreKind storeKind,
        string container,
        string id,
        string version,
        string? chunkId = null,
        string? contentHash = null)
    {
        StoreKind = storeKind;
        Container = container;
        Id = id;
        Version = version;
        ChunkId = chunkId;
        ContentHash = contentHash;
    }

    /// <summary>权威内容所在的数据模型。</summary>
    public KnowledgeContentStoreKind StoreKind { get; init; }

    /// <summary>Document collection 或 Object bucket 名称。</summary>
    public string Container { get; init; } = string.Empty;

    /// <summary>document ID 或 object key。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>内容版本、ETag 或不可变 revision。</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>可选的稳定 chunk ID。</summary>
    public string? ChunkId { get; init; }

    /// <summary>可选的内容 hash。</summary>
    public string? ContentHash { get; init; }
}

/// <summary>
/// Vector 中派生 embedding 的稳定引用。
/// 合同不内嵌向量数组，embedding 的生命周期仍由 Vector 管理。
/// </summary>
public sealed record KnowledgeVectorReference
{
    /// <summary>创建空引用，供 source-generated JSON 反序列化使用。</summary>
    public KnowledgeVectorReference()
    {
    }

    /// <summary>创建 Vector 引用。</summary>
    /// <param name="index">Vector index 名称。</param>
    /// <param name="id">向量记录的稳定 ID。</param>
    /// <param name="profileId">不可变 embedding profile ID。</param>
    public KnowledgeVectorReference(string index, string id, string profileId)
    {
        Index = index;
        Id = id;
        ProfileId = profileId;
    }

    /// <summary>Vector index 名称。</summary>
    public string Index { get; init; } = string.Empty;

    /// <summary>向量记录的稳定 ID。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>不可变 embedding profile ID。</summary>
    public string ProfileId { get; init; } = string.Empty;
}

/// <summary>抽取、消歧、算法或 LLM job 的可追溯来源。</summary>
public sealed record KnowledgeProvenance
{
    /// <summary>创建空来源，供 source-generated JSON 反序列化使用。</summary>
    public KnowledgeProvenance()
    {
    }

    /// <summary>创建可追溯来源。</summary>
    /// <param name="producer">产生结果的 job/provider 名称。</param>
    /// <param name="revision">job、规则、模型或 prompt revision。</param>
    /// <param name="runId">一次可审计运行的稳定 ID。</param>
    /// <param name="observedAtUtc">产生或观察结果的 UTC 时间。</param>
    /// <param name="source">可选的权威内容来源。</param>
    public KnowledgeProvenance(
        string producer,
        string revision,
        string runId,
        DateTimeOffset observedAtUtc,
        KnowledgeContentReference? source = null)
    {
        Producer = producer;
        Revision = revision;
        RunId = runId;
        ObservedAtUtc = observedAtUtc;
        Source = source;
    }

    /// <summary>产生结果的 job/provider 名称。</summary>
    public string Producer { get; init; } = string.Empty;

    /// <summary>job、规则、模型或 prompt revision。</summary>
    public string Revision { get; init; } = string.Empty;

    /// <summary>一次可审计运行的稳定 ID。</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>产生或观察结果的 UTC 时间。</summary>
    public DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>可选的权威内容来源及 chunk。</summary>
    public KnowledgeContentReference? Source { get; init; }
}

/// <summary>知识事实的半开有效时间区间。</summary>
public sealed record KnowledgeValidTime
{
    /// <summary>创建无界有效时间，供 source-generated JSON 反序列化使用。</summary>
    public KnowledgeValidTime()
    {
    }

    /// <summary>创建半开有效时间区间。</summary>
    /// <param name="validFromUtc">可选起点，inclusive。</param>
    /// <param name="validToUtc">可选终点，exclusive。</param>
    public KnowledgeValidTime(DateTimeOffset? validFromUtc, DateTimeOffset? validToUtc)
    {
        ValidFromUtc = validFromUtc;
        ValidToUtc = validToUtc;
    }

    /// <summary>无界有效时间。</summary>
    public static KnowledgeValidTime Unbounded { get; } = new();

    /// <summary>可选起点，inclusive。</summary>
    public DateTimeOffset? ValidFromUtc { get; init; }

    /// <summary>可选终点，exclusive。</summary>
    public DateTimeOffset? ValidToUtc { get; init; }
}

/// <summary>声明节点的结构化主语、谓词与宾语。</summary>
public sealed record KnowledgeClaimValue
{
    /// <summary>创建空声明，供 source-generated JSON 反序列化使用。</summary>
    public KnowledgeClaimValue()
    {
    }

    /// <summary>创建以实体为宾语的声明。</summary>
    /// <param name="subjectId">主语实体的外部 ID。</param>
    /// <param name="predicate">稳定谓词。</param>
    /// <param name="objectId">宾语实体的外部 ID。</param>
    public KnowledgeClaimValue(string subjectId, string predicate, string objectId)
    {
        SubjectId = subjectId;
        Predicate = predicate;
        ObjectId = objectId;
    }

    /// <summary>主语实体的外部 ID。</summary>
    public string SubjectId { get; init; } = string.Empty;

    /// <summary>稳定谓词。</summary>
    public string Predicate { get; init; } = string.Empty;

    /// <summary>可选的宾语实体外部 ID；与 <see cref="LiteralValue"/> 二选一。</summary>
    public string? ObjectId { get; init; }

    /// <summary>可选的小型标量宾语；不得承载正文或媒体。</summary>
    public string? LiteralValue { get; init; }
}

/// <summary>离线 community 结果的可追溯引用。</summary>
public sealed record KnowledgeCommunityReference
{
    /// <summary>创建空引用，供 source-generated JSON 反序列化使用。</summary>
    public KnowledgeCommunityReference()
    {
    }

    /// <summary>创建 community 结果引用。</summary>
    /// <param name="resultVersion">例如 operationId@sourceSequence 的稳定结果版本。</param>
    /// <param name="algorithm">产生 community 的算法和 revision。</param>
    /// <param name="sourceSequence">可选的 Graph statement snapshot sequence。</param>
    public KnowledgeCommunityReference(
        string resultVersion,
        string algorithm,
        long? sourceSequence = null)
    {
        ResultVersion = resultVersion;
        Algorithm = algorithm;
        SourceSequence = sourceSequence;
    }

    /// <summary>例如 operationId@sourceSequence 的稳定结果版本。</summary>
    public string ResultVersion { get; init; } = string.Empty;

    /// <summary>产生 community 的算法和 revision。</summary>
    public string Algorithm { get; init; } = string.Empty;

    /// <summary>可选的 Graph statement snapshot sequence。</summary>
    public long? SourceSequence { get; init; }
}

/// <summary>待投影到通用属性图的知识节点。</summary>
public sealed record KnowledgeGraphNode
{
    /// <summary>创建空节点，供 source-generated JSON 反序列化使用。</summary>
    public KnowledgeGraphNode()
    {
    }

    /// <summary>创建最小知识节点。</summary>
    /// <param name="id">稳定外部 ID。</param>
    /// <param name="kind">节点类别。</param>
    /// <param name="provenance">产生该节点的来源。</param>
    public KnowledgeGraphNode(string id, KnowledgeGraphNodeKind kind, KnowledgeProvenance provenance)
    {
        Id = id;
        Kind = kind;
        Provenance = provenance;
    }

    /// <summary>稳定外部 ID。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>节点类别。</summary>
    public KnowledgeGraphNodeKind Kind { get; init; }

    /// <summary>更新时预期的 Graph element version；新建时为 0。</summary>
    public long ExpectedElementVersion { get; init; }

    /// <summary>实体规范名或 alias 文本；不用于保存正文。</summary>
    public string? Name { get; init; }

    /// <summary>Claim 节点的结构化声明。</summary>
    public KnowledgeClaimValue? Claim { get; init; }

    /// <summary>Source、Chunk 或 Summary 节点的权威内容引用。</summary>
    public KnowledgeContentReference? Content { get; init; }

    /// <summary>可选的 Vector 派生记录引用。</summary>
    public KnowledgeVectorReference? Vector { get; init; }

    /// <summary>Community 或 Summary 节点引用的算法结果版本。</summary>
    public KnowledgeCommunityReference? Community { get; init; }

    /// <summary>产生该节点的来源。</summary>
    public KnowledgeProvenance? Provenance { get; init; }

    /// <summary>可选置信度，范围为 0 到 1。</summary>
    public double? Confidence { get; init; }

    /// <summary>可选有效时间；Claim 和 Alias 必须显式提供，包括无界区间。</summary>
    public KnowledgeValidTime? ValidTime { get; init; }
}

/// <summary>待投影到通用属性图的知识关系。</summary>
public sealed record KnowledgeGraphRelation
{
    /// <summary>创建空关系，供 source-generated JSON 反序列化使用。</summary>
    public KnowledgeGraphRelation()
    {
    }

    /// <summary>创建最小知识关系。</summary>
    /// <param name="id">稳定外部 ID。</param>
    /// <param name="kind">关系类别。</param>
    /// <param name="sourceId">源节点外部 ID。</param>
    /// <param name="targetId">目标节点外部 ID。</param>
    /// <param name="provenance">产生该关系的来源。</param>
    public KnowledgeGraphRelation(
        string id,
        KnowledgeGraphRelationKind kind,
        string sourceId,
        string targetId,
        KnowledgeProvenance provenance)
    {
        Id = id;
        Kind = kind;
        SourceId = sourceId;
        TargetId = targetId;
        Provenance = provenance;
    }

    /// <summary>稳定外部 ID。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>关系类别。</summary>
    public KnowledgeGraphRelationKind Kind { get; init; }

    /// <summary>源节点外部 ID。</summary>
    public string SourceId { get; init; } = string.Empty;

    /// <summary>目标节点外部 ID。</summary>
    public string TargetId { get; init; } = string.Empty;

    /// <summary>更新时预期的 Graph element version；新建时为 0。</summary>
    public long ExpectedElementVersion { get; init; }

    /// <summary>产生该关系的来源。</summary>
    public KnowledgeProvenance? Provenance { get; init; }

    /// <summary>可选置信度，范围为 0 到 1。</summary>
    public double? Confidence { get; init; }

    /// <summary>可选有效时间；事实/证据关系必须显式提供，包括无界区间。</summary>
    public KnowledgeValidTime? ValidTime { get; init; }
}

/// <summary>一个有界、幂等的知识图谱 upsert 批次。</summary>
public sealed record KnowledgeGraphBatch
{
    /// <summary>创建空批次，供 source-generated JSON 反序列化使用。</summary>
    public KnowledgeGraphBatch()
    {
    }

    /// <summary>创建知识图谱批次。</summary>
    /// <param name="requestId">映射到 Graph 原子事务的幂等 request ID。</param>
    /// <param name="nodes">待 upsert 节点。</param>
    /// <param name="relations">待 upsert 关系。</param>
    public KnowledgeGraphBatch(
        Guid requestId,
        IReadOnlyList<KnowledgeGraphNode> nodes,
        IReadOnlyList<KnowledgeGraphRelation> relations)
    {
        RequestId = requestId;
        Nodes = nodes;
        Relations = relations;
    }

    /// <summary>合同版本；M40 #365 固定为 1。</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>映射到 Graph 原子事务的幂等 request ID。</summary>
    public Guid RequestId { get; init; }

    /// <summary>待 upsert 节点。</summary>
    public IReadOnlyList<KnowledgeGraphNode> Nodes { get; init; } = [];

    /// <summary>待 upsert 关系。</summary>
    public IReadOnlyList<KnowledgeGraphRelation> Relations { get; init; } = [];
}

/// <summary>知识图谱合同校验失败。</summary>
public sealed record KnowledgeGraphValidationFailure
{
    /// <summary>创建结构化校验失败。</summary>
    /// <param name="path">失败字段路径。</param>
    /// <param name="rule">稳定规则代码。</param>
    /// <param name="message">面向调用方的错误说明。</param>
    public KnowledgeGraphValidationFailure(string path, string rule, string message)
    {
        Path = path;
        Rule = rule;
        Message = message;
    }

    /// <summary>失败字段路径。</summary>
    public string Path { get; init; }

    /// <summary>稳定规则代码。</summary>
    public string Rule { get; init; }

    /// <summary>面向调用方的错误说明。</summary>
    public string Message { get; init; }
}

/// <summary>知识图谱合同校验结果。</summary>
public sealed record KnowledgeGraphValidationResult
{
    /// <summary>创建校验结果。</summary>
    /// <param name="isValid">批次是否有效。</param>
    /// <param name="failures">结构化失败列表。</param>
    public KnowledgeGraphValidationResult(
        bool isValid,
        IReadOnlyList<KnowledgeGraphValidationFailure> failures)
    {
        IsValid = isValid;
        Failures = failures;
    }

    /// <summary>共享的成功结果。</summary>
    public static KnowledgeGraphValidationResult Valid { get; }
        = new(true, Array.Empty<KnowledgeGraphValidationFailure>());

    /// <summary>批次是否有效。</summary>
    public bool IsValid { get; init; }

    /// <summary>结构化失败列表。</summary>
    public IReadOnlyList<KnowledgeGraphValidationFailure> Failures { get; init; }
}
