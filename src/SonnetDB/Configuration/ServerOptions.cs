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
    /// 旧版慢查询开关。仅用于兼容尚未迁移到
    /// <c>SonnetDBServer:Observability:SlowQueryLog:Enabled</c> 的配置文件。
    /// </summary>
    public bool SlowQueryEnabled { get; set; } = true;

    /// <summary>
    /// 旧版慢查询基础阈值。仅用于兼容平铺配置。
    /// </summary>
    public int SlowQueryThresholdMs { get; set; } = 10_000;

    /// <summary>
    /// 旧版慢查询警告级阈值。仅用于兼容平铺配置。
    /// </summary>
    public int SlowQueryWarningThresholdMs { get; set; } = 30_000;

    /// <summary>
    /// 旧版慢查询严重级阈值。仅用于兼容平铺配置。
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
    /// MQTT 接入配置（M28 P5b #242/#243）。绑定路径：<c>"SonnetDBServer:Mqtt"</c>。
    /// </summary>
    public MqttBrokerOptions Mqtt { get; set; } = new();

    /// <summary>
    /// CoAP 接入配置（M30 #265/#266）。绑定路径：<c>"SonnetDBServer:Coap"</c>。
    /// </summary>
    public CoapServerOptions Coap { get; set; } = new();

    /// <summary>
    /// Line Protocol UDP 接入配置（M30 #267）。绑定路径：<c>"SonnetDBServer:LineProtocolUdp"</c>。
    /// </summary>
    public LineProtocolUdpOptions LineProtocolUdp { get; set; } = new();

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

    /// <summary>慢查询日志与 Top-N 统计配置。</summary>
    public SlowQueryLogOptions SlowQueryLog { get; set; } = new();
}

/// <summary>
/// 慢查询日志配置。达到基础阈值的 SQL 会进入进程内环形缓冲、结构化日志、
/// Activity 事件以及既有 SSE <c>slow_query</c> 通道。
/// </summary>
public sealed class SlowQueryLogOptions
{
    /// <summary>是否启用慢查询采集。默认 <c>true</c>。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 慢查询基础阈值（毫秒）。默认 <c>10000</c>；<c>0</c> 表示记录全部 SQL，
    /// 负数表示关闭采集。
    /// </summary>
    public int ThresholdMs { get; set; } = 10_000;

    /// <summary>警告级阈值（毫秒）。默认 <c>30000</c>；小于等于 0 表示禁用该级别。</summary>
    public int WarningThresholdMs { get; set; } = 30_000;

    /// <summary>严重级阈值（毫秒）。默认 <c>60000</c>；小于等于 0 表示禁用该级别。</summary>
    public int CriticalThresholdMs { get; set; } = 60_000;

    /// <summary>进程内慢查询环形缓冲容量。默认 <c>256</c>，有效范围为 16～4096。</summary>
    public int Capacity { get; set; } = 256;
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
/// MQTT 接入配置。MQTT 协议栈仅位于 Server 层，Core 不感知 MQTT。
/// </summary>
public sealed class MqttBrokerOptions
{
    /// <summary>
    /// 是否启用内建 MQTT broker。默认关闭；发布配置可显式打开。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// MQTT TCP 监听端口。默认 <c>1883</c>。
    /// </summary>
    public int Port { get; set; } = 1883;

    /// <summary>
    /// MQTT over WebSocket 路径。为空时不映射 WebSocket 入口。
    /// </summary>
    public string WebSocketPath { get; set; } = "/mqtt";

    /// <summary>
    /// 每个 MQTT 客户端最多桥接到 SonnetMQ 的订阅数量。
    /// </summary>
    public int MaxMqSubscriptionsPerClient { get; set; } = 32;

    /// <summary>
    /// 订阅外部 MQTT broker 的 client 配置（M28 P5b #243）。
    /// </summary>
    public MqttExternalClientOptions ExternalClient { get; set; } = new();
}

/// <summary>
/// 外部 MQTT broker 订阅配置。Server 作为 MQTT client 拉取既有 EMQX/Mosquitto 等 broker 的消息。
/// </summary>
public sealed class MqttExternalClientOptions
{
    /// <summary>
    /// 是否启用外部 MQTT client 订阅。默认关闭。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 外部 MQTT broker 主机名或 IP。
    /// </summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>
    /// 外部 MQTT broker 端口。默认 <c>1883</c>。
    /// </summary>
    public int Port { get; set; } = 1883;

    /// <summary>
    /// 是否使用 TLS 连接外部 broker。
    /// </summary>
    public bool UseTls { get; set; }

    /// <summary>
    /// 连接外部 broker 时使用的 client id。为空时使用默认稳定 id。
    /// </summary>
    public string ClientId { get; set; } = "sonnetdb-external-client";

    /// <summary>
    /// 外部 broker 用户名。为空表示不发送用户名/密码。
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// 外部 broker 密码。仅用于连接外部 broker，不映射为 SonnetDB 用户。
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// 是否使用 clean start 会话。默认 <c>true</c>，避免重启后重放外部 broker 积压消息。
    /// </summary>
    public bool CleanStart { get; set; } = true;

    /// <summary>
    /// 首次重连等待秒数。连接失败后按指数退避增长到 <see cref="MaxReconnectDelaySeconds"/>。
    /// </summary>
    public int ReconnectDelaySeconds { get; set; } = 5;

    /// <summary>
    /// 最大重连等待秒数。
    /// </summary>
    public int MaxReconnectDelaySeconds { get; set; } = 60;

    /// <summary>
    /// 向外部 broker 订阅的 topic filter 列表。收到的实际 topic 仍需匹配
    /// <c>db/{db}/m/{measurement}</c> 后才会落库。
    /// </summary>
    public List<MqttExternalSubscriptionOptions> Subscriptions { get; set; } = [];
}

/// <summary>
/// 外部 MQTT broker 的单个订阅项。
/// </summary>
public sealed class MqttExternalSubscriptionOptions
{
    /// <summary>
    /// MQTT topic filter，可使用 broker 支持的 <c>+</c> / <c>#</c> 通配符。
    /// </summary>
    public string TopicFilter { get; set; } = "db/+/m/+";

    /// <summary>
    /// 订阅 QoS。当前支持 <c>0</c> / <c>1</c>，默认 <c>1</c>。
    /// </summary>
    public int Qos { get; set; } = 1;
}

/// <summary>
/// CoAP 设备写入配置。CoAP 协议栈仅位于 Server 层，Core 不感知 CoAP。
/// </summary>
public sealed class CoapServerOptions
{
    /// <summary>
    /// 是否启用明文 CoAP UDP 服务端。默认关闭，需显式开启。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 明文 CoAP UDP 监听端口。默认 <c>5683</c>。
    /// </summary>
    public int Port { get; set; } = 5683;

    /// <summary>
    /// 单个 CoAP payload 最大字节数。默认 1MiB，块传输重组后仍受此限制。
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// DTLS/coaps 配置。默认关闭，启用后监听 <see cref="CoapDtlsOptions.Port"/>。
    /// </summary>
    public CoapDtlsOptions Dtls { get; set; } = new();
}

/// <summary>
/// CoAP DTLS PSK 传输配置。
/// </summary>
public sealed class CoapDtlsOptions
{
    /// <summary>
    /// 是否启用 <c>coaps</c> DTLS 监听。默认关闭。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// DTLS/coaps UDP 监听端口。默认 <c>5684</c>。
    /// </summary>
    public int Port { get; set; } = 5684;

    /// <summary>
    /// PSK identity 到明文 key 的映射。当前实现只支持 PSK，RPK/证书留作增量。
    /// </summary>
    public Dictionary<string, string> PskKeys { get; set; } = new();

    /// <summary>
    /// DTLS 会话空闲超时秒数。超时后清理远端会话状态。
    /// </summary>
    public int SessionIdleSeconds { get; set; } = 300;
}

/// <summary>
/// Line Protocol UDP 监听配置。UDP 是 fire-and-forget 入口，无鉴权、无响应和应用层背压，默认关闭。
/// </summary>
public sealed class LineProtocolUdpOptions
{
    /// <summary>
    /// 是否启用 Line Protocol UDP 监听。默认关闭，需显式开启并限定在可信内网。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// UDP 监听端口。默认 <c>8089</c>，对齐常见 InfluxDB UDP listener 配置。
    /// </summary>
    public int Port { get; set; } = 8089;

    /// <summary>
    /// 数据报写入的目标数据库名。UDP 包本身没有查询参数，启用时必须显式配置。
    /// </summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>
    /// 单个 UDP 数据报最大字节数。默认 65,507 字节（IPv4 UDP payload 上限）。
    /// </summary>
    public int MaxDatagramBytes { get; set; } = 65_507;

    /// <summary>
    /// Line Protocol timestamp 精度。支持 <c>n/ns</c>、<c>u/us/µs</c>、<c>ms</c>、<c>s</c>；
    /// 默认 <c>ns</c>，对齐 InfluxDB 写入语义。
    /// </summary>
    public string Precision { get; set; } = "ns";
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
