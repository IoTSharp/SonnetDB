using System.Runtime.CompilerServices;
using System.Threading;

namespace SonnetDB.Data.Mq;

/// <summary>
/// 高层 MQ 消息确认模式。
/// </summary>
public enum SndbMqAckMode
{
    /// <summary>由调用方显式调用 <see cref="SndbMqDelivery.AckAsync"/>。</summary>
    Manual = 0,

    /// <summary>在消息交给枚举器前自动确认。</summary>
    Auto = 1,
}

/// <summary>
/// 高层 MQ 发布器配置。
/// </summary>
public sealed class SndbMqProducerOptions
{
    /// <summary>同时进行的发布操作上限，用于提供有界背压。</summary>
    public int MaxInFlight { get; set; } = 1;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxInFlight);
    }
}

/// <summary>
/// 高层 MQ 消费者配置。
/// </summary>
public sealed class SndbMqConsumerOptions
{
    /// <summary>一次预取并在内存中保留的消息上限。</summary>
    public int Prefetch { get; set; } = 32;

    /// <summary>消息交给枚举器前采用的确认模式。</summary>
    public SndbMqAckMode AckMode { get; set; } = SndbMqAckMode.Manual;

    /// <summary>没有消息时两次拉取之间的等待时间。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Prefetch);
        if (PollInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PollInterval), "PollInterval 不能为负数。");
        if (!Enum.IsDefined(AckMode))
            throw new ArgumentOutOfRangeException(nameof(AckMode));
    }
}

/// <summary>
/// 高层 MQ 发布器构建器。
/// </summary>
public sealed class SndbMqProducerBuilder
{
    private readonly SndbMqClient _client;
    private readonly string _topic;
    private readonly SndbMqProducerOptions _options = new();

    /// <summary>创建发布器构建器。</summary>
    /// <param name="client">底层 MQ 客户端。</param>
    /// <param name="topic">发布目标 Topic。</param>
    public SndbMqProducerBuilder(SndbMqClient client, string topic)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        _topic = topic;
    }

    /// <summary>设置同时进行的发布操作上限。</summary>
    /// <param name="maxInFlight">正整数上限。</param>
    /// <returns>当前构建器。</returns>
    public SndbMqProducerBuilder MaxInFlight(int maxInFlight)
    {
        _options.MaxInFlight = maxInFlight;
        return this;
    }

    /// <summary>设置同时进行的发布操作上限。</summary>
    /// <param name="maxInFlight">正整数上限。</param>
    /// <returns>当前构建器。</returns>
    public SndbMqProducerBuilder WithMaxInFlight(int maxInFlight) => MaxInFlight(maxInFlight);

    /// <summary>构建发布器。</summary>
    /// <returns>有界发布器实例。</returns>
    public SndbMqProducer Build()
    {
        _options.Validate();
        return new SndbMqProducer(_client, _topic, _options);
    }
}

/// <summary>
/// 高层 MQ 消费者构建器。
/// </summary>
public sealed class SndbMqConsumerBuilder
{
    private readonly SndbMqClient _client;
    private readonly string _topic;
    private readonly string _consumerGroup;
    private readonly SndbMqConsumerOptions _options = new();

    /// <summary>创建消费者构建器。</summary>
    /// <param name="client">底层 MQ 客户端。</param>
    /// <param name="topic">消费目标 Topic。</param>
    /// <param name="consumerGroup">消费者组名称。</param>
    public SndbMqConsumerBuilder(SndbMqClient client, string topic, string consumerGroup)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        _topic = topic;
        _consumerGroup = consumerGroup;
    }

    /// <summary>设置预取上限。</summary>
    /// <param name="prefetch">正整数上限。</param>
    /// <returns>当前构建器。</returns>
    public SndbMqConsumerBuilder Prefetch(int prefetch)
    {
        _options.Prefetch = prefetch;
        return this;
    }

    /// <summary>设置预取上限。</summary>
    /// <param name="prefetch">正整数上限。</param>
    /// <returns>当前构建器。</returns>
    public SndbMqConsumerBuilder WithPrefetch(int prefetch) => Prefetch(prefetch);

    /// <summary>使用手动确认模式。</summary>
    /// <returns>当前构建器。</returns>
    public SndbMqConsumerBuilder ManualAck()
    {
        _options.AckMode = SndbMqAckMode.Manual;
        return this;
    }

    /// <summary>使用自动确认模式。</summary>
    /// <returns>当前构建器。</returns>
    public SndbMqConsumerBuilder AutoAck()
    {
        _options.AckMode = SndbMqAckMode.Auto;
        return this;
    }

    /// <summary>设置无消息时的轮询间隔。</summary>
    /// <param name="pollInterval">非负等待时间。</param>
    /// <returns>当前构建器。</returns>
    public SndbMqConsumerBuilder PollInterval(TimeSpan pollInterval)
    {
        _options.PollInterval = pollInterval;
        return this;
    }

    /// <summary>构建消费者。</summary>
    /// <returns>有界消费者实例。</returns>
    public SndbMqConsumer Build()
    {
        _options.Validate();
        return new SndbMqConsumer(_client, _topic, _consumerGroup, _options);
    }
}

/// <summary>
/// 高层 MQ 发布器。发布并发由 <see cref="SndbMqProducerOptions.MaxInFlight"/> 限制。
/// </summary>
public sealed class SndbMqProducer : IAsyncDisposable, IDisposable
{
    private readonly SndbMqClient _client;
    private readonly string _topic;
    private readonly SemaphoreSlim _slots;
    private readonly object _sync = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private bool _draining;
    private bool _disposed;

    internal SndbMqProducer(SndbMqClient client, string topic, SndbMqProducerOptions options)
    {
        _client = client;
        _topic = topic;
        _slots = new SemaphoreSlim(options.MaxInFlight, options.MaxInFlight);
    }

    /// <summary>发布一条消息。</summary>
    /// <param name="payload">消息体。</param>
    /// <param name="headers">可选消息头。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>消息 offset。</returns>
    public Task<long> PublishAsync(
        ReadOnlyMemory<byte> payload,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
        => PublishCoreAsync(payload, headers, cancellationToken);

    /// <summary>发布一批消息。</summary>
    /// <param name="messages">消息集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按输入顺序返回的 offset。</returns>
    public Task<IReadOnlyList<long>> PublishManyAsync(
        IReadOnlyList<SndbMqPublishEntry> messages,
        CancellationToken cancellationToken = default)
        => PublishManyCoreAsync(messages, cancellationToken);

    /// <summary>
    /// 停止接收新的发布并等待已进入发布器的操作完成。
    /// </summary>
    /// <param name="cancellationToken">取消等待的令牌，不会中断已开始的发布。</param>
    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _draining = true;
            if (_active == 0)
                _drained.TrySetResult();
        }

        await _drained.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        DrainAsync().GetAwaiter().GetResult();
        lock (_sync)
            _disposed = true;
        _slots.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        await DrainAsync().ConfigureAwait(false);
        lock (_sync)
            _disposed = true;
        _slots.Dispose();
    }

    private async Task<long> PublishCoreAsync(
        ReadOnlyMemory<byte> payload,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _client.PublishAsync(_topic, payload.ToArray(), headers, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Exit();
        }
    }

    private async Task<IReadOnlyList<long>> PublishManyCoreAsync(
        IReadOnlyList<SndbMqPublishEntry> messages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _client.PublishManyAsync(_topic, messages, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Exit();
        }
    }

    private async ValueTask EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            if (_draining || _disposed)
            {
                _slots.Release();
                ObjectDisposedException.ThrowIf(_disposed, this);
                throw new InvalidOperationException("MQ 发布器已进入 drain，不能再接收新消息。");
            }

            _active++;
        }
    }

    private void Exit()
    {
        lock (_sync)
        {
            _active--;
            if (_draining && _active == 0)
                _drained.TrySetResult();
        }

        _slots.Release();
    }
}

/// <summary>
/// 高层 MQ 消费投递，包含消息和显式确认操作。
/// </summary>
public sealed class SndbMqDelivery
{
    private readonly SndbMqConsumer _owner;
    private int _acknowledged;

    internal SndbMqDelivery(SndbMqConsumer owner, SndbMqMessage message, bool acknowledged)
    {
        _owner = owner;
        Message = message;
        _acknowledged = acknowledged ? 1 : 0;
    }

    /// <summary>底层消息。</summary>
    public SndbMqMessage Message { get; }

    /// <summary>Topic 名称。</summary>
    public string Topic => Message.Topic;

    /// <summary>消息 offset。</summary>
    public long Offset => Message.Offset;

    /// <summary>消息体。</summary>
    public byte[] Payload => Message.Payload;

    /// <summary>消息头。</summary>
    public IReadOnlyDictionary<string, string> Headers => Message.Headers;

    /// <summary>是否已经确认。</summary>
    public bool IsAcknowledged => Volatile.Read(ref _acknowledged) != 0;

    /// <summary>确认该消息及其之前的消息。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>消费者组下一条 offset。</returns>
    public async Task<long> AckAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _acknowledged, 1) != 0)
            return Message.Offset + 1;

        try
        {
            return await _owner.AckAsync(Message.Offset, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _acknowledged, 0);
            throw;
        }
    }

    /// <summary>拒绝该消息并请求重新投递；达到最大次数后进入死信。</summary>
    /// <param name="reason">可选拒绝原因。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>拒绝结果。</returns>
    public async Task<SndbMqNackResult> NackAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _acknowledged, 1) != 0)
            return new SndbMqNackResult(Message.Offset, 0, false, null);

        try
        {
            return await _owner.NackAsync(Message.Offset, reason, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _acknowledged, 0);
            throw;
        }
    }
}

/// <summary>
/// 高层 MQ 消费者，提供有界预取的 push/pull 异步枚举。
/// </summary>
public sealed class SndbMqConsumer : IAsyncDisposable, IDisposable
{
    private readonly SndbMqClient _client;
    private readonly string _topic;
    private readonly string _consumerGroup;
    private readonly SndbMqConsumerOptions _options;
    private readonly object _sync = new();
    private readonly HashSet<long> _pending = [];
    private readonly CancellationTokenSource _disposeCts = new();
    private TaskCompletionSource _ackPulse = NewPulse();
    private bool _draining;
    private bool _disposed;
    private bool _enumerating;

    internal SndbMqConsumer(SndbMqClient client, string topic, string consumerGroup, SndbMqConsumerOptions options)
    {
        _client = client;
        _topic = topic;
        _consumerGroup = consumerGroup;
        _options = options;
    }

    /// <summary>
    /// 按预取上限轮询并产生投递。手动确认模式下需对每个投递调用 <see cref="SndbMqDelivery.AckAsync"/>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>有界消息投递序列。</returns>
    public IAsyncEnumerable<SndbMqDelivery> PullAsync(CancellationToken cancellationToken = default)
        => ConsumeCoreAsync(cancellationToken);

    /// <summary>推送式异步枚举入口，与 <see cref="PullAsync"/> 共用有界预取实现。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>有界消息投递序列。</returns>
    public IAsyncEnumerable<SndbMqDelivery> PushAsync(CancellationToken cancellationToken = default)
        => ConsumeCoreAsync(cancellationToken);

    /// <summary>读取消息投递序列的别名。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>有界消息投递序列。</returns>
    public IAsyncEnumerable<SndbMqDelivery> ReadAllAsync(CancellationToken cancellationToken = default)
        => ConsumeCoreAsync(cancellationToken);

    /// <summary>
    /// 停止新的轮询；已经预取的消息仍会在枚举器继续读取时交付。
    /// </summary>
    /// <param name="cancellationToken">取消等待的令牌。</param>
    public Task DrainAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _draining = true;
            _ackPulse.TrySetResult();
        }

        return Task.CompletedTask.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        lock (_sync)
        {
            _draining = true;
            _disposed = true;
            _ackPulse.TrySetResult();
        }

        _disposeCts.Cancel();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal async Task<long> AckAsync(long offset, CancellationToken cancellationToken)
    {
        lock (_sync)
            ObjectDisposedException.ThrowIf(_disposed, this);

        long next = await _client.AckAsync(_topic, _consumerGroup, offset, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _pending.RemoveWhere(value => value < next);
            _ackPulse.TrySetResult();
            _ackPulse = NewPulse();
        }

        return next;
    }

    internal async Task<SndbMqNackResult> NackAsync(long offset, string? reason, CancellationToken cancellationToken)
    {
        lock (_sync)
            ObjectDisposedException.ThrowIf(_disposed, this);

        SndbMqNackResult result = await _client.NackAsync(_topic, _consumerGroup, offset, reason, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _pending.Remove(offset);
            _pending.RemoveWhere(value => value < result.NextOffset);
            _ackPulse.TrySetResult();
            _ackPulse = NewPulse();
        }

        return result;
    }

    private async IAsyncEnumerable<SndbMqDelivery> ConsumeCoreAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCts.Token);
        CancellationToken operationToken = linked.Token;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_enumerating)
                throw new InvalidOperationException("同一个 MQ consumer 不能并发创建多个消息枚举器。");
            _enumerating = true;
        }

        try
        {
            var queue = new Queue<SndbMqMessage>();
            while (true)
            {
                if (queue.Count == 0)
                {
                    bool stop;
                    lock (_sync)
                        stop = _draining || _disposed;
                    if (stop)
                        yield break;

                    Task? ackWait = null;
                    lock (_sync)
                    {
                        if (_pending.Count != 0)
                            ackWait = _ackPulse.Task;
                    }

                    if (ackWait is not null)
                    {
                        await ackWait.WaitAsync(operationToken).ConfigureAwait(false);
                        continue;
                    }

                    IReadOnlyList<SndbMqMessage> messages = await _client
                        .PullAsync(_topic, _consumerGroup, _options.Prefetch, operationToken)
                        .ConfigureAwait(false);
                    foreach (SndbMqMessage message in messages)
                    {
                        lock (_sync)
                        {
                            if (_pending.Add(message.Offset))
                                queue.Enqueue(message);
                        }
                    }

                    if (queue.Count == 0)
                    {
                        await Task.Delay(_options.PollInterval, operationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                SndbMqMessage next = queue.Dequeue();
                bool autoAck = _options.AckMode == SndbMqAckMode.Auto;
                if (autoAck)
                {
                    await AckAsync(next.Offset, operationToken).ConfigureAwait(false);
                }

                yield return new SndbMqDelivery(this, next, autoAck);
            }
        }
        finally
        {
            lock (_sync)
                _enumerating = false;
        }
    }

    private static TaskCompletionSource NewPulse()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
