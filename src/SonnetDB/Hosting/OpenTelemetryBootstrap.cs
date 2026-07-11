using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SonnetDB.Configuration;
using SonnetDB.Diagnostics;

namespace SonnetDB.Hosting;

/// <summary>
/// 负责 SonnetDB Server 的 OpenTelemetry 指标、追踪与导出器引导。
/// </summary>
internal static class OpenTelemetryBootstrap
{
    /// <summary>
    /// 按配置注册 OpenTelemetry Resource、Metrics、Tracing 与可选导出器。
    /// </summary>
    /// <param name="builder">Web 应用构建器。</param>
    /// <param name="serverOptions">服务器配置。</param>
    public static void Configure(WebApplicationBuilder builder, ServerOptions serverOptions)
    {
        string serviceVersion = typeof(global::SonnetDB.Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(global::SonnetDB.Program).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        var telemetryOptions = new OpenTelemetryBootstrapOptions();
        builder.Configuration.Bind(telemetryOptions);
        bool hasOtlpEndpoint = !string.IsNullOrWhiteSpace(telemetryOptions.OTEL_EXPORTER_OTLP_ENDPOINT);
        bool prometheusEnabled = serverOptions.Observability.Prometheus.Enabled;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: "sonnetdb",
                    serviceVersion: serviceVersion,
                    serviceInstanceId: Environment.MachineName + ":" + Environment.ProcessId)
                .AddAttributes([new KeyValuePair<string, object>("host.name", Environment.MachineName)]))
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(SonnetDbMeter.MeterName)
                    .AddMeter("SonnetDB.Server")
                    .AddMeter("SonnetDB.Copilot")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (hasOtlpEndpoint)
                    metrics.AddOtlpExporter();
                if (prometheusEnabled)
                    metrics.AddPrometheusExporter();
            })
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(SonnetDbActivitySource.SourceName)
                    .AddSource("SonnetDB.Copilot")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (hasOtlpEndpoint)
                    tracing.AddOtlpExporter();
                if (builder.Environment.IsDevelopment())
                    tracing.AddConsoleExporter();
            });
    }
}
