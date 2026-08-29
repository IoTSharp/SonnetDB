namespace SonnetDB.Engine.Retention;

/// <summary>
/// 后台 Retention 工作线程：按 <see cref="RetentionPolicy"/> 周期扫描段集合，
/// 通过"整段 drop + 墓碑注入"双路径回收超过 TTL 的过期数据。
/// <list type="bullet">
///   <item><description>整段直接 drop：MaxTimestamp &lt; cutoff 的段原子移除 + 删除文件；</description></item>
///   <item><description>部分过期段：注入"全段时间窗"墓碑，由 Compaction 路径在下一轮合并时物理删除；</description></item>
///   <item><description>幂等：重复运行不产生额外副作用（已有等价墓碑时跳过）；</description></item>
///   <item><description>线程安全：通过 SegmentManager 锁协调与 Compaction / Flush 的并发。</description></item>
/// </list>
/// </summary>
public sealed class RetentionWorker : IDisposable
{
    private readonly Tsdb _owner;
    private readonly RetentionPolicy _policy;
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _disposeCompleted = new(initialState: false);
    private Thread? _thread;
    private int _disposeState;
    private int _workerExited;
    private Exception? _disposeError;

    private long _executedRounds;
    private long _droppedSegmentCount;
    private long _injectedTombstoneCount;
    private long _failureCount;
    private Exception? _lastError;

    /// <summary>已成功执行的 Retention 轮次。</summary>
    public long ExecutedRounds => Interlocked.Read(ref _executedRounds);

    /// <summary>累计已 drop 的段数量。</summary>
    public long DroppedSegmentCount => Interlocked.Read(ref _droppedSegmentCount);

    /// <summary>累计已注入的墓碑数量。</summary>
    public long InjectedTombstoneCount => Interlocked.Read(ref _injectedTombstoneCount);

    /// <summary>累计失败次数。</summary>
    public long FailureCount => Interlocked.Read(ref _failureCount);

    /// <summary>最近一次失败的异常（仅诊断用）。</summary>
    public Exception? LastError => Volatile.Read(ref _lastError);

    internal Action? BeforeMaintenanceRoundTestHook { get; set; }

    internal bool HasExited => Volatile.Read(ref _workerExited) != 0;

    /// <summary>
    /// 创建 <see cref="RetentionWorker"/> 实例（尚未启动）。
    /// </summary>
    /// <param name="owner">所属 <see cref="Tsdb"/> 实例。</param>
    /// <param name="policy">Retention 策略。</param>
    /// <exception cref="ArgumentNullException">任何参数为 null 时抛出。</exception>
    public RetentionWorker(Tsdb owner, RetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(policy);
        _owner = owner;
        _policy = policy;
    }

    /// <summary>
    /// 启动后台工作线程。只能调用一次。
    /// </summary>
    /// <exception cref="InvalidOperationException">已启动时抛出。</exception>
    public void Start()
    {
        if (_thread != null)
            throw new InvalidOperationException("RetentionWorker 已启动。");

        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SonnetDB-RetentionWorker",
        };
        _thread.Start();
    }

    /// <summary>
    /// 立即同步执行一次 Retention 扫描（用于测试 / 管理接口）。
    /// <para>在维护串行锁内执行，与 Compaction / DropMeasurement 互斥；线程安全；不可与 <see cref="Dispose"/> 并发调用。</para>
    /// </summary>
    /// <returns>本次执行的统计信息。</returns>
    public RetentionExecutionStats RunOnce()
    {
        RetentionExecutionStats stats = default!;
        _owner.RunUnderMaintenanceLock(() => stats = RunOnceUnderMaintenanceLock());
        return stats;
    }

    /// <summary>
    /// 执行一次 Retention 扫描本体；调用方已持有 <c>_maintenanceSync</c>。
    /// 持读租约计算 plan，保证与并发 Compaction/Drop 的 reader 生命周期安全。
    /// </summary>
    private RetentionExecutionStats RunOnceUnderMaintenanceLock()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        RetentionPlan plan;
        // 持租约取 readers 并规划（plan 只读 reader 元数据，不解码 block）。
        using (var lease = _owner.AcquireReadSnapshot())
        {
            plan = RetentionPlanner.Plan(lease.Readers, _owner.Tombstones, _policy);
        }

        int droppedSegments = 0;
        int injectedTombstones = 0;

        // 3. 注入墓碑（Tsdb.Delete 内部取 _writeSync；锁序 _maintenanceSync → _writeSync 一致，无死锁）
        foreach (var inject in plan.TombstonesToInject)
        {
            _owner.Delete(inject.SeriesId, inject.FieldName, inject.FromTimestamp, inject.ToTimestamp);
            injectedTombstones++;
        }

        // 4. 整段 drop（SegmentManager.DropSegments 原子移除多个段）
        if (plan.SegmentsToDrop.Count > 0)
        {
            SegmentReplacementManifest.CommitDroppedSegments(_owner.RootDirectory, plan.SegmentsToDrop);
            var dropped = _owner.Segments.DropSegments(plan.SegmentsToDrop);
            droppedSegments = dropped.Count;

            // 删除磁盘文件（与 Compaction 同步骤：Swap 后 Delete，失败仅记录）
            foreach (var reader in dropped)
            {
                TryDelete(reader.Path);
                TryDelete(TsdbPaths.VectorIndexPathForSegment(reader.Path));
                TryDelete(TsdbPaths.AggregateIndexPathForSegment(reader.Path));
            }
        }

        sw.Stop();
        long elapsedMicros = sw.ElapsedTicks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;

        Interlocked.Add(ref _droppedSegmentCount, droppedSegments);
        Interlocked.Add(ref _injectedTombstoneCount, injectedTombstones);

        return new RetentionExecutionStats(plan.Cutoff, droppedSegments, injectedTombstones, elapsedMicros);
    }

    /// <summary>
    /// 取消后台线程并等待其真实退出；超过关闭预算时记录诊断，但仍阻止 owner 继续释放依赖。
    /// </summary>
    public void Dispose()
    {
        Thread? thread = _thread;
        if (thread is not null && ReferenceEquals(Thread.CurrentThread, thread))
            throw new InvalidOperationException("RetentionWorker 不能从自身后台线程执行 Dispose。");

        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            WaitForDisposeCompletion();
            return;
        }

        try
        {
            _cts.Cancel();

            if (thread is not null && !thread.Join(_policy.ShutdownTimeout))
            {
                Volatile.Write(ref _lastError,
                    new TimeoutException($"RetentionWorker 关闭超时（{_policy.ShutdownTimeout}）；将继续等待线程安全退出。"));
                thread.Join();
            }

            _cts.Dispose();
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _disposeError, exception);
            throw;
        }
        finally
        {
            Volatile.Write(ref _disposeState, 2);
            _disposeCompleted.Set();
        }
    }

    private void WaitForDisposeCompletion()
    {
        _disposeCompleted.Wait();
        if (Volatile.Read(ref _disposeError) is { } error)
            throw new InvalidOperationException("RetentionWorker 关闭失败。", error);
    }

    // ── 私有 ──────────────────────────────────────────────────────────────────

    private void WorkerLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (_cts.Token.WaitHandle.WaitOne(_policy.PollInterval))
                    break;

                if (_cts.IsCancellationRequested)
                    break;

                BeforeMaintenanceRoundTestHook?.Invoke();
                if (_cts.IsCancellationRequested)
                    break;

                try
                {
                    RunOnce();
                    Interlocked.Increment(ref _executedRounds);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failureCount);
                    Volatile.Write(ref _lastError, ex);
                    _owner.ReportBackgroundWorkerDiagnostic(
                        "RetentionWorker.RunOnce",
                        TsdbDiagnosticSeverity.Error,
                        "后台 Retention 执行失败；异常已被捕获，后续轮询会继续尝试。",
                        ex);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _workerExited, 1);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 删除过期段文件失败不阻塞（下次 Retention 或重启时会再处理）
        }
    }
}

/// <summary>
/// 单次 Retention 扫描的执行统计信息。
/// </summary>
/// <param name="Cutoff">本次扫描使用的 cutoff 时间戳。</param>
/// <param name="DroppedSegments">本次 drop 的段数量。</param>
/// <param name="InjectedTombstones">本次注入的墓碑数量。</param>
/// <param name="ElapsedMicros">本次扫描耗时（微秒）。</param>
public sealed record RetentionExecutionStats(
    long Cutoff,
    int DroppedSegments,
    int InjectedTombstones,
    long ElapsedMicros);
