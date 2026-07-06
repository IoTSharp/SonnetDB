using System.Buffers;
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

        if (_embedded is not null)
            return _embedded.Publish(topic, payload, new SonnetMqPublishOptions(headers));

        if (_frames is { } fx && fx.ShouldTryFrames())
        {
            var w = new ArrayBufferWriter<byte>();
            MqFrameCodec.EncodePublishRequest(w, 1, _database, topic, headers, payload);
            var frame = await fx.SendUnaryAsync(w.WrittenMemory, cancellationToken).ConfigureAwait(false);
            if (frame is { } f)
                return MqFrameCodec.DecodePublishResponse(f.Payload);
        }

        using var response = await PostJsonAsync(
            MqUrl(topic, "publish"),
            new MqPublishRequest(payload, headers),
            RemoteJsonContext.Default.MqPublishRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.MqPublishResponse, cancellationToken).ConfigureAwait(false);
        return body.Offset;
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
        if (messages.Count == 0)
            return [];

        if (_embedded is not null)
        {
            var entries = new SonnetMqPublishEntry[messages.Count];
            for (int i = 0; i < messages.Count; i++)
            {
                var message = messages[i] ?? throw new ArgumentException("批量消息不能包含 null。", nameof(messages));
                entries[i] = new SonnetMqPublishEntry(message.Payload, message.Headers);
            }

            return _embedded.PublishMany(topic, entries);
        }

        if (_frames is { } fx && fx.ShouldTryFrames())
        {
            var frameEntries = new SonnetMqPublishEntry[messages.Count];
            for (int i = 0; i < messages.Count; i++)
            {
                var message = messages[i] ?? throw new ArgumentException("批量消息不能包含 null。", nameof(messages));
                frameEntries[i] = new SonnetMqPublishEntry(message.Payload, message.Headers);
            }

            var w = new ArrayBufferWriter<byte>();
            MqFrameCodec.EncodePublishBatchRequest(w, 1, _database, topic, frameEntries);
            var frame = await fx.SendUnaryAsync(w.WrittenMemory, cancellationToken).ConfigureAwait(false);
            if (frame is { } f)
                return MqFrameCodec.DecodePublishBatchResponse(f.Payload);
        }

        var payload = new MqPublishBatchEntry[messages.Count];
        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i] ?? throw new ArgumentException("批量消息不能包含 null。", nameof(messages));
            payload[i] = new MqPublishBatchEntry(message.Payload.ToArray(), message.Headers);
        }

        using var response = await PostJsonAsync(
            MqUrl(topic, "publish-batch"),
            new MqPublishBatchRequest(payload),
            RemoteJsonContext.Default.MqPublishBatchRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.MqPublishBatchResponse, cancellationToken).ConfigureAwait(false);
        return body.Offsets;
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

        if (_embedded is not null)
            return _embedded.Pull(topic, consumerGroup, maxCount)
                .Select(static message => new SndbMqMessage(message.Topic, message.Offset, message.TimestampUtc, message.Headers, message.Payload))
                .ToArray();

        if (_frames is { } fx && fx.ShouldTryFrames())
        {
            var w = new ArrayBufferWriter<byte>();
            MqFrameCodec.EncodePullRequest(w, 1, _database, topic, consumerGroup, maxCount);
            var frame = await fx.SendUnaryAsync(w.WrittenMemory, cancellationToken).ConfigureAwait(false);
            if (frame is { } f)
                return MqFrameCodec.DecodePullResponse(f.Payload, topic)
                    .Select(static message => new SndbMqMessage(message.Topic, message.Offset, message.TimestampUtc, message.Headers, message.Payload))
                    .ToArray();
        }

        using var response = await PostJsonAsync(
            MqUrl(topic, "pull"),
            new MqPullRequest(consumerGroup, maxCount),
            RemoteJsonContext.Default.MqPullRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.MqPullResponse, cancellationToken).ConfigureAwait(false);
        return body.Messages
            .Select(static message => new SndbMqMessage(message.Topic, message.Offset, message.TimestampUtc, message.Headers, message.Payload))
            .ToArray();
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

        if (_embedded is not null)
            return _embedded.Ack(topic, consumerGroup, offset);

        if (_frames is { } fx && fx.ShouldTryFrames())
        {
            var w = new ArrayBufferWriter<byte>();
            MqFrameCodec.EncodeAckRequest(w, 1, _database, topic, consumerGroup, offset);
            var frame = await fx.SendUnaryAsync(w.WrittenMemory, cancellationToken).ConfigureAwait(false);
            if (frame is { } f)
                return MqFrameCodec.DecodeAckResponse(f.Payload);
        }

        using var response = await PostJsonAsync(
            MqUrl(topic, "ack"),
            new MqAckRequest(consumerGroup, offset),
            RemoteJsonContext.Default.MqAckRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.MqAckResponse, cancellationToken).ConfigureAwait(false);
        return body.NextOffset;
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

        if (_embedded is not null)
        {
            var stats = _embedded.GetStats(topic);
            return new SndbMqStats(stats.Topic, stats.MessageCount, stats.NextOffset, stats.ConsumerOffsets);
        }

        using var response = await PostJsonAsync(
            MqUrl(topic, "stats"),
            new MqPullRequest("_stats", 1),
            RemoteJsonContext.Default.MqPullRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.MqStatsResponse, cancellationToken).ConfigureAwait(false);
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

            _database = _builder.DataSource;
            _embedded = SharedSndbMqRegistry.Acquire(new SonnetMqOptions { Path = Path.Combine(_builder.DataSource, ".system", "mq") });
            return;
        }

        var (baseUrl, dbFromUrl) = ParseRemoteEndpoint(_builder.DataSource);
        _database = !string.IsNullOrWhiteSpace(_builder.Database) ? _builder.Database! : dbFromUrl;
        if (string.IsNullOrWhiteSpace(_database))
            throw new InvalidOperationException("远程 MQ 客户端缺少数据库名。");

        _http = RemoteHttpClientFactory.Create(
            new Uri(baseUrl, UriKind.Absolute),
            _builder.Token,
            TimeSpan.FromSeconds(_builder.Timeout));
        _frames = new FrameChannel(_http, _builder.ResolveProtocol());
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
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);
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
        catch
        {
        }

        return new SndbServerException("http_error", response.ReasonPhrase ?? "SonnetDB HTTP error.", response.StatusCode);
    }

    private string MqUrl(string topic, string action) =>
        $"v1/db/{Uri.EscapeDataString(_database)}/mq/{Uri.EscapeDataString(topic)}/{action}";

    private static (string BaseUrl, string Database) ParseRemoteEndpoint(string dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
            throw new InvalidOperationException("远程 MQ 客户端缺少 Data Source。");

        var ds = dataSource.Trim();
        if (ds.StartsWith("sonnetdb+http://", StringComparison.OrdinalIgnoreCase))
            ds = "http://" + ds["sonnetdb+http://".Length..];
        else if (ds.StartsWith("sonnetdb+https://", StringComparison.OrdinalIgnoreCase))
            ds = "https://" + ds["sonnetdb+https://".Length..];

        if (!Uri.TryCreate(ds, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"远程 Data Source 不是合法 URL: {dataSource}");

        return ($"{uri.Scheme}://{uri.Authority}/", uri.AbsolutePath.Trim('/'));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
