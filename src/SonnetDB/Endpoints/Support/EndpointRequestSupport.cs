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
    private static bool TryResolveDatabase(HttpContext ctx, TsdbRegistry registry, string db, out Tsdb tsdb)
    {
        if (!TsdbRegistry.IsValidName(db))
        {
            _ = WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", $"非法数据库名 '{db}'。");
            tsdb = null!;
            return false;
        }
        if (!registry.TryGet(db, out tsdb))
        {
            _ = WriteSimpleErrorAsync(ctx, StatusCodes.Status404NotFound, "db_not_found", $"数据库 '{db}' 不存在。");
            return false;
        }
        return true;
    }

    private static async Task<bool> TryResolveKvAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        string db,
        string keyspace,
        DatabasePermission requiredPermission)
    {
        if (!TryResolveDatabase(ctx, registry, db, out _))
            return false;

        if (!IsValidKeyspaceName(keyspace))
        {
            await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request",
                $"非法 keyspace 名 '{keyspace}'。").ConfigureAwait(false);
            return false;
        }

        var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
        return await TryRequireDatabasePermissionAsync(ctx, db, databasePermission, requiredPermission).ConfigureAwait(false);
    }

    private static async Task<bool> TryResolveObjectStorageAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        string db,
        DatabasePermission requiredPermission)
    {
        if (!TryResolveDatabase(ctx, registry, db, out _))
            return false;

        var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
        return await TryRequireDatabasePermissionAsync(ctx, db, databasePermission, requiredPermission).ConfigureAwait(false);
    }

    private static async Task<bool> TryResolveMqAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        string db,
        string topic,
        DatabasePermission requiredPermission)
    {
        if (!TryResolveDatabase(ctx, registry, db, out _))
            return false;

        if (!IsValidKeyspaceName(topic))
        {
            await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request",
                $"非法 topic 名 '{topic}'。").ConfigureAwait(false);
            return false;
        }

        var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
        return await TryRequireDatabasePermissionAsync(ctx, db, databasePermission, requiredPermission).ConfigureAwait(false);
    }

    private static bool IsValidKeyspaceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 128)
            return false;

        for (int i = 0; i < name.Length; i++)
        {
            char ch = name[i];
            bool valid =
                ch is >= 'a' and <= 'z' ||
                ch is >= 'A' and <= 'Z' ||
                ch is >= '0' and <= '9' ||
                ch is '_' or '-' or '.';
            if (!valid)
                return false;
        }

        return true;
    }

    private static string QualifyMqTopic(string db, string topic) => db + "." + topic;

    private static async Task WriteSimpleErrorAsync(HttpContext ctx, int statusCode, string code, string message)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, new ErrorResponse(code, message),
            ServerJsonContext.Default.ErrorResponse, ctx.RequestAborted).ConfigureAwait(false);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (ctx.Request.ContentLength == 0)
            return null;
        try
        {
            return await JsonSerializer.DeserializeAsync(ctx.Request.Body, typeInfo, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static async Task<bool> TryBindMcpDatabaseAsync(HttpContext ctx, TsdbRegistry registry, GrantsStore grants)
    {
        if (!ctx.Request.Path.StartsWithSegments("/mcp", out var remaining))
            return false;

        var tail = remaining.Value;
        if (string.IsNullOrWhiteSpace(tail))
            return false;

        var segments = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        var databaseName = segments[0];
        if (!TsdbRegistry.IsValidName(databaseName))
        {
            await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request",
                $"非法数据库名 '{databaseName}'。").ConfigureAwait(false);
            return true;
        }

        if (!registry.TryGet(databaseName, out var tsdb))
        {
            await WriteSimpleErrorAsync(ctx, StatusCodes.Status404NotFound, "db_not_found",
                $"数据库 '{databaseName}' 不存在。").ConfigureAwait(false);
            return true;
        }

        var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, databaseName);
        if (!await TryRequireDatabasePermissionAsync(ctx, databaseName, databasePermission, DatabasePermission.Read).ConfigureAwait(false))
            return true;

        ctx.Items[SonnetDbMcpContextAccessor.DatabaseNameItemKey] = databaseName;
        ctx.Items[SonnetDbMcpContextAccessor.TsdbItemKey] = tsdb;
        return false;
    }

    private static SonnetDB.Sql.Execution.IControlPlane CreateScopedControlPlane(
        HttpContext ctx,
        SonnetDB.Sql.Execution.IControlPlane controlPlane,
        UserStore users,
        GrantsStore grants,
        TsdbRegistry registry)
        => new ScopedDatabaseListControlPlane(
            controlPlane,
            users,
            () => DatabaseAccessEvaluator.GetVisibleDatabases(ctx, grants, registry.ListDatabases()),
            BearerAuthMiddleware.GetUser(ctx));

    private static async Task<bool> TryRequireDatabasePermissionAsync(
        HttpContext ctx,
        string db,
        DatabasePermission actualPermission,
        DatabasePermission requiredPermission)
    {
        if (DatabaseAccessEvaluator.HasPermission(actualPermission, requiredPermission))
            return true;

        await WriteSimpleErrorAsync(
            ctx,
            StatusCodes.Status403Forbidden,
            "forbidden",
            $"当前凭据对数据库 '{db}' 没有 {requiredPermission.ToString().ToLowerInvariant()} 权限。").ConfigureAwait(false);
        return false;
    }

    private static IResult ForbiddenResult(string message)
        => Results.Json(new ErrorResponse("forbidden", message),
            ServerJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status403Forbidden);

    private static IResult BadRequestResult(string message)
        => Results.Json(new ErrorResponse("bad_request", message),
            ServerJsonContext.Default.ErrorResponse, statusCode: StatusCodes.Status400BadRequest);
}
