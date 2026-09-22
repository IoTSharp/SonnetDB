using System.Threading.Channels;

namespace SonnetDB.Streaming;

/// <summary>
/// 可恢复合同的内存实现：有界缓冲、事件时间 watermark 与单个 in-flight 批次的至少一次投递。
/// 该实现不提供跨进程持久化、分布式协调或 exactly-once。
/// </summary>
public sealed class InMemoryStreamingSubscription : IAsyncDisposable
{
    private readonly object _stateGate = new();
    private readonly Channel<StreamingEvent> _channel;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private StreamingSubscriptionCheckpoint _checkpoint;
    private StreamingDeliveryBatch? _inFlight;
    private DateTimeOffset _watermarkUtc;
    private bool _closed;

    /// <summary>
    /// 创建内存订阅。
    /// </summary>
    /// <param name="definition">订阅定义。</param>
    /// <param name="checkpoint">可选的已恢复检查点。</param>
    public InMemoryStreamingSubscription(
        StreamingSubscriptionDefinition definition,
        StreamingSubscriptionCheckpoint? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        if (checkpoint is not null)
        {
            checkpoint.Validate();
            if (!string.Equals(checkpoint.SubscriptionId, definition.SubscriptionId, StringComparison.Ordinal))
                throw new ArgumentException("检查点不属于当前订阅。", nameof(checkpoint));
        }

        Definition = definition;
        _checkpoint = checkpoint ?? StreamingSubscriptionCheckpoint.Create(definition.SubscriptionId);
        _watermarkUtc = _checkpoint.WatermarkUtc;
        _channel = Channel.CreateBounded<StreamingEvent>(new BoundedChannelOptions(definition.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>订阅定义。</summary>
    public StreamingSubscriptionDefinition Definition { get; }

    /// <summary>最近一次已确认的检查点。</summary>
    public StreamingSubscriptionCheckpoint Checkpoint
    {
        get
        {
            lock (_stateGate)
                return _checkpoint;
        }
    }

    /// <summary>当前事件时间 watermark。</summary>
    public DateTimeOffset WatermarkUtc
    {
        get
        {
            lock (_stateGate)
                return _watermarkUtc;
        }
    }

    /// <summary>当前有界缓冲区中尚未读取的事件数。</summary>
    public int BufferedCount => _channel.Reader.Count;

    /// <summary>
    /// 推进事件时间 watermark。watermark 只能单调前进。
    /// </summary>
    /// <param name="watermarkUtc">新的 UTC watermark。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public ValueTask AdvanceWatermarkAsync(DateTimeOffset watermarkUtc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (watermarkUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("watermark 必须为 UTC。", nameof(watermarkUtc));

        lock (_stateGate)
        {
            EnsureOpenLocked();
            if (watermarkUtc < _watermarkUtc)
                throw new ArgumentOutOfRangeException(nameof(watermarkUtc), "watermark 不能回退。");
            _watermarkUtc = watermarkUtc;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 发布事件。缓冲区满时等待空间；取消令牌会中止等待。
    /// </summary>
    /// <param name="value">待发布事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>事件被接受或按迟到策略丢弃的结果。</returns>
    public async ValueTask<StreamingPublishResult> PublishAsync(
        StreamingEvent value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        StreamingEvent accepted;
        DateTimeOffset watermark;
        lock (_stateGate)
        {
            EnsureOpenLocked();
            watermark = _watermarkUtc;
            bool isLate = IsLate(value.EventTimeUtc, watermark, Definition.AllowedLateness);
            if (isLate && Definition.LateEventPolicy == StreamingLateEventPolicy.Drop)
                return new StreamingPublishResult(StreamingPublishDisposition.DroppedLate, value.EventId, value.Sequence, watermark);
            if (isLate && Definition.LateEventPolicy == StreamingLateEventPolicy.Reject)
                throw new StreamingLateEventException(value.EventId);

            accepted = value with
            {
                Payload = value.Payload.ToArray(),
                Headers = value.Headers is null
                    ? null
                    : new Dictionary<string, string>(value.Headers, StringComparer.Ordinal),
                IsLate = isLate,
            };
        }

        await _channel.Writer.WriteAsync(accepted, cancellationToken).ConfigureAwait(false);
        return new StreamingPublishResult(StreamingPublishDisposition.Accepted, value.EventId, value.Sequence, watermark);
    }

    /// <summary>
    /// 读取一个有界批次。未确认的批次会以相同事件再次投递，并递增 Attempt。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>有事件时返回批次；订阅关闭且已排空时返回 null。</returns>
    public async ValueTask<StreamingDeliveryBatch?> ReadBatchAsync(CancellationToken cancellationToken = default)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                if (_inFlight is not null)
                {
                    _inFlight = _inFlight with
                    {
                        Attempt = checked(_inFlight.Attempt + 1),
                        Status = StreamingDeliveryStatus.Redelivered,
                    };
                    return _inFlight;
                }
            }

            while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var events = new List<StreamingEvent>(Definition.BatchSize);
                while (events.Count < Definition.BatchSize && _channel.Reader.TryRead(out StreamingEvent? value))
                    events.Add(value);
                if (events.Count == 0)
                    continue;

                lock (_stateGate)
                {
                    DateTimeOffset watermark = _watermarkUtc;
                    long sequence = events[^1].Sequence;
                    var candidate = _checkpoint.Advance(sequence, watermark);
                    var batch = new StreamingDeliveryBatch(
                        Guid.NewGuid().ToString("N"),
                        1,
                        StreamingDeliveryStatus.InFlight,
                        events,
                        candidate);
                    _inFlight = batch;
                    return batch;
                }
            }

            return null;
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <summary>
    /// 确认当前 in-flight 批次，并推进内存检查点。
    /// </summary>
    /// <param name="deliveryId">待读取批次的 delivery id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>确认结果和可持久化检查点。</returns>
    public ValueTask<StreamingDeliveryReceipt> AcknowledgeAsync(
        string deliveryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateGate)
        {
            if (_inFlight is null || !string.Equals(_inFlight.DeliveryId, deliveryId, StringComparison.Ordinal))
                throw new InvalidOperationException("没有匹配的 in-flight Streaming 批次。");

            int attempt = _inFlight.Attempt;
            DateTimeOffset watermark = _watermarkUtc >= _inFlight.CandidateCheckpoint.WatermarkUtc
                ? _watermarkUtc
                : _inFlight.CandidateCheckpoint.WatermarkUtc;
            _checkpoint = _inFlight.CandidateCheckpoint with { WatermarkUtc = watermark };
            _inFlight = null;
            return ValueTask.FromResult(new StreamingDeliveryReceipt(
                deliveryId,
                attempt,
                StreamingDeliveryStatus.Acknowledged,
                _checkpoint));
        }
    }

    /// <summary>关闭发布端；已入队事件仍可读完，未确认批次仍可确认。</summary>
    public ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (!_closed)
            {
                _closed = true;
                _channel.Writer.TryComplete();
            }
        }

        return ValueTask.CompletedTask;
    }

    private void EnsureOpenLocked()
    {
        if (_closed)
            throw new ObjectDisposedException(nameof(InMemoryStreamingSubscription));
    }

    private static bool IsLate(DateTimeOffset eventTimeUtc, DateTimeOffset watermarkUtc, TimeSpan allowedLateness)
    {
        if (watermarkUtc == DateTimeOffset.MinValue)
            return false;
        DateTimeOffset threshold = allowedLateness >= watermarkUtc - DateTimeOffset.MinValue
            ? DateTimeOffset.MinValue
            : watermarkUtc - allowedLateness;
        return eventTimeUtc < threshold;
    }
}
