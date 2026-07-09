namespace SonnetDB.Hosting;

/// <summary>
/// OpenTelemetry 启动期环境配置。
/// </summary>
internal sealed class OpenTelemetryBootstrapOptions
{
    /// <summary>
    /// OTLP 导出端点。存在时启用 OpenTelemetry OTLP exporter。
    /// </summary>
    public string? OTEL_EXPORTER_OTLP_ENDPOINT { get; set; }
}
