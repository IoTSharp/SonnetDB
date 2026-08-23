using System.Text.Json.Serialization;

namespace SonnetDB.KnowledgeGraphs;

/// <summary>知识图谱上层合同的 Native AOT JSON 元数据。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(KnowledgeContentReference))]
[JsonSerializable(typeof(KnowledgeVectorReference))]
[JsonSerializable(typeof(KnowledgeProvenance))]
[JsonSerializable(typeof(KnowledgeValidTime))]
[JsonSerializable(typeof(KnowledgeClaimValue))]
[JsonSerializable(typeof(KnowledgeCommunityReference))]
[JsonSerializable(typeof(KnowledgeGraphNode))]
[JsonSerializable(typeof(KnowledgeGraphRelation))]
[JsonSerializable(typeof(KnowledgeGraphBatch))]
[JsonSerializable(typeof(KnowledgeGraphValidationFailure))]
[JsonSerializable(typeof(KnowledgeGraphValidationResult))]
[JsonSerializable(typeof(IReadOnlyList<KnowledgeGraphNode>))]
[JsonSerializable(typeof(IReadOnlyList<KnowledgeGraphRelation>))]
[JsonSerializable(typeof(IReadOnlyList<KnowledgeGraphValidationFailure>))]
public sealed partial class KnowledgeGraphJsonContext : JsonSerializerContext;
