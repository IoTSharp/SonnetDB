using System.Buffers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using SonnetDB.Configuration;
using SonnetDB.Diagnostics;

namespace SonnetDB.Mqtt;

/// <summary>
/// 订阅外部 MQTT broker 并把受管 topic 写入 SonnetDB 的后台服务（M28 P5b #243）。
/// </summary>
internal sealed class SonnetMqttExternalClientService : BackgroundService
{
    private readonly SonnetMqttMeasurementIngestor _ingestor;
    private readonly MqttExternalClientOptions _options;
    private readonly ILogger<SonnetMqttExternalClientService> _logger;

    /// <summary>
    /// 创建外部 MQTT client 订阅服务。
    /// </summary>
    public SonnetMqttExternalClientService(
        SonnetMqttMeasurementIngestor ingestor,
        IOptions<ServerOptions> options,
        ILogger<SonnetMqttExternalClientService> logger)
    {
        _ingestor = ingestor;
        _options = options.Value.Mqtt.ExternalClient;
        _logger = logger;
    }

    /// <summary>
    /// 运行外部 broker 连接、订阅与重连循环。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        if (!TryValidateOptions(out string validationError))
        {
            _logger.ExternalMqttConfigurationInvalid(validationError);
            return;
        }

        var initialDelay = TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectDelaySeconds));
        var maxDelay = TimeSpan.FromSeconds(Math.Max((int)initialDelay.TotalSeconds, _options.MaxReconnectDelaySeconds));
        var reconnectDelay = initialDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            var client = CreateClient(out var clientOptions);
            try
            {
                bool connected = await ConnectAndSubscribeAsync(client, clientOptions, stoppingToken).ConfigureAwait(false);
                if (connected)
                    reconnectDelay = initialDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.ExternalMqttConnectionFailed(ex, _options.Host, _options.Port);
            }
            finally
            {
                await DisconnectQuietlyAsync(client).ConfigureAwait(false);
                (client as IDisposable)?.Dispose();
            }

            if (stoppingToken.IsCancellationRequested)
                break;

            try
            {
                await Task.Delay(reconnectDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            reconnectDelay = TimeSpan.FromSeconds(Math.Min(maxDelay.TotalSeconds, reconnectDelay.TotalSeconds * 2));
        }
    }

    private IMqttClient CreateClient(out MqttClientOptions clientOptions)
    {
        string clientId = string.IsNullOrWhiteSpace(_options.ClientId)
            ? "sonnetdb-external-client"
            : _options.ClientId.Trim();

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithClientId(clientId)
            .WithCleanStart(_options.CleanStart);

        if (!string.IsNullOrWhiteSpace(_options.UserName) || !string.IsNullOrWhiteSpace(_options.Password))
            builder.WithCredentials(_options.UserName ?? string.Empty, _options.Password ?? string.Empty);

        if (_options.UseTls)
            builder.WithTlsOptions(tls => tls.UseTls(true));

        clientOptions = builder.Build();
        return new MqttClientFactory().CreateMqttClient();
    }

    private async Task<bool> ConnectAndSubscribeAsync(
        IMqttClient client,
        MqttClientOptions clientOptions,
        CancellationToken stoppingToken)
    {
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DisconnectedAsync += args =>
        {
            disconnected.TrySetResult();
            if (!stoppingToken.IsCancellationRequested)
            {
                _logger.ExternalMqttDisconnected(_options.Host, _options.Port, (int)args.Reason);
            }

            return Task.CompletedTask;
        };
        client.ApplicationMessageReceivedAsync += args =>
        {
            HandleMessage(args.ApplicationMessage);
            return Task.CompletedTask;
        };

        var connectResult = await client.ConnectAsync(clientOptions, stoppingToken).ConfigureAwait(false);
        if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
        {
            _logger.ExternalMqttConnectionRejected(
                _options.Host,
                _options.Port,
                (int)connectResult.ResultCode,
                connectResult.ReasonString);
            return false;
        }

        int subscribed = await SubscribeAllAsync(client, stoppingToken).ConfigureAwait(false);
        if (subscribed == 0)
        {
            _logger.ExternalMqttNoSubscriptions();
            return false;
        }

        _logger.ExternalMqttConnected(_options.Host, _options.Port, subscribed);

        await disconnected.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
        return true;
    }

    private async Task<int> SubscribeAllAsync(IMqttClient client, CancellationToken cancellationToken)
    {
        var builder = new MqttClientSubscribeOptionsBuilder();
        int requested = 0;
        foreach (var subscription in _options.Subscriptions)
        {
            if (string.IsNullOrWhiteSpace(subscription.TopicFilter))
                continue;

            var qos = subscription.Qos <= 0
                ? MqttQualityOfServiceLevel.AtMostOnce
                : MqttQualityOfServiceLevel.AtLeastOnce;
            builder.WithTopicFilter(subscription.TopicFilter.Trim(), qos);
            requested++;
        }

        if (requested == 0)
            return 0;

        var result = await client.SubscribeAsync(builder.Build(), cancellationToken).ConfigureAwait(false);
        int accepted = 0;
        foreach (var item in result.Items)
        {
            if (item.ResultCode is MqttClientSubscribeResultCode.GrantedQoS0
                or MqttClientSubscribeResultCode.GrantedQoS1)
            {
                accepted++;
                continue;
            }

            _logger.ExternalMqttSubscriptionRejected((int)item.ResultCode);
        }

        return accepted;
    }

    private void HandleMessage(MqttApplicationMessage message)
    {
        try
        {
            if (!MqttTopicParser.TryParse(message.Topic, out var route, out string error)
                || route.Kind != MqttTopicKind.Measurement)
            {
                _logger.ExternalMqttMessageIgnored(message.Topic, error);
                return;
            }

            byte[] payload = message.Payload.ToArray();
            if (!_ingestor.TryIngestMeasurement(
                    route,
                    payload,
                    message.ContentType,
                    message.UserProperties,
                    out var result,
                    out var reasonCode,
                    out string reason))
            {
                _logger.ExternalMqttIngestFailed(message.Topic, (int)reasonCode, reason);
                return;
            }

            _logger.ExternalMqttIngested(message.Topic, result.Written, result.Skipped);
        }
        catch (Exception ex)
        {
            _logger.ExternalMqttMessageFailed(ex, message.Topic);
        }
    }

    private bool TryValidateOptions(out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            error = "Host 不可为空。";
            return false;
        }

        if (_options.Port < 0 || _options.Port > 65535)
        {
            error = "Port 必须位于 0..65535。";
            return false;
        }

        if (_options.Subscriptions.Count == 0)
        {
            error = "Subscriptions 至少需要一个 topic filter。";
            return false;
        }

        for (int i = 0; i < _options.Subscriptions.Count; i++)
        {
            var subscription = _options.Subscriptions[i];
            if (string.IsNullOrWhiteSpace(subscription.TopicFilter))
            {
                error = $"Subscriptions[{i}].TopicFilter 不可为空。";
                return false;
            }

            if (subscription.Qos is < 0 or > 1)
            {
                error = $"Subscriptions[{i}].Qos 当前仅支持 0 或 1。";
                return false;
            }
        }

        return true;
    }

    private static async Task DisconnectQuietlyAsync(IMqttClient client)
    {
        try
        {
            if (client.IsConnected)
                await client.DisconnectAsync(new MqttClientDisconnectOptions(), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 应用关闭或网络异常时尽力断开即可。
        }
    }
}
