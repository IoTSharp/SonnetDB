using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Diagnostics;
using SonnetDB.Hosting;
using SonnetDB.Json;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    private const string DiagnosticsControlPlaneDatabase = "__control";

    /// <summary>
    /// 映射管理员 Diagnostic Dump、慢查询明细与 Top-N 指纹聚合诊断端点。
    /// </summary>
    private static void MapDiagnosticsEndpoints(this WebApplication app)
    {
        var diagnostics = app.Services.GetRequiredService<SlowQueryDiagnostics>();
        var grants = app.Services.GetRequiredService<GrantsStore>();

        var dumpEnabled = app.Services.GetRequiredService<IOptions<ServerOptions>>()
            .Value.Observability.DiagnosticDump.Enabled;
        if (dumpEnabled)
        {
            var dumpService = app.Services.GetRequiredService<DiagnosticDumpService>();
            app.MapGet("/v1/diagnostics/dump", (HttpContext ctx) =>
            {
                if (!DatabaseAccessEvaluator.IsServerAdmin(ctx))
                    return ForbiddenResult("仅 admin 可读取 Diagnostic Dump。");

                return Results.Json(
                    dumpService.Capture(),
                    ServerJsonContext.Default.DiagnosticDumpResponse);
            });
        }

        app.MapGet("/v1/diagnostics/slow-queries", (HttpContext ctx) =>
        {
            if (!TryBuildDiagnosticsFilter(ctx, grants, out var filter, out var error))
                return error!;

            var limit = ParseLimit(ctx, defaultValue: 100, maximum: diagnostics.Ring.Capacity);
            var snapshot = diagnostics.Ring.Snapshot(filter!);
            var options = diagnostics.Options;
            var response = new SlowQueryListResponse(
                options.Enabled && options.ThresholdMs >= 0,
                options.ThresholdMs,
                options.WarningThresholdMs,
                options.CriticalThresholdMs,
                diagnostics.Ring.Capacity,
                snapshot.Count,
                snapshot.Take(limit).ToList());
            return Results.Json(response, ServerJsonContext.Default.SlowQueryListResponse);
        });

        app.MapGet("/v1/diagnostics/top-queries", (HttpContext ctx) =>
        {
            if (!TryBuildDiagnosticsFilter(ctx, grants, out var filter, out var error))
                return error!;

            var limit = ParseLimit(ctx, defaultValue: 20, maximum: 100);
            var (items, sampleCount) = diagnostics.Ring.Top(filter!, limit);
            var options = diagnostics.Options;
            var response = new TopQueryListResponse(
                options.Enabled && options.ThresholdMs >= 0,
                diagnostics.Ring.Capacity,
                sampleCount,
                items);
            return Results.Json(response, ServerJsonContext.Default.TopQueryListResponse);
        });
    }

    private static bool TryBuildDiagnosticsFilter(
        HttpContext context,
        GrantsStore grants,
        out Func<SlowQueryDiagnosticEntry, bool>? filter,
        out IResult? error)
    {
        var requestedDatabase = context.Request.Query["database"].ToString().Trim();
        var isAdmin = DatabaseAccessEvaluator.IsServerAdmin(context);
        if (requestedDatabase.Length > 0)
        {
            if (!string.Equals(requestedDatabase, DiagnosticsControlPlaneDatabase, StringComparison.Ordinal)
                && !TsdbRegistry.IsValidName(requestedDatabase))
            {
                filter = null;
                error = BadRequestResult($"非法数据库名 '{requestedDatabase}'。");
                return false;
            }

            if (!CanReadDiagnosticsDatabase(context, grants, requestedDatabase, isAdmin))
            {
                filter = null;
                error = ForbiddenResult($"当前凭据无权读取数据库 '{requestedDatabase}' 的查询诊断。");
                return false;
            }

            filter = entry => string.Equals(entry.Database, requestedDatabase, StringComparison.Ordinal);
            error = null;
            return true;
        }

        filter = entry => CanReadDiagnosticsDatabase(context, grants, entry.Database, isAdmin);
        error = null;
        return true;
    }

    private static bool CanReadDiagnosticsDatabase(
        HttpContext context,
        GrantsStore grants,
        string database,
        bool isAdmin)
    {
        if (isAdmin)
            return true;
        if (string.Equals(database, DiagnosticsControlPlaneDatabase, StringComparison.Ordinal))
            return false;

        var permission = DatabaseAccessEvaluator.GetEffectivePermission(context, grants, database);
        return DatabaseAccessEvaluator.HasPermission(permission, DatabasePermission.Read);
    }

    private static int ParseLimit(HttpContext context, int defaultValue, int maximum)
    {
        var raw = context.Request.Query["limit"].ToString();
        if (!int.TryParse(raw, out var parsed))
            return defaultValue;
        return Math.Clamp(parsed, 1, maximum);
    }
}
