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
    /// 慢查询阈值（毫秒）。单条 SQL 实际耗时超过该值会通过 SSE
    /// <c>/v1/events</c> 广播 <c>slow_query</c> 事件。默认 <c>500</c>。
    /// </summary>
    public int SlowQueryThresholdMs { get; set; } = 500;

    /// <summary>
    /// SSE <c>metrics</c> 通道的快照推送周期（秒）。默认 <c>5</c>。
    /// </summary>
    public int MetricsTickSeconds { get; set; } = 5;

    /// <summary>
    /// Copilot 子系统配置。
    /// </summary>
    public CopilotOptions Copilot { get; set; } = new();
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
