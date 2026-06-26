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
    private static void MapHealthEndpoints(this WebApplication app)
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

        app.MapGet("/metrics", (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
            return ctx.Response.WriteAsync(PrometheusFormatter.Render(metrics, registry));
        });

    }
}
