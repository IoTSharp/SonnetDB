using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Diagnostics;
using SonnetDB.Hosting;
using SonnetMQ;

namespace SonnetDB.Mqtt;

/// <summary>
/// 内建 MQTT broker 与 SonnetDB 控制面之间的桥接器。
/// </summary>
internal sealed class SonnetMqttBrokerBridge
{
    internal const string PrincipalSessionKey = "sndb.mqtt.principal";
    internal const string InternalPublishSessionKey = "sndb.mqtt.internal-publish";
    private const int PumpBatchMax = 100;

    private readonly TsdbRegistry _registry;
    private readonly GrantsStore _grants;
    private readonly UserStore _users;
    private readonly SonnetMqStore _mq;
    private readonly ServerOptions _options;
    private readonly ILogger<SonnetMqttBrokerBridge> _logger;
    private readonly ConcurrentDictionary<string, MqttClientPrincipal> _principals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _clientMqSubscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TopicPump> _topicPumps = new(StringComparer.Ordinal);
    private int _configured;
    private MqttServer? _server;

    public SonnetMqttBrokerBridge(
        TsdbRegistry registry,
        GrantsStore grants,
        UserStore users,
        SonnetMqStore mq,
        IOptions<ServerOptions> options,
        ILogger<SonnetMqttBrokerBridge> logger)
    {
        _registry = registry;
        _grants = grants;
        _users = users;
        _mq = mq;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 将 MQTTnet server 事件接入 SonnetDB 鉴权、路由与 SonnetMQ 推送。
    /// </summary>
    public void Configure(MqttServer server)
    {
        if (Interlocked.Exchange(ref _configured, 1) == 1)
            return;

        _server = server;
        server.ValidatingConnectionAsync += ValidateConnectionAsync;
        server.InterceptingSubscriptionAsync += InterceptSubscriptionAsync;
        server.InterceptingClientEnqueueAsync += InterceptClientEnqueueAsync;
        server.ClientSubscribedTopicAsync += ClientSubscribedTopicAsync;
        server.ClientUnsubscribedTopicAsync += ClientUnsubscribedTopicAsync;
        server.ClientDisconnectedAsync += ClientDisconnectedAsync;
    }

    private Task ValidateConnectionAsync(ValidatingConnectionEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.ClientId))
            args.AssignedClientIdentifier = "sndb-" + Guid.NewGuid().ToString("N");

        if (!TryAuthenticate(args.UserName, args.Password, out var principal))
        {
            args.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            args.ReasonString = "缺失或无效的 SonnetDB MQTT 凭据。";
            return Task.CompletedTask;
        }

        args.ReasonCode = MqttConnectReasonCode.Success;
        args.SessionItems[PrincipalSessionKey] = principal;
        string clientId = string.IsNullOrWhiteSpace(args.ClientId) ? args.AssignedClientIdentifier : args.ClientId;
        if (!string.IsNullOrWhiteSpace(clientId))
            _principals[clientId] = principal;
        return Task.CompletedTask;
    }

    private Task InterceptSubscriptionAsync(InterceptingSubscriptionEventArgs args)
    {
        if (args.TopicFilter.QualityOfServiceLevel == MqttQualityOfServiceLevel.ExactlyOnce)
        {
            RejectSubscription(args, MqttSubscribeReasonCode.ImplementationSpecificError, "SonnetDB MQTT broker 当前仅支持 QoS 0/1。");
            return Task.CompletedTask;
        }

        if (!TryGetPrincipal(args.ClientId, args.SessionItems, out var principal))
        {
            RejectSubscription(args, MqttSubscribeReasonCode.NotAuthorized, "MQTT 会话未通过 SonnetDB 鉴权。");
            return Task.CompletedTask;
        }

        if (!MqttTopicParser.TryParse(args.TopicFilter.Topic, out var route, out string error))
        {
            var code = args.TopicFilter.Topic.Contains('+', StringComparison.Ordinal) || args.TopicFilter.Topic.Contains('#', StringComparison.Ordinal)
                ? MqttSubscribeReasonCode.WildcardSubscriptionsNotSupported
                : MqttSubscribeReasonCode.TopicFilterInvalid;
            RejectSubscription(args, code, error);
            return Task.CompletedTask;
        }

        if (!Authorize(route, principal, DatabasePermission.Read, out string authError))
        {
            RejectSubscription(args, MqttSubscribeReasonCode.NotAuthorized, authError);
            return Task.CompletedTask;
        }

        if (route.Kind == MqttTopicKind.Mq
            && !TryAddMqSubscription(args.ClientId, args.TopicFilter.Topic, route, out string limitError))
        {
            RejectSubscription(args, MqttSubscribeReasonCode.ImplementationSpecificError, limitError);
            return Task.CompletedTask;
        }

        args.ProcessSubscription = true;
        args.Response.ReasonCode = args.TopicFilter.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce
            ? MqttSubscribeReasonCode.GrantedQoS1
            : MqttSubscribeReasonCode.GrantedQoS0;
        return Task.CompletedTask;
    }

    private async Task InterceptClientEnqueueAsync(InterceptingClientApplicationMessageEnqueueEventArgs args)
    {
        if (!MqttTopicParser.TryParse(args.ApplicationMessage.Topic, out var route, out _))
            return;

        var principal = await TryGetPrincipalForReceiverAsync(args.ReceiverClientId).ConfigureAwait(false);
        if (principal is null || !Authorize(route, principal, DatabasePermission.Read, out _))
        {
            args.AcceptEnqueue = false;
        }
    }

    private Task ClientSubscribedTopicAsync(ClientSubscribedTopicEventArgs args)
    {
        if (!MqttTopicParser.TryParse(args.TopicFilter.Topic, out var route, out _)
            || route.Kind != MqttTopicKind.Mq)
            return Task.CompletedTask;

        _ = TryAddMqSubscription(args.ClientId, args.TopicFilter.Topic, route, out _);
        return Task.CompletedTask;
    }

    private Task ClientUnsubscribedTopicAsync(ClientUnsubscribedTopicEventArgs args)
    {
        RemoveClientSubscription(args.ClientId, args.TopicFilter);
        return Task.CompletedTask;
    }

    private Task ClientDisconnectedAsync(ClientDisconnectedEventArgs args)
    {
        _principals.TryRemove(args.ClientId, out _);
        if (_clientMqSubscriptions.TryRemove(args.ClientId, out var topics))
        {
            foreach (string topic in topics.Keys)
                DecrementPump(topic);
        }

        return Task.CompletedTask;
    }

    private bool TryAuthenticate(string? userName, string? password, out MqttClientPrincipal principal)
    {
        principal = null!;

        if (!string.IsNullOrWhiteSpace(userName)
            && !string.IsNullOrWhiteSpace(password)
            && _users.VerifyPassword(userName, password))
        {
            principal = MqttClientPrincipal.ForUser(new AuthenticatedUser(userName.ToLowerInvariant(), _users.IsSuperuser(userName)));
            return true;
        }

        if (TryAuthenticateToken(password, out principal))
            return true;

        return TryAuthenticateToken(userName, out principal);
    }

    private bool TryAuthenticateToken(string? token, out MqttClientPrincipal principal)
    {
        principal = null!;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (_options.Tokens.TryGetValue(token, out string? role))
        {
            principal = MqttClientPrincipal.ForRole(role);
            return true;
        }

        if (_users.TryAuthenticate(token, out var user))
        {
            principal = MqttClientPrincipal.ForUser(user);
            return true;
        }

        return false;
    }

    private bool TryGetPrincipal(string clientId, System.Collections.IDictionary sessionItems, out MqttClientPrincipal principal)
    {
        if (sessionItems[PrincipalSessionKey] is MqttClientPrincipal fromSession)
        {
            principal = fromSession;
            return true;
        }

        return _principals.TryGetValue(clientId, out principal!);
    }

    private async Task<MqttClientPrincipal?> TryGetPrincipalForReceiverAsync(string clientId)
    {
        if (_principals.TryGetValue(clientId, out var principal))
            return principal;

        if (_server is null)
            return null;

        var session = await _server.GetSessionAsync(clientId).ConfigureAwait(false);
        if (session.Items[PrincipalSessionKey] is not MqttClientPrincipal fromSession)
            return null;

        _principals[clientId] = fromSession;
        return fromSession;
    }

    private bool Authorize(in MqttTopicRoute route, MqttClientPrincipal principal, DatabasePermission required, out string error)
    {
        error = string.Empty;
        if (!_registry.TryGet(route.Db, out _))
        {
            error = $"数据库 '{route.Db}' 不存在。";
            return false;
        }

        if (!principal.HasPermission(_grants, route.Db, required))
        {
            error = $"当前 MQTT 凭据对数据库 '{route.Db}' 没有 {required.ToString().ToLowerInvariant()} 权限。";
            return false;
        }

        return true;
    }

    private bool TryAddMqSubscription(string clientId, string mqttTopic, in MqttTopicRoute route, out string error)
    {
        error = string.Empty;
        var topics = _clientMqSubscriptions.GetOrAdd(
            clientId,
            _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        if (topics.ContainsKey(mqttTopic))
            return true;

        int max = Math.Max(0, _options.Mqtt.MaxMqSubscriptionsPerClient);
        if (max == 0)
        {
            error = "当前配置不允许 MQTT 客户端桥接 SonnetMQ topic。";
            return false;
        }

        if (topics.Count >= max)
        {
            error = $"当前 MQTT 客户端最多可订阅 {max} 个 SonnetMQ topic。";
            return false;
        }

        if (topics.TryAdd(mqttTopic, 0))
            IncrementPump(mqttTopic, route);
        return true;
    }

    private void IncrementPump(string mqttTopic, in MqttTopicRoute route)
    {
        string db = route.Db;
        string resource = route.Resource;
        _topicPumps.AddOrUpdate(
            mqttTopic,
            key => StartPump(key, db, resource),
            (_, existing) =>
            {
                existing.Increment();
                return existing;
            });
    }

    private TopicPump StartPump(string mqttTopic, string db, string resource)
    {
        var pump = new TopicPump(SonnetDB.Endpoints.SonnetDbEndpoints.QualifyMqTopic(db, resource));
        pump.Task = RunPumpAsync(mqttTopic, pump);
        return pump;
    }

    private void DecrementPump(string mqttTopic)
    {
        if (!_topicPumps.TryGetValue(mqttTopic, out var pump))
            return;

        if (pump.Decrement() > 0)
            return;

        if (_topicPumps.TryRemove(mqttTopic, out var removed))
            removed.Cancellation.Cancel();
    }

    private void RemoveClientSubscription(string clientId, string mqttTopic)
    {
        if (!_clientMqSubscriptions.TryGetValue(clientId, out var topics))
            return;

        if (topics.TryRemove(mqttTopic, out _))
            DecrementPump(mqttTopic);

        if (topics.IsEmpty)
            _clientMqSubscriptions.TryRemove(clientId, out _);
    }

    private async Task RunPumpAsync(string mqttTopic, TopicPump pump)
    {
        long cursor = _mq.GetStats(pump.QualifiedTopic).NextOffset;
        try
        {
            while (!pump.Cancellation.IsCancellationRequested)
            {
                cursor = await _mq.WaitForMessagesAsync(pump.QualifiedTopic, cursor, pump.Cancellation.Token).ConfigureAwait(false);
                IReadOnlyList<SonnetMqMessage> batch = _mq.Pull(pump.QualifiedTopic, cursor, PumpBatchMax);
                foreach (SonnetMqMessage message in batch)
                {
                    await InjectMqMessageAsync(mqttTopic, message, pump.Cancellation.Token).ConfigureAwait(false);
                    cursor = message.Offset + 1;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退订 / 断开。
        }
        catch (ObjectDisposedException)
        {
            // 应用关闭。
        }
        catch (Exception ex)
        {
            _logger.MqttMqPumpFailed(ex, mqttTopic);
        }
        finally
        {
            pump.Cancellation.Dispose();
        }
    }

    private async Task InjectMqMessageAsync(string mqttTopic, SonnetMqMessage message, CancellationToken cancellationToken)
    {
        if (_server is null)
            return;

        var builder = new MqttApplicationMessageBuilder()
            .WithTopic(mqttTopic)
            .WithPayload(message.Payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        if (message.Headers.TryGetValue("contentType", out string? contentType) && !string.IsNullOrWhiteSpace(contentType))
            builder.WithContentType(contentType);

        var injected = new InjectedMqttApplicationMessage(builder.Build())
        {
            CustomSessionItems = new System.Collections.Hashtable { [InternalPublishSessionKey] = true },
            SenderClientId = "sonnetdb",
            SenderUserName = "sonnetdb",
        };
        await _server.InjectApplicationMessage(injected, cancellationToken).ConfigureAwait(false);
    }

    private static void RejectSubscription(InterceptingSubscriptionEventArgs args, MqttSubscribeReasonCode reasonCode, string reason)
    {
        args.ProcessSubscription = false;
        args.Response.ReasonCode = reasonCode;
        args.Response.ReasonString = reason;
    }

    private sealed class TopicPump(string qualifiedTopic)
    {
        private int _refCount = 1;

        public string QualifiedTopic { get; } = qualifiedTopic;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task Task { get; set; } = Task.CompletedTask;

        public void Increment() => Interlocked.Increment(ref _refCount);

        public int Decrement() => Interlocked.Decrement(ref _refCount);
    }
}
