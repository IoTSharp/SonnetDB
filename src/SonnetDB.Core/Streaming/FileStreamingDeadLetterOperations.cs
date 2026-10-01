namespace SonnetDB.Streaming;

public sealed partial class FileStreamingSubscription
{
    private readonly FileStreamingDeadLetterStore _deadLetters;

    /// <summary>创建或恢复时保存的独立死信容量边界。</summary>
    public FileStreamingDeadLetterOptions DeadLetterOptions => _deadLetters.State.Options;

    /// <summary>
    /// 管理员显式条件隔离当前已耗尽的批次，先持久保存完整原事件，再推进确认位点并回收事件空间。
    /// </summary>
    /// <remarks>
    /// <para>宿主必须限制此入口的管理员访问；原因最多为 4096 UTF-8 字节，整个批次一起隔离。</para>
    /// <para>这不是业务确认或副作用事务。任何持久写入后的失败都可能结果未知，必须关闭并重开；重开会幂等完成已保存的隔离意图。</para>
    /// <para>死信容量不足时不写入意图、不推进检查点；当前批次、投递次数和暂停状态保持原样。</para>
    /// </remarks>
    /// <param name="deliveryId">状态快照中当前已耗尽批次的稳定投递标识。</param>
    /// <param name="expectedRevision">状态快照中的状态修订号，陈旧值会拒绝。</param>
    /// <param name="expectedAttempt">状态快照中的已耗尽投递次数，不匹配会拒绝。</param>
    /// <param name="reason">管理员提供的非空隔离原因。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>持久死信身份、已推进检查点和新的状态修订号。</returns>
    public async ValueTask<FileStreamingDeadLetterReceipt> MoveExhaustedBatchToDeadLetterAsync(
        string deliveryId,
        long expectedRevision,
        int expectedAttempt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedAttempt, 1);
        FileStreamingDeadLetterStore.ValidateReason(reason);
        return await ExecuteAsync(async token =>
        {
            ValidateExpectedRevision(expectedRevision);
            FileStreamingDeliveryState? delivery = _state.InFlight;
            if (delivery is null || !string.Equals(delivery.DeliveryId, deliveryId, StringComparison.Ordinal)
                || delivery.Attempt != expectedAttempt || delivery.Attempt < Options.MaxDeliveryAttempts)
            {
                throw new InvalidOperationException("隔离命令与当前已耗尽批次的标识或投递次数不匹配。");
            }

            _ = checked(_state.StateRevision + 1);
            Cdc.CdcEventSpoolBatch replay = await _spool.ReplayBatchAsync(
                maxEvents: delivery.EventCount, cancellationToken: token).ConfigureAwait(false);
            StreamingEvent[] events = replay.Events.Select(DecodeEvent).ToArray();
            ValidateBatchEvents(delivery, events);
            StreamingSubscriptionCheckpoint checkpoint = delivery.CandidateCheckpoint with { WatermarkUtc = _state.WatermarkUtc };
            var record = new FileStreamingDeadLetterRecord(
                _deadLetters.State.Records.Length + 1L, delivery.DeliveryId, delivery.Attempt,
                reason, DateTimeOffset.UtcNow, _state.StateRevision, _state.Checkpoint, checkpoint,
                events.Select(FileStreamingDeadLetterEvent.FromEvent).ToArray());
            FileStreamingDeadLetterState intent = _deadLetters.Prepare(record);
            token.ThrowIfCancellationRequested();
            try
            {
                // 完整事件和管理员 CAS 条件先成为持久提交意图，之后才允许推进或回收原事件。
                await _deadLetters.CommitAsync(intent, token).ConfigureAwait(false);
                await _checkpointStore.SaveAsync(checkpoint, _state.Checkpoint.Revision, token).ConfigureAwait(false);
                await _spool.AcknowledgeAsync(new Cdc.CdcCheckpoint(0, checkpoint.CommittedSequence), token).ConfigureAwait(false);
                await PersistAsync(_state with
                {
                    Checkpoint = checkpoint,
                    PendingEventCount = _state.PendingEventCount - delivery.EventCount,
                    InFlight = null,
                }, token).ConfigureAwait(false);
                await _deadLetters.CommitAsync(intent with { PendingDeliveryId = null }, token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Fault(exception);
                throw;
            }

            Pulse();
            return new FileStreamingDeadLetterReceipt(
                deliveryId, record.Sequence, delivery.Attempt, checkpoint, _state.StateRevision);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按独立死信序号有界列出摘要；不会读取或重新发布事件。</summary>
    /// <param name="afterSequence">仅返回此死信批次序号之后的条目，第一页使用 0。</param>
    /// <param name="maxCount">返回的最大批次数，范围为 1 至 100。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按死信批次序号递增的摘要副本，空列表表示已经到达当前目录末尾。</returns>
    public async ValueTask<IReadOnlyList<FileStreamingDeadLetterSummary>> ListDeadLettersAsync(
        long afterSequence = 0,
        int maxCount = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxCount, 100);
        return await ExecuteAsync<IReadOnlyList<FileStreamingDeadLetterSummary>>(token =>
        {
            token.ThrowIfCancellationRequested();
            FileStreamingDeadLetterRecord[] records = _deadLetters.State.Records;
            int start = (int)Math.Min(afterSequence, records.Length);
            int count = Math.Min(maxCount, records.Length - start);
            var summaries = new FileStreamingDeadLetterSummary[count];
            for (int index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                summaries[index] = SummarizeDeadLetter(records[start + index]);
            }

            return Task.FromResult<IReadOnlyList<FileStreamingDeadLetterSummary>>(summaries);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取一个有界死信批次的完整事件副本；修改结果不影响持久目录。</summary>
    /// <param name="sequence">目录摘要中的死信批次序号，最小为 1。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完整死信批次；没有该序号时返回空值。</returns>
    public async ValueTask<FileStreamingDeadLetterBatch?> ReadDeadLetterAsync(
        long sequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        return await ExecuteAsync<FileStreamingDeadLetterBatch?>(token =>
        {
            token.ThrowIfCancellationRequested();
            FileStreamingDeadLetterRecord[] records = _deadLetters.State.Records;
            if (sequence > records.Length)
                return Task.FromResult<FileStreamingDeadLetterBatch?>(null);
            FileStreamingDeadLetterRecord record = records[(int)sequence - 1];
            var events = new StreamingEvent[record.Events.Length];
            for (int index = 0; index < events.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                events[index] = record.Events[index].ToEvent();
            }

            return Task.FromResult<FileStreamingDeadLetterBatch?>(
                new FileStreamingDeadLetterBatch(SummarizeDeadLetter(record), events));
        }, cancellationToken).ConfigureAwait(false);
    }

    private FileStreamingDeadLetterSummary SummarizeDeadLetter(FileStreamingDeadLetterRecord record)
        => new(record.Sequence, record.DeliveryId, Definition.SubscriptionId, Definition.StreamName,
            record.Attempt, record.Reason, record.CreatedAtUtc, record.Events.Length,
            record.Events[0].Sequence, record.Events[^1].Sequence, record.Checkpoint);

    private async Task<StreamingSubscriptionCheckpoint> RecoverDeadLetterIntentAsync(
        StreamingSubscriptionCheckpoint checkpoint,
        CancellationToken token)
    {
        FileStreamingDeadLetterRecord? pending = _deadLetters.Pending;
        if (pending is null)
            return checkpoint;
        if (_state.Checkpoint == pending.Checkpoint)
        {
            if (checkpoint != pending.Checkpoint || _state.InFlight is not null
                || _state.StateRevision != checked(pending.ExpectedStateRevision + 1))
            {
                throw new InvalidDataException("已推进的死信隔离意图与订阅状态不匹配。");
            }

            return checkpoint;
        }

        FileStreamingDeliveryState? delivery = _state.InFlight;
        if (_state.Checkpoint != pending.PreviousCheckpoint || _state.StateRevision != pending.ExpectedStateRevision
            || delivery is null || delivery.DeliveryId != pending.DeliveryId || delivery.Attempt != pending.Attempt
            || delivery.EventCount != pending.Events.Length
            || delivery.FirstSequence != pending.Events[0].Sequence || delivery.LastSequence != pending.Events[^1].Sequence
            || (delivery.CandidateCheckpoint with { WatermarkUtc = _state.WatermarkUtc }) != pending.Checkpoint
            || (checkpoint != pending.PreviousCheckpoint && checkpoint != pending.Checkpoint))
        {
            throw new InvalidDataException("死信隔离意图与当前未确认批次、CAS 条件或检查点不匹配。");
        }

        long spoolAcknowledged = _spool.AcknowledgedCheckpoints.GetValueOrDefault(0, -1);
        if (spoolAcknowledged != pending.PreviousCheckpoint.CommittedSequence
            && (checkpoint != pending.Checkpoint || spoolAcknowledged != pending.Checkpoint.CommittedSequence))
        {
            throw new InvalidDataException("死信隔离意图的 spool 位点无法恢复。");
        }

        if (spoolAcknowledged == pending.PreviousCheckpoint.CommittedSequence)
        {
            Cdc.CdcEventSpoolBatch replay = await _spool.ReplayBatchAsync(
                maxEvents: delivery.EventCount, cancellationToken: token).ConfigureAwait(false);
            StreamingEvent[] events = replay.Events.Select(DecodeEvent).ToArray();
            ValidateBatchEvents(delivery, events);
            ValidateDeadLetterOriginalEvents(pending, events, token);
        }

        if (checkpoint == pending.PreviousCheckpoint)
        {
            await _checkpointStore.SaveAsync(pending.Checkpoint, pending.PreviousCheckpoint.Revision, token).ConfigureAwait(false);
            checkpoint = pending.Checkpoint;
        }

        return checkpoint;
    }

    private async Task CompleteRecoveredDeadLetterIntentAsync(CancellationToken token)
    {
        if (_deadLetters.State.Records.Length > 0)
        {
            FileStreamingDeadLetterRecord last = _deadLetters.State.Records[^1];
            if (last.Checkpoint.CommittedSequence > _state.Checkpoint.CommittedSequence
                || last.Checkpoint.Revision > _state.Checkpoint.Revision
                || last.Checkpoint.WatermarkUtc > _state.Checkpoint.WatermarkUtc
                || last.ExpectedStateRevision >= _state.StateRevision)
            {
                throw new InvalidDataException("持久死信记录超出订阅已确认检查点或状态修订号。");
            }
        }

        if (_deadLetters.Pending is { } pending)
        {
            if (_state.Checkpoint != pending.Checkpoint || _state.InFlight is not null
                || _state.StateRevision != checked(pending.ExpectedStateRevision + 1))
            {
                throw new InvalidDataException("死信隔离意图尚未完成检查点和事件回收。");
            }

            await _deadLetters.CommitAsync(_deadLetters.State with { PendingDeliveryId = null }, token).ConfigureAwait(false);
        }
    }

    private static void ValidateDeadLetterOriginalEvents(
        FileStreamingDeadLetterRecord record,
        StreamingEvent[] events,
        CancellationToken token)
    {
        for (int index = 0; index < events.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            StreamingEvent actual = events[index];
            FileStreamingDeadLetterEvent saved = record.Events[index];
            if (actual.EventId != saved.EventId || actual.Sequence != saved.Sequence
                || actual.EventTimeUtc != saved.EventTimeUtc || actual.IsLate != saved.IsLate
                || !actual.Payload.AsSpan().SequenceEqual(saved.Payload)
                || (actual.Headers is null) != (saved.Headers is null)
                || (actual.Headers is not null && saved.Headers is not null
                    && (actual.Headers.Count != saved.Headers.Count
                        || actual.Headers.Any(pair => !saved.Headers.TryGetValue(pair.Key, out string? value) || value != pair.Value))))
            {
                throw new InvalidDataException("死信隔离意图的完整事件与原 spool 不匹配。");
            }
        }
    }
}
