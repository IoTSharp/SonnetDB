using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Auth;
using SonnetDB.Exceptions;
using SonnetDB.Generations;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.SemanticSearch;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    private static void MapRagManagementEndpoints(WebApplication app, TsdbRegistry registry, GrantsStore grants)
    {
        app.MapGet("/v1/db/{db}/semantic/rag/status", async (HttpContext ctx, string db) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Admin).ConfigureAwait(false)) return;
            registry.TryGet(db, out var database);
            await RunRagManagementAsync(ctx, async token =>
            {
                var status = await ctx.RequestServices.GetRequiredService<RagManagementService>()
                    .StatusAsync(database, ctx.Request.Query["stream"].ToString(), token).ConfigureAwait(false);
                await Results.Json(status, ServerJsonContext.Default.RagManagementStatus).ExecuteAsync(ctx).ConfigureAwait(false);
            }, seconds: 5).ConfigureAwait(false);
        });
        app.MapGet("/v1/db/{db}/semantic/rag/profiles", async (HttpContext ctx, string db) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Admin).ConfigureAwait(false)) return;
            await RunRagManagementAsync(ctx, async _ =>
            {
                var profiles = ctx.RequestServices.GetRequiredService<RagManagementService>().Profiles();
                await Results.Json(profiles, ServerJsonContext.Default.RagManagementProfiles).ExecuteAsync(ctx).ConfigureAwait(false);
            }, seconds: 5).ConfigureAwait(false);
        });
        app.MapGet("/v1/db/{db}/semantic/rag/audit", async (HttpContext ctx, string db) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Admin).ConfigureAwait(false)) return;
            registry.TryGet(db, out var database);
            await RunRagManagementAsync(ctx, async token =>
            {
                int limit = ParseOptionalInt(ctx.Request.Query["limit"].FirstOrDefault(), "limit") ?? 100;
                var page = new RagManagementAuditStore(database).Read(limit, ctx.Request.Query["continuationToken"].FirstOrDefault(), token);
                await Results.Json(page, ServerJsonContext.Default.RagManagementAuditPage).ExecuteAsync(ctx).ConfigureAwait(false);
            }, seconds: 5).ConfigureAwait(false);
        });
        app.MapPost("/v1/db/{db}/semantic/rag/{operation}", async (HttpContext ctx, string db, string operation) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Admin).ConfigureAwait(false)) return;
            registry.TryGet(db, out var database);
            await RunRagManagementAsync(ctx, async token =>
            {
                if (operation is not ("rebuild" or "resume" or "discard" or "cleanup"))
                    throw new ArgumentException("不支持的 RAG 管理操作。");
                // 单个 DTO 上界 16 KiB，未知字段被 source-generated 合同拒绝；不接受额外 provider 配置。
                if (ctx.Request.ContentLength is > 16 * 1024) throw new ArgumentException("RAG 管理请求超过 16 KiB。");
                using var bodyDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                bodyDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                byte[] bytes = new byte[16 * 1024 + 1];
                int read = await ctx.Request.Body.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false,
                    bodyDeadline.Token).ConfigureAwait(false);
                if (read > 16 * 1024) throw new ArgumentException("RAG 管理请求超过 16 KiB。");
                var request = JsonSerializer.Deserialize(bytes.AsSpan(0, read), ServerJsonContext.Default.RagManagementRequest)
                    ?? throw new ArgumentException("RAG 管理请求不能为空。");
                var result = await ctx.RequestServices.GetRequiredService<RagManagementService>()
                    .ExecuteAsync(database, operation, request, token).ConfigureAwait(false);
                await Results.Json(result, ServerJsonContext.Default.RagManagementResult).ExecuteAsync(ctx).ConfigureAwait(false);
            }, seconds: 125).ConfigureAwait(false);
        });
    }

    private static async Task RunRagManagementAsync(HttpContext ctx, Func<CancellationToken, Task> run, int seconds)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        try { await run(deadline.Token).ConfigureAwait(false); }
        catch (RagManagementAuditCompletionException)
        { await WriteSimpleErrorAsync(ctx, 503, "rag_audit_completion_failed", "操作结果尚未确认，完成审计未能持久化；请刷新状态和审计，勿自动重试。").ConfigureAwait(false); }
        catch (RagManagementEmbeddingException)
        { await WriteSimpleErrorAsync(ctx, 503, "rag_embedding_failed", "可信模型调用未完成；请查询任务状态和脱敏审计。").ConfigureAwait(false); }
        catch (RagManagementProfileException)
        { await WriteSimpleErrorAsync(ctx, 409, "profile_mismatch", "请求 profile 与服务端可信配置不匹配。").ConfigureAwait(false); }
        catch (DatabaseGenerationException exception)
        { await WriteSimpleErrorAsync(ctx, 409, exception.Code, "RAG generation 或任务身份已变化，请刷新状态后再操作。").ConfigureAwait(false); }
        catch (JsonException)
        { await WriteSimpleErrorAsync(ctx, 400, "bad_request", "RAG 请求 JSON 无效或含未允许字段。").ConfigureAwait(false); }
        catch (ArgumentException)
        { await WriteSimpleErrorAsync(ctx, 400, "bad_request", "RAG 管理参数无效，请检查 stream、revision、任务身份及操作字段。").ConfigureAwait(false); }
        catch (TimeoutException)
        { await WriteSimpleErrorAsync(ctx, 504, "rag_timeout", "RAG 操作超时；请查询状态和审计，失败任务可续跑。").ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
        { await WriteSimpleErrorAsync(ctx, 504, "rag_timeout", "RAG 操作超时；请查询状态和审计。").ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException or UnauthorizedAccessException)
        { await WriteSimpleErrorAsync(ctx, 503, "rag_unavailable", "RAG 操作或审计不可用；详见脱敏审计并刷新任务状态。").ConfigureAwait(false); }
    }
}
