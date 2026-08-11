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
using SonnetDB.Graphs;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

public sealed class GraphEndpointTests : IAsyncLifetime
{
    private const string AdminToken = "admin-graph-token";
    private const string ReadOnlyToken = "readonly-graph-token";
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(
            Path.GetTempPath(),
            "sonnetdb-graph-endpoint-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            Tokens = new Dictionary<string, string>
            {
                [AdminToken] = ServerRoles.Admin,
                [ReadOnlyToken] = ServerRoles.ReadOnly,
            },
        });
        await _app.StartAsync();
        IServerAddressesFeature addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        using HttpClient admin = CreateClient(AdminToken);
        HttpResponseMessage create = await admin.PostAsJsonAsync(
            "/v1/db",
            new CreateDatabaseRequest("graphapi"),
            ServerJsonContext.Default.CreateDatabaseRequest);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
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

    private SndbGraphClient CreateGraphClient(
        string token,
        SndbTransportProtocol protocol = SndbTransportProtocol.Auto)
        => new(new SndbConnectionStringBuilder
        {
            DataSource = $"sonnetdb+http://{new Uri(_baseUrl!).Authority}/graphapi",
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
}
