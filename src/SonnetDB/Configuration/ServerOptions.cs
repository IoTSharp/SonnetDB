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
    /// 是否对 <c>/healthz</c>、<c>/healthz/live</c>、<c>/healthz/ready</c> 与 <c>/metrics</c> 端点豁免认证。默认 <c>true</c>。
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
    /// REST 与 frame-http2 SQL 请求的数据库级并发准入配置。
    /// </summary>
    public SqlHttpAdmissionOptions SqlHttpAdmission { get; set; } = new();

    /// <summary>
    /// KV 存储维护配置。这里只放服务器部署需要覆盖的有界恢复预算，日常写入预算仍使用 Core 默认值。
    /// </summary>
    public KvStorageOptions Kv { get; set; } = new();

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
    /// Modbus TCP 协议运行时配置（M34）。全局门禁默认关闭，catalog 中的 <c>ENABLED</c>
    /// 不能单独启动网络连接。
    /// </summary>
    public ModbusRuntimeOptions Modbus { get; set; } = new();

    /// <summary>
    /// Copilot 子系统配置。
    /// </summary>
    public CopilotOptions Copilot { get; set; } = new();

    /// <summary>
    /// 语义图片检索配置。模型文件由部署者提供，SonnetDB 不在运行时下载模型。
    /// </summary>
    public SemanticSearchOptions SemanticSearch { get; set; } = new();
}

/// <summary>
/// KV 关系表索引恢复配置。较大预算只在缺少干净关闭令牌的索引重建期间生效。
/// </summary>
public sealed class KvStorageOptions
{
    /// <summary>
    /// 索引重建允许的 WAL 最大字节数，默认与日常预算一致为 256 MiB；
    /// 服务端绑定会把非正数修正为 1 MiB，避免部署配置意外取消恢复上限。
    /// </summary>
    public long IndexRebuildMaxWalBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// 索引重建允许的可变覆盖层最大条目数，默认与日常预算一致为 100,000；
    /// 服务端绑定会把非正数修正为 1，避免部署配置意外取消恢复上限。
    /// </summary>
    public int IndexRebuildMaxOverlayEntries { get; set; } = 100_000;
}

/// <summary>
/// SigLIP2 文本/图片 embedding 与图片检索配置。
/// </summary>
public sealed class SemanticSearchOptions
{
    /// <summary>是否启用语义图片检索端点。默认关闭。</summary>
    public bool Enabled { get; set; }

    /// <summary>多模态 provider 名称。当前支持 <c>siglip2-onnx</c>。</summary>
    public string Provider { get; set; } = "siglip2-onnx";

    /// <summary>写入图片索引的 embedding profile 标识。</summary>
    public string Profile { get; set; } = "siglip2-base-patch16-224";

    /// <summary>SigLIP2 文本编码器 ONNX 文件路径。</summary>
    public string TextModelPath { get; set; } = string.Empty;

    /// <summary>SigLIP2 视觉编码器 ONNX 文件路径。</summary>
    public string VisionModelPath { get; set; } = string.Empty;

    /// <summary>SentencePiece <c>tokenizer.model</c> 文件路径。</summary>
    public string TokenizerModelPath { get; set; } = string.Empty;

    /// <summary>文本与图片 embedding 维度。SigLIP2 base 默认 768。</summary>
    public int Dimensions { get; set; } = 768;

    /// <summary>文本编码最大 token 数，包含 EOS 与右侧 padding。</summary>
    public int MaxTextTokens { get; set; } = 64;

    /// <summary>输入图片统一缩放的宽高。SigLIP2 patch16-224 默认 224。</summary>
    public int ImageSize { get; set; } = 224;

    /// <summary>单张输入图片最大字节数。默认 20 MiB。</summary>
    public int MaxImageBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>文本模型输入 tensor 名称。</summary>
    public string TextInputName { get; set; } = "input_ids";

    /// <summary>文本模型输出 tensor 名称。</summary>
    public string TextOutputName { get; set; } = "pooler_output";

    /// <summary>视觉模型输入 tensor 名称。</summary>
    public string VisionInputName { get; set; } = "pixel_values";

    /// <summary>视觉模型输出 tensor 名称。</summary>
    public string VisionOutputName { get; set; } = "pooler_output";

    /// <summary>
    /// ANN 后端。支持 <c>auto</c>、<c>managed</c> 与 <c>usearch</c>；默认在受支持平台优先
    /// USearch，否则使用托管 HNSW。
    /// </summary>
    public string Backend { get; set; } = "auto";

    /// <summary>USearch 不受当前 RID 支持或加载失败时，是否回退到托管 HNSW。</summary>
    public bool FallbackToManaged { get; set; } = true;

    /// <summary>搜索默认返回条数。</summary>
    public int DefaultTopK { get; set; } = 10;

    /// <summary>单次搜索允许的最大返回条数。</summary>
    public int MaxTopK { get; set; } = 100;
}

/// <summary>
/// <c>POST /v1/db/{db}/sql</c>、<c>/sql/batch</c> 与 frame-http2 SQL query 的
/// 数据库级并发准入配置。
/// 每个数据库拥有独立的许可和等待队列，避免慢存储路径耗尽服务器线程与内存。
/// </summary>
public sealed class SqlHttpAdmissionOptions
{
    /// <summary>每个数据库可同时执行的 SQL HTTP 请求数。默认 <c>4</c>。</summary>
    public int PermitLimit { get; set; } = 4;

    /// <summary>
    /// 每个数据库允许异步等待许可的请求数。默认 <c>8</c>；超出后 REST 返回 503，
    /// frame-http2 返回 <c>sql_overloaded</c> 错误帧。
    /// </summary>
    public int QueueLimit { get; set; } = 8;
}

/// <summary>
/// 可观测性配置（M17 #90/#91）。指标 / 追踪默认开启（无导出目标时近零开销）；
/// Prometheus 与 Diagnostic Dump 端点默认关闭，需显式启用。
/// </summary>
public sealed class ObservabilityOptions
{
    /// <summary>Prometheus 拉取端点配置。</summary>
    public PrometheusOptions Prometheus { get; set; } = new();

    /// <summary>慢查询日志与 Top-N 统计配置。</summary>
    public SlowQueryLogOptions SlowQueryLog { get; set; } = new();

    /// <summary>管理员 Diagnostic Dump 端点配置。</summary>
    public DiagnosticDumpOptions DiagnosticDump { get; set; } = new();
}

/// <summary>
/// Diagnostic Dump 端点配置。端点默认不映射，需显式启用。
/// </summary>
public sealed class DiagnosticDumpOptions
{
    /// <summary>是否启用 <c>GET /v1/diagnostics/dump</c>。默认 <c>false</c>。</summary>
    public bool Enabled { get; set; }
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
    /// Sparkplug B 工业遥测接入配置。
    /// </summary>
    public SparkplugOptions Sparkplug { get; set; } = new();

    /// <summary>
    /// 订阅外部 MQTT broker 的 client 配置（M28 P5b #243）。
    /// </summary>
    public MqttExternalClientOptions ExternalClient { get; set; } = new();
}

/// <summary>
/// Sparkplug B payload 解码和目标数据库配置。
/// </summary>
public sealed class SparkplugOptions
{
    /// <summary>
    /// 是否处理 <c>spBv1.0/...</c> topic。默认关闭，需显式指定目标数据库后启用。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Sparkplug metric 写入的 SonnetDB 数据库。数据库需预先创建。
    /// </summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>
    /// 单条 Sparkplug MQTT payload 最大字节数。默认 1 MiB。
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Primary Host Application 标识，用于 retained <c>spBv1.0/STATE/{hostId}</c>。
    /// </summary>
    public string HostId { get; set; } = "sonnetdb-primary";

    /// <summary>
    /// 是否发布 primary host 的 ONLINE/OFFLINE STATE。默认开启。
    /// </summary>
    public bool PublishHostState { get; set; } = true;

    /// <summary>
    /// 是否允许外部 MQTT 管理员发布 NCMD/DCMD。默认关闭；开启后仍需显式审批属性。
    /// </summary>
    public bool AllowCommands { get; set; }
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
/// Modbus TCP 运行时配置。第一版仅包含主动轮询外部设备的 master/client。
/// </summary>
public sealed class ModbusRuntimeOptions
{
    /// <summary>
    /// 是否启用 Modbus TCP master 运行时。默认关闭；启用后仍只运行 catalog 中
    /// <c>ENABLED TRUE</c> 的 source。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 扫描数据库与 catalog 变化的周期，单位为毫秒。默认 <c>250</c>。
    /// </summary>
    public int DiscoveryIntervalMilliseconds { get; set; } = 250;

    /// <summary>
    /// 单轮请求内首次重试的退避时间，单位为毫秒。后续重试指数增长。
    /// </summary>
    public int RetryBaseDelayMilliseconds { get; set; } = 100;

    /// <summary>单轮请求内重试退避的最大时间，单位为毫秒。</summary>
    public int MaxRetryDelayMilliseconds { get; set; } = 2_000;

    /// <summary>
    /// 一轮采集彻底失败后首次重连的退避时间，单位为毫秒。
    /// </summary>
    public int ReconnectBaseDelayMilliseconds { get; set; } = 1_000;

    /// <summary>连续失败时重连退避的最大时间，单位为毫秒。</summary>
    public int MaxReconnectDelayMilliseconds { get; set; } = 30_000;
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
