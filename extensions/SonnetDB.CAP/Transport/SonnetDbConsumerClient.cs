using System.Text.Json;
using DotNetCore.CAP.Messages;
using DotNetCore.CAP.Transport;

namespace SonnetDB.CAP.Transport;

internal sealed class SonnetDbConsumerClientFactory(CapMqConnection connection) : IConsumerClientFactory
{
    public Task<IConsumerClient> CreateAsync(string groupName, byte groupConcurrent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        if (groupConcurrent > 1)
            throw new NotSupportedException("SonnetDB CAP 消费入口要求 GroupConcurrent 为默认值 0 或 1；业务处理并发由 CAP dispatcher 管理。");
        return Task.FromResult<IConsumerClient>(new SonnetDbConsumerClient(connection, groupName));
    }
}

internal sealed class SonnetDbConsumerClient(CapMqConnection connection, string groupName) : IConsumerClient
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string[]? _topics;
    private Delivery? _pending;
    private int _listening;
    private bool _disposed;
    public BrokerAddress BrokerAddress => connection.BrokerAddress;
    public Func<TransportMessage, object?, Task>? OnMessageCallback { get; set; }
    public Action<LogMessageEventArgs>? OnLogCallback { get; set; }

    public Task<ICollection<string>> FetchTopicsAsync(IEnumerable<string> topicNames)
        => Task.FromResult<ICollection<string>>(ValidateTopics(topicNames));

    private static string[] ValidateTopics(IEnumerable<string> topics)
    {
        ArgumentNullException.ThrowIfNull(topics);
        string[] result = topics.Distinct(StringComparer.Ordinal).ToArray();
        foreach (string topic in result)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(topic);
            if (topic.IndexOfAny(['*', '#', '+']) >= 0)
                throw new NotSupportedException("SonnetDB CAP 传输暂不支持通配符订阅。");
        }
        return result;
    }

    public async Task SubscribeAsync(IEnumerable<string> topics)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_topics is not null)
            throw new InvalidOperationException("消费者只能订阅一次。");
        string[] names = ValidateTopics(topics);
        connection.AcquireGroup(groupName);
        try
        {
            foreach (string name in names)
                await connection.Client.EnsureConsumerGroupAsync(connection.Topic(name), connection.Group(groupName)).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(_disposed, this);
            _topics = names;
        }
        catch
        {
            connection.ReleaseGroup(groupName);
            throw;
        }
    }

    public async Task ListeningAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_topics is null || OnMessageCallback is null)
            throw new InvalidOperationException("必须先订阅并注册消息回调。");
        if (Interlocked.CompareExchange(ref _listening, 1, 0) != 0)
            throw new InvalidOperationException("消费者已启动。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        CancellationToken token = linked.Token;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                bool advanced = false;
                foreach (string name in _topics)
                {
                    string topic = connection.Topic(name);
                    var messages = await connection.Client.PullAsync(topic, connection.Group(groupName), 1, token).ConfigureAwait(false);
                    if (messages.Count == 0)
                        continue;
                    var message = messages[0];
                    if (message.Payload.Length > connection.Options.MaxEnvelopeBytes)
                        throw new InvalidDataException("CAP MQ 信封超过字节预算。");
                    var envelope = JsonSerializer.Deserialize(message.Payload.AsSpan(), CapTransportJsonContext.Default.CapTransportEnvelope)
                        ?? throw new InvalidDataException("CAP MQ 信封为空。");
                    if (envelope.FormatVersion != 1 || envelope.Headers is null || envelope.Body is null
                        || !envelope.Headers.TryGetValue(Headers.MessageName, out string? originalName) || originalName != name
                        || !envelope.Headers.TryGetValue(Headers.MessageId, out string? id) || string.IsNullOrWhiteSpace(id))
                        throw new InvalidDataException("CAP MQ 信封版本、topic 或消息身份不匹配。");
                    envelope.Headers[Headers.Group] = groupName;
                    var delivery = new Delivery(this, topic, message.Offset);
                    _pending = delivery;
                    await OnMessageCallback(new TransportMessage(envelope.Headers, envelope.Body), delivery).ConfigureAwait(false);
                    advanced |= delivery.Committed;
                    _pending = null;
                }
                if (!advanced)
                    await Task.Delay(connection.Options.PollInterval, token).ConfigureAwait(false);
            }
        }
        finally
        {
            _pending = null;
            _stopped.TrySetResult();
        }
    }

    public async Task CommitAsync(object? sender)
    {
        Delivery delivery = RequireDelivery(sender);
        if (delivery.Committed)
            return;
        await connection.Client.AckAsync(delivery.Topic, connection.Group(groupName), delivery.Offset).ConfigureAwait(false);
        delivery.Committed = true;
    }

    public Task RejectAsync(object? sender)
    {
        _ = RequireDelivery(sender);
        // Inbox 保存失败时保持 offset；不能使用有限 Nack 重试将未持久化消息自动跳过。
        return Task.CompletedTask;
    }

    private Delivery RequireDelivery(object? sender)
    {
        if (sender is not Delivery delivery || !ReferenceEquals(delivery.Owner, this) || !ReferenceEquals(_pending, delivery))
            throw new ArgumentException("只能确认当前消费者的在途消息。", nameof(sender));
        return delivery;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (Volatile.Read(ref _listening) != 0)
            await _stopped.Task.ConfigureAwait(false);
        if (_topics is not null)
            connection.ReleaseGroup(groupName);
        _stop.Dispose();
    }

    private sealed class Delivery(SonnetDbConsumerClient owner, string topic, long offset)
    {
        public SonnetDbConsumerClient Owner { get; } = owner;
        public string Topic { get; } = topic;
        public long Offset { get; } = offset;
        public bool Committed { get; set; }
    }
}
