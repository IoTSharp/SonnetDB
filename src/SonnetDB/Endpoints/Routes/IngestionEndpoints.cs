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
    private static void MapIngestionEndpoints(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var grants = app.Services.GetRequiredService<GrantsStore>();
        var metrics = app.Services.GetRequiredService<ServerMetrics>();

        app.MapGet("/v1/db/{db}/geo/{measurement}/trajectory", async (HttpContext ctx, string db, string measurement) =>
        {
            if (!TryResolveDatabase(ctx, registry, db, out var tsdb))
                return;
            var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
            if (!await TryRequireDatabasePermissionAsync(ctx, db, databasePermission, DatabasePermission.Read).ConfigureAwait(false))
                return;
            await GeoEndpointHandler.HandleTrajectoryAsync(ctx, tsdb, measurement).ConfigureAwait(false);
        });

        // ---- PR #44：批量入库快路径三端点（绕开 SQL parser）----
        // PR #47：批量端点 payload 可达数百 MB，移除 Kestrel 默认 30MB 上限。
        app.MapPost("/v1/db/{db}/measurements/{m}/lp", async (HttpContext ctx, string db, string m) =>
            await HandleBulkAsync(ctx, registry, grants, metrics, db, m, BulkIngestEndpointHandler.Format.LineProtocol).ConfigureAwait(false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());
        app.MapPost("/v1/db/{db}/measurements/{m}/json", async (HttpContext ctx, string db, string m) =>
            await HandleBulkAsync(ctx, registry, grants, metrics, db, m, BulkIngestEndpointHandler.Format.Json).ConfigureAwait(false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());
        app.MapPost("/v1/db/{db}/measurements/{m}/bulk", async (HttpContext ctx, string db, string m) =>
            await HandleBulkAsync(ctx, registry, grants, metrics, db, m, BulkIngestEndpointHandler.Format.BulkValues).ConfigureAwait(false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());

        // ---- InfluxDB 兼容写入端点：让 Telegraf / EMQX / influx CLI 等生态工具可直接对接 ----
        // v1: POST /write?db=<db>&precision=<n|u|ms|s>
        // v2: POST /api/v2/write?bucket=<db>&org=<ignored>&precision=<ns|us|ms|s>
        // 与 /v1/db/{db}/measurements/{m}/lp 的关键差别：measurement 来自每行 LP，而非 URL。
        app.MapPost("/write", async (HttpContext ctx) =>
            await InfluxLineProtocolEndpointHandler.HandleAsync(
                ctx, registry, grants, metrics, InfluxLineProtocolEndpointHandler.ApiVersion.V1).ConfigureAwait(false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());
        app.MapPost("/api/v2/write", async (HttpContext ctx) =>
            await InfluxLineProtocolEndpointHandler.HandleAsync(
                ctx, registry, grants, metrics, InfluxLineProtocolEndpointHandler.ApiVersion.V2).ConfigureAwait(false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());

        // ---- Prometheus Remote Write v1 兼容入站端点 ----
        // POST /api/v1/prom/write?db=<name>，body = snappy(block) + protobuf(prometheus.WriteRequest)
        // 让 Prometheus / VictoriaMetrics agent / Grafana Alloy / OpenTelemetry Collector 直接对接 SonnetDB。
        app.MapPost("/api/v1/prom/write", async (HttpContext ctx) =>
            await PrometheusRemoteWriteEndpointHandler.HandleAsync(ctx, registry, grants, metrics).ConfigureAwait(false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());

    }

    private static async Task HandleBulkAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        ServerMetrics metrics,
        string db,
        string measurement,
        BulkIngestEndpointHandler.Format format)
    {
        if (!TryResolveDatabase(ctx, registry, db, out var tsdb))
            return;
        var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
        if (!await TryRequireDatabasePermissionAsync(ctx, db, databasePermission, DatabasePermission.Write).ConfigureAwait(false))
            return;
        if (string.IsNullOrWhiteSpace(measurement) || measurement.Length > 255)
        {
            await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request",
                $"非法 measurement 名 '{measurement}'。").ConfigureAwait(false);
            return;
        }
        await BulkIngestEndpointHandler.HandleAsync(ctx, tsdb, measurement, format, metrics).ConfigureAwait(false);
    }
}
