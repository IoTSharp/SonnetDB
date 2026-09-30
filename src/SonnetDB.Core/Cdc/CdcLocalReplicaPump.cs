using System.Runtime.CompilerServices;

namespace SonnetDB.Cdc;

/// <summary>
/// 将一个固定源视图和专用事件 spool 有界地推进到本地接收端。
/// </summary>
/// <remarks>
/// 调用方负责持续捕获源端变更；此方法一次只处理一页或一批，不调度后台任务。
/// 同一接收端的调用在本进程内串行化，spool 不得由另一个接收端提前确认。
/// </remarks>
public static class CdcLocalReplicaPump
{
    private static readonly ConditionalWeakTable<CdcSnapshotReplica, SemaphoreSlim> Gates = new();

    /// <summary>
    /// 推进一页快照、完成已复制快照，或应用一批增量并确认其位点。
    /// </summary>
    /// <param name="view">与接收端 descriptor 完全一致的固定源视图。</param>
    /// <param name="spool">仅供该接收端使用的源事件 spool。</param>
    /// <param name="replica">持久本地接收端。</param>
    /// <param name="maxRows">本次最多复制的行数或应用的事件数，不得超过视图及接收端页上限。</param>
    /// <param name="maxBytes">本次最多读取的行或事件正文 UTF-8 字节数，不得超过两端批次上限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>操作完成后接收端的持久恢复状态。</returns>
    public static async ValueTask<CdcSnapshotReplicaState> AdvanceAsync(
        CdcSourceReadView view,
        CdcEventSpool spool,
        CdcSnapshotReplica replica,
        int maxRows,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(replica);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();

        SemaphoreSlim gate = Gates.GetValue(replica, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SemaphoreSlim pipelineGate = CdcLocalPipelineGate.For(spool);
            await pipelineGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await AdvanceLockedAsync(view, spool, replica, maxRows, maxBytes, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                pipelineGate.Release();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static async ValueTask<CdcSnapshotReplicaState> AdvanceLockedAsync(
        CdcSourceReadView view,
        CdcEventSpool spool,
        CdcSnapshotReplica replica,
        int maxRows,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        CdcSnapshotReplicaState state = replica.GetState();
        CdcSnapshotDescriptor descriptor = view.Descriptor;
        if (state.Descriptor != descriptor)
            throw new InvalidDataException("CDC 固定源视图与接收端 descriptor 不一致。");

        if (state.Phase == CdcSnapshotPhase.Snapshot)
        {
            if (state.SnapshotRowsCopied == descriptor.RowCount)
                return await replica.CompleteSnapshotAsync(cancellationToken).ConfigureAwait(false);

            CdcSnapshotRowBatch page = await view.ReadPageAsync(
                state.LastSnapshotKey, maxRows, maxBytes, cancellationToken).ConfigureAwait(false);
            if (page.AppliedCheckpoint != descriptor.Checkpoint || page.Rows.Count == 0)
                throw new InvalidDataException("CDC 固定源视图在快照完成前缺少预期的行或位点。");
            return await replica.WriteSnapshotPageAsync(
                state.SnapshotRowsCopied, page.Rows, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyDictionary<long, long> highWatermarks = spool.PartitionHighWatermarks;
        if (highWatermarks.Keys.Any(partition => partition != descriptor.Checkpoint.Partition))
            throw new InvalidDataException("CDC 接收端专用 spool 包含其它分区的事件。");
        IReadOnlyDictionary<long, long> acknowledgements = spool.AcknowledgedCheckpoints;
        if (acknowledgements.Keys.Any(partition => partition != descriptor.Checkpoint.Partition))
            throw new InvalidDataException("CDC 接收端专用 spool 包含其它分区的确认位点。");

        bool hasSourceFrames = highWatermarks.TryGetValue(
            descriptor.Checkpoint.Partition, out long sourceHighWatermark);
        if (descriptor.Checkpoint.Offset > 0
            && (!hasSourceFrames || sourceHighWatermark < descriptor.Checkpoint.Offset))
            throw new InvalidOperationException("CDC 源捕获尚未到达固定读视图位点。");
        if (hasSourceFrames && sourceHighWatermark < state.AppliedCheckpoint.Offset)
            throw new InvalidDataException("CDC 接收端位点超过源 spool 高水位。");

        long acknowledged = acknowledgements.TryGetValue(
            descriptor.Checkpoint.Partition, out long offset) ? offset : -1;
        if (acknowledged > state.AppliedCheckpoint.Offset)
            throw new InvalidDataException("CDC spool 已确认位点超过接收端物化位点。");
        if (acknowledged < state.AppliedCheckpoint.Offset && hasSourceFrames)
            await spool.AcknowledgeAsync(state.AppliedCheckpoint, cancellationToken).ConfigureAwait(false);
        else if (acknowledged < state.AppliedCheckpoint.Offset
            && state.AppliedCheckpoint.Offset != descriptor.Checkpoint.Offset)
            throw new InvalidDataException("CDC spool 缺少尚未确认的已物化事件。");

        CdcEventSpoolState spoolState = spool.GetState();
        if (spoolState.EventCount == 0)
            return state;
        int replayRows = (int)Math.Min(maxRows, spoolState.EventCount);
        long replayBytes = Math.Min(maxBytes, spoolState.StoredBytes);
        CdcEventSpoolBatch batch = await spool.ReplayBatchAsync(
            state.AppliedCheckpoint, replayRows, replayBytes, cancellationToken).ConfigureAwait(false);
        if (batch.Events.Count == 0)
        {
            if (batch.HasMore)
                throw new InvalidOperationException("CDC 增量批次字节上限无法容纳下一条源事件。");
            return state;
        }

        state = await replica.ApplyIncrementalAsync(batch.Events, cancellationToken).ConfigureAwait(false);
        await spool.AcknowledgeAsync(state.AppliedCheckpoint, cancellationToken).ConfigureAwait(false);
        return state;
    }
}
