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

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    private static void MapHealthEndpoints(this WebApplication app, ServerOptions serverOptions)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var metrics = app.Services.GetRequiredService<ServerMetrics>();
        var copilotReadiness = app.Services.GetRequiredService<CopilotReadiness>();

        app.MapGet("/healthz", () =>
        {
            var readiness = copilotReadiness.Evaluate();
            var resp = new HealthResponse("ok", registry.Count, metrics.UptimeSeconds, readiness.Enabled, readiness.Ready);
            return Results.Json(resp, ServerJsonContext.Default.HealthResponse);
        });

        // M17 #91：启用 Prometheus exporter 时 /metrics 由 OpenTelemetry 拉取端点接管，
        // 暴露 SonnetDB.Core/SonnetDB.Server Meter + ASP.NET Core 指标（含 histogram bucket）；
        // 关闭（默认）时保留原有最小指标集文本端点（向后兼容既有 scrape 配置）。
        if (serverOptions.Observability.Prometheus.Enabled)
        {
            app.MapPrometheusScrapingEndpoint("/metrics");
        }
        else
        {
            app.MapGet("/metrics", (HttpContext ctx) =>
            {
                ctx.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
                return ctx.Response.WriteAsync(PrometheusFormatter.Render(metrics, registry));
            });
        }

    }
}
