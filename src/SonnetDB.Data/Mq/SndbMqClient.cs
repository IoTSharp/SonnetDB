using System.Buffers;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SonnetDB.Data.Remote;
using SonnetDB.Protocol;
using SonnetMQ;

namespace SonnetDB.Data.Mq;

/// <summary>
/// SonnetDB 消息队列客户端，统一支持嵌入式与远程 SonnetDB。
/// </summary>
public sealed class SndbMqClient : IDisposable
{
    private readonly SndbConnectionStringBuilder _builder;
    private HttpClient? _http;
    private FrameChannel? _frames;
    private SonnetMqStore? _embedded;
    private string _database = string.Empty;
    private bool _disposed;
    private int _nextStreamId;

    /// <summary>
    /// 使用 SonnetDB 连接字符串创建 MQ 客户端。
    /// </summary>
    /// <param name="connectionString">SonnetDB 连接字符串。</param>
    public SndbMqClient(string connectionString)
    {
        _builder = new SndbConnectionStringBuilder(connectionString);
        Open();
    }

    /// <summary>
    /// 当前连接模式。
    /// </summary>
    public SndbProviderMode ProviderMode => _builder.ResolveMode();

    /// <summary>
    /// 远程数据库名或嵌入式数据目录。
    /// </summary>
    public string Database => _database;

    /// <summary>
    /// 发布消息。
    /// </summary>
    /// <param name="topic">Topic 名称。</param>
    /// <param name="payload">消息体。</param>
    /// <param name="headers">可选消息头。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>消息 offset。</returns>
    public async Task<long> PublishAsync(
        string topic,
        byte[] payload,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();

        if (_embedded is not null)
            return _embedded.Publish(topic, payload, new SonnetMqPublishOptions(headers));

        if (_frames is { } fx && fx.ShouldTryFrames())
        {
            var w = new ArrayBufferWriter<byte>();
            MqFrameCodec.EncodePublishRequest(w, NextStreamId(), _database, topic, headers, payload);
            var frame = await fx.SendUnaryAsync(w.WrittenMemory, cancellationToken, allowFallback: false).ConfigureAwait(false);
            if (frame is { } f)
                return ValidatePublishResponse(topic, MqFrameCodec.DecodePublishResponse(f.Payload));
        }

        using var response = await PostJsonAsync(
            MqUrl(topic, "publish"),
            new MqPublishRequest(payload, headers),
            RemoteJsonContext.Default.MqPublishRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.MqPublishResponse, cancellationToken).ConfigureAwait(false);
        return ValidatePublishResponse(topic, body);
    }

    /// <summary>
    /// 批量发布同一 topic 下的多条消息，共享一次刷盘。
    /// </summary>
    /// <param name="topic">Topic 名称。</param>
    /// <param name="messages">消息集合，按顺序分配连续 offset。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按输入顺序分配的 offset。</returns>
    public async Task<IReadOnlyList<long>> PublishManyAsync(
        string topic,
        IReadOnlyList<SndbMqPublishEntry> messages,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();
        if (messages.Count == 0)
            return [];

        if (_embedded is not null)
        {
            SonnetMqPublishEntry[] entries = MaterializePublishEntries(messages, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            return _embedded.PublishMany(topic, entries);
        }

        if (_frames is { } fx && fx.ShouldTryFrames())
        {
            SonnetMqPublishEntry[] frameEntries = MaterializePublishEntries(messages, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var w = new ArrayBufferWriter<byte>();
            MqFrameCodec.EncodePublishBatchRequest(w, NextStreamId(), _database, topic, frameEntries);
            var frame = await fx.SendUnaryAsync(w.WrittenMemory, cancellationToken, allowFallback: false).ConfigureAwait(false);
            if (frame is { } f)
                return ValidatePublishBatchResponse(messages.Count, MqFrameCodec.DecodePublishBatchResponse(f.Payload));
        }

        SonnetMqPublishEntry[] entriesForRest = MaterializePublishEntries(messages, cancellationToken);
        var payload = new MqPublishBatchEntry[entriesForRest.Length];
        for (int i = 0; i < entriesForRest.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SonnetMqPublishEntry message = entriesForRest[i];
            payload[i] = new MqPublishBatchEntry(message.Payload.ToArray(), message.Headers);
        }

        using var response = await PostJsonAsync(
            MqUrl(topic, "publish-batch"),
            new MqPublishBatchRequest(payload),
            RemoteJsonContext.Default.MqPublishBatchRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.MqPublishBatchResponse, cancellationToken).ConfigureAwait(false);
        ValidateTopic(topic, body.Topic, "publish-batch");
        return ValidatePublishBatchResponse(messages.Count, body.Offsets);
    }

    /// <summary>
    /// 拉取消息。
    /// </summary>
    /// <param name="topic">Topic 名称。</param>
    /// <param name="consumerGroup">消费者组名称。</param>
    /// <param name="maxCount">最多拉取消息数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>消息列表。</returns>
    public async Task<IReadOnlyList<SndbMqMessage>> PullAsync(
        string topic,
        string consumerGroup,
        int maxCount = 100,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        cancellationToken.ThrowIfCancellationRequested();

        if (_embedded is not null)
        {
            var messages = _embedded.Pull(topic, consumerGroup, maxCount)
                .Select(static message => new SndbMqMessage(message.Topic, message.Offset, message.TimestampUtc, message.Headers, message.Payload))
                .ToArray();
            return ValidatePulledMessages(topic, messages, maxCount);
        }

        if (_frames is { } fx && fx.ShouldTryFrames())
        {
            var w = new ArrayBufferWriter<byte>();
            MqFrameCodec.EncodePullRequest(w, NextStreamId(), _database, topic, consumerGroup, maxCount);
            var frame = await fx.SendUnaryAsync(w.WrittenMemory, cancellationToken).ConfigureAwait(false);
            if (frame is { } f)
            {
                var messages = MqFrameCodec.DecodePullResponse(f.Payload, topic)
                    .Select(static message => new SndbMqMessage(message.Topic, message.Offset, message.TimestampUtc, message.Headers, message.Payload))
                    .ToArray();
                return ValidatePulledMessages(topic, messages, maxCount);
            }
        }

        using var response = await PostJsonAsync(
            MqUrl(topic, "pull"),
            new MqPullRequest(consumerGroup, maxCount),
            RemoteJsonContext.Default.MqPullRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.MqPullResponse, cancellationToken).ConfigureAwait(false);
        var remoteMessages = body.Messages
            .Select(static message => new SndbMqMessage(message.Topic, message.Offset, message.TimestampUtc, message.Headers, message.Payload))
            .ToArray();
        return ValidatePulledMessages(topic, remoteMessages, maxCount);
    }

    /// <summary>
    /// 确认消费者组已处理到指定 offset。
    /// </summary>
    /// <param name="topic">Topic 名称。</param>
    /// <param name="consumerGroup">消费者组名称。</param>
    /// <param name="offset">已处理完成的最后一条 offset。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>消费者组下一条待消费 offset。</returns>
    public async Task<long> AckAsync(
        string topic,
        string consumerGroup,
        long offset,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        cancellationToken.ThrowIfCancellationRequested();

        if (_embedded is not null)
            return ValidateAckResponse(_embedded.Ack(topic, consumerGroup, offset));

        if (_frames is { } fx && fx.ShouldTryFrames())
        {
            var w = new ArrayBufferWriter<byte>();
            MqFrameCodec.EncodeAckRequest(w, NextStreamId(), _database, topic, consumerGroup, offset);
            var frame = await fx.SendUnaryAsync(w.WrittenMemory, cancellationToken, allowFallback: false).ConfigureAwait(false);
            if (frame is { } f)
                return ValidateAckResponse(MqFrameCodec.DecodeAckResponse(f.Payload));
        }

        using var response = await PostJsonAsync(
            MqUrl(topic, "ack"),
            new MqAckRequest(consumerGroup, offset),
            RemoteJsonContext.Default.MqAckRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.MqAckResponse, cancellationToken).ConfigureAwait(false);
        ValidateTopic(topic, body.Topic, "ack");
        return ValidateAckResponse(body.NextOffset);
    }

    /// <summary>
    /// 获取 Topic 统计。
    /// </summary>
    /// <param name="topic">Topic 名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统计快照。</returns>
    public async Task<SndbMqStats> GetStatsAsync(string topic, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        cancellationToken.ThrowIfCancellationRequested();

        if (_embedded is not null)
        {
            var stats = _embedded.GetStats(topic);
            ValidateTopic(topic, stats.Topic, "stats");
            if (stats.MessageCount < 0 || stats.NextOffset < 0
                || stats.ConsumerOffsets.Any(static pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0))
                throw new InvalidDataException("MQ stats 响应包含负数或无效消费者 offset。");
            return new SndbMqStats(stats.Topic, stats.MessageCount, stats.NextOffset, stats.ConsumerOffsets);
        }

        using var response = await PostJsonAsync(
            MqUrl(topic, "stats"),
            new MqPullRequest("_stats", 1),
            RemoteJsonContext.Default.MqPullRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.MqStatsResponse, cancellationToken).ConfigureAwait(false);
        ValidateTopic(topic, body.Topic, "stats");
        ValidateStats(body);
        return new SndbMqStats(body.Topic, body.MessageCount, body.NextOffset, body.ConsumerOffsets);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _http?.Dispose();
        if (_embedded is { } embedded)
            SharedSndbMqRegistry.Release(embedded);
    }

    private void Open()
    {
        if (_builder.ResolveMode() == SndbProviderMode.Embedded)
        {
            if (string.IsNullOrWhiteSpace(_builder.DataSource))
                throw new InvalidOperationException("MQ 客户端缺少 Data Source。");

            var dataSource = _builder.ResolveEmbeddedDataSource();
            _database = dataSource;
            _embedded = SharedSndbMqRegistry.Acquire(new SonnetMqOptions { Path = Path.Combine(dataSource, ".system", "mq") });
            return;
        }

        var baseUrl = _builder.ResolveBaseUrl();
        _database = _builder.ResolveDatabase();
        if (string.IsNullOrWhiteSpace(_database))
            throw new InvalidOperationException("远程 MQ 客户端缺少数据库名。");

        var protocol = _builder.ResolveProtocol();
        _http = RemoteHttpClientFactory.Create(
            new Uri(baseUrl, UriKind.Absolute),
            _builder.Username,
            _builder.Password,
            _builder.Token,
            TimeSpan.FromSeconds(_builder.Timeout),
            allowAutoRedirect: false);
        if (protocol == SndbTransportProtocol.FrameHttp2)
        {
            _http.DefaultRequestVersion = HttpVersion.Version20;
            _http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }
        _frames = new FrameChannel(_http, protocol);
    }

    private async Task<HttpResponseMessage> PostJsonAsync<T>(
        string url,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        if (_http is null)
            throw new InvalidOperationException("远程连接未打开。");

        using var content = JsonContent.Create(value, typeInfo);
        var response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            using (response)
                throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);
        }
        return response;
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("SonnetDB MQ response body is empty.");
    }

    private static async Task<SndbServerException> BuildHttpErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var error = await JsonSerializer.DeserializeAsync(stream, RemoteJsonContext.Default.ServerErrorBody, cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
                return new SndbServerException(error.Error, error.Message, response.StatusCode);
        }
        catch (JsonException)
        {
            // 非 JSON 错误保留 HTTP 状态；取消和 I/O 异常必须传播。
        }

        return new SndbServerException("http_error", response.ReasonPhrase ?? "SonnetDB HTTP error.", response.StatusCode);
    }

    private string MqUrl(string topic, string action) =>
        $"v1/db/{Uri.EscapeDataString(_database)}/mq/{Uri.EscapeDataString(topic)}/{action}";

    private uint NextStreamId()
    {
        int value = Interlocked.Increment(ref _nextStreamId);
        if (value <= 0)
        {
            Interlocked.Exchange(ref _nextStreamId, 1);
            value = 1;
        }

        return (uint)value;
    }

    private static long ValidatePublishResponse(string topic, MqPublishResponse response)
    {
        ValidateTopic(topic, response.Topic, "publish");
        ArgumentOutOfRangeException.ThrowIfNegative(response.Offset);
        return response.Offset;
    }

    private static long ValidatePublishResponse(string topic, long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return offset;
    }

    private static IReadOnlyList<long> ValidatePublishBatchResponse(
        int expectedCount,
        IReadOnlyList<long> offsets)
    {
        ArgumentNullException.ThrowIfNull(offsets);
        if (offsets.Count != expectedCount)
            throw new InvalidDataException($"MQ publish-batch 响应 offset 数量 {offsets.Count} 与请求 {expectedCount} 不一致。");

        long previous = -1;
        foreach (long offset in offsets)
        {
            if (offset < 0 || (previous >= 0 && offset != previous + 1))
                throw new InvalidDataException("MQ publish-batch 响应 offset 必须为非负连续序列；结果未知。");
            previous = offset;
        }

        return offsets;
    }

    private static SonnetMqPublishEntry[] MaterializePublishEntries(
        IReadOnlyList<SndbMqPublishEntry> messages,
        CancellationToken cancellationToken)
    {
        var entries = new SonnetMqPublishEntry[messages.Count];
        for (int i = 0; i < messages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SndbMqPublishEntry? message = messages[i];
            if (message is null)
                throw new ArgumentException("批量消息不能包含 null。", nameof(messages));
            entries[i] = new SonnetMqPublishEntry(message.Payload, message.Headers);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return entries;
    }

    private static IReadOnlyList<SndbMqMessage> ValidatePulledMessages(
        string topic,
        IReadOnlyList<SndbMqMessage> messages,
        int maxCount)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count > maxCount)
            throw new InvalidDataException($"MQ pull 响应消息数 {messages.Count} 超过请求上限 {maxCount}。");

        long previous = -1;
        foreach (SndbMqMessage message in messages)
        {
            ValidateTopic(topic, message.Topic, "pull");
            if (message.Offset < 0 || (previous >= 0 && message.Offset <= previous))
                throw new InvalidDataException("MQ pull 响应 offset 必须按严格升序排列。");
            previous = message.Offset;
        }

        return messages;
    }

    private static long ValidateAckResponse(long nextOffset)
    {
        if (nextOffset < 0)
            throw new InvalidDataException("MQ ack 响应 nextOffset 不能为负数；结果未知。");
        return nextOffset;
    }

    private static void ValidateTopic(string requestedTopic, string responseTopic, string operation)
    {
        if (!string.Equals(requestedTopic, responseTopic, StringComparison.Ordinal))
            throw new InvalidDataException($"MQ {operation} 响应 topic 与请求目标不一致；结果未知。");
    }

    private static void ValidateStats(MqStatsResponse response)
    {
        if (response.MessageCount < 0 || response.NextOffset < 0 || response.ConsumerOffsets is null
            || response.ConsumerOffsets.Any(static pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0))
            throw new InvalidDataException("MQ stats 响应包含负数或无效消费者 offset。");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
