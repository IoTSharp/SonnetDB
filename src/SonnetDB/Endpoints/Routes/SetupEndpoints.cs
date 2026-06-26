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
    private static void MapSetupEndpoints(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var installation = app.Services.GetRequiredService<InstallationStore>();

        app.MapGet("/v1/setup/status", () =>
        {
            var users = app.Services.GetRequiredService<UserStore>();
            var visibleDatabaseCount = registry.ListDatabases()
                .Count(static database => !DatabaseAccessEvaluator.IsSystemDatabase(database));
            var status = installation.GetStatus(users.Count, visibleDatabaseCount);
            var resp = new SetupStatusResponse(
                status.NeedsSetup,
                status.SuggestedServerId,
                status.ServerId,
                status.Organization,
                status.UserCount,
                status.DatabaseCount);
            return Results.Json(resp, ServerJsonContext.Default.SetupStatusResponse);
        });

        app.MapMethods("/v1/setup/initialize", new[] { "POST" }, (RequestDelegate)(async ctx =>
        {
            var users = app.Services.GetRequiredService<UserStore>();
            var visibleDatabaseCount = registry.ListDatabases()
                .Count(static database => !DatabaseAccessEvaluator.IsSystemDatabase(database));
            var status = installation.GetStatus(users.Count, visibleDatabaseCount);
            if (!status.NeedsSetup)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "already_initialized", "SonnetDB Server 已完成首次安装。").ConfigureAwait(false);
                return;
            }

            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.SetupInitializeRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不能为空。").ConfigureAwait(false);
                return;
            }

            try
            {
                users.CreateUser(req.Username, req.Password, isSuperuser: true);
                var tokenId = users.ImportToken(req.Username, req.BearerToken);
                var bootstrap = installation.CompleteInitialization(
                    req.ServerId,
                    req.Organization,
                    req.Username,
                    tokenId,
                    users.Count,
                    registry.Count);

                var resp = new SetupInitializeResponse(
                    bootstrap.ServerId,
                    bootstrap.Organization,
                    bootstrap.AdminUserName,
                    req.BearerToken.Trim(),
                    bootstrap.InitialTokenId,
                    IsSuperuser: true);

                ctx.Response.StatusCode = StatusCodes.Status201Created;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.SetupInitializeResponse).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "setup_conflict", ex.Message).ConfigureAwait(false);
            }
        }));

    }
}
