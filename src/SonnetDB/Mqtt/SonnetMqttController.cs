using System.Buffers;
using MQTTnet.AspNetCore.Routing;
using MQTTnet.AspNetCore.Routing.Attributes;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using SonnetDB.Auth;
using SonnetDB.Endpoints;
using SonnetDB.Hosting;
using SonnetMQ;

namespace SonnetDB.Mqtt;

/// <summary>
/// SonnetDB 内建 MQTT broker 的受管 topic 路由控制器。
/// </summary>
[MqttController]
[MqttRoute("db/{db}")]
public sealed class SonnetMqttController : MqttBaseController
{
    private readonly TsdbRegistry _registry;
    private readonly GrantsStore _grants;
    private readonly SonnetMqStore _mq;
    private readonly SonnetMqttMeasurementIngestor _measurementIngestor;

    /// <summary>
    /// 创建 MQTT 路由控制器。
    /// </summary>
    public SonnetMqttController(
        TsdbRegistry registry,
        GrantsStore grants,
        SonnetMqStore mq,
        SonnetMqttMeasurementIngestor measurementIngestor)
    {
        _registry = registry;
        _grants = grants;
        _mq = mq;
        _measurementIngestor = measurementIngestor;
    }

    /// <summary>
    /// 处理 <c>db/{db}/m/{measurement}</c> 的设备遥测发布。
    /// </summary>
    /// <param name="db">数据库名称。</param>
    /// <param name="measurement">measurement 名称。</param>
    [MqttRoute("m/{measurement}")]
    public MqttResult PublishMeasurement(
        [FromMqttRoute] string db,
        [FromMqttRoute] string measurement)
    {
        if (ValidatePublishEnvelope() is { } envelopeError)
            return envelopeError;
        if (TryAuthorize(db, DatabasePermission.Write) is { } authError)
            return authError;

        var route = new MqttTopicRoute(MqttTopicKind.Measurement, db, measurement);
        if (!_measurementIngestor.TryIngestMeasurement(
                route,
                Message.Payload.ToArray(),
                Message.ContentType,
                Message.UserProperties,
                out _,
                out var reasonCode,
                out string reason))
        {
            return Reject(reasonCode, reason);
        }

        return Acknowledge();
    }

    /// <summary>
    /// 处理 <c>db/{db}/mq/{topic}</c> 的设备消息发布。
    /// </summary>
    /// <param name="db">数据库名称。</param>
    /// <param name="topic">SonnetMQ topic 名称。</param>
    [MqttRoute("mq/{topic}")]
    public MqttResult PublishMq(
        [FromMqttRoute] string db,
        [FromMqttRoute] string topic)
    {
        if (ValidatePublishEnvelope() is { } envelopeError)
            return envelopeError;
        if (IsInternalBridgePublish())
        {
            return Acknowledge();
        }

        if (TryAuthorize(db, DatabasePermission.Write) is { } authError)
            return authError;
        if (!MqttTopicParser.TryParse(Message.Topic, out var route, out string error) || route.Kind != MqttTopicKind.Mq)
            return Reject(MqttPubAckReasonCode.TopicNameInvalid, error);

        try
        {
            byte[] payload = Message.Payload.ToArray();
            IReadOnlyDictionary<string, string>? headers = ExtractMqHeaders();
            _mq.Publish(
                SonnetDbEndpoints.QualifyMqTopic(db, topic),
                payload,
                headers is null ? null : new SonnetMqPublishOptions(headers));

            // PUBLISH 已写入 SonnetMQ；后续由 WaitForMessagesAsync pump 注入到 broker，避免本次
            // 原生 fan-out 与持久队列推送重复。
            return Suppress();
        }
        catch (ArgumentException ex)
        {
            return Reject(MqttPubAckReasonCode.TopicNameInvalid, ex.Message);
        }
        catch (IOException ex)
        {
            return Reject(MqttPubAckReasonCode.ImplementationSpecificError, ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return Reject(MqttPubAckReasonCode.ImplementationSpecificError, ex.Message);
        }
    }

    /// <summary>
    /// 捕获未匹配的 MQTT topic，并以明确的 MQTT v5 reason code 拒绝。
    /// </summary>
    /// <param name="path">未匹配 topic 的完整路径。</param>
    [MqttRoute("/{*path}")]
    public MqttResult RejectUnmatched([FromMqttRoute] string path)
        => Reject(
            MqttPubAckReasonCode.TopicNameInvalid,
            "topic 需匹配 db/{db}/m/{measurement} 或 db/{db}/mq/{topic}。");

    private MqttResult? ValidatePublishEnvelope()
    {
        if (Message.QualityOfServiceLevel != MqttQualityOfServiceLevel.ExactlyOnce)
            return null;

        return Reject(MqttPubAckReasonCode.ImplementationSpecificError, "SonnetDB MQTT broker 当前仅支持 QoS 0/1。");
    }

    private MqttResult? TryAuthorize(string db, DatabasePermission required)
    {
        if (!TsdbRegistry.IsValidName(db))
        {
            return Reject(MqttPubAckReasonCode.TopicNameInvalid, $"非法数据库名 '{db}'。");
        }

        if (!MqttContext.SessionItems.Contains(SonnetMqttBrokerBridge.PrincipalSessionKey)
            || MqttContext.SessionItems[SonnetMqttBrokerBridge.PrincipalSessionKey] is not MqttClientPrincipal principal)
        {
            return Reject(MqttPubAckReasonCode.NotAuthorized, "MQTT 会话未通过 SonnetDB 鉴权。");
        }

        if (!_registry.TryGet(db, out _))
        {
            return Reject(MqttPubAckReasonCode.TopicNameInvalid, $"数据库 '{db}' 不存在。");
        }

        if (!principal.HasPermission(_grants, db, required))
        {
            return Reject(
                MqttPubAckReasonCode.NotAuthorized,
                $"当前 MQTT 凭据对数据库 '{db}' 没有 {required.ToString().ToLowerInvariant()} 权限。");
        }

        return null;
    }

    private bool IsInternalBridgePublish()
        => MqttContext.SessionItems.Contains(SonnetMqttBrokerBridge.InternalPublishSessionKey)
            && MqttContext.SessionItems[SonnetMqttBrokerBridge.InternalPublishSessionKey] is true;

    private IReadOnlyDictionary<string, string>? ExtractMqHeaders()
    {
        Dictionary<string, string>? headers = null;
        AddHeader(ref headers, "mqttClientId", ClientId);
        AddHeader(ref headers, "contentType", Message.ContentType);

        if (Message.UserProperties is { Count: > 0 } properties)
        {
            foreach (var property in properties)
            {
                if (string.IsNullOrWhiteSpace(property.Name)
                    || string.Equals(property.Name, SonnetMqttMeasurementIngestor.FormatProperty, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!IsHeaderName(property.Name))
                    continue;
                AddHeader(ref headers, property.Name, ReadUserPropertyValue(property));
            }
        }

        return headers;
    }

    private static string ReadUserPropertyValue(MqttUserProperty property)
        => property.ReadValueAsString();

    private static void AddHeader(ref Dictionary<string, string>? headers, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !IsHeaderName(name))
            return;

        headers ??= new Dictionary<string, string>(StringComparer.Ordinal);
        headers[name] = value;
    }

    private static bool IsHeaderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        for (int i = 0; i < name.Length; i++)
        {
            char ch = name[i];
            bool valid =
                ch is >= 'a' and <= 'z' ||
                ch is >= 'A' and <= 'Z' ||
                ch is >= '0' and <= '9' ||
                ch is '_' or '-' or '.';
            if (!valid)
                return false;
        }

        return true;
    }
}
