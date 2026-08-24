using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Data;
using SonnetDB.Data.Graphs;
using SonnetDB.Data.KnowledgeGraphs;
using SonnetDB.Graphs;
using SonnetDB.Json;
using SonnetDB.KnowledgeGraphs;
using Xunit;

namespace SonnetDB.Tests;

public sealed class GraphEndpointTests : IAsyncLifetime
{
    private const string AdminToken = "admin-graph-token";
    private const string ReadOnlyToken = "readonly-graph-token";
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _frameH2Url;
    private string? _dataRoot;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(
            Path.GetTempPath(),
            "sonnetdb-graph-endpoint-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        _app = BuildTestServer();
        await _app.StartAsync();
        await ResolveServerAddressesAsync();

        using HttpClient admin = CreateClient(AdminToken);
        HttpResponseMessage create = await admin.PostAsJsonAsync(
            "/v1/db",
            new CreateDatabaseRequest("graphapi"),
            ServerJsonContext.Default.CreateDatabaseRequest);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }

    private WebApplication BuildTestServer()
        => TestServerHost.Build(new ServerOptions
        {
            DataRoot = _dataRoot!,
            AutoLoadExistingDatabases = true,
            Tokens = new Dictionary<string, string>
            {
                [AdminToken] = ServerRoles.Admin,
                [ReadOnlyToken] = ServerRoles.ReadOnly,
            },
        }, extraArgs:
        [
            "--Kestrel:Endpoints:FrameH2:Url=http://127.0.0.1:0",
            "--Kestrel:Endpoints:FrameH2:Protocols=Http2",
        ]);

    private async Task ResolveServerAddressesAsync()
    {
        IServerAddressesFeature addresses = _app!.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        Assert.Equal(2, addresses.Addresses.Count);

        _baseUrl = null;
        _frameH2Url = null;
        foreach (string address in addresses.Addresses)
        {
            if (await ProbeIsHttp11Async(address))
                _baseUrl = address;
            else
                _frameH2Url = address;
        }

        Assert.NotNull(_baseUrl);
        Assert.NotNull(_frameH2Url);
    }

    private static async Task<bool> ProbeIsHttp11Async(string address)
    {
        using var client = new HttpClient();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address + "/healthz")
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            using var response = await client.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        if (_dataRoot is not null && Directory.Exists(_dataRoot))
        {
            try { Directory.Delete(_dataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GraphApi_RemoteCrudReplayAndNdjsonExpand_RoundTrips()
    {
        using HttpClient admin = CreateClient(AdminToken);
        HttpResponseMessage create = await admin.PostAsJsonAsync(
            "/v1/db/graphapi/graphs",
            new GraphCreateRequest { Name = "code" },
            ServerJsonContext.Default.GraphCreateRequest);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using SndbGraphClient client = CreateGraphClient(AdminToken);
        Guid vertexOneRequestId = Guid.NewGuid();
        GraphCommitResult first = await client.UpsertVertexAsync(
            "code",
            new GraphUpsertVertexRequest
            {
                Id = 1,
                RequestId = vertexOneRequestId,
                Labels = [1],
                Properties =
                [
                    new GraphPropertyDto
                    {
                        PropertyId = 7,
                        Value = new GraphValueDto
                        {
                            Kind = GraphPropertyKind.String,
                            String = "source",
                        },
                    },
                ],
                UniquePropertyIds = [7],
            });
        Assert.False(first.IsDuplicate);
        GraphCommitResult replay = await client.UpsertVertexAsync(
            "code",
            new GraphUpsertVertexRequest
            {
                Id = 1,
                RequestId = vertexOneRequestId,
                Labels = [1],
                Properties =
                [
                    new GraphPropertyDto
                    {
                        PropertyId = 7,
                        Value = new GraphValueDto
                        {
                            Kind = GraphPropertyKind.String,
                            String = "source",
                        },
                    },
                ],
                UniquePropertyIds = [7],
            });
        Assert.True(replay.IsDuplicate);

        await client.UpsertVertexAsync(
            "code",
            new GraphUpsertVertexRequest { Id = 2, RequestId = Guid.NewGuid(), Labels = [1] });
        await client.UpsertEdgeAsync(
            "code",
            new GraphUpsertEdgeRequest
            {
                Id = 10,
                RequestId = Guid.NewGuid(),
                SourceId = 1,
                TargetId = 2,
                LabelId = 2,
            });

        GraphVertex vertex = Assert.IsType<GraphVertex>(await client.GetVertexAsync("code", new GraphElementId(1)));
        Assert.Equal("source", Assert.Single(vertex.Properties).Value.AsString());

        var soughtVertices = new List<GraphVertex>();
        await foreach (GraphVertex item in client.SeekVerticesAsync(
            "code",
            new GraphSeekRequest
            {
                LabelId = 1,
                PropertyId = 7,
                Value = new GraphValueDto
                {
                    Kind = GraphPropertyKind.String,
                    String = "source",
                },
                PageSize = 1,
            }))
        {
            soughtVertices.Add(item);
        }
        Assert.Equal(1, Assert.Single(soughtVertices).Id.Value);

        var soughtEdges = new List<GraphEdge>();
        await foreach (GraphEdge item in client.SeekEdgesAsync(
            "code",
            new GraphSeekRequest { LabelId = 2, PageSize = 1 }))
        {
            soughtEdges.Add(item);
        }
        Assert.Equal(10, Assert.Single(soughtEdges).Id.Value);

        var paths = new List<GraphPath>();
        await foreach (GraphPath path in client.TraverseAsync(
            "code",
            new GraphTraversalRequest
            {
                StartId = 1,
                Kind = GraphTraversalKind.BreadthFirst,
                MaxDepth = 1,
                PageSize = 1,
            }))
        {
            paths.Add(path);
        }
        Assert.Equal([0, 1], paths.Select(static path => path.Depth).ToArray());
        GraphPath shortest = Assert.IsType<GraphPath>(await client.ShortestPathAsync(
            "code",
            new GraphShortestPathRequest { StartId = 1, TargetId = 2, MaxDepth = 1 }));
        Assert.Equal(1, shortest.Depth);

        using HttpClient readOnly = CreateClient(ReadOnlyToken);
        HttpResponseMessage stream = await readOnly.PostAsJsonAsync(
            "/v1/db/graphapi/graphs/code/expand/stream",
            new GraphExpandRequest
            {
                VertexId = 2,
                Direction = GraphDirection.Incoming,
                PageSize = 1,
            },
            ServerJsonContext.Default.GraphExpandRequest);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal("application/x-ndjson", stream.Content.Headers.ContentType?.MediaType);
        string[] lines = (await stream.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        GraphExpansionDto expansion = Assert.IsType<GraphExpansionDto>(
            JsonSerializer.Deserialize(lines.Single(), ServerJsonContext.Default.GraphExpansionDto));
        Assert.Equal(1, expansion.NeighborId);
        Assert.Equal(GraphDirection.Incoming, expansion.Direction);

        using SndbGraphClient frameClient = CreateGraphClient(
            AdminToken,
            SndbTransportProtocol.FrameHttp2);
        var framed = new List<GraphExpansion>();
        await foreach (GraphExpansion item in frameClient.ExpandAsync(
            "code",
            new GraphExpandRequest
            {
                VertexId = 1,
                Direction = GraphDirection.Outgoing,
                PageSize = 1,
            }))
        {
            framed.Add(item);
        }
        Assert.Equal(2, Assert.Single(framed).NeighborId.Value);

        await client.DeleteEdgeAsync(
            "code",
            new GraphElementId(10),
            new GraphDeleteRequest { ExpectedElementVersion = 1, RequestId = Guid.NewGuid() });
        await client.DeleteVertexAsync(
            "code",
            new GraphElementId(2),
            new GraphDeleteRequest { ExpectedElementVersion = 1, RequestId = Guid.NewGuid() });
        Assert.Null(await client.GetEdgeAsync("code", new GraphElementId(10)));
        Assert.Null(await client.GetVertexAsync("code", new GraphElementId(2)));
    }

    [Fact]
    public async Task GraphApi_PermissionsMissingGraphAndInvalidBudget_ReturnStableStatusCodes()
    {
        using HttpClient admin = CreateClient(AdminToken);
        HttpResponseMessage create = await admin.PostAsJsonAsync(
            "/v1/db/graphapi/graphs",
            new GraphCreateRequest { Name = "guarded" },
            ServerJsonContext.Default.GraphCreateRequest);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using HttpClient readOnly = CreateClient(ReadOnlyToken);
        HttpResponseMessage forbidden = await readOnly.PutAsJsonAsync(
            "/v1/db/graphapi/graphs/guarded/vertices/1",
            new GraphUpsertVertexRequest { Id = 1, RequestId = Guid.NewGuid(), Labels = [1] },
            ServerJsonContext.Default.GraphUpsertVertexRequest);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var anonymous = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/v1/db/graphapi/graphs")).StatusCode);

        HttpResponseMessage missing = await readOnly.PostAsJsonAsync(
            "/v1/db/graphapi/graphs/missing/expand/stream",
            new GraphExpandRequest { VertexId = 1 },
            ServerJsonContext.Default.GraphExpandRequest);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        HttpResponseMessage invalid = await readOnly.PostAsJsonAsync(
            "/v1/db/graphapi/graphs/guarded/expand",
            new GraphExpandRequest { VertexId = 1, PageSize = 0, MaxResults = 10_001 },
            ServerJsonContext.Default.GraphExpandRequest);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        HttpResponseMessage invalidSeek = await readOnly.PostAsJsonAsync(
            "/v1/db/graphapi/graphs/guarded/vertices/seek/stream",
            new GraphSeekRequest
            {
                LabelId = 1,
                PropertyId = 7,
                Value = new GraphValueDto { Kind = GraphPropertyKind.String },
            },
            ServerJsonContext.Default.GraphSeekRequest);
        Assert.Equal(HttpStatusCode.BadRequest, invalidSeek.StatusCode);

        using SndbGraphClient remote = CreateGraphClient(AdminToken);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (GraphExpansion _ in remote.ExpandAsync(
                "guarded",
                new GraphExpandRequest { VertexId = 1, PageSize = 0 }))
            {
            }
        });
    }

    [Fact]
    public async Task GraphApi_BothPaginationAndShortestPathBudget_PropagateThroughTypedSdk()
    {
        using SndbGraphClient client = CreateGraphClient(AdminToken);
        await client.CreateGraphAsync("pagination");
        for (long id = 1; id <= 6; id++)
        {
            await client.UpsertVertexAsync(
                "pagination",
                new GraphUpsertVertexRequest { Id = id, RequestId = Guid.NewGuid(), Labels = [1] });
        }

        await client.UpsertEdgeAsync("pagination", Edge(10, 1, 2));
        await client.UpsertEdgeAsync("pagination", Edge(11, 3, 1));
        await client.UpsertEdgeAsync("pagination", Edge(12, 4, 1));
        await client.UpsertEdgeAsync("pagination", Edge(13, 5, 1));
        await client.UpsertEdgeAsync("pagination", Edge(14, 6, 1));

        var expansions = new List<GraphExpansion>();
        await foreach (GraphExpansion expansion in client.ExpandAsync(
            "pagination",
            new GraphExpandRequest
            {
                VertexId = 1,
                Direction = GraphDirection.Both,
                PageSize = 3,
                MaxResults = 8,
            }))
        {
            expansions.Add(expansion);
        }
        Assert.Equal([10L, 11L, 12L, 13L, 14L], expansions.Select(static item => item.Edge.Id.Value).Order().ToArray());

        var paths = new List<GraphPath>();
        await foreach (GraphPath path in client.TraverseAsync(
            "pagination",
            new GraphTraversalRequest
            {
                StartId = 1,
                Kind = GraphTraversalKind.BreadthFirst,
                Direction = GraphDirection.Both,
                MaxDepth = 1,
                MaxFrontier = 8,
                MaxPaths = 8,
                PageSize = 3,
                PathUniqueness = GraphPathUniqueness.Edge,
            }))
        {
            paths.Add(path);
        }
        Assert.Equal(6, paths.Count);

        GraphPath withinBudget = Assert.IsType<GraphPath>(await client.ShortestPathAsync(
            "pagination",
            new GraphShortestPathRequest
            {
                StartId = 1,
                TargetId = 2,
                Direction = GraphDirection.Both,
                MaxDepth = 1,
                MaxFrontier = 8,
                MaxPaths = 2,
            }));
        Assert.Equal(1, withinBudget.Depth);

        SndbServerException error = await Assert.ThrowsAsync<SndbServerException>(() => client.ShortestPathAsync(
            "pagination",
            new GraphShortestPathRequest
            {
                StartId = 1,
                TargetId = 3,
                Direction = GraphDirection.Both,
                MaxDepth = 1,
                MaxFrontier = 8,
                MaxPaths = 2,
            }));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal("graph_budget_exceeded", error.Error);
    }

    [Fact]
    public async Task GraphApi_FilteredExpandAndImportBudgets_AreConsistentAcrossHttpAndTypedSdk()
    {
        using SndbGraphClient admin = CreateGraphClient(AdminToken);
        await admin.CreateGraphAsync("filtered");
        await admin.UpsertVertexAsync(
            "filtered",
            new GraphUpsertVertexRequest { Id = 1, RequestId = Guid.NewGuid(), Labels = [1] });
        await admin.UpsertVertexAsync("filtered", FilterVertex(2, 10, "match"));
        await admin.UpsertVertexAsync("filtered", FilterVertex(3, 20, "match"));
        await admin.UpsertVertexAsync("filtered", FilterVertex(4, 20, "skip"));
        await admin.UpsertEdgeAsync("filtered", Edge(20, 1, 2));
        await admin.UpsertEdgeAsync("filtered", Edge(21, 1, 3));
        await admin.UpsertEdgeAsync("filtered", Edge(22, 1, 4));

        using SndbGraphClient frameClient = CreateGraphClient(
            AdminToken,
            SndbTransportProtocol.FrameHttp2);
        var filtered = new List<GraphExpansion>();
        await foreach (GraphExpansion expansion in frameClient.ExpandAsync(
            "filtered",
            new GraphExpandRequest
            {
                VertexId = 1,
                Direction = GraphDirection.Outgoing,
                TargetLabelId = 20,
                TargetPropertyId = 7,
                TargetPropertyValue = new GraphValueDto
                {
                    Kind = GraphPropertyKind.String,
                    String = "match",
                },
                PageSize = 1,
            }))
        {
            filtered.Add(expansion);
        }
        Assert.Equal(3, Assert.Single(filtered).NeighborId.Value);

        using HttpClient http = CreateClient(AdminToken);
        HttpResponseMessage invalidPredicate = await http.PostAsJsonAsync(
            "/v1/db/graphapi/graphs/filtered/expand/stream",
            new GraphExpandRequest { VertexId = 1, TargetPropertyId = 7 },
            ServerJsonContext.Default.GraphExpandRequest);
        Assert.Equal(HttpStatusCode.BadRequest, invalidPredicate.StatusCode);

        using var oversizedContent = new UnknownLengthContent(GraphImportLimits.MaxBatchBytes + 1);
        oversizedContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        HttpResponseMessage oversizedResponse = await http.PostAsync(
            "/v1/db/graphapi/graphs/filtered/import",
            oversizedContent);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResponse.StatusCode);
        Assert.Contains(
            "graph_import_budget_exceeded",
            await oversizedResponse.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        string largeValue = new('x', 1024 * 1024);
        GraphImportVertexDto[] oversizedVertices = Enumerable.Range(100, 9)
            .Select(id => new GraphImportVertexDto
            {
                Id = id,
                Labels = [10],
                Properties =
                [
                    new GraphPropertyDto
                    {
                        PropertyId = 7,
                        Value = new GraphValueDto
                        {
                            Kind = GraphPropertyKind.String,
                            String = largeValue,
                        },
                    },
                ],
            })
            .ToArray();
        GraphImportLimitExceededException sdkError = await Assert.ThrowsAsync<GraphImportLimitExceededException>(
            () => frameClient.ImportAsync(
                "filtered",
                new GraphImportRequest
                {
                    RequestId = Guid.NewGuid(),
                    Vertices = oversizedVertices,
                }));
        Assert.Equal("batch", sdkError.LimitName);
        Assert.Equal(GraphImportLimits.MaxBatchBytes, sdkError.MaximumBytes);
    }

    [Fact]
    public async Task GraphApi_RemoteWeightedShortestPath_UsesSharedGraphContract()
    {
        using SndbGraphClient client = CreateGraphClient(AdminToken);
        await client.CreateGraphAsync("weighted");
        for (long id = 1; id <= 4; id++)
        {
            await client.UpsertVertexAsync(
                "weighted",
                new GraphUpsertVertexRequest { Id = id, RequestId = Guid.NewGuid(), Labels = [1] });
        }

        await client.UpsertEdgeAsync("weighted", WeightedEdge(11, 1, 2, 10));
        await client.UpsertEdgeAsync("weighted", WeightedEdge(12, 1, 3, 1));
        await client.UpsertEdgeAsync("weighted", WeightedEdge(13, 3, 2, 1));
        await client.UpsertEdgeAsync("weighted", WeightedEdge(14, 2, 4, 1));
        await client.UpsertEdgeAsync("weighted", WeightedEdge(15, 3, 4, 20));

        GraphWeightedPath result = Assert.IsType<GraphWeightedPath>(await client.WeightedShortestPathAsync(
            "weighted",
            new GraphWeightedShortestPathRequest
            {
                StartId = 1,
                TargetId = 4,
                WeightPropertyId = 1,
                Algorithm = GraphWeightedShortestPathAlgorithm.BidirectionalDijkstra,
            }));

        Assert.Equal(3d, result.TotalWeight);
        Assert.Equal([1L, 3L, 2L, 4L], result.VertexIds.Select(static id => id.Value).ToArray());
        Assert.Equal(GraphWeightedShortestPathAlgorithm.BidirectionalDijkstra, result.Algorithm);
        Assert.True(result.SnapshotSequence > 0);
    }

    [Fact]
    public async Task GraphApi_RemoteKnowledgeContract_UsesExistingGraphImportAndReferencesOnly()
    {
        using SndbGraphClient client = CreateGraphClient(AdminToken);
        await client.CreateGraphAsync("knowledge");
        var source = new KnowledgeContentReference(
            KnowledgeContentStoreKind.Object,
            "evidence",
            "manuals/alarm.pdf",
            "etag-7",
            chunkId: "page-12",
            contentHash: "sha256:page-12");
        var provenance = new KnowledgeProvenance(
            "test-extractor",
            "v1",
            "remote-run",
            DateTimeOffset.Parse("2026-08-23T00:00:00Z"),
            source);
        KnowledgeValidTime validTime = KnowledgeValidTime.Unbounded;
        var batch = new KnowledgeGraphBatch(
            Guid.Parse("36500000-0000-0000-0000-000000000002"),
            [
                new KnowledgeGraphNode("entity:alarm", KnowledgeGraphNodeKind.Entity, provenance)
                {
                    Name = "Alarm",
                },
                new KnowledgeGraphNode("claim:alarm-critical", KnowledgeGraphNodeKind.Claim, provenance)
                {
                    Claim = new KnowledgeClaimValue
                    {
                        SubjectId = "entity:alarm",
                        Predicate = "severity",
                        LiteralValue = "critical",
                    },
                    Confidence = 0.98,
                    ValidTime = validTime,
                },
                new KnowledgeGraphNode("chunk:alarm-page", KnowledgeGraphNodeKind.Chunk, provenance)
                {
                    Content = source,
                },
            ],
            [
                new KnowledgeGraphRelation(
                    "assert:alarm-critical",
                    KnowledgeGraphRelationKind.Asserts,
                    "entity:alarm",
                    "claim:alarm-critical",
                    provenance)
                {
                    Confidence = 0.98,
                    ValidTime = validTime,
                },
                new KnowledgeGraphRelation(
                    "evidence:alarm-critical",
                    KnowledgeGraphRelationKind.SupportedBy,
                    "claim:alarm-critical",
                    "chunk:alarm-page",
                    provenance)
                {
                    Confidence = 0.99,
                    ValidTime = validTime,
                },
            ]);

        GraphImportResponse first = await client.ImportKnowledgeGraphAsync("knowledge", batch);
        GraphImportResponse replay = await client.ImportKnowledgeGraphAsync("knowledge", batch);

        Assert.False(first.IsDuplicate);
        Assert.True(replay.IsDuplicate);
        GraphVertex claim = Assert.IsType<GraphVertex>(await client.GetVertexAsync(
            "knowledge",
            KnowledgeGraphMapper.GetNodeId("claim:alarm-critical")));
        Assert.Contains(KnowledgeGraphMapper.GetNodeLabelId(KnowledgeGraphNodeKind.Claim), claim.Labels);
        Assert.Equal(
            "manuals/alarm.pdf",
            claim.Properties.Single(property =>
                property.PropertyId == SndbGraphImporter.GetStablePropertyId("__kg_provenance_source_id"))
                .Value.AsString());
    }

    [Fact]
    public async Task GraphOperations_ReadSurfaceAndJsonExport_RoundTripsThroughTypedSdk()
    {
        using SndbGraphClient admin = CreateGraphClient(AdminToken);
        await admin.CreateGraphAsync("operations");
        await admin.UpsertVertexAsync(
            "operations",
            new GraphUpsertVertexRequest
            {
                Id = 1,
                RequestId = Guid.NewGuid(),
                Labels = [10],
                Properties =
                [
                    new GraphPropertyDto
                    {
                        PropertyId = 20,
                        Value = new GraphValueDto { Kind = GraphPropertyKind.String, String = "pump" },
                    },
                ],
            });
        await admin.UpsertVertexAsync(
            "operations",
            new GraphUpsertVertexRequest { Id = 2, RequestId = Guid.NewGuid(), Labels = [10] });
        await admin.UpsertEdgeAsync(
            "operations",
            new GraphUpsertEdgeRequest
            {
                Id = 100,
                RequestId = Guid.NewGuid(),
                SourceId = 1,
                TargetId = 2,
                LabelId = 30,
            });

        using SndbGraphClient reader = CreateGraphClient(ReadOnlyToken);
        GraphOperationsOverviewDto overview = await reader.GetOperationsOverviewAsync("operations");
        Assert.Equal(2, overview.VertexCount);
        Assert.Equal(1, overview.EdgeCount);
        Assert.Contains(overview.Labels, item => item.LabelId == 10 && item.ElementCount == 2);
        Assert.Contains(overview.DegreeHistogram, item => item.Degree == 1 && item.VertexCount == 1);
        Assert.True(overview.Capabilities.BoundedVisualization);
        Assert.True(overview.Capabilities.RestrictedEditing);
        Assert.Equal("server_sql_diagnostics", overview.SlowTraversalSource);

        GraphVisualizationDto visualization = await reader.GetVisualizationAsync("operations", limit: 10);
        Assert.False(visualization.Truncated);
        Assert.Equal([1L, 2L], visualization.Vertices.Select(static vertex => vertex.Id).ToArray());
        Assert.Equal(100, Assert.Single(visualization.Edges).Id);

        using var export = new MemoryStream();
        await reader.ExportJsonAsync("operations", export, maxElements: 10);
        export.Position = 0;
        using JsonDocument exported = await JsonDocument.ParseAsync(export);
        Assert.False(exported.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(3, exported.RootElement.GetProperty("elementCount").GetInt32());
        Assert.Equal(2, exported.RootElement.GetProperty("vertices").GetArrayLength());
        Assert.Equal(1, exported.RootElement.GetProperty("edges").GetArrayLength());

        await admin.CreateGraphAsync("operations_copy");
        export.Position = 0;
        SndbGraphImportReport report = await SndbGraphImporter.ImportJsonAsync(
            admin,
            "operations_copy",
            export,
            new SndbGraphImportOptions { RequestId = Guid.NewGuid(), BatchSize = 10 });
        Assert.Equal(2, report.VertexCount);
        Assert.Equal(1, report.EdgeCount);
        GraphOperationsOverviewDto copy = await reader.GetOperationsOverviewAsync("operations_copy");
        Assert.Equal(overview.VertexCount, copy.VertexCount);
        Assert.Equal(overview.EdgeCount, copy.EdgeCount);
    }

    [Fact]
    public async Task GraphMaintenance_RequiresAdminAndPersistsStagedDecisionAuditAcrossRestart()
    {
        using SndbGraphClient admin = CreateGraphClient(AdminToken);
        await admin.CreateGraphAsync("maintained");
        await admin.UpsertVertexAsync(
            "maintained",
            new GraphUpsertVertexRequest { Id = 1, RequestId = Guid.NewGuid(), Labels = [1] });
        GraphOperationsOverviewDto before = await admin.GetOperationsOverviewAsync("maintained");

        using HttpClient readOnly = CreateClient(ReadOnlyToken);
        HttpResponseMessage forbidden = await readOnly.PostAsJsonAsync(
            "/v1/db/graphapi/graphs/maintained/maintenance/stage",
            new GraphMaintenanceStageRequest { Action = GraphMaintenanceAction.Checkpoint },
            ServerJsonContext.Default.GraphMaintenanceStageRequest);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await readOnly.GetAsync("/v1/db/graphapi/graphs/maintained/maintenance/audit")).StatusCode);

        GraphMaintenanceApprovalDto staged = await admin.StageMaintenanceAsync(
            "maintained",
            new GraphMaintenanceStageRequest { Action = GraphMaintenanceAction.Checkpoint });
        Assert.Equal("staged", staged.State);
        GraphOperationsOverviewDto afterStage = await admin.GetOperationsOverviewAsync("maintained");
        Assert.Equal(before.SnapshotSequence, afterStage.SnapshotSequence);
        Assert.Equal(before.VertexCount, afterStage.VertexCount);

        GraphMaintenanceApprovalDto completed = await admin.ApproveMaintenanceAsync(
            "maintained",
            staged.ApprovalId);
        Assert.Equal("completed", completed.State);
        Assert.True(completed.Result?.IsComplete);

        using HttpClient adminHttp = CreateClient(AdminToken);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await adminHttp.PostAsync(
                $"/v1/db/graphapi/graphs/maintained/maintenance/{staged.ApprovalId:D}/approve",
                content: null)).StatusCode);

        GraphMaintenanceApprovalDto rejectable = await admin.StageMaintenanceAsync(
            "maintained",
            new GraphMaintenanceStageRequest { Action = GraphMaintenanceAction.Compact });
        GraphMaintenanceApprovalDto rejected = await admin.RejectMaintenanceAsync(
            "maintained",
            rejectable.ApprovalId,
            "maintenance window closed");
        Assert.Equal("rejected", rejected.State);
        Assert.Equal("maintenance window closed", rejected.Reason);

        IReadOnlyList<GraphMaintenanceApprovalDto> beforeRestart = await admin.ListMaintenanceAuditAsync("maintained");
        Assert.Contains(beforeRestart, entry => entry.ApprovalId == staged.ApprovalId && entry.State == "completed");
        Assert.Contains(beforeRestart, entry => entry.ApprovalId == rejectable.ApprovalId && entry.State == "rejected");

        admin.Dispose();
        await RestartAsync();
        using SndbGraphClient reopened = CreateGraphClient(AdminToken);
        IReadOnlyList<GraphMaintenanceApprovalDto> afterRestart = await reopened.ListMaintenanceAuditAsync("maintained");
        Assert.Contains(afterRestart, entry => entry.ApprovalId == staged.ApprovalId && entry.State == "completed");
        Assert.Contains(afterRestart, entry => entry.ApprovalId == rejectable.ApprovalId && entry.State == "rejected");
    }

    private async Task RestartAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _app = BuildTestServer();
        await _app.StartAsync();
        await ResolveServerAddressesAsync();
    }

    private SndbGraphClient CreateGraphClient(
        string token,
        SndbTransportProtocol protocol = SndbTransportProtocol.Auto)
        => new(new SndbConnectionStringBuilder
        {
            DataSource = $"sonnetdb+http://{new Uri(protocol == SndbTransportProtocol.FrameHttp2 ? _frameH2Url! : _baseUrl!).Authority}/graphapi",
            Token = token,
            Timeout = 30,
            Protocol = protocol,
        }.ConnectionString);

    private HttpClient CreateClient(string token)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static GraphUpsertEdgeRequest WeightedEdge(long id, long source, long target, long weight)
        => new()
        {
            Id = id,
            RequestId = Guid.NewGuid(),
            SourceId = source,
            TargetId = target,
            LabelId = 2,
            Properties =
            [
                new GraphPropertyDto
                {
                    PropertyId = 1,
                    Value = new GraphValueDto
                    {
                        Kind = GraphPropertyKind.Int64,
                        Int64 = weight,
                    },
                },
            ],
        };

    private static GraphUpsertEdgeRequest Edge(long id, long source, long target)
        => new()
        {
            Id = id,
            RequestId = Guid.NewGuid(),
            SourceId = source,
            TargetId = target,
            LabelId = 2,
        };

    private static GraphUpsertVertexRequest FilterVertex(long id, int labelId, string value)
        => new()
        {
            Id = id,
            RequestId = Guid.NewGuid(),
            Labels = [labelId],
            Properties =
            [
                new GraphPropertyDto
                {
                    PropertyId = 7,
                    Value = new GraphValueDto
                    {
                        Kind = GraphPropertyKind.String,
                        String = value,
                    },
                },
            ],
        };

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _value;

        internal UnknownLengthContent(int length)
        {
            _value = new byte[length];
            Array.Fill(_value, (byte)' ');
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_value, 0, _value.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
