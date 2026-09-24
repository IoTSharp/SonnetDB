using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SonnetDB.Auth;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Kv;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    private static void MapKvDiagnosticsEndpoints(
        WebApplication app,
        TsdbRegistry registry,
        GrantsStore grants)
    {
        app.MapPost("/v1/db/{db}/kv/{keyspace}/diagnostics", async (HttpContext ctx, string db, string keyspace) =>
        {
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Read).ConfigureAwait(false))
                return;

            KvDiagnosticsRequest request = await ReadJsonAsync(ctx, ServerJsonContext.Default.KvDiagnosticsRequest).ConfigureAwait(false)
                ?? new KvDiagnosticsRequest();
            int topHotKeys = request.TopHotKeys ?? 10;
            if (topHotKeys is < 0 or > 100)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest,
                    "invalid_argument", "topHotKeys 必须在 0 至 100 之间。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            KvDiagnostics diagnostics = tsdb.Keyspaces.Open(keyspace).GetDiagnostics(topHotKeys);
            var response = new KvDiagnosticsResponse(
                diagnostics.TotalKeys,
                diagnostics.ActiveKeys,
                diagnostics.ExpiredKeys,
                diagnostics.ExpiringKeys,
                diagnostics.NearestExpiresAtUtc,
                diagnostics.MutableOverlayEntries,
                diagnostics.FrozenOverlayEntries,
                diagnostics.WalBytes,
                diagnostics.MaxSnapshotOverlayEntries,
                diagnostics.HotKeys.Select(static key => new KvHotKeyResponse(key.Key, key.Reads)).ToArray());
            await Results.Json(response, ServerJsonContext.Default.KvDiagnosticsResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });
    }
}
