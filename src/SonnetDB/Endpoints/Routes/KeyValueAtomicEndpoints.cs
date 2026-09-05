using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SonnetDB.Auth;
using SonnetDB.Contracts;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Kv;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    private static void MapKvAtomicEndpoints(WebApplication app, TsdbRegistry registry, GrantsStore grants)
    {
        MapKvAtomic<KvSetRequest, KvSetResponse>(app, registry, grants, "set",
            ServerJsonContext.Default.KvSetRequest, ServerJsonContext.Default.KvSetResponse,
            static (kv, request, cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request.Value);
                return new KvSetResponse(kv.Set(request.Key, request.Value, KvSetCondition.Always, request.ExpiresAtUtc, cancellationToken).Version!.Value);
            });
        MapKvAtomic<KvCasRequest, KvCasResponse>(app, registry, grants, "cas",
            ServerJsonContext.Default.KvCasRequest, ServerJsonContext.Default.KvCasResponse,
            static (kv, request, cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request.Value);
                var result = kv.CompareAndSet(request.Key, request.ExpectedVersion, request.Value, request.ExpiresAtUtc, cancellationToken);
                return new KvCasResponse(result.Succeeded, result.CurrentVersion, result.NewVersion);
            });
        MapKvAtomic<KvExpireRequest, KvBooleanResponse>(app, registry, grants, "expire",
            ServerJsonContext.Default.KvExpireRequest, ServerJsonContext.Default.KvBooleanResponse,
            static (kv, request, cancellationToken) => new KvBooleanResponse(kv.ExpireAt(request.Key, request.ExpiresAtUtc, cancellationToken)));
        MapKvAtomic<KvDeleteRequest, KvBooleanResponse>(app, registry, grants, "persist",
            ServerJsonContext.Default.KvDeleteRequest, ServerJsonContext.Default.KvBooleanResponse,
            static (kv, request, cancellationToken) => new KvBooleanResponse(kv.Persist(request.Key, cancellationToken)));
        MapKvAtomic<KvConditionalSetRequest, KvConditionalSetResponse>(app, registry, grants, "set-conditional",
            ServerJsonContext.Default.KvConditionalSetRequest, ServerJsonContext.Default.KvConditionalSetResponse,
            static (kv, request, cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request.Value);
                var result = kv.Set(request.Key, request.Value, request.Condition, request.ExpiresAtUtc, cancellationToken);
                return new KvConditionalSetResponse(result.Applied, result.Version);
            });
        MapKvAtomic<KvSetRequest, KvExchangeResponse>(app, registry, grants, "get-and-set",
            ServerJsonContext.Default.KvSetRequest, ServerJsonContext.Default.KvExchangeResponse,
            static (kv, request, cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request.Value);
                return ToKvExchangeResponse(kv.GetAndSet(request.Key, request.Value, request.ExpiresAtUtc, cancellationToken));
            });
        MapKvAtomic<KvDeleteRequest, KvExchangeResponse>(app, registry, grants, "get-and-delete",
            ServerJsonContext.Default.KvDeleteRequest, ServerJsonContext.Default.KvExchangeResponse,
            static (kv, request, cancellationToken) => ToKvExchangeResponse(kv.GetAndDelete(request.Key, cancellationToken)));
    }

    private static void MapKvAtomic<TRequest, TResponse>(
        WebApplication app, TsdbRegistry registry, GrantsStore grants, string action,
        JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType,
        Func<KvKeyspace, TRequest, CancellationToken, TResponse> execute) where TRequest : class
    {
        app.MapPost($"/v1/db/{{db}}/kv/{{keyspace}}/{action}", async (HttpContext ctx, string db, string keyspace) =>
        {
            ctx.Response.Headers["X-SonnetDB-Contract-Version"] = "1";
            ctx.Response.Headers["X-Request-ID"] = ctx.TraceIdentifier;
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Write).ConfigureAwait(false))
                return;
            try
            {
                var request = await JsonSerializer.DeserializeAsync(ctx.Request.Body, requestType, ctx.RequestAborted).ConfigureAwait(false);
                ArgumentNullException.ThrowIfNull(request);
                ctx.RequestAborted.ThrowIfCancellationRequested();
                if (!registry.TryGet(db, out var tsdb))
                {
                    await WriteSimpleErrorAsync(ctx, 404, "db_not_found", "数据库不存在。").ConfigureAwait(false);
                    return;
                }
                var response = execute(tsdb.Keyspaces.Open(keyspace), request, ctx.RequestAborted);
                await Results.Json(response, responseType).ExecuteAsync(ctx).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException)
            {
                await WriteSimpleErrorAsync(ctx, 400, "bad_request", "KV 请求参数无效，请检查 key、value、条件和 UTC 过期时间。").ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await WriteSimpleErrorAsync(ctx, 503, "kv_write_timeout", "KV 写入等待超时，请检查检查点状态并核对写入结果。").ConfigureAwait(false);
            }
            catch (IOException)
            {
                await WriteSimpleErrorAsync(ctx, 500, "kv_io_error", "KV 存储操作失败，请检查服务端日志并核对写入结果。").ConfigureAwait(false);
            }
        });
    }

    private static KvExchangeResponse ToKvExchangeResponse(KvExchangeResult result) => new(
        result.PreviousEntry is { } previous
            ? new KvValueResponse(true, previous.Value.ToArray(), previous.Version, previous.ExpiresAtUtc)
            : new KvValueResponse(false, null, null, null),
        result.MutationVersion);
}
