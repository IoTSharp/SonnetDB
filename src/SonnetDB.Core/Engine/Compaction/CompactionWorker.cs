namespace SonnetDB.Engine.Compaction;

using SonnetDB.Wal;

/// <summary>
/// 后台 Compaction 工作线程：周期性执行 Plan + Execute + Swap。
/// <para>
/// 生命周期模型：
/// <list type="bullet">
///   <item><description>构造后调用 <see cref="Start"/> 启动后台线程；</description></item>
///   <item><description><see cref="Dispose"/> 取消 token → 等待线程退出（超时不抛）。</description></item>
/// </list>
/// </para>
/// </summary>
internal sealed class CompactionWorker : IDisposable
{
    private readonly Tsdb _owner;
    private readonly CompactionPolicy _policy;
    private readonly SegmentCompactor _compactor;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private bool _disposed;

    private long _executedCount;
    private long _failureCount;
    private Exception? _lastError;

    /// <summary>已成功执行的 Compaction 次数。</summary>
    public long ExecutedCount => Interlocked.Read(ref _executedCount);

    /// <summary>执行失败的 Compaction 次数。</summary>
    public long FailureCount => Interlocked.Read(ref _failureCount);

    /// <summary>最近一次执行失败的异常（仅诊断用）。</summary>
    public Exception? LastError => Volatile.Read(ref _lastError);

    /// <summary>
    /// 创建 <see cref="CompactionWorker"/> 实例（尚未启动）。
    /// </summary>
    /// <param name="owner">所属 <see cref="Tsdb"/> 实例。</param>
    /// <param name="policy">Compaction 触发策略。</param>
    public CompactionWorker(Tsdb owner, CompactionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(policy);
        _owner = owner;
        _policy = policy;
        _compactor = new SegmentCompactor(owner.CompactionWriterOptions);
    }

    /// <summary>
    /// 启动后台工作线程。只能调用一次。
    /// </summary>
    public void Start()
    {
        if (_thread != null)
            throw new InvalidOperationException("CompactionWorker 已启动。");

        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SonnetDB-CompactionWorker",
        };
        _thread.Start();
    }

    /// <summary>
    /// 取消后台线程并等待其退出（超时记录但不抛异常）。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _cts.Cancel();

        if (_thread != null)
        {
            // 中断 Thread.Sleep 以加快退出
            _thread.Interrupt();
            bool exited = _thread.Join(_policy.ShutdownTimeout);
            if (!exited)
            {
                Volatile.Write(ref _lastError,
                    new TimeoutException($"CompactionWorker 关闭超时（{_policy.ShutdownTimeout}）。"));
            }
        }

        _cts.Dispose();
    }

    // ── 私有 ──────────────────────────────────────────────────────────────────

    private void WorkerLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            // 等待轮询周期（专用后台线程使用 Thread.Sleep，避免线程池饥饿）
            try
            {
                Thread.Sleep(_policy.PollInterval);
            }
            catch (ThreadInterruptedException)
            {
                break;
            }

            if (_cts.IsCancellationRequested)
                break;

            // 获取当前 readers 快照
            var readers = _owner.Segments.Readers;
            var plans = CompactionPlanner.Plan(readers, _policy);

            foreach (var plan in plans)
            {
                if (_cts.IsCancellationRequested)
                    break;

                try
                {
                    var newId = _owner.AllocateSegmentId();
                    var newPath = TsdbPaths.SegmentPath(_owner.RootDirectory, newId);
                    var readerDict = readers.ToDictionary(static r => r.Header.SegmentId);
                    var result = _compactor.Execute(
                        plan,
                        readerDict,
                        newId,
                        newPath,
                        _owner.Tombstones,
                        _owner.Catalog,
                        _owner.Measurements);

                    _owner.Segments.SwapSegments(plan.SourceSegmentIds, newPath);

                    // SwapSegments 已 Dispose 旧 reader；删除旧文件（失败不抛）
                    foreach (long oldId in plan.SourceSegmentIds)
                    {
                        foreach (string artifactPath in TsdbPaths.SegmentArtifactPaths(_owner.RootDirectory, oldId))
                            TryDelete(artifactPath);
                    }

                    // 回收已被消化的墓碑（不再覆盖任何活段的墓碑可以丢弃）
                    RecycleDiscardedTombstones();

                    Interlocked.Increment(ref _executedCount);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failureCount);
                    Volatile.Write(ref _lastError, ex);
                }
            }
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
            // 删除旧段文件失败不阻塞（Windows 文件锁等情况），下次 Compaction 会再处理
        }
    }

    /// <summary>
    /// Compaction Swap 后，回收不再覆盖任何活段的墓碑：
    /// 遍历所有当前墓碑，若 Index.LookupCandidates 返回空（即无任何活段包含被覆盖点），
    /// 则将其标记为可丢弃，从 TombstoneTable 移除，并重写 manifest。
    /// </summary>
    private void RecycleDiscardedTombstones()
    {
        var tombstones = _owner.Tombstones;
        var all = tombstones.All;
        if (all.Count == 0)
            return;

        var discarded = new List<Tombstone>();
        var index = _owner.Segments.Index;

        foreach (var tomb in all)
        {
            var candidates = index.LookupCandidates(tomb.SeriesId, tomb.FieldName, tomb.FromTimestamp, tomb.ToTimestamp);
            if (candidates.Count == 0)
                discarded.Add(tomb);
        }

        if (discarded.Count == 0)
            return;

        tombstones.RemoveAll(discarded);
        try
        {
            TombstoneManifestCodec.Save(
                TsdbPaths.TombstoneManifestPath(_owner.RootDirectory),
                tombstones.All);
        }
        catch
        {
            // manifest 写入失败不应中断 Compaction（下次 Flush 或启动时会重写）
        }
    }
}
