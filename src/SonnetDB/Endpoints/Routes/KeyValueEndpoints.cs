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
    private static void MapKvEndpoints(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var grants = app.Services.GetRequiredService<GrantsStore>();

        // ---- KV API ----
        app.MapPost("/v1/db/{db}/kv/{keyspace}/get", async (HttpContext ctx, string db, string keyspace) =>
        {
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Read).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.KvGetRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            var entry = registry.TryGet(db, out var tsdb)
                ? tsdb.Keyspaces.Open(keyspace).GetEntry(req.Key)
                : null;
            var response = entry is null
                ? new KvValueResponse(false, null, null, null)
                : new KvValueResponse(true, entry.Value.ToArray(), entry.Version, entry.ExpiresAtUtc);
            await Results.Json(response, ServerJsonContext.Default.KvValueResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/kv/{keyspace}/get-many", async (HttpContext ctx, string db, string keyspace) =>
        {
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Read).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.KvGetManyRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            var kv = tsdb.Keyspaces.Open(keyspace);
            var values = req.Keys
                .Select(key =>
                {
                    var entry = kv.GetEntry(key);
                    return entry is null
                        ? new KvValueItemResponse(key, false, null, null, null)
                        : new KvValueItemResponse(key, true, entry.Value.ToArray(), entry.Version, entry.ExpiresAtUtc);
                })
                .ToArray();
            await Results.Json(new KvGetManyResponse(values), ServerJsonContext.Default.KvGetManyResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/kv/{keyspace}/set", async (HttpContext ctx, string db, string keyspace) =>
        {
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Write).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.KvSetRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            long version = tsdb.Keyspaces.Open(keyspace).Put(req.Key, req.Value, req.ExpiresAtUtc);
            await Results.Json(new KvSetResponse(version), ServerJsonContext.Default.KvSetResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/kv/{keyspace}/set-many", async (HttpContext ctx, string db, string keyspace) =>
        {
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Write).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.KvSetManyRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            var versions = tsdb.Keyspaces.Open(keyspace).PutMany(
                req.Entries.Select(static x => new KeyValuePair<string, byte[]>(x.Key, x.Value)),
                req.ExpiresAtUtc);
            await Results.Json(new KvSetManyResponse(versions), ServerJsonContext.Default.KvSetManyResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/kv/{keyspace}/incr", async (HttpContext ctx, string db, string keyspace) =>
        {
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Write).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.KvIncrementRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            var (value, version) = tsdb.Keyspaces.Open(keyspace).Increment(req.Key, req.Delta);
            await Results.Json(new KvIncrementResponse(value, version), ServerJsonContext.Default.KvIncrementResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/kv/{keyspace}/decr", async (HttpContext ctx, string db, string keyspace) =>
        {
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Write).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.KvIncrementRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            var (value, version) = tsdb.Keyspaces.Open(keyspace).Decrement(req.Key, req.Delta);
            await Results.Json(new KvIncrementResponse(value, version), ServerJsonContext.Default.KvIncrementResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/kv/{keyspace}/cas", async (HttpContext ctx, string db, string keyspace) =>
        {
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Write).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.KvCasRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            var result = tsdb.Keyspaces.Open(keyspace).CompareAndSet(req.Key, req.ExpectedVersion, req.Value, req.ExpiresAtUtc);
            await Results.Json(
                new KvCasResponse(result.Succeeded, result.CurrentVersion, result.NewVersion),
                ServerJsonContext.Default.KvCasResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/kv/{keyspace}/remove", async (HttpContext ctx, string db, string keyspace) =>
        {
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Write).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.KvDeleteRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            bool removed = tsdb.Keyspaces.Open(keyspace).Delete(req.Key);
            await Results.Json(new KvDeleteResponse(removed ? 1 : 0), ServerJsonContext.Default.KvDeleteResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/kv/{keyspace}/remove-many", async (HttpContext ctx, string db, string keyspace) =>
        {
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Write).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.KvDeleteManyRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            int removed = tsdb.Keyspaces.Open(keyspace).DeleteMany(req.Keys);
            await Results.Json(new KvDeleteResponse(removed), ServerJsonContext.Default.KvDeleteResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/kv/{keyspace}/expire", async (HttpContext ctx, string db, string keyspace) =>
        {
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Write).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.KvExpireRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            bool succeeded = tsdb.Keyspaces.Open(keyspace).ExpireAt(req.Key, req.ExpiresAtUtc);
            await Results.Json(new KvBooleanResponse(succeeded), ServerJsonContext.Default.KvBooleanResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/kv/{keyspace}/persist", async (HttpContext ctx, string db, string keyspace) =>
        {
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Write).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.KvDeleteRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            bool succeeded = tsdb.Keyspaces.Open(keyspace).Persist(req.Key);
            await Results.Json(new KvBooleanResponse(succeeded), ServerJsonContext.Default.KvBooleanResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/kv/{keyspace}/ttl", async (HttpContext ctx, string db, string keyspace) =>
        {
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Read).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.KvDeleteRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            var ttl = tsdb.Keyspaces.Open(keyspace).GetTimeToLive(req.Key);
            await Results.Json(new KvTtlResponse(ttl.Milliseconds, ttl.ExpiresAtUtc), ServerJsonContext.Default.KvTtlResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/kv/{keyspace}/scan-prefix", async (HttpContext ctx, string db, string keyspace) =>
        {
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Read).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.KvPrefixRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            var entries = tsdb.Keyspaces.Open(keyspace).ScanPrefix(req.Prefix, req.Limit)
                .Select(static entry => new KvEntryResponse(
                    System.Text.Encoding.UTF8.GetString(entry.Key.Span),
                    entry.Value.ToArray(),
                    entry.Version,
                    entry.ExpiresAtUtc))
                .ToArray();
            await Results.Json(new KvScanResponse(entries), ServerJsonContext.Default.KvScanResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/kv/{keyspace}/remove-prefix", async (HttpContext ctx, string db, string keyspace) =>
        {
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Write).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.KvPrefixRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            int removed = tsdb.Keyspaces.Open(keyspace).DeletePrefix(req.Prefix, req.Limit);
            await Results.Json(new KvDeleteResponse(removed), ServerJsonContext.Default.KvDeleteResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/kv/{keyspace}/clean-expired", async (HttpContext ctx, string db, string keyspace) =>
        {
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Write).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.KvCleanExpiredRequest).ConfigureAwait(false);
            registry.TryGet(db, out var tsdb);
            int removed = tsdb.Keyspaces.Open(keyspace).CleanExpired(limit: req?.Limit);
            await Results.Json(new KvDeleteResponse(removed), ServerJsonContext.Default.KvDeleteResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/kv/{keyspace}/stats", async (HttpContext ctx, string db, string keyspace) =>
        {
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Read).ConfigureAwait(false))
                return;
            registry.TryGet(db, out var tsdb);
            var stats = tsdb.Keyspaces.Open(keyspace).GetExpirationStats();
            var response = new KvStatsResponse(
                stats.TotalKeys,
                stats.ActiveKeys,
                stats.ExpiredKeys,
                stats.ExpiringKeys,
                stats.NearestExpiresAtUtc);
            await Results.Json(response, ServerJsonContext.Default.KvStatsResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });
    }
}
