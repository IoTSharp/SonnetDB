namespace SonnetDB.KnowledgeGraphs;

/// <summary>M40 #365 知识图谱/GraphRAG 上层合同校验器。</summary>
public static class KnowledgeGraphValidator
{
    /// <summary>单个原子批次允许的最大节点与关系总数。</summary>
    public const int MaxElementsPerBatch = 256;

    /// <summary>外部 ID、名称和版本字段的最大字符数。</summary>
    public const int MaxIdentifierLength = 512;

    /// <summary>声明标量值的最大字符数；正文必须使用内容引用。</summary>
    public const int MaxLiteralLength = 4 * 1024;

    /// <summary>校验知识图谱批次，不执行写入。</summary>
    /// <param name="batch">待校验批次。</param>
    /// <returns>结构化校验结果。</returns>
    public static KnowledgeGraphValidationResult Validate(KnowledgeGraphBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var failures = new List<KnowledgeGraphValidationFailure>();
        if (batch.SchemaVersion != 1)
            Add(failures, "schemaVersion", "version", "当前仅支持 knowledge graph schemaVersion 1。");
        if (batch.RequestId == Guid.Empty)
            Add(failures, "requestId", "required", "requestId 不能为空。");
        if (batch.Nodes is null)
            Add(failures, "nodes", "required", "nodes 不能为 null；没有节点时使用空数组。");
        if (batch.Relations is null)
            Add(failures, "relations", "required", "relations 不能为 null；没有关系时使用空数组。");
        if (batch.Nodes is null || batch.Relations is null)
            return new KnowledgeGraphValidationResult(false, failures.AsReadOnly());

        int elementCount = batch.Nodes.Count + batch.Relations.Count;
        if (elementCount == 0)
            Add(failures, "nodes", "minimum", "知识图谱批次至少需要一个节点或关系。");
        if (elementCount > MaxElementsPerBatch)
        {
            Add(
                failures,
                "nodes",
                "maximum",
                $"节点与关系总数不能超过 {MaxElementsPerBatch}。");
        }

        var nodesById = new Dictionary<string, KnowledgeGraphNode>(StringComparer.Ordinal);
        for (var index = 0; index < batch.Nodes.Count; index++)
        {
            KnowledgeGraphNode? node = batch.Nodes[index];
            string path = $"nodes[{index}]";
            if (node is null)
            {
                Add(failures, path, "required", "节点不能为 null。");
                continue;
            }

            ValidateNode(node, path, failures);
            if (ValidateIdentifier(node.Id, path + ".id", failures)
                && !nodesById.TryAdd(node.Id, node))
            {
                Add(failures, path + ".id", "unique", "同一批次中的节点 ID 不能重复。");
            }
        }

        for (var index = 0; index < batch.Nodes.Count; index++)
        {
            KnowledgeGraphNode? node = batch.Nodes[index];
            if (node?.Claim is not null)
            {
                ValidateClaimEndpointKinds(
                    node.Claim,
                    $"nodes[{index}].claim",
                    nodesById,
                    failures);
            }
        }

        var relationIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < batch.Relations.Count; index++)
        {
            KnowledgeGraphRelation? relation = batch.Relations[index];
            string path = $"relations[{index}]";
            if (relation is null)
            {
                Add(failures, path, "required", "关系不能为 null。");
                continue;
            }

            ValidateRelation(relation, path, nodesById, failures);
            if (ValidateIdentifier(relation.Id, path + ".id", failures)
                && !relationIds.Add(relation.Id))
            {
                Add(failures, path + ".id", "unique", "同一批次中的关系 ID 不能重复。");
            }
        }

        return failures.Count == 0
            ? KnowledgeGraphValidationResult.Valid
            : new KnowledgeGraphValidationResult(false, failures.AsReadOnly());
    }

    /// <summary>校验批次，并在失败时抛出包含稳定字段路径的异常。</summary>
    /// <param name="batch">待校验批次。</param>
    public static void ValidateOrThrow(KnowledgeGraphBatch batch)
    {
        KnowledgeGraphValidationResult result = Validate(batch);
        if (result.IsValid)
            return;

        throw new ArgumentException(
            "知识图谱合同校验失败："
            + string.Join("; ", result.Failures.Select(static failure =>
                $"[{failure.Path}] {failure.Rule}: {failure.Message}")),
            nameof(batch));
    }

    private static void ValidateNode(
        KnowledgeGraphNode node,
        string path,
        ICollection<KnowledgeGraphValidationFailure> failures)
    {
        ValidateIdentifier(node.Id, path + ".id", failures);
        if (!Enum.IsDefined(node.Kind))
            Add(failures, path + ".kind", "enum", "节点类别非法。");
        if (node.ExpectedElementVersion < 0)
            Add(failures, path + ".expectedElementVersion", "minimum", "预期版本不能为负数。");
        ValidateProvenance(node.Provenance, path + ".provenance", failures);
        ValidateConfidence(node.Confidence, path + ".confidence", failures);
        ValidateValidTime(node.ValidTime, path + ".validTime", failures);
        ValidateVector(node.Vector, path + ".vector", failures);

        switch (node.Kind)
        {
            case KnowledgeGraphNodeKind.Entity:
            case KnowledgeGraphNodeKind.Alias:
                ValidateRequiredText(node.Name, path + ".name", failures);
                RejectUnexpected(node.Claim, path + ".claim", failures);
                RejectUnexpected(node.Content, path + ".content", failures);
                RejectUnexpected(node.Community, path + ".community", failures);
                if (node.Kind == KnowledgeGraphNodeKind.Alias)
                {
                    RequireConfidence(node.Confidence, path + ".confidence", failures);
                    RequireValidTime(node.ValidTime, path + ".validTime", failures);
                }
                break;
            case KnowledgeGraphNodeKind.Claim:
                if (node.Claim is null)
                    Add(failures, path + ".claim", "required", "Claim 节点必须提供结构化声明。");
                else
                    ValidateClaim(node.Claim, path + ".claim", failures);
                RejectUnexpected(node.Content, path + ".content", failures);
                RejectUnexpected(node.Community, path + ".community", failures);
                RequireConfidence(node.Confidence, path + ".confidence", failures);
                RequireValidTime(node.ValidTime, path + ".validTime", failures);
                break;
            case KnowledgeGraphNodeKind.Source:
                ValidateContent(
                    node.Content,
                    path + ".content",
                    requireChunk: false,
                    allowChunk: false,
                    failures);
                RejectUnexpected(node.Claim, path + ".claim", failures);
                RejectUnexpected(node.Community, path + ".community", failures);
                break;
            case KnowledgeGraphNodeKind.Chunk:
                ValidateContent(
                    node.Content,
                    path + ".content",
                    requireChunk: true,
                    allowChunk: true,
                    failures);
                RejectUnexpected(node.Claim, path + ".claim", failures);
                RejectUnexpected(node.Community, path + ".community", failures);
                break;
            case KnowledgeGraphNodeKind.Community:
                ValidateCommunity(node.Community, path + ".community", failures);
                RejectUnexpected(node.Claim, path + ".claim", failures);
                RejectUnexpected(node.Content, path + ".content", failures);
                break;
            case KnowledgeGraphNodeKind.Summary:
                ValidateContent(
                    node.Content,
                    path + ".content",
                    requireChunk: false,
                    allowChunk: false,
                    failures);
                ValidateCommunity(node.Community, path + ".community", failures);
                RejectUnexpected(node.Claim, path + ".claim", failures);
                break;
        }
    }

    private static void ValidateRelation(
        KnowledgeGraphRelation relation,
        string path,
        IReadOnlyDictionary<string, KnowledgeGraphNode> nodesById,
        ICollection<KnowledgeGraphValidationFailure> failures)
    {
        ValidateIdentifier(relation.Id, path + ".id", failures);
        bool sourceValid = ValidateIdentifier(relation.SourceId, path + ".sourceId", failures);
        bool targetValid = ValidateIdentifier(relation.TargetId, path + ".targetId", failures);
        if (!Enum.IsDefined(relation.Kind))
            Add(failures, path + ".kind", "enum", "关系类别非法。");
        if (relation.ExpectedElementVersion < 0)
            Add(failures, path + ".expectedElementVersion", "minimum", "预期版本不能为负数。");
        ValidateProvenance(relation.Provenance, path + ".provenance", failures);
        ValidateConfidence(relation.Confidence, path + ".confidence", failures);
        ValidateValidTime(relation.ValidTime, path + ".validTime", failures);

        if (relation.Kind is KnowledgeGraphRelationKind.Asserts
            or KnowledgeGraphRelationKind.SupportedBy
            or KnowledgeGraphRelationKind.Contradicts
            or KnowledgeGraphRelationKind.AliasOf)
        {
            RequireConfidence(relation.Confidence, path + ".confidence", failures);
            RequireValidTime(relation.ValidTime, path + ".validTime", failures);
        }

        if (!sourceValid || !targetValid
            || !nodesById.TryGetValue(relation.SourceId, out KnowledgeGraphNode? source)
            || !nodesById.TryGetValue(relation.TargetId, out KnowledgeGraphNode? target))
        {
            return;
        }

        (KnowledgeGraphNodeKind Source, KnowledgeGraphNodeKind Target) expected = relation.Kind switch
        {
            KnowledgeGraphRelationKind.Asserts =>
                (KnowledgeGraphNodeKind.Entity, KnowledgeGraphNodeKind.Claim),
            KnowledgeGraphRelationKind.SupportedBy or KnowledgeGraphRelationKind.Contradicts =>
                (KnowledgeGraphNodeKind.Claim, target.Kind),
            KnowledgeGraphRelationKind.AliasOf =>
                (KnowledgeGraphNodeKind.Alias, KnowledgeGraphNodeKind.Entity),
            KnowledgeGraphRelationKind.ChunkOf =>
                (KnowledgeGraphNodeKind.Chunk, KnowledgeGraphNodeKind.Source),
            KnowledgeGraphRelationKind.MemberOf =>
                (source.Kind, KnowledgeGraphNodeKind.Community),
            KnowledgeGraphRelationKind.SummarizedBy =>
                (KnowledgeGraphNodeKind.Community, KnowledgeGraphNodeKind.Summary),
            _ => (source.Kind, target.Kind),
        };

        bool shapeValid = relation.Kind switch
        {
            KnowledgeGraphRelationKind.SupportedBy or KnowledgeGraphRelationKind.Contradicts =>
                source.Kind == KnowledgeGraphNodeKind.Claim
                && target.Kind is KnowledgeGraphNodeKind.Source or KnowledgeGraphNodeKind.Chunk,
            KnowledgeGraphRelationKind.MemberOf =>
                source.Kind is KnowledgeGraphNodeKind.Entity or KnowledgeGraphNodeKind.Claim
                && target.Kind == KnowledgeGraphNodeKind.Community,
            _ => source.Kind == expected.Source && target.Kind == expected.Target,
        };
        if (!shapeValid)
        {
            Add(
                failures,
                path,
                "relation_shape",
                $"关系 {relation.Kind} 不允许 {source.Kind} -> {target.Kind}。批次外端点由 Graph transaction 校验。");
        }
    }

    private static void ValidateClaim(
        KnowledgeClaimValue claim,
        string path,
        ICollection<KnowledgeGraphValidationFailure> failures)
    {
        ValidateIdentifier(claim.SubjectId, path + ".subjectId", failures);
        ValidateRequiredText(claim.Predicate, path + ".predicate", failures);
        bool hasObject = !string.IsNullOrWhiteSpace(claim.ObjectId);
        bool hasLiteral = !string.IsNullOrWhiteSpace(claim.LiteralValue);
        if (hasObject == hasLiteral)
        {
            Add(
                failures,
                path,
                "choice",
                "Claim 必须且只能提供 objectId 或 literalValue 之一。");
        }
        if (hasObject)
            ValidateIdentifier(claim.ObjectId, path + ".objectId", failures);
        if (hasLiteral && claim.LiteralValue!.Length > MaxLiteralLength)
        {
            Add(
                failures,
                path + ".literalValue",
                "maximum",
                $"Claim literalValue 不能超过 {MaxLiteralLength} 个字符；正文必须使用 content 引用。");
        }
    }

    private static void ValidateClaimEndpointKinds(
        KnowledgeClaimValue claim,
        string path,
        IReadOnlyDictionary<string, KnowledgeGraphNode> nodesById,
        ICollection<KnowledgeGraphValidationFailure> failures)
    {
        if (nodesById.TryGetValue(claim.SubjectId, out KnowledgeGraphNode? subject)
            && subject.Kind != KnowledgeGraphNodeKind.Entity)
        {
            Add(failures, path + ".subjectId", "reference_kind", "Claim subject 必须引用 Entity 节点。");
        }
        if (claim.ObjectId is { } objectId
            && nodesById.TryGetValue(objectId, out KnowledgeGraphNode? target)
            && target.Kind != KnowledgeGraphNodeKind.Entity)
        {
            Add(failures, path + ".objectId", "reference_kind", "Claim objectId 必须引用 Entity 节点。");
        }
    }

    private static void ValidateProvenance(
        KnowledgeProvenance? provenance,
        string path,
        ICollection<KnowledgeGraphValidationFailure> failures)
    {
        if (provenance is null)
        {
            Add(failures, path, "required", "provenance 不能为空。");
            return;
        }

        ValidateRequiredText(provenance.Producer, path + ".producer", failures);
        ValidateRequiredText(provenance.Revision, path + ".revision", failures);
        ValidateRequiredText(provenance.RunId, path + ".runId", failures);
        if (provenance.ObservedAtUtc == default)
            Add(failures, path + ".observedAtUtc", "required", "observedAtUtc 不能为空。");
        else if (provenance.ObservedAtUtc.Offset != TimeSpan.Zero)
            Add(failures, path + ".observedAtUtc", "utc", "observedAtUtc 必须使用 UTC offset。");
        if (provenance.Source is not null)
        {
            ValidateContent(
                provenance.Source,
                path + ".source",
                requireChunk: false,
                allowChunk: true,
                failures);
        }
    }

    private static void ValidateContent(
        KnowledgeContentReference? content,
        string path,
        bool requireChunk,
        bool allowChunk,
        ICollection<KnowledgeGraphValidationFailure> failures)
    {
        if (content is null)
        {
            Add(failures, path, "required", "必须提供 Document/Object 权威内容引用。");
            return;
        }

        if (!Enum.IsDefined(content.StoreKind))
            Add(failures, path + ".storeKind", "enum", "内容存储模型非法。");
        ValidateIdentifier(content.Container, path + ".container", failures);
        ValidateIdentifier(content.Id, path + ".id", failures);
        ValidateRequiredText(content.Version, path + ".version", failures);
        if (requireChunk && string.IsNullOrWhiteSpace(content.ChunkId))
            Add(failures, path + ".chunkId", "required", "Chunk 节点必须引用稳定 chunkId。");
        if (!allowChunk && content.ChunkId is not null)
            Add(failures, path + ".chunkId", "forbidden", "Source/Summary 内容引用不能携带 chunkId。");
        if (!requireChunk && content.ChunkId is not null && string.IsNullOrWhiteSpace(content.ChunkId))
            Add(failures, path + ".chunkId", "format", "chunkId 不能是空白文本。");
        if (content.ChunkId is { } chunkId && chunkId.Length > MaxIdentifierLength)
            Add(failures, path + ".chunkId", "maximum", $"chunkId 不能超过 {MaxIdentifierLength} 个字符。");
        if (content.ContentHash is { } hash && string.IsNullOrWhiteSpace(hash))
            Add(failures, path + ".contentHash", "format", "contentHash 不能是空白文本。");
        else if (content.ContentHash is { Length: > MaxIdentifierLength })
            Add(failures, path + ".contentHash", "maximum", $"contentHash 不能超过 {MaxIdentifierLength} 个字符。");
    }

    private static void ValidateVector(
        KnowledgeVectorReference? vector,
        string path,
        ICollection<KnowledgeGraphValidationFailure> failures)
    {
        if (vector is null)
            return;
        ValidateIdentifier(vector.Index, path + ".index", failures);
        ValidateIdentifier(vector.Id, path + ".id", failures);
        ValidateRequiredText(vector.ProfileId, path + ".profileId", failures);
    }

    private static void ValidateCommunity(
        KnowledgeCommunityReference? community,
        string path,
        ICollection<KnowledgeGraphValidationFailure> failures)
    {
        if (community is null)
        {
            Add(failures, path, "required", "必须提供 community 结果引用。");
            return;
        }
        ValidateRequiredText(community.ResultVersion, path + ".resultVersion", failures);
        ValidateRequiredText(community.Algorithm, path + ".algorithm", failures);
        if (community.SourceSequence is < 0)
            Add(failures, path + ".sourceSequence", "minimum", "sourceSequence 不能为负数。");
    }

    private static void ValidateConfidence(
        double? confidence,
        string path,
        ICollection<KnowledgeGraphValidationFailure> failures)
    {
        if (confidence is { } value
            && (!double.IsFinite(value) || value < 0 || value > 1))
        {
            Add(failures, path, "range", "confidence 必须是 0 到 1 的有限数值。");
        }
    }

    private static void RequireConfidence(
        double? confidence,
        string path,
        ICollection<KnowledgeGraphValidationFailure> failures)
    {
        if (confidence is null)
            Add(failures, path, "required", "该节点或关系必须显式提供 confidence。");
    }

    private static void ValidateValidTime(
        KnowledgeValidTime? validTime,
        string path,
        ICollection<KnowledgeGraphValidationFailure> failures)
    {
        if (validTime?.ValidFromUtc is { } from
            && validTime.ValidToUtc is { } to
            && from >= to)
        {
            Add(failures, path, "range", "valid time 必须满足 validFromUtc < validToUtc。");
        }
        if (validTime?.ValidFromUtc is { Offset: var fromOffset } && fromOffset != TimeSpan.Zero)
            Add(failures, path + ".validFromUtc", "utc", "validFromUtc 必须使用 UTC offset。");
        if (validTime?.ValidToUtc is { Offset: var toOffset } && toOffset != TimeSpan.Zero)
            Add(failures, path + ".validToUtc", "utc", "validToUtc 必须使用 UTC offset。");
    }

    private static void RequireValidTime(
        KnowledgeValidTime? validTime,
        string path,
        ICollection<KnowledgeGraphValidationFailure> failures)
    {
        if (validTime is null)
            Add(failures, path, "required", "该节点或关系必须显式提供 validTime；无界时使用 Unbounded。");
    }

    private static bool ValidateIdentifier(
        string? value,
        string path,
        ICollection<KnowledgeGraphValidationFailure> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(failures, path, "required", "标识符不能为空。");
            return false;
        }
        if (value.Length > MaxIdentifierLength)
        {
            Add(failures, path, "maximum", $"标识符不能超过 {MaxIdentifierLength} 个字符。");
            return false;
        }
        return true;
    }

    private static void ValidateRequiredText(
        string? value,
        string path,
        ICollection<KnowledgeGraphValidationFailure> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
            Add(failures, path, "required", "文本不能为空。");
        else if (value.Length > MaxIdentifierLength)
            Add(failures, path, "maximum", $"文本不能超过 {MaxIdentifierLength} 个字符。");
    }

    private static void RejectUnexpected(
        object? value,
        string path,
        ICollection<KnowledgeGraphValidationFailure> failures)
    {
        if (value is not null)
            Add(failures, path, "forbidden", "该节点类别不允许此字段。");
    }

    private static void Add(
        ICollection<KnowledgeGraphValidationFailure> failures,
        string path,
        string rule,
        string message)
        => failures.Add(new KnowledgeGraphValidationFailure(path, rule, message));
}
