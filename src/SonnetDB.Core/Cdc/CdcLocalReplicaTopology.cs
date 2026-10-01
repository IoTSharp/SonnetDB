using System.Diagnostics;

namespace SonnetDB.Cdc;

/// <summary>本地复制拓扑中拥有独立持久文件和位点的一条分区路由。</summary>
/// <param name="View">该分区的固定源视图。</param>
/// <param name="Spool">由匹配源、实体、schema 的捕获器生产且仅供该分区接收端确认的事件 spool。</param>
/// <param name="Replica">与固定视图身份完全一致的持久接收端。</param>
public sealed record CdcLocalReplicaPartition(
    CdcSourceReadView View,
    CdcEventSpool Spool,
    CdcSnapshotReplica Replica);

/// <summary>本地多分区复制的一次有界运行设置。</summary>
public sealed record CdcLocalReplicaTopologyRunOptions
{
    /// <summary>本次最多完成的单分区推进次数。</summary>
    public int MaxSteps { get; init; } = 64;

    /// <summary>包括排队和批次间等待的最长运行时间。</summary>
    public TimeSpan MaxElapsedTime { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>每次推进最多复制的快照行数或增量事件数。</summary>
    public int MaxRowsPerStep { get; init; } = 256;

    /// <summary>每次推进的最大行或事件正文 UTF-8 字节数。</summary>
    public int MaxBytesPerStep { get; init; } = 1024 * 1024;

    /// <summary>两次推进之间的可取消等待时间。</summary>
    public TimeSpan Interval { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSteps, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxSteps, 100_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRowsPerStep, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxRowsPerStep, 100_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBytesPerStep, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBytesPerStep, 256 * 1024 * 1024);
        if (MaxElapsedTime <= TimeSpan.Zero || MaxElapsedTime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(MaxElapsedTime), "运行时间必须为正数且不超过一小时。");
        if (Interval < TimeSpan.Zero || Interval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(Interval), "推进间隔必须在零至一分钟之间。");
    }
}

/// <summary>一个已经完成提交和 spool 确认的分区推进结果。</summary>
/// <param name="CompletedSteps">本次运行已经完成的推进次数。</param>
/// <param name="PartitionIndex">本次推进的路由序号。</param>
/// <param name="Before">推进之前的持久接收端状态。</param>
/// <param name="After">推进完成之后的持久接收端状态。</param>
public sealed record CdcLocalReplicaTopologyProgress(
    int CompletedSteps,
    int PartitionIndex,
    CdcSnapshotReplicaState Before,
    CdcSnapshotReplicaState After);

/// <summary>本地复制拓扑的一次有界运行结果。</summary>
/// <param name="CompletedSteps">已经成功完成的单分区推进次数。</param>
/// <param name="ReachedStepLimit">尚未追平已捕获事件时是否达到推进次数上限。</param>
/// <param name="ReachedTimeLimit">是否达到最长运行时间。</param>
/// <param name="IsCaughtUp">观测时所有快照和已捕获 spool 位点是否均已应用并确认。</param>
/// <param name="States">按路由顺序读取的独立持久状态；不构成跨分区原子快照。</param>
public sealed record CdcLocalReplicaTopologyRunResult(
    int CompletedSteps,
    bool ReachedStepLimit,
    bool ReachedTimeLimit,
    bool IsCaughtUp,
    IReadOnlyList<CdcSnapshotReplicaState> States);

/// <summary>按轮转顺序有界推进多个独立分区的本地显式复制拓扑。</summary>
/// <remarks>
/// <para>拓扑最多包含 64 条路由。源、实体、分区构成精确路由身份，每条路由必须拥有
/// 专用固定视图、spool 和接收端文件。各分区独立提交；一路失败不会回滚其它分区。</para>
/// <para>调用方负责源捕获和底层对象的生命周期。此类型不创建后台任务，不合并同实体
/// 的分片行，不提供跨分区事务、远程传输或冲突解决。重建拓扑从各接收端持久位点续传。</para>
/// <para>调用方必须用与路由 descriptor 匹配的源、实体和 schema 捕获器生产专用 spool。
/// 构造校验的精确身份限于视图与接收端，spool 只校验分区及文件隔离；已 ACK 回收的事件
/// 无法证明来源，快照前缀也不用于来源核验。实际应用的增量继续由接收端严格校验身份。</para>
/// </remarks>
public sealed class CdcLocalReplicaTopology
{
    private readonly CdcLocalReplicaPartition[] _partitions;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private int _nextPartition;

    /// <summary>创建固定路由列表，校验视图与接收端身份以及 spool 分区和文件隔离。</summary>
    /// <param name="partitions">一至 64 条具有独立文件和精确身份的路由。</param>
    /// <exception cref="ArgumentException">路由身份重复或持久文件被多条路由共享。</exception>
    /// <exception cref="InvalidDataException">视图、接收端或 spool 的分区身份不匹配。</exception>
    public CdcLocalReplicaTopology(IReadOnlyList<CdcLocalReplicaPartition> partitions)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentOutOfRangeException.ThrowIfLessThan(partitions.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(partitions.Count, 64);
        _partitions = partitions.ToArray();
        var identities = new HashSet<(string Source, string Entity, long Partition)>();
        var paths = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (CdcLocalReplicaPartition partition in _partitions)
        {
            ArgumentNullException.ThrowIfNull(partition);
            ArgumentNullException.ThrowIfNull(partition.View);
            ArgumentNullException.ThrowIfNull(partition.Spool);
            ArgumentNullException.ThrowIfNull(partition.Replica);
            CdcSnapshotDescriptor descriptor = partition.View.Descriptor;
            if (partition.Replica.GetState().Descriptor != descriptor)
                throw new InvalidDataException("CDC 分区固定视图和接收端身份不一致。");
            if (!identities.Add((descriptor.Source, descriptor.Entity, descriptor.Checkpoint.Partition)))
                throw new ArgumentException("CDC 拓扑不能重复注册同源、同实体、同分区路由。", nameof(partitions));
            RegisterPath(partition.View.FilePath);
            RegisterPath(partition.View.FilePath + ".lock");
            RegisterPath(partition.Spool.FilePath);
            RegisterPath(partition.Spool.CheckpointPath);
            RegisterPath(partition.Spool.LeasePath);
            RegisterPath(partition.Replica.FilePath);
            RegisterPath(partition.Replica.FilePath + ".lock");
            ValidateSpoolPartitions(partition);
        }

        void RegisterPath(string path)
        {
            if (!paths.Add(Path.GetFullPath(path)))
                throw new ArgumentException("CDC 拓扑的分区必须使用互不重叠的持久文件和 lease。", nameof(partitions));
        }
    }

    /// <summary>读取各路由的独立持久状态，顺序与构造时一致。</summary>
    /// <returns>只读状态列表；各条状态分别一致，不表示跨分区原子快照。</returns>
    public IReadOnlyList<CdcSnapshotReplicaState> GetStates()
        => Array.AsReadOnly(_partitions.Select(static partition => partition.Replica.GetState()).ToArray());

    /// <summary>在当前调用任务中按轮转顺序推进快照和增量，直到追平或达到一个运行边界。</summary>
    /// <param name="options">次数、墙钟时间、单步行数、字节数及间隔边界。</param>
    /// <param name="progress">可选的已完成分区进度通知；接收方应迅速返回。</param>
    /// <param name="cancellationToken">调用方取消令牌；取消抛出异常，已提交分区仍然保留。</param>
    /// <returns>停止原因及各分区持久状态；内部时间上限在持久对象仍可核对时返回结果。</returns>
    /// <remarks>
    /// 超时通过取消令牌停止排队、等待和后续 I/O；已经开始的不可取消刷盘需要先完成。
    /// 接收端提交后 ACK 中断时，重试沿用单分区泵的补确认合同；不能把超时解释为回滚。
    /// 存储操作因取消进入 faulted 状态时，继续抛出恢复核对错误，调用方必须关闭并重开。
    /// </remarks>
    public async ValueTask<CdcLocalReplicaTopologyRunResult> RunAsync(
        CdcLocalReplicaTopologyRunOptions options,
        IProgress<CdcLocalReplicaTopologyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.MaxElapsedTime);
        long started = Stopwatch.GetTimestamp();
        bool acquired = false;
        bool reachedTimeLimit = false;
        int completedSteps = 0;
        try
        {
            await _runGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            for (int step = 0; step < options.MaxSteps; step++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (IsCaughtUp(GetStates()))
                    break;
                if (Stopwatch.GetElapsedTime(started) >= options.MaxElapsedTime)
                {
                    reachedTimeLimit = true;
                    break;
                }

                int index = _nextPartition;
                CdcLocalReplicaPartition partition = _partitions[index];
                CdcSnapshotReplicaState before = partition.Replica.GetState();
                CdcSnapshotReplicaState after = await CdcLocalReplicaPump.AdvanceAsync(
                    partition.View, partition.Spool, partition.Replica,
                    options.MaxRowsPerStep, options.MaxBytesPerStep, deadline.Token).ConfigureAwait(false);
                _nextPartition = (index + 1) % _partitions.Length;
                completedSteps++;
                progress?.Report(new CdcLocalReplicaTopologyProgress(completedSteps, index, before, after));

                if (IsCaughtUp(GetStates()))
                    break;
                if (step + 1 < options.MaxSteps && options.Interval > TimeSpan.Zero)
                    await Task.Delay(options.Interval, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            reachedTimeLimit = true;
        }
        finally
        {
            if (acquired)
                _runGate.Release();
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CdcSnapshotReplicaState> states = GetStates();
        bool caughtUp = IsCaughtUp(states);
        reachedTimeLimit |= Stopwatch.GetElapsedTime(started) >= options.MaxElapsedTime;
        return new CdcLocalReplicaTopologyRunResult(
            completedSteps, !caughtUp && completedSteps >= options.MaxSteps,
            reachedTimeLimit, caughtUp, states);
    }

    private bool IsCaughtUp(IReadOnlyList<CdcSnapshotReplicaState> states)
    {
        bool caughtUp = true;
        for (int index = 0; index < _partitions.Length; index++)
        {
            CdcLocalReplicaPartition partition = _partitions[index];
            ValidateSpoolPartitions(partition);
            CdcSnapshotReplicaState state = states[index];
            if (state.Phase != CdcSnapshotPhase.Incremental)
            {
                caughtUp = false;
                continue;
            }

            long expectedPartition = state.AppliedCheckpoint.Partition;
            bool hasFrames = partition.Spool.PartitionHighWatermarks.TryGetValue(expectedPartition, out long highWatermark);
            long acknowledged = partition.Spool.AcknowledgedCheckpoints.GetValueOrDefault(expectedPartition, -1);
            if (hasFrames)
                caughtUp &= highWatermark == state.AppliedCheckpoint.Offset && acknowledged == state.AppliedCheckpoint.Offset;
            else
                caughtUp &= state.AppliedCheckpoint.Offset == 0;
        }
        return caughtUp;
    }

    private static void ValidateSpoolPartitions(CdcLocalReplicaPartition partition)
    {
        long expectedPartition = partition.View.Descriptor.Checkpoint.Partition;
        if (partition.Spool.PartitionHighWatermarks.Keys.Any(value => value != expectedPartition)
            || partition.Spool.AcknowledgedCheckpoints.Keys.Any(value => value != expectedPartition))
            throw new InvalidDataException("CDC 分区专用 spool 中包含其它分区。");
    }
}
