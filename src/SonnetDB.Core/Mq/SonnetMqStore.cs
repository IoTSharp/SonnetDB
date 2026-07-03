using System.Buffers;
using System.Buffers.Binary;
using System.Text;

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

    private readonly object _sync = new();
    private readonly SonnetMqOptions _options;
    private readonly Dictionary<string, TopicState> _topics = new(StringComparer.Ordinal);
    private readonly int _offsetIndexStride;
    private Thread? _retentionWorker;
    private CancellationTokenSource? _retentionCts;
    private FileStream? _singleFileStream;
    private bool _disposed;

    private SonnetMqStore(SonnetMqOptions options)
    {
        _options = options;
        _offsetIndexStride = Math.Max(1, options.OffsetIndexStride);
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

        var entry = new SonnetMqPublishEntry(payload.ToArray(), options?.Headers);
        return PublishMany(topic, [entry])[0];
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

        byte[] topicBytes = EncodeName(topic, nameof(topic));
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
                new Dictionary<string, string>(headers, StringComparer.Ordinal));
        }

        lock (_sync)
        {
            var state = GetOrCreateTopic(topic);
            var offsets = new long[prepared.Length];
            for (int i = 0; i < prepared.Length; i++)
            {
                var publish = prepared[i];
                long offset = state.NextOffset;
                var timestamp = DateTimeOffset.UtcNow;
                WriteRecord(state, RecordTypeMessage, topicBytes, publish.HeadersBytes, publish.Payload, offset, timestamp.UtcTicks, flush: false);
                state.Append(new StoredMessage(topic, offset, timestamp, publish.Headers, publish.Payload), _offsetIndexStride);
                offsets[i] = offset;
            }

            FlushPublishBatchIfNeeded(state);
            return offsets;
        }
    }

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

        lock (_sync)
        {
            if (!_topics.TryGetValue(topic, out var state))
                return [];

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

        lock (_sync)
        {
            if (!_topics.TryGetValue(topic, out var state))
                return [];

            return PullFromState(state, offset, maxCount);
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

        lock (_sync)
        {
            var state = GetOrCreateTopic(topic);
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
        lock (_sync)
        {
            var state = GetOrCreateTopic(topic);
            long cutoff = Math.Min(beforeOffset, state.NextOffset);
            if (cutoff <= state.TrimmedBeforeOffset)
                return state.TrimmedBeforeOffset;

            WriteRecord(state, RecordTypeTombstone, topicBytes, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, cutoff, DateTimeOffset.UtcNow.UtcTicks);
            state.ApplyTombstone(cutoff, _offsetIndexStride);
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

        lock (_sync)
        {
            foreach (var state in _topics.Values)
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

        lock (_sync)
            return GetStatsLocked(topic);
    }

    /// <summary>
    /// 枚举当前所有 topic 的统计快照。只读，不改变任何队列状态。
    /// </summary>
    /// <returns>按 topic 名称升序排列的统计快照。</returns>
    public IReadOnlyList<SonnetMqTopicStats> ListTopicStats()
    {
        EnsureNotDisposed();

        lock (_sync)
        {
            return _topics.Keys
                .Order(StringComparer.Ordinal)
                .Select(GetStatsLocked)
                .ToArray();
        }
    }

    private SonnetMqTopicStats GetStatsLocked(string topic)
    {
        if (!_topics.TryGetValue(topic, out var state))
            return new SonnetMqTopicStats(topic, 0, 0, new Dictionary<string, long>(StringComparer.Ordinal));

        return new SonnetMqTopicStats(
            topic,
            state.Messages.Count,
            state.NextOffset,
            new Dictionary<string, long>(state.ConsumerOffsets, StringComparer.Ordinal));
    }

    /// <summary>
    /// 将当前写缓冲刷新到文件。
    /// </summary>
    /// <param name="flushToDisk">是否请求持久化到磁盘。</param>
    public void Flush(bool flushToDisk = false)
    {
        EnsureNotDisposed();
        lock (_sync)
        {
            if (_options.OpenMode == SonnetMqOpenMode.SingleFile)
            {
                _singleFileStream?.Flush(flushToDisk);
                return;
            }

            foreach (var topic in _topics.Values)
                topic.Writer?.Flush(flushToDisk);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _retentionCts?.Cancel();
        _retentionWorker?.Join(TimeSpan.FromSeconds(2));
        _retentionCts?.Dispose();

        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (var topic in _topics.Values)
                topic.Dispose();
            _singleFileStream?.Dispose();
        }
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
                ReplayStream(stream, state);
            }
        }
    }

    private void ReplayStream(Stream stream, TopicState? knownTopicState)
    {
        stream.Seek(0, SeekOrigin.Begin);
        Span<byte> header = stackalloc byte[HeaderSize];

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
                    state.Append(new StoredMessage(topic, offsetOrNext, new DateTimeOffset(ticks, TimeSpan.Zero), headers, body), _offsetIndexStride);
                }
                else if (type == RecordTypeAck)
                {
                    string consumerGroup = Encoding.UTF8.GetString(metaBytes.AsSpan(0, metaLength));
                    state.SetConsumerOffset(consumerGroup, Math.Min(offsetOrNext, state.NextOffset));
                }
                else if (type == RecordTypeTombstone)
                {
                    state.ApplyTombstone(offsetOrNext, _offsetIndexStride);
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
    {
        FileStream stream = GetWritableStream(state, HeaderSize + topic.Length + meta.Length + payload.Length);
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
        header[6] = type;
        header[7] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], topic.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], meta.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], payload.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header[20..], offsetOrNext);
        BinaryPrimitives.WriteInt64LittleEndian(header[28..], ticks);

        stream.Write(header);
        stream.Write(topic);
        stream.Write(meta);
        stream.Write(payload);
        if (flush && (_options.FlushOnPublish || _options.SyncOnPublish))
            stream.Flush(_options.SyncOnPublish);
    }

    private FileStream GetWritableStream(TopicState state, long recordBytes)
    {
        if (_options.OpenMode == SonnetMqOpenMode.SingleFile)
            return _singleFileStream ?? throw new InvalidOperationException("Single-file stream is not open.");

        EnsureWriter(state);
        if (state.Writer!.Position > 0 && state.Writer.Position + recordBytes > _options.SegmentMaxBytes)
        {
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

    private static IReadOnlyList<SonnetMqMessage> PullFromState(TopicState state, long offset, int maxCount)
    {
        long effectiveOffset = Math.Max(offset, state.TrimmedBeforeOffset);
        int start = state.FindFirstIndexAtOrAfter(effectiveOffset);
        if (start >= state.Messages.Count)
            return [];

        int count = Math.Min(maxCount, state.Messages.Count - start);
        var result = new SonnetMqMessage[count];
        for (int i = 0; i < count; i++)
        {
            var message = state.Messages[start + i];
            result[i] = new SonnetMqMessage(
                message.Topic,
                message.Offset,
                message.TimestampUtc,
                message.Headers,
                message.Payload.ToArray());
        }

        return result;
    }

    private void TrimTopicRetention(TopicState state)
    {
        long cutoff = state.TrimmedBeforeOffset;
        if (_options.RetentionMaxAge is { } maxAge)
        {
            var threshold = DateTimeOffset.UtcNow - maxAge;
            foreach (var message in state.Messages)
            {
                if (message.TimestampUtc > threshold)
                    break;
                cutoff = message.Offset + 1;
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
            state.ApplyTombstone(cutoff, _offsetIndexStride);
            DeleteRetiredSegments(state);
        }
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
        state.ApplyTombstone(cutoff, _offsetIndexStride);
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

            try { File.Delete(segment.Path); } catch (IOException) { continue; }
            state.Segments.RemoveAt(i);
        }
    }

    private TopicState GetOrCreateTopic(string topic)
    {
        if (_topics.TryGetValue(topic, out var state))
            return state;

        state = new TopicState(topic);
        _topics.Add(topic, state);
        return state;
    }

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

        var builder = new StringBuilder();
        foreach (var pair in headers.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            ValidateHeaderName(pair.Key);
            builder.Append(pair.Key);
            builder.Append('=');
            builder.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Value ?? string.Empty)));
            builder.Append('\n');
        }

        byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
        if (bytes.Length > MaxHeadersBytes)
            throw new ArgumentOutOfRangeException(nameof(headers), "消息头总长度超过 SonnetMQ 当前上限。");
        return bytes;
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
        public TopicState(string topic)
        {
            Topic = topic;
        }

        public string Topic { get; }

        public List<StoredMessage> Messages { get; } = [];

        public Dictionary<string, long> ConsumerOffsets { get; } = new(StringComparer.Ordinal);

        public List<SegmentState> Segments { get; } = [];

        public FileStream? Writer { get; set; }

        public long NextOffset { get; private set; }

        public long TrimmedBeforeOffset { get; private set; }

        private readonly List<OffsetIndexEntry> _offsetIndex = [];

        public void AddSegment(SegmentState segment)
        {
            if (!Segments.Any(s => string.Equals(s.Path, segment.Path, StringComparison.Ordinal)))
                Segments.Add(segment);
            Segments.Sort(static (a, b) => a.BaseOffset.CompareTo(b.BaseOffset));
        }

        public void Append(StoredMessage message, int offsetIndexStride)
        {
            if (message.Offset < TrimmedBeforeOffset)
            {
                if (message.Offset >= NextOffset)
                    NextOffset = message.Offset + 1;
                return;
            }

            if (Messages.Count == 0 || message.Offset % offsetIndexStride == 0)
                _offsetIndex.Add(new OffsetIndexEntry(message.Offset, Messages.Count));

            Messages.Add(message);
            if (message.Offset >= NextOffset)
                NextOffset = message.Offset + 1;
        }

        public void ApplyTombstone(long beforeOffset, int offsetIndexStride)
        {
            if (beforeOffset <= TrimmedBeforeOffset)
                return;

            TrimmedBeforeOffset = beforeOffset;
            Messages.RemoveAll(m => m.Offset < beforeOffset);
            foreach (var pair in ConsumerOffsets.ToArray())
                ConsumerOffsets[pair.Key] = Math.Max(pair.Value, beforeOffset);
            RebuildOffsetIndex(offsetIndexStride);
        }

        public int FindFirstIndexAtOrAfter(long offset)
        {
            if (Messages.Count == 0)
                return 0;

            int start = 0;
            int left = 0;
            int right = _offsetIndex.Count - 1;
            while (left <= right)
            {
                int middle = left + ((right - left) / 2);
                if (_offsetIndex[middle].Offset <= offset)
                {
                    start = _offsetIndex[middle].MessageIndex;
                    left = middle + 1;
                }
                else
                {
                    right = middle - 1;
                }
            }

            while (start < Messages.Count && Messages[start].Offset < offset)
                start++;

            return start;
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

        private void RebuildOffsetIndex(int offsetIndexStride)
        {
            _offsetIndex.Clear();
            for (int i = 0; i < Messages.Count; i++)
            {
                if (i == 0 || Messages[i].Offset % offsetIndexStride == 0)
                    _offsetIndex.Add(new OffsetIndexEntry(Messages[i].Offset, i));
            }
        }
    }

    private sealed record PreparedPublish(
        byte[] Payload,
        byte[] HeadersBytes,
        IReadOnlyDictionary<string, string> Headers);

    private sealed record SegmentState(string Path, long BaseOffset);

    private readonly record struct OffsetIndexEntry(long Offset, int MessageIndex);

    private sealed record StoredMessage(
        string Topic,
        long Offset,
        DateTimeOffset TimestampUtc,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Payload);

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
