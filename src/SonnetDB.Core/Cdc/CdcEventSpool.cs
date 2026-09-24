using System.Buffers;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO.Hashing;

namespace SonnetDB.Cdc;

/// <summary>
/// 有界、可恢复的 CDC append-only spool。
/// </summary>
/// <remarks>
/// <para>
/// spool 文件由固定帧头、长度受限的 <see cref="CdcEventCodec"/> JSON 正文和 CRC32 组成。
/// 每个分区的确认位点保存在独立的原子替换元数据文件中；确认先发布元数据，再尝试物理
/// 截断，因此掉电时最多保留已经确认的帧，不会因为元数据落后而丢失未确认事件。
/// </para>
/// <para>
/// 一个文件可以包含多个分区。没有显式 checkpoint 的回放按每个分区自己的确认位点过滤；
/// 指定 checkpoint 时只回放该 checkpoint 所属分区且 offset 更大的事件。
/// 一个路径同一时间只允许一个 spool 实例持有写 lease；另一个实例会在打开时收到 I/O
/// 异常。线程内的 append、ack 和 replay 则由单写者门闩串行化。
/// </para>
/// </remarks>
public sealed class CdcEventSpool : IDisposable, IAsyncDisposable
{
    /// <summary>当前 spool 帧格式版本。</summary>
    public const int CurrentFormatVersion = 1;

    // Metadata v2 records unacknowledged append intent with acknowledged = -1.
    // v1 remains readable for existing spools.
    private const int CurrentMetadataFormatVersion = 2;
    private const int LegacyMetadataFormatVersion = 1;

    /// <summary>固定帧头字节数。</summary>
    // magic(8) + version(4) + header size(4) + payload length(4) + reserved(4)
    // + partition(8) + offset(8) + crc32(4).
    internal const int FrameHeaderSize = 44;

    private const int FrameCrcCoveredBytes = FrameHeaderSize - sizeof(uint);
    private const int MetadataHeaderSize = 24;
    private const int MetadataEntrySize = 24;
    private const int MetadataCrcSize = sizeof(uint);
    private const int CopyBufferSize = 64 * 1024;
    private const int MaxMetadataReadBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan DisposeWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly byte[] FrameMagic = "SDBCSP01"u8.ToArray();
    private static readonly byte[] MetadataMagic = "SDBCSPM1"u8.ToArray();

    private readonly string _filePath;
    private readonly string _checkpointPath;
    private readonly string _leasePath;
    private readonly string _directory;
    private readonly CdcEventSpoolOptions _options;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly object _disposeLock = new();
    private FileStream? _writerLease;
    private List<FrameInfo> _frames = [];
    private Dictionary<long, PartitionState> _partitions = new();
    private long _storedBytes;
    private bool _disposed;
    private Exception? _fault;

    /// <summary>
    /// 打开或创建一个 CDC spool。
    /// </summary>
    /// <param name="path">append-only 数据文件路径。</param>
    /// <param name="options">容量和回放边界；为空时使用默认值。</param>
    /// <exception cref="InvalidDataException">已有文件包含截断、损坏或越界帧时抛出。</exception>
    public CdcEventSpool(string path, CdcEventSpoolOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _options = options ?? new CdcEventSpoolOptions();
        _options.Validate();

        _filePath = Path.GetFullPath(path);
        _directory = Path.GetDirectoryName(_filePath)
            ?? throw new ArgumentException("CDC spool 路径必须包含父目录。", nameof(path));
        _checkpointPath = _filePath + ".checkpoint";
        _leasePath = _filePath + ".lock";

        Directory.CreateDirectory(_directory);
        FileStream lease = new(
            _leasePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 1,
            options: FileOptions.SequentialScan);
        _writerLease = lease;
        try
        {
            LoadCheckpointMetadata();
            LoadFrames();
        }
        catch
        {
            lease.Dispose();
            _writerLease = null;
            throw;
        }
    }

    /// <summary>
    /// 打开或创建 spool 的静态工厂。
    /// </summary>
    /// <param name="path">append-only 数据文件路径。</param>
    /// <param name="options">容量和回放边界；为空时使用默认值。</param>
    /// <returns>已加载并校验的 spool。</returns>
    public static CdcEventSpool Open(string path, CdcEventSpoolOptions? options = null)
        => new(path, options);

    /// <summary>spool 数据文件的绝对路径。</summary>
    public string FilePath => _filePath;

    /// <summary>checkpoint 元数据文件的绝对路径。</summary>
    public string CheckpointPath => _checkpointPath;

    /// <summary>用于检测同路径并发写者的 lease 文件路径。</summary>
    public string LeasePath => _leasePath;

    /// <summary>当前文件中保留的事件帧数。</summary>
    public long EventCount
    {
        get
        {
            lock (_stateLock)
                return _frames.Count;
        }
    }

    /// <summary>当前文件中保留的总字节数（包含帧头）。</summary>
    public long StoredBytes
    {
        get
        {
            lock (_stateLock)
                return _storedBytes;
        }
    }

    /// <summary>
    /// 当前每个分区已确认的最大 offset。返回值是独立快照，修改不会影响 spool。
    /// </summary>
    public IReadOnlyDictionary<long, long> AcknowledgedCheckpoints
    {
        get
        {
            lock (_stateLock)
            {
                var result = new Dictionary<long, long>();
                foreach ((long partition, PartitionState state) in _partitions)
                {
                    if (state.AcknowledgedOffset >= 0)
                        result.Add(partition, state.AcknowledgedOffset);
                }

                return new ReadOnlyDictionary<long, long>(result);
            }
        }
    }

    /// <summary>
    /// 当 spool 只有一个已确认分区时返回该位点；多分区时返回空值。
    /// </summary>
    public CdcCheckpoint? AcknowledgedCheckpoint
    {
        get
        {
            lock (_stateLock)
            {
                CdcCheckpoint? result = null;
                foreach ((long partition, PartitionState state) in _partitions)
                {
                    if (state.AcknowledgedOffset < 0)
                        continue;
                    if (result is not null)
                        return null;
                    result = new CdcCheckpoint(partition, state.AcknowledgedOffset);
                }

                return result;
            }
        }
    }

    /// <summary>获取 spool 的一致状态快照。</summary>
    /// <returns>事件数量、字节数和确认位点。</returns>
    public CdcEventSpoolState GetState()
    {
        lock (_stateLock)
        {
            var checkpoints = new Dictionary<long, long>();
            foreach ((long partition, PartitionState state) in _partitions)
            {
                if (state.AcknowledgedOffset >= 0)
                    checkpoints.Add(partition, state.AcknowledgedOffset);
            }

            return new CdcEventSpoolState(
                _frames.Count,
                _storedBytes,
                new ReadOnlyDictionary<long, long>(checkpoints));
        }
    }

    /// <summary>异步获取 spool 的一致状态快照。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>事件数量、字节数和确认位点。</returns>
    public ValueTask<CdcEventSpoolState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOperational();
        return ValueTask.FromResult(GetState());
    }

    /// <summary>
    /// 追加一条 CDC 事件，并将帧正文和帧头一起持久化。
    /// </summary>
    /// <param name="value">待追加的 CDC 事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新帧的文件位置和 checkpoint。</returns>
    /// <exception cref="InvalidOperationException">spool 容量或分区数量已满。</exception>
    public async ValueTask<CdcEventSpoolAppendResult> AppendAsync(
        CdcEvent value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] payload = CdcEventCodec.Encode(value);
        CdcCheckpoint checkpoint = value.Metadata.Checkpoint;
        int frameLength = checked(FrameHeaderSize + payload.Length);

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOperational();

            long frameOffset;
            Dictionary<long, PartitionState> durableState;
            lock (_stateLock)
            {
                if (!_partitions.ContainsKey(checkpoint.Partition)
                    && _partitions.Count >= _options.MaxPartitions)
                {
                    throw new InvalidOperationException(
                        $"CDC spool 分区数已达到上限 {_options.MaxPartitions}。");
                }

                if (_partitions.TryGetValue(checkpoint.Partition, out PartitionState state)
                    && checkpoint.Offset <= state.HighWatermark)
                {
                    throw new ArgumentException(
                        "同一分区的 CDC offset 必须严格递增。",
                        nameof(value));
                }

                if (_frames.Count >= _options.MaxEvents)
                    throw new InvalidOperationException(
                        $"CDC spool 事件数已达到上限 {_options.MaxEvents}，请先确认并截断。");
                if (_storedBytes > _options.MaxBytes - frameLength)
                    throw new InvalidOperationException(
                        $"CDC spool 字节数已达到上限 {_options.MaxBytes}，请先确认并截断。");

                frameOffset = _storedBytes;
                durableState = new Dictionary<long, PartitionState>(_partitions);
                if (durableState.TryGetValue(checkpoint.Partition, out PartitionState existingState))
                {
                    durableState[checkpoint.Partition] = existingState with
                    {
                        HighWatermark = checkpoint.Offset,
                    };
                }
                else
                {
                    durableState.Add(
                        checkpoint.Partition,
                        new PartitionState(-1, checkpoint.Offset));
                }
            }

            try
            {
                // Publish the append intent before writing the frame. If the process
                // stops between these operations, reopen must fail closed on the
                // missing high-watermark frame instead of silently dropping it.
                await SaveCheckpointMetadataAsync(durableState, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                MarkFault(exception);
                throw;
            }

            try
            {
                await WriteFrameAsync(frameOffset, payload, checkpoint, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (!TryRollbackAppend(frameOffset))
                {
                    exception = new InvalidDataException(
                        "CDC spool append 在失败后无法回滚 partial frame；必须重开 spool。",
                        exception);
                }

                // Metadata already advertises the pending high-watermark, so even a
                // successful rollback leaves an outcome that must be reconciled by reopen.
                MarkFault(exception);
                throw;
            }

            lock (_stateLock)
            {
                _frames.Add(new FrameInfo(frameOffset, frameLength, payload.Length, checkpoint));
                _storedBytes = checked(_storedBytes + frameLength);
                _partitions = durableState;
            }

            return new CdcEventSpoolAppendResult(frameOffset, frameLength, checkpoint);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>同步追加一条 CDC 事件。</summary>
    /// <param name="value">待追加的 CDC 事件。</param>
    /// <returns>新帧的文件位置和 checkpoint。</returns>
    public CdcEventSpoolAppendResult Append(CdcEvent value)
        => AppendAsync(value).AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// 按有界批次回放事件。未指定 checkpoint 时按每个分区的已确认位点过滤。
    /// </summary>
    /// <param name="afterCheckpoint">可选的起始位点；指定后只回放该分区更大的 offset。</param>
    /// <param name="maxEvents">本批次最大事件数；为空时使用选项默认值。</param>
    /// <param name="maxBytes">本批次最大事件正文 UTF-8 字节数；为空时使用选项默认值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本批次事件列表。</returns>
    public async ValueTask<IReadOnlyList<CdcEvent>> ReplayAsync(
        CdcCheckpoint? afterCheckpoint = null,
        int? maxEvents = null,
        long? maxBytes = null,
        CancellationToken cancellationToken = default)
    {
        CdcEventSpoolBatch batch = await ReplayBatchAsync(
            afterCheckpoint,
            maxEvents,
            maxBytes,
            cancellationToken).ConfigureAwait(false);
        return batch.Events;
    }

    /// <summary>以取消令牌作为唯一参数回放默认批次。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本批次事件列表。</returns>
    public ValueTask<IReadOnlyList<CdcEvent>> ReplayAsync(CancellationToken cancellationToken)
        => ReplayAsync(null, null, null, cancellationToken);

    /// <summary>
    /// 回放一个有界批次，并报告是否还有后续事件。
    /// </summary>
    /// <param name="afterCheckpoint">可选的起始位点。</param>
    /// <param name="maxEvents">本批次最大事件数。</param>
    /// <param name="maxBytes">本批次最大事件正文 UTF-8 字节数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>事件、字节数和后续标志。</returns>
    public async ValueTask<CdcEventSpoolBatch> ReplayBatchAsync(
        CdcCheckpoint? afterCheckpoint = null,
        int? maxEvents = null,
        long? maxBytes = null,
        CancellationToken cancellationToken = default)
    {
        int requestedEvents = maxEvents ?? _options.MaxReplayEvents;
        long requestedBytes = maxBytes ?? _options.MaxReplayBytes;
        if (requestedEvents <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEvents));
        if (requestedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxEvents.HasValue && requestedEvents > _options.MaxEvents)
            throw new ArgumentOutOfRangeException(nameof(maxEvents));
        if (maxBytes.HasValue && requestedBytes > _options.MaxBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        int eventLimit = Math.Min(requestedEvents, _options.MaxEvents);
        long byteLimit = Math.Min(requestedBytes, _options.MaxBytes);
        if (afterCheckpoint is { Partition: < 0 } or { Offset: < 0 })
            throw new ArgumentOutOfRangeException(nameof(afterCheckpoint));

        cancellationToken.ThrowIfCancellationRequested();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOperational();

            FrameInfo[] frames;
            Dictionary<long, PartitionState> partitions;
            long storedBytes;
            lock (_stateLock)
            {
                frames = _frames.ToArray();
                partitions = new Dictionary<long, PartitionState>(_partitions);
                storedBytes = _storedBytes;
            }

            var events = new List<CdcEvent>(Math.Min(eventLimit, frames.Length));
            long encodedBytes = 0;
            bool hasMore = false;

            await using var source = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: CopyBufferSize,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length != storedBytes)
                throw new InvalidDataException("CDC spool 在回放期间发生了未受控的文件长度变化。");

            for (int index = 0; index < frames.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FrameInfo frame = frames[index];
                if (!ShouldReplay(frame.Checkpoint, afterCheckpoint, partitions))
                    continue;

                if (events.Count >= eventLimit
                    || encodedBytes > byteLimit - frame.PayloadLength)
                {
                    hasMore = true;
                    break;
                }

                CdcEvent value = await ReadFrameAsync(source, frame, cancellationToken)
                    .ConfigureAwait(false);
                events.Add(value);
                encodedBytes = checked(encodedBytes + frame.PayloadLength);
            }

            return new CdcEventSpoolBatch(
                new ReadOnlyCollection<CdcEvent>(events),
                encodedBytes,
                hasMore);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>同步回放一个有界批次。</summary>
    /// <param name="afterCheckpoint">可选的起始位点。</param>
    /// <param name="maxEvents">本批次最大事件数。</param>
    /// <param name="maxBytes">本批次最大事件正文 UTF-8 字节数。</param>
    /// <returns>本批次事件列表。</returns>
    public IReadOnlyList<CdcEvent> Replay(
        CdcCheckpoint? afterCheckpoint = null,
        int? maxEvents = null,
        long? maxBytes = null)
        => ReplayAsync(afterCheckpoint, maxEvents, maxBytes).AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// 确认某个分区的 checkpoint，并物理删除所有已确认帧。
    /// </summary>
    /// <param name="checkpoint">要确认的分区位点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentException">分区不存在或 checkpoint 倒退时抛出。</exception>
    public async ValueTask AcknowledgeAsync(
        CdcCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ValidateCheckpoint(checkpoint, nameof(checkpoint));
        cancellationToken.ThrowIfCancellationRequested();

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOperational();

            Dictionary<long, PartitionState> candidate;
            List<FrameInfo> retained;
            lock (_stateLock)
            {
                if (!_partitions.TryGetValue(checkpoint.Partition, out PartitionState current))
                {
                    throw new ArgumentException(
                        "不能确认尚未写入的 CDC 分区。",
                        nameof(checkpoint));
                }
                if (checkpoint.Offset < current.AcknowledgedOffset)
                    throw new ArgumentException(
                        "CDC checkpoint 只能单调递增。",
                        nameof(checkpoint));
                if (checkpoint.Offset > current.HighWatermark)
                    throw new ArgumentException(
                        "CDC checkpoint 不能超过已追加事件的最高 offset。",
                        nameof(checkpoint));

                candidate = new Dictionary<long, PartitionState>(_partitions)
                {
                    [checkpoint.Partition] = current with { AcknowledgedOffset = checkpoint.Offset },
                };
                retained = _frames
                    .Where(frame => !IsAcknowledged(frame.Checkpoint, candidate))
                    .ToList();
            }

            // 先发布 checkpoint，再删除帧。崩溃时旧帧仍然存在也只会被安全跳过。
            await SaveCheckpointMetadataAsync(candidate, cancellationToken).ConfigureAwait(false);
            lock (_stateLock)
                _partitions = candidate;

            if (retained.Count == _frames.Count)
                return;

            List<FrameInfo> rewritten = await RewriteLogAsync(retained, cancellationToken)
                .ConfigureAwait(false);
            lock (_stateLock)
            {
                _frames = rewritten;
                _storedBytes = rewritten.Count == 0
                    ? 0
                    : checked(rewritten[^1].FileOffset + rewritten[^1].FrameLength);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>同步确认并截断某个分区的 checkpoint。</summary>
    /// <param name="checkpoint">要确认的分区位点。</param>
    public void Acknowledge(CdcCheckpoint checkpoint)
        => AcknowledgeAsync(checkpoint).AsTask().GetAwaiter().GetResult();

    /// <summary>关闭 spool；关闭不会删除数据文件或 checkpoint 文件。</summary>
    /// <exception cref="TimeoutException">
    /// 正在进行的存储操作未能在有界关闭等待内结束。
    /// </exception>
    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed)
                return;

            if (!_operationGate.Wait(DisposeWaitTimeout))
            {
                throw new TimeoutException(
                    "CDC spool 关闭等待正在进行的存储操作超时；请在操作结束后重试关闭。");
            }
            try
            {
                _disposed = true;
            }
            finally
            {
                _operationGate.Release();
                _writerLease?.Dispose();
                _writerLease = null;
                // 不释放 SemaphoreSlim：Dispose 期间可能仍有已经排队的调用方，
                // 保留门闩让它们醒来后由 EnsureOperational 统一收到关闭错误。
            }
        }
    }

    /// <summary>异步关闭 spool。</summary>
    /// <returns>已完成的关闭操作。</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void LoadCheckpointMetadata()
    {
        if (!File.Exists(_checkpointPath))
            return;

        long length = new FileInfo(_checkpointPath).Length;
        long maxLength = checked(MetadataHeaderSize
            + (long)_options.MaxPartitions * MetadataEntrySize
            + MetadataCrcSize);
        if (length < MetadataHeaderSize + MetadataCrcSize
            || length > maxLength
            || length > MaxMetadataReadBytes
            || length > int.MaxValue)
        {
            throw new InvalidDataException("CDC spool checkpoint 元数据长度越界。");
        }

        byte[] bytes = File.ReadAllBytes(_checkpointPath);
        if (bytes.Length != length)
            throw new InvalidDataException("CDC spool checkpoint 元数据在读取期间发生变化。");
        if (bytes.Length < MetadataHeaderSize + MetadataCrcSize
            || !bytes.AsSpan(0, MetadataMagic.Length).SequenceEqual(MetadataMagic))
        {
            throw new InvalidDataException("CDC spool checkpoint 元数据 magic 无效。");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4));
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4));
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16, 4));
        int reserved = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20, 4));
        if ((version != LegacyMetadataFormatVersion && version != CurrentMetadataFormatVersion)
            || headerSize != MetadataHeaderSize
            || reserved != 0)
            throw new InvalidDataException("CDC spool checkpoint 元数据版本或头部无效。");
        if (count < 0 || count > _options.MaxPartitions)
            throw new InvalidDataException("CDC spool checkpoint 元数据分区数越界。");

        int expectedLength = checked(MetadataHeaderSize + count * MetadataEntrySize + MetadataCrcSize);
        if (bytes.Length != expectedLength)
            throw new InvalidDataException("CDC spool checkpoint 元数据长度与分区数不匹配。");

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(bytes.Length - MetadataCrcSize, MetadataCrcSize));
        uint actualCrc = Crc32.HashToUInt32(bytes.AsSpan(0, bytes.Length - MetadataCrcSize));
        if (storedCrc != actualCrc)
            throw new InvalidDataException("CDC spool checkpoint 元数据 CRC32 不匹配。");

        long previousPartition = -1;
        for (int index = 0; index < count; index++)
        {
            int offset = MetadataHeaderSize + index * MetadataEntrySize;
            long partition = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, 8));
            long acknowledged = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + 8, 8));
            long highWatermark = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + 16, 8));
            if (partition < 0 || partition <= previousPartition
                || (version == LegacyMetadataFormatVersion && acknowledged < 0)
                || (version == CurrentMetadataFormatVersion && acknowledged < -1)
                || highWatermark < 0
                || highWatermark < acknowledged)
            {
                throw new InvalidDataException("CDC spool checkpoint 元数据包含无效分区位点。");
            }

            _partitions.Add(partition, new PartitionState(acknowledged, highWatermark));
            previousPartition = partition;
        }
    }

    private void LoadFrames()
    {
        if (!File.Exists(_filePath))
        {
            // A checkpoint with an unacknowledged high-water mark proves that the
            // missing data file contained events which must still be replayed.
            // Never replace that state with a new empty spool.
            foreach ((long partition, PartitionState state) in _partitions)
            {
                if (state.HighWatermark > state.AcknowledgedOffset)
                {
                    throw new InvalidDataException(
                        $"CDC spool 数据文件缺失，但分区 {partition} 仍有未确认事件。");
                }
            }

            using var create = new FileStream(_filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            create.Flush(flushToDisk: true);
            SonnetDB.Wal.DirectoryFsync.FlushBestEffort(_directory);
            return;
        }

        using var source = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: CopyBufferSize,
            options: FileOptions.SequentialScan);
        long length = source.Length;
        if (length > _options.MaxBytes)
            throw new InvalidDataException("CDC spool 文件超过配置的字节上限。");

        var frames = new List<FrameInfo>();
        var lastOffsets = new Dictionary<long, long>();
        long position = 0;
        for (int index = 0; position < length; index++)
        {
            if (index >= _options.MaxEvents)
                throw new InvalidDataException("CDC spool 文件超过配置的事件数上限。");

            byte[] header = new byte[FrameHeaderSize];
            ReadExactly(source, header, "CDC spool 帧头");
            FrameHeader parsed = ParseFrameHeader(header);
            if (parsed.PayloadLength <= 0 || parsed.PayloadLength > CdcEventCodec.MaxEventBytes)
                throw new InvalidDataException("CDC spool 帧正文长度越界。");

            int frameLength = checked(FrameHeaderSize + parsed.PayloadLength);
            if (frameLength > _options.MaxBytes - position || frameLength > length - position)
                throw new InvalidDataException("CDC spool 帧长度超过文件边界。");

            byte[] payload = new byte[parsed.PayloadLength];
            ReadExactly(source, payload, "CDC spool 帧正文");
            VerifyFrameCrc(header, payload);
            CdcEvent value = DecodeFramePayload(payload, parsed.Checkpoint);

            if (lastOffsets.TryGetValue(parsed.Checkpoint.Partition, out long previous)
                && parsed.Checkpoint.Offset <= previous)
            {
                throw new InvalidDataException("CDC spool 同一分区的 offset 非单调递增。");
            }

            frames.Add(new FrameInfo(position, frameLength, parsed.PayloadLength, parsed.Checkpoint));
            lastOffsets[parsed.Checkpoint.Partition] = parsed.Checkpoint.Offset;
            position = checked(position + frameLength);
            _ = value;
        }

        if (position != length)
            throw new InvalidDataException("CDC spool 文件存在无法解析的尾随数据。");

        foreach ((long partition, PartitionState state) in _partitions)
        {
            if (state.HighWatermark <= state.AcknowledgedOffset)
                continue;
            if (!lastOffsets.TryGetValue(partition, out long observed)
                || observed < state.HighWatermark)
            {
                throw new InvalidDataException(
                    "CDC spool checkpoint high-watermark 指向缺失的未确认帧。");
            }
        }

        foreach ((long partition, long highWatermark) in lastOffsets)
        {
            if (_partitions.TryGetValue(partition, out PartitionState state))
            {
                if (state.HighWatermark > highWatermark)
                    continue;
                _partitions[partition] = state with { HighWatermark = highWatermark };
            }
            else
            {
                if (_partitions.Count >= _options.MaxPartitions)
                    throw new InvalidDataException("CDC spool 分区数超过配置上限。");
                _partitions.Add(partition, new PartitionState(-1, highWatermark));
            }
        }

        lock (_stateLock)
        {
            _frames = frames;
            _storedBytes = length;
        }
    }

    private async ValueTask WriteFrameAsync(
        long expectedOffset,
        byte[] payload,
        CdcCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        byte[] header = CreateFrameHeader(payload, checkpoint);
        await using var destination = new FileStream(
            _filePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: CopyBufferSize,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (destination.Length != expectedOffset)
            throw new InvalidDataException("CDC spool 文件长度与内存状态不一致。");

        await destination.WriteAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    private async ValueTask<CdcEvent> ReadFrameAsync(
        FileStream source,
        FrameInfo expected,
        CancellationToken cancellationToken)
    {
        source.Position = expected.FileOffset;
        byte[] header = new byte[FrameHeaderSize];
        await ReadExactlyAsync(source, header, "CDC spool 回放帧头", cancellationToken)
            .ConfigureAwait(false);
        FrameHeader parsed = ParseFrameHeader(header);
        if (parsed.PayloadLength != expected.PayloadLength
            || parsed.Checkpoint != expected.Checkpoint)
        {
            throw new InvalidDataException("CDC spool 回放帧索引与文件内容不一致。");
        }

        byte[] payload = new byte[parsed.PayloadLength];
        await ReadExactlyAsync(source, payload, "CDC spool 回放帧正文", cancellationToken)
            .ConfigureAwait(false);
        VerifyFrameCrc(header, payload);
        return DecodeFramePayload(payload, parsed.Checkpoint);
    }

    private async ValueTask<List<FrameInfo>> RewriteLogAsync(
        IReadOnlyList<FrameInfo> retained,
        CancellationToken cancellationToken)
    {
        string temporaryPath = _filePath + ".tmp";
        bool published = false;
        var rewritten = new List<FrameInfo>(retained.Count);
        try
        {
            await using (var source = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: CopyBufferSize,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: CopyBufferSize,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (source.Length != _storedBytes)
                    throw new InvalidDataException("CDC spool 截断前文件长度发生变化。");

                byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
                try
                {
                    long nextOffset = 0;
                    foreach (FrameInfo frame in retained)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        source.Position = frame.FileOffset;
                        await CopyExactlyAsync(
                            source,
                            destination,
                            frame.FrameLength,
                            buffer,
                            cancellationToken).ConfigureAwait(false);
                        rewritten.Add(frame with { FileOffset = nextOffset });
                        nextOffset = checked(nextOffset + frame.FrameLength);
                    }

                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    destination.Flush(flushToDisk: true);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _filePath, overwrite: true);
            published = true;
            SonnetDB.Wal.DirectoryFsync.FlushRequired(_directory);
            return rewritten;
        }
        catch (Exception exception) when (published)
        {
            // The canonical file has already changed.  Stop this instance rather
            // than allowing its in-memory frame offsets to reference the old file.
            MarkFault(exception);
            throw;
        }
        finally
        {
            if (!published)
                TryDeleteTemporary(temporaryPath);
        }
    }

    private async ValueTask SaveCheckpointMetadataAsync(
        IReadOnlyDictionary<long, PartitionState> partitions,
        CancellationToken cancellationToken)
    {
        var entries = partitions
            .Where(static pair => pair.Value.HighWatermark >= 0)
            .OrderBy(static pair => pair.Key)
            .ToArray();
        if (entries.Length > _options.MaxPartitions)
            throw new InvalidOperationException("CDC spool checkpoint 分区数超过上限。");

        int contentLength = checked(MetadataHeaderSize + entries.Length * MetadataEntrySize);
        byte[] content = new byte[checked(contentLength + MetadataCrcSize)];
        MetadataMagic.CopyTo(content.AsSpan(0, MetadataMagic.Length));
        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(8, 4), CurrentMetadataFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(12, 4), MetadataHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(16, 4), entries.Length);
        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(20, 4), 0);

        for (int index = 0; index < entries.Length; index++)
        {
            int offset = MetadataHeaderSize + index * MetadataEntrySize;
            (long partition, PartitionState state) = entries[index];
            BinaryPrimitives.WriteInt64LittleEndian(content.AsSpan(offset, 8), partition);
            BinaryPrimitives.WriteInt64LittleEndian(content.AsSpan(offset + 8, 8), state.AcknowledgedOffset);
            BinaryPrimitives.WriteInt64LittleEndian(content.AsSpan(offset + 16, 8), state.HighWatermark);
        }

        uint crc = Crc32.HashToUInt32(content.AsSpan(0, contentLength));
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(contentLength, MetadataCrcSize), crc);

        string temporaryPath = _checkpointPath + ".tmp";
        bool published = false;
        try
        {
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await destination.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _checkpointPath, overwrite: true);
            published = true;
            SonnetDB.Wal.DirectoryFsync.FlushRequired(_directory);
        }
        catch (Exception exception) when (published)
        {
            MarkFault(exception);
            throw;
        }
        finally
        {
            if (!published)
                TryDeleteTemporary(temporaryPath);
        }
    }

    private static bool ShouldReplay(
        CdcCheckpoint checkpoint,
        CdcCheckpoint? explicitCheckpoint,
        IReadOnlyDictionary<long, PartitionState> partitions)
    {
        if (explicitCheckpoint is CdcCheckpoint start)
            return checkpoint.Partition == start.Partition && checkpoint.Offset > start.Offset;

        return !partitions.TryGetValue(checkpoint.Partition, out PartitionState state)
            || state.AcknowledgedOffset < 0
            || checkpoint.Offset > state.AcknowledgedOffset;
    }

    private static bool IsAcknowledged(
        CdcCheckpoint checkpoint,
        IReadOnlyDictionary<long, PartitionState> partitions)
        => partitions.TryGetValue(checkpoint.Partition, out PartitionState state)
            && state.AcknowledgedOffset >= checkpoint.Offset;

    private static byte[] CreateFrameHeader(byte[] payload, CdcCheckpoint checkpoint)
    {
        byte[] header = new byte[FrameHeaderSize];
        FrameMagic.CopyTo(header.AsSpan(0, FrameMagic.Length));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), CurrentFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), FrameHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, 4), 0);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24, 8), checkpoint.Partition);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32, 8), checkpoint.Offset);
        uint crc = ComputeFrameCrc(header.AsSpan(0, FrameCrcCoveredBytes), payload);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), crc);
        return header;
    }

    private static FrameHeader ParseFrameHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != FrameHeaderSize
            || !header[..FrameMagic.Length].SequenceEqual(FrameMagic))
        {
            throw new InvalidDataException("CDC spool 帧 magic 无效。");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4));
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4));
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(16, 4));
        int reserved = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(20, 4));
        long partition = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(24, 8));
        long offset = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(32, 8));
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(40, 4));
        if (version != CurrentFormatVersion || headerSize != FrameHeaderSize || reserved != 0)
            throw new InvalidDataException("CDC spool 帧版本或头部无效。");
        if (payloadLength <= 0 || payloadLength > CdcEventCodec.MaxEventBytes)
            throw new InvalidDataException("CDC spool 帧正文长度无效。");
        ValidateCheckpoint(new CdcCheckpoint(partition, offset), "CDC spool 帧 checkpoint");
        return new FrameHeader(payloadLength, new CdcCheckpoint(partition, offset), crc);
    }

    private static void VerifyFrameCrc(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
    {
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(40, 4));
        uint actual = ComputeFrameCrc(header[..FrameCrcCoveredBytes], payload);
        if (stored != actual)
            throw new InvalidDataException("CDC spool 帧 CRC32 不匹配。");
    }

    private static uint ComputeFrameCrc(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
    {
        var crc = new Crc32();
        crc.Append(header);
        crc.Append(payload);
        return crc.GetCurrentHashAsUInt32();
    }

    private static CdcEvent DecodeFramePayload(byte[] payload, CdcCheckpoint checkpoint)
    {
        try
        {
            CdcEvent value = CdcEventCodec.Decode(payload);
            if (value.Metadata.Checkpoint != checkpoint)
                throw new InvalidDataException("CDC spool 帧 checkpoint 与事件正文不一致。");
            return value;
        }
        catch (CdcFormatException exception)
        {
            throw new InvalidDataException("CDC spool 帧正文不是有效 CDC 事件。", exception);
        }
    }

    private static void ValidateCheckpoint(CdcCheckpoint checkpoint, string parameterName)
    {
        if (checkpoint.Partition < 0)
            throw new ArgumentOutOfRangeException(parameterName, "partition 不能为负数。");
        if (checkpoint.Offset < 0)
            throw new ArgumentOutOfRangeException(parameterName, "offset 不能为负数。");
    }

    private static void ReadExactly(Stream source, byte[] destination, string description)
    {
        int total = 0;
        int maxOperations = destination.Length + 1;
        for (int operation = 0; total < destination.Length && operation < maxOperations; operation++)
        {
            int read = source.Read(destination, total, destination.Length - total);
            if (read == 0)
                break;
            total += read;
        }

        if (total != destination.Length)
            throw new InvalidDataException($"{description} 被截断。");
    }

    private static async ValueTask ReadExactlyAsync(
        Stream source,
        byte[] destination,
        string description,
        CancellationToken cancellationToken)
    {
        int total = 0;
        int maxOperations = destination.Length + 1;
        for (int operation = 0; total < destination.Length && operation < maxOperations; operation++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await source.ReadAsync(
                destination.AsMemory(total, destination.Length - total),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }

        if (total != destination.Length)
            throw new InvalidDataException($"{description} 被截断。");
    }

    private static async ValueTask CopyExactlyAsync(
        Stream source,
        Stream destination,
        int count,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        int remaining = count;
        int maxOperations = count + 1;
        for (int operation = 0; remaining > 0 && operation < maxOperations; operation++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requested = Math.Min(buffer.Length, remaining);
            int read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new InvalidDataException("CDC spool 截断时发现源帧不完整。");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            remaining -= read;
        }

        if (remaining != 0)
            throw new InvalidDataException("CDC spool 截断时超过了有界复制次数。");
    }

    private void EnsureOperational()
    {
        if (Volatile.Read(ref _disposed))
            throw new ObjectDisposedException(nameof(CdcEventSpool));
        Exception? fault = Volatile.Read(ref _fault);
        if (fault is not null)
        {
            throw new InvalidDataException(
                "CDC spool 已因 append partial frame 回滚失败而停止；请关闭并重新打开。",
                fault);
        }
    }

    private void MarkFault(Exception exception)
        => Interlocked.CompareExchange(ref _fault, exception, comparand: null);

    private bool TryRollbackAppend(long expectedLength)
    {
        try
        {
            using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: CopyBufferSize,
                options: FileOptions.SequentialScan);
            if (stream.Length < expectedLength)
                return false;
            if (stream.Length != expectedLength)
            {
                stream.SetLength(expectedLength);
                stream.Flush(flushToDisk: true);
                SonnetDB.Wal.DirectoryFsync.FlushBestEffort(_directory);
            }

            return stream.Length == expectedLength;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct FrameHeader(
        int PayloadLength,
        CdcCheckpoint Checkpoint,
        uint Crc32);

    private readonly record struct FrameInfo(
        long FileOffset,
        int FrameLength,
        int PayloadLength,
        CdcCheckpoint Checkpoint);

    private readonly record struct PartitionState(
        long AcknowledgedOffset,
        long HighWatermark);
}
