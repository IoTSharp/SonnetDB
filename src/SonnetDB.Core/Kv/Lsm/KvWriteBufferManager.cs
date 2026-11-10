namespace SonnetDB.Kv.Lsm;

/// <summary>
/// 写缓冲预算的参与者（KV-LSM 引擎）。
/// </summary>
internal interface IKvWriteBufferClient
{
    /// <summary>
    /// 请求尽快把活跃 memtable 轮转并刷盘。实现必须是非阻塞的（只设置标志并排队后台作业），
    /// 不得获取任何引擎锁：调用方可能正持有另一个引擎的写锁。
    /// </summary>
    void RequestFlushForWriteBuffer();
}

/// <summary>
/// 跨引擎的 memtable 内存预算（默认进程级 256 MiB）。各引擎在 memtable 占用或刷盘请求状态变化时上报；
/// 超过上限时，选择活跃 memtable 最大且没有待处理请求的引擎，请求其轮转并刷盘。
/// </summary>
/// <remarks>
/// <para>
/// 已轮转、正在刷盘的不可变 memtable，以及已被请求、尚未轮转的活跃 memtable 都视为“正在释放”，
/// 扣除后仍超限才会请求新的引擎，避免一次超限把大量小 memtable 全部刷出。
/// </para>
/// <para>
/// 请求状态以引擎上报为准（引擎兑现或放弃请求后会立即上报），管理器不根据字节变化推测。
/// 预算是软上限：刷盘落后于写入时，单个引擎仍由 <see cref="KvLsmEngineOptions.MaxImmutableMemTables"/> 背压约束。
/// </para>
/// </remarks>
internal sealed class KvWriteBufferManager
{
    /// <summary>默认进程级预算：256 MiB。</summary>
    internal const long DefaultLimitBytes = 256L * 1024 * 1024;

    /// <summary>默认最小刷盘对象：活跃 memtable 不足 64 KiB 时不请求刷盘（新 memtable 本身就有固定占用）。</summary>
    internal const long DefaultMinVictimBytes = 64L * 1024;

    private static readonly Lazy<KvWriteBufferManager> SharedInstance = new(
        static () => new KvWriteBufferManager(DefaultLimitBytes),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly object _sync = new();
    private readonly Dictionary<IKvWriteBufferClient, Usage> _clients = new(ReferenceEqualityComparer.Instance);
    private readonly long _minVictimBytes;
    private long _total;
    private long _flushRequests;

    /// <summary>创建预算管理器。</summary>
    /// <param name="limitBytes">memtable 总字节上限，必须为正数。</param>
    /// <param name="minVictimBytes">可被请求刷盘的最小活跃 memtable 字节数，不能为负数。</param>
    internal KvWriteBufferManager(long limitBytes, long minVictimBytes = DefaultMinVictimBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(minVictimBytes);
        LimitBytes = limitBytes;
        _minVictimBytes = minVictimBytes;
    }

    /// <summary>进程共享实例。</summary>
    internal static KvWriteBufferManager Shared => SharedInstance.Value;

    /// <summary>memtable 总字节上限。</summary>
    internal long LimitBytes { get; }

    /// <summary>当前上报的 memtable 总字节数（活跃 + 不可变）。</summary>
    internal long UsageBytes
    {
        get
        {
            lock (_sync)
                return _total;
        }
    }

    /// <summary>已发出的刷盘请求次数（诊断与测试用）。</summary>
    internal long FlushRequests => Interlocked.Read(ref _flushRequests);

    /// <summary>已登记的引擎数。</summary>
    internal int ClientCount
    {
        get
        {
            lock (_sync)
                return _clients.Count;
        }
    }

    /// <summary>登记引擎（初始占用为 0）。</summary>
    internal void Register(IKvWriteBufferClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_sync)
            _clients.TryAdd(client, new Usage(0, 0, false));
    }

    /// <summary>注销引擎并扣除其占用。</summary>
    internal void Unregister(IKvWriteBufferClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_sync)
        {
            if (_clients.Remove(client, out Usage usage))
                _total -= usage.Total;
        }
    }

    /// <summary>
    /// 上报引擎当前的 memtable 占用与刷盘请求状态；超出预算时可能请求某个引擎（包括调用方自己）刷盘。
    /// </summary>
    /// <param name="client">上报的引擎。</param>
    /// <param name="totalBytes">活跃与不可变 memtable 的总字节数。</param>
    /// <param name="mutableBytes">活跃 memtable 的字节数（只有这部分可以通过请求刷盘释放）。</param>
    /// <param name="flushPending">引擎是否仍有尚未兑现的刷盘请求。</param>
    internal void Report(IKvWriteBufferClient client, long totalBytes, long mutableBytes, bool flushPending)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(mutableBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(mutableBytes, totalBytes);
        IKvWriteBufferClient? victim = null;
        lock (_sync)
        {
            if (!_clients.TryGetValue(client, out Usage previous))
                return;
            _clients[client] = new Usage(totalBytes, mutableBytes, flushPending);
            _total += totalBytes - previous.Total;
            if (_total <= LimitBytes)
                return;

            long releasing = 0;
            long best = 0;
            foreach ((IKvWriteBufferClient candidate, Usage usage) in _clients)
            {
                // 不可变 memtable 已在刷盘队列中，迟早释放。
                releasing += usage.Total - usage.Mutable;
                if (usage.FlushRequested)
                {
                    releasing += usage.Mutable;
                    continue;
                }
                if (usage.Mutable > best && usage.Mutable >= _minVictimBytes)
                {
                    best = usage.Mutable;
                    victim = candidate;
                }
            }

            if (victim is null || _total - releasing <= LimitBytes)
                return;
            // 先在管理器侧标记，避免并发上报在引擎设置标志之前重复选中同一引擎。
            _clients[victim] = _clients[victim] with { FlushRequested = true };
        }

        Interlocked.Increment(ref _flushRequests);
        victim.RequestFlushForWriteBuffer();
    }

    private readonly record struct Usage(long Total, long Mutable, bool FlushRequested);
}
