using System.Diagnostics;

namespace SonnetDB.Cdc;

/// <summary>多个独立 CDC 到流桥接的一次有界运行设置。</summary>
public sealed record CdcStreamingBridgeTopologyRunOptions
{
    /// <summary>本次最多成功完成的单分区批次次数，最高为十万。</summary>
    public int MaxSteps { get; init; } = 64;

    /// <summary>包含排队、单批次和批次间等待的运行时间上限，最高为一小时。</summary>
    public TimeSpan MaxElapsedTime { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>两次批次之间的可取消等待，最高为一分钟。</summary>
    public TimeSpan Interval { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSteps, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxSteps, 100_000);
        if (MaxElapsedTime <= TimeSpan.Zero || MaxElapsedTime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(MaxElapsedTime), "运行时间必须为正数且不超过一小时。");
        if (Interval < TimeSpan.Zero || Interval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(Interval), "推进间隔必须在零至一分钟之间。");
    }
}

/// <summary>已经完成目标交付和源确认的一个独立分区批次。</summary>
/// <param name="CompletedSteps">本次已成功完成的批次数。</param>
/// <param name="PartitionIndex">构造时的桥接序号。</param>
/// <param name="Before">推进之前的独立桥接状态。</param>
/// <param name="After">推进之后的独立桥接状态。</param>
/// <param name="Result">单分区桥接返回的批次结果。</param>
public sealed record CdcStreamingBridgeTopologyProgress(
    int CompletedSteps, int PartitionIndex, CdcStreamingBridgeState Before,
    CdcStreamingBridgeState After, CdcStreamingBridgeResult Result);

/// <summary>多个独立桥接的一次有界调度结果。</summary>
/// <param name="CompletedSteps">已成功完成的单分区批次数。</param>
/// <param name="ReachedStepLimit">未追平时是否达到批次数上限。</param>
/// <param name="ReachedTimeLimit">是否达到总时间上限。</param>
/// <param name="IsCaughtUp">观察时所有已捕获事件是否完成目标交付和源确认，不要求目标消费者已确认。</param>
/// <param name="States">按桥接顺序读取的独立状态，不表示跨分区原子快照。</param>
public sealed record CdcStreamingBridgeTopologyRunResult(
    int CompletedSteps, bool ReachedStepLimit, bool ReachedTimeLimit, bool IsCaughtUp,
    IReadOnlyList<CdcStreamingBridgeState> States);

/// <summary>按轮转顺序有界调度至多 64 个具有独立持久路径的本地 CDC 到流桥接。</summary>
/// <remarks>
/// <para>调用方拥有桥接、源 spool 和目标订阅，负责捕获事件、恢复及关闭；协调器不启动后台任务。
/// 每次只调用一个桥接的一批推进，容量仍由桥接的固定选项限定。并发运行在总时间边界内排队。</para>
/// <para>各分区独立提交，失败不回滚其它分区，也不提供跨分区事务、远程传输或业务副作用 exactly-once。
/// 调用方不得同时在其它协调器或直接调用中推进这些桥接。</para>
/// <para>排队或批次间等待超时返回边界结果；批次中取消、超时或存储失败继续抛出，必须按单分区
/// 桥接合同关闭并重开失败桥接后重建拓扑。进度接收方应迅速返回，不受可取消 I/O 的时间保证。</para>
/// </remarks>
public sealed class CdcStreamingBridgeTopology
{
    private readonly CdcStreamingBridge[] _bridges;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private int _nextPartition;

    /// <summary>保存调用方桥接列表并校验精确路由身份及所有源、目标和状态路径隔离。</summary>
    /// <param name="bridges">一至 64 个由调用方打开并拥有的独立桥接。</param>
    /// <param name="cancellationToken">构造校验取消令牌；校验另受五秒总时间边界约束。</param>
    /// <exception cref="ArgumentException">重复桥接、同源同实体同分区，或持久路径交叉重叠。</exception>
    /// <exception cref="InvalidDataException">专用源 spool 包含其它分区。</exception>
    public CdcStreamingBridgeTopology(
        IReadOnlyList<CdcStreamingBridge> bridges, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bridges);
        ArgumentOutOfRangeException.ThrowIfLessThan(bridges.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bridges.Count, 64);
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        _bridges = new CdcStreamingBridge[bridges.Count];
        for (int index = 0; index < _bridges.Length; index++)
        {
            CheckBoundary();
            _bridges[index] = bridges[index];
        }
        var instances = new HashSet<CdcStreamingBridge>(ReferenceEqualityComparer.Instance);
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<(string Source, string Entity, long Partition)>();
        var files = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var directories = new List<string>(_bridges.Length);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (CdcStreamingBridge bridge in _bridges)
        {
            CheckBoundary();
            ArgumentNullException.ThrowIfNull(bridge);
            CdcStreamingBridgeBinding binding = bridge.Binding;
            if (!instances.Add(bridge) || !identifiers.Add(binding.BridgeId)
                || !identities.Add((binding.Source, binding.Entity, binding.Partition)))
                throw new ArgumentException("CDC 流拓扑不能重复注册桥接或同源、同实体、同分区路由。", nameof(bridges));
            RegisterFile(bridge.StatePath);
            RegisterFile(bridge.StatePath + ".lock");
            RegisterFile(bridge.SourceSpool.FilePath);
            RegisterFile(bridge.SourceSpool.CheckpointPath);
            RegisterFile(bridge.SourceSpool.LeasePath);
            directories.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(bridge.TargetSubscription.DirectoryPath)));
            ValidateSourcePartitions(bridge, out _, out _);
        }

        for (int index = 0; index < directories.Count; index++)
        {
            CheckBoundary();
            string directory = directories[index];
            foreach (string file in files)
            {
                CheckBoundary();
                if (IsWithin(file, directory))
                    throw new ArgumentException("CDC 流拓扑的源文件及状态不得位于任何目标订阅目录内。", nameof(bridges));
            }

            for (int other = index + 1; other < directories.Count; other++)
            {
                CheckBoundary();
                if (IsWithin(directory, directories[other]) || IsWithin(directories[other], directory))
                    throw new ArgumentException("CDC 流拓扑的目标订阅目录必须互不重叠。", nameof(bridges));
            }
        }

        bool IsWithin(string path, string directory)
            => string.Equals(path, directory, comparison) || path.StartsWith(directory + Path.DirectorySeparatorChar, comparison);

        void RegisterFile(string path)
        {
            if (!files.Add(Path.GetFullPath(path)))
                throw new ArgumentException("CDC 流拓扑的源数据、检查点和桥接状态文件必须互不重叠。", nameof(bridges));
        }

        void CheckBoundary()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5))
                throw new TimeoutException("CDC 流拓扑构造校验超过五秒边界。");
        }
    }

    /// <summary>按构造顺序读取各桥接的独立状态。</summary>
    /// <returns>只读状态列表；各状态分别一致，不表示跨分区原子快照。</returns>
    public IReadOnlyList<CdcStreamingBridgeState> GetStates()
        => Array.AsReadOnly(_bridges.Select(static bridge => bridge.GetState()).ToArray());

    /// <summary>有界轮转推进所有独立桥接，直到已捕获源追平或达到运行边界。</summary>
    /// <param name="options">总批次数、墙钟时间和批次间等待边界。</param>
    /// <param name="progress">可选的成功批次进度通知。</param>
    /// <param name="cancellationToken">取消令牌；取消不回滚已经提交的分区。</param>
    /// <returns>停止原因和独立持久状态；不包含业务消费者确认状态。</returns>
    public async ValueTask<CdcStreamingBridgeTopologyRunResult> RunAsync(
        CdcStreamingBridgeTopologyRunOptions options,
        IProgress<CdcStreamingBridgeTopologyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.MaxElapsedTime);
        long started = Stopwatch.GetTimestamp();
        bool acquired = false;
        bool pumping = false;
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
                CdcStreamingBridge bridge = _bridges[index];
                CdcStreamingBridgeState before = bridge.GetState();
                // 失败路由也消耗一次轮转位置，避免后续显式重试总从它开始阻挡其它分区。
                _nextPartition = (index + 1) % _bridges.Length;
                pumping = true;
                CdcStreamingBridgeResult result = await bridge.PumpOnceAsync(before.Revision, deadline.Token).ConfigureAwait(false);
                pumping = false;
                completedSteps++;
                progress?.Report(new(completedSteps, index, before, bridge.GetState(), result));
                if (IsCaughtUp(GetStates()))
                    break;
                if (step + 1 < options.MaxSteps && options.Interval > TimeSpan.Zero)
                    await Task.Delay(options.Interval, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (pumping)
                throw new TimeoutException("CDC 流拓扑批次中达到时间边界；须关闭并重开该桥接核对恢复 outbox。", exception);
            reachedTimeLimit = true;
        }
        finally
        {
            if (acquired)
                _runGate.Release();
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CdcStreamingBridgeState> states = GetStates();
        bool caughtUp = IsCaughtUp(states);
        reachedTimeLimit |= Stopwatch.GetElapsedTime(started) >= options.MaxElapsedTime;
        return new(completedSteps, !caughtUp && completedSteps >= options.MaxSteps, reachedTimeLimit, caughtUp, states);
    }

    private bool IsCaughtUp(IReadOnlyList<CdcStreamingBridgeState> states)
    {
        bool caughtUp = true;
        for (int index = 0; index < _bridges.Length; index++)
        {
            CdcStreamingBridge bridge = _bridges[index];
            ValidateSourcePartitions(bridge, out long acknowledged, out long highWatermark);
            CdcStreamingBridgeState state = states[index];
            CdcEventSpool spool = bridge.SourceSpool;
            caughtUp &= state.OutboxEventCount == 0 && spool.EventCount == 0
                && acknowledged == state.CommittedSourceOffset
                && highWatermark == state.CommittedSourceOffset
                && bridge.TargetSubscription.LastAcceptedSequence == state.LastTargetSequence;
        }

        return caughtUp;
    }

    private static void ValidateSourcePartitions(CdcStreamingBridge bridge, out long acknowledged, out long highWatermark)
    {
        // 锁内按分区数量和键读取，非法多分区也无需复制或枚举整个元数据字典。
        if (!bridge.SourceSpool.TryGetDedicatedPartitionProgress(bridge.Binding.Partition, out acknowledged, out highWatermark))
            throw new InvalidDataException("CDC 流拓扑的专用源 spool 中包含其它分区。");
    }
}
