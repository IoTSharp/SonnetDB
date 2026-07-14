using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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

        app.MapHealthChecks("/healthz/live", new HealthCheckOptions
        {
            Predicate = static _ => false,
            ResponseWriter = WriteHealthReportAsync,
        });
        app.MapHealthChecks("/healthz/ready", new HealthCheckOptions
        {
            Predicate = static registration => registration.Tags.Contains("ready"),
            ResponseWriter = WriteHealthReportAsync,
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

    /// <summary>
    /// 以 AOT 友好的 ASP.NET Core HealthChecks 标准结构写出健康报告。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <param name="report">聚合后的健康检查报告。</param>
    private static async Task WriteHealthReportAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", report.Status.ToString());
            writer.WriteString("totalDuration", report.TotalDuration.ToString("c"));
            writer.WriteStartObject("entries");
            foreach (var entry in report.Entries.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject(entry.Key);
                writer.WriteString("status", entry.Value.Status.ToString());
                if (!string.IsNullOrWhiteSpace(entry.Value.Description))
                    writer.WriteString("description", entry.Value.Description);
                writer.WriteString("duration", entry.Value.Duration.ToString("c"));
                writer.WriteStartArray("tags");
                foreach (var tag in entry.Value.Tags.Order(StringComparer.Ordinal))
                    writer.WriteStringValue(tag);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted).ConfigureAwait(false);
    }
}
