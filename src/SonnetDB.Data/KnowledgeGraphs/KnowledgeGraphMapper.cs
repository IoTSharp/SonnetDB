using SonnetDB.Data.Graphs;
using SonnetDB.Graphs;
using SonnetDB.KnowledgeGraphs;

namespace SonnetDB.Data.KnowledgeGraphs;

/// <summary>
/// 把 M40 #365 知识图谱合同确定性投影为通用 Graph V1 导入请求。
/// </summary>
/// <remarks>
/// 投影只保存小型 typed property 和其他模型的稳定引用，不改变 Graph record 格式，
/// 也不复制 Document/Object 正文或 Vector embedding。
/// </remarks>
public static class KnowledgeGraphMapper
{
    /// <summary>当前知识图谱到通用属性图的稳定投影版本。</summary>
    public const string ProjectionVersion = "m40-kg-v1";

    /// <summary>把知识节点外部 ID 映射为稳定 Graph vertex ID。</summary>
    /// <param name="externalId">知识节点外部 ID。</param>
    /// <returns>确定性的正数 Graph ID。</returns>
    public static GraphElementId GetNodeId(string externalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        return SndbGraphImporter.GetStableElementId($"{ProjectionVersion}:node:{externalId}");
    }

    /// <summary>把知识关系外部 ID 映射为稳定 Graph edge ID。</summary>
    /// <param name="externalId">知识关系外部 ID。</param>
    /// <returns>确定性的正数 Graph ID。</returns>
    public static GraphElementId GetRelationId(string externalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        return SndbGraphImporter.GetStableElementId($"{ProjectionVersion}:relation:{externalId}");
    }

    /// <summary>返回节点类别对应的稳定 Graph label ID。</summary>
    /// <param name="kind">知识节点类别。</param>
    /// <returns>投影 label ID。</returns>
    public static LabelId GetNodeLabelId(KnowledgeGraphNodeKind kind)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        return SndbGraphImporter.GetStableLabelId(GetNodeLabel(kind));
    }

    /// <summary>返回关系类别对应的稳定 Graph label ID。</summary>
    /// <param name="kind">知识关系类别。</param>
    /// <returns>投影 label ID。</returns>
    public static LabelId GetRelationLabelId(KnowledgeGraphRelationKind kind)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        return SndbGraphImporter.GetStableLabelId(GetRelationLabel(kind));
    }

    /// <summary>把通过校验的知识图谱批次编译为现有 Graph 原子导入请求。</summary>
    /// <param name="batch">知识图谱 upsert 批次。</param>
    /// <returns>可直接交给 <see cref="SndbGraphClient.ImportAsync"/> 的请求。</returns>
    public static GraphImportRequest ToGraphImportRequest(KnowledgeGraphBatch batch)
    {
        KnowledgeGraphValidator.ValidateOrThrow(batch);

        var nodeIds = new Dictionary<long, string>();
        GraphImportVertexDto[] vertices = batch.Nodes.Select(node =>
        {
            GraphElementId id = GetNodeId(node.Id);
            EnsureNoStableIdCollision(nodeIds, id.Value, node.Id, "node");
            List<GraphPropertyDto> properties = CreateCommonProperties(
                node.Id,
                node.Kind.ToString(),
                node.Provenance!,
                node.Confidence,
                node.ValidTime);
            AddString(properties, KnowledgeGraphVocabulary.Name, node.Name);
            AddClaim(properties, node.Claim);
            AddContent(properties, KnowledgeGraphVocabulary.ContentPrefix, node.Content);
            AddVector(properties, node.Vector);
            AddCommunity(properties, node.Community);
            return new GraphImportVertexDto
            {
                Id = id.Value,
                Labels = [GetNodeLabelId(node.Kind).Value],
                Properties = SortProperties(properties),
                UniquePropertyIds = [PropertyId(KnowledgeGraphVocabulary.ExternalId)],
                ExpectedElementVersion = node.ExpectedElementVersion,
            };
        }).ToArray();

        var relationIds = new Dictionary<long, string>();
        GraphImportEdgeDto[] edges = batch.Relations.Select(relation =>
        {
            GraphElementId id = GetRelationId(relation.Id);
            EnsureNoStableIdCollision(relationIds, id.Value, relation.Id, "relation");
            List<GraphPropertyDto> properties = CreateCommonProperties(
                relation.Id,
                relation.Kind.ToString(),
                relation.Provenance!,
                relation.Confidence,
                relation.ValidTime);
            return new GraphImportEdgeDto
            {
                Id = id.Value,
                SourceId = GetNodeId(relation.SourceId).Value,
                TargetId = GetNodeId(relation.TargetId).Value,
                LabelId = GetRelationLabelId(relation.Kind).Value,
                Properties = SortProperties(properties),
                UniquePropertyIds = [PropertyId(KnowledgeGraphVocabulary.ExternalId)],
                ExpectedElementVersion = relation.ExpectedElementVersion,
            };
        }).ToArray();

        return new GraphImportRequest
        {
            RequestId = batch.RequestId,
            Vertices = vertices,
            Edges = edges,
        };
    }

    private static List<GraphPropertyDto> CreateCommonProperties(
        string externalId,
        string kind,
        KnowledgeProvenance provenance,
        double? confidence,
        KnowledgeValidTime? validTime)
    {
        var properties = new List<GraphPropertyDto>();
        AddString(properties, KnowledgeGraphVocabulary.ExternalId, externalId);
        AddString(properties, KnowledgeGraphVocabulary.Kind, kind);
        AddString(properties, KnowledgeGraphVocabulary.ProjectionVersion, ProjectionVersion);
        AddString(properties, KnowledgeGraphVocabulary.Producer, provenance.Producer);
        AddString(properties, KnowledgeGraphVocabulary.ProducerRevision, provenance.Revision);
        AddString(properties, KnowledgeGraphVocabulary.RunId, provenance.RunId);
        AddDateTime(properties, KnowledgeGraphVocabulary.ObservedAt, provenance.ObservedAtUtc);
        AddContent(properties, KnowledgeGraphVocabulary.ProvenanceSourcePrefix, provenance.Source);
        if (confidence is { } confidenceValue)
            AddFloat64(properties, KnowledgeGraphVocabulary.Confidence, confidenceValue);
        if (validTime?.ValidFromUtc is { } validFrom)
            AddDateTime(properties, KnowledgeGraphVocabulary.ValidFrom, validFrom);
        if (validTime?.ValidToUtc is { } validTo)
            AddDateTime(properties, KnowledgeGraphVocabulary.ValidTo, validTo);
        return properties;
    }

    private static void AddClaim(
        ICollection<GraphPropertyDto> properties,
        KnowledgeClaimValue? claim)
    {
        if (claim is null)
            return;
        AddString(properties, KnowledgeGraphVocabulary.ClaimSubjectId, claim.SubjectId);
        AddString(properties, KnowledgeGraphVocabulary.ClaimPredicate, claim.Predicate);
        AddString(properties, KnowledgeGraphVocabulary.ClaimObjectId, claim.ObjectId);
        AddString(properties, KnowledgeGraphVocabulary.ClaimLiteral, claim.LiteralValue);
    }

    private static void AddContent(
        ICollection<GraphPropertyDto> properties,
        string prefix,
        KnowledgeContentReference? content)
    {
        if (content is null)
            return;
        AddString(properties, prefix + "store", content.StoreKind.ToString());
        AddString(properties, prefix + "container", content.Container);
        AddString(properties, prefix + "id", content.Id);
        AddString(properties, prefix + "version", content.Version);
        AddString(properties, prefix + "chunk_id", content.ChunkId);
        AddString(properties, prefix + "content_hash", content.ContentHash);
    }

    private static void AddVector(
        ICollection<GraphPropertyDto> properties,
        KnowledgeVectorReference? vector)
    {
        if (vector is null)
            return;
        AddString(properties, KnowledgeGraphVocabulary.VectorIndex, vector.Index);
        AddString(properties, KnowledgeGraphVocabulary.VectorId, vector.Id);
        AddString(properties, KnowledgeGraphVocabulary.VectorProfileId, vector.ProfileId);
    }

    private static void AddCommunity(
        ICollection<GraphPropertyDto> properties,
        KnowledgeCommunityReference? community)
    {
        if (community is null)
            return;
        AddString(properties, KnowledgeGraphVocabulary.CommunityResultVersion, community.ResultVersion);
        AddString(properties, KnowledgeGraphVocabulary.CommunityAlgorithm, community.Algorithm);
        if (community.SourceSequence is { } sourceSequence)
            AddInt64(properties, KnowledgeGraphVocabulary.CommunitySourceSequence, sourceSequence);
    }

    private static void AddString(
        ICollection<GraphPropertyDto> properties,
        string name,
        string? value)
    {
        if (value is null)
            return;
        properties.Add(new GraphPropertyDto
        {
            PropertyId = PropertyId(name),
            Value = new GraphValueDto { Kind = GraphPropertyKind.String, String = value },
        });
    }

    private static void AddInt64(
        ICollection<GraphPropertyDto> properties,
        string name,
        long value)
        => properties.Add(new GraphPropertyDto
        {
            PropertyId = PropertyId(name),
            Value = new GraphValueDto { Kind = GraphPropertyKind.Int64, Int64 = value },
        });

    private static void AddFloat64(
        ICollection<GraphPropertyDto> properties,
        string name,
        double value)
        => properties.Add(new GraphPropertyDto
        {
            PropertyId = PropertyId(name),
            Value = new GraphValueDto { Kind = GraphPropertyKind.Float64, Float64 = value },
        });

    private static void AddDateTime(
        ICollection<GraphPropertyDto> properties,
        string name,
        DateTimeOffset value)
        => properties.Add(new GraphPropertyDto
        {
            PropertyId = PropertyId(name),
            Value = new GraphValueDto { Kind = GraphPropertyKind.DateTime, DateTime = value },
        });

    private static IReadOnlyList<GraphPropertyDto> SortProperties(List<GraphPropertyDto> properties)
        => properties.OrderBy(static property => property.PropertyId).ToArray();

    private static int PropertyId(string name)
        => SndbGraphImporter.GetStablePropertyId(name);

    private static string GetNodeLabel(KnowledgeGraphNodeKind kind) => kind switch
    {
        KnowledgeGraphNodeKind.Entity => "__kg_entity",
        KnowledgeGraphNodeKind.Alias => "__kg_alias",
        KnowledgeGraphNodeKind.Claim => "__kg_claim",
        KnowledgeGraphNodeKind.Source => "__kg_source",
        KnowledgeGraphNodeKind.Chunk => "__kg_chunk",
        KnowledgeGraphNodeKind.Community => "__kg_community",
        KnowledgeGraphNodeKind.Summary => "__kg_summary",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string GetRelationLabel(KnowledgeGraphRelationKind kind) => kind switch
    {
        KnowledgeGraphRelationKind.Asserts => "__kg_asserts",
        KnowledgeGraphRelationKind.SupportedBy => "__kg_supported_by",
        KnowledgeGraphRelationKind.Contradicts => "__kg_contradicts",
        KnowledgeGraphRelationKind.AliasOf => "__kg_alias_of",
        KnowledgeGraphRelationKind.ChunkOf => "__kg_chunk_of",
        KnowledgeGraphRelationKind.MemberOf => "__kg_member_of",
        KnowledgeGraphRelationKind.SummarizedBy => "__kg_summarized_by",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static void EnsureNoStableIdCollision(
        IDictionary<long, string> ids,
        long stableId,
        string externalId,
        string kind)
    {
        if (ids.TryGetValue(stableId, out string? existing)
            && !string.Equals(existing, externalId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Knowledge graph {kind} ID '{externalId}' 与 '{existing}' 映射到相同 Graph ID；批次未写入。");
        }
        ids[stableId] = externalId;
    }
}

internal static class KnowledgeGraphVocabulary
{
    internal const string ExternalId = "__kg_external_id";
    internal const string Kind = "__kg_kind";
    internal const string ProjectionVersion = "__kg_projection_version";
    internal const string Name = "__kg_name";
    internal const string Confidence = "__kg_confidence";
    internal const string ValidFrom = "__kg_valid_from";
    internal const string ValidTo = "__kg_valid_to";
    internal const string Producer = "__kg_producer";
    internal const string ProducerRevision = "__kg_producer_revision";
    internal const string RunId = "__kg_run_id";
    internal const string ObservedAt = "__kg_observed_at";
    internal const string ContentPrefix = "__kg_content_";
    internal const string ProvenanceSourcePrefix = "__kg_provenance_source_";
    internal const string ClaimSubjectId = "__kg_claim_subject_id";
    internal const string ClaimPredicate = "__kg_claim_predicate";
    internal const string ClaimObjectId = "__kg_claim_object_id";
    internal const string ClaimLiteral = "__kg_claim_literal";
    internal const string VectorIndex = "__kg_vector_index";
    internal const string VectorId = "__kg_vector_id";
    internal const string VectorProfileId = "__kg_vector_profile_id";
    internal const string CommunityResultVersion = "__kg_community_result_version";
    internal const string CommunityAlgorithm = "__kg_community_algorithm";
    internal const string CommunitySourceSequence = "__kg_community_source_sequence";
}
