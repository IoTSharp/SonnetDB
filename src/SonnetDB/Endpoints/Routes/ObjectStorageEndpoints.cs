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
    private static void MapObjectStorageEndpoints(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var grants = app.Services.GetRequiredService<GrantsStore>();

        app.MapGet("/v1/db/{db}/s3", async (HttpContext ctx, string db) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            registry.TryGet(db, out var tsdb);
            await ObjectStorageEndpointHandler.HandleBucketsAsync(ctx, tsdb).ConfigureAwait(false);
        });

        app.MapMethods("/v1/db/{db}/s3/{bucket}", ["GET", "PUT", "POST", "DELETE"], async (HttpContext ctx, string db, string bucket) =>
        {
            var required = HttpMethods.IsGet(ctx.Request.Method) ? DatabasePermission.Read : DatabasePermission.Write;
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, required).ConfigureAwait(false))
                return;
            registry.TryGet(db, out var tsdb);
            await ObjectStorageEndpointHandler.HandleBucketAsync(ctx, tsdb, bucket).ConfigureAwait(false);
        });

        app.MapMethods("/v1/db/{db}/s3/{bucket}/{**key}", ["GET", "HEAD", "PUT", "POST", "DELETE"], async (HttpContext ctx, string db, string bucket, string key) =>
        {
            var required = HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)
                ? DatabasePermission.Read
                : DatabasePermission.Write;
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, required).ConfigureAwait(false))
                return;
            registry.TryGet(db, out var tsdb);
            ctx.Request.RouteValues["db"] = db;
            await ObjectStorageEndpointHandler.HandleObjectAsync(ctx, tsdb, bucket, key).ConfigureAwait(false);
        });

        app.MapMethods("/s3/{db}/{bucket}/{**key}", ["GET", "HEAD", "PUT", "DELETE"], async (HttpContext ctx, string db, string bucket, string key) =>
        {
            if (!TryResolveDatabase(ctx, registry, db, out var tsdb))
                return;
            await ObjectStorageEndpointHandler.HandlePresignedObjectAsync(ctx, tsdb, bucket, key).ConfigureAwait(false);
        });
    }
}
