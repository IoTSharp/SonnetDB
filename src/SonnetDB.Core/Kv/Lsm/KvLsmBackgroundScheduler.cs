namespace SonnetDB.Kv.Lsm;

/// <summary>
/// 进程级 KV-LSM 后台调度器：所有引擎共享固定数量的 flush 与 compaction 工作线程，
/// 取代“每个引擎两条专用线程”，使成百上千个 keyspace 不会线性增加线程数。
/// </summary>
/// <remarks>
/// flush 与 compaction 分属两条独立通道，避免长时间 compaction 阻塞 memtable 落盘（写入背压由 flush 解除）。
/// 同一引擎在同一通道内最多排队一次（由引擎侧的排队标志去重）；作业内部的异常已由引擎记录为 fault，
/// 调度器只负责吞掉异常以保持工作线程存活。
/// </remarks>
internal sealed class KvLsmBackgroundScheduler : IDisposable
{
    private static readonly Lazy<KvLsmBackgroundScheduler> SharedInstance = new(
        static () => new KvLsmBackgroundScheduler(DefaultFlushWorkers, DefaultCompactionWorkers, "Shared"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Lane _flush;
    private readonly Lane _compaction;

    /// <summary>创建调度器并启动工作线程。</summary>
    /// <param name="flushWorkers">flush 工作线程数，必须为正数。</param>
    /// <param name="compactionWorkers">compaction 工作线程数，必须为正数。</param>
    /// <param name="name">线程名后缀（诊断用）。</param>
    internal KvLsmBackgroundScheduler(int flushWorkers, int compactionWorkers, string name = "Private")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(flushWorkers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(compactionWorkers);
        _flush = new Lane($"SonnetDB-KvLsm-Flush-{name}", flushWorkers);
        _compaction = new Lane($"SonnetDB-KvLsm-Compaction-{name}", compactionWorkers);
    }

    /// <summary>进程共享实例（首次使用时启动线程，随进程结束）。</summary>
    internal static KvLsmBackgroundScheduler Shared => SharedInstance.Value;

    /// <summary>默认 flush 线程数：<c>max(2, CPU/4)</c>。</summary>
    internal static int DefaultFlushWorkers => Math.Max(2, Environment.ProcessorCount / 4);

    /// <summary>默认 compaction 线程数：<c>max(1, CPU/4)</c>。</summary>
    internal static int DefaultCompactionWorkers => Math.Max(1, Environment.ProcessorCount / 4);

    /// <summary>当前排队的 flush 作业数（诊断与测试用）。</summary>
    internal int PendingFlushJobs => _flush.Pending;

    /// <summary>当前排队的 compaction 作业数（诊断与测试用）。</summary>
    internal int PendingCompactionJobs => _compaction.Pending;

    /// <summary>flush 工作线程数。</summary>
    internal int FlushWorkerCount => _flush.WorkerCount;

    /// <summary>compaction 工作线程数。</summary>
    internal int CompactionWorkerCount => _compaction.WorkerCount;

    /// <summary>排队一个 flush 作业。</summary>
    internal void ScheduleFlush(Action job) => _flush.Enqueue(job);

    /// <summary>排队一个 compaction 作业。</summary>
    internal void ScheduleCompaction(Action job) => _compaction.Enqueue(job);

    /// <summary>停止工作线程；尚未执行的作业被丢弃（只用于测试创建的私有调度器）。</summary>
    public void Dispose()
    {
        _flush.Dispose();
        _compaction.Dispose();
    }

    private sealed class Lane : IDisposable
    {
        private readonly object _sync = new();
        private readonly Queue<Action> _queue = new();
        private readonly Thread[] _workers;
        private bool _disposed;

        internal Lane(string name, int workers)
        {
            _workers = new Thread[workers];
            for (int i = 0; i < workers; i++)
            {
                _workers[i] = new Thread(Run)
                {
                    IsBackground = true,
                    Name = $"{name}-{i}",
                };
                _workers[i].Start();
            }
        }

        internal int Pending
        {
            get
            {
                lock (_sync)
                    return _queue.Count;
            }
        }

        internal int WorkerCount => _workers.Length;

        internal void Enqueue(Action job)
        {
            ArgumentNullException.ThrowIfNull(job);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _queue.Enqueue(job);
                Monitor.Pulse(_sync);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _queue.Clear();
                Monitor.PulseAll(_sync);
            }
            foreach (Thread worker in _workers)
            {
                if (worker != Thread.CurrentThread)
                    worker.Join(TimeSpan.FromSeconds(30));
            }
        }

        private void Run()
        {
            while (true)
            {
                Action job;
                lock (_sync)
                {
                    while (_queue.Count == 0 && !_disposed)
                        Monitor.Wait(_sync);
                    if (_disposed)
                        return;
                    job = _queue.Dequeue();
                }

                try
                {
                    job();
                }
                catch (Exception)
                {
                    // 作业失败已由引擎记录为 fault（写入随后 fail closed）；保持工作线程存活以服务其他引擎。
                }
            }
        }
    }
}
