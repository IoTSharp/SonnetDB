using System.Buffers;
using Microsoft.Extensions.Options;
using MQTTnet.AspNetCore.Routing;
using MQTTnet.AspNetCore.Routing.Attributes;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Hosting;

namespace SonnetDB.Mqtt;

/// <summary>
/// 通过 source-generated route 处理 Sparkplug B 节点和设备消息。
/// </summary>
[MqttController]
[MqttGeneratedController]
[MqttRoute("spBv1.0/{groupId}")]
internal sealed class SparkplugMqttController : MqttBaseController
{
    private readonly TsdbRegistry _registry;
    private readonly GrantsStore _grants;
    private readonly SparkplugOptions _options;
    private readonly SparkplugIngestor _ingestor;
    private readonly SparkplugHostApplicationService _hostApplication;

    public SparkplugMqttController(
        TsdbRegistry registry,
        GrantsStore grants,
        IOptions<ServerOptions> options,
        SparkplugIngestor ingestor,
        SparkplugHostApplicationService hostApplication)
    {
        _registry = registry;
        _grants = grants;
        _options = options.Value.Mqtt.Sparkplug;
        _ingestor = ingestor;
        _hostApplication = hostApplication;
    }

    /// <summary>
    /// 处理 NBIRTH 和 NDATA 节点级消息。
    /// </summary>
    [MqttRoute("{messageType}/{edgeNodeId}")]
    public MqttResult PublishNode(
        [FromMqttRoute] string groupId,
        [FromMqttRoute] string messageType,
        [FromMqttRoute] string edgeNodeId)
        => HandlePublish(groupId, messageType, edgeNodeId, deviceId: null);

    /// <summary>
    /// 处理 DBIRTH 和 DDATA 设备级消息。
    /// </summary>
    [MqttRoute("{messageType}/{edgeNodeId}/{deviceId}")]
    public MqttResult PublishDevice(
        [FromMqttRoute] string groupId,
        [FromMqttRoute] string messageType,
        [FromMqttRoute] string edgeNodeId,
        [FromMqttRoute] string deviceId)
        => HandlePublish(groupId, messageType, edgeNodeId, deviceId);

    private MqttResult HandlePublish(
        string groupId,
        string messageType,
        string edgeNodeId,
        string? deviceId)
    {
        if (Message.QualityOfServiceLevel == MqttQualityOfServiceLevel.ExactlyOnce)
            return Reject(MqttPubAckReasonCode.ImplementationSpecificError, "SonnetDB MQTT broker 当前仅支持 QoS 0/1。");

        if (!_options.Enabled)
            return Reject(MqttPubAckReasonCode.ImplementationSpecificError, "Sparkplug B 接入未启用。");

        if (!SparkplugTopicParser.TryParse(Message.Topic, out var route, out string routeError)
            || !string.Equals(route.GroupId, groupId, StringComparison.Ordinal)
            || !string.Equals(route.EdgeNodeId, edgeNodeId, StringComparison.Ordinal)
            || !string.Equals(route.DeviceId, deviceId, StringComparison.Ordinal)
            || !string.Equals(ToWireName(route.MessageType), messageType, StringComparison.Ordinal))
        {
            return Reject(MqttPubAckReasonCode.TopicNameInvalid, routeError);
        }

        if (!TsdbRegistry.IsValidName(_options.Database))
            return Reject(MqttPubAckReasonCode.ImplementationSpecificError, "SonnetDBServer:Mqtt:Sparkplug:Database 配置无效。");

        if (!_registry.TryGet(_options.Database, out _))
            return Reject(MqttPubAckReasonCode.ImplementationSpecificError, $"Sparkplug 目标数据库 '{_options.Database}' 不存在。");

        bool internalPublish = IsInternalPublish();
        MqttClientPrincipal? principal = null;
        if (!internalPublish)
        {
            if (!MqttContext.SessionItems.Contains(SonnetMqttBrokerBridge.PrincipalSessionKey)
                || MqttContext.SessionItems[SonnetMqttBrokerBridge.PrincipalSessionKey] is not MqttClientPrincipal authenticated)
            {
                return Reject(MqttPubAckReasonCode.NotAuthorized, "MQTT 会话未通过 SonnetDB 鉴权。");
            }

            principal = authenticated;
        }

        if (!internalPublish
            && !principal!.HasPermission(_grants, _options.Database, DatabasePermission.Write))
        {
            return Reject(
                MqttPubAckReasonCode.NotAuthorized,
                $"当前 MQTT 凭据对数据库 '{_options.Database}' 没有 write 权限。");
        }

        if (route.IsCommand)
            return HandleCommand(route, principal, internalPublish);

        if (!_ingestor.TryIngest(
                route,
                Message.Payload.ToArray(),
                Message.Topic,
                out _,
                out bool requiresRebirth,
                out var reasonCode,
                out string reason))
        {
            return Reject(reasonCode, reason);
        }

        if (requiresRebirth)
            _hostApplication.RequestRebirth(route.GroupId, route.EdgeNodeId);

        return Acknowledge();
    }

    private MqttResult HandleCommand(
        in SparkplugTopicRoute route,
        MqttClientPrincipal? principal,
        bool internalPublish)
    {
        if (!internalPublish)
        {
            if (!_options.AllowCommands)
            {
                return Reject(
                    MqttPubAckReasonCode.NotAuthorized,
                    "Sparkplug NCMD/DCMD 默认关闭；需显式启用 SonnetDBServer:Mqtt:Sparkplug:AllowCommands。");
            }

            if (principal is null
                || principal.GetEffectivePermission(_grants, _options.Database) < DatabasePermission.Admin)
            {
                return Reject(MqttPubAckReasonCode.NotAuthorized, "Sparkplug 下行命令仅允许数据库管理员发布。");
            }

            if (!HasExplicitApproval())
            {
                return Reject(
                    MqttPubAckReasonCode.NotAuthorized,
                    "Sparkplug 下行命令缺少 MQTT v5 user property: sndb-approved=true。");
            }
        }

        if (!_ingestor.TryValidateCommand(
                route,
                Message.Payload.ToArray(),
                out MqttPubAckReasonCode reasonCode,
                out string reason))
        {
            return Reject(reasonCode, reason);
        }

        return Acknowledge();
    }

    private bool IsInternalPublish()
        => MqttContext.SessionItems.Contains(SonnetMqttBrokerBridge.InternalPublishSessionKey)
            && MqttContext.SessionItems[SonnetMqttBrokerBridge.InternalPublishSessionKey] is true;

    private bool HasExplicitApproval()
        => Message.UserProperties is { Count: > 0 } properties
            && properties.Any(static property =>
                string.Equals(property.Name, "sndb-approved", StringComparison.OrdinalIgnoreCase)
                && string.Equals(property.ReadValueAsString(), "true", StringComparison.OrdinalIgnoreCase));

    private static string ToWireName(SparkplugMessageType messageType)
        => messageType switch
        {
            SparkplugMessageType.NBirth => "NBIRTH",
            SparkplugMessageType.DBirth => "DBIRTH",
            SparkplugMessageType.NData => "NDATA",
            SparkplugMessageType.DData => "DDATA",
            SparkplugMessageType.NDeath => "NDEATH",
            SparkplugMessageType.DDeath => "DDEATH",
            SparkplugMessageType.NCommand => "NCMD",
            SparkplugMessageType.DCommand => "DCMD",
            _ => string.Empty,
        };
}
