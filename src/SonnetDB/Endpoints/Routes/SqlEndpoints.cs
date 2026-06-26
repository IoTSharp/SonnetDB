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
    private static void MapSqlEndpoints(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var users = app.Services.GetRequiredService<UserStore>();
        var grants = app.Services.GetRequiredService<GrantsStore>();
        var metrics = app.Services.GetRequiredService<ServerMetrics>();

        // ---- SQL ----
        var controlPlane = app.Services.GetRequiredService<SonnetDB.Sql.Execution.IControlPlane>();
        app.MapPost("/v1/db/{db}/sql", async (HttpContext ctx, string db) =>
        {
            if (!TryResolveDatabase(ctx, registry, db, out var tsdb))
                return;
            var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
            if (!await TryRequireDatabasePermissionAsync(ctx, db, databasePermission, DatabasePermission.Read).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.SqlRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }
            var scopedControlPlane = CreateScopedControlPlane(ctx, controlPlane, users, grants, registry);
            await SqlEndpointHandler.HandleSingleAsync(ctx, tsdb, db, req, metrics,
                DatabaseAccessEvaluator.HasPermission(databasePermission, DatabasePermission.Write),
                DatabaseAccessEvaluator.IsServerAdmin(ctx),
                scopedControlPlane).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/sql/batch", async (HttpContext ctx, string db) =>
        {
            if (!TryResolveDatabase(ctx, registry, db, out var tsdb))
                return;
            var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
            if (!await TryRequireDatabasePermissionAsync(ctx, db, databasePermission, DatabasePermission.Read).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.SqlBatchRequest).ConfigureAwait(false);
            if (req is null || req.Statements.Count == 0)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体或 statements 不可为空。").ConfigureAwait(false);
                return;
            }
            var scopedControlPlane = CreateScopedControlPlane(ctx, controlPlane, users, grants, registry);
            await SqlEndpointHandler.HandleBatchAsync(ctx, tsdb, db, req, metrics,
                DatabaseAccessEvaluator.HasPermission(databasePermission, DatabasePermission.Write),
                DatabaseAccessEvaluator.IsServerAdmin(ctx),
                scopedControlPlane).ConfigureAwait(false);
        });
    }
}
