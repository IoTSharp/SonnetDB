using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Kv;
using SonnetDB.Mcp;
using SonnetMQ;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    private static void MapCopilotEndpoints(this WebApplication app, ServerOptions serverOptions)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var grants = app.Services.GetRequiredService<GrantsStore>();
        var aiConfigStore = app.Services.GetRequiredService<AiConfigStore>();
        var httpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();
        var copilotReadiness = app.Services.GetRequiredService<CopilotReadiness>();

        AiEndpointHandler.Map(
            app,
            aiConfigStore,
            grants,
            registry,
            httpClientFactory,
            serverOptions.Copilot.Chat,
            serverOptions.Copilot.Embedding);
        CopilotChatEndpointHandler.Map(
            app,
            aiConfigStore,
            app.Services.GetRequiredService<ICopilotCloudGatewayClient>(),
            app.Services.GetRequiredService<CopilotLocalToolExecutor>(),
            grants,
            registry);

        // ---- Copilot 文档摄入 / 检索（PR #64）----
        var copilotOptions = serverOptions.Copilot;
        app.MapMethods("/v1/copilot/docs/ingest", new[] { "POST" }, (RequestDelegate)(async ctx =>
        {
            if (!copilotOptions.Enabled)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "copilot_disabled", "Copilot 子系统已禁用。").ConfigureAwait(false);
                return;
            }
            if (!DatabaseAccessEvaluator.IsServerAdmin(ctx))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden", "/v1/copilot/docs/ingest 仅 admin 可调用。").ConfigureAwait(false);
                return;
            }
            var readiness = copilotReadiness.Evaluate();
            if (!readiness.EmbeddingReady)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "embedding_not_ready",
                    $"Embedding provider 未就绪：{readiness.Reason ?? "unknown"}。").ConfigureAwait(false);
                return;
            }

            CopilotIngestRequest? req = null;
            if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Content-Type"))
                req = await ReadJsonAsync(ctx, ServerJsonContext.Default.CopilotIngestRequest).ConfigureAwait(false);
            req ??= new CopilotIngestRequest();

            var roots = (req.Roots is { Count: > 0 } ? req.Roots : copilotOptions.Docs.Roots).ToArray();
            var ingestor = app.Services.GetRequiredService<DocsIngestor>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var stats = await ingestor.IngestAsync(roots, req.Force, req.DryRun, ctx.RequestAborted).ConfigureAwait(false);
                var resp = new CopilotIngestResponse(
                    stats.ScannedFiles,
                    stats.IndexedFiles,
                    stats.SkippedFiles,
                    stats.DeletedFiles,
                    stats.WrittenChunks,
                    stats.DryRun,
                    sw.Elapsed.TotalMilliseconds);
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.CopilotIngestResponse).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status500InternalServerError, "ingest_failed", ex.Message).ConfigureAwait(false);
            }
        }));

        app.MapMethods("/v1/copilot/docs/search", new[] { "POST" }, (RequestDelegate)(async ctx =>
        {
            if (!copilotOptions.Enabled)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "copilot_disabled", "Copilot 子系统已禁用。").ConfigureAwait(false);
                return;
            }
            var readiness = copilotReadiness.Evaluate();
            if (!readiness.EmbeddingReady)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "embedding_not_ready",
                    $"Embedding provider 未就绪：{readiness.Reason ?? "unknown"}。").ConfigureAwait(false);
                return;
            }

            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.CopilotSearchRequest).ConfigureAwait(false);
            if (req is null || string.IsNullOrWhiteSpace(req.Query))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含 query。").ConfigureAwait(false);
                return;
            }

            var requested = req.K is null or <= 0 ? 5 : Math.Min(req.K.Value, 50);
            var search = app.Services.GetRequiredService<DocsSearchService>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var hits = await search.SearchAsync(req.Query, requested, ctx.RequestAborted).ConfigureAwait(false);
                var resp = new CopilotSearchResponse(
                    Query: req.Query,
                    Requested: requested,
                    Hits: hits.Select(h => new CopilotSearchHit(h.Source, h.Title, h.Section, h.Content, h.Score)).ToArray(),
                    ElapsedMilliseconds: sw.Elapsed.TotalMilliseconds);
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.CopilotSearchResponse).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status500InternalServerError, "search_failed", ex.Message).ConfigureAwait(false);
            }
        }));

        // ---- Copilot 技能库（PR #65）----
        app.MapMethods("/v1/copilot/skills/reload", new[] { "POST" }, (RequestDelegate)(async ctx =>
        {
            if (!copilotOptions.Enabled)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "copilot_disabled", "Copilot 子系统已禁用。").ConfigureAwait(false);
                return;
            }
            if (!DatabaseAccessEvaluator.IsServerAdmin(ctx))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden", "/v1/copilot/skills/reload 仅 admin 可调用。").ConfigureAwait(false);
                return;
            }
            var readiness = copilotReadiness.Evaluate();
            if (!readiness.EmbeddingReady)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "embedding_not_ready",
                    $"Embedding provider 未就绪：{readiness.Reason ?? "unknown"}。").ConfigureAwait(false);
                return;
            }

            CopilotSkillsIngestRequest? req = null;
            if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Content-Type"))
                req = await ReadJsonAsync(ctx, ServerJsonContext.Default.CopilotSkillsIngestRequest).ConfigureAwait(false);
            req ??= new CopilotSkillsIngestRequest();

            var root = string.IsNullOrWhiteSpace(req.Root) ? copilotOptions.Skills.Root : req.Root!;
            var registry2 = app.Services.GetRequiredService<SkillRegistry>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var stats = await registry2.IngestAsync(root, req.Force, req.DryRun, ctx.RequestAborted).ConfigureAwait(false);
                var resp = new CopilotSkillsIngestResponse(
                    stats.ScannedSkills,
                    stats.IndexedSkills,
                    stats.SkippedSkills,
                    stats.DeletedSkills,
                    stats.DryRun,
                    sw.Elapsed.TotalMilliseconds);
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.CopilotSkillsIngestResponse).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status500InternalServerError, "skills_reload_failed", ex.Message).ConfigureAwait(false);
            }
        }));

        app.MapMethods("/v1/copilot/skills/search", new[] { "POST" }, (RequestDelegate)(async ctx =>
        {
            if (!copilotOptions.Enabled)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "copilot_disabled", "Copilot 子系统已禁用。").ConfigureAwait(false);
                return;
            }
            var readiness = copilotReadiness.Evaluate();
            if (!readiness.EmbeddingReady)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "embedding_not_ready",
                    $"Embedding provider 未就绪：{readiness.Reason ?? "unknown"}。").ConfigureAwait(false);
                return;
            }

            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.CopilotSkillsSearchRequest).ConfigureAwait(false);
            if (req is null || string.IsNullOrWhiteSpace(req.Query))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含 query。").ConfigureAwait(false);
                return;
            }

            var requested = req.K is null or <= 0 ? 5 : Math.Min(req.K.Value, 50);
            var skillSearch = app.Services.GetRequiredService<SkillSearchService>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var hits = await skillSearch.SearchAsync(req.Query, requested, ctx.RequestAborted).ConfigureAwait(false);
                var resp = new CopilotSkillsSearchResponse(
                    Query: req.Query,
                    Requested: requested,
                    Hits: hits.Select(h => new CopilotSkillsSearchHit(h.Name, h.Description, h.Triggers, h.RequiresTools, h.Score)).ToArray(),
                    ElapsedMilliseconds: sw.Elapsed.TotalMilliseconds);
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.CopilotSkillsSearchResponse).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status500InternalServerError, "skills_search_failed", ex.Message).ConfigureAwait(false);
            }
        }));

        app.MapMethods("/v1/copilot/skills/list", new[] { "GET" }, (RequestDelegate)(async ctx =>
        {
            if (!copilotOptions.Enabled)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "copilot_disabled", "Copilot 子系统已禁用。").ConfigureAwait(false);
                return;
            }

            var skillRegistry = app.Services.GetRequiredService<SkillRegistry>();
            try
            {
                var items = skillRegistry.List();
                var resp = new CopilotSkillsListResponse(
                    items.Select(h => new CopilotSkillsSearchHit(h.Name, h.Description, h.Triggers, h.RequiresTools, h.Score)).ToArray());
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.CopilotSkillsListResponse).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status500InternalServerError, "skills_list_failed", ex.Message).ConfigureAwait(false);
            }
        }));

        app.MapMethods("/v1/copilot/skills/{name}", new[] { "GET" }, (RequestDelegate)(async ctx =>
        {
            if (!copilotOptions.Enabled)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "copilot_disabled", "Copilot 子系统已禁用。").ConfigureAwait(false);
                return;
            }

            var name = ctx.Request.RouteValues["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "缺少 name 路径参数。").ConfigureAwait(false);
                return;
            }

            var skillRegistry = app.Services.GetRequiredService<SkillRegistry>();
            try
            {
                var skill = skillRegistry.Load(name);
                if (skill is null)
                {
                    await WriteSimpleErrorAsync(ctx, StatusCodes.Status404NotFound, "skill_not_found", $"未找到技能 '{name}'。").ConfigureAwait(false);
                    return;
                }

                var resp = new CopilotSkillLoadResponse(
                    skill.Name,
                    skill.Description,
                    skill.Triggers,
                    skill.RequiresTools,
                    skill.Body,
                    skill.Source);
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.CopilotSkillLoadResponse).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status500InternalServerError, "skills_load_failed", ex.Message).ConfigureAwait(false);
            }
        }));

        // ---- Copilot 知识库可视化（M1.5）：只读 status ----
        app.MapMethods("/v1/copilot/knowledge/status", new[] { "GET" }, (RequestDelegate)(async ctx =>
        {
            if (!copilotOptions.Enabled)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "copilot_disabled", "Copilot 子系统已禁用。").ConfigureAwait(false);
                return;
            }

            try
            {
                var ingestor = app.Services.GetRequiredService<DocsIngestor>();
                var skillRegistry = app.Services.GetRequiredService<SkillRegistry>();
                var indexState = await ingestor.GetIndexStateAsync(ctx.RequestAborted).ConfigureAwait(false);
                var skillCount = skillRegistry.List().Count;

                var embeddingProvider = app.Services.GetRequiredService<IEmbeddingProvider>();
                var providerName = copilotOptions.Embedding.Provider ?? "builtin";
                var fallback = embeddingProvider is BuiltinHashEmbeddingProvider builtin && builtin.IsFallback;

                var docsRoots = copilotOptions.Docs.Roots
                    .Where(static root => !string.IsNullOrWhiteSpace(root))
                    .Select(static root => Path.IsPathRooted(root) ? Path.GetFullPath(root) : Path.GetFullPath(root, Directory.GetCurrentDirectory()))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var resp = new CopilotKnowledgeStatusResponse(
                    Enabled: true,
                    EmbeddingProvider: providerName,
                    EmbeddingFallback: fallback,
                    VectorDimension: BuiltinHashEmbeddingProvider.VectorDimension,
                    DocsRoots: docsRoots,
                    IndexedFiles: indexState.IndexedFiles,
                    IndexedChunks: indexState.IndexedChunks,
                    LastIngestedUtc: indexState.LastIngestedUtc,
                    SkillCount: skillCount);

                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.CopilotKnowledgeStatusResponse).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status500InternalServerError, "knowledge_status_failed", ex.Message).ConfigureAwait(false);
            }
        }));
    }
}
