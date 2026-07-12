using System.Net;
using Microsoft.Extensions.Logging;

namespace SonnetDB.Diagnostics;

/// <summary>
/// SonnetDB Server 的源生成结构化日志事件。
/// EventId 按 Write / Query / Flush / Compaction / Wal / Copilot / Auth / Http 分段治理。
/// </summary>
internal static partial class ServerLog
{
    // Write：1000~1999
    [LoggerMessage(EventId = 1001, EventName = "Write.ExternalMqttConfigurationInvalid", Level = LogLevel.Error,
        Message = "外部 MQTT client 配置无效：{Error}")]
    internal static partial void ExternalMqttConfigurationInvalid(this ILogger logger, string error);

    [LoggerMessage(EventId = 1002, EventName = "Write.ExternalMqttConnectionFailed", Level = LogLevel.Warning,
        Message = "外部 MQTT broker 连接或订阅失败：{Host}:{Port}")]
    internal static partial void ExternalMqttConnectionFailed(this ILogger logger, Exception exception, string host, int port);

    [LoggerMessage(EventId = 1003, EventName = "Write.ExternalMqttDisconnected", Level = LogLevel.Warning,
        Message = "外部 MQTT broker 连接断开：{Host}:{Port}，原因 {Reason}")]
    internal static partial void ExternalMqttDisconnected(this ILogger logger, string host, int port, int reason);

    [LoggerMessage(EventId = 1004, EventName = "Write.ExternalMqttConnectionRejected", Level = LogLevel.Warning,
        Message = "外部 MQTT broker 连接被拒绝：{Host}:{Port}，结果 {ResultCode}，原因 {ReasonString}")]
    internal static partial void ExternalMqttConnectionRejected(
        this ILogger logger,
        string host,
        int port,
        int resultCode,
        string? reasonString);

    [LoggerMessage(EventId = 1005, EventName = "Write.ExternalMqttNoSubscriptions", Level = LogLevel.Error,
        Message = "外部 MQTT client 没有成功订阅任何 topic filter")]
    internal static partial void ExternalMqttNoSubscriptions(this ILogger logger);

    [LoggerMessage(EventId = 1006, EventName = "Write.ExternalMqttConnected", Level = LogLevel.Information,
        Message = "外部 MQTT client 已连接 {Host}:{Port}，订阅 {Count} 个 topic filter")]
    internal static partial void ExternalMqttConnected(this ILogger logger, string host, int port, int count);

    [LoggerMessage(EventId = 1007, EventName = "Write.ExternalMqttSubscriptionRejected", Level = LogLevel.Warning,
        Message = "外部 MQTT broker 拒绝一个订阅项：{ResultCode}")]
    internal static partial void ExternalMqttSubscriptionRejected(this ILogger logger, int resultCode);

    [LoggerMessage(EventId = 1008, EventName = "Write.ExternalMqttMessageIgnored", Level = LogLevel.Debug,
        Message = "忽略外部 MQTT 消息：topic {Topic} 未匹配 measurement 路由，原因 {Error}")]
    internal static partial void ExternalMqttMessageIgnored(this ILogger logger, string topic, string error);

    [LoggerMessage(EventId = 1009, EventName = "Write.ExternalMqttIngestFailed", Level = LogLevel.Warning,
        Message = "外部 MQTT 消息落库失败：Topic={Topic}, ReasonCode={ReasonCode}, Reason={Reason}")]
    internal static partial void ExternalMqttIngestFailed(this ILogger logger, string topic, int reasonCode, string reason);

    [LoggerMessage(EventId = 1010, EventName = "Write.ExternalMqttIngested", Level = LogLevel.Debug,
        Message = "外部 MQTT 消息已落库：Topic={Topic}, Written={Written}, Skipped={Skipped}")]
    internal static partial void ExternalMqttIngested(this ILogger logger, string topic, int written, int skipped);

    [LoggerMessage(EventId = 1011, EventName = "Write.ExternalMqttMessageFailed", Level = LogLevel.Warning,
        Message = "处理外部 MQTT 消息失败：{Topic}")]
    internal static partial void ExternalMqttMessageFailed(this ILogger logger, Exception exception, string topic);

    [LoggerMessage(EventId = 1012, EventName = "Write.LineProtocolUdpStarted", Level = LogLevel.Information,
        Message = "Line Protocol UDP 监听已启用：0.0.0.0:{Port} -> database '{Database}'，无鉴权/无 ack，仅限可信内网")]
    internal static partial void LineProtocolUdpStarted(this ILogger logger, int port, string database);

    [LoggerMessage(EventId = 1013, EventName = "Write.LineProtocolUdpStopped", Level = LogLevel.Debug,
        Message = "Line Protocol UDP 监听停止")]
    internal static partial void LineProtocolUdpStopped(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1014, EventName = "Write.LineProtocolUdpOversized", Level = LogLevel.Warning,
        Message = "丢弃超过限制的 Line Protocol UDP 数据报：Remote={Remote}, Bytes={Bytes}, Limit={Limit}")]
    internal static partial void LineProtocolUdpOversized(this ILogger logger, IPEndPoint remote, int bytes, int limit);

    [LoggerMessage(EventId = 1015, EventName = "Write.LineProtocolUdpDatabaseMissing", Level = LogLevel.Warning,
        Message = "丢弃 Line Protocol UDP 数据报：目标数据库 '{Database}' 不存在。Remote={Remote}")]
    internal static partial void LineProtocolUdpDatabaseMissing(this ILogger logger, string database, IPEndPoint remote);

    [LoggerMessage(EventId = 1016, EventName = "Write.LineProtocolUdpIngested", Level = LogLevel.Debug,
        Message = "Line Protocol UDP 数据报已落库：Remote={Remote}, Written={Written}, Skipped={Skipped}")]
    internal static partial void LineProtocolUdpIngested(this ILogger logger, IPEndPoint remote, int written, int skipped);

    [LoggerMessage(EventId = 1017, EventName = "Write.LineProtocolUdpIngestFailed", Level = LogLevel.Warning,
        Message = "Line Protocol UDP 数据报解析或写入失败：Remote={Remote}, Bytes={Bytes}")]
    internal static partial void LineProtocolUdpIngestFailed(this ILogger logger, Exception exception, IPEndPoint remote, int bytes);

    [LoggerMessage(EventId = 1018, EventName = "Write.LineProtocolUdpInvalidUtf8", Level = LogLevel.Warning,
        Message = "Line Protocol UDP 数据报不是有效 UTF-8：Remote={Remote}, Bytes={Bytes}")]
    internal static partial void LineProtocolUdpInvalidUtf8(this ILogger logger, Exception exception, IPEndPoint remote, int bytes);

    [LoggerMessage(EventId = 1019, EventName = "Write.LineProtocolUdpInvalidArguments", Level = LogLevel.Warning,
        Message = "Line Protocol UDP 数据报写入参数无效：Remote={Remote}, Bytes={Bytes}")]
    internal static partial void LineProtocolUdpInvalidArguments(this ILogger logger, Exception exception, IPEndPoint remote, int bytes);

    [LoggerMessage(EventId = 1020, EventName = "Write.LineProtocolUdpFailed", Level = LogLevel.Error,
        Message = "Line Protocol UDP 数据报处理失败：Remote={Remote}, Bytes={Bytes}")]
    internal static partial void LineProtocolUdpFailed(this ILogger logger, Exception exception, IPEndPoint remote, int bytes);

    [LoggerMessage(EventId = 1021, EventName = "Write.CoapIngestFailed", Level = LogLevel.Error,
        Message = "CoAP 写入处理失败：{Source}")]
    internal static partial void CoapIngestFailed(this ILogger logger, Exception exception, string source);

    [LoggerMessage(EventId = 1022, EventName = "Write.CoapObservePumpFailed", Level = LogLevel.Warning,
        Message = "CoAP Observe 推送桥异常终止：{Topic}")]
    internal static partial void CoapObservePumpFailed(this ILogger logger, Exception exception, string topic);

    [LoggerMessage(EventId = 1023, EventName = "Write.MqttMqPumpFailed", Level = LogLevel.Warning,
        Message = "MQTT SonnetMQ 推送桥异常终止：{Topic}")]
    internal static partial void MqttMqPumpFailed(this ILogger logger, Exception exception, string topic);

    [LoggerMessage(EventId = 1024, EventName = "Write.SparkplugIngested", Level = LogLevel.Debug,
        Message = "Sparkplug 消息已落库：Topic={Topic}, Written={Written}, Skipped={Skipped}, Orphan={Orphan}, Unsupported={Unsupported}")]
    internal static partial void SparkplugIngested(
        this ILogger logger,
        string topic,
        int written,
        int skipped,
        int orphan,
        int unsupported);

    [LoggerMessage(EventId = 1025, EventName = "Write.SparkplugIngestFailed", Level = LogLevel.Warning,
        Message = "Sparkplug 消息解析或落库失败：Topic={Topic}, Reason={Reason}")]
    internal static partial void SparkplugIngestFailed(this ILogger logger, string topic, string reason);

    // Copilot：6000~6999
    [LoggerMessage(EventId = 6001, EventName = "Copilot.PlannerInvalidResponse", Level = LogLevel.Warning,
        Message = "Copilot planner returned non-JSON content: {Response}")]
    internal static partial void CopilotPlannerInvalidResponse(this ILogger logger, string response);

    [LoggerMessage(EventId = 6002, EventName = "Copilot.PlannerFailed", Level = LogLevel.Warning,
        Message = "Copilot planner failed, falling back to heuristics")]
    internal static partial void CopilotPlannerFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6003, EventName = "Copilot.AnswerGenerationFailed", Level = LogLevel.Warning,
        Message = "Copilot final answer generation failed, using deterministic fallback")]
    internal static partial void CopilotAnswerGenerationFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6004, EventName = "Copilot.SqlRepairFailed", Level = LogLevel.Warning,
        Message = "Copilot SQL repair failed for sql={Sql}")]
    internal static partial void CopilotSqlRepairFailed(this ILogger logger, Exception exception, string? sql);

    [LoggerMessage(EventId = 6005, EventName = "Copilot.DocsIngestSkipped", Level = LogLevel.Information,
        Message = "Copilot docs auto-ingest skipped: embedding provider not ready (reason={Reason})")]
    internal static partial void CopilotDocsIngestSkipped(this ILogger logger, string? reason);

    [LoggerMessage(EventId = 6006, EventName = "Copilot.DocsIngestCompleted", Level = LogLevel.Information,
        Message = "Copilot docs auto-ingest completed: scanned={Scanned}, indexed={Indexed}, skipped={Skipped}, deleted={Deleted}, chunks={Chunks}")]
    internal static partial void CopilotDocsIngestCompleted(
        this ILogger logger,
        int scanned,
        int indexed,
        int skipped,
        int deleted,
        int chunks);

    [LoggerMessage(EventId = 6007, EventName = "Copilot.DocsIngestFailed", Level = LogLevel.Warning,
        Message = "Copilot docs auto-ingest failed; continuing startup")]
    internal static partial void CopilotDocsIngestFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6008, EventName = "Copilot.SkillsIngestSkipped", Level = LogLevel.Information,
        Message = "Copilot skills auto-ingest skipped: embedding provider not ready (reason={Reason})")]
    internal static partial void CopilotSkillsIngestSkipped(this ILogger logger, string? reason);

    [LoggerMessage(EventId = 6009, EventName = "Copilot.SkillsIngestCompleted", Level = LogLevel.Information,
        Message = "Copilot skills auto-ingest completed: scanned={Scanned}, indexed={Indexed}, skipped={Skipped}, deleted={Deleted}")]
    internal static partial void CopilotSkillsIngestCompleted(this ILogger logger, int scanned, int indexed, int skipped, int deleted);

    [LoggerMessage(EventId = 6010, EventName = "Copilot.SkillsIngestFailed", Level = LogLevel.Warning,
        Message = "Copilot skills auto-ingest failed; continuing startup")]
    internal static partial void CopilotSkillsIngestFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6011, EventName = "Copilot.LegacyDocsSchema", Level = LogLevel.Warning,
        Message = "Legacy docs schema detected in {Measurement}: section/title are TAG columns. Reserved characters will be normalized for compatibility during ingest")]
    internal static partial void CopilotLegacyDocsSchema(this ILogger logger, string measurement);

    [LoggerMessage(EventId = 6012, EventName = "Copilot.DocsSourceIndexed", Level = LogLevel.Information,
        Message = "Indexed {ChunkCount} chunks for docs source {Source}")]
    internal static partial void CopilotDocsSourceIndexed(this ILogger logger, int chunkCount, string source);

    [LoggerMessage(EventId = 6013, EventName = "Copilot.SkillIndexed", Level = LogLevel.Information,
        Message = "Indexed skill {Skill} ({Path})")]
    internal static partial void CopilotSkillIndexed(this ILogger logger, string skill, string path);

    // Auth：7000~7999
    [LoggerMessage(EventId = 7001, EventName = "Auth.EnvironmentBootstrapCompleted", Level = LogLevel.Information,
        Message = "环境变量引导完成：已创建超级用户 '{User}'，DefaultDatabase={DefaultDatabase}")]
    internal static partial void EnvironmentBootstrapCompleted(this ILogger logger, string user, string? defaultDatabase);

    [LoggerMessage(EventId = 7002, EventName = "Auth.EnvironmentBootstrapFailed", Level = LogLevel.Warning,
        Message = "环境变量引导未完成（可能已初始化或参数非法）：{ErrorMessage}")]
    internal static partial void EnvironmentBootstrapFailed(this ILogger logger, Exception exception, string errorMessage);
}
