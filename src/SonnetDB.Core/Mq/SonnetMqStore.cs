using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SonnetMQ;

/// <summary>
/// 零依赖、本地 append-only 消息队列。
/// </summary>
public sealed class SonnetMqStore : IDisposable
{
    private const uint Magic = 0x514D_4E53; // SNMQ little-endian
    private const ushort Version = 1;
    private const byte RecordTypeMessage = 1;
    private const byte RecordTypeAck = 2;
    private const byte RecordTypeTombstone = 3;
    private const int HeaderSize = 36;
    private const int MaxNameBytes = 512;
    private const int MaxHeadersBytes = 64 * 1024;
    private const int MaxPayloadBytes = 128 * 1024 * 1024;
    private const int SegmentFileNameWidth = 20;

    private readonly object _globalSync = new();
    private readonly SonnetMqOptions _options;
    private readonly ConcurrentDictionary<string, TopicState> _topics = new(StringComparer.Ordinal);
    private readonly object? _singleFileLock;
    private readonly int _offsetIndexStride;
    private readonly long _residentHotTailMaxBytes;
    private readonly SegmentHandleCache _handleCache;
    private Thread? _retentionWorker;
    private CancellationTokenSource? _retentionCts;
    private FileStream? _singleFileStream;
    private bool _disposed;

    private SonnetMqStore(SonnetMqOptions options)
    {
        _options = options;
        _offsetIndexStride = Math.Max(1, options.OffsetIndexStride);
        // 单文件模式（共享单流、无 per-topic 段边界）保持全量常驻，热尾上限视为无穷；目录模式启用有界热尾。
        _residentHotTailMaxBytes = options.OpenMode == SonnetMqOpenMode.SingleFile
            ? long.MaxValue
            : Math.Max(1, options.HotTailMaxBytes);
        // 单文件模式下所有 topic 共享同一个 FileStream，必须串行化到同一把锁（并复用为 Dispose/Flush 的全局锁）；
        // 目录模式下每个 topic 各持一把锁（见 TopicState.SyncRoot），topic 间互不阻塞。
        _singleFileLock = options.OpenMode == SonnetMqOpenMode.SingleFile ? _globalSync : null;
        _handleCache = new SegmentHandleCache(Math.Max(1, options.SegmentCacheSize));
    }

    /// <summary>
    /// 打开或创建本地 SonnetMQ 队列。
    /// </summary>
    /// <param name="options">打开选项。</param>
    /// <returns>已加载历史记录的队列实例。</returns>
    public static SonnetMqStore Open(SonnetMqOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Path);
        if (options.SegmentMaxBytes <= HeaderSize)
            throw new ArgumentOutOfRangeException(nameof(options), "SegmentMaxBytes 必须大于单条记录头长度。");
        if (options.SegmentCacheSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "SegmentCacheSize 必须为正数。");

        var store = new SonnetMqStore(options);
        try
        {
            if (options.OpenMode == SonnetMqOpenMode.SingleFile)
                store.OpenSingleFile();
            else
                Directory.CreateDirectory(store.RootDirectory);

            store.Replay();
            store.SeekWritableSegmentsToEnd();
            store.TrimRetention();
            store.StartRetentionWorkerIfNeeded();
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 发布一条消息。
    /// </summary>
    /// <param name="topic">Topic 名称。</param>
    /// <param name="payload">消息体。</param>
    /// <param name="options">发布选项。</param>
    /// <returns>分配给该消息的 offset。</returns>
    public long Publish(string topic, ReadOnlySpan<byte> payload, SonnetMqPublishOptions? options = null)
    {
        EnsureNotDisposed();
        ValidateTopic(topic);
        if (payload.Length > MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, "消息体超过 SonnetMQ 当前单条大小上限。");

        var headers = options?.Headers ?? EmptyHeaders.Instance;
        // 单次拷贝：span → 常驻数组，直接入 prepared，不再经 entry 二次 ToArray（MQ2）。
        var prepared = new PreparedPublish(payload.ToArray(), EncodeHeaders(headers), SnapshotHeaders(headers));
        return PublishPrepared(topic, [prepared])[0];
    }

    /// <summary>
    /// 批量发布同一 Topic 下的多条消息。
    /// </summary>
    /// <param name="topic">Topic 名称。</param>
    /// <param name="entries">消息集合。调用方可为每条消息提供独立 headers。</param>
    /// <returns>按输入顺序返回分配后的 offset。</returns>
    public IReadOnlyList<long> PublishMany(string topic, IReadOnlyList<SonnetMqPublishEntry> entries)
    {
        EnsureNotDisposed();
        ValidateTopic(topic);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            return [];

        var prepared = new PreparedPublish[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i] ?? throw new ArgumentException("批量发布消息不能包含 null。", nameof(entries));
            if (entry.Payload.Length > MaxPayloadBytes)
                throw new ArgumentOutOfRangeException(nameof(entries), entry.Payload.Length, "消息体超过 SonnetMQ 当前单条大小上限。");

            var headers = entry.Headers ?? EmptyHeaders.Instance;
            prepared[i] = new PreparedPublish(
                entry.Payload.ToArray(),
                EncodeHeaders(headers),
                SnapshotHeaders(headers));
        }

        return PublishPrepared(topic, prepared);
    }

    private IReadOnlyList<long> PublishPrepared(string topic, PreparedPublish[] prepared)
    {
        byte[] topicBytes = EncodeName(topic, nameof(topic));
        var state = GetOrCreateTopic(topic);

        bool groupCommit = _options.GroupCommitPublish
            && _options.OpenMode != SonnetMqOpenMode.SingleFile
            && (_options.FlushOnPublish || _options.SyncOnPublish);

        long mySeq;
        long[] offsets;
        lock (state.SyncRoot)
        {
            offsets = new long[prepared.Length];
            for (int i = 0; i < prepared.Length; i++)
            {
                var publish = prepared[i];
                long offset = state.NextOffset;
                var timestamp = DateTimeOffset.UtcNow;
                var location = WriteRecordAt(state, RecordTypeMessage, topicBytes, publish.HeadersBytes, publish.Payload, offset, timestamp.UtcTicks, flush: false);
                state.Append(
                    new StoredMessage(topic, offset, timestamp, publish.Headers, publish.Payload, ColdReadable: location.SegmentBaseOffset >= 0),
                    location,
                    _offsetIndexStride,
                    _residentHotTailMaxBytes);
                offsets[i] = offset;
            }

            state.AppendedSeq += prepared.Length;
            mySeq = state.AppendedSeq;

            // 未启用组提交（含单文件模式）：沿用逐次内联刷盘，锁内完成，语义与逐条刷盘一致。
            if (!groupCommit)
                FlushPublishBatchIfNeeded(state);
        }

        // 组提交 leader-flush：在 SyncRoot 外合并刷盘，仅刷盘瞬间借回 SyncRoot 序列化于写入者。
        if (groupCommit)
            CommitPublishFlush(state, mySeq);

        // 唤醒推送订阅者（#236）：刷盘完成后、SyncRoot 外，避免被唤醒者立刻回争锁。虚假/重复 pulse 无害。
        state.Pulse();
        return offsets;
    }

    private void CommitPublishFlush(TopicState state, long mySeq)
    {
        // 快路径：我的字节已被此前某个 leader 刷盘覆盖，直接返回（零系统调用）。
        if (Volatile.Read(ref state.FlushedSeq) >= mySeq)
            return;

        lock (state.FlushRoot)
        {
            // 等待 FlushRoot 期间已被覆盖：并发发布者的刷盘顺带带走了我的字节。
            if (state.FlushedSeq >= mySeq)
                return;

            lock (state.SyncRoot)
            {
                long target = state.AppendedSeq;
                state.Writer?.Flush(_options.SyncOnPublish);
                Volatile.Write(ref state.FlushedSeq, target);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> SnapshotHeaders(IReadOnlyDictionary<string, string> headers)
        => headers.Count == 0
            ? EmptyHeaders.Instance
            : new Dictionary<string, string>(headers, StringComparer.Ordinal);

    /// <summary>
    /// 读取指定消费者组尚未确认的消息。
    /// </summary>
    /// <param name="topic">Topic 名称。</param>
    /// <param name="consumerGroup">消费者组名称。</param>
    /// <param name="maxCount">最多返回消息数。</param>
    /// <returns>按 offset 升序排列的消息。</returns>
    public IReadOnlyList<SonnetMqMessage> Pull(string topic, string consumerGroup, int maxCount)
    {
        EnsureNotDisposed();
        ValidateTopic(topic);
        ValidateConsumerGroup(consumerGroup);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);

        if (!_topics.TryGetValue(topic, out var state))
            return [];

        lock (state.SyncRoot)
        {
            long next = state.GetConsumerOffset(consumerGroup);
            return PullFromState(state, next, maxCount);
        }
    }

    /// <summary>
    /// 从指定 Topic offset 开始读取消息，不改变消费者组提交位置。
    /// </summary>
    /// <param name="topic">Topic 名称。</param>
    /// <param name="offset">起始 offset，包含该 offset。</param>
    /// <param name="maxCount">最多返回消息数。</param>
    /// <returns>按 offset 升序排列的消息。</returns>
    public IReadOnlyList<SonnetMqMessage> Pull(string topic, long offset, int maxCount)
    {
        EnsureNotDisposed();
        ValidateTopic(topic);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);

        if (!_topics.TryGetValue(topic, out var state))
            return [];

        lock (state.SyncRoot)
            return PullFromState(state, offset, maxCount);
    }

    /// <summary>
    /// 异步等待 <paramref name="topic"/> 出现 offset ≥ 有效起点的消息（#236 推送订阅）。
    /// 有效起点 = <c>max(fromOffset, 当前 TrimmedBeforeOffset)</c>，返回该有效起点，
    /// 供调用方以此为游标 <see cref="Pull(string, long, int)"/>——从而穿越 retention gap 不空转。
    /// 已有可读消息时立即返回；否则挂起直至下一次 <see cref="Publish"/>/<see cref="PublishMany"/> 唤醒。
    /// </summary>
    /// <param name="topic">Topic 名称。</param>
    /// <param name="fromOffset">期望的起始 offset（含）。</param>
    /// <param name="cancellationToken">取消令牌（连接关闭时取消，不泄漏等待者）。</param>
    /// <returns>可读起点 offset；从该 offset 起至少有一条消息可 Pull。</returns>
    /// <exception cref="ObjectDisposedException">store 已释放，或释放发生在等待期间。</exception>
    public async ValueTask<long> WaitForMessagesAsync(string topic, long fromOffset, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ValidateTopic(topic);
        ArgumentOutOfRangeException.ThrowIfNegative(fromOffset);

        var state = GetOrCreateTopic(topic);
        while (true)
        {
            Task pulse;
            lock (state.SyncRoot)
            {
                if (state.Faulted)
                    throw new ObjectDisposedException(nameof(SonnetMqStore));
                long effective = Math.Max(fromOffset, state.TrimmedBeforeOffset);
                if (state.NextOffset > effective)
                    return effective;

                // 条件不满足：在同一把锁内取 pulse，与发布路径的 append+Pulse、Dispose 的 FaultWaiters 经 SyncRoot
                // 串行化——故障后新建的 pulse 不会发生（先查 Faulted），发布后不会漏唤醒。
                pulse = state.GetOrCreatePulse().Task;
                fromOffset = effective;
            }

            // WaitAsync 的取消注册随任务完成或取消自动释放，取消一个等待者不影响共享 TCS 的其他等待者。
            await pulse.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 确认消费者组已处理到指定 offset。
    /// </summary>
    /// <param name="topic">Topic 名称。</param>
    /// <param name="consumerGroup">消费者组名称。</param>
    /// <param name="offset">已成功处理的最后一条消息 offset。</param>
    /// <returns>消费者组下一条待消费 offset。</returns>
    public long Ack(string topic, string consumerGroup, long offset)
    {
        EnsureNotDisposed();
        ValidateTopic(topic);
        ValidateConsumerGroup(consumerGroup);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        byte[] topicBytes = EncodeName(topic, nameof(topic));
        byte[] consumerBytes = EncodeName(consumerGroup, nameof(consumerGroup));

        var state = GetOrCreateTopic(topic);
        lock (state.SyncRoot)
        {
            long next = Math.Min(offset + 1, state.NextOffset);
            WriteRecord(state, RecordTypeAck, topicBytes, consumerBytes, ReadOnlySpan<byte>.Empty, next, DateTimeOffset.UtcNow.UtcTicks);
            state.SetConsumerOffset(consumerGroup, next);
            TrimAcknowledgedMessages(state, force: false);
            return next;
        }
    }

    /// <summary>
    /// 写入 retention tombstone，并删除 tombstone 之前的内存消息和可安全清理的旧段。
    /// </summary>
    /// <param name="topic">Topic 名称。</param>
    /// <param name="beforeOffset">裁剪该 offset 之前的消息，不包含该 offset。</param>
    /// <returns>实际保留的第一条 offset。</returns>
    public long TombstoneBefore(string topic, long beforeOffset)
    {
        EnsureNotDisposed();
        ValidateTopic(topic);
        ArgumentOutOfRangeException.ThrowIfNegative(beforeOffset);

        byte[] topicBytes = EncodeName(topic, nameof(topic));
        var state = GetOrCreateTopic(topic);
        lock (state.SyncRoot)
        {
            long cutoff = Math.Min(beforeOffset, state.NextOffset);
            if (cutoff <= state.TrimmedBeforeOffset)
                return state.TrimmedBeforeOffset;

            WriteRecord(state, RecordTypeTombstone, topicBytes, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, cutoff, DateTimeOffset.UtcNow.UtcTicks);
            state.ApplyTombstone(cutoff);
            DeleteRetiredSegments(state);
            return state.TrimmedBeforeOffset;
        }
    }

    /// <summary>
    /// 按当前 time / size retention 配置同步执行一次裁剪。
    /// </summary>
    public void TrimRetention()
    {
        EnsureNotDisposed();
        if (_options.OpenMode == SonnetMqOpenMode.SingleFile)
            return;

        // MQ7：只锁被裁剪的单个 topic，文件系统调用不再阻塞其它 topic 的 publish/pull。
        foreach (var state in _topics.Values)
        {
            lock (state.SyncRoot)
            {
                TrimAcknowledgedMessages(state, force: true);
                TrimTopicRetention(state);
            }
        }
    }

    /// <summary>
    /// 获取 Topic 统计信息。
    /// </summary>
    /// <param name="topic">Topic 名称。</param>
    /// <returns>统计快照；Topic 不存在时返回空统计。</returns>
    public SonnetMqTopicStats GetStats(string topic)
    {
        EnsureNotDisposed();
        ValidateTopic(topic);

        if (!_topics.TryGetValue(topic, out var state))
            return new SonnetMqTopicStats(topic, 0, 0, new Dictionary<string, long>(StringComparer.Ordinal));

        lock (state.SyncRoot)
            return SnapshotStats(state);
    }

    /// <summary>
    /// 枚举当前所有 topic 的统计快照。只读，不改变任何队列状态。
    /// </summary>
    /// <returns>按 topic 名称升序排列的统计快照。</returns>
    public IReadOnlyList<SonnetMqTopicStats> ListTopicStats()
    {
        EnsureNotDisposed();

        var result = new List<SonnetMqTopicStats>(_topics.Count);
        foreach (var state in _topics.Values)
        {
            lock (state.SyncRoot)
                result.Add(SnapshotStats(state));
        }

        result.Sort(static (a, b) => string.CompareOrdinal(a.Topic, b.Topic));
        return result;
    }

    private static SonnetMqTopicStats SnapshotStats(TopicState state)
        => new(
            state.Topic,
            state.NextOffset - state.TrimmedBeforeOffset,
            state.NextOffset,
            new Dictionary<string, long>(state.ConsumerOffsets, StringComparer.Ordinal));

    /// <summary>
    /// 将当前写缓冲刷新到文件。
    /// </summary>
    /// <param name="flushToDisk">是否请求持久化到磁盘。</param>
    public void Flush(bool flushToDisk = false)
    {
        EnsureNotDisposed();
        if (_options.OpenMode == SonnetMqOpenMode.SingleFile)
        {
            lock (_globalSync)
                _singleFileStream?.Flush(flushToDisk);
            return;
        }

        foreach (var state in _topics.Values)
        {
            lock (state.SyncRoot)
                state.Writer?.Flush(flushToDisk);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _retentionCts?.Cancel();
        _retentionWorker?.Join(TimeSpan.FromSeconds(2));
        _retentionCts?.Dispose();

        lock (_globalSync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        foreach (var state in _topics.Values)
        {
            // 在 SyncRoot 内置 Faulted + 故障 pulse（#236）：与等待者的条件检查串行化，杜绝故障后新建 pulse 永不完成。
            lock (state.SyncRoot)
            {
                state.FaultWaiters(new ObjectDisposedException(nameof(SonnetMqStore)));
                state.Dispose();
            }
        }

        _handleCache.Dispose();

        lock (_globalSync)
            _singleFileStream?.Dispose();
    }

    private string RootDirectory => Path.GetFullPath(_options.Path);

    private void OpenSingleFile()
    {
        string logPath = Path.GetFullPath(_options.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        _singleFileStream = OpenLogStream(logPath);
    }

    private void Replay()
    {
        if (_options.OpenMode == SonnetMqOpenMode.SingleFile)
        {
            ReplayStream(_singleFileStream ?? throw new InvalidOperationException("Single-file stream is not open."), null);
            return;
        }

        string legacyLogPath = Path.Combine(RootDirectory, "sonnetmq.log");
        if (File.Exists(legacyLogPath))
        {
            using var legacy = File.Open(legacyLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            ReplayStream(legacy, null);
        }

        foreach (string topicDirectory in Directory.EnumerateDirectories(RootDirectory))
        {
            string topic = DecodeTopicDirectory(Path.GetFileName(topicDirectory));
            var state = GetOrCreateTopic(topic);
            foreach (string segmentPath in EnumerateSegmentPaths(topicDirectory))
            {
                long baseOffset = ParseSegmentBaseOffset(segmentPath);
                state.AddSegment(new SegmentState(segmentPath, baseOffset));
                using var stream = File.Open(segmentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                ReplayStream(stream, state, baseOffset);
            }
        }
    }

    private void ReplayStream(Stream stream, TopicState? knownTopicState, long segmentBaseOffset = -1)
    {
        stream.Seek(0, SeekOrigin.Begin);
        Span<byte> header = stackalloc byte[HeaderSize];
        long recordPosition = 0;

        while (TryReadExact(stream, header))
        {
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
            byte type = header[6];
            int topicLength = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            int metaLength = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
            long offsetOrNext = BinaryPrimitives.ReadInt64LittleEndian(header[20..]);
            long ticks = BinaryPrimitives.ReadInt64LittleEndian(header[28..]);

            if (magic != Magic || version != Version || topicLength < 0 || metaLength < 0 || payloadLength < 0)
                throw new InvalidDataException("SonnetMQ log header is invalid.");
            if (topicLength > MaxNameBytes || metaLength > MaxHeadersBytes || payloadLength > MaxPayloadBytes)
                throw new InvalidDataException("SonnetMQ log record exceeds configured bounds.");

            byte[] topicBytes = ArrayPool<byte>.Shared.Rent(topicLength);
            byte[] metaBytes = ArrayPool<byte>.Shared.Rent(Math.Max(metaLength, 1));
            byte[] payload = ArrayPool<byte>.Shared.Rent(Math.Max(payloadLength, 1));
            try
            {
                ReadExactOrThrow(stream, topicBytes.AsSpan(0, topicLength));
                ReadExactOrThrow(stream, metaBytes.AsSpan(0, metaLength));
                ReadExactOrThrow(stream, payload.AsSpan(0, payloadLength));

                string topic = Encoding.UTF8.GetString(topicBytes.AsSpan(0, topicLength));
                var state = knownTopicState ?? GetOrCreateTopic(topic);

                if (type == RecordTypeMessage)
                {
                    var headers = DecodeHeaders(metaBytes.AsSpan(0, metaLength));
                    var body = payload.AsSpan(0, payloadLength).ToArray();
                    state.Append(
                        new StoredMessage(topic, offsetOrNext, new DateTimeOffset(ticks, TimeSpan.Zero), headers, body, ColdReadable: segmentBaseOffset >= 0),
                        new RecordLocation(segmentBaseOffset, recordPosition),
                        _offsetIndexStride,
                        _residentHotTailMaxBytes);
                }
                else if (type == RecordTypeAck)
                {
                    string consumerGroup = Encoding.UTF8.GetString(metaBytes.AsSpan(0, metaLength));
                    state.SetConsumerOffset(consumerGroup, Math.Min(offsetOrNext, state.NextOffset));
                }
                else if (type == RecordTypeTombstone)
                {
                    state.ApplyTombstone(offsetOrNext);
                }
                else
                {
                    throw new InvalidDataException($"SonnetMQ log record type {type} is not supported.");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(topicBytes);
                ArrayPool<byte>.Shared.Return(metaBytes);
                ArrayPool<byte>.Shared.Return(payload);
            }

            recordPosition += HeaderSize + topicLength + metaLength + payloadLength;
        }
    }

    private void SeekWritableSegmentsToEnd()
    {
        if (_options.OpenMode == SonnetMqOpenMode.SingleFile)
        {
            _singleFileStream?.Seek(0, SeekOrigin.End);
            return;
        }

        foreach (var state in _topics.Values)
            EnsureWriter(state);
    }

    private void WriteRecord(
        TopicState state,
        byte type,
        ReadOnlySpan<byte> topic,
        ReadOnlySpan<byte> meta,
        ReadOnlySpan<byte> payload,
        long offsetOrNext,
        long ticks,
        bool flush = true)
        => WriteRecordAt(state, type, topic, meta, payload, offsetOrNext, ticks, flush);

    /// <summary>
    /// 写入一条记录，返回其在所属段文件内的起始字节位置与所属段 baseOffset（供消息记录填充位置索引）。
    /// 非消息记录（ack/tombstone）调用方忽略返回值。
    /// </summary>
    private RecordLocation WriteRecordAt(
        TopicState state,
        byte type,
        ReadOnlySpan<byte> topic,
        ReadOnlySpan<byte> meta,
        ReadOnlySpan<byte> payload,
        long offsetOrNext,
        long ticks,
        bool flush = true)
    {
        FileStream stream = GetWritableStream(state, HeaderSize + topic.Length + meta.Length + payload.Length);
        // 单文件模式无 per-topic 段边界、不可冷读：SegmentBaseOffset=-1 哨兵（Append 据此跳过位置索引并钉住常驻）。
        long segmentBaseOffset = _options.OpenMode == SonnetMqOpenMode.SingleFile ? -1 : state.Segments[^1].BaseOffset;
        long position = stream.Position;

        // MQ5：把定长头 + topic + meta 合并进一个租借缓冲区一次写出，payload 单独直写（大 payload 免二次拷贝）。
        int prefixLength = HeaderSize + topic.Length + meta.Length;
        byte[] rented = ArrayPool<byte>.Shared.Rent(prefixLength);
        try
        {
            Span<byte> prefix = rented.AsSpan(0, prefixLength);
            BinaryPrimitives.WriteUInt32LittleEndian(prefix, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(prefix[4..], Version);
            prefix[6] = type;
            prefix[7] = 0;
            BinaryPrimitives.WriteInt32LittleEndian(prefix[8..], topic.Length);
            BinaryPrimitives.WriteInt32LittleEndian(prefix[12..], meta.Length);
            BinaryPrimitives.WriteInt32LittleEndian(prefix[16..], payload.Length);
            BinaryPrimitives.WriteInt64LittleEndian(prefix[20..], offsetOrNext);
            BinaryPrimitives.WriteInt64LittleEndian(prefix[28..], ticks);
            topic.CopyTo(prefix[HeaderSize..]);
            meta.CopyTo(prefix[(HeaderSize + topic.Length)..]);

            stream.Write(prefix);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        if (!payload.IsEmpty)
            stream.Write(payload);

        if (flush && (_options.FlushOnPublish || _options.SyncOnPublish))
            stream.Flush(_options.SyncOnPublish);

        return new RecordLocation(segmentBaseOffset, position);
    }

    private FileStream GetWritableStream(TopicState state, long recordBytes)
    {
        if (_options.OpenMode == SonnetMqOpenMode.SingleFile)
            return _singleFileStream ?? throw new InvalidOperationException("Single-file stream is not open.");

        EnsureWriter(state);
        if (state.Writer!.Position > 0 && state.Writer.Position + recordBytes > _options.SegmentMaxBytes)
        {
            // 组提交下滚段前先把旧段刷到所配置持久层：Dispose 仅 OS-flush 缓冲，不 fsync。
            // 若不在此 fsync，SyncOnPublish 时旧段里延迟刷盘的记录可能被后续「刷新段」的 leader
            // 误判为已持久（FlushedSeq 覆盖），实则只在 OS 页缓存 → 掉电丢失（比照旧逐条内联 fsync 语义）。
            if (_options.SyncOnPublish)
                state.Writer.Flush(flushToDisk: true);
            state.Writer.Dispose();
            state.Writer = null;
            state.AddSegment(new SegmentState(SegmentPath(state.Topic, state.NextOffset), state.NextOffset));
            EnsureWriter(state);
        }

        return state.Writer!;
    }

    private void EnsureWriter(TopicState state)
    {
        if (state.Writer is not null)
            return;

        Directory.CreateDirectory(TopicDirectory(state.Topic));
        var segment = state.Segments.Count == 0
            ? new SegmentState(SegmentPath(state.Topic, state.NextOffset), state.NextOffset)
            : state.Segments[^1];
        if (state.Segments.Count == 0)
            state.AddSegment(segment);

        state.Writer = OpenLogStream(segment.Path);
        state.Writer.Seek(0, SeekOrigin.End);
    }

    private void FlushPublishBatchIfNeeded(TopicState state)
    {
        if (_options.FlushOnPublish || _options.SyncOnPublish)
        {
            var stream = _options.OpenMode == SonnetMqOpenMode.SingleFile ? _singleFileStream : state.Writer;
            stream?.Flush(_options.SyncOnPublish);
        }
    }

    private IReadOnlyList<SonnetMqMessage> PullFromState(TopicState state, long offset, int maxCount)
    {
        long effectiveOffset = Math.Max(offset, state.TrimmedBeforeOffset);
        if (effectiveOffset >= state.NextOffset)
            return [];

        // 目标命中常驻热尾（keeping-up 消费者）：纯内存路径，零回归。
        if (state.IsHot(effectiveOffset))
            return PullHot(state, effectiveOffset, maxCount);

        return PullColdThenHot(state, effectiveOffset, maxCount);
    }

    private static IReadOnlyList<SonnetMqMessage> PullHot(TopicState state, long effectiveOffset, int maxCount)
    {
        int start = state.FindFirstIndexAtOrAfter(effectiveOffset);
        if (start >= state.Messages.Count)
            return [];

        int count = Math.Min(maxCount, state.Messages.Count - start);
        var result = new SonnetMqMessage[count];
        for (int i = 0; i < count; i++)
            result[i] = ToMessage(state.Messages[start + i]);
        return result;
    }

    private static SonnetMqMessage ToMessage(StoredMessage message)
        => new(message.Topic, message.Offset, message.TimestampUtc, message.Headers, message.Payload.ToArray());

    /// <summary>
    /// 冷读：目标 offset 已被逐出常驻热尾，经稀疏位置索引取锚点、通过有界只读句柄 LRU 从段文件按需读盘，
    /// 从锚点顺序解码跳到目标 offset，再连续读 maxCount 条；跨越冷/热边界时无缝续读常驻热尾。
    /// 单文件模式不驱逐（全量常驻），故不会走到此路径。
    /// </summary>
    private IReadOnlyList<SonnetMqMessage> PullColdThenHot(TopicState state, long effectiveOffset, int maxCount)
    {
        if (!state.TryGetColdAnchor(effectiveOffset, out var anchor))
            return state.IsHot(effectiveOffset) ? PullHot(state, effectiveOffset, maxCount) : [];

        // 冷读前把写缓冲推到 OS 页缓存，确保尚未刷盘的已逐出记录在磁盘可见（RandomAccess 读页缓存）。
        state.Writer?.Flush(flushToDisk: false);

        var results = new List<SonnetMqMessage>(Math.Min(maxCount, 1024));
        long next = effectiveOffset;
        int segmentIndex = FindSegmentIndex(state, anchor.SegmentBaseOffset);
        long position = anchor.FilePosition;

        while (results.Count < maxCount && next < state.NextOffset)
        {
            // 一旦推进到常驻热尾起点，剩余部分直接走内存，避免读已在内存的记录。
            if (state.IsHot(next))
            {
                foreach (var message in PullHot(state, next, maxCount - results.Count))
                    results.Add(message);
                break;
            }

            if (segmentIndex < 0 || segmentIndex >= state.Segments.Count)
                break;

            var segment = state.Segments[segmentIndex];
            SafeFileHandle handle = _handleCache.Acquire(segment.Path);
            long length = RandomAccess.GetLength(handle);

            bool advancedSegment = false;
            while (results.Count < maxCount && position < length)
            {
                if (!TryReadRecordAt(handle, position, length, out var record, out long nextPosition))
                    break;
                position = nextPosition;

                if (record.Type != RecordTypeMessage)
                    continue;
                if (record.Offset < next)
                    continue;
                if (state.IsHot(record.Offset))
                {
                    // 到达热尾边界：跳出内层，外层循环转内存续读。
                    advancedSegment = true;
                    break;
                }

                results.Add(new SonnetMqMessage(
                    state.Topic,
                    record.Offset,
                    new DateTimeOffset(record.Ticks, TimeSpan.Zero),
                    record.Headers,
                    record.Payload));
                next = record.Offset + 1;
            }

            if (advancedSegment)
                continue;

            // 当前段读尽仍未满足：续读下一段（跨段冷读）。
            segmentIndex++;
            position = 0;
        }

        return results;
    }

    private static int FindSegmentIndex(TopicState state, long baseOffset)
    {
        for (int i = 0; i < state.Segments.Count; i++)
        {
            if (state.Segments[i].BaseOffset == baseOffset)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 从只读句柄 + 文件位置解码一条记录（与 <see cref="ReplayStream"/> 字段解析同构，数据源换成句柄）。
    /// </summary>
    private static bool TryReadRecordAt(SafeFileHandle handle, long position, long length, out ColdRecord record, out long nextPosition)
    {
        record = default;
        nextPosition = position;
        if (position + HeaderSize > length)
            return false;

        Span<byte> header = stackalloc byte[HeaderSize];
        if (RandomAccess.Read(handle, header, position) != HeaderSize)
            return false;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        byte type = header[6];
        int topicLength = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        int metaLength = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        long offsetOrNext = BinaryPrimitives.ReadInt64LittleEndian(header[20..]);
        long ticks = BinaryPrimitives.ReadInt64LittleEndian(header[28..]);

        if (magic != Magic || version != Version || topicLength < 0 || metaLength < 0 || payloadLength < 0)
            throw new InvalidDataException("SonnetMQ segment header is invalid.");
        if (topicLength > MaxNameBytes || metaLength > MaxHeadersBytes || payloadLength > MaxPayloadBytes)
            throw new InvalidDataException("SonnetMQ segment record exceeds configured bounds.");

        long bodyPosition = position + HeaderSize;
        long recordEnd = bodyPosition + topicLength + metaLength + payloadLength;
        if (recordEnd > length)
            return false;

        nextPosition = recordEnd;
        if (type != RecordTypeMessage)
        {
            record = new ColdRecord(type, offsetOrNext, ticks, EmptyHeaders.Instance, []);
            return true;
        }

        // 跳过 topic，读 meta + payload。
        var headers = EmptyHeaders.Instance as IReadOnlyDictionary<string, string>;
        if (metaLength > 0)
        {
            byte[] metaBytes = ArrayPool<byte>.Shared.Rent(metaLength);
            try
            {
                ReadExactAt(handle, metaBytes.AsSpan(0, metaLength), bodyPosition + topicLength);
                headers = DecodeHeaders(metaBytes.AsSpan(0, metaLength));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(metaBytes);
            }
        }

        byte[] payload = payloadLength == 0 ? [] : new byte[payloadLength];
        if (payloadLength > 0)
            ReadExactAt(handle, payload, bodyPosition + topicLength + metaLength);

        record = new ColdRecord(type, offsetOrNext, ticks, headers, payload);
        return true;
    }

    private static void ReadExactAt(SafeFileHandle handle, Span<byte> destination, long position)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int n = RandomAccess.Read(handle, destination[read..], position + read);
            if (n == 0)
                throw new InvalidDataException("SonnetMQ segment has a truncated record.");
            read += n;
        }
    }

    private void TrimTopicRetention(TopicState state)
    {
        long cutoff = state.TrimmedBeforeOffset;
        if (_options.RetentionMaxAge is { } maxAge)
        {
            // 按段粒度：丢弃「整段最新记录都已超龄」的非活跃段（与 RetentionMaxBytes 的按段裁剪一致，
            // 也与 Kafka 时间保留一致），无需常驻每条时间戳。段最新记录 ticks = 顺序扫描取最大（ticks 随发布单调）。
            long threshold = (DateTimeOffset.UtcNow - maxAge).UtcTicks;
            for (int i = 0; i < state.Segments.Count; i++)
            {
                var segment = state.Segments[i];
                if (segment == state.Segments[^1])
                    break; // 活跃段永不按时间裁剪。

                if (!TryGetSegmentNewestTicks(segment, out long newestTicks) || newestTicks >= threshold)
                    break; // 该段仍有未超龄记录 → 之后的段更新，停止。

                cutoff = Math.Max(cutoff, NextSegmentBaseOffset(state, segment));
            }
        }

        if (_options.RetentionMaxBytes is { } maxBytes && maxBytes >= 0)
        {
            long total = state.Segments.Where(static s => File.Exists(s.Path)).Sum(static s => new FileInfo(s.Path).Length);
            foreach (var segment in state.Segments.OrderBy(static s => s.BaseOffset))
            {
                if (total <= maxBytes || segment == state.Segments[^1])
                    break;

                long size = File.Exists(segment.Path) ? new FileInfo(segment.Path).Length : 0;
                total -= size;
                cutoff = Math.Max(cutoff, NextSegmentBaseOffset(state, segment));
            }
        }

        if (cutoff > state.TrimmedBeforeOffset)
        {
            byte[] topicBytes = EncodeName(state.Topic, nameof(state.Topic));
            WriteRecord(state, RecordTypeTombstone, topicBytes, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, cutoff, DateTimeOffset.UtcNow.UtcTicks);
            state.ApplyTombstone(cutoff);
            DeleteRetiredSegments(state);
        }
    }

    /// <summary>顺序扫描一个段，返回其消息记录的最大 ticks（ticks 随发布单调，即最新记录时间戳）。段无消息记录返回 false。</summary>
    private bool TryGetSegmentNewestTicks(SegmentState segment, out long newestTicks)
    {
        newestTicks = 0;
        if (!File.Exists(segment.Path))
            return false;

        SafeFileHandle handle = _handleCache.Acquire(segment.Path);
        long length = RandomAccess.GetLength(handle);
        long position = 0;
        bool any = false;
        while (TryReadRecordAt(handle, position, length, out var record, out long nextPosition))
        {
            position = nextPosition;
            if (record.Type == RecordTypeMessage)
            {
                newestTicks = record.Ticks;
                any = true;
            }
        }

        return any;
    }

    private void TrimAcknowledgedMessages(TopicState state, bool force)
    {
        if (!_options.TrimAcknowledgedMessages || state.ConsumerOffsets.Count == 0)
            return;

        long cutoff = state.ConsumerOffsets.Values.Min();
        cutoff = Math.Min(cutoff, state.NextOffset);
        if (cutoff <= state.TrimmedBeforeOffset)
            return;

        long minDelta = Math.Max(1, _options.AckRetentionMinOffsetDelta);
        if (!force && cutoff - state.TrimmedBeforeOffset < minDelta)
            return;

        byte[] topicBytes = EncodeName(state.Topic, nameof(state.Topic));
        WriteRecord(state, RecordTypeTombstone, topicBytes, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, cutoff, DateTimeOffset.UtcNow.UtcTicks);
        state.ApplyTombstone(cutoff);
        DeleteRetiredSegments(state);
    }

    private static long NextSegmentBaseOffset(TopicState state, SegmentState segment)
    {
        int index = state.Segments.IndexOf(segment);
        if (index >= 0 && index + 1 < state.Segments.Count)
            return state.Segments[index + 1].BaseOffset;
        return state.NextOffset;
    }

    private void DeleteRetiredSegments(TopicState state)
    {
        if (_options.OpenMode == SonnetMqOpenMode.SingleFile)
            return;

        for (int i = state.Segments.Count - 2; i >= 0; i--)
        {
            var segment = state.Segments[i];
            if (NextSegmentBaseOffset(state, segment) > state.TrimmedBeforeOffset)
                continue;

            _handleCache.Invalidate(segment.Path);
            try { File.Delete(segment.Path); } catch (IOException) { continue; }
            state.Segments.RemoveAt(i);
            state.RemoveSegmentIndexEntries(segment.BaseOffset);
        }
    }

    private TopicState GetOrCreateTopic(string topic)
        => _topics.GetOrAdd(topic, static (t, self) => new TopicState(t, self._singleFileLock ?? new object()), this);

    private void RetentionWorkerLoop()
    {
        var token = _retentionCts?.Token ?? CancellationToken.None;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (token.WaitHandle.WaitOne(_options.RetentionInterval))
                    break;
                TrimRetention();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (IOException)
            {
                // 下一轮继续尝试。Retention 是后台清理，不影响 publish/ack 主路径。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上，保留给宿主诊断权限问题。
            }
        }
    }

    private void StartRetentionWorkerIfNeeded()
    {
        if (_options.OpenMode != SonnetMqOpenMode.Directory || _options.RetentionInterval <= TimeSpan.Zero)
            return;

        _retentionCts = new CancellationTokenSource();
        _retentionWorker = new Thread(RetentionWorkerLoop)
        {
            IsBackground = true,
            Name = "SonnetMQ RetentionWorker",
        };
        _retentionWorker.Start();
    }

    private string TopicDirectory(string topic)
        => Path.Combine(RootDirectory, EncodeTopicDirectory(topic));

    private string SegmentPath(string topic, long baseOffset)
        => Path.Combine(TopicDirectory(topic), baseOffset.ToString("D" + SegmentFileNameWidth, System.Globalization.CultureInfo.InvariantCulture) + ".smqseg");

    private static IEnumerable<string> EnumerateSegmentPaths(string topicDirectory)
        => Directory.EnumerateFiles(topicDirectory, "*.smqseg").Order(StringComparer.Ordinal);

    private static long ParseSegmentBaseOffset(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return long.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long value)
            ? value
            : 0L;
    }

    private static FileStream OpenLogStream(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
    }

    private static string EncodeTopicDirectory(string topic)
        => Convert.ToHexString(Encoding.UTF8.GetBytes(topic));

    private static string DecodeTopicDirectory(string directoryName)
        => Encoding.UTF8.GetString(Convert.FromHexString(directoryName));

    private static byte[] EncodeName(string value, string parameterName)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length == 0 || bytes.Length > MaxNameBytes)
            throw new ArgumentOutOfRangeException(parameterName, value, "名称 UTF-8 编码长度必须位于 1 到 512 字节之间。");
        return bytes;
    }

    private static byte[] EncodeHeaders(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.Count == 0)
            return [];

        // MQ6：免 StringBuilder + LINQ OrderBy + 逐值 Convert.ToBase64String。
        var pairs = new KeyValuePair<string, string>[headers.Count];
        int n = 0;
        foreach (var pair in headers)
            pairs[n++] = pair;
        Array.Sort(pairs, static (a, b) => string.CompareOrdinal(a.Key, b.Key));

        var writer = new ArrayBufferWriter<byte>();
        foreach (var pair in pairs)
        {
            ValidateHeaderName(pair.Key);
            WriteUtf8(writer, pair.Key);
            writer.GetSpan(1)[0] = (byte)'=';
            writer.Advance(1);
            WriteBase64Utf8(writer, pair.Value ?? string.Empty);
            writer.GetSpan(1)[0] = (byte)'\n';
            writer.Advance(1);
        }

        if (writer.WrittenCount > MaxHeadersBytes)
            throw new ArgumentOutOfRangeException(nameof(headers), "消息头总长度超过 SonnetMQ 当前上限。");
        return writer.WrittenSpan.ToArray();
    }

    private static void WriteUtf8(ArrayBufferWriter<byte> writer, string value)
    {
        if (value.Length == 0)
            return;
        var span = writer.GetSpan(Encoding.UTF8.GetMaxByteCount(value.Length));
        writer.Advance(Encoding.UTF8.GetBytes(value, span));
    }

    private static void WriteBase64Utf8(ArrayBufferWriter<byte> writer, string value)
    {
        if (value.Length == 0)
            return;

        byte[] rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(value.Length));
        try
        {
            int utf8Len = Encoding.UTF8.GetBytes(value, rented);
            var span = writer.GetSpan(Base64.GetMaxEncodedToUtf8Length(utf8Len));
            Base64.EncodeToUtf8(rented.AsSpan(0, utf8Len), span, out _, out int written);
            writer.Advance(written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static Dictionary<string, string> DecodeHeaders(ReadOnlySpan<byte> bytes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (bytes.IsEmpty)
            return result;

        string text = Encoding.UTF8.GetString(bytes);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int idx = line.IndexOf('=', StringComparison.Ordinal);
            if (idx <= 0)
                throw new InvalidDataException("SonnetMQ message headers are invalid.");

            string key = line[..idx];
            string value = Encoding.UTF8.GetString(Convert.FromBase64String(line[(idx + 1)..]));
            result[key] = value;
        }
        return result;
    }

    private static bool TryReadExact(Stream stream, Span<byte> destination)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int n = stream.Read(destination[read..]);
            if (n == 0)
            {
                if (read == 0)
                    return false;
                throw new InvalidDataException("SonnetMQ log has a truncated tail.");
            }
            read += n;
        }
        return true;
    }

    private static void ReadExactOrThrow(Stream stream, Span<byte> destination)
    {
        if (!TryReadExact(stream, destination))
            throw new InvalidDataException("SonnetMQ log has a truncated record.");
    }

    private static void ValidateTopic(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ValidateNameChars(topic, nameof(topic));
    }

    private static void ValidateConsumerGroup(string consumerGroup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        ValidateNameChars(consumerGroup, nameof(consumerGroup));
    }

    private static void ValidateHeaderName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateNameChars(name, nameof(name));
    }

    private static void ValidateNameChars(string value, string parameterName)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            bool valid =
                ch is >= 'a' and <= 'z' ||
                ch is >= 'A' and <= 'Z' ||
                ch is >= '0' and <= '9' ||
                ch is '_' or '-' or '.';
            if (!valid)
                throw new ArgumentException("名称仅允许 ASCII 字母、数字、下划线、连字符与点。", parameterName);
        }
    }

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class TopicState : IDisposable
    {
        public TopicState(string topic, object syncRoot)
        {
            Topic = topic;
            SyncRoot = syncRoot;
        }

        /// <summary>
        /// 该 topic 的写/读串行化锁。目录模式下每个 topic 独占一把；单文件模式下所有 topic 共享同一把（共用底层流）。
        /// </summary>
        public object SyncRoot { get; }

        /// <summary>
        /// 组提交 leader 选举锁。同一时刻只有一个 leader 执行刷盘。锁序固定为 <see cref="FlushRoot"/>（外）→
        /// <see cref="SyncRoot"/>（内）：leader 仅在真正调用 <c>Flush</c> 的瞬间再取 <see cref="SyncRoot"/>，
        /// 序列化于写入者（FileStream 非线程安全）；写入者只取 <see cref="SyncRoot"/>，不涉及 <see cref="FlushRoot"/>。
        /// </summary>
        public object FlushRoot { get; } = new();

        /// <summary>已追加（延迟刷盘）的记录序号，SyncRoot 保护，单调递增。</summary>
        public long AppendedSeq;

        /// <summary>已刷盘到所配置持久层的最高 <see cref="AppendedSeq"/>，SyncRoot 保护。</summary>
        public long FlushedSeq;

        /// <summary>
        /// 推送订阅唤醒信号（#236）。惰性创建：仅在有等待者时才有实例；发布后 <see cref="Pulse"/> 取出并置空、
        /// <c>TrySetResult</c> 唤醒全部等待者。无订阅者时热路径只做一次 volatile 读，零分配。
        /// </summary>
        private TaskCompletionSource? _pulse;

        /// <summary>已故障（store 释放）；在 <see cref="SyncRoot"/> 内置位与读取，供等待者判定不再挂起。</summary>
        public bool Faulted { get; private set; }

        /// <summary>取得（惰性创建）当前 pulse 的 <see cref="Task"/>。必须在持有 <see cref="SyncRoot"/> 时调用。</summary>
        public TaskCompletionSource GetOrCreatePulse()
            => _pulse ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>唤醒全部等待者（若有）。在 <see cref="SyncRoot"/> 外调用，避免被唤醒者立刻回来争锁。</summary>
        public void Pulse()
            => Interlocked.Exchange(ref _pulse, null)?.TrySetResult();

        /// <summary>
        /// 使全部等待者故障退出（store 释放时调用）。必须在持有 <see cref="SyncRoot"/> 时调用：
        /// 与等待者的「检查条件 + 创建 pulse」串行化，杜绝故障后新建 pulse 永不完成的丢唤醒。
        /// </summary>
        public void FaultWaiters(Exception error)
        {
            Faulted = true;
            Interlocked.Exchange(ref _pulse, null)?.TrySetException(error);
        }

        public string Topic { get; }

        /// <summary>常驻内存的「热尾部」——最近发布、尚未因超 <c>HotTailMaxBytes</c> 被驱逐的消息，按 offset 升序。</summary>
        public List<StoredMessage> Messages { get; } = [];

        public Dictionary<string, long> ConsumerOffsets { get; } = new(StringComparer.Ordinal);

        public List<SegmentState> Segments { get; } = [];

        public FileStream? Writer { get; set; }

        public long NextOffset { get; private set; }

        public long TrimmedBeforeOffset { get; private set; }

        /// <summary>热尾内第一条消息的 offset；&lt; 此 offset 的消息已被驱逐、需冷读。等于 <see cref="NextOffset"/> 表示热尾为空。</summary>
        public long HotTailStartOffset { get; private set; }

        private long _residentPayloadBytes;
        private readonly List<OffsetIndexEntry> _offsetIndex = [];

        public void AddSegment(SegmentState segment)
        {
            if (!Segments.Any(s => string.Equals(s.Path, segment.Path, StringComparison.Ordinal)))
                Segments.Add(segment);
            Segments.Sort(static (a, b) => a.BaseOffset.CompareTo(b.BaseOffset));
        }

        public void Append(StoredMessage message, RecordLocation location, int offsetIndexStride, long hotTailMaxBytes)
        {
            if (message.Offset < TrimmedBeforeOffset)
            {
                if (message.Offset >= NextOffset)
                    NextOffset = message.Offset + 1;
                return;
            }

            // 位置索引按 stride 采样（含每段首条），驱逐后仍可据此定位冷 offset → 从锚点顺序扫描。
            // 无段来源（单文件模式 / legacy 日志）的记录不可冷读，不入位置索引（也不会被驱逐）。
            if (location.SegmentBaseOffset >= 0
                && (_offsetIndex.Count == 0
                    || message.Offset % offsetIndexStride == 0
                    || _offsetIndex[^1].SegmentBaseOffset != location.SegmentBaseOffset))
            {
                _offsetIndex.Add(new OffsetIndexEntry(message.Offset, location.SegmentBaseOffset, location.FilePosition));
            }

            if (Messages.Count == 0)
                HotTailStartOffset = message.Offset;

            Messages.Add(message);
            _residentPayloadBytes += message.Payload.Length;
            if (message.Offset >= NextOffset)
                NextOffset = message.Offset + 1;

            EvictHotTailIfNeeded(hotTailMaxBytes);
        }

        /// <summary>
        /// 热尾 payload 超上限时从头部驱逐最老消息；始终至少保留最后一条（保证 keeping-up 消费者热路径）。
        /// 不可冷读的消息（legacy 日志来源，无段位置）被钉住不驱逐——驱逐它们将导致不可达。
        /// </summary>
        private void EvictHotTailIfNeeded(long hotTailMaxBytes)
        {
            if (_residentPayloadBytes <= hotTailMaxBytes)
                return;

            int evict = 0;
            while (evict < Messages.Count - 1
                && _residentPayloadBytes > hotTailMaxBytes
                && Messages[evict].ColdReadable)
            {
                _residentPayloadBytes -= Messages[evict].Payload.Length;
                evict++;
            }

            if (evict == 0)
                return;

            Messages.RemoveRange(0, evict);
            HotTailStartOffset = Messages.Count > 0 ? Messages[0].Offset : NextOffset;
        }

        public void ApplyTombstone(long beforeOffset)
        {
            if (beforeOffset <= TrimmedBeforeOffset)
                return;

            long removedBytes = 0;
            int keptFrom = Messages.Count;
            for (int i = 0; i < Messages.Count; i++)
            {
                if (Messages[i].Offset >= beforeOffset)
                {
                    keptFrom = i;
                    break;
                }
                removedBytes += Messages[i].Payload.Length;
            }

            TrimmedBeforeOffset = beforeOffset;
            if (keptFrom > 0)
            {
                Messages.RemoveRange(0, keptFrom);
                _residentPayloadBytes -= removedBytes;
            }
            HotTailStartOffset = Messages.Count > 0 ? Messages[0].Offset : NextOffset;

            // 位置索引条目按「所属段被删除」清理（见 RemoveSegmentIndexEntries），此处不按 offset 删——
            // 稀疏采样下 cutoff 之上第一条冷消息可能仍需 cutoff 之下最近的锚点向前扫描定位。
            foreach (var pair in ConsumerOffsets.ToArray())
                ConsumerOffsets[pair.Key] = Math.Max(pair.Value, beforeOffset);
        }

        /// <summary>段被物理删除后清理其全部位置索引锚点（其 offset 均已 &lt; TrimmedBeforeOffset，不再可达）。</summary>
        public void RemoveSegmentIndexEntries(long segmentBaseOffset)
            => _offsetIndex.RemoveAll(e => e.SegmentBaseOffset == segmentBaseOffset);

        /// <summary>目标 offset 是否命中常驻热尾（无需冷读）。</summary>
        public bool IsHot(long offset) => Messages.Count > 0 && offset >= HotTailStartOffset;

        public int FindFirstIndexAtOrAfter(long offset)
        {
            // Messages 按 offset 升序，直接二分（热尾窗口内不再依赖稀疏索引）。
            int lo = 0;
            int hi = Messages.Count;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) / 2);
                if (Messages[mid].Offset < offset)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo;
        }

        /// <summary>二分位置索引，取 offset ≤ target 的最近锚点（段 baseOffset + 文件位置）作为冷读入口。</summary>
        public bool TryGetColdAnchor(long targetOffset, out OffsetIndexEntry anchor)
        {
            anchor = default;
            int left = 0;
            int right = _offsetIndex.Count - 1;
            int found = -1;
            while (left <= right)
            {
                int middle = left + ((right - left) / 2);
                if (_offsetIndex[middle].Offset <= targetOffset)
                {
                    found = middle;
                    left = middle + 1;
                }
                else
                {
                    right = middle - 1;
                }
            }

            if (found < 0)
                return false;

            anchor = _offsetIndex[found];
            return true;
        }

        public long GetConsumerOffset(string consumerGroup)
            => ConsumerOffsets.TryGetValue(consumerGroup, out long offset) ? Math.Max(offset, TrimmedBeforeOffset) : TrimmedBeforeOffset;

        public void SetConsumerOffset(string consumerGroup, long nextOffset)
        {
            ConsumerOffsets[consumerGroup] = Math.Max(Math.Max(nextOffset, TrimmedBeforeOffset), GetConsumerOffset(consumerGroup));
        }

        public void Dispose()
        {
            Writer?.Dispose();
        }
    }

    private sealed record PreparedPublish(
        byte[] Payload,
        byte[] HeadersBytes,
        IReadOnlyDictionary<string, string> Headers);

    private sealed record SegmentState(string Path, long BaseOffset);

    private readonly record struct OffsetIndexEntry(long Offset, long SegmentBaseOffset, long FilePosition);

    private readonly record struct RecordLocation(long SegmentBaseOffset, long FilePosition);

    private readonly record struct ColdRecord(
        byte Type,
        long Offset,
        long Ticks,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Payload);

    /// <summary>
    /// 有界只读段句柄 LRU，落地 <see cref="SonnetMqOptions.SegmentCacheSize"/>。冷读复用已打开句柄，
    /// 超容量按最近最少使用关闭最久未用句柄。只读句柄可并发 <c>RandomAccess.Read</c>，无需与写序列化；
    /// 自持一把锁保护 LRU 结构本身。
    /// </summary>
    private sealed class SegmentHandleCache : IDisposable
    {
        private readonly int _capacity;
        private readonly object _sync = new();
        private readonly LinkedList<string> _lru = new();
        private readonly Dictionary<string, (SafeFileHandle Handle, LinkedListNode<string> Node)> _entries =
            new(StringComparer.Ordinal);
        private bool _disposed;

        public SegmentHandleCache(int capacity) => _capacity = capacity;

        public SafeFileHandle Acquire(string path)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_entries.TryGetValue(path, out var existing))
                {
                    _lru.Remove(existing.Node);
                    _lru.AddFirst(existing.Node);
                    return existing.Handle;
                }

                var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var node = _lru.AddFirst(path);
                _entries[path] = (handle, node);
                EvictIfNeeded();
                return handle;
            }
        }

        public void Invalidate(string path)
        {
            lock (_sync)
            {
                if (_entries.Remove(path, out var entry))
                {
                    _lru.Remove(entry.Node);
                    entry.Handle.Dispose();
                }
            }
        }

        private void EvictIfNeeded()
        {
            while (_entries.Count > _capacity && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                if (_entries.Remove(last.Value, out var entry))
                    entry.Handle.Dispose();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                foreach (var entry in _entries.Values)
                    entry.Handle.Dispose();
                _entries.Clear();
                _lru.Clear();
            }
        }
    }

    private sealed record StoredMessage(
        string Topic,
        long Offset,
        DateTimeOffset TimestampUtc,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Payload,
        bool ColdReadable);

    private sealed class EmptyHeaders : IReadOnlyDictionary<string, string>
    {
        public static readonly EmptyHeaders Instance = new();

        private EmptyHeaders() { }

        public IEnumerable<string> Keys => [];

        public IEnumerable<string> Values => [];

        public int Count => 0;

        public string this[string key] => throw new KeyNotFoundException();

        public bool ContainsKey(string key) => false;

        public bool TryGetValue(string key, out string value)
        {
            value = string.Empty;
            return false;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
            => Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
