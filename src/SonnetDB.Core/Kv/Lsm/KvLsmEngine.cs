using SonnetDB.Wal;

namespace SonnetDB.Kv.Lsm;

/// <summary>引擎持久化流程中的故障注入点。</summary>
internal enum KvLsmEnginePhase
{
    /// <summary>WAL 记录已追加（按选项已 fsync），尚未写入 memtable。</summary>
    AfterWalAppend,

    /// <summary>flush 的 SST 已发布，manifest 尚未提交。</summary>
    FlushAfterSstWritten,

    /// <summary>flush 的 manifest 已提交，尚未安装新读状态与删除旧 WAL。</summary>
    FlushAfterManifestCommit,

    /// <summary>compaction 输出 SST 已发布，manifest 尚未提交。</summary>
    CompactionAfterOutputsWritten,

    /// <summary>compaction 的 manifest 已提交，尚未安装新读状态与清理输入文件。</summary>
    CompactionAfterManifestCommit,
}

/// <summary>KV-LSM 引擎选项。</summary>
internal sealed record KvLsmEngineOptions
{
    /// <summary>默认选项。</summary>
    internal static KvLsmEngineOptions Default { get; } = new();

    /// <summary>活跃 memtable 实际占用（<see cref="KvMemTable.UsedBytes"/>）达到该字节数后轮转为不可变 memtable。</summary>
    internal long WriteBufferBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>未刷盘的不可变 memtable 上限；达到后写入等待 flush（背压）。</summary>
    internal int MaxImmutableMemTables { get; init; } = 2;

    /// <summary>写入等待背压的最长时间，超时抛出可重试分类的 <see cref="IOException"/>。</summary>
    internal TimeSpan WriteBackpressureTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>每次写入后 fsync WAL。</summary>
    internal bool SyncWalOnEveryWrite { get; init; } = true;

    /// <summary>WAL 写缓冲字节数。</summary>
    internal int WalBufferSize { get; init; } = 64 * 1024;

    /// <summary>为 false 时不启动后台 flush 线程，由 <see cref="KvLsmEngine.Flush"/> 同步刷盘（测试用）。</summary>
    internal bool BackgroundFlush { get; init; } = true;

    /// <summary>关闭时等待后台 flush/compaction 线程结束的最长时间。</summary>
    internal TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>新建实例时的 generation。</summary>
    internal long Generation { get; init; }

    /// <summary>SST 写入选项（generation 由引擎覆盖）。</summary>
    internal KvSstWriterOptions SstWriterOptions { get; init; } = KvSstWriterOptions.Default;

    /// <summary>版本集选项。</summary>
    internal KvVersionSetOptions VersionSetOptions { get; init; } = KvVersionSetOptions.Default;

    /// <summary>可选的跨实例 SST 读取并发预算。</summary>
    internal KvDiskReadBudget? ReadBudget { get; init; }

    /// <summary>测试用故障注入钩子；抛出异常即模拟在该点崩溃。</summary>
    internal Action<KvLsmEnginePhase>? TestHook { get; init; }

    /// <summary>leveled compaction 选项。</summary>
    internal KvCompactionOptions Compaction { get; init; } = KvCompactionOptions.Default;

    /// <summary>L0 文件数达到该值时写入等待 compaction（背压）。</summary>
    internal int Level0StopWritesTrigger { get; init; } = 20;

    /// <summary>共享块缓存；为 null 时引擎创建私有的 32 MiB 缓存。</summary>
    internal KvBlockCache? BlockCache { get; init; }

    /// <summary>后台 flush/compaction 调度器；为 null 时使用进程共享的 <see cref="KvLsmBackgroundScheduler.Shared"/>。</summary>
    internal KvLsmBackgroundScheduler? Scheduler { get; init; }

    /// <summary>跨引擎的 memtable 内存预算；为 null 时不参与全局预算（只受本引擎的轮转与背压约束）。</summary>
    internal KvWriteBufferManager? WriteBufferManager { get; init; }

    /// <summary>后台调度时每轮最多执行的 compaction 作业数，之后重新排队，让其他引擎有机会执行。</summary>
    internal int MaxCompactionJobsPerTurn { get; init; } = 4;

    /// <summary>仅后台 compaction 的暂停条件；flush 与显式 Compact 不受影响，L0 停写水位强制推进。</summary>
    internal Func<KvCleanupThrottleReason>? CompactionPauseReason { get; init; }

    /// <summary>
    /// compaction 判断 TTL 到期所用的时钟（UTC ticks）；默认 <see cref="DateTimeOffset.UtcNow"/>。
    /// 前提：之后获取的快照读取时刻不早于该时钟，否则已被物理回收的到期条目对其不可见。
    /// </summary>
    internal Func<long> UtcNowTicks { get; init; } = static () => DateTimeOffset.UtcNow.UtcTicks;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Compaction);
        Compaction.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(Level0StopWritesTrigger, Compaction.Level0CompactionTrigger);
        ArgumentNullException.ThrowIfNull(UtcNowTicks);
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteBufferBytes, 64 * 1024);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxImmutableMemTables);
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteBackpressureTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(WalBufferSize);
        ArgumentOutOfRangeException.ThrowIfNegative(Generation);
        ArgumentNullException.ThrowIfNull(SstWriterOptions);
        ArgumentNullException.ThrowIfNull(VersionSetOptions);
    }
}

/// <summary>引擎运行统计（诊断与测试用）。</summary>
/// <param name="LastSequence">已提交的最后 sequence。</param>
/// <param name="ImmutableMemTables">待刷盘的不可变 memtable 数。</param>
/// <param name="ActiveMemTableBytes">活跃 memtable 实际占用字节数。</param>
/// <param name="FileCountByLevel">各层 SST 文件数。</param>
/// <param name="FlushCount">已完成的 flush 次数。</param>
/// <param name="WalNumber">活跃 WAL 文件号。</param>
/// <param name="CompactionCount">已完成的 compaction 次数（含 trivial move）。</param>
/// <param name="BytesByLevel">各层 SST 总字节数。</param>
internal sealed record KvLsmEngineStats(
    long LastSequence,
    int ImmutableMemTables,
    long ActiveMemTableBytes,
    IReadOnlyList<int> FileCountByLevel,
    long FlushCount,
    long WalNumber,
    long CompactionCount,
    IReadOnlyList<long> BytesByLevel);

/// <summary>
/// 单个 KV-LSM 实例：WAL + 活跃/不可变 memtable + 版本集管理的 SST，提供原子批量写、O(1) 快照、
/// 点查、双向范围迭代、后台 flush、leveled compaction 与崩溃恢复。由 <see cref="KvKeyspace"/> 在 LSM 模式下使用（K4b）。
/// </summary>
/// <remarks>
/// <para>
/// 目录：<c>CURRENT</c>、<c>MANIFEST-*</c>、<c>sst/</c>，以及 <c>wal/&lt;文件号&gt;.SDBKVWAL</c>
/// （复用 KV WAL v3 格式，每个 memtable 对应一个 WAL）。manifest 的 LogNumber 之前的 WAL 已全部持久化到 SST。
/// </para>
/// <para>
/// 写入在写锁内依次完成：分配 sequence 并追加一条原子 <c>MutationBatch</c> WAL 记录（可选 fsync）→
/// 插入 memtable → 发布 LastSequence。WAL 追加或插入失败时提交结果未知，引擎进入 faulted 并拒绝后续写入。
/// </para>
/// <para>
/// 读状态（活跃 memtable、不可变 memtable、版本）是写时复制的不可变对象。快照先读 LastSequence 再读状态，
/// 由于 memtable 插入先于发布 sequence、flush 在同一次状态切换中“移除 memtable 并加入 SST”，
/// 任何不晚于该 sequence 的提交都必然包含在所取得的状态中。
/// </para>
/// </remarks>
internal sealed partial class KvLsmEngine : IDisposable, IKvWriteBufferClient
{
    private readonly string _root;
    private readonly KvLsmEngineOptions _options;
    private readonly KvVersionSet _versions;
    private readonly object _writeLock = new();
    private readonly object _stateLock = new();
    private readonly object _tableCacheLock = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<long, KvSstReader> _tableCache = [];
    private readonly List<(KvMemTable Table, long WalNumber)> _immutables = [];
    private readonly SemaphoreSlim _compactionGate = new(1, 1);
    private readonly KvLsmBackgroundScheduler? _scheduler;
    private readonly KvWriteBufferManager? _writeBufferManager;
    private readonly Action _flushJob;
    private readonly Action _compactionJob;
    private readonly Timer? _compactionDelay;
    private long _compactionPauseCount;

    internal long CompactionPauseCount => Interlocked.Read(ref _compactionPauseCount);
    private readonly object _backgroundSync = new();
    private int _backgroundJobs;
    private bool _shuttingDown;
    private int _flushQueued;
    private int _compactionQueued;
    private int _bufferFlushRequested;
    private int _flushRetry;
    private int _compactionRetry;
    private long _immutableBytes;
    private long _reportedWriteBufferBytes = -1;
    private bool _reportedFlushPending;
    private readonly byte[]?[] _compactPointers = new byte[KvVersion.LevelCount][];
    private readonly KvBlockCache _blockCache;
    private long _compactionCount;
    private KvMemTable _mutable;
    private KvWalFile _wal;
    private long _walNumber;
    private ReadState _state;
    private long _lastSequence;
    private long _flushCount;
    private Exception? _fault;
    private bool _disposed;

    private KvLsmEngine(string root, KvLsmEngineOptions options, KvVersionSet versions, KvWalFile wal, long walNumber, long lastSequence)
    {
        _root = root;
        _options = options;
        _versions = versions;
        _wal = wal;
        _walNumber = walNumber;
        _lastSequence = lastSequence;
        _mutable = new KvMemTable();
        _state = new ReadState(_mutable, [], versions.AcquireCurrent());
        _blockCache = options.BlockCache ?? new KvBlockCache(32L * 1024 * 1024);
        _flushJob = RunScheduledFlush;
        _compactionJob = RunScheduledCompaction;
        if (options.BackgroundFlush && options.CompactionPauseReason is not null)
            _compactionDelay = new Timer(static state => ((KvLsmEngine)state!).ScheduleCompaction(), this, Timeout.Infinite, Timeout.Infinite);
        _writeBufferManager = options.WriteBufferManager;
        _writeBufferManager?.Register(this);
        LiveEngines.TryAdd(this, 0);
        if (options.BackgroundFlush)
        {
            // 不再为每个引擎启动专用线程：flush/compaction 作为作业排入进程共享调度器。
            _scheduler = options.Scheduler ?? KvLsmBackgroundScheduler.Shared;
            ScheduleCompaction();
        }
    }

    /// <summary>
    /// 全局写缓冲预算请求本引擎尽快轮转并刷盘：只设置标志并排队 flush 作业，不获取任何锁。
    /// 同步模式（无后台调度）下由下一次写入在写锁内完成轮转。
    /// </summary>
    void IKvWriteBufferClient.RequestFlushForWriteBuffer()
    {
        Interlocked.Exchange(ref _bufferFlushRequested, 1);
        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        if (_scheduler is null || Volatile.Read(ref _shuttingDown) || Interlocked.Exchange(ref _flushQueued, 1) != 0)
            return;
        try
        {
            _scheduler.ScheduleFlush(_flushJob);
        }
        catch (ObjectDisposedException)
        {
            Interlocked.Exchange(ref _flushQueued, 0);
        }
    }

    private void ScheduleCompaction()
    {
        if (_scheduler is null || Volatile.Read(ref _shuttingDown) || Interlocked.Exchange(ref _compactionQueued, 1) != 0)
            return;
        try
        {
            _scheduler.ScheduleCompaction(_compactionJob);
        }
        catch (ObjectDisposedException)
        {
            Interlocked.Exchange(ref _compactionQueued, 0);
        }
    }

    private bool TryEnterBackgroundJob()
    {
        lock (_backgroundSync)
        {
            if (_shuttingDown)
                return false;
            _backgroundJobs++;
            return true;
        }
    }

    private void ExitBackgroundJob()
    {
        lock (_backgroundSync)
        {
            _backgroundJobs--;
            if (_backgroundJobs == 0)
                Monitor.PulseAll(_backgroundSync);
        }
    }

    private void RunScheduledFlush()
    {
        // 先清除排队标志：执行期间产生的新请求会重新排队，不会丢失。
        Interlocked.Exchange(ref _flushQueued, 0);
        if (!TryEnterBackgroundJob())
            return;
        bool ran = false;
        try
        {
            if (Interlocked.Exchange(ref _bufferFlushRequested, 0) == 1)
            {
                lock (_writeLock)
                {
                    if (!_disposed && _fault is null && !_mutable.IsEmpty)
                        RotateLocked();
                    else
                        ReportWriteBufferLocked(); // 请求已作废（例如写入路径已轮转）：同步告知预算管理器
                }
            }
            // 共享调度线程不得阻塞在本引擎的门上（暂停维护或另一作业持有时），否则会拖住其他引擎。
            ran = TryEnterGate(_flushGate, ref _flushRetry);
            if (ran)
            {
                try
                {
                    FlushPendingCore(_shutdown.Token);
                }
                finally
                {
                    _flushGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // FlushPending/RotateLocked 已记录 _fault 并唤醒等待者；写入随后 fail closed，需要重新打开实例。
        }
        finally
        {
            ExitBackgroundJob();
        }
        // 执行期间若有其他作业因门被占用而放弃，补排一次（只在确有待刷 memtable 时）。
        if (ran && Interlocked.Exchange(ref _flushRetry, 0) == 1)
        {
            bool pending;
            lock (_writeLock)
                pending = _immutables.Count > 0 && _fault is null && !_disposed;
            if (pending)
                ScheduleFlush();
        }
    }

    private void RunScheduledCompaction()
    {
        Interlocked.Exchange(ref _compactionQueued, 0);
        if (!TryEnterBackgroundJob())
            return;
        bool more = false;
        bool ran = false;
        try
        {
            ran = TryEnterGate(_compactionGate, ref _compactionRetry);
            if (ran)
            {
                try
                {
                    if (ShouldDelayCompaction())
                    {
                        Interlocked.Increment(ref _compactionPauseCount);
                        // Idle engines never arm a polling timer. Only pending compaction work
                        // retries under pressure, without occupying a shared worker.
                        try { _compactionDelay?.Change(250, Timeout.Infinite); }
                        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed)) { }
                        return;
                    }
                    more = CompactCore(_shutdown.Token, Math.Max(1, _options.MaxCompactionJobsPerTurn));
                }
                finally
                {
                    _compactionGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // RunCompactionJob 已记录 _fault 并唤醒等待者。
        }
        finally
        {
            ExitBackgroundJob();
        }
        // 执行期间若有其他作业因门被占用而放弃（例如新的 L0 文件触发的 compaction），在这里补排一次。
        if (more || (ran && Interlocked.Exchange(ref _compactionRetry, 0) == 1))
            ScheduleCompaction();
    }

    private bool ShouldDelayCompaction()
    {
        if (_options.CompactionPauseReason is null) return false;
        KvVersion version = _versions.AcquireCurrent();
        try
        {
            return version.Files(0).Count < _options.Level0StopWritesTrigger
                && KvCompactionPicker.Pick(version, _options.Compaction, _compactPointers) is not null
                && _options.CompactionPauseReason() != KvCleanupThrottleReason.None;
        }
        finally { version.Release(); }
    }

    /// <summary>
    /// 不阻塞地取得门。取不到时登记重试再试一次：持有者释放门后检查重试标志并重新排队，
    /// 二者之间不存在遗漏窗口。取得后清除重试标志（本次执行覆盖之前的请求）。
    /// </summary>
    private static bool TryEnterGate(SemaphoreSlim gate, ref int retry)
    {
        if (!gate.Wait(0))
        {
            Interlocked.Exchange(ref retry, 1);
            if (!gate.Wait(0))
                return false;
        }
        Interlocked.Exchange(ref retry, 0);
        return true;
    }

    /// <summary>在写锁内向全局写缓冲预算上报 memtable 占用与刷盘请求状态（只在变化时上报）。</summary>
    private void ReportWriteBufferLocked()
    {
        if (_writeBufferManager is null)
            return;
        long mutableBytes = _mutable.ApproximateMemoryUsage;
        long total = mutableBytes + _immutableBytes;
        bool pending = Volatile.Read(ref _bufferFlushRequested) == 1;
        if (total == _reportedWriteBufferBytes && pending == _reportedFlushPending)
            return;
        _reportedWriteBufferBytes = total;
        _reportedFlushPending = pending;
        _writeBufferManager.Report(this, total, mutableBytes, pending);
    }

    /// <summary>块缓存（诊断与测试用）。</summary>
    internal KvBlockCache BlockCache => _blockCache;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<KvLsmEngine, byte> LiveEngines = new();

    /// <summary>
    /// 暂停本引擎的 flush 与 compaction（等待正在执行的作业结束），直到返回值被释放；期间不会产生或删除 SST/MANIFEST。
    /// 写入仍可进入 memtable 与 WAL，memtable 用满时按背压规则等待或超时。
    /// </summary>
    internal IDisposable EnterMaintenancePause()
    {
        _flushGate.Wait();
        try
        {
            _compactionGate.Wait();
        }
        catch
        {
            _flushGate.Release();
            throw;
        }
        return new MaintenancePause(this);
    }

    /// <summary>
    /// 暂停根目录位于 <paramref name="directory"/> 之下的所有已打开引擎的 flush/compaction（备份复制文件期间使用）。
    /// </summary>
    internal static IDisposable PauseMaintenanceUnder(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        string prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var pauses = new List<IDisposable>();
        try
        {
            foreach (KvLsmEngine engine in LiveEngines.Keys.OrderBy(static engine => engine._root, StringComparer.Ordinal))
            {
                if (engine._root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    pauses.Add(engine.EnterMaintenancePause());
            }
        }
        catch
        {
            foreach (IDisposable pause in pauses)
                pause.Dispose();
            throw;
        }
        return new CompositePause(pauses);
    }

    private sealed class MaintenancePause(KvLsmEngine engine) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            engine._compactionGate.Release();
            engine._flushGate.Release();
            // 暂停期间被推迟的作业重新排队。
            engine.ScheduleFlush();
            engine.ScheduleCompaction();
        }
    }

    private sealed class CompositePause(List<IDisposable> pauses) : IDisposable
    {
        public void Dispose()
        {
            foreach (IDisposable pause in pauses)
                pause.Dispose();
        }
    }

    /// <summary>取得当前版本的引用（诊断与测试用），调用方必须释放。</summary>
    internal KvVersion AcquireCurrentVersion() => _versions.AcquireCurrent();

    /// <summary>实例根目录。</summary>
    internal string RootDirectory => _root;

    /// <summary>已提交的最后 sequence。</summary>
    internal long LastSequence => Volatile.Read(ref _lastSequence);

    /// <summary>当前 generation。</summary>
    internal long Generation => _versions.Generation;

    /// <summary>引擎是否因写入或后台 flush 失败而拒绝写入。</summary>
    internal bool IsFaulted { get { lock (_writeLock) return _fault is not null; } }

    /// <summary>首个导致引擎拒绝写入的异常；未故障时为 null。调用方用引用相等判断“提交结果未知”。</summary>
    internal Exception? Fault { get { lock (_writeLock) return _fault; } }

    private Action? _walSyncTestHook;
    /// <summary>共享事务真实强杀的 WAL 已同步、memtable 尚未发布边界。</summary>
    internal Action? AfterWalAppendTestHook { get; set; }
    private Action? _walDisposeFlushTestHook;

    /// <summary>测试钩子：在每次 WAL fsync 前调用（随 WAL 轮转转移到新文件）。</summary>
    internal Action? WalSyncTestHook
    {
        set
        {
            lock (_writeLock)
            {
                _walSyncTestHook = value;
                if (!_disposed)
                    _wal.SyncTestHook = value;
            }
        }
    }

    /// <summary>测试钩子：在 WAL 关闭刷盘前调用（随 WAL 轮转转移到新文件）。</summary>
    internal Action? WalDisposeFlushTestHook
    {
        set
        {
            lock (_writeLock)
            {
                _walDisposeFlushTestHook = value;
                if (!_disposed)
                    _wal.DisposeFlushTestHook = value;
            }
        }
    }

    private void InstallWalTestHooks(KvWalFile wal)
    {
        wal.SyncTestHook = _walSyncTestHook;
        wal.DisposeFlushTestHook = _walDisposeFlushTestHook;
    }

    /// <summary>下一次提交将分配的 sequence（不保留；仅供在同一写锁外乐观构造批次）。</summary>
    internal long NextSequence => Volatile.Read(ref _lastSequence) + 1;

    internal bool SyncWalOnEveryWrite => _options.SyncWalOnEveryWrite;

    /// <summary>活跃 WAL 的字节数；已释放时为 0。</summary>
    internal long ActiveWalLength
    {
        get
        {
            lock (_writeLock)
                return _disposed ? 0 : _wal.Length;
        }
    }

    /// <summary>活跃 WAL 文件的完整路径（随 memtable 轮换和 Clear 变化；诊断与测试用）。</summary>
    internal string ActiveWalPath => KvLsmPaths.WalPath(_root, Volatile.Read(ref _walNumber));

    /// <summary>活跃 memtable 的条目数与全部不可变 memtable 的条目数之和（不含 range tombstone）。</summary>
    internal (long Mutable, long Immutable) MemTableEntryCounts
    {
        get
        {
            lock (_stateLock)
                return (_state.Mutable.EntryCount, _state.Immutables.Sum(static table => table.EntryCount));
        }
    }

    /// <summary>
    /// 在写锁内执行 <paramref name="action"/>，期间不会有其他提交插入；用于读取-判断-提交的原子操作。
    /// 回调可调用 <see cref="ApplyUnderWriteLock"/>；写锁为可重入的 <see cref="Monitor"/>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌，仅在等待写锁时生效。</param>
    /// <param name="lockTimeout">等待写锁的上限；超时抛出 <see cref="TimeoutException"/>。null 表示无限等待。</param>
    internal T ExecuteUnderWriteLock<T>(Func<T> action, CancellationToken cancellationToken, TimeSpan? lockTimeout)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (lockTimeout is null && !cancellationToken.CanBeCanceled)
        {
            lock (_writeLock)
            {
                ThrowIfUnwritableLocked();
                return action();
            }
        }

        TimeSpan timeout = lockTimeout ?? Timeout.InfiniteTimeSpan;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool taken = false;
        try
        {
            while (!taken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan slice = TimeSpan.FromMilliseconds(50);
                if (timeout != Timeout.InfiniteTimeSpan)
                {
                    TimeSpan remaining = timeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);
                    if (remaining <= TimeSpan.Zero)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException("KV write timed out while waiting for the keyspace lock.");
                    }
                    if (remaining < slice)
                        slice = remaining;
                }
                Monitor.TryEnter(_writeLock, slice, ref taken);
            }

            ThrowIfUnwritableLocked();
            return action();
        }
        finally
        {
            if (taken)
                Monitor.Exit(_writeLock);
        }
    }

    /// <summary>
    /// 在调用方已持有写锁（<see cref="ExecuteUnderWriteLock"/> 回调内）时提交一批变更；
    /// <paramref name="expectedSequence"/> 非空时校验本次提交恰好使用该 sequence。
    /// </summary>
    /// <exception cref="InvalidOperationException">调用方未持有写锁，或 sequence 已被其他提交占用。</exception>
    internal long ApplyUnderWriteLock(
        IReadOnlyList<KvBatchMutation> mutations,
        IReadOnlyList<KvRangeDeletion> rangeDeletions,
        long? expectedSequence,
        CancellationToken cancellationToken)
    {
        if (!Monitor.IsEntered(_writeLock))
            throw new InvalidOperationException("KV-LSM ApplyUnderWriteLock requires the engine write lock.");
        if (expectedSequence is long expected && expected != Volatile.Read(ref _lastSequence) + 1)
            throw new InvalidOperationException("KV-LSM commit sequence changed while the batch was being prepared.");
        return Apply(mutations, rangeDeletions, cancellationToken);
    }

    /// <summary>
    /// 在调用方已持有写锁时等待写入预算（不可变 memtable 与 L0 背压）。等待期间会暂时释放写锁，
    /// 因此“读取-判断-提交”操作必须先调用本方法，再读取状态并提交，保证读取与提交之间没有其他提交。
    /// </summary>
    internal void WaitForWriteBudgetUnderLock(CancellationToken cancellationToken)
    {
        if (!Monitor.IsEntered(_writeLock))
            throw new InvalidOperationException("KV-LSM WaitForWriteBudgetUnderLock requires the engine write lock.");
        ThrowIfUnwritableLocked();
        WaitForWriteBudgetLocked(cancellationToken);
    }

    /// <summary>调用方持有写锁时，判断写入当前是否会因背压而等待。</summary>
    internal bool IsWriteStalledUnderLock
    {
        get
        {
            if (!Monitor.IsEntered(_writeLock))
                throw new InvalidOperationException("KV-LSM IsWriteStalledUnderLock requires the engine write lock.");
            return IsWriteStalledLocked();
        }
    }

    /// <summary>打开（或在空目录创建）实例，并重放 LogNumber 之后的 WAL。</summary>
    /// <exception cref="InvalidDataException">manifest、SST 或 WAL 损坏，或 WAL 含 LSM 不支持的记录类型。</exception>
    internal static KvLsmEngine Open(string root, KvLsmEngineOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        options ??= KvLsmEngineOptions.Default;
        options.Validate();
        string fullRoot = Path.GetFullPath(root);
        KvVersionSet versions = File.Exists(KvLsmPaths.CurrentPath(fullRoot))
            ? KvVersionSet.Open(fullRoot, options.VersionSetOptions)
            : KvVersionSet.Create(fullRoot, options.Generation, options.VersionSetOptions);
        KvWalFile? wal = null;
        try
        {
            string walDirectory = KvLsmPaths.WalDirectory(fullRoot);
            Directory.CreateDirectory(walDirectory);

            long lastSequence = versions.LastSequence;
            long persistedSequence = lastSequence;
            var recovered = new KvMemTable();
            var replayed = new List<long>();
            foreach ((long number, string path) in EnumerateWalFiles(walDirectory))
            {
                if (number < versions.LogNumber)
                    continue;
                replayed.Add(number);
                using KvWalFile replay = KvWalFile.Open(
                    path,
                    Math.Max(lastSequence + 1, 1),
                    options.WalBufferSize,
                    record =>
                    {
                        if (record.Sequence <= persistedSequence)
                            return;
                        ApplyRecord(recovered, record);
                        lastSequence = Math.Max(lastSequence, record.Sequence);
                    });
            }

            long walNumber = versions.NewFileNumber();
            versions.ReleasePendingOutput(walNumber);
            wal = KvWalFile.Open(KvLsmPaths.WalPath(fullRoot, walNumber), lastSequence + 1, options.WalBufferSize);
            DirectoryFsync.FlushRequired(walDirectory);

            var edit = new KvVersionEdit { LogNumber = walNumber, LastSequence = lastSequence };
            if (!recovered.IsEmpty)
                edit.AddedFiles.Add(WriteLevel0(versions, recovered, options));
            versions.LogAndApply(edit);

            foreach ((long number, string path) in EnumerateWalFiles(walDirectory))
            {
                if (number < walNumber)
                    File.Delete(path);
            }
            DirectoryFsync.FlushBestEffort(walDirectory);
            versions.DeleteObsoleteFiles();

            return new KvLsmEngine(fullRoot, options, versions, wal, walNumber, lastSequence);
        }
        catch
        {
            wal?.Dispose();
            versions.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 原子提交一批 put/delete；同一 key 重复时以最后一次为准。返回整批共享的 commit sequence。
    /// </summary>
    /// <exception cref="ArgumentException">批次为空或包含无效 mutation。</exception>
    /// <exception cref="IOException">WAL 写入失败（引擎进入 faulted）或背压等待超时（可重试分类）。</exception>
    /// <exception cref="InvalidOperationException">引擎已 faulted。</exception>
    internal long Apply(IReadOnlyList<KvBatchMutation> mutations, CancellationToken cancellationToken = default)
        => Apply(mutations, [], cancellationToken);

    /// <summary>
    /// 原子提交一批点写入与范围删除：范围删除只遮蔽 sequence 更小的条目，因此同批次点写入不受影响。
    /// </summary>
    /// <exception cref="ArgumentException">批次为空、mutation 或范围无效。</exception>
    internal long Apply(
        IReadOnlyList<KvBatchMutation> mutations,
        IReadOnlyList<KvRangeDeletion> rangeDeletions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(rangeDeletions);
        foreach (KvRangeDeletion range in rangeDeletions)
        {
            ArgumentNullException.ThrowIfNull(range, nameof(rangeDeletions));
            range.Validate();
        }
        KvBatchMutation[] batch = Canonicalize(mutations, allowEmpty: rangeDeletions.Count > 0);
        KvRangeDeletion[] ranges = [.. rangeDeletions];

        lock (_writeLock)
        {
            ThrowIfUnwritableLocked();
            WaitForWriteBudgetLocked(cancellationToken);

            long sequence;
            try
            {
                sequence = ranges.Length > 0
                    ? _wal.AppendMutationBatchWithRanges(batch, ranges)
                    : _wal.AppendMutationBatch(batch);
            }
            catch (ArgumentException)
            {
                // 编码前的参数校验失败：WAL 未写入任何字节，引擎状态不变。
                throw;
            }
            catch (Exception exception)
            {
                _fault ??= exception;
                throw;
            }

            try
            {
                if (_options.SyncWalOnEveryWrite)
                    _wal.Sync();
                _options.TestHook?.Invoke(KvLsmEnginePhase.AfterWalAppend);
                AfterWalAppendTestHook?.Invoke();
                foreach (KvRangeDeletion range in ranges)
                    _mutable.AddRangeDeletion(range, sequence);
                foreach (KvBatchMutation mutation in batch)
                {
                    bool delete = mutation.Value is null;
                    _mutable.Add(
                        KvInternalKey.Encode(mutation.Key, sequence, delete ? KvEntryKind.Delete : KvEntryKind.Put),
                        mutation.Value,
                        mutation.ExpiresAtUtc?.UtcTicks ?? 0);
                }
            }
            catch (Exception exception)
            {
                _fault ??= exception;
                throw;
            }

            Volatile.Write(ref _lastSequence, sequence);
            // 达到本引擎写缓冲上限，或全局预算请求本引擎刷盘（同步模式下只能在这里兑现）时轮转。
            if (_mutable.UsedBytes >= _options.WriteBufferBytes
                || (Volatile.Read(ref _bufferFlushRequested) == 1 && Interlocked.Exchange(ref _bufferFlushRequested, 0) == 1))
            {
                RotateLocked();
            }
            else
            {
                ReportWriteBufferLocked();
            }
            return sequence;
        }
    }

    /// <summary>删除 <c>[startInclusive, endExclusive)</c> 内的全部现有 key（O(1) 写入一个 range tombstone）。</summary>
    internal long DeleteRange(byte[] startInclusive, byte[] endExclusive, CancellationToken cancellationToken = default)
        => Apply([], [new KvRangeDeletion(startInclusive, endExclusive)], cancellationToken);

    /// <summary>
    /// 清空整个实例：在一次 VersionEdit 中推进 generation、删除全部 SST 并把 WAL 下界移到新 WAL，
    /// 同时丢弃全部 memtable。已有快照仍读取其固定的旧状态。返回新的 generation。
    /// </summary>
    /// <remarks>与后台 flush/compaction 互斥；<see cref="KvLsmEngineOptions.BackgroundFlush"/> 为 false 的同步模式仅供单线程测试。</remarks>
    internal long Clear(CancellationToken cancellationToken = default)
    {
        _flushGate.Wait(cancellationToken);
        try
        {
            _compactionGate.Wait(cancellationToken);
            try
            {
                long walNumber;
                lock (_writeLock)
                {
                    ThrowIfUnwritableLocked();
                    // 与 legacy 一致：一次清空消耗一个 sequence；新 WAL 从该 sequence 之后开始，重开后不会回退。
                    long clearSequence = checked(_lastSequence + 1);
                    walNumber = _versions.NewFileNumber();
                    _versions.ReleasePendingOutput(walNumber);
                    KvWalFile next;
                    try
                    {
                        next = KvWalFile.Open(KvLsmPaths.WalPath(_root, walNumber), clearSequence + 1, _options.WalBufferSize);
                        DirectoryFsync.FlushRequired(KvLsmPaths.WalDirectory(_root));
                    }
                    catch (Exception exception)
                    {
                        _fault ??= exception;
                        throw;
                    }

                    var edit = new KvVersionEdit
                    {
                        Generation = _versions.Generation + 1,
                        LogNumber = walNumber,
                        LastSequence = clearSequence,
                    };
                    KvVersion current = _versions.AcquireCurrent();
                    try
                    {
                        foreach (KvLsmFileMetadata file in current.AllFiles)
                            edit.DeletedFiles.Add((file.Level, file.FileNumber));
                    }
                    finally
                    {
                        current.Release();
                    }

                    try
                    {
                        _versions.LogAndApply(edit);
                    }
                    catch (Exception exception)
                    {
                        next.Dispose();
                        _fault ??= exception;
                        throw;
                    }

                    try
                    {
                        _wal.Dispose();
                    }
                    catch (IOException)
                    {
                        // 旧 WAL 已低于新的 LogNumber，内容不再需要。
                    }
                    _wal = next;
                    InstallWalTestHooks(next);
                    Volatile.Write(ref _walNumber, walNumber);
                    _immutables.Clear();
                    _immutableBytes = 0;
                    _mutable = new KvMemTable();
                    ReportWriteBufferLocked();
                    Volatile.Write(ref _lastSequence, clearSequence);
                    PublishState(_versions.AcquireCurrent());
                    Monitor.PulseAll(_writeLock);
                }

                foreach ((long number, string path) in EnumerateWalFiles(KvLsmPaths.WalDirectory(_root)))
                {
                    if (number < walNumber)
                        TryDelete(path);
                }
                CleanupObsoleteFiles();
                return _versions.Generation;
            }
            finally
            {
                _compactionGate.Release();
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>取得当前一致快照；用完必须释放以便回收旧 SST。</summary>
    internal KvLsmSnapshot AcquireSnapshot(DateTimeOffset? readTimestampUtc = null)
    {
        long readTicks = (readTimestampUtc ?? DateTimeOffset.UtcNow).UtcTicks;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long sequence = Volatile.Read(ref _lastSequence);
            ReadState state = _state;
            return new KvLsmSnapshot(this, sequence, readTicks, state.Mutable, state.Immutables, state.Version.AddReference());
        }
    }

    /// <summary>读取当前可见值；不存在、已删除或已到期返回 null。</summary>
    internal KvEntry? Get(ReadOnlySpan<byte> key, CancellationToken cancellationToken = default)
    {
        using KvLsmSnapshot snapshot = AcquireSnapshot();
        return snapshot.Get(key, cancellationToken);
    }

    /// <summary>
    /// 轮转活跃 memtable（非空时）并等待全部不可变 memtable 刷盘完成。
    /// </summary>
    /// <exception cref="IOException">后台 flush 失败。</exception>
    internal void Flush(CancellationToken cancellationToken = default)
    {
        lock (_writeLock)
        {
            ThrowIfUnwritableLocked();
            if (!_mutable.IsEmpty)
                RotateLocked();
        }

        if (!_options.BackgroundFlush)
        {
            FlushPending(cancellationToken);
            MaybeCompact(cancellationToken);
            return;
        }

        lock (_writeLock)
        {
            while (_immutables.Count > 0)
            {
                ThrowIfUnwritableLocked();
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(_writeLock, TimeSpan.FromMilliseconds(100));
            }
            ThrowIfUnwritableLocked();
        }
    }

    /// <summary>强制 fsync 活跃 WAL（<see cref="KvLsmEngineOptions.SyncWalOnEveryWrite"/> 为 false 时使用）。</summary>
    internal void SyncWal()
    {
        lock (_writeLock)
        {
            ThrowIfUnwritableLocked();
            try
            {
                _wal.Sync();
            }
            catch (Exception exception)
            {
                _fault ??= exception;
                throw;
            }
        }
    }

    /// <summary>运行统计。</summary>
    internal KvLsmEngineStats GetStats()
    {
        lock (_stateLock)
        {
            ReadState state = _state;
            return new KvLsmEngineStats(
                Volatile.Read(ref _lastSequence),
                state.Immutables.Length,
                state.Mutable.UsedBytes,
                Enumerable.Range(0, KvVersion.LevelCount).Select(level => state.Version.Files(level).Count).ToArray(),
                Interlocked.Read(ref _flushCount),
                Volatile.Read(ref _walNumber),
                Interlocked.Read(ref _compactionCount),
                Enumerable.Range(0, KvVersion.LevelCount).Select(level => state.Version.Files(level).Sum(static f => f.FileSize)).ToArray());
        }
    }

    /// <summary>
    /// 停止后台 flush/compaction 并关闭 WAL 与 manifest；未刷盘数据由下次打开时重放 WAL 恢复。
    /// 仍被快照或迭代器引用的 SST 读取器保留到最后一个版本引用释放后才关闭（与 legacy 磁盘租约合同一致）。
    /// WAL 关闭失败按原类型重新抛出。
    /// </summary>
    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            Monitor.PulseAll(_writeLock);
        }

        _shutdown.Cancel();
        _compactionDelay?.Dispose();
        // 停止接收新的后台作业，并等待本引擎正在执行的 flush/compaction 观察到取消后退出。
        lock (_backgroundSync)
        {
            _shuttingDown = true;
            DateTime deadline = DateTime.UtcNow + _options.ShutdownTimeout;
            while (_backgroundJobs > 0)
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    break;
                Monitor.Wait(_backgroundSync, remaining);
            }
        }
        _writeBufferManager?.Unregister(this);
        LiveEngines.TryRemove(this, out _);

        Exception? walFailure = null;
        lock (_writeLock)
        {
            try
            {
                _wal.Dispose();
            }
            catch (Exception exception)
            {
                walFailure = exception;
            }
        }

        lock (_stateLock)
            _state.Version.Release();
        _versions.Dispose();
        _shutdown.Dispose();
        ReleaseUnreferencedReaders();
        if (walFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(walFailure).Throw();
    }

    /// <summary>
    /// 引擎已关闭时，关闭不再被任何存活版本引用的 SST 读取器；由快照释放时调用，最后一个快照释放后全部关闭。
    /// </summary>
    internal void ReleaseUnreferencedReaders()
    {
        lock (_tableCacheLock)
        {
            if (!_disposed || _tableCache.Count == 0)
                return;
            HashSet<long> live = _versions.LiveFileNumbers();
            foreach (long number in _tableCache.Keys.Where(number => !live.Contains(number)).ToList())
            {
                _tableCache[number].Dispose();
                _tableCache.Remove(number);
            }
        }
    }

    internal KvSstReader GetReader(long fileNumber, CancellationToken cancellationToken, bool maintenanceRead = false)
    {
        lock (_tableCacheLock)
        {
            if (_tableCache.TryGetValue(fileNumber, out KvSstReader? reader))
                return reader;
            // 关闭后仍允许已固定版本的快照按需打开其文件；版本引用保证文件尚未被删除。
            // maintenanceRead 只决定打开时元数据读取走哪个预算通道；之后的块读取由各迭代器自行指定。
            reader = KvSstReader.Open(KvLsmPaths.SstPath(_root, fileNumber), _options.ReadBudget, cancellationToken, _blockCache, maintenanceRead);
            _tableCache[fileNumber] = reader;
            return reader;
        }
    }

    /// <summary>
    /// 在提交 manifest 前以维护通道打开新写出的 SST：既校验文件可读（footer、索引、过滤器），
    /// 也把索引/过滤器的加载从后续第一个请求读取挪到后台维护路径。
    /// </summary>
    private void OpenNewTables(IEnumerable<KvLsmFileMetadata> files, CancellationToken cancellationToken)
    {
        foreach (KvLsmFileMetadata file in files)
            GetReader(file.FileNumber, cancellationToken, maintenanceRead: true);
    }

    private void RotateLocked()
    {
        long walNumber = _versions.NewFileNumber();
        _versions.ReleasePendingOutput(walNumber);
        KvWalFile next;
        try
        {
            next = KvWalFile.Open(KvLsmPaths.WalPath(_root, walNumber), _lastSequence + 1, _options.WalBufferSize);
            DirectoryFsync.FlushRequired(KvLsmPaths.WalDirectory(_root));
            _wal.Dispose();
        }
        catch (Exception exception)
        {
            _fault ??= exception;
            throw;
        }

        _immutables.Insert(0, (_mutable, _walNumber));
        _immutableBytes += _mutable.ApproximateMemoryUsage;
        _mutable = new KvMemTable();
        _wal = next;
        InstallWalTestHooks(next);
        Volatile.Write(ref _walNumber, walNumber);
        PublishState(version: null);
        ReportWriteBufferLocked();
        ScheduleFlush();
    }

    private void PublishState(KvVersion? version)
    {
        lock (_stateLock)
        {
            ReadState previous = _state;
            _state = new ReadState(
                _mutable,
                [.. _immutables.Select(static item => item.Table)],
                version ?? previous.Version.AddReference());
            previous.Version.Release();
        }
    }

    private bool IsWriteStalledLocked()
        => _immutables.Count >= _options.MaxImmutableMemTables
            || _state.Version.Files(0).Count >= _options.Level0StopWritesTrigger;

    private void WaitForWriteBudgetLocked(CancellationToken cancellationToken)
    {
        if (!IsWriteStalledLocked())
            return;
        if (!_options.BackgroundFlush)
        {
            // 无后台线程：在当前线程同步刷盘与 compaction（写锁可重入）。
            FlushPending(cancellationToken);
            MaybeCompact(cancellationToken);
            return;
        }
        DateTime deadline = DateTime.UtcNow + _options.WriteBackpressureTimeout;
        while (IsWriteStalledLocked())
        {
            ThrowIfUnwritableLocked();
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw KvAtomicBatchErrors.CreateCheckpointBackpressureTimeout(_options.WriteBackpressureTimeout);
            Monitor.Wait(_writeLock, remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100));
        }
    }


    private void FlushPending(CancellationToken cancellationToken)
    {
        _flushGate.Wait(cancellationToken);
        try
        {
            FlushPendingCore(cancellationToken);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>刷盘全部不可变 memtable（从旧到新）；调用方已持有 flush 门。</summary>
    private void FlushPendingCore(CancellationToken cancellationToken)
    {
        while (true)
        {
            KvMemTable table;
            lock (_writeLock)
            {
                if (_immutables.Count == 0 || _fault is not null)
                    return;
                table = _immutables[^1].Table;
            }

            try
            {
                FlushOne(table, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lock (_writeLock)
                {
                    _fault ??= exception;
                    Monitor.PulseAll(_writeLock);
                }
                throw;
            }
        }
    }

    private void FlushOne(KvMemTable table, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        KvLsmFileMetadata file = WriteLevel0(_versions, table, _options);
        _options.TestHook?.Invoke(KvLsmEnginePhase.FlushAfterSstWritten);
        OpenNewTables([file], cancellationToken);

        long nextLogNumber;
        lock (_writeLock)
        {
            // 从旧到新刷盘：剩余最旧的 memtable 的 WAL（或活跃 WAL）就是新的 WAL 下界。
            nextLogNumber = _immutables.Count >= 2 ? _immutables[^2].WalNumber : _walNumber;
        }

        var edit = new KvVersionEdit
        {
            LogNumber = nextLogNumber,
            LastSequence = Math.Max(_versions.LastSequence, file.MaxSequence),
        };
        edit.AddedFiles.Add(file);
        _versions.LogAndApply(edit);
        _options.TestHook?.Invoke(KvLsmEnginePhase.FlushAfterManifestCommit);

        lock (_writeLock)
        {
            int index = _immutables.FindIndex(item => ReferenceEquals(item.Table, table));
            _immutables.RemoveAt(index);
            _immutableBytes -= table.ApproximateMemoryUsage;
            PublishState(_versions.AcquireCurrent());
            Interlocked.Increment(ref _flushCount);
            ReportWriteBufferLocked();
            Monitor.PulseAll(_writeLock);
        }
        ScheduleCompaction();

        string walDirectory = KvLsmPaths.WalDirectory(_root);
        foreach ((long number, string path) in EnumerateWalFiles(walDirectory))
        {
            if (number < nextLogNumber)
                TryDelete(path);
        }
        CleanupObsoleteFiles();
    }

    private void CleanupObsoleteFiles()
    {
        KvObsoleteCleanupResult cleanup = _versions.DeleteObsoleteFiles();
        lock (_tableCacheLock)
        {
            foreach (long deleted in cleanup.DeletedSstFiles)
            {
                if (_tableCache.Remove(deleted, out KvSstReader? reader))
                    reader.Dispose();
            }
        }
    }

    /// <summary>
    /// 刷盘全部 memtable，并把所有 SST 合并到最底层（同一 key 只留最新版本，丢弃墓碑与已到期条目）。
    /// </summary>
    internal void CompactAll(CancellationToken cancellationToken = default)
    {
        Flush(cancellationToken);
        _compactionGate.Wait(cancellationToken);
        try
        {
            KvVersion version = _versions.AcquireCurrent();
            try
            {
                KvCompactionJob? job = KvCompactionPicker.Full(version);
                if (job is not null)
                    RunCompactionJob(job, version, cancellationToken);
            }
            finally
            {
                version.Release();
            }
        }
        finally
        {
            _compactionGate.Release();
        }
    }

    /// <summary>执行最多 <paramref name="maxJobs"/> 个 compaction 作业；返回 true 表示仍有待执行的作业。</summary>
    private bool MaybeCompact(CancellationToken cancellationToken, int maxJobs = int.MaxValue)
    {
        _compactionGate.Wait(cancellationToken);
        try
        {
            return CompactCore(cancellationToken, maxJobs);
        }
        finally
        {
            _compactionGate.Release();
        }
    }

    /// <summary>调用方已持有 compaction 门；执行最多 <paramref name="maxJobs"/> 个作业，返回是否仍有待执行的作业。</summary>
    private bool CompactCore(CancellationToken cancellationToken, int maxJobs)
    {
        for (int executed = 0; ; executed++)
        {
            lock (_writeLock)
            {
                if (_fault is not null || _disposed)
                    return false;
            }

            KvVersion version = _versions.AcquireCurrent();
            try
            {
                KvCompactionJob? job = KvCompactionPicker.Pick(version, _options.Compaction, _compactPointers);
                if (job is null)
                    return false;
                if (executed >= maxJobs)
                    return true;
                RunCompactionJob(job, version, cancellationToken);
            }
            finally
            {
                version.Release();
            }
        }
    }

    private void RunCompactionJob(KvCompactionJob job, KvVersion version, CancellationToken cancellationToken)
    {
        try
        {
            // compaction 是后台维护：输入块读取走读取预算的维护通道，请求过载不会让 compaction 失败并使 keyspace 只读。
            KvVersionEdit edit = KvCompactionExecutor.Run(
                job,
                version,
                _versions,
                file => GetReader(file.FileNumber, cancellationToken, maintenanceRead: true).CreateIterator(cancellationToken, maintenanceRead: true),
                file => GetReader(file.FileNumber, cancellationToken, maintenanceRead: true).RangeTombstones,
                _options.SstWriterOptions,
                _options.Compaction,
                _options.UtcNowTicks(),
                cancellationToken);
            _options.TestHook?.Invoke(KvLsmEnginePhase.CompactionAfterOutputsWritten);
            OpenNewTables(edit.AddedFiles, cancellationToken);
            _versions.LogAndApply(edit);
            _options.TestHook?.Invoke(KvLsmEnginePhase.CompactionAfterManifestCommit);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_writeLock)
            {
                _fault ??= exception;
                Monitor.PulseAll(_writeLock);
            }
            throw;
        }

        lock (_writeLock)
        {
            PublishState(_versions.AcquireCurrent());
            Interlocked.Increment(ref _compactionCount);
            Monitor.PulseAll(_writeLock);
        }
        CleanupObsoleteFiles();
    }

    private static KvLsmFileMetadata WriteLevel0(KvVersionSet versions, KvMemTable table, KvLsmEngineOptions options)
    {
        long number = versions.NewFileNumber();
        string path = KvLsmPaths.SstPath(versions.RootDirectory, number);
        try
        {
            KvSstProperties properties;
            using (var writer = new KvSstWriter(path, options.SstWriterOptions with { Generation = versions.Generation }))
            {
                using KvMemTableIterator iterator = table.CreateIterator();
                for (iterator.SeekToFirst(); iterator.Valid; iterator.MoveNext())
                    writer.Add(iterator.Key, iterator.Value, iterator.ExpiresAtUtcTicks);
                foreach (KvRangeTombstone tombstone in table.RangeTombstones)
                    writer.AddRangeTombstone(tombstone);
                properties = writer.Finish();
            }
            return KvLsmFileMetadata.FromProperties(number, level: 0, new FileInfo(path).Length, properties);
        }
        catch
        {
            versions.ReleasePendingOutput(number);
            throw;
        }
    }

    private static void ApplyRecord(KvMemTable table, KvWalRecord record)
    {
        switch (record.Kind)
        {
            case KvWalRecordKind.MutationBatch:
                foreach (KvBatchMutation mutation in KvWalFile.DecodeMutationBatch(record))
                {
                    bool delete = mutation.Value is null;
                    table.Add(
                        KvInternalKey.Encode(mutation.Key, record.Sequence, delete ? KvEntryKind.Delete : KvEntryKind.Put),
                        mutation.Value,
                        mutation.ExpiresAtUtc?.UtcTicks ?? 0);
                }
                break;
            case KvWalRecordKind.MutationBatchWithRanges:
                (IReadOnlyList<KvBatchMutation> mutations, IReadOnlyList<KvRangeDeletion> ranges) = KvWalFile.DecodeMutationBatchWithRanges(record);
                foreach (KvRangeDeletion range in ranges)
                    table.AddRangeDeletion(range, record.Sequence);
                foreach (KvBatchMutation mutation in mutations)
                {
                    bool delete = mutation.Value is null;
                    table.Add(
                        KvInternalKey.Encode(mutation.Key, record.Sequence, delete ? KvEntryKind.Delete : KvEntryKind.Put),
                        mutation.Value,
                        mutation.ExpiresAtUtc?.UtcTicks ?? 0);
                }
                break;
            case KvWalRecordKind.Put:
                table.Add(KvInternalKey.Encode(record.Key, record.Sequence, KvEntryKind.Put), record.Value, record.ExpiresAtUtc?.UtcTicks ?? 0);
                break;
            case KvWalRecordKind.Delete:
                table.Add(KvInternalKey.Encode(record.Key, record.Sequence, KvEntryKind.Delete), []);
                break;
            default:
                throw new InvalidDataException($"KV-LSM WAL contains unsupported record kind {record.Kind} at sequence {record.Sequence}.");
        }
    }

    private static KvBatchMutation[] Canonicalize(IReadOnlyList<KvBatchMutation> mutations, bool allowEmpty)
    {
        if (mutations.Count == 0)
        {
            if (allowEmpty)
                return [];
            throw new ArgumentException("KV-LSM batch must contain at least one mutation.", nameof(mutations));
        }
        var lastIndex = new Dictionary<byte[], int>(KvKeyComparer.Instance);
        for (int i = 0; i < mutations.Count; i++)
        {
            KvBatchMutation mutation = mutations[i] ?? throw new ArgumentException("KV-LSM batch contains a null mutation.", nameof(mutations));
            if (mutation.Key is not { Length: > 0 })
                throw new ArgumentException("KV-LSM batch keys must be non-empty.", nameof(mutations));
            if (mutation.Value is null && mutation.ExpiresAtUtc.HasValue)
                throw new ArgumentException("KV-LSM delete mutations cannot carry a TTL.", nameof(mutations));
            lastIndex[mutation.Key] = i;
        }
        return lastIndex.Count == mutations.Count
            ? [.. mutations]
            : [.. lastIndex.Values.Order().Select(index => mutations[index])];
    }

    private static IEnumerable<(long Number, string Path)> EnumerateWalFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return [];
        return Directory.EnumerateFiles(directory)
            .Select(static path => (Ok: KvLsmPaths.TryParseWalFileName(Path.GetFileName(path), out long number), Number: number, Path: path))
            .Where(static item => item.Ok)
            .Select(static item => (item.Number, item.Path))
            .OrderBy(static item => item.Number)
            .ToList();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 下一次打开会再次删除 LogNumber 之前的 WAL。
        }
    }

    private void ThrowIfUnwritableLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_fault is not null)
            throw new InvalidOperationException("KV-LSM engine is faulted after a write or flush failure; reopen the instance.", _fault);
    }

    private sealed record ReadState(KvMemTable Mutable, KvMemTable[] Immutables, KvVersion Version);
}

/// <summary>
/// KV-LSM 一致快照：固定 sequence、TTL 判断时刻、memtable 集合与一个版本引用。获取为 O(1)，不复制数据。
/// </summary>
/// <remarks>释放快照后已创建的迭代器仍可继续使用（迭代器持有独立的版本引用）。</remarks>
internal sealed class KvLsmSnapshot : IDisposable
{
    private readonly KvLsmEngine _engine;
    private readonly KvMemTable _mutable;
    private readonly KvMemTable[] _immutables;
    private readonly KvVersion _version;
    private int _released;

    internal KvLsmSnapshot(KvLsmEngine engine, long sequence, long readTimestampUtcTicks, KvMemTable mutable, KvMemTable[] immutables, KvVersion version)
    {
        _engine = engine;
        Sequence = sequence;
        ReadTimestampUtcTicks = readTimestampUtcTicks;
        _mutable = mutable;
        _immutables = immutables;
        _version = version;
    }

    /// <summary>可见的最大 sequence。</summary>
    internal long Sequence { get; }

    /// <summary>TTL 判断时刻（UTC ticks）。</summary>
    internal long ReadTimestampUtcTicks { get; }

    /// <summary>点查：按 memtable（新→旧）、L0（新→旧）、L1+ 的顺序取第一个可见版本。</summary>
    internal KvEntry? Get(ReadOnlySpan<byte> key, CancellationToken cancellationToken = default)
    {
        KvSstEntry? raw = GetRaw(key, cancellationToken);
        return raw is { } entry ? ToEntry(entry) : null;
    }

    /// <summary>
    /// 点查快照中 <paramref name="key"/> 的最新版本，不按 TTL 过滤：未找到或被墓碑/range tombstone 遮蔽时返回 null，
    /// 否则返回该 Put 条目（可能已到期，由调用方按自己的时刻判断）。
    /// </summary>
    internal KvSstEntry? GetRaw(ReadOnlySpan<byte> key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
        // 从新到旧检查数据源；每个数据源先累计覆盖该 key 的 range tombstone 最大 sequence，
        // 首个找到的点版本若早于该 sequence 则视为已删除（较旧数据源的 tombstone 不会比较新数据源的条目更新）。
        long covering = 0;
        KvSstEntry entry;
        foreach (KvMemTable table in Enumerable.Prepend(_immutables, _mutable))
        {
            covering = Math.Max(covering, KvRangeTombstone.MaxCovering(table.RangeTombstones, key, Sequence));
            if (table.TryGet(key, Sequence, out entry))
                return Resolve(entry, covering);
        }

        foreach (KvLsmFileMetadata file in _version.Files(0))
        {
            if (!Covers(file, key))
                continue;
            KvSstReader reader = _engine.GetReader(file.FileNumber, cancellationToken);
            covering = Math.Max(covering, KvRangeTombstone.MaxCovering(reader.RangeTombstones, key, Sequence));
            if (reader.TryGet(key, Sequence, out entry, cancellationToken))
                return Resolve(entry, covering);
        }

        byte[] seekKey = KvInternalKey.EncodeSeekKey(key, Sequence);
        for (int level = 1; level < KvVersion.LevelCount; level++)
        {
            IReadOnlyList<KvLsmFileMetadata> files = _version.Files(level);
            int low = 0;
            int high = files.Count;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (KvInternalKey.Compare(files[middle].LargestKey, seekKey) < 0)
                    low = middle + 1;
                else
                    high = middle;
            }
            if (low >= files.Count || !Covers(files[low], key))
                continue;
            KvSstReader reader = _engine.GetReader(files[low].FileNumber, cancellationToken);
            covering = Math.Max(covering, KvRangeTombstone.MaxCovering(reader.RangeTombstones, key, Sequence));
            if (reader.TryGet(key, Sequence, out entry, cancellationToken))
                return Resolve(entry, covering);
        }
        return null;
    }

    private static KvSstEntry? Resolve(KvSstEntry entry, long coveringSequence)
        => entry.Sequence < coveringSequence || entry.Kind != KvEntryKind.Put ? null : entry;

    /// <summary>创建 <c>[lowerBound, upperBound)</c> 的双向用户视图迭代器（创建后需定位）。</summary>
    internal KvSnapshotIterator CreateIterator(byte[]? lowerBound = null, byte[]? upperBound = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
        var children = new List<IKvInternalIterator> { _mutable.CreateIterator() };
        var tombstones = new List<KvRangeTombstone>(_mutable.RangeTombstones);
        foreach (KvMemTable table in _immutables)
        {
            children.Add(table.CreateIterator());
            tombstones.AddRange(table.RangeTombstones);
        }
        KvVersion pinned = _version.AddReference();
        try
        {
            byte[]? storagePrefix = null;
            if (lowerBound is { Length: >= 8 } && upperBound is not null)
            {
                byte[] prefix = lowerBound[..8];
                byte[]? prefixEnd = KvLsmKeyspaceBackend.PrefixUpperBound(prefix);
                if (upperBound.AsSpan().StartsWith(prefix) || prefixEnd is not null && upperBound.AsSpan().SequenceEqual(prefixEnd))
                    storagePrefix = prefix;
            }
            bool Include(KvLsmFileMetadata file)
            {
                if (lowerBound is not null && KvInternalKey.UserKey(file.LargestKey).SequenceCompareTo(lowerBound) < 0
                    || upperBound is not null && KvInternalKey.UserKey(file.SmallestKey).SequenceCompareTo(upperBound) >= 0)
                    return false;
                return storagePrefix is null || _engine.GetReader(file.FileNumber, cancellationToken).MayContainStoragePrefix(storagePrefix);
            }
            var filesByLevel = new IReadOnlyList<KvLsmFileMetadata>[KvVersion.LevelCount];
            for (int level = 0; level < filesByLevel.Length; level++)
            {
                filesByLevel[level] = pinned.Files(level).Where(Include).ToArray();
                foreach (KvLsmFileMetadata file in filesByLevel[level])
                    tombstones.AddRange(_engine.GetReader(file.FileNumber, cancellationToken).RangeTombstones);
            }
            foreach (KvLsmFileMetadata file in filesByLevel[0])
                children.Add(_engine.GetReader(file.FileNumber, cancellationToken).CreateIterator(cancellationToken));
            for (int level = 1; level < KvVersion.LevelCount; level++)
            {
                IReadOnlyList<KvLsmFileMetadata> files = filesByLevel[level];
                if (files.Count > 0)
                {
                    children.Add(new KvLevelIterator(
                        files,
                        file => _engine.GetReader(file.FileNumber, cancellationToken).CreateIterator(cancellationToken)));
                }
            }
        }
        catch
        {
            pinned.Release();
            throw;
        }
        return new KvSnapshotIterator(
            new VersionPinnedIterator(new KvMergingIterator(children), pinned, _engine),
            Sequence, ReadTimestampUtcTicks, lowerBound, upperBound,
            KvRangeTombstoneSet.Build(tombstones, Sequence));
    }

    /// <summary>释放版本引用（幂等）；引擎已关闭时随之关闭不再需要的 SST 读取器。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _version.Release();
            _engine.ReleaseUnreferencedReaders();
        }
    }

    private KvEntry? ToEntry(KvSstEntry entry)
    {
        if (entry.Kind != KvEntryKind.Put
            || (entry.ExpiresAtUtcTicks != 0 && entry.ExpiresAtUtcTicks <= ReadTimestampUtcTicks))
        {
            return null;
        }
        return new KvEntry(
            KvInternalKey.UserKey(entry.InternalKey).ToArray(),
            entry.Value.ToArray(),
            entry.Sequence,
            entry.ExpiresAtUtcTicks == 0 ? null : new DateTimeOffset(entry.ExpiresAtUtcTicks, TimeSpan.Zero));
    }

    private static bool Covers(KvLsmFileMetadata file, ReadOnlySpan<byte> key)
        => key.SequenceCompareTo(KvInternalKey.UserKey(file.SmallestKey)) >= 0
            && key.SequenceCompareTo(KvInternalKey.UserKey(file.LargestKey)) <= 0;

    private sealed class VersionPinnedIterator(IKvInternalIterator inner, KvVersion version, KvLsmEngine engine) : IKvInternalIterator
    {
        private int _released;

        public bool Valid => inner.Valid;

        public ReadOnlySpan<byte> Key => inner.Key;

        public ReadOnlySpan<byte> Value => inner.Value;

        public long ExpiresAtUtcTicks => inner.ExpiresAtUtcTicks;

        public void SeekToFirst() => inner.SeekToFirst();

        public void SeekToLast() => inner.SeekToLast();

        public void Seek(ReadOnlySpan<byte> target) => inner.Seek(target);

        public void SeekForPrev(ReadOnlySpan<byte> target) => inner.SeekForPrev(target);

        public void MoveNext() => inner.MoveNext();

        public void MovePrev() => inner.MovePrev();

        public void Dispose()
        {
            inner.Dispose();
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                version.Release();
                engine.ReleaseUnreferencedReaders();
            }
        }
    }
}
