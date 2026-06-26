using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Kv;
using SonnetDB.Mcp;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    private static void MapDatabaseEndpoints(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var grants = app.Services.GetRequiredService<GrantsStore>();

        app.MapGet("/v1/db", (HttpContext ctx) =>
        {
            var visibleDatabases = DatabaseAccessEvaluator.GetVisibleDatabases(ctx, grants, registry.ListDatabases());
            var resp = new DatabaseListResponse(visibleDatabases);
            return Results.Json(resp, ServerJsonContext.Default.DatabaseListResponse);
        });

        app.MapPost("/v1/db", async (HttpContext ctx) =>
        {
            var role = BearerAuthMiddleware.GetRole(ctx);
            if (!BearerAuthMiddleware.IsAdmin(role))
                return ForbiddenResult("仅 admin 可创建数据库。");
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.CreateDatabaseRequest).ConfigureAwait(false);
            if (req is null)
                return BadRequestResult("请求体不可为空。");
            if (!TsdbRegistry.IsValidName(req.Name))
                return BadRequestResult($"非法数据库名 '{req.Name}'。");
            var created = registry.TryCreate(req.Name, out _);
            return Results.Json(new DatabaseOperationResponse(req.Name, created ? "created" : "exists"),
                ServerJsonContext.Default.DatabaseOperationResponse,
                statusCode: created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        });

        app.MapDelete("/v1/db/{db}", (HttpContext ctx, string db) =>
        {
            var role = BearerAuthMiddleware.GetRole(ctx);
            if (!BearerAuthMiddleware.IsAdmin(role))
                return ForbiddenResult("仅 admin 可删除数据库。");
            if (!TsdbRegistry.IsValidName(db))
                return BadRequestResult($"非法数据库名 '{db}'。");
            var dropped = registry.Drop(db);
            return Results.Json(new DatabaseOperationResponse(db, dropped ? "dropped" : "not_found"),
                ServerJsonContext.Default.DatabaseOperationResponse,
                statusCode: dropped ? StatusCodes.Status200OK : StatusCodes.Status404NotFound);
        });
    }
}
