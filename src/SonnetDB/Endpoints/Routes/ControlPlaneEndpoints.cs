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
    private static void MapControlPlaneEndpoints(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var users = app.Services.GetRequiredService<UserStore>();
        var grants = app.Services.GetRequiredService<GrantsStore>();
        var metrics = app.Services.GetRequiredService<ServerMetrics>();
        var controlPlane = app.Services.GetRequiredService<SonnetDB.Sql.Execution.IControlPlane>();

        app.MapMethods("/v1/sql", new[] { "POST" }, (RequestDelegate)(async ctx =>
        {
            var isAdmin = DatabaseAccessEvaluator.IsServerAdmin(ctx);
            if (!isAdmin && BearerAuthMiddleware.GetUser(ctx) is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden",
                    "/v1/sql 仅 admin 或动态用户 token 可调用。").ConfigureAwait(false);
                return;
            }
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.SqlRequest).ConfigureAwait(false);
            if (req is null || string.IsNullOrEmpty(req.Sql))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含 sql。").ConfigureAwait(false);
                return;
            }
            var scopedControlPlane = CreateScopedControlPlane(ctx, controlPlane, users, grants, registry);
            await SqlEndpointHandler.HandleControlPlaneAsync(ctx, req, metrics, isAdmin, scopedControlPlane).ConfigureAwait(false);
        }));

        // ---- 认证 ----
        app.MapMethods("/v1/auth/login", new[] { "POST" }, (RequestDelegate)(async ctx =>
        {
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.LoginRequest).ConfigureAwait(false);
            if (req is null || string.IsNullOrEmpty(req.Username) || string.IsNullOrEmpty(req.Password))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含 username 与 password。").ConfigureAwait(false);
                return;
            }
            if (!users.VerifyPassword(req.Username, req.Password))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status401Unauthorized, "unauthorized", "用户名或密码错误。").ConfigureAwait(false);
                return;
            }
            var (token, tokenId) = users.IssueToken(req.Username);
            var resp = new LoginResponse(req.Username, token, tokenId, users.IsSuperuser(req.Username));
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.LoginResponse).ConfigureAwait(false);
        }));

        // ---- SSE：实时事件流（指标 / 慢查询 / 数据库事件）----
        var broadcaster = app.Services.GetRequiredService<EventBroadcaster>();
        app.MapGet("/v1/events", async (HttpContext ctx) =>
        {
            await SseEndpointHandler.HandleAsync(ctx, broadcaster, grants).ConfigureAwait(false);
        });

        // ---- Schema API ----
        app.MapGet("/v1/db/{db}/schema", async (HttpContext ctx, string db) =>
        {
            if (!TryResolveDatabase(ctx, registry, db, out var tsdb))
                return;
            var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
            if (!await TryRequireDatabasePermissionAsync(ctx, db, databasePermission, DatabasePermission.Read).ConfigureAwait(false))
                return;
            await SchemaEndpointHandler.Handle(db, tsdb).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        // ---- Maintenance API ----
        app.MapPost("/v1/db/{db}/maintenance", async (HttpContext ctx, string db) =>
        {
            if (!TryResolveDatabase(ctx, registry, db, out var tsdb))
                return;

            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.MaintenanceRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            var operation = MaintenanceEndpointHandler.NormalizeOperation(req.Operation);
            if (operation is "backup_verify" or "restore_dry_run" && !DatabaseAccessEvaluator.IsServerAdmin(ctx))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden",
                    $"{operation} 需要 server admin 权限。").ConfigureAwait(false);
                return;
            }

            var requiredPermission = operation == "health_check"
                ? DatabasePermission.Read
                : DatabasePermission.Admin;
            var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
            if (!await TryRequireDatabasePermissionAsync(ctx, db, databasePermission, requiredPermission).ConfigureAwait(false))
                return;

            await MaintenanceEndpointHandler.Handle(tsdb, req).ExecuteAsync(ctx).ConfigureAwait(false);
        });
    }
}
