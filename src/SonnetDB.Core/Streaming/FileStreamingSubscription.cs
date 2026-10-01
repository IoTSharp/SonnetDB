using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Cdc;

namespace SonnetDB.Streaming;

/// <summary>
/// 使用有界 CDC spool、条件检查点和版本化状态文件实现本地持久订阅。
/// </summary>
/// <remarks>
/// <para>事件、watermark 与未确认批次在关闭和重开后恢复，投递语义为至少一次。</para>
/// <para>同一目录只允许一个执行者持有文件锁；没有分布式租约、远程投递或业务副作用事务。</para>
/// <para>存储操作失败后必须重开；检查点提交结果可能未知，不能假设失败的确认已回滚。</para>
/// </remarks>
public sealed class FileStreamingSubscription : IAsyncDisposable
{
    private const int LegacyFormatVersion = 1;
    private const int FormatVersion = 2;
    private const int LegacyMaxDeliveryAttempts = 100;
    private const int MaxStateBytes = 64 * 1024;
    private const int MaxHeaders = 64;
    private const string EventSchema = "sonnetdb.streaming-event";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly FileStream _lease;
    private readonly CdcEventSpool _spool;
    private readonly FileStreamingSubscriptionCheckpointStore _checkpointStore;
    private FileStreamingSubscriptionState _state;
    private TaskCompletionSource _changed = NewSignal();
    private Exception? _fault;
    private int _disposed;

    private FileStreamingSubscription(
        string directoryPath,
        FileStream lease,
        CdcEventSpool spool,
        FileStreamingSubscriptionCheckpointStore checkpointStore,
        FileStreamingSubscriptionState state)
    {
        DirectoryPath = directoryPath;
        _lease = lease;
        _spool = spool;
        _checkpointStore = checkpointStore;
        _state = state;
    }

    /// <summary>订阅存储目录的绝对路径。</summary>
    public string DirectoryPath { get; }

    /// <summary>创建时保存的订阅定义。</summary>
    public StreamingSubscriptionDefinition Definition => Volatile.Read(ref _state).Definition;

    /// <summary>创建时保存的文件容量与操作边界。</summary>
    public FileStreamingSubscriptionOptions Options => Volatile.Read(ref _state).Options;

    /// <summary>最近一次成功返回确认的持久检查点。</summary>
    public StreamingSubscriptionCheckpoint Checkpoint => Volatile.Read(ref _state).Checkpoint;

    /// <summary>已持久化的当前事件时间 watermark。</summary>
    public DateTimeOffset WatermarkUtc => Volatile.Read(ref _state).WatermarkUtc;

    /// <summary>尚未确认的事件数，包含正在投递的批次。</summary>
    public int PendingEventCount => Volatile.Read(ref _state).PendingEventCount;

    /// <summary>最近已接受并可恢复的事件序号，供发布源在重开后继续；初始值为 -1。</summary>
    public long LastAcceptedSequence => Volatile.Read(ref _state).LastAcceptedSequence;

    /// <summary>事件 spool 当前占用的字节数，包含帧头。</summary>
    public long StoredBytes => _spool.StoredBytes;

    /// <summary>
    /// 查询当前文件订阅的本地运维状态，包括 backlog 数量、最早事件时间和未确认批次投递次数。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>带有查询时刻的本地状态快照。</returns>
    public async ValueTask<FileStreamingSubscriptionStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(async token =>
        {
            FileStreamingSubscriptionState state = _state;
            DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
            DateTimeOffset? oldestEventTimeUtc = null;
            if (state.PendingEventCount > 0)
            {
                CdcEventSpoolBatch replay = await _spool.ReplayBatchAsync(
                    afterCheckpoint: state.Checkpoint.CommittedSequence < 0
                        ? null
                        : new CdcCheckpoint(0, state.Checkpoint.CommittedSequence),
                    maxEvents: 1,
                    maxBytes: Options.MaxBatchBytes,
                    cancellationToken: token).ConfigureAwait(false);
                if (replay.Events.Count == 0)
                    throw new InvalidDataException("持久订阅声明有未确认事件，但 spool 未提供状态快照事件。");
                oldestEventTimeUtc = DecodeEvent(replay.Events[0]).EventTimeUtc;
            }

            FileStreamingDeliveryState? inFlight = state.InFlight;
            TimeSpan? oldestEventAge = oldestEventTimeUtc is { } eventTime
                ? observedAtUtc - eventTime
                : null;
            if (oldestEventAge < TimeSpan.Zero)
                oldestEventAge = TimeSpan.Zero;

            return new FileStreamingSubscriptionStatus(
                state.Definition.SubscriptionId,
                state.Definition.StreamName,
                state.PublishingCompleted,
                state.Checkpoint.CommittedSequence,
                state.LastAcceptedSequence,
                state.PendingEventCount,
                _spool.StoredBytes,
                state.WatermarkUtc,
                inFlight?.DeliveryId,
                inFlight?.Attempt ?? 0,
                state.Options.MaxDeliveryAttempts,
                inFlight?.EventCount ?? 0,
                oldestEventTimeUtc,
                oldestEventAge,
                observedAtUtc);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 创建或重开持久订阅，并校验定义、容量、检查点与未确认事件。
    /// </summary>
    /// <param name="directoryPath">专属于该订阅的存储目录。</param>
    /// <param name="definition">订阅定义，重开时必须与已保存定义完全相同。</param>
    /// <param name="options">容量与操作边界，重开时必须与已保存边界完全相同。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已恢复并持有单执行者文件锁的订阅。</returns>
    public static async ValueTask<FileStreamingSubscription> OpenAsync(
        string directoryPath,
        StreamingSubscriptionDefinition definition,
        FileStreamingSubscriptionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        options ??= new FileStreamingSubscriptionOptions();
        options.Validate();
        ValidateString(definition.SubscriptionId, FileStreamingSubscriptionCheckpointStore.MaxSubscriptionIdBytes);
        ValidateString(definition.StreamName, CdcEventCodec.MaxStringBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(definition.Capacity, CdcEventSpoolOptions.DefaultMaxEvents);
        cancellationToken.ThrowIfCancellationRequested();
        string directory = Path.GetFullPath(directoryPath);
        Directory.CreateDirectory(directory);
        var lease = new FileStream(
            Path.Combine(directory, "subscription.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
        CdcEventSpool? spool = null;
        FileStreamingSubscriptionCheckpointStore? checkpointStore = null;
        try
        {
            using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operationSource.CancelAfter(options.OperationTimeoutMilliseconds);
            CancellationToken token = operationSource.Token;
            string statePath = Path.Combine(directory, "subscription.json");
            (FileStreamingSubscriptionState? state, bool migrated) =
                await ReadStateAsync(statePath, token).ConfigureAwait(false);
            if (state is null
                && (File.Exists(Path.Combine(directory, "events.spool"))
                    || File.Exists(Path.Combine(directory, "events.spool.checkpoint"))
                    || Directory.Exists(Path.Combine(directory, "checkpoints"))))
            {
                throw new InvalidDataException("持久订阅状态文件缺失，但目录中仍有订阅数据；不能重置为空订阅。");
            }

            if (state is not null)
            {
                ValidateState(state);
                if (state.Definition != definition || state.Options != options)
                    throw new InvalidDataException("持久订阅定义或文件容量配置与已保存状态不匹配。");
                if (migrated)
                    await WriteStateAsync(directory, state, token).ConfigureAwait(false);
            }

            checkpointStore = new FileStreamingSubscriptionCheckpointStore(
                Path.Combine(directory, "checkpoints"),
                TimeSpan.FromMilliseconds(options.OperationTimeoutMilliseconds));
            spool = new CdcEventSpool(Path.Combine(directory, "events.spool"), new CdcEventSpoolOptions
            {
                MaxEvents = definition.Capacity,
                MaxBytes = options.MaxStoredBytes,
                MaxPartitions = 1,
                MaxReplayEvents = definition.BatchSize,
                MaxReplayBytes = options.MaxBatchBytes,
            }, token);
            if (state is null)
            {
                var initial = StreamingSubscriptionCheckpoint.Create(definition.SubscriptionId);
                await checkpointStore.SaveAsync(initial, expectedRevision: -1, token).ConfigureAwait(false);
                state = new FileStreamingSubscriptionState(
                    FormatVersion, definition, options, initial, DateTimeOffset.MinValue,
                    -1, 0, false, null);
                await WriteStateAsync(directory, state, token).ConfigureAwait(false);
            }

            var subscription = new FileStreamingSubscription(directory, lease, spool, checkpointStore, state);
            await subscription.RecoverAsync(token).ConfigureAwait(false);
            return subscription;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await ReleaseResourcesAsync(checkpointStore, spool, lease, suppressErrors: true).ConfigureAwait(false);
            throw new TimeoutException("打开持久订阅超过存储操作超时边界。", exception);
        }
        catch
        {
            await ReleaseResourcesAsync(checkpointStore, spool, lease, suppressErrors: true).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>持久化单调前进的 UTC watermark，重开后仍用于迟到判定。</summary>
    /// <param name="watermarkUtc">新的 UTC watermark。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async ValueTask AdvanceWatermarkAsync(
        DateTimeOffset watermarkUtc,
        CancellationToken cancellationToken = default)
    {
        if (watermarkUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("watermark 必须为 UTC。", nameof(watermarkUtc));
        await ExecuteAsync(async token =>
        {
            if (_state.PublishingCompleted)
                throw new InvalidOperationException("已结束发布的订阅不能推进 watermark。");
            if (watermarkUtc < _state.WatermarkUtc)
                throw new ArgumentOutOfRangeException(nameof(watermarkUtc), "watermark 不能回退。");
            await PersistAsync(_state with { WatermarkUtc = watermarkUtc }, token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 持久化事件；事件数或磁盘容量已满时等待确认回收空间，等待可以取消。
    /// </summary>
    /// <param name="value">序号严格递增的事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>事件持久化成功或按迟到策略丢弃的结果。</returns>
    public async ValueTask<StreamingPublishResult> PublishAsync(
        StreamingEvent value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateEvent(value, Options);
        StreamingEvent snapshot = Clone(value);
        for (; ; )
        {
            (StreamingPublishResult? result, Task? wait) = await ExecuteAsync(async token =>
            {
                if (_state.PublishingCompleted)
                    throw new InvalidOperationException("订阅发布端已经结束。");
                if (snapshot.Sequence <= _state.LastAcceptedSequence)
                    throw new ArgumentException("事件序号必须严格大于最近接受的序号。", nameof(value));
                bool isLate = IsLate(snapshot.EventTimeUtc, _state.WatermarkUtc, Definition.AllowedLateness);
                if (isLate && Definition.LateEventPolicy == StreamingLateEventPolicy.Drop)
                    return ((StreamingPublishResult?)Result(StreamingPublishDisposition.DroppedLate), (Task?)null);
                if (isLate && Definition.LateEventPolicy == StreamingLateEventPolicy.Reject)
                    throw new StreamingLateEventException(snapshot.EventId);

                CdcEvent envelope = EncodeEvent(snapshot with { IsLate = isLate });
                int encodedBytes = CdcEventCodec.Encode(envelope).Length;
                long frameBytes = CdcEventSpool.FrameHeaderSize + (long)encodedBytes;
                if (encodedBytes > Options.MaxBatchBytes || frameBytes > Options.MaxStoredBytes)
                    throw new ArgumentException("单个事件超过批次或磁盘容量上限，无法投递。", nameof(value));
                if (_state.PendingEventCount >= Definition.Capacity
                    || _spool.StoredBytes > Options.MaxStoredBytes - frameBytes)
                {
                    return ((StreamingPublishResult?)null, (Task?)_changed.Task);
                }

                try
                {
                    await _spool.AppendAsync(envelope, token).ConfigureAwait(false);
                    await PersistAsync(_state with
                    {
                        LastAcceptedSequence = snapshot.Sequence,
                        PendingEventCount = _state.PendingEventCount + 1,
                    }, token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Fault(exception);
                    throw;
                }

                Pulse();
                return ((StreamingPublishResult?)Result(StreamingPublishDisposition.Accepted), (Task?)null);

                StreamingPublishResult Result(StreamingPublishDisposition disposition)
                    => new(disposition, snapshot.EventId, snapshot.Sequence, _state.WatermarkUtc);
            }, cancellationToken).ConfigureAwait(false);
            if (result is not null)
                return result;
            await wait!.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 读取一个有界持久批次；未确认批次保留投递标识并增加次数，重开后继续重投。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>投递批次；发布结束且全部事件已确认时返回空值。</returns>
    public async ValueTask<StreamingDeliveryBatch?> ReadBatchAsync(CancellationToken cancellationToken = default)
    {
        for (; ; )
        {
            (StreamingDeliveryBatch? batch, Task? wait) = await ExecuteAsync(async token =>
            {
                if (_state.PendingEventCount == 0)
                    return ((StreamingDeliveryBatch?)null, _state.PublishingCompleted ? null : _changed.Task);

                FileStreamingDeliveryState? previous = _state.InFlight;
                CdcEventSpoolBatch replay = await _spool.ReplayBatchAsync(
                    maxEvents: previous?.EventCount ?? Definition.BatchSize,
                    cancellationToken: token).ConfigureAwait(false);
                StreamingEvent[] events = replay.Events.Select(DecodeEvent).ToArray();
                if (events.Length == 0)
                    throw new InvalidDataException("持久订阅声明有未确认事件，但 spool 未提供可投递事件。");
                FileStreamingDeliveryState delivery;
                StreamingDeliveryStatus status;
                if (previous is not null)
                {
                    if (previous.Attempt >= Options.MaxDeliveryAttempts)
                    {
                        throw new FileStreamingDeliveryAttemptLimitException(
                            previous.DeliveryId,
                            previous.Attempt,
                            Options.MaxDeliveryAttempts);
                    }

                    ValidateBatchEvents(previous, events);
                    delivery = previous with { Attempt = checked(previous.Attempt + 1) };
                    status = StreamingDeliveryStatus.Redelivered;
                }
                else
                {
                    delivery = new FileStreamingDeliveryState(
                        Guid.NewGuid().ToString("N"), 1, events[0].Sequence, events[^1].Sequence,
                        events.Length, _state.Checkpoint.Advance(events[^1].Sequence, _state.WatermarkUtc));
                    status = StreamingDeliveryStatus.InFlight;
                }

                await PersistAsync(_state with { InFlight = delivery }, token).ConfigureAwait(false);
                return ((StreamingDeliveryBatch?)new StreamingDeliveryBatch(
                    delivery.DeliveryId, delivery.Attempt, status, events, delivery.CandidateCheckpoint), (Task?)null);
            }, cancellationToken).ConfigureAwait(false);
            if (wait is null)
                return batch;
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 先条件持久化检查点，再回收已确认事件；返回成功表示确认已持久化。
    /// </summary>
    /// <param name="deliveryId">当前未确认批次的投递标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>确认结果和已保存检查点。</returns>
    public async ValueTask<StreamingDeliveryReceipt> AcknowledgeAsync(
        string deliveryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        return await ExecuteAsync(async token =>
        {
            FileStreamingDeliveryState? delivery = _state.InFlight;
            if (delivery is null || !string.Equals(delivery.DeliveryId, deliveryId, StringComparison.Ordinal))
                throw new InvalidOperationException("没有匹配的未确认持久订阅批次。");
            StreamingSubscriptionCheckpoint checkpoint = delivery.CandidateCheckpoint with
            {
                WatermarkUtc = _state.WatermarkUtc,
            };
            try
            {
                await _checkpointStore.SaveAsync(checkpoint, _state.Checkpoint.Revision, token).ConfigureAwait(false);
                await _spool.AcknowledgeAsync(new CdcCheckpoint(0, checkpoint.CommittedSequence), token).ConfigureAwait(false);
                await PersistAsync(_state with
                {
                    Checkpoint = checkpoint,
                    PendingEventCount = _state.PendingEventCount - delivery.EventCount,
                    InFlight = null,
                }, token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Fault(exception);
                throw;
            }

            Pulse();
            return new StreamingDeliveryReceipt(
                deliveryId, delivery.Attempt, StreamingDeliveryStatus.Acknowledged, checkpoint);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>持久化发布完成标记；已接受事件和未确认批次仍可读取及确认。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(async token =>
        {
            if (!_state.PublishingCompleted)
                await PersistAsync(_state with { PublishingCompleted = true }, token).ConfigureAwait(false);
            Pulse();
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>关闭文件句柄并唤醒等待调用；全部持久状态保留供后续重开。</summary>
    public async ValueTask DisposeAsync()
    {
        if (!await _operationGate.WaitAsync(TimeSpan.FromMilliseconds(Options.OperationTimeoutMilliseconds)).ConfigureAwait(false))
            throw new TimeoutException("关闭持久订阅等待存储操作超时。");
        try
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Pulse();
            await ReleaseResourcesAsync(_checkpointStore, _spool, _lease, suppressErrors: false).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static async Task ReleaseResourcesAsync(
        FileStreamingSubscriptionCheckpointStore? checkpointStore,
        CdcEventSpool? spool,
        FileStream lease,
        bool suppressErrors)
    {
        Exception? failure = null;
        try
        {
            if (checkpointStore is not null)
                await checkpointStore.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            try
            {
                if (spool is not null)
                    await spool.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            finally
            {
                try
                {
                    lease.Dispose();
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }
        }

        // 打开失败时保留调用方看到的原始异常；正常关闭则报告最早发生的清理异常。
        if (!suppressErrors && failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken callerToken)
    {
        using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        operationSource.CancelAfter(Options.OperationTimeoutMilliseconds);
        try
        {
            await _operationGate.WaitAsync(operationSource.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (_fault is not null)
                    throw new InvalidOperationException("持久订阅存储操作失败，必须关闭并重开。", _fault);
                try
                {
                    return await operation(operationSource.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException)
                {
                    Fault(exception);
                    throw;
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new TimeoutException("持久订阅操作超过存储操作超时边界。");
        }
    }

    private async Task RecoverAsync(CancellationToken token)
    {
        StreamingSubscriptionCheckpoint checkpoint = await _checkpointStore.LoadAsync(Definition.SubscriptionId, token)
            .ConfigureAwait(false) ?? throw new InvalidDataException("持久订阅已存在，但确认检查点文件缺失。");
        IReadOnlyDictionary<long, long> spoolCheckpoints = _spool.AcknowledgedCheckpoints;
        if (spoolCheckpoints.Keys.Any(static partition => partition != 0))
            throw new InvalidDataException("持久订阅 spool 包含不属于订阅的分区。");
        long spoolAcknowledged = spoolCheckpoints.GetValueOrDefault(0, -1);
        FileStreamingSubscriptionState recovered = _state;
        if (checkpoint != _state.Checkpoint)
        {
            FileStreamingDeliveryState delivery = _state.InFlight
                ?? throw new InvalidDataException("持久检查点与状态文件不一致且不存在中断的确认批次。");
            StreamingSubscriptionCheckpoint expected = delivery.CandidateCheckpoint with { WatermarkUtc = _state.WatermarkUtc };
            if (checkpoint != expected
                || (spoolAcknowledged != _state.Checkpoint.CommittedSequence
                    && spoolAcknowledged != checkpoint.CommittedSequence))
            {
                throw new InvalidDataException("持久订阅检查点不是当前批次的确认结果。");
            }

            if (spoolAcknowledged < checkpoint.CommittedSequence)
            {
                CdcEventSpoolBatch pending = await _spool.ReplayBatchAsync(
                    maxEvents: delivery.EventCount, cancellationToken: token).ConfigureAwait(false);
                ValidateBatchEvents(delivery, pending.Events.Select(DecodeEvent).ToArray());
            }

            // 条件检查点是确认提交记录；只有该记录发布后才允许回收事件。
            await _spool.AcknowledgeAsync(new CdcCheckpoint(0, checkpoint.CommittedSequence), token).ConfigureAwait(false);
            recovered = _state with
            {
                Checkpoint = checkpoint,
                PendingEventCount = _state.PendingEventCount - delivery.EventCount,
                InFlight = null,
            };
        }
        else if (spoolAcknowledged != checkpoint.CommittedSequence)
        {
            throw new InvalidDataException("持久订阅 spool 确认位点与条件检查点不匹配。");
        }

        int count = 0;
        long lastSequence = checkpoint.CommittedSequence;
        bool hasMore;
        do
        {
            CdcEventSpoolBatch replay = await _spool.ReplayBatchAsync(
                afterCheckpoint: lastSequence < 0 ? null : new CdcCheckpoint(0, lastSequence),
                maxEvents: Math.Min(Definition.Capacity, 1024),
                maxBytes: Math.Min(Options.MaxStoredBytes, CdcEventCodec.MaxEventBytes * 1024L),
                cancellationToken: token).ConfigureAwait(false);
            foreach (CdcEvent value in replay.Events)
            {
                StreamingEvent decoded = DecodeEvent(value);
                if (decoded.Sequence <= lastSequence || ++count > Definition.Capacity)
                    throw new InvalidDataException("持久订阅事件序号或容量不符合合同。");
                lastSequence = decoded.Sequence;
            }

            hasMore = replay.HasMore;
            if (hasMore && replay.Events.Count == 0)
                throw new InvalidDataException("持久订阅事件无法在有界恢复批次内读取。");
        } while (hasMore);

        if (lastSequence < recovered.LastAcceptedSequence || count < recovered.PendingEventCount
            || count > recovered.PendingEventCount + 1
            || (count == recovered.PendingEventCount && lastSequence != recovered.LastAcceptedSequence)
            || (count > recovered.PendingEventCount && lastSequence <= recovered.LastAcceptedSequence))
        {
            throw new InvalidDataException("持久订阅状态声明的未确认事件缺失或与 spool 不匹配。");
        }

        recovered = recovered with { PendingEventCount = count, LastAcceptedSequence = lastSequence };
        if (recovered.InFlight is { } inFlight)
        {
            CdcEventSpoolBatch pending = await _spool.ReplayBatchAsync(
                maxEvents: inFlight.EventCount, cancellationToken: token).ConfigureAwait(false);
            ValidateBatchEvents(inFlight, pending.Events.Select(DecodeEvent).ToArray());
        }

        if (recovered != _state)
            await WriteStateAsync(DirectoryPath, recovered, token).ConfigureAwait(false);
        _state = recovered;
    }

    private async Task PersistAsync(FileStreamingSubscriptionState state, CancellationToken token)
    {
        try
        {
            await WriteStateAsync(DirectoryPath, state, token).ConfigureAwait(false);
            Volatile.Write(ref _state, state);
        }
        catch (Exception exception)
        {
            Fault(exception);
            throw;
        }
    }

    private CdcEvent EncodeEvent(StreamingEvent value)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, FileStreamingJsonContext.Default.StreamingEvent);
        if (payload.Length > Options.MaxEventBytes)
            throw new ArgumentException("事件 JSON 超过文件订阅事件字节上限。", nameof(value));
        return new CdcEvent(
            value.EventId, Definition.SubscriptionId, Definition.StreamName, value.EventId,
            value.Sequence, value.EventTimeUtc,
            new CdcEventMetadata(CdcEventCodec.CurrentContractVersion, EventSchema,
                CdcEventCodec.CurrentSchemaVersion, CdcOperation.Insert, new CdcCheckpoint(0, value.Sequence)),
            null, StrictUtf8.GetString(payload));
    }

    private StreamingEvent DecodeEvent(CdcEvent value)
    {
        if (value.Source != Definition.SubscriptionId || value.Entity != Definition.StreamName
            || value.Key != value.EventId || value.Metadata.Schema != EventSchema
            || value.Metadata.Checkpoint.Partition != 0 || value.Metadata.Checkpoint.Offset != value.Sequence
            || value.Metadata.Operation != CdcOperation.Insert || value.BeforeJson is not null || value.AfterJson is null)
        {
            throw new InvalidDataException("spool 事件包装与订阅或事件合同不匹配。");
        }

        try
        {
            if (StrictUtf8.GetByteCount(value.AfterJson) > Options.MaxEventBytes)
                throw new InvalidDataException("持久事件 JSON 超过配置上限。");
            StreamingEvent decoded = JsonSerializer.Deserialize(value.AfterJson, FileStreamingJsonContext.Default.StreamingEvent)
                ?? throw new InvalidDataException("持久事件 JSON 为空。");
            ValidateEvent(decoded, Options);
            if (decoded.EventId != value.EventId || decoded.Sequence != value.Sequence
                || decoded.EventTimeUtc != value.OccurredAtUtc
                || CdcEventCodec.Encode(value).Length > Options.MaxBatchBytes)
            {
                throw new InvalidDataException("持久事件字段与外层包装或批次字节边界不匹配。");
            }

            return decoded;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException)
        {
            throw new InvalidDataException("持久订阅事件无法完整解析。", exception);
        }
    }

    private static void ValidateBatchEvents(FileStreamingDeliveryState delivery, StreamingEvent[] events)
    {
        if (events.Length != delivery.EventCount || events.Length == 0
            || events[0].Sequence != delivery.FirstSequence || events[^1].Sequence != delivery.LastSequence)
        {
            throw new InvalidDataException("持久未确认批次与可回放事件不匹配。");
        }
    }

    private static void ValidateState(FileStreamingSubscriptionState state)
    {
        if (state.FormatVersion != FormatVersion || state.Definition is null || state.Options is null || state.Checkpoint is null)
            throw new InvalidDataException("持久订阅状态版本或必需字段无效。");
        state.Definition.Validate();
        state.Options.Validate();
        state.Checkpoint.Validate();
        if (state.Checkpoint.SubscriptionId != state.Definition.SubscriptionId
            || state.WatermarkUtc.Offset != TimeSpan.Zero || state.WatermarkUtc < state.Checkpoint.WatermarkUtc
            || state.LastAcceptedSequence < state.Checkpoint.CommittedSequence
            || state.PendingEventCount < 0 || state.PendingEventCount > state.Definition.Capacity
            || ((state.PendingEventCount == 0) != (state.LastAcceptedSequence == state.Checkpoint.CommittedSequence)))
        {
            throw new InvalidDataException("持久订阅状态中的检查点、watermark 或待确认事件边界无效。");
        }

        if (state.InFlight is { } delivery)
        {
            if (delivery.CandidateCheckpoint is null || !Guid.TryParseExact(delivery.DeliveryId, "N", out _))
                throw new InvalidDataException("持久订阅未确认批次标识无效。");
            delivery.CandidateCheckpoint.Validate();
            if (delivery.Attempt < 1 || delivery.EventCount < 1
                || delivery.Attempt > state.Options.MaxDeliveryAttempts
                || delivery.EventCount > state.Definition.BatchSize || delivery.EventCount > state.PendingEventCount
                || delivery.FirstSequence <= state.Checkpoint.CommittedSequence || delivery.LastSequence < delivery.FirstSequence
                || delivery.LastSequence > state.LastAcceptedSequence
                || delivery.CandidateCheckpoint != state.Checkpoint.Advance(delivery.LastSequence, delivery.CandidateCheckpoint.WatermarkUtc)
                || delivery.CandidateCheckpoint.WatermarkUtc > state.WatermarkUtc)
            {
                throw new InvalidDataException("持久订阅未确认批次的检查点或事件边界无效。");
            }
        }
    }

    private static async Task<(FileStreamingSubscriptionState? State, bool Migrated)> ReadStateAsync(
        string path,
        CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length < 1 || stream.Length > MaxStateBytes)
                throw new InvalidDataException("持久订阅状态文件长度越界。");
            byte[] bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            byte[] trailing = new byte[1];
            if (await stream.ReadAsync(trailing, token).ConfigureAwait(false) != 0)
                throw new InvalidDataException("持久订阅状态文件在读取期间发生变化。");
            var envelope = JsonSerializer.Deserialize(bytes, FileStreamingJsonContext.Default.FileStreamingStateEnvelope)
                ?? throw new InvalidDataException("持久订阅状态为空。");
            if (envelope.State is null || envelope.Sha256 is null)
                throw new InvalidDataException("持久订阅状态缺少校验字段。");
            byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.State,
                FileStreamingJsonContext.Default.FileStreamingSubscriptionState);
            if (string.Equals(envelope.Sha256, Convert.ToHexString(SHA256.HashData(stateBytes)), StringComparison.Ordinal))
                return (envelope.State, false);

            return (MigrateLegacyState(bytes, envelope.Sha256), true);
        }
        catch (FileNotFoundException)
        {
            return (null, false);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or EndOfStreamException)
        {
            throw new InvalidDataException("持久订阅状态文件损坏或截断。", exception);
        }
    }

    private static FileStreamingSubscriptionState MigrateLegacyState(byte[] bytes, string expectedSha256)
    {
        try
        {
            LegacyFileStreamingStateEnvelope legacy = JsonSerializer.Deserialize(
                bytes,
                FileStreamingJsonContext.Default.LegacyFileStreamingStateEnvelope)
                ?? throw new InvalidDataException("持久订阅状态为空。");
            if (legacy.State is null || legacy.Sha256 is null
                || legacy.State.FormatVersion != LegacyFormatVersion)
            {
                throw new InvalidDataException("持久订阅状态版本或必需字段无效。");
            }

            byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(
                legacy.State,
                FileStreamingJsonContext.Default.LegacyFileStreamingSubscriptionState);
            if (!string.Equals(legacy.Sha256, Convert.ToHexString(SHA256.HashData(stateBytes)), StringComparison.Ordinal)
                || !string.Equals(expectedSha256, legacy.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("持久订阅状态 SHA-256 校验失败。");
            }

            LegacyFileStreamingSubscriptionOptions options = legacy.State.Options;
            return new FileStreamingSubscriptionState(
                FormatVersion,
                legacy.State.Definition,
                new FileStreamingSubscriptionOptions
                {
                    MaxEventBytes = options.MaxEventBytes,
                    MaxStoredBytes = options.MaxStoredBytes,
                    MaxBatchBytes = options.MaxBatchBytes,
                    OperationTimeoutMilliseconds = options.OperationTimeoutMilliseconds,
                    MaxDeliveryAttempts = LegacyMaxDeliveryAttempts,
                },
                legacy.State.Checkpoint,
                legacy.State.WatermarkUtc,
                legacy.State.LastAcceptedSequence,
                legacy.State.PendingEventCount,
                legacy.State.PublishingCompleted,
                legacy.State.InFlight);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException("持久订阅状态 SHA-256 校验失败。", exception);
        }
    }

    private static async Task WriteStateAsync(string directory, FileStreamingSubscriptionState state, CancellationToken token)
    {
        ValidateState(state);
        byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(state,
            FileStreamingJsonContext.Default.FileStreamingSubscriptionState);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new FileStreamingStateEnvelope(
            state, Convert.ToHexString(SHA256.HashData(stateBytes))), FileStreamingJsonContext.Default.FileStreamingStateEnvelope);
        if (bytes.Length > MaxStateBytes)
            throw new InvalidDataException("持久订阅状态 JSON 超过字节上限。");
        string path = Path.Combine(directory, "subscription.json");
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            Wal.DirectoryFsync.FlushRequired(directory);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 临时文件清理失败不能覆盖存储提交或取消的原始异常。
            }
        }
    }

    private static void ValidateEvent(StreamingEvent value, FileStreamingSubscriptionOptions options)
    {
        value.Validate();
        ValidateString(value.EventId, CdcEventCodec.MaxStringBytes);
        if (value.Payload.Length > options.MaxEventBytes || value.Headers?.Count > MaxHeaders)
            throw new ArgumentException("事件载荷或事件头数量超过文件订阅上限。", nameof(value));
        int headerBytes = 0;
        if (value.Headers is not null)
        {
            foreach ((string key, string headerValue) in value.Headers)
            {
                ValidateString(key, CdcEventCodec.MaxStringBytes);
                ArgumentNullException.ThrowIfNull(headerValue);
                headerBytes = checked(headerBytes + StrictUtf8.GetByteCount(key) + StrictUtf8.GetByteCount(headerValue));
                if (headerBytes > options.MaxEventBytes)
                    throw new ArgumentException("事件头总字节数超过文件订阅上限。", nameof(value));
            }
        }
    }

    private static void ValidateString(string value, int maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > maxBytes || StrictUtf8.GetByteCount(value) > maxBytes)
            throw new ArgumentException("标识字符串超过 UTF-8 字节上限。", nameof(value));
    }

    private static StreamingEvent Clone(StreamingEvent value)
        => value with
        {
            Payload = value.Payload.ToArray(),
            Headers = value.Headers is null ? null : new Dictionary<string, string>(value.Headers, StringComparer.Ordinal),
        };

    private static bool IsLate(DateTimeOffset eventTime, DateTimeOffset watermark, TimeSpan lateness)
        => eventTime < (lateness >= watermark - DateTimeOffset.MinValue ? DateTimeOffset.MinValue : watermark - lateness);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Pulse()
    {
        TaskCompletionSource previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private void Fault(Exception exception)
    {
        _fault = exception;
        Pulse();
    }
}
