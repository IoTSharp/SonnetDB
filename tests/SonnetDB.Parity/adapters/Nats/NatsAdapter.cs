using System.Net.Sockets;
using System.Collections.Concurrent;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace SonnetDB.Parity.Adapters.Nats;

/// <summary>
/// NATS JetStream 竞品适配器，使用官方 <c>NATS.Client.Core</c> 与 <c>NATS.Client.JetStream</c> 客户端实现 MQ 场景。
/// </summary>
public sealed class NatsAdapter : IDataPlane, IMqOps
{
    private NatsConnection _connection;
    private INatsJSContext _js;
    private readonly ConcurrentDictionary<string, long> _consumerOffsets = new(StringComparer.Ordinal);

    /// <summary>使用 <c>PARITY_NATS_*</c> 环境变量创建 NATS 连接。</summary>
    public NatsAdapter()
    {
        _connection = CreateConnection();
        _js = new NatsJSContext(_connection);
    }

    /// <inheritdoc />
    public string BackendName => "nats";

    /// <inheritdoc />
    public Capability Capabilities => Capability.Mq | Capability.MqConsumerGroup | Capability.MqReplayFromOffset;

    /// <inheritdoc />
    public IRelationalOps Relational => throw new NotSupportedException("NATS 适配器不支持关系型操作。");

    /// <inheritdoc />
    public ITimeSeriesOps TimeSeries => UnsupportedTimeSeriesOps.Instance;

    /// <inheritdoc />
    public IKvOps Kv => UnsupportedKvOps.Instance;

    /// <inheritdoc />
    public IObjectOps Objects => UnsupportedObjectOps.Instance;

    /// <inheritdoc />
    public IVectorOps Vector => UnsupportedVectorOps.Instance;

    /// <inheritdoc />
    public IMqOps Mq => this;

    /// <inheritdoc />
    public IFullTextOps FullText => UnsupportedFullTextOps.Instance;

    /// <inheritdoc />
    public IAnalyticalOps Analytics => UnsupportedAnalyticalOps.Instance;

    /// <summary>探测 NATS 是否可达。</summary>
    public static async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = CreateConnection();
            await connection.PingAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is NatsException or SocketException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ResetTopicAsync(string topic, CancellationToken ct)
    {
        foreach (string key in _consumerOffsets.Keys.Where(k => k.StartsWith(topic + "|", StringComparison.Ordinal)).ToArray())
            _consumerOffsets.TryRemove(key, out _);

        string stream = StreamName(topic);
        try { await _js.DeleteStreamAsync(stream, ct).ConfigureAwait(false); }
        catch (NatsJSApiException) { }
        catch (NatsJSException) { }

        await _js.CreateOrUpdateStreamAsync(
            new StreamConfig
            {
                Name = stream,
                Subjects = [topic],
                Storage = StreamConfigStorage.File,
                Retention = StreamConfigRetention.Limits,
                MaxMsgs = -1,
                MaxBytes = -1,
                MaxAge = TimeSpan.Zero,
                MaxMsgSize = -1,
                NumReplicas = 1,
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> PublishAsync(string topic, byte[] payload, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        var ack = await _js.PublishAsync(topic, payload, headers: headers is null ? null : ToHeaders(headers), cancellationToken: ct).ConfigureAwait(false);
        return checked((long)ack.Seq - 1L);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> PublishManyAsync(string topic, IReadOnlyList<MqPublishRecord> records, CancellationToken ct)
    {
        var offsets = new long[records.Count];
        for (int i = 0; i < records.Count; i++)
            offsets[i] = await PublishAsync(topic, records[i].Payload, records[i].Headers, ct).ConfigureAwait(false);
        return offsets;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MqMessageRecord>> PullAsync(string topic, string consumerGroup, int maxCount, CancellationToken ct)
    {
        long nextOffset = _consumerOffsets.GetOrAdd(ConsumerOffsetKey(topic, consumerGroup), 0);
        var consumer = await GetOrCreateConsumerAsync(topic, "pull-" + consumerGroup + "-" + Guid.NewGuid().ToString("N"), nextOffset + 1, ct).ConfigureAwait(false);
        return await FetchAsync(topic, consumer, maxCount, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MqMessageRecord>> ReplayAsync(string topic, long offset, int maxCount, CancellationToken ct)
    {
        var consumer = await GetOrCreateConsumerAsync(topic, "replay-" + Guid.NewGuid().ToString("N"), offset + 1, ct).ConfigureAwait(false);
        return await FetchAsync(topic, consumer, maxCount, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> AckAsync(string topic, string consumerGroup, long offset, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string key = ConsumerOffsetKey(topic, consumerGroup);
        long next = offset + 1;
        _consumerOffsets.AddOrUpdate(key, next, (_, current) => Math.Max(current, next));
        return next;
    }

    /// <inheritdoc />
    public async Task RestartAsync(CancellationToken ct)
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _connection = CreateConnection();
        _js = new NatsJSContext(_connection);
        await _connection.PingAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<INatsJSConsumer> GetOrCreateConsumerAsync(string topic, string consumerGroup, long? startSequence, CancellationToken ct)
    {
        var config = new ConsumerConfig
        {
            DurableName = SanitizeConsumerName(consumerGroup),
            Name = SanitizeConsumerName(consumerGroup),
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            DeliverPolicy = startSequence.HasValue ? ConsumerConfigDeliverPolicy.ByStartSequence : ConsumerConfigDeliverPolicy.All,
            OptStartSeq = checked((ulong)(startSequence ?? 0)),
            FilterSubject = topic,
            MaxAckPending = 100_000,
        };
        return await _js.CreateOrUpdateConsumerAsync(StreamName(topic), config, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<MqMessageRecord>> FetchAsync(
        string topic,
        INatsJSConsumer consumer,
        int maxCount,
        CancellationToken ct)
    {
        var rows = new List<MqMessageRecord>(maxCount);
        await foreach (var msg in consumer.FetchNoWaitAsync<byte[]>(new NatsJSFetchOpts { MaxMsgs = maxCount }, cancellationToken: ct).ConfigureAwait(false))
        {
            var metadata = msg.Metadata ?? throw new InvalidOperationException("NATS JetStream message metadata is missing.");
            long offset = checked((long)metadata.Sequence.Stream - 1L);
            rows.Add(new MqMessageRecord(
                topic,
                offset,
                metadata.Timestamp,
                FromHeaders(msg.Headers),
                msg.Data ?? []));
            await msg.AckAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        return rows;
    }

    private static string ConsumerOffsetKey(string topic, string consumerGroup)
        => topic + "|" + consumerGroup;

    private static NatsConnection CreateConnection()
        => new(new NatsOpts { Url = Env("PARITY_NATS_URL", "nats://127.0.0.1:" + Env("PARITY_NATS_PORT", "24222")) });

    private static string StreamName(string topic)
        => "S_" + SanitizeConsumerName(topic);

    private static string SanitizeConsumerName(string value)
    {
        var chars = value.Select(static ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        string name = new(chars);
        return name.Length <= 200 ? name : name[..200];
    }

    private static NatsHeaders ToHeaders(IReadOnlyDictionary<string, string> headers)
    {
        var result = new NatsHeaders();
        foreach (var pair in headers)
            result[pair.Key] = pair.Value;
        return result;
    }

    private static IReadOnlyDictionary<string, string> FromHeaders(NatsHeaders? headers)
    {
        if (headers is null || headers.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        return headers.ToDictionary(static h => h.Key, static h => h.Value.ToString(), StringComparer.Ordinal);
    }

    private static string Env(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
