using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SonnetDB.Auth;
using SonnetDB.Engine;
using SonnetDB.Graphs;
using SonnetDB.Hosting;
using SonnetDB.Json;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    private static readonly byte[] GraphNdjsonNewLine = "\n"u8.ToArray();

    private static void MapGraphEndpoints(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var grants = app.Services.GetRequiredService<GrantsStore>();

        app.MapGet("/v1/db/{db}/graphs", async (HttpContext ctx, string db) =>
        {
            if (!await RequireGraphAccess(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            registry.TryGet(db, out Tsdb tsdb);
            GraphInfoDto[] graphs = tsdb.Graphs.Catalog.Snapshot()
                .Select(static definition => new GraphInfoDto
                {
                    Name = definition.Name,
                    StorageId = definition.StorageId,
                    RecordFormatVersion = definition.RecordFormatVersion,
                })
                .ToArray();
            await JsonSerializer.SerializeAsync(
                ctx.Response.Body,
                graphs,
                ServerJsonContext.Default.GraphInfoDtoArray,
                ctx.RequestAborted).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/graphs", async (HttpContext ctx, string db) =>
        {
            if (!await RequireGraphAccess(ctx, registry, grants, db, DatabasePermission.Write).ConfigureAwait(false))
                return;
            GraphCreateRequest? request = await ReadJsonAsync(ctx, ServerJsonContext.Default.GraphCreateRequest).ConfigureAwait(false);
            if (request is null || string.IsNullOrWhiteSpace(request.Name))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "graph name 不能为空。").ConfigureAwait(false);
                return;
            }
            try
            {
                GraphStore store = tsdbFor(registry, db).Graphs.Create(request.Name);
                ctx.Response.StatusCode = StatusCodes.Status201Created;
                await JsonSerializer.SerializeAsync(
                    ctx.Response.Body,
                    ToInfo(store.Definition),
                    ServerJsonContext.Default.GraphInfoDto,
                    ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (ArgumentException exception)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", exception.Message).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "graph_conflict", exception.Message).ConfigureAwait(false);
            }
        });

        app.MapDelete("/v1/db/{db}/graphs/{graph}", async (HttpContext ctx, string db, string graph) =>
        {
            if (!await RequireGraphAccess(ctx, registry, grants, db, DatabasePermission.Write).ConfigureAwait(false))
                return;
            try
            {
                Tsdb tsdb = tsdbFor(registry, db);
                bool dropped = tsdb.Graphs.Drop(graph);
                ctx.Response.StatusCode = dropped ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
            }
            catch (ArgumentException exception)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", exception.Message).ConfigureAwait(false);
            }
        });

        app.MapGet("/v1/db/{db}/graphs/{graph}/vertices/{id:long}", async (HttpContext ctx, string db, string graph, long id) =>
        {
            if (!await RequireGraphAccess(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            if (id <= 0)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "vertex id 必须为正数。").ConfigureAwait(false);
                return;
            }
            GraphStore? store = await TryOpenGraphAsync(ctx, registry, db, graph).ConfigureAwait(false);
            if (store is null)
                return;
            using GraphReadSession read = store.BeginRead();
            GraphVertex? vertex = read.GetVertex(new GraphElementId(id));
            if (vertex is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await JsonSerializer.SerializeAsync(ctx.Response.Body, ToDto(vertex), ServerJsonContext.Default.GraphVertexDto, ctx.RequestAborted).ConfigureAwait(false);
        });

        app.MapGet("/v1/db/{db}/graphs/{graph}/edges/{id:long}", async (HttpContext ctx, string db, string graph, long id) =>
        {
            if (!await RequireGraphAccess(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            if (id <= 0)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "edge id 必须为正数。").ConfigureAwait(false);
                return;
            }
            GraphStore? store = await TryOpenGraphAsync(ctx, registry, db, graph).ConfigureAwait(false);
            if (store is null)
                return;
            using GraphReadSession read = store.BeginRead();
            GraphEdge? edge = read.GetEdge(new GraphElementId(id));
            if (edge is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await JsonSerializer.SerializeAsync(ctx.Response.Body, ToDto(edge), ServerJsonContext.Default.GraphEdgeDto, ctx.RequestAborted).ConfigureAwait(false);
        });

        app.MapPut("/v1/db/{db}/graphs/{graph}/vertices/{id:long}", async (HttpContext ctx, string db, string graph, long id) =>
        {
            if (!await RequireGraphAccess(ctx, registry, grants, db, DatabasePermission.Write).ConfigureAwait(false))
                return;
            GraphUpsertVertexRequest? request = await ReadJsonAsync(ctx, ServerJsonContext.Default.GraphUpsertVertexRequest).ConfigureAwait(false);
            if (request is null || id <= 0 || request.Id != id || request.RequestId == Guid.Empty)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "vertex id 和 requestId 必须有效且与路径一致。").ConfigureAwait(false);
                return;
            }
            GraphStore? store = await TryOpenGraphAsync(ctx, registry, db, graph).ConfigureAwait(false);
            if (store is null)
                return;
            try
            {
                GraphTransaction transaction = store.BeginTransaction(request.RequestId);
                transaction.UpsertVertex(new GraphElementId(id), request.ExpectedElementVersion, ToLabels(request.Labels ?? []), ToProperties(request.Properties ?? []), request.UniquePropertyIds ?? []);
                GraphCommitResult result = transaction.Commit(ctx.RequestAborted);
                await JsonSerializer.SerializeAsync(ctx.Response.Body, new GraphMutationResponse { Sequence = result.Sequence, IsDuplicate = result.IsDuplicate }, ServerJsonContext.Default.GraphMutationResponse, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsGraphConflict(exception))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "graph_conflict", exception.Message).ConfigureAwait(false);
            }
            catch (GraphCommitOutcomeUnknownException exception)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "graph_commit_unknown", exception.Message).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or GraphTransactionLimitExceededException)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", exception.Message).ConfigureAwait(false);
            }
        });

        app.MapPut("/v1/db/{db}/graphs/{graph}/edges/{id:long}", async (HttpContext ctx, string db, string graph, long id) =>
        {
            if (!await RequireGraphAccess(ctx, registry, grants, db, DatabasePermission.Write).ConfigureAwait(false))
                return;
            GraphUpsertEdgeRequest? request = await ReadJsonAsync(ctx, ServerJsonContext.Default.GraphUpsertEdgeRequest).ConfigureAwait(false);
            if (request is null || id <= 0 || request.Id != id || request.RequestId == Guid.Empty)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "edge id 和 requestId 必须有效且与路径一致。").ConfigureAwait(false);
                return;
            }
            GraphStore? store = await TryOpenGraphAsync(ctx, registry, db, graph).ConfigureAwait(false);
            if (store is null)
                return;
            try
            {
                GraphTransaction transaction = store.BeginTransaction(request.RequestId);
                transaction.UpsertEdge(new GraphElementId(id), request.ExpectedElementVersion, new GraphElementId(request.SourceId), new GraphElementId(request.TargetId), new LabelId(request.LabelId), ToProperties(request.Properties ?? []), request.UniquePropertyIds ?? []);
                GraphCommitResult result = transaction.Commit(ctx.RequestAborted);
                await JsonSerializer.SerializeAsync(ctx.Response.Body, new GraphMutationResponse { Sequence = result.Sequence, IsDuplicate = result.IsDuplicate }, ServerJsonContext.Default.GraphMutationResponse, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsGraphConflict(exception))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "graph_conflict", exception.Message).ConfigureAwait(false);
            }
            catch (GraphCommitOutcomeUnknownException exception)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "graph_commit_unknown", exception.Message).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or GraphTransactionLimitExceededException)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", exception.Message).ConfigureAwait(false);
            }
        });

        app.MapDelete("/v1/db/{db}/graphs/{graph}/vertices/{id:long}", async (HttpContext ctx, string db, string graph, long id) =>
        {
            if (!await RequireGraphAccess(ctx, registry, grants, db, DatabasePermission.Write).ConfigureAwait(false))
                return;
            GraphDeleteRequest? request = await ReadJsonAsync(ctx, ServerJsonContext.Default.GraphDeleteRequest).ConfigureAwait(false);
            if (request is null || id <= 0 || request.RequestId == Guid.Empty)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "requestId 必须有效。").ConfigureAwait(false);
                return;
            }
            GraphStore? store = await TryOpenGraphAsync(ctx, registry, db, graph).ConfigureAwait(false);
            if (store is null)
                return;
            try
            {
                GraphTransaction transaction = store.BeginTransaction(request.RequestId);
                transaction.DeleteVertex(new GraphElementId(id), request.ExpectedElementVersion);
                GraphCommitResult result = transaction.Commit(ctx.RequestAborted);
                await JsonSerializer.SerializeAsync(ctx.Response.Body, new GraphMutationResponse { Sequence = result.Sequence, IsDuplicate = result.IsDuplicate }, ServerJsonContext.Default.GraphMutationResponse, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsGraphConflict(exception))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "graph_conflict", exception.Message).ConfigureAwait(false);
            }
            catch (GraphCommitOutcomeUnknownException exception)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "graph_commit_unknown", exception.Message).ConfigureAwait(false);
            }
            catch (ArgumentException exception)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", exception.Message).ConfigureAwait(false);
            }
        });

        app.MapDelete("/v1/db/{db}/graphs/{graph}/edges/{id:long}", async (HttpContext ctx, string db, string graph, long id) =>
        {
            if (!await RequireGraphAccess(ctx, registry, grants, db, DatabasePermission.Write).ConfigureAwait(false))
                return;
            GraphDeleteRequest? request = await ReadJsonAsync(ctx, ServerJsonContext.Default.GraphDeleteRequest).ConfigureAwait(false);
            if (request is null || id <= 0 || request.RequestId == Guid.Empty)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "requestId 必须有效。").ConfigureAwait(false);
                return;
            }
            GraphStore? store = await TryOpenGraphAsync(ctx, registry, db, graph).ConfigureAwait(false);
            if (store is null)
                return;
            try
            {
                GraphTransaction transaction = store.BeginTransaction(request.RequestId);
                transaction.DeleteEdge(new GraphElementId(id), request.ExpectedElementVersion);
                GraphCommitResult result = transaction.Commit(ctx.RequestAborted);
                await JsonSerializer.SerializeAsync(ctx.Response.Body, new GraphMutationResponse { Sequence = result.Sequence, IsDuplicate = result.IsDuplicate }, ServerJsonContext.Default.GraphMutationResponse, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsGraphConflict(exception))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "graph_conflict", exception.Message).ConfigureAwait(false);
            }
            catch (GraphCommitOutcomeUnknownException exception)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "graph_commit_unknown", exception.Message).ConfigureAwait(false);
            }
            catch (ArgumentException exception)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", exception.Message).ConfigureAwait(false);
            }
        });

        app.MapPost("/v1/db/{db}/graphs/{graph}/expand", async (HttpContext ctx, string db, string graph) =>
        {
            if (!await RequireGraphAccess(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            GraphExpandRequest? request = await ReadJsonAsync(ctx, ServerJsonContext.Default.GraphExpandRequest).ConfigureAwait(false);
            if (!TryValidateExpandRequest(request, out string validationError))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", validationError).ConfigureAwait(false);
                return;
            }
            GraphStore? store = await TryOpenGraphAsync(ctx, registry, db, graph).ConfigureAwait(false);
            if (store is null)
                return;
            using GraphReadSession read = store.BeginRead();
            using GraphCursor<GraphExpansion> cursor = read.Expand(
                new GraphElementId(request.VertexId),
                request.Direction,
                request.EdgeLabelId is { } label ? new LabelId(label) : null,
                new GraphCursorOptions { PageSize = request.PageSize, MaxResults = request.MaxResults });
            var items = new List<GraphExpansionDto>();
            while (true)
            {
                IReadOnlyList<GraphExpansion> page = cursor.ReadNextPage(ctx.RequestAborted);
                if (page.Count == 0)
                    break;
                items.AddRange(page.Select(ToDto));
            }
            await JsonSerializer.SerializeAsync(ctx.Response.Body, new GraphExpandResponse { SnapshotSequence = cursor.SnapshotSequence, Items = items, IsExhausted = cursor.IsExhausted }, ServerJsonContext.Default.GraphExpandResponse, ctx.RequestAborted).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/graphs/{graph}/expand/stream", async (HttpContext ctx, string db, string graph) =>
        {
            if (!await RequireGraphAccess(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            GraphExpandRequest? request = await ReadJsonAsync(ctx, ServerJsonContext.Default.GraphExpandRequest).ConfigureAwait(false);
            if (!TryValidateExpandRequest(request, out string validationError))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", validationError).ConfigureAwait(false);
                return;
            }
            GraphStore? store = await TryOpenGraphAsync(ctx, registry, db, graph).ConfigureAwait(false);
            if (store is null)
                return;
            using GraphReadSession read = store.BeginRead();
            using GraphCursor<GraphExpansion> cursor = read.Expand(
                new GraphElementId(request.VertexId),
                request.Direction,
                request.EdgeLabelId is { } label ? new LabelId(label) : null,
                new GraphCursorOptions { PageSize = request.PageSize, MaxResults = request.MaxResults });
            ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
            while (true)
            {
                IReadOnlyList<GraphExpansion> page = cursor.ReadNextPage(ctx.RequestAborted);
                if (page.Count == 0)
                    break;
                foreach (GraphExpansion item in page)
                {
                    await JsonSerializer.SerializeAsync(ctx.Response.Body, ToDto(item), ServerJsonContext.Default.GraphExpansionDto, ctx.RequestAborted).ConfigureAwait(false);
                    await ctx.Response.Body.WriteAsync(GraphNdjsonNewLine, ctx.RequestAborted).ConfigureAwait(false);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
                }
            }
        });

        app.MapPost("/v1/db/{db}/graphs/{graph}/vertices/seek/stream", async (HttpContext ctx, string db, string graph) =>
        {
            if (!await RequireGraphAccess(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            GraphSeekRequest? request = await ReadJsonAsync(ctx, ServerJsonContext.Default.GraphSeekRequest).ConfigureAwait(false);
            if (!TryValidateSeekRequest(request, out string error))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", error).ConfigureAwait(false);
                return;
            }
            GraphStore? store = await TryOpenGraphAsync(ctx, registry, db, graph).ConfigureAwait(false);
            if (store is null)
                return;
            using GraphReadSession read = store.BeginRead();
            using GraphCursor<GraphVertex> cursor = request.PropertyId is { } propertyId
                ? read.SeekVertices(
                    new LabelId(request.LabelId),
                    propertyId,
                    ToValue(request.Value!),
                    new GraphCursorOptions { PageSize = request.PageSize, MaxResults = request.MaxResults })
                : read.SeekVerticesByLabel(
                    new LabelId(request.LabelId),
                    new GraphCursorOptions { PageSize = request.PageSize, MaxResults = request.MaxResults });
            ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
            while (true)
            {
                IReadOnlyList<GraphVertex> page = cursor.ReadNextPage(ctx.RequestAborted);
                if (page.Count == 0)
                    break;
                foreach (GraphVertex vertex in page)
                {
                    await JsonSerializer.SerializeAsync(ctx.Response.Body, ToDto(vertex), ServerJsonContext.Default.GraphVertexDto, ctx.RequestAborted).ConfigureAwait(false);
                    await ctx.Response.Body.WriteAsync(GraphNdjsonNewLine, ctx.RequestAborted).ConfigureAwait(false);
                }
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
            }
        });

        app.MapPost("/v1/db/{db}/graphs/{graph}/edges/seek/stream", async (HttpContext ctx, string db, string graph) =>
        {
            if (!await RequireGraphAccess(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            GraphSeekRequest? request = await ReadJsonAsync(ctx, ServerJsonContext.Default.GraphSeekRequest).ConfigureAwait(false);
            if (!TryValidateSeekRequest(request, out string error))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", error).ConfigureAwait(false);
                return;
            }
            GraphStore? store = await TryOpenGraphAsync(ctx, registry, db, graph).ConfigureAwait(false);
            if (store is null)
                return;
            using GraphReadSession read = store.BeginRead();
            using GraphCursor<GraphEdge> cursor = request.PropertyId is { } propertyId
                ? read.SeekEdges(
                    new LabelId(request.LabelId),
                    propertyId,
                    ToValue(request.Value!),
                    new GraphCursorOptions { PageSize = request.PageSize, MaxResults = request.MaxResults })
                : read.SeekEdgesByLabel(
                    new LabelId(request.LabelId),
                    new GraphCursorOptions { PageSize = request.PageSize, MaxResults = request.MaxResults });
            ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
            while (true)
            {
                IReadOnlyList<GraphEdge> page = cursor.ReadNextPage(ctx.RequestAborted);
                if (page.Count == 0)
                    break;
                foreach (GraphEdge edge in page)
                {
                    await JsonSerializer.SerializeAsync(ctx.Response.Body, ToDto(edge), ServerJsonContext.Default.GraphEdgeDto, ctx.RequestAborted).ConfigureAwait(false);
                    await ctx.Response.Body.WriteAsync(GraphNdjsonNewLine, ctx.RequestAborted).ConfigureAwait(false);
                }
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
            }
        });

        app.MapPost("/v1/db/{db}/graphs/{graph}/traverse/stream", async (HttpContext ctx, string db, string graph) =>
        {
            if (!await RequireGraphAccess(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            GraphTraversalRequest? request = await ReadJsonAsync(ctx, ServerJsonContext.Default.GraphTraversalRequest).ConfigureAwait(false);
            if (!TryValidateTraversalRequest(request, out string error))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", error).ConfigureAwait(false);
                return;
            }
            GraphStore? store = await TryOpenGraphAsync(ctx, registry, db, graph).ConfigureAwait(false);
            if (store is null)
                return;
            using GraphReadSession read = store.BeginRead();
            if (read.GetVertex(new GraphElementId(request.StartId)) is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status404NotFound, "vertex_not_found", $"vertex {request.StartId} 不存在。").ConfigureAwait(false);
                return;
            }
            GraphTraversalOptions options = ToTraversalOptions(request);
            using GraphCursor<GraphPath> cursor = request.Kind switch
            {
                GraphTraversalKind.BreadthFirst => read.Bfs(
                    new GraphElementId(request.StartId),
                    request.Direction,
                    ToOptionalLabel(request.EdgeLabelId),
                    options),
                GraphTraversalKind.DepthFirst => read.Dfs(
                    new GraphElementId(request.StartId),
                    request.Direction,
                    ToOptionalLabel(request.EdgeLabelId),
                    options),
                GraphTraversalKind.Paths => read.Paths(
                    new GraphElementId(request.StartId),
                    request.MinDepth,
                    request.MaxDepth,
                    request.Direction,
                    ToOptionalLabel(request.EdgeLabelId),
                    options),
                _ => throw new InvalidOperationException("未知 Graph traversal kind。"),
            };
            ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
            while (true)
            {
                IReadOnlyList<GraphPath> page = cursor.ReadNextPage(ctx.RequestAborted);
                if (page.Count == 0)
                    break;
                foreach (GraphPath path in page)
                {
                    await JsonSerializer.SerializeAsync(ctx.Response.Body, ToDto(path), ServerJsonContext.Default.GraphPathDto, ctx.RequestAborted).ConfigureAwait(false);
                    await ctx.Response.Body.WriteAsync(GraphNdjsonNewLine, ctx.RequestAborted).ConfigureAwait(false);
                }
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
            }
        });

        app.MapPost("/v1/db/{db}/graphs/{graph}/shortest-path", async (HttpContext ctx, string db, string graph) =>
        {
            if (!await RequireGraphAccess(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            GraphShortestPathRequest? request = await ReadJsonAsync(ctx, ServerJsonContext.Default.GraphShortestPathRequest).ConfigureAwait(false);
            if (!TryValidateShortestPathRequest(request, out string error))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", error).ConfigureAwait(false);
                return;
            }
            GraphStore? store = await TryOpenGraphAsync(ctx, registry, db, graph).ConfigureAwait(false);
            if (store is null)
                return;
            using GraphReadSession read = store.BeginRead();
            if (read.GetVertex(new GraphElementId(request.StartId)) is null
                || read.GetVertex(new GraphElementId(request.TargetId)) is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status404NotFound, "vertex_not_found", "shortest path 的起点或目标不存在。").ConfigureAwait(false);
                return;
            }
            GraphPath? path = read.ShortestPath(
                new GraphElementId(request.StartId),
                new GraphElementId(request.TargetId),
                request.Direction,
                ToOptionalLabel(request.EdgeLabelId),
                new GraphTraversalOptions
                {
                    MaxDepth = request.MaxDepth,
                    MaxFrontier = request.MaxFrontier,
                    MaxPaths = request.MaxFrontier,
                },
                ctx.RequestAborted);
            await JsonSerializer.SerializeAsync(
                ctx.Response.Body,
                new GraphShortestPathResponse
                {
                    SnapshotSequence = read.Sequence,
                    Path = path is null ? null : ToDto(path),
                },
                ServerJsonContext.Default.GraphShortestPathResponse,
                ctx.RequestAborted).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/graphs/{graph}/weighted-shortest-path", async (HttpContext ctx, string db, string graph) =>
        {
            if (!await RequireGraphAccess(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            GraphWeightedShortestPathRequest? request = await ReadJsonAsync(
                ctx,
                ServerJsonContext.Default.GraphWeightedShortestPathRequest).ConfigureAwait(false);
            if (!TryValidateWeightedShortestPathRequest(request, out string error))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", error).ConfigureAwait(false);
                return;
            }
            GraphStore? store = await TryOpenGraphAsync(ctx, registry, db, graph).ConfigureAwait(false);
            if (store is null)
                return;
            try
            {
                using GraphReadSession read = store.BeginRead();
                if (read.GetVertex(new GraphElementId(request.StartId)) is null
                    || read.GetVertex(new GraphElementId(request.TargetId)) is null)
                {
                    await WriteSimpleErrorAsync(
                        ctx,
                        StatusCodes.Status404NotFound,
                        "vertex_not_found",
                        "weighted shortest path 的起点或目标不存在。").ConfigureAwait(false);
                    return;
                }
                GraphWeightedPath? path = read.WeightedShortestPath(
                    new GraphElementId(request.StartId),
                    new GraphElementId(request.TargetId),
                    GraphWeightedShortestPathOptions.ForProperty(request.WeightPropertyId) with
                    {
                        Algorithm = request.Algorithm,
                        Direction = request.Direction,
                        EdgeLabelId = ToOptionalLabel(request.EdgeLabelId),
                        MaxDepth = request.MaxDepth,
                        MaxFrontier = request.MaxFrontier,
                        MaxVisitedVertices = request.MaxVisitedVertices,
                        MaxExpandedEdges = request.MaxExpandedEdges,
                        MaxTotalWeight = request.MaxTotalWeight ?? double.PositiveInfinity,
                        PageSize = request.PageSize,
                        MaxPageBytes = request.MaxPageBytes,
                    },
                    ctx.RequestAborted);
                await JsonSerializer.SerializeAsync(
                    ctx.Response.Body,
                    new GraphWeightedShortestPathResponse
                    {
                        SnapshotSequence = read.Sequence,
                        Algorithm = request.Algorithm,
                        TotalWeight = path?.TotalWeight,
                        ExpandedVertices = path?.ExpandedVertices ?? 0,
                        ExpandedEdges = path?.ExpandedEdges ?? 0,
                        Path = path is null ? null : ToDto(path.Path),
                    },
                    ServerJsonContext.Default.GraphWeightedShortestPathResponse,
                    ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (GraphWeightedPathLimitExceededException exception)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "graph_budget_exceeded", exception.Message).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is GraphNegativeWeightException
                or GraphMissingWeightException
                or GraphWeightTypeException
                or GraphWeightOverflowException)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "invalid_edge_weight", exception.Message).ConfigureAwait(false);
            }
        });

        app.MapPost("/v1/db/{db}/graphs/{graph}/import", async (HttpContext ctx, string db, string graph) =>
        {
            if (!await RequireGraphAccess(ctx, registry, grants, db, DatabasePermission.Write).ConfigureAwait(false))
                return;
            GraphImportRequest? request = await ReadJsonAsync(ctx, ServerJsonContext.Default.GraphImportRequest).ConfigureAwait(false);
            if (request is null || request.RequestId == Guid.Empty)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "导入 requestId 必须有效。").ConfigureAwait(false);
                return;
            }
            GraphImportVertexDto[] vertices = (request.Vertices ?? []).Concat(request.Nodes ?? []).ToArray();
            GraphImportEdgeDto[] edges = (request.Edges ?? []).Concat(request.Relationships ?? []).ToArray();
            long importCount = (long)vertices.Length + edges.Length;
            if (importCount == 0 || importCount > 10_000)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "导入批次必须包含 1 到 10,000 个元素。").ConfigureAwait(false);
                return;
            }
            if (vertices.Any(static vertex => vertex is null)
                || edges.Any(static edge => edge is null))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "导入批次不能包含 null 元素。").ConfigureAwait(false);
                return;
            }
            GraphStore? store = await TryOpenGraphAsync(ctx, registry, db, graph).ConfigureAwait(false);
            if (store is null)
                return;
            try
            {
                GraphTransaction transaction = store.BeginTransaction(request.RequestId);
                foreach (GraphImportVertexDto vertex in vertices)
                {
                    transaction.UpsertVertex(
                        new GraphElementId(vertex.Id),
                        vertex.ExpectedElementVersion,
                        ToLabels(vertex.Labels ?? []),
                        ToProperties(vertex.Properties ?? []),
                        vertex.UniquePropertyIds ?? []);
                }
                foreach (GraphImportEdgeDto edge in edges)
                {
                    transaction.UpsertEdge(
                        new GraphElementId(edge.Id),
                        edge.ExpectedElementVersion,
                        new GraphElementId(edge.SourceId),
                        new GraphElementId(edge.TargetId),
                        new LabelId(edge.LabelId),
                        ToProperties(edge.Properties ?? []),
                        edge.UniquePropertyIds ?? []);
                }
                GraphCommitResult result = transaction.Commit(ctx.RequestAborted);
                await JsonSerializer.SerializeAsync(
                    ctx.Response.Body,
                    new GraphImportResponse
                    {
                        Sequence = result.Sequence,
                        IsDuplicate = result.IsDuplicate,
                        VertexCount = vertices.Length,
                        EdgeCount = edges.Length,
                    },
                    ServerJsonContext.Default.GraphImportResponse,
                    ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsGraphConflict(exception))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "graph_conflict", exception.Message).ConfigureAwait(false);
            }
            catch (GraphCommitOutcomeUnknownException exception)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "graph_commit_unknown", exception.Message).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or GraphTransactionLimitExceededException)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", exception.Message).ConfigureAwait(false);
            }
        });
    }

    private static async Task<GraphStore?> TryOpenGraphAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        string database,
        string graph)
    {
        try
        {
            GraphStore? store = tsdbFor(registry, database).Graphs.TryOpen(graph);
            if (store is not null)
                return store;
            await WriteSimpleErrorAsync(
                ctx,
                StatusCodes.Status404NotFound,
                "graph_not_found",
                $"graph '{graph}' 不存在。").ConfigureAwait(false);
            return null;
        }
        catch (ArgumentException exception)
        {
            await WriteSimpleErrorAsync(
                ctx,
                StatusCodes.Status400BadRequest,
                "bad_request",
                exception.Message).ConfigureAwait(false);
            return null;
        }
    }

    private static bool TryValidateExpandRequest(
        [NotNullWhen(true)]
        GraphExpandRequest? request,
        out string validationError)
    {
        if (request is null)
        {
            validationError = "expand request 不能为空。";
            return false;
        }
        if (request.VertexId <= 0
            || request.EdgeLabelId is <= 0
            || !Enum.IsDefined(request.Direction)
            || request.PageSize is <= 0 or > 1_000
            || request.MaxResults is <= 0 or > 10_000)
        {
            validationError = "vertexId、direction、edgeLabelId 或读取预算无效。";
            return false;
        }
        validationError = string.Empty;
        return true;
    }

    private static bool TryValidateSeekRequest(
        [NotNullWhen(true)] GraphSeekRequest? request,
        out string validationError)
    {
        if (request is null
            || request.LabelId <= 0
            || request.PageSize is <= 0 or > 1_000
            || request.MaxResults is <= 0 or > 10_000
            || request.PropertyId is <= 0
            || (request.PropertyId is null) != (request.Value is null))
        {
            validationError = "labelId、property/value 或读取预算无效。";
            return false;
        }
        if (request.Value is not null)
        {
            try
            {
                _ = ToValue(request.Value);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidDataException or JsonException)
            {
                validationError = "property value 与 kind 不匹配。";
                return false;
            }
        }
        validationError = string.Empty;
        return true;
    }

    private static bool TryValidateTraversalRequest(
        [NotNullWhen(true)] GraphTraversalRequest? request,
        out string validationError)
    {
        if (request is null
            || request.StartId <= 0
            || !Enum.IsDefined(request.Kind)
            || !Enum.IsDefined(request.Direction)
            || !Enum.IsDefined(request.PathUniqueness)
            || request.EdgeLabelId is <= 0
            || request.MinDepth < 0
            || request.MaxDepth is < 0 or > 64
            || request.MinDepth > request.MaxDepth
            || request.MaxFrontier is <= 0 or > 10_000
            || request.MaxPaths is <= 0 or > 10_000
            || request.PageSize is <= 0 or > 1_000
            || (request.Kind != GraphTraversalKind.Paths && request.MinDepth != 0))
        {
            validationError = "Graph traversal 的起点、模式、方向、深度或执行预算无效。";
            return false;
        }
        validationError = string.Empty;
        return true;
    }

    private static bool TryValidateShortestPathRequest(
        [NotNullWhen(true)] GraphShortestPathRequest? request,
        out string validationError)
    {
        if (request is null
            || request.StartId <= 0
            || request.TargetId <= 0
            || !Enum.IsDefined(request.Direction)
            || request.EdgeLabelId is <= 0
            || request.MaxDepth is < 0 or > 64
            || request.MaxFrontier is <= 0 or > 10_000)
        {
            validationError = "Shortest path 的端点、方向、深度或 frontier 预算无效。";
            return false;
        }
        validationError = string.Empty;
        return true;
    }

    private static bool TryValidateWeightedShortestPathRequest(
        [NotNullWhen(true)] GraphWeightedShortestPathRequest? request,
        out string validationError)
    {
        if (request is null
            || request.StartId <= 0
            || request.TargetId <= 0
            || request.WeightPropertyId <= 0
            || !Enum.IsDefined(request.Algorithm)
            || !Enum.IsDefined(request.Direction)
            || request.EdgeLabelId is <= 0
            || request.MaxDepth is < 0 or > 64
            || request.MaxFrontier is <= 0 or > 100_000
            || request.MaxVisitedVertices is <= 0 or > 1_000_000
            || request.MaxExpandedEdges is <= 0 or > 10_000_000
            || request.PageSize is <= 0 or > 1_000
            || request.MaxPageBytes is <= 0 or > 128 * 1024 * 1024)
        {
            validationError = "加权 shortest path 的权重属性、端点、算法或执行预算无效。";
            return false;
        }
        if (request.MaxTotalWeight is { } maxWeight
            && (!double.IsFinite(maxWeight) || maxWeight < 0))
        {
            validationError = "加权 shortest path 的 MaxTotalWeight 必须为空或非负有限数。";
            return false;
        }
        validationError = string.Empty;
        return true;
    }

    private static GraphTraversalOptions ToTraversalOptions(GraphTraversalRequest request)
        => new()
        {
            MaxDepth = request.MaxDepth,
            MaxFrontier = request.MaxFrontier,
            MaxPaths = request.MaxPaths,
            PathUniqueness = request.PathUniqueness,
            PageSize = request.PageSize,
        };

    private static LabelId? ToOptionalLabel(int? labelId)
        => labelId is { } value ? new LabelId(value) : null;

    private static bool IsGraphConflict(Exception exception)
        => exception is GraphConcurrencyException
            or GraphUniqueConstraintException
            or GraphVertexDeleteRestrictedException
            or GraphRequestConflictException;

    private static async Task<bool> RequireGraphAccess(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        string db,
        DatabasePermission permission)
    {
        if (!TryResolveDatabase(ctx, registry, db, out _))
            return false;
        DatabasePermission effective = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
        return await TryRequireDatabasePermissionAsync(ctx, db, effective, permission).ConfigureAwait(false);
    }

    private static Tsdb tsdbFor(TsdbRegistry registry, string db)
        => registry.TryGet(db, out Tsdb tsdb) ? tsdb : throw new InvalidOperationException($"数据库 '{db}' 不存在。");

    private static GraphInfoDto ToInfo(GraphDefinition definition)
        => new() { Name = definition.Name, StorageId = definition.StorageId, RecordFormatVersion = definition.RecordFormatVersion };

    private static GraphVertexDto ToDto(GraphVertex vertex)
        => new() { Id = vertex.Id.Value, ElementVersion = vertex.ElementVersion, Labels = vertex.Labels.Select(static label => label.Value).ToArray(), Properties = vertex.Properties.Select(ToDto).ToArray() };

    private static GraphEdgeDto ToDto(GraphEdge edge)
        => new() { Id = edge.Id.Value, ElementVersion = edge.ElementVersion, SourceId = edge.SourceId.Value, TargetId = edge.TargetId.Value, LabelId = edge.LabelId.Value, Properties = edge.Properties.Select(ToDto).ToArray() };

    private static GraphExpansionDto ToDto(GraphExpansion expansion)
        => new() { AnchorId = expansion.AnchorId.Value, NeighborId = expansion.NeighborId.Value, Direction = expansion.Direction, Edge = ToDto(expansion.Edge) };

    private static GraphPathDto ToDto(GraphPath path)
        => new()
        {
            VertexIds = path.VertexIds.Select(static id => id.Value).ToArray(),
            EdgeIds = path.EdgeIds.Select(static id => id.Value).ToArray(),
        };

    private static GraphPropertyDto ToDto(GraphProperty property)
        => new() { PropertyId = property.PropertyId, Value = ToDto(property.Value) };

    private static GraphValueDto ToDto(GraphPropertyValue value)
        => value.Kind switch
        {
            GraphPropertyKind.Null => new() { Kind = value.Kind },
            GraphPropertyKind.Int64 => new() { Kind = value.Kind, Int64 = value.AsInt64() },
            GraphPropertyKind.Float64 => new() { Kind = value.Kind, Float64 = value.AsFloat64() },
            GraphPropertyKind.Boolean => new() { Kind = value.Kind, Boolean = value.AsBoolean() },
            GraphPropertyKind.String => new() { Kind = value.Kind, String = value.AsString() },
            GraphPropertyKind.DateTime => new() { Kind = value.Kind, DateTime = value.AsDateTime() },
            GraphPropertyKind.Blob => new() { Kind = value.Kind, BlobBase64 = Convert.ToBase64String(value.AsBlob()) },
            GraphPropertyKind.Json => new() { Kind = value.Kind, Json = value.AsJson() },
            _ => throw new InvalidDataException("未知 graph property kind。"),
        };

    private static IEnumerable<LabelId> ToLabels(IReadOnlyList<int> labels)
        => labels.Select(static value => new LabelId(value));

    private static IEnumerable<GraphProperty> ToProperties(IReadOnlyList<GraphPropertyDto> properties)
    {
        foreach (GraphPropertyDto property in properties)
        {
            if (property is null)
                throw new ArgumentException("Graph properties 不能包含 null 元素。", nameof(properties));
            yield return new GraphProperty(property.PropertyId, ToValue(property.Value));
        }
    }

    private static GraphPropertyValue ToValue(GraphValueDto value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
        {
            GraphPropertyKind.Null => GraphPropertyValue.Null,
            GraphPropertyKind.Int64 when value.Int64 is { } int64 => GraphPropertyValue.FromInt64(int64),
            GraphPropertyKind.Float64 when value.Float64 is { } float64 => GraphPropertyValue.FromFloat64(float64),
            GraphPropertyKind.Boolean when value.Boolean is { } boolean => GraphPropertyValue.FromBoolean(boolean),
            GraphPropertyKind.String when value.String is not null => GraphPropertyValue.FromString(value.String),
            GraphPropertyKind.DateTime when value.DateTime is { } dateTime => GraphPropertyValue.FromDateTime(dateTime),
            GraphPropertyKind.Blob when value.BlobBase64 is not null => GraphPropertyValue.FromBlob(Convert.FromBase64String(value.BlobBase64)),
            GraphPropertyKind.Json when value.Json is not null => GraphPropertyValue.FromJson(value.Json),
            _ => throw new ArgumentException("graph property value 与 kind 不匹配。", nameof(value)),
        };
    }
}
