using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Auth;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.ObjectStorage;
using SonnetDB.SemanticSearch;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    private static void MapSemanticEmbeddingEndpoints(WebApplication app, TsdbRegistry registry, GrantsStore grants)
    {
        app.MapPost("/v1/db/{db}/semantic/embeddings/object", async (HttpContext ctx, string db) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            registry.TryGet(db, out var tsdb);
            try
            {
                var request = await ReadJsonAsync(ctx, ServerJsonContext.Default.ObjectEmbeddingRequest).ConfigureAwait(false);
                if (request?.Object is null)
                    throw new ArgumentException("必须提供固定版本的对象引用。");
                var result = await ctx.RequestServices.GetRequiredService<SemanticEmbeddingService>()
                    .EmbedObjectAsync(tsdb, request.Object, ctx.RequestAborted).ConfigureAwait(false);
                await Results.Json(result, ServerJsonContext.Default.ObjectEmbeddingResponse).ExecuteAsync(ctx).ConfigureAwait(false);
            }
            catch (SemanticEmbeddingPolicyException)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status403Forbidden, "semantic_egress_denied", "内容外发策略拒绝当前 provider。").ConfigureAwait(false);
            }
            catch (SemanticObjectVersionException)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "object_version_mismatch", "对象 ETag 与请求不一致。").ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status404NotFound, "object_not_found", "对象版本不存在。").ConfigureAwait(false);
            }
            catch (SndbObjectStorageException exception)
            {
                int status = exception.Code.EndsWith("_not_found", StringComparison.Ordinal)
                    ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest;
                await WriteSimpleErrorAsync(ctx, status, exception.Code, exception.Message).ConfigureAwait(false);
            }
            catch (ArgumentException exception)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", exception.Message).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status415UnsupportedMediaType, "unsupported_embedding_content", "对象媒体类型不在 provider 能力清单中。").ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status504GatewayTimeout, "semantic_timeout", "语义 embedding 调用超时。").ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "semantic_provider_unavailable", "语义 embedding provider 或审计存储不可用。").ConfigureAwait(false);
            }
        });

        app.MapGet("/v1/db/{db}/semantic/embeddings/audit", async (HttpContext ctx, string db) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Admin).ConfigureAwait(false))
                return;
            registry.TryGet(db, out var tsdb);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                int limit = ParseOptionalInt(ctx.Request.Query["limit"].FirstOrDefault(), "limit") ?? 100;
                var page = new SemanticEmbeddingAuditStore(tsdb).Read(limit,
                    ctx.Request.Query["continuationToken"].FirstOrDefault(), deadline.Token);
                await Results.Json(page, ServerJsonContext.Default.SemanticEmbeddingAuditPage).ExecuteAsync(ctx).ConfigureAwait(false);
            }
            catch (ArgumentException exception)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", exception.Message).ConfigureAwait(false);
            }
        });
    }

    private static Task WriteSemanticProviderFailureAsync(HttpContext ctx, Exception exception)
        => exception switch
        {
            SemanticEmbeddingPolicyException => WriteSimpleErrorAsync(ctx, StatusCodes.Status403Forbidden,
                "semantic_egress_denied", "内容外发策略拒绝当前 provider。"),
            TimeoutException => WriteSimpleErrorAsync(ctx, StatusCodes.Status504GatewayTimeout,
                "semantic_timeout", "语义 embedding 调用超时。"),
            _ => WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable,
                "semantic_provider_unavailable", exception.Message),
        };
}
