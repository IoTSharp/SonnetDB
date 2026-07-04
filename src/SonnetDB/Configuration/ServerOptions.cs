namespace SonnetDB.Configuration;

/// <summary>
/// 服务器配置。绑定路径：<c>"SonnetDBServer"</c>。
/// </summary>
public sealed class ServerOptions
{
    /// <summary>
    /// 数据库根目录。每个 db 在该目录下占一个子目录。
    /// </summary>
    public string DataRoot { get; set; } = "./sonnetdb-data";

    /// <summary>
    /// 启动时若 <see cref="DataRoot"/> 下存在子目录，是否自动作为已存在的数据库注册。
    /// </summary>
    public bool AutoLoadExistingDatabases { get; set; } = true;

    /// <summary>
    /// Bearer token → 角色映射。允许的角色：<c>admin</c>、<c>readwrite</c>、<c>readonly</c>。
    /// </summary>
    public Dictionary<string, string> Tokens { get; set; } = new();

    /// <summary>
    /// 是否对 <c>/healthz</c> 与 <c>/metrics</c> 端点豁免认证。默认 <c>true</c>。
    /// </summary>
    public bool AllowAnonymousProbes { get; set; } = true;

    /// <summary>
    /// 帮助文档静态站点根目录。若为空，则默认使用 <c>AppContext.BaseDirectory/wwwroot/help</c>。
    /// </summary>
    public string? HelpDocsRoot { get; set; }

    /// <summary>
    /// 是否启用慢查询事件。关闭后不再通过 SSE <c>/v1/events</c> 广播
    /// <c>slow_query</c> 事件。默认 <c>true</c>。
    /// </summary>
    public bool SlowQueryEnabled { get; set; } = true;

    /// <summary>
    /// 慢查询基础阈值（毫秒）。单条 SQL 实际耗时达到该值会通过 SSE
    /// <c>/v1/events</c> 广播 <c>slow_query</c> 事件。默认 <c>10000</c>。
    /// 设置为 <c>0</c> 表示记录全部 SQL，设置为负数表示关闭慢查询事件。
    /// </summary>
    public int SlowQueryThresholdMs { get; set; } = 10_000;

    /// <summary>
    /// 慢查询警告级阈值（毫秒）。达到该值的事件 <c>severity</c> 为
    /// <c>warning</c>。默认 <c>30000</c>；小于等于 0 表示不启用该级别。
    /// </summary>
    public int SlowQueryWarningThresholdMs { get; set; } = 30_000;

    /// <summary>
    /// 慢查询严重级阈值（毫秒）。达到该值的事件 <c>severity</c> 为
    /// <c>critical</c>。默认 <c>60000</c>；小于等于 0 表示不启用该级别。
    /// </summary>
    public int SlowQueryCriticalThresholdMs { get; set; } = 60_000;

    /// <summary>
    /// SSE <c>metrics</c> 通道的快照推送周期（秒）。默认 <c>5</c>。
    /// </summary>
    public int MetricsTickSeconds { get; set; } = 5;

    /// <summary>
    /// 可观测性配置（M17）。绑定路径：<c>"SonnetDBServer:Observability"</c>。
    /// </summary>
    public ObservabilityOptions Observability { get; set; } = new();

    /// <summary>
    /// Copilot 子系统配置。
    /// </summary>
    public CopilotOptions Copilot { get; set; } = new();
}

/// <summary>
/// 可观测性配置（M17 #90/#91）。指标 / 追踪默认开启（无导出目标时近零开销）；
/// Prometheus 端点默认关闭，需显式启用。
/// </summary>
public sealed class ObservabilityOptions
{
    /// <summary>Prometheus 拉取端点配置。</summary>
    public PrometheusOptions Prometheus { get; set; } = new();
}

/// <summary>
/// Prometheus 拉取端点配置。启用后 <c>/metrics</c> 由 OpenTelemetry Prometheus exporter 接管，
/// 暴露 <c>SonnetDB.Core</c> / <c>SonnetDB.Server</c> Meter 与 ASP.NET Core 指标；
/// 关闭（默认）时保留原有最小指标集文本端点。
/// </summary>
public sealed class PrometheusOptions
{
    /// <summary>是否启用 OpenTelemetry Prometheus 拉取端点。默认 <c>false</c>。</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// 三角色定义。
/// </summary>
public static class ServerRoles
{
    /// <summary>具备所有权限。</summary>
    public const string Admin = "admin";

    /// <summary>可读写数据，但不可创建/删除数据库。</summary>
    public const string ReadWrite = "readwrite";

    /// <summary>仅可执行 SELECT。</summary>
    public const string ReadOnly = "readonly";
}
