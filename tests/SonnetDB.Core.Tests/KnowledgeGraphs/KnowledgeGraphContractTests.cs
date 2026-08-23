using System.Text.Json;
using SonnetDB.Data.Graphs;
using SonnetDB.Data.KnowledgeGraphs;
using SonnetDB.Graphs;
using SonnetDB.KnowledgeGraphs;

namespace SonnetDB.Core.Tests.KnowledgeGraphs;

/// <summary>M40 #365 知识图谱上层合同、投影和嵌入式 SDK 回归。</summary>
public sealed class KnowledgeGraphContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-knowledge-graph-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Validate_WithEvidenceAliasCommunityAndSummary_Passes()
    {
        KnowledgeGraphValidationResult result = KnowledgeGraphValidator.Validate(CreateBatch());

        Assert.True(result.IsValid);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Validate_WithInvalidConfidenceTimeClaimChunkAndShape_ReportsStablePaths()
    {
        KnowledgeProvenance provenance = CreateProvenance();
        var batch = new KnowledgeGraphBatch(
            Guid.NewGuid(),
            [
                new KnowledgeGraphNode("claim:bad", KnowledgeGraphNodeKind.Claim, provenance)
                {
                    Claim = new KnowledgeClaimValue("entity:1", "status", "entity:2")
                    {
                        LiteralValue = "duplicate target",
                    },
                    Confidence = double.NaN,
                    ValidTime = new KnowledgeValidTime(
                        DateTimeOffset.Parse("2026-08-24T00:00:00Z"),
                        DateTimeOffset.Parse("2026-08-23T00:00:00Z")),
                },
                new KnowledgeGraphNode("chunk:bad", KnowledgeGraphNodeKind.Chunk, provenance)
                {
                    Content = new KnowledgeContentReference(
                        KnowledgeContentStoreKind.Document,
                        "knowledge",
                        "doc:1",
                        "v1"),
                },
                new KnowledgeGraphNode("alias:bad", KnowledgeGraphNodeKind.Alias, provenance)
                {
                    Name = "wrong endpoint",
                    Confidence = 0.8,
                    ValidTime = KnowledgeValidTime.Unbounded,
                },
            ],
            [
                new KnowledgeGraphRelation(
                    "relation:bad",
                    KnowledgeGraphRelationKind.AliasOf,
                    "claim:bad",
                    "chunk:bad",
                    provenance)
                {
                    Confidence = 2,
                    ValidTime = KnowledgeValidTime.Unbounded,
                },
            ]);

        KnowledgeGraphValidationResult result = KnowledgeGraphValidator.Validate(batch);

        Assert.False(result.IsValid);
        Assert.Contains(result.Failures, failure =>
            failure.Path == "nodes[0].claim" && failure.Rule == "choice");
        Assert.Contains(result.Failures, failure =>
            failure.Path == "nodes[0].confidence" && failure.Rule == "range");
        Assert.Contains(result.Failures, failure =>
            failure.Path == "nodes[0].validTime" && failure.Rule == "range");
        Assert.Contains(result.Failures, failure =>
            failure.Path == "nodes[1].content.chunkId" && failure.Rule == "required");
        Assert.Contains(result.Failures, failure =>
            failure.Path == "relations[0]" && failure.Rule == "relation_shape");
    }

    [Fact]
    public void JsonRoundTrip_UsesVersionedReferencesAndDoesNotEmbedContentOrVectors()
    {
        KnowledgeGraphBatch batch = CreateBatch();

        string json = JsonSerializer.Serialize(
            batch,
            KnowledgeGraphJsonContext.Default.KnowledgeGraphBatch);
        KnowledgeGraphBatch? roundTripped = JsonSerializer.Deserialize(
            json,
            KnowledgeGraphJsonContext.Default.KnowledgeGraphBatch);

        Assert.NotNull(roundTripped);
        Assert.Equal(1, roundTripped!.SchemaVersion);
        Assert.Equal(batch.Nodes.Count, roundTripped.Nodes.Count);
        Assert.Contains("\"storeKind\":\"Document\"", json, StringComparison.Ordinal);
        Assert.Contains("\"profileId\":\"text-v1\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("embedding\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objectBytes", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(KnowledgeGraphValidator.Validate(roundTripped).IsValid);
    }

    [Fact]
    public void ToGraphImportRequest_WithValidBatch_UsesStableTypedProjection()
    {
        KnowledgeGraphBatch batch = CreateBatch();

        GraphImportRequest request = KnowledgeGraphMapper.ToGraphImportRequest(batch);

        Assert.Equal(batch.RequestId, request.RequestId);
        Assert.Equal(batch.Nodes.Count, request.Vertices.Count);
        Assert.Equal(batch.Relations.Count, request.Edges.Count);
        GraphImportVertexDto claim = request.Vertices.Single(vertex =>
            vertex.Id == KnowledgeGraphMapper.GetNodeId("claim:temperature-high").Value);
        Assert.Equal(
            KnowledgeGraphMapper.GetNodeLabelId(KnowledgeGraphNodeKind.Claim).Value,
            Assert.Single(claim.Labels));
        Assert.Equal(
            "temperature_status",
            GetProperty(claim.Properties, "__kg_claim_predicate").Value.String);
        Assert.Equal(
            0.94,
            GetProperty(claim.Properties, "__kg_confidence").Value.Float64);
        Assert.Equal(
            SndbGraphImporter.GetStablePropertyId("__kg_external_id"),
            Assert.Single(claim.UniquePropertyIds));
        Assert.Equal(
            claim.Properties.OrderBy(static property => property.PropertyId).Select(static property => property.PropertyId),
            claim.Properties.Select(static property => property.PropertyId));

        GraphImportEdgeDto evidence = request.Edges.Single(edge =>
            edge.Id == KnowledgeGraphMapper.GetRelationId("evidence:temperature-high").Value);
        Assert.Equal(
            KnowledgeGraphMapper.GetRelationLabelId(KnowledgeGraphRelationKind.SupportedBy).Value,
            evidence.LabelId);
        Assert.Equal(KnowledgeGraphMapper.GetNodeId("claim:temperature-high").Value, evidence.SourceId);
        Assert.Equal(KnowledgeGraphMapper.GetNodeId("chunk:manual:alarm").Value, evidence.TargetId);
    }

    [Fact]
    public async Task ImportKnowledgeGraphAsync_EmbeddedAndReplay_PersistsGenericGraphOnly()
    {
        Directory.CreateDirectory(_root);
        using var client = new SndbGraphClient($"Data Source={_root};Mode=Embedded");
        await client.CreateGraphAsync("knowledge");
        KnowledgeGraphBatch batch = CreateBatch();

        GraphImportResponse first = await client.ImportKnowledgeGraphAsync("knowledge", batch);
        GraphImportResponse replay = await client.ImportKnowledgeGraphAsync("knowledge", batch);

        Assert.False(first.IsDuplicate);
        Assert.True(replay.IsDuplicate);
        GraphVertex claim = Assert.IsType<GraphVertex>(await client.GetVertexAsync(
            "knowledge",
            KnowledgeGraphMapper.GetNodeId("claim:temperature-high")));
        Assert.Contains(KnowledgeGraphMapper.GetNodeLabelId(KnowledgeGraphNodeKind.Claim), claim.Labels);
        Assert.Equal(
            "docs-v7",
            claim.Properties.Single(property =>
                property.PropertyId == SndbGraphImporter.GetStablePropertyId("__kg_provenance_source_version"))
                .Value.AsString());

        var evidence = new List<GraphExpansion>();
        await foreach (GraphExpansion expansion in client.ExpandAsync(
            "knowledge",
            new GraphExpandRequest
            {
                VertexId = claim.Id.Value,
                EdgeLabelId = KnowledgeGraphMapper.GetRelationLabelId(
                    KnowledgeGraphRelationKind.SupportedBy).Value,
            }))
        {
            evidence.Add(expansion);
        }
        Assert.Equal(
            KnowledgeGraphMapper.GetNodeId("chunk:manual:alarm"),
            Assert.Single(evidence).NeighborId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static KnowledgeGraphBatch CreateBatch()
    {
        KnowledgeProvenance provenance = CreateProvenance();
        var content = new KnowledgeContentReference(
            KnowledgeContentStoreKind.Document,
            "manuals",
            "manual:temperature",
            "docs-v7",
            contentHash: "sha256:manual");
        var chunk = content with { ChunkId = "alarm", ContentHash = "sha256:alarm" };
        var community = new KnowledgeCommunityReference(
            "community-run@42",
            "label-propagation-v1",
            sourceSequence: 42);
        var validity = new KnowledgeValidTime(
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            null);

        return new KnowledgeGraphBatch(
            Guid.Parse("36500000-0000-0000-0000-000000000001"),
            [
                new KnowledgeGraphNode("entity:sensor-a", KnowledgeGraphNodeKind.Entity, provenance)
                {
                    Name = "Sensor A",
                    Confidence = 1,
                    Vector = new KnowledgeVectorReference("knowledge-vectors", "entity:sensor-a", "text-v1"),
                },
                new KnowledgeGraphNode("alias:line-1-sensor", KnowledgeGraphNodeKind.Alias, provenance)
                {
                    Name = "Line 1 sensor",
                    Confidence = 0.91,
                    ValidTime = validity,
                },
                new KnowledgeGraphNode("claim:temperature-high", KnowledgeGraphNodeKind.Claim, provenance)
                {
                    Claim = new KnowledgeClaimValue
                    {
                        SubjectId = "entity:sensor-a",
                        Predicate = "temperature_status",
                        LiteralValue = "high",
                    },
                    Confidence = 0.94,
                    ValidTime = validity,
                },
                new KnowledgeGraphNode("source:manual", KnowledgeGraphNodeKind.Source, provenance)
                {
                    Content = content,
                },
                new KnowledgeGraphNode("chunk:manual:alarm", KnowledgeGraphNodeKind.Chunk, provenance)
                {
                    Content = chunk,
                    Vector = new KnowledgeVectorReference("knowledge-vectors", "chunk:manual:alarm", "text-v1"),
                },
                new KnowledgeGraphNode("community:42:7", KnowledgeGraphNodeKind.Community, provenance)
                {
                    Community = community,
                },
                new KnowledgeGraphNode("summary:42:7", KnowledgeGraphNodeKind.Summary, provenance)
                {
                    Content = new KnowledgeContentReference(
                        KnowledgeContentStoreKind.Document,
                        "community-summaries",
                        "summary:42:7",
                        "summary-v1",
                        contentHash: "sha256:summary"),
                    Vector = new KnowledgeVectorReference("knowledge-vectors", "summary:42:7", "text-v1"),
                    Community = community,
                },
            ],
            [
                Relation("alias-of:sensor-a", KnowledgeGraphRelationKind.AliasOf, "alias:line-1-sensor", "entity:sensor-a", provenance, 0.91, validity),
                Relation("assert:temperature-high", KnowledgeGraphRelationKind.Asserts, "entity:sensor-a", "claim:temperature-high", provenance, 0.94, validity),
                Relation("evidence:temperature-high", KnowledgeGraphRelationKind.SupportedBy, "claim:temperature-high", "chunk:manual:alarm", provenance, 0.97, validity),
                Relation("chunk-of:manual:alarm", KnowledgeGraphRelationKind.ChunkOf, "chunk:manual:alarm", "source:manual", provenance),
                Relation("member-of:sensor-a", KnowledgeGraphRelationKind.MemberOf, "entity:sensor-a", "community:42:7", provenance),
                Relation("summary-of:42:7", KnowledgeGraphRelationKind.SummarizedBy, "community:42:7", "summary:42:7", provenance),
            ]);
    }

    private static KnowledgeGraphRelation Relation(
        string id,
        KnowledgeGraphRelationKind kind,
        string sourceId,
        string targetId,
        KnowledgeProvenance provenance,
        double? confidence = null,
        KnowledgeValidTime? validTime = null)
        => new(id, kind, sourceId, targetId, provenance)
        {
            Confidence = confidence,
            ValidTime = validTime,
        };

    private static KnowledgeProvenance CreateProvenance()
        => new(
            "industrial-extractor",
            "rules-v3",
            "run-20260823",
            DateTimeOffset.Parse("2026-08-23T10:00:00Z"),
            new KnowledgeContentReference(
                KnowledgeContentStoreKind.Document,
                "manuals",
                "manual:temperature",
                "docs-v7",
                chunkId: "alarm",
                contentHash: "sha256:alarm"));

    private static GraphPropertyDto GetProperty(
        IReadOnlyList<GraphPropertyDto> properties,
        string name)
        => properties.Single(property =>
            property.PropertyId == SndbGraphImporter.GetStablePropertyId(name));
}
