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
    internal const int MinPublishTimeoutMilliseconds = 100;
    internal const int MaxPublishTimeoutMilliseconds = 60_000;

    private readonly ISparkplugInternalPublisher _publisher;
    private readonly SparkplugLifecycleStore _lifecycle;
    private readonly SparkplugRebirthQueue _requests;
    private readonly SparkplugOptions _options;
    private readonly ServerMetrics _metrics;
    private readonly ILogger<SparkplugHostApplicationService> _logger;
    private byte _sequence;

    public SparkplugHostApplicationService(
        ISparkplugInternalPublisher publisher,
        SparkplugLifecycleStore lifecycle,
        IOptions<ServerOptions> options,
        ServerMetrics metrics,
        ILogger<SparkplugHostApplicationService> logger)
    {
        _publisher = publisher;
        _lifecycle = lifecycle;
        _options = options.Value.Mqtt.Sparkplug;
        _metrics = metrics;
        _logger = logger;
        _requests = new SparkplugRebirthQueue(_options.RebirthQueueCapacity, metrics);
        if (_options.RebirthPublishTimeoutMilliseconds is < MinPublishTimeoutMilliseconds
            or > MaxPublishTimeoutMilliseconds)
        {
            throw new InvalidOperationException(
                $"Sparkplug Rebirth 发布超时必须位于 {MinPublishTimeoutMilliseconds}..{MaxPublishTimeoutMilliseconds} 毫秒。");
        }
    }

    /// <summary>
    /// 将 edge node 的 Rebirth 请求加入单读后台队列。
    /// </summary>
    public bool RequestRebirth(string groupId, string edgeNodeId)
    {
        SparkplugRebirthEnqueueResult result = _requests.TryEnqueue(groupId, edgeNodeId);
        if (result is SparkplugRebirthEnqueueResult.RejectedFull or SparkplugRebirthEnqueueResult.RejectedStopped)
        {
            // 拒绝后立即恢复生命周期标记，下一条数据仍有机会重新请求，而不会永久静默。
            _lifecycle.ReleaseRebirthRequest(groupId, edgeNodeId);
            _logger.SparkplugRebirthQueueRejected(groupId, edgeNodeId, result.ToString(), _requests.Capacity);
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_options.PublishHostState)
                await TryPublishInitialStateAsync(stoppingToken).ConfigureAwait(false);

            while (await _requests.ReadAsync(stoppingToken).ConfigureAwait(false) is { } request)
                await PublishRebirthAsync(request, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 宿主停止取消是正常生命周期，不计发布失败。
        }
        catch (SparkplugPublisherUnavailableException) when (stoppingToken.IsCancellationRequested)
        {
            // broker 未就绪与宿主取消竞态时仍按正常停止处理，但不吞正常运行期的同类错误。
        }
        finally
        {
            _requests.StopAccepting();
            ReleaseDiscardedRequests();
            if (_options.PublishHostState)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await PublishStateAsync("OFFLINE", timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SparkplugPublisherUnavailableException)
                {
                    _logger.SparkplugHostStatePublishFailed(ex, _options.HostId, "OFFLINE");
                }
            }
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _requests.StopAccepting();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task PublishStateAsync(string state, CancellationToken cancellationToken)
        => _publisher.PublishInternalAsync(
            $"spBv1.0/STATE/{_options.HostId}",
            System.Text.Encoding.UTF8.GetBytes(state),
            MqttQualityOfServiceLevel.AtLeastOnce,
            retain: true,
            cancellationToken);

    /// <summary>首次 ONLINE 在 broker 启动窗口内失败时记录告警，并继续提供有界 Rebirth 服务。</summary>
    private async Task TryPublishInitialStateAsync(CancellationToken stoppingToken)
    {
        try
        {
            await PublishStateAsync("ONLINE", stoppingToken).ConfigureAwait(false);
        }
        catch (SparkplugPublisherUnavailableException ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.SparkplugHostStatePublishFailed(ex, _options.HostId, "ONLINE");
        }
    }

    /// <summary>
    /// 在独立 deadline 内发布一条 Rebirth；单节点失败不会终止后续节点处理。
    /// </summary>
    private async Task PublishRebirthAsync(
        SparkplugRebirthRequest request,
        CancellationToken stoppingToken)
    {
        string topic = $"spBv1.0/{request.GroupId}/NCMD/{request.EdgeNodeId}";
        bool published = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(_options.RebirthPublishTimeoutMilliseconds);

        try
        {
            byte[] payload = SparkplugCommandEncoder.EncodeRebirth(_sequence++);
            await _publisher.PublishInternalAsync(
                topic,
                payload,
                MqttQualityOfServiceLevel.AtLeastOnce,
                retain: false,
                deadline.Token).ConfigureAwait(false);
            published = true;
            _metrics.RecordSparkplugRebirthCommand();
            _logger.SparkplugRebirthPublished(topic);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            _metrics.RecordSparkplugRebirthPublishFailure();
            _logger.SparkplugRebirthPublishTimedOut(
                ex,
                topic,
                _options.RebirthPublishTimeoutMilliseconds);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _metrics.RecordSparkplugRebirthPublishFailure();
            _logger.SparkplugRebirthPublishFailed(ex, topic);
        }
        finally
        {
            _requests.Complete(request);
            if (!published)
            {
                // 当前项无论超时、异常还是宿主停止都不再占用容量，并允许后续数据重新请求。
                _metrics.RecordSparkplugRebirthQueueDiscarded(outstandingCount: 1, queuedCount: 0);
                _lifecycle.ReleaseRebirthRequest(request.GroupId, request.EdgeNodeId);
            }
        }
    }

    /// <summary>释放停止时未发送请求的生命周期标记，避免同进程内重启后无法重试。</summary>
    private void ReleaseDiscardedRequests()
    {
        IReadOnlyList<SparkplugRebirthRequest> discarded = _requests.DiscardOutstanding();
        foreach (SparkplugRebirthRequest request in discarded)
            _lifecycle.ReleaseRebirthRequest(request.GroupId, request.EdgeNodeId);
    }
}

/// <summary>
/// Sparkplug Host 发布边界；生产实现复用 broker bridge，测试可注入确定性的阻塞或异常行为。
/// </summary>
internal interface ISparkplugInternalPublisher
{
    /// <summary>
    /// 向内建 broker 发布一条 Host STATE 或 NCMD 消息；未就绪使用专用异常，未知错误原样传播。
    /// </summary>
    Task PublishInternalAsync(
        string topic,
        ReadOnlyMemory<byte> payload,
        MqttQualityOfServiceLevel qualityOfService,
        bool retain,
        CancellationToken cancellationToken);
}

/// <summary>表示内建 broker 尚未就绪或在内部发布期限内不可用。</summary>
internal sealed class SparkplugPublisherUnavailableException : InvalidOperationException
{
    /// <summary>创建带可选底层原因的可恢复发布异常。</summary>
    public SparkplugPublisherUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
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
