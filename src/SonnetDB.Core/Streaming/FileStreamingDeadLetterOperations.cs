namespace SonnetDB.Streaming;

public sealed partial class FileStreamingSubscription
{
    private readonly FileStreamingDeadLetterStore _deadLetters;

    /// <summary>创建或恢复时保存的独立死信容量边界。</summary>
    public FileStreamingDeadLetterOptions DeadLetterOptions => _deadLetters.State.Options;

    /// <summary>最近持久提交的独立死信目录修订号；与订阅状态和确认检查点修订号不同。</summary>
    public long DeadLetterRevision => _deadLetters.State.Revision;

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
                _deadLetters.State.NextSequence, delivery.DeliveryId, delivery.Attempt,
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
            var summaries = new List<FileStreamingDeadLetterSummary>(Math.Min(maxCount, records.Length));
            // 目录最多 10,000 条，单页最多 100 条；操作令牌约束墙钟和取消。
            for (int index = 0; index < records.Length && summaries.Count < maxCount; index++)
            {
                token.ThrowIfCancellationRequested();
                if (records[index].Sequence > afterSequence)
                    summaries.Add(SummarizeDeadLetter(records[index]));
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
            int recordIndex = FindDeadLetterIndex(records, sequence, token);
            if (recordIndex < 0)
                return Task.FromResult<FileStreamingDeadLetterBatch?>(null);
            FileStreamingDeadLetterRecord record = records[recordIndex];
            return Task.FromResult<FileStreamingDeadLetterBatch?>(
                new FileStreamingDeadLetterBatch(SummarizeDeadLetter(record), CopyDeadLetterEvents(record, token)));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>条件持久领取一个本地死信重放批次，首次分配稳定重放身份，后续领取保留身份并增加次数。</summary>
    /// <remarks>
    /// <para>宿主须进行管理员授权。业务处理使用原事件稳定 ID 幂等执行，成功后以返回摘要条件调用 <see cref="DeleteDeadLetterAsync"/> 完成。</para>
    /// <para>领取不自动发布到原订阅、不推进原订阅检查点。业务失败或进程退出后可重开再次领取；最多领取一百万次。</para>
    /// <para>持久写入失败或取消的结果可能未知，当前实例停止后续操作，调用方须重开核对目录修订号和重放身份。</para>
    /// </remarks>
    /// <param name="sequence">已观察到的死信批次序号。</param>
    /// <param name="expectedDeliveryId">摘要中的原投递身份，不匹配会拒绝。</param>
    /// <param name="expectedRevision">摘要中的独立死信目录修订号，陈旧值会拒绝。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>包含已持久重放身份、领取次数和完整原事件副本的批次。</returns>
    public async ValueTask<FileStreamingDeadLetterReplayBatch> ReplayDeadLetterAsync(
        long sequence,
        string expectedDeliveryId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ValidateDeadLetterOperationArguments(sequence, expectedDeliveryId, expectedRevision);
        return await ExecuteAsync(async token =>
        {
            int index = ValidateDeadLetterCondition(sequence, expectedDeliveryId, expectedRevision, token);
            FileStreamingDeadLetterRecord record = _deadLetters.State.Records[index];
            FileStreamingDeadLetterReplayState? previous = record.Replay;
            if (previous?.Attempt >= FileStreamingDeadLetterStore.MaxReplayAttempts)
                throw new InvalidOperationException("死信批次已达到本地重放领取次数上限，须显式处理或删除。");
            var replay = new FileStreamingDeadLetterReplayState(previous?.ReplayId ?? Guid.NewGuid().ToString("N"),
                checked((previous?.Attempt ?? 0) + 1), DateTimeOffset.UtcNow);
            FileStreamingDeadLetterRecord claimed = record with { Replay = replay };
            FileStreamingDeadLetterRecord[] records = _deadLetters.State.Records.ToArray();
            records[index] = claimed;
            FileStreamingDeadLetterState proposed = _deadLetters.PrepareOperation(records);
            StreamingEvent[] events = CopyDeadLetterEvents(claimed, token);
            await CommitDeadLetterOperationAsync(proposed, token).ConfigureAwait(false);
            return new FileStreamingDeadLetterReplayBatch(SummarizeDeadLetter(claimed), events);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>根据目录修订号、原批次身份和重放身份/次数条件删除一条死信，回收批次和持久字节容量。</summary>
    /// <remarks>
    /// <para>业务重放成功后传入领取回执中的全部条件，删除即完成本地重放；业务副作用与删除之间没有事务，结果未知时须重开核对。</para>
    /// <para>管理员直接丢弃尚未领取的批次时须传入空重放身份和 0 次数。已领取批次不能使用该条件丢弃，须提供实际身份和次数。</para>
    /// <para>删除不会修改原订阅位点、暂停标记或未确认批次，已删除序号不会再分配；删除不是业务副作用完成的证明。</para>
    /// </remarks>
    /// <param name="sequence">已观察到的死信批次序号。</param>
    /// <param name="expectedDeliveryId">摘要中的原投递身份。</param>
    /// <param name="expectedRevision">摘要中的独立死信目录修订号。</param>
    /// <param name="expectedReplayId">摘要中的稳定重放身份；尚未领取时为空。</param>
    /// <param name="expectedReplayAttempt">摘要中的重放领取次数；尚未领取时为 0。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已删除身份和新的独立死信目录修订号。</returns>
    public async ValueTask<FileStreamingDeadLetterDeletionReceipt> DeleteDeadLetterAsync(
        long sequence,
        string expectedDeliveryId,
        long expectedRevision,
        string? expectedReplayId,
        int expectedReplayAttempt,
        CancellationToken cancellationToken = default)
    {
        ValidateDeadLetterOperationArguments(sequence, expectedDeliveryId, expectedRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedReplayAttempt);
        if (expectedReplayId is null ? expectedReplayAttempt != 0 : expectedReplayAttempt < 1)
            throw new ArgumentException("重放身份与领取次数条件不一致。", nameof(expectedReplayAttempt));
        if (expectedReplayId is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedReplayId);
        return await ExecuteAsync(async token =>
        {
            int index = ValidateDeadLetterCondition(sequence, expectedDeliveryId, expectedRevision, token);
            FileStreamingDeadLetterRecord record = _deadLetters.State.Records[index];
            if (record.Replay?.ReplayId != expectedReplayId || (record.Replay?.Attempt ?? 0) != expectedReplayAttempt)
                throw new InvalidOperationException("删除命令的重放身份或领取次数与当前死信批次不匹配。");
            FileStreamingDeadLetterRecord[] records = _deadLetters.State.Records;
            var remaining = new FileStreamingDeadLetterRecord[records.Length - 1];
            records.AsSpan(0, index).CopyTo(remaining);
            records.AsSpan(index + 1).CopyTo(remaining.AsSpan(index));
            FileStreamingDeadLetterState proposed = _deadLetters.PrepareOperation(remaining);
            await CommitDeadLetterOperationAsync(proposed, token).ConfigureAwait(false);
            return new FileStreamingDeadLetterDeletionReceipt(sequence, record.DeliveryId,
                expectedReplayId, expectedReplayAttempt, _deadLetters.State.Revision);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateDeadLetterOperationArguments(long sequence, string expectedDeliveryId, long expectedRevision)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDeliveryId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
    }

    private int ValidateDeadLetterCondition(long sequence, string expectedDeliveryId, long expectedRevision, CancellationToken token)
    {
        if (_deadLetters.State.PendingDeliveryId is not null)
            throw new InvalidOperationException("死信隔离意图尚未恢复完成，必须重开订阅。");
        if (_deadLetters.State.Revision != expectedRevision)
            throw new InvalidOperationException("死信操作的预期目录修订号与当前状态不匹配。");
        int index = FindDeadLetterIndex(_deadLetters.State.Records, sequence, token);
        if (index < 0 || _deadLetters.State.Records[index].DeliveryId != expectedDeliveryId)
            throw new InvalidOperationException("死信操作的批次序号或原投递身份与当前状态不匹配。");
        return index;
    }

    private async Task CommitDeadLetterOperationAsync(FileStreamingDeadLetterState proposed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            await _deadLetters.CommitAsync(proposed, token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Fault(exception);
            throw;
        }
    }

    private static int FindDeadLetterIndex(FileStreamingDeadLetterRecord[] records, long sequence, CancellationToken token)
    {
        // Records 经恢复校验限制为最多 10,000 条；操作令牌约束墙钟和取消。
        for (int index = 0; index < records.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            if (records[index].Sequence == sequence)
                return index;
            if (records[index].Sequence > sequence)
                break;
        }

        return -1;
    }

    private static StreamingEvent[] CopyDeadLetterEvents(FileStreamingDeadLetterRecord record, CancellationToken token)
    {
        var events = new StreamingEvent[record.Events.Length];
        // 单批事件数受订阅 BatchSize（最多 100,000）限制；操作令牌约束墙钟和取消。
        for (int index = 0; index < events.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            events[index] = record.Events[index].ToEvent();
        }

        return events;
    }

    private FileStreamingDeadLetterSummary SummarizeDeadLetter(FileStreamingDeadLetterRecord record)
        => new(record.Sequence, record.DeliveryId, Definition.SubscriptionId, Definition.StreamName,
            record.Attempt, record.Reason, record.CreatedAtUtc, record.Events.Length,
            record.Events[0].Sequence, record.Events[^1].Sequence, record.Checkpoint)
        {
            DeadLetterRevision = _deadLetters.State.Revision,
            ReplayId = record.Replay?.ReplayId,
            ReplayAttempt = record.Replay?.Attempt ?? 0,
            LastReplayClaimedAtUtc = record.Replay?.LastClaimedAtUtc,
        };

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
