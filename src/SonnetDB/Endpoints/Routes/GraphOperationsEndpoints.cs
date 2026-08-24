using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Auth;
using SonnetDB.Contracts;
using SonnetDB.Diagnostics;
using SonnetDB.Graphs;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Server.Graphs;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    private const int DefaultGraphVisualizationLimit = 250;
    private const int MaximumGraphVisualizationLimit = 1_000;
    private const int DefaultGraphExportLimit = 100_000;
    private const int MaximumGraphExportLimit = 1_000_000;

    private static void MapGraphOperationsEndpoints(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var grants = app.Services.GetRequiredService<GrantsStore>();
        var diagnostics = app.Services.GetRequiredService<SlowQueryDiagnostics>();
        var approvals = app.Services.GetRequiredService<GraphMaintenanceApprovalService>();

        app.MapGet("/v1/db/{db}/graphs/{graph}/operations/overview", async (
            HttpContext context,
            string db,
            string graph) =>
        {
            if (!await RequireGraphAccess(context, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            GraphStore? store = await TryOpenGraphAsync(context, registry, db, graph).ConfigureAwait(false);
            if (store is null)
                return;

            try
            {
                GraphOperationsOverviewDto overview = BuildGraphOperationsOverview(
                    store,
                    diagnostics.Ring.Snapshot(entry => IsGraphTraversal(entry, db, graph)),
                    context.RequestAborted);
                await Results.Json(overview, ServerJsonContext.Default.GraphOperationsOverviewDto)
                    .ExecuteAsync(context).ConfigureAwait(false);
            }
            catch (GraphStatisticsLimitExceededException exception)
            {
                await WriteSimpleErrorAsync(
                    context,
                    StatusCodes.Status413PayloadTooLarge,
                    "graph_statistics_budget_exceeded",
                    exception.Message).ConfigureAwait(false);
            }
        });

        app.MapGet("/v1/db/{db}/graphs/{graph}/operations/visualization", async (
            HttpContext context,
            string db,
            string graph) =>
        {
            if (!await RequireGraphAccess(context, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            GraphStore? store = await TryOpenGraphAsync(context, registry, db, graph).ConfigureAwait(false);
            if (store is null)
                return;

            int limit = ParseBoundedGraphQueryValue(
                context,
                "limit",
                DefaultGraphVisualizationLimit,
                MaximumGraphVisualizationLimit);
            GraphVisualizationDto visualization = BuildGraphVisualization(
                store,
                limit,
                context.RequestAborted);
            await Results.Json(visualization, ServerJsonContext.Default.GraphVisualizationDto)
                .ExecuteAsync(context).ConfigureAwait(false);
        });

        app.MapGet("/v1/db/{db}/graphs/{graph}/operations/export", async (
            HttpContext context,
            string db,
            string graph) =>
        {
            if (!await RequireGraphAccess(context, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            GraphStore? store = await TryOpenGraphAsync(context, registry, db, graph).ConfigureAwait(false);
            if (store is null)
                return;

            int maxElements = ParseBoundedGraphQueryValue(
                context,
                "maxElements",
                DefaultGraphExportLimit,
                MaximumGraphExportLimit);
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{graph}.graph.json\"";
            await WriteGraphExportAsync(
                context.Response.BodyWriter,
                store,
                maxElements,
                context.RequestAborted).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/graphs/{graph}/maintenance/stage", async (
            HttpContext context,
            string db,
            string graph) =>
        {
            if (!await RequireGraphAccess(context, registry, grants, db, DatabasePermission.Admin).ConfigureAwait(false))
                return;
            if (await TryOpenGraphAsync(context, registry, db, graph).ConfigureAwait(false) is null)
                return;
            GraphMaintenanceStageRequest? request = await ReadJsonAsync(
                context,
                ServerJsonContext.Default.GraphMaintenanceStageRequest).ConfigureAwait(false);
            if (request is null)
            {
                await WriteSimpleErrorAsync(context, StatusCodes.Status400BadRequest, "bad_request", "维护请求不能为空。")
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                GraphMaintenanceApprovalDto approval = approvals.Stage(
                    db,
                    graph,
                    request,
                    ResolveGraphOperationsPrincipal(context));
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                await Results.Json(approval, ServerJsonContext.Default.GraphMaintenanceApprovalDto)
                    .ExecuteAsync(context).ConfigureAwait(false);
            }
            catch (GraphMaintenanceApprovalException exception)
            {
                await WriteGraphMaintenanceErrorAsync(context, exception).ConfigureAwait(false);
            }
        });

        app.MapPost("/v1/db/{db}/graphs/{graph}/maintenance/{approvalId:guid}/approve", async (
            HttpContext context,
            string db,
            string graph,
            Guid approvalId) =>
        {
            if (!await RequireGraphAccess(context, registry, grants, db, DatabasePermission.Admin).ConfigureAwait(false))
                return;
            GraphStore? store = await TryOpenGraphAsync(context, registry, db, graph).ConfigureAwait(false);
            if (store is null)
                return;

            try
            {
                GraphMaintenanceApprovalDto approval = approvals.Approve(
                    db,
                    graph,
                    approvalId,
                    ResolveGraphOperationsPrincipal(context),
                    store,
                    context.RequestAborted);
                await Results.Json(approval, ServerJsonContext.Default.GraphMaintenanceApprovalDto)
                    .ExecuteAsync(context).ConfigureAwait(false);
            }
            catch (GraphMaintenanceApprovalException exception)
            {
                await WriteGraphMaintenanceErrorAsync(context, exception).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException
                or InvalidOperationException
                or UnauthorizedAccessException)
            {
                await WriteSimpleErrorAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "graph_maintenance_failed",
                    exception.Message).ConfigureAwait(false);
            }
        });

        app.MapPost("/v1/db/{db}/graphs/{graph}/maintenance/{approvalId:guid}/reject", async (
            HttpContext context,
            string db,
            string graph,
            Guid approvalId) =>
        {
            if (!await RequireGraphAccess(context, registry, grants, db, DatabasePermission.Admin).ConfigureAwait(false))
                return;
            if (await TryOpenGraphAsync(context, registry, db, graph).ConfigureAwait(false) is null)
                return;
            GraphMaintenanceDecisionRequest? request = context.Request.ContentLength is 0
                ? null
                : await ReadJsonAsync(
                    context,
                    ServerJsonContext.Default.GraphMaintenanceDecisionRequest).ConfigureAwait(false);
            try
            {
                GraphMaintenanceApprovalDto approval = approvals.Reject(
                    db,
                    graph,
                    approvalId,
                    ResolveGraphOperationsPrincipal(context),
                    request?.Reason);
                await Results.Json(approval, ServerJsonContext.Default.GraphMaintenanceApprovalDto)
                    .ExecuteAsync(context).ConfigureAwait(false);
            }
            catch (GraphMaintenanceApprovalException exception)
            {
                await WriteGraphMaintenanceErrorAsync(context, exception).ConfigureAwait(false);
            }
        });

        app.MapGet("/v1/db/{db}/graphs/{graph}/maintenance/audit", async (
            HttpContext context,
            string db,
            string graph) =>
        {
            if (!await RequireGraphAccess(context, registry, grants, db, DatabasePermission.Admin).ConfigureAwait(false))
                return;
            if (await TryOpenGraphAsync(context, registry, db, graph).ConfigureAwait(false) is null)
                return;
            int limit = ParseBoundedGraphQueryValue(context, "limit", 200, 2_000);
            var response = new GraphMaintenanceAuditListDto(approvals.List(db, graph, limit));
            await Results.Json(response, ServerJsonContext.Default.GraphMaintenanceAuditListDto)
                .ExecuteAsync(context).ConfigureAwait(false);
        });
    }

    private static GraphOperationsOverviewDto BuildGraphOperationsOverview(
        GraphStore store,
        IReadOnlyList<SlowQueryDiagnosticEntry> slowQueries,
        CancellationToken cancellationToken)
    {
        using GraphReadSession read = store.BeginRead();
        GraphStatistics statistics = read.RefreshStatistics(
            new GraphStatisticsRefreshOptions
            {
                MaxScannedEntries = 50_000_000,
                MaxStatisticGroups = 1_000_000,
            },
            cancellationToken);
        GraphLabelStatisticDto[] labels = statistics.LabelCardinality
            .OrderBy(static item => item.Key.Value)
            .Select(static item => new GraphLabelStatisticDto(item.Key.Value, item.Value))
            .ToArray();
        GraphIndexStatisticDto[] indexes = statistics.PropertyIndexCardinality
            .OrderBy(static item => item.Key.ElementKind)
            .ThenBy(static item => item.Key.LabelId.Value)
            .ThenBy(static item => item.Key.PropertyId)
            .ThenBy(static item => item.Key.ValueKind)
            .Select(static item => new GraphIndexStatisticDto(
                item.Key.ElementKind.ToString().ToLowerInvariant(),
                item.Key.LabelId.Value,
                item.Key.PropertyId,
                item.Key.ValueKind.ToString().ToLowerInvariant(),
                item.Value))
            .ToArray();
        GraphDegreeBucketDto[] degrees = statistics.DegreeHistogram
            .OrderBy(static item => item.Key)
            .Select(static item => new GraphDegreeBucketDto(item.Key, item.Value))
            .ToArray();
        GraphSlowTraversalDto[] traversals = slowQueries
            .Take(20)
            .Select(static item => new GraphSlowTraversalDto(
                item.TimestampMs,
                item.Fingerprint,
                item.ElapsedMs,
                item.RowCount,
                item.AccessPath,
                item.FallbackReason,
                item.Sql))
            .ToArray();
        return new GraphOperationsOverviewDto(
            new GraphInfoDto
            {
                Name = store.Name,
                StorageId = store.StorageId,
                RecordFormatVersion = store.RecordFormatVersion,
            },
            statistics.Sequence,
            statistics.VertexCount,
            statistics.EdgeCount,
            labels,
            indexes,
            degrees,
            traversals,
            "server_sql_diagnostics",
            CreateGraphOperationsCapabilities(slowTraversalDiagnostics: true, audit: true));
    }

    private static GraphVisualizationDto BuildGraphVisualization(
        GraphStore store,
        int limit,
        CancellationToken cancellationToken)
    {
        using GraphReadSession read = store.BeginRead();
        List<GraphVertexDto> vertices = ReadGraphVertices(read, limit + 1, cancellationToken);
        bool truncated = vertices.Count > limit;
        if (truncated)
            vertices.RemoveAt(vertices.Count - 1);
        var vertexIds = vertices.Select(static vertex => vertex.Id).ToHashSet();
        int edgeResultLimit = checked((limit * 2) + 1);
        int edgeScanLimit = Math.Min(100_000, Math.Max(edgeResultLimit, limit * 100));
        List<GraphEdgeDto> edges = [];
        using GraphCursor<GraphEdge> cursor = read.ScanEdges(new GraphCursorOptions
        {
            PageSize = 256,
            MaxResults = edgeScanLimit,
        });
        int scanned = 0;
        while (edges.Count < edgeResultLimit)
        {
            IReadOnlyList<GraphEdge> page = cursor.ReadNextPage(cancellationToken);
            if (page.Count == 0)
                break;
            scanned += page.Count;
            edges.AddRange(page
                .Where(edge => vertexIds.Contains(edge.SourceId.Value) && vertexIds.Contains(edge.TargetId.Value))
                .Select(ToDto));
        }
        if (edges.Count >= edgeResultLimit)
        {
            edges.RemoveRange(edgeResultLimit - 1, edges.Count - (edgeResultLimit - 1));
            truncated = true;
        }
        if (scanned >= edgeScanLimit)
            truncated = true;
        return new GraphVisualizationDto(read.Sequence, truncated, vertices, edges);
    }

    private static List<GraphVertexDto> ReadGraphVertices(
        GraphReadSession read,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var vertices = new List<GraphVertexDto>(maxResults);
        using GraphCursor<GraphVertex> cursor = read.ScanVertices(new GraphCursorOptions
        {
            PageSize = 256,
            MaxResults = maxResults,
        });
        while (vertices.Count < maxResults)
        {
            IReadOnlyList<GraphVertex> page = cursor.ReadNextPage(cancellationToken);
            if (page.Count == 0)
                break;
            vertices.AddRange(page.Select(ToDto));
        }
        return vertices;
    }

    private static async Task WriteGraphExportAsync(
        PipeWriter destination,
        GraphStore store,
        int maxElements,
        CancellationToken cancellationToken)
    {
        using GraphReadSession read = store.BeginRead();
        using var writer = new Utf8JsonWriter(destination);
        writer.WriteStartObject();
        writer.WriteNumber("snapshotSequence", read.Sequence);
        writer.WriteStartArray("vertices");
        int written = 0;
        bool truncated = false;
        using (GraphCursor<GraphVertex> cursor = read.ScanVertices(new GraphCursorOptions
        {
            PageSize = 256,
            MaxResults = Math.Min(maxElements + 1, MaximumGraphExportLimit + 1),
        }))
        {
            while (written <= maxElements)
            {
                IReadOnlyList<GraphVertex> page = cursor.ReadNextPage(cancellationToken);
                if (page.Count == 0)
                    break;
                foreach (GraphVertex vertex in page)
                {
                    if (written >= maxElements)
                    {
                        truncated = true;
                        break;
                    }
                    JsonSerializer.Serialize(writer, ToDto(vertex), ServerJsonContext.Default.GraphVertexDto);
                    written++;
                }
                if (truncated)
                    break;
                writer.Flush();
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        writer.WriteEndArray();
        writer.WriteStartArray("edges");
        if (!truncated && written < maxElements)
        {
            int remaining = maxElements - written;
            using GraphCursor<GraphEdge> cursor = read.ScanEdges(new GraphCursorOptions
            {
                PageSize = 256,
                MaxResults = remaining + 1,
            });
            while (written <= maxElements)
            {
                IReadOnlyList<GraphEdge> page = cursor.ReadNextPage(cancellationToken);
                if (page.Count == 0)
                    break;
                foreach (GraphEdge edge in page)
                {
                    if (written >= maxElements)
                    {
                        truncated = true;
                        break;
                    }
                    JsonSerializer.Serialize(writer, ToDto(edge), ServerJsonContext.Default.GraphEdgeDto);
                    written++;
                }
                if (truncated)
                    break;
                writer.Flush();
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        writer.WriteEndArray();
        writer.WriteBoolean("truncated", truncated);
        writer.WriteNumber("elementCount", written);
        writer.WriteEndObject();
        writer.Flush();
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static GraphOperationsCapabilitiesDto CreateGraphOperationsCapabilities(
        bool slowTraversalDiagnostics,
        bool audit)
        => new(
            SchemaAndIndexes: true,
            DegreeHistogram: true,
            SlowTraversalDiagnostics: slowTraversalDiagnostics,
            BoundedVisualization: true,
            RestrictedEditing: true,
            JsonImportExport: true,
            StagedMaintenance: true,
            Audit: audit);

    private static bool IsGraphTraversal(
        SlowQueryDiagnosticEntry entry,
        string database,
        string graph)
        => string.Equals(entry.Database, database, StringComparison.OrdinalIgnoreCase)
            && (entry.Sql.Contains("GRAPH_TABLE", StringComparison.OrdinalIgnoreCase)
                || entry.Sql.Contains("USE GRAPH", StringComparison.OrdinalIgnoreCase))
            && entry.Sql.Contains(graph, StringComparison.OrdinalIgnoreCase);

    private static int ParseBoundedGraphQueryValue(
        HttpContext context,
        string name,
        int defaultValue,
        int maximum)
        => int.TryParse(context.Request.Query[name], out int parsed)
            ? Math.Clamp(parsed, 1, maximum)
            : defaultValue;

    private static string ResolveGraphOperationsPrincipal(HttpContext context)
    {
        if (BearerAuthMiddleware.GetUser(context) is { } user)
            return user.UserName;
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
        return "credential:" + Convert.ToHexString(digest.AsSpan(0, 16));
    }

    private static Task WriteGraphMaintenanceErrorAsync(
        HttpContext context,
        GraphMaintenanceApprovalException exception)
    {
        int statusCode = exception.Code switch
        {
            "bad_request" => StatusCodes.Status400BadRequest,
            "graph_maintenance_approval_not_found" => StatusCodes.Status404NotFound,
            "graph_maintenance_approval_expired" => StatusCodes.Status409Conflict,
            "graph_maintenance_approval_not_pending" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status409Conflict,
        };
        return WriteSimpleErrorAsync(context, statusCode, exception.Code, exception.Message);
    }
}
