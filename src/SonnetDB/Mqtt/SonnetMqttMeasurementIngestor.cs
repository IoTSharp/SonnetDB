using MQTTnet.Packets;
using MQTTnet.Protocol;
using SonnetDB.Endpoints;
using SonnetDB.Hosting;
using SonnetDB.Ingest;

namespace SonnetDB.Mqtt;

/// <summary>
/// MQTT measurement topic 的共享落库服务，供内建 broker 与外部 broker client 共用。
/// </summary>
public sealed class SonnetMqttMeasurementIngestor
{
    internal const string FormatProperty = "sndb-format";

    private readonly TsdbRegistry _registry;
    private readonly ServerMetrics _metrics;

    /// <summary>
    /// 创建 MQTT measurement 落库服务。
    /// </summary>
    public SonnetMqttMeasurementIngestor(TsdbRegistry registry, ServerMetrics metrics)
    {
        _registry = registry;
        _metrics = metrics;
    }

    /// <summary>
    /// 将已解析为 measurement topic 的 MQTT payload 写入目标数据库。
    /// </summary>
    /// <param name="route">已解析的 MQTT topic 路由。</param>
    /// <param name="payload">MQTT payload 原始字节。</param>
    /// <param name="contentType">MQTT v5 ContentType，可能为空。</param>
    /// <param name="userProperties">MQTT v5 user properties，可能为空。</param>
    /// <param name="result">成功写入时的批量入库结果。</param>
    /// <param name="reasonCode">失败时建议返回给 MQTT PUBLISH 的原因码。</param>
    /// <param name="reason">失败原因。</param>
    /// <returns>成功写入返回 <c>true</c>；topic 或 payload 不合法返回 <c>false</c>。</returns>
    internal bool TryIngestMeasurement(
        in MqttTopicRoute route,
        ReadOnlyMemory<byte> payload,
        string? contentType,
        IReadOnlyList<MqttUserProperty>? userProperties,
        out BulkIngestResult result,
        out MqttPubAckReasonCode reasonCode,
        out string reason)
    {
        result = default;
        reasonCode = MqttPubAckReasonCode.Success;
        reason = string.Empty;

        if (route.Kind != MqttTopicKind.Measurement)
        {
            reasonCode = MqttPubAckReasonCode.TopicNameInvalid;
            reason = "topic 需匹配 db/{db}/m/{measurement}。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(route.Resource) || route.Resource.Length > 255)
        {
            reasonCode = MqttPubAckReasonCode.TopicNameInvalid;
            reason = $"非法 measurement 名 '{route.Resource}'。";
            return false;
        }

        if (!_registry.TryGet(route.Db, out var tsdb))
        {
            reasonCode = MqttPubAckReasonCode.TopicNameInvalid;
            reason = $"数据库 '{route.Db}' 不存在。";
            return false;
        }

        try
        {
            var format = ResolveFormat(payload.Span, contentType, userProperties);
            result = BulkIngestEndpointHandler.IngestPayload(
                tsdb,
                route.Resource,
                format,
                payload,
                BulkErrorPolicy.FailFast,
                BulkFlushMode.None);
            _metrics.AddInsertedRows(result.Written);
            return true;
        }
        catch (BulkIngestException ex)
        {
            reasonCode = MqttPubAckReasonCode.PayloadFormatInvalid;
            reason = ex.Message;
            return false;
        }
        catch (ArgumentException ex)
        {
            reasonCode = MqttPubAckReasonCode.PayloadFormatInvalid;
            reason = ex.Message;
            return false;
        }
    }

    private static BulkIngestEndpointHandler.Format ResolveFormat(
        ReadOnlySpan<byte> payload,
        string? contentType,
        IReadOnlyList<MqttUserProperty>? userProperties)
    {
        string? explicitFormat = FindUserProperty(userProperties, FormatProperty);
        if (string.IsNullOrWhiteSpace(explicitFormat))
            explicitFormat = contentType;

        if (!string.IsNullOrWhiteSpace(explicitFormat))
        {
            if (explicitFormat.Contains("json", StringComparison.OrdinalIgnoreCase))
                return BulkIngestEndpointHandler.Format.Json;
            if (explicitFormat.Contains("bulk", StringComparison.OrdinalIgnoreCase)
                || explicitFormat.Contains("values", StringComparison.OrdinalIgnoreCase))
                return BulkIngestEndpointHandler.Format.BulkValues;
            if (explicitFormat.Contains("line", StringComparison.OrdinalIgnoreCase)
                || explicitFormat.Contains("lp", StringComparison.OrdinalIgnoreCase)
                || explicitFormat.Contains("text/plain", StringComparison.OrdinalIgnoreCase))
                return BulkIngestEndpointHandler.Format.LineProtocol;
        }

        foreach (byte b in payload)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                continue;
            return b is (byte)'{' or (byte)'['
                ? BulkIngestEndpointHandler.Format.Json
                : BulkIngestEndpointHandler.Format.LineProtocol;
        }

        return BulkIngestEndpointHandler.Format.LineProtocol;
    }

    private static string? FindUserProperty(IReadOnlyList<MqttUserProperty>? properties, string name)
    {
        if (properties is null)
            return null;

        foreach (var property in properties)
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.ReadValueAsString();
        }

        return null;
    }
}
