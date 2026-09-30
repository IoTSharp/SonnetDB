using System.Security.Cryptography;
using System.Text;
using SonnetDB.Documents;

namespace SonnetDB.Cdc;

/// <summary>
/// 文档集合 CDC 源捕获的身份、分区和有界批次设置。
/// </summary>
public sealed record CdcDocumentSourceCaptureOptions
{
    /// <summary>源数据库或连接的稳定标识。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>源文档集合名称。</summary>
    public string Entity { get; init; } = string.Empty;

    /// <summary>事件 schema 的稳定名称。</summary>
    public string Schema { get; init; } = "documents";

    /// <summary>事件 schema 版本。</summary>
    public int SchemaVersion { get; init; } = CdcEventCodec.CurrentSchemaVersion;

    /// <summary>源 change feed 所属的分区编号。</summary>
    public long Partition { get; init; }

    /// <summary>单次从源 change feed 读取的最大事件数。</summary>
    public int BatchSize { get; init; } = 256;

    /// <summary>单次从源读取并追加的最大事件正文总字节数。</summary>
    public long MaxBatchBytes { get; init; } = CdcEventSpoolOptions.DefaultMaxReplayBytes;

    internal void Validate(string expectedEntity)
    {
        ValidateIdentifier(Source, nameof(Source));
        ValidateIdentifier(Entity, nameof(Entity));
        ValidateIdentifier(Schema, nameof(Schema));
        if (!string.Equals(Entity, expectedEntity, StringComparison.Ordinal))
            throw new ArgumentException("CDC 源捕获实体必须与文档集合名称一致。", nameof(Entity));
        if (SchemaVersion != CdcEventCodec.CurrentSchemaVersion)
            throw new CdcUnsupportedSchemaVersionException(Schema, SchemaVersion);
        ArgumentOutOfRangeException.ThrowIfNegative(Partition);
        ArgumentOutOfRangeException.ThrowIfLessThan(BatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BatchSize, 1_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBatchBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBatchBytes, CdcEventSpoolOptions.DefaultMaxBytes);
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (System.Text.Encoding.UTF8.GetByteCount(value) > CdcEventCodec.MaxStringBytes)
            throw new ArgumentException("CDC 源捕获标识超过 UTF-8 字节上限。", parameterName);
    }
}

/// <summary>
/// 一次文档集合 CDC 源捕获的结果。
/// </summary>
/// <param name="StartSequence">本次读取前的源 change feed 游标。</param>
/// <param name="EndSequence">本次完成追加后可恢复的源 change feed 游标。</param>
/// <param name="CapturedEvents">本次成功持久化到 spool 的事件数。</param>
/// <param name="HasMore">源或本次批次边界之后是否仍有待捕获事件。</param>
public sealed record CdcDocumentSourceCaptureResult(
    long StartSequence,
    long EndSequence,
    int CapturedEvents,
    bool HasMore);

/// <summary>
/// 将单个文档集合的持久 change feed 捕获到专用 CDC spool。
/// </summary>
/// <remarks>
/// 捕获器不参与文档写事务；主文档和 change feed 已由
/// <see cref="DocumentCollectionStore"/> 在同一 KV batch 中提交。追加成功前不推进恢复游标，
/// 因此进程在追加失败或重启后会从 spool 的确认位点和未确认帧重建连续游标。
/// </remarks>
public sealed class CdcDocumentSourceCapture : IDisposable, IAsyncDisposable
{
    private readonly DocumentCollectionStore _source;
    private readonly CdcEventSpool _spool;
    private readonly CdcDocumentSourceCaptureOptions _options;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _disposeLock = new();
    private bool _disposed;

    /// <summary>
    /// 创建单集合、单分区的文档 CDC 源捕获器。
    /// </summary>
    /// <param name="source">包含持久 change feed 的文档集合。</param>
    /// <param name="spool">仅供此捕获器和对应接收端使用的 CDC spool。</param>
    /// <param name="options">源身份、schema 和批次边界。</param>
    public CdcDocumentSourceCapture(
        DocumentCollectionStore source,
        CdcEventSpool spool,
        CdcDocumentSourceCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(source.Schema.Name);
        _source = source;
        _spool = spool;
        _options = options;
    }

    /// <summary>
    /// 捕获并持久化一批源端变更；快照期间调用方应继续调用此方法保留边界后的事件。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次捕获前后的可恢复游标和后续标志。</returns>
    public async ValueTask<CdcDocumentSourceCaptureResult> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SemaphoreSlim pipelineGate = CdcLocalPipelineGate.For(_spool);
            await pipelineGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await CaptureLockedAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                pipelineGate.Release();
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async ValueTask<CdcDocumentSourceCaptureResult> CaptureLockedAsync(CancellationToken cancellationToken)
    {
        EnsureOperational();
        long startSequence = await RecoverSpoolSequenceAsync(cancellationToken).ConfigureAwait(false);
        long sourceLatestSequence = _source.LatestChangeSequence;
        if (startSequence > sourceLatestSequence)
            throw new InvalidDataException("CDC spool 确认位点超过源集合当前 change feed 序号。");
        DocumentChangeFeedPage page = _source.ReadChangeFeed(startSequence, _options.BatchSize);
        if (page.Changes.Count == 0 && page.LatestSequence > startSequence)
            throw new InvalidDataException("文档 change feed 缺少尚未捕获的源事件。");
        long cursor = startSequence;
        int captured = 0;
        long encodedBytes = 0;

        foreach (DocumentChangeFeedEntry change in page.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (change.Sequence != cursor + 1)
                throw new InvalidDataException("文档 change feed 序号存在缺口，不能继续捕获。");
            if (change.PayloadTruncated)
                throw new InvalidDataException("文档 change feed 载荷已截断，不能生成可重放 CDC 事件。");

            CdcEvent value = CreateEvent(change);
            byte[] encoded = CdcEventCodec.Encode(value);
            if (encodedBytes > _options.MaxBatchBytes - encoded.Length)
                throw new InvalidOperationException("CDC 源捕获批次超过字节上限。");

            // AppendAsync 的 durable frame 是游标推进的提交点；异常时不更新局部游标。
            await _spool.AppendAsync(value, cancellationToken).ConfigureAwait(false);
            encodedBytes = checked(encodedBytes + encoded.Length);
            cursor = change.Sequence;
            captured++;
        }

        bool hasMore = page.HasMore || _source.LatestChangeSequence > cursor;
        return new CdcDocumentSourceCaptureResult(startSequence, cursor, captured, hasMore);
    }

    /// <summary>关闭捕获器并释放其操作闸门；源集合和 spool 由调用方关闭。</summary>
    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed)
                return;
            _operationGate.Wait();
            try
            {
                _disposed = true;
            }
            finally
            {
                _operationGate.Release();
                _operationGate.Dispose();
            }
        }
    }

    /// <summary>异步关闭捕获器。</summary>
    /// <returns>已经完成的关闭操作。</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<long> RecoverSpoolSequenceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<long, long> highWatermarks = _spool.PartitionHighWatermarks;
        if (highWatermarks.Keys.Any(partition => partition != _options.Partition))
            throw new InvalidDataException("CDC 源捕获 spool 包含其它分区的事件。");
        IReadOnlyDictionary<long, long> acknowledgements = _spool.AcknowledgedCheckpoints;
        if (acknowledgements.Keys.Any(partition => partition != _options.Partition))
            throw new InvalidDataException("CDC 源捕获 spool 包含其它分区的确认位点。");
        long cursor = acknowledgements.TryGetValue(_options.Partition, out long acknowledged)
            ? acknowledged
            : 0;
        if (acknowledged < 0)
            cursor = 0;

        CdcCheckpoint? afterCheckpoint = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CdcEventSpoolBatch batch = await _spool.ReplayBatchAsync(
                afterCheckpoint: afterCheckpoint,
                maxEvents: null,
                maxBytes: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (batch.Events.Count == 0)
            {
                if (batch.HasMore)
                    throw new InvalidOperationException("CDC spool 回放字节上限无法容纳下一条源事件。");
                if (highWatermarks.TryGetValue(_options.Partition, out long highWatermark)
                    && cursor != highWatermark)
                    throw new InvalidDataException("CDC spool 高水位与已校验的源捕获位点不一致。");
                return cursor;
            }

            foreach (CdcEvent value in batch.Events)
            {
                ValidateCapturedEvent(value);
                long expected = checked(cursor + 1);
                if (value.Metadata.Checkpoint.Offset != expected || value.Sequence != expected)
                    throw new InvalidDataException("CDC spool 中源捕获位点存在缺口或错配。");
                cursor = expected;
            }

            if (!batch.HasMore)
            {
                if (highWatermarks.TryGetValue(_options.Partition, out long highWatermark)
                    && cursor != highWatermark)
                    throw new InvalidDataException("CDC spool 高水位与已校验的源捕获位点不一致。");
                return cursor;
            }
            afterCheckpoint = new CdcCheckpoint(_options.Partition, cursor);
        }
    }

    private CdcEvent CreateEvent(DocumentChangeFeedEntry change)
    {
        CdcOperation operation = change.Operation switch
        {
            "insert" => CdcOperation.Insert,
            "update" => CdcOperation.Update,
            "delete" => CdcOperation.Delete,
            _ => throw new InvalidDataException($"未知文档 change feed 操作 '{change.Operation}'。"),
        };
        if (operation is CdcOperation.Insert or CdcOperation.Update && change.AfterJson is null)
            throw new InvalidDataException("文档 change feed 插入或更新缺少完整 after JSON。");
        if (operation == CdcOperation.Delete && change.AfterJson is not null)
            throw new InvalidDataException("文档 change feed 删除包含 after JSON。");
        if (change.Sequence < 0)
            throw new InvalidDataException("文档 change feed 序号不能为负数。");

        return new CdcEvent(
            CreateEventId(change.Sequence),
            _options.Source,
            _options.Entity,
            change.DocumentId,
            change.Sequence,
            change.OccurredAtUtc,
            new CdcEventMetadata(
                CdcEventCodec.CurrentContractVersion,
                _options.Schema,
                _options.SchemaVersion,
                operation,
                new CdcCheckpoint(_options.Partition, change.Sequence)),
            change.BeforeJson,
            change.AfterJson);
    }

    private string CreateEventId(long sequence)
    {
        string identity = $"{_options.Source.Length}:{_options.Source}{_options.Entity.Length}:{_options.Entity}{_options.Partition}:{sequence}";
        return "cdc-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private void ValidateCapturedEvent(CdcEvent value)
    {
        if (value.Source != _options.Source
            || value.Entity != _options.Entity
            || value.Metadata.Schema != _options.Schema
            || value.Metadata.SchemaVersion != _options.SchemaVersion
            || value.Metadata.Checkpoint.Partition != _options.Partition)
        {
            throw new InvalidDataException("CDC spool 事件身份与源捕获配置不一致。");
        }
        if (value.Sequence != value.Metadata.Checkpoint.Offset)
            throw new InvalidDataException("CDC spool 事件 sequence 与 checkpoint offset 不一致。");
    }

    private void EnsureOperational()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
