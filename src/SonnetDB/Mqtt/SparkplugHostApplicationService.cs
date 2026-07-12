using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using SonnetDB.Configuration;
using SonnetDB.Diagnostics;
using SonnetDB.Hosting;

namespace SonnetDB.Mqtt;

/// <summary>
/// 发布 Sparkplug Primary Host STATE，并异步发送去重后的 Rebirth 命令。
/// </summary>
internal sealed class SparkplugHostApplicationService : BackgroundService
{
    private readonly Channel<RebirthRequest> _requests = Channel.CreateUnbounded<RebirthRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly SonnetMqttBrokerBridge _bridge;
    private readonly SparkplugOptions _options;
    private readonly ServerMetrics _metrics;
    private readonly ILogger<SparkplugHostApplicationService> _logger;
    private byte _sequence;

    public SparkplugHostApplicationService(
        SonnetMqttBrokerBridge bridge,
        IOptions<ServerOptions> options,
        ServerMetrics metrics,
        ILogger<SparkplugHostApplicationService> logger)
    {
        _bridge = bridge;
        _options = options.Value.Mqtt.Sparkplug;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// 将 edge node 的 Rebirth 请求加入单读后台队列。
    /// </summary>
    public bool RequestRebirth(string groupId, string edgeNodeId)
        => _requests.Writer.TryWrite(new(groupId, edgeNodeId));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_options.PublishHostState)
                await PublishStateAsync("ONLINE", stoppingToken).ConfigureAwait(false);

            await foreach (RebirthRequest request in _requests.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                string topic = $"spBv1.0/{request.GroupId}/NCMD/{request.EdgeNodeId}";
                byte[] payload = SparkplugCommandEncoder.EncodeRebirth(_sequence++);
                await _bridge.PublishInternalAsync(
                    topic,
                    payload,
                    MqttQualityOfServiceLevel.AtLeastOnce,
                    retain: false,
                    stoppingToken).ConfigureAwait(false);
                _metrics.RecordSparkplugRebirthCommand();
                _logger.SparkplugRebirthPublished(topic);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停止。
        }
        finally
        {
            if (_options.PublishHostState)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await PublishStateAsync("OFFLINE", timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
                {
                    _logger.SparkplugHostStatePublishFailed(ex, _options.HostId, "OFFLINE");
                }
            }
        }
    }

    private Task PublishStateAsync(string state, CancellationToken cancellationToken)
        => _bridge.PublishInternalAsync(
            $"spBv1.0/STATE/{_options.HostId}",
            System.Text.Encoding.UTF8.GetBytes(state),
            MqttQualityOfServiceLevel.AtLeastOnce,
            retain: true,
            cancellationToken);

    private readonly record struct RebirthRequest(string GroupId, string EdgeNodeId);
}

/// <summary>最小 Sparkplug protobuf 命令编码器。</summary>
internal static class SparkplugCommandEncoder
{
    /// <summary>编码 <c>Node Control/Rebirth=true</c> NCMD payload。</summary>
    public static byte[] EncodeRebirth(byte sequence)
    {
        ulong timestamp = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var metric = new MemoryStream(64);
        WriteString(metric, 1, "Node Control/Rebirth");
        WriteTag(metric, 3, 0);
        WriteVarint(metric, timestamp);
        WriteTag(metric, 4, 0);
        WriteVarint(metric, 11);
        WriteTag(metric, 14, 0);
        WriteVarint(metric, 1);

        using var payload = new MemoryStream(96);
        WriteTag(payload, 1, 0);
        WriteVarint(payload, timestamp);
        WriteTag(payload, 2, 2);
        WriteVarint(payload, checked((ulong)metric.Length));
        metric.Position = 0;
        metric.CopyTo(payload);
        WriteTag(payload, 3, 0);
        WriteVarint(payload, sequence);
        return payload.ToArray();
    }

    private static void WriteString(Stream stream, int fieldNumber, string value)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
        WriteTag(stream, fieldNumber, 2);
        WriteVarint(stream, checked((ulong)bytes.Length));
        stream.Write(bytes);
    }

    private static void WriteTag(Stream stream, int fieldNumber, int wireType)
        => WriteVarint(stream, checked((ulong)((fieldNumber << 3) | wireType)));

    private static void WriteVarint(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[10];
        int count = 0;
        while (value >= 0x80)
        {
            buffer[count++] = (byte)(value | 0x80);
            value >>= 7;
        }
        buffer[count++] = (byte)value;
        stream.Write(buffer[..count]);
    }
}
