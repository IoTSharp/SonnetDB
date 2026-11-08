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
/// 点查、双向范围迭代、后台 flush、leveled compaction 与崩溃恢复。尚未接入 <see cref="KvKeyspace"/>（K4b）。
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
internal sealed class KvLsmEngine : IDisposable
{
    private readonly string _root;
    private readonly KvLsmEngineOptions _options;
    private readonly KvVersionSet _versions;
    private readonly object _writeLock = new();
    private readonly object _stateLock = new();
    private readonly object _tableCacheLock = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly SemaphoreSlim _flushSignal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<long, KvSstReader> _tableCache = [];
    private readonly List<(KvMemTable Table, long WalNumber)> _immutables = [];
    private readonly Task? _flushWorker;
    private readonly Task? _compactionWorker;
    private readonly SemaphoreSlim _compactionGate = new(1, 1);
    private readonly SemaphoreSlim _compactionSignal = new(0, int.MaxValue);
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
        if (options.BackgroundFlush)
        {
            _flushWorker = Task.Factory.StartNew(FlushLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _compactionWorker = Task.Factory.StartNew(CompactionLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _compactionSignal.Release();
        }
    }

    /// <summary>块缓存（诊断与测试用）。</summary>
    internal KvBlockCache BlockCache => _blockCache;

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
            if (recovered.EntryCount > 0)
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
    {
        ArgumentNullException.ThrowIfNull(mutations);
        KvBatchMutation[] batch = Canonicalize(mutations);

        lock (_writeLock)
        {
            ThrowIfUnwritableLocked();
            WaitForWriteBudgetLocked(cancellationToken);

            long sequence;
            try
            {
                sequence = _wal.AppendMutationBatch(batch);
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
            if (_mutable.UsedBytes >= _options.WriteBufferBytes)
                RotateLocked();
            return sequence;
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
            if (_mutable.EntryCount > 0)
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

    /// <summary>停止后台 flush 并关闭 WAL、SST 与 manifest；未刷盘数据由下次打开时重放 WAL 恢复。</summary>
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
        _flushSignal.Release();
        _compactionSignal.Release();
        try
        {
            _flushWorker?.Wait(TimeSpan.FromSeconds(30));
            _compactionWorker?.Wait(TimeSpan.FromSeconds(30));
        }
        catch (AggregateException)
        {
            // 后台错误已记录在 _fault；关闭流程继续释放资源。
        }

        lock (_writeLock)
        {
            try
            {
                _wal.Dispose();
            }
            catch (IOException)
            {
                // 未 fsync 的尾部由 WAL 撕裂规则处理。
            }
        }

        lock (_stateLock)
            _state.Version.Release();
        lock (_tableCacheLock)
        {
            foreach (KvSstReader reader in _tableCache.Values)
                reader.Dispose();
            _tableCache.Clear();
        }
        _versions.Dispose();
        _shutdown.Dispose();
    }

    internal KvSstReader GetReader(long fileNumber, CancellationToken cancellationToken)
    {
        lock (_tableCacheLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tableCache.TryGetValue(fileNumber, out KvSstReader? reader))
                return reader;
            reader = KvSstReader.Open(KvLsmPaths.SstPath(_root, fileNumber), _options.ReadBudget, cancellationToken, _blockCache);
            _tableCache[fileNumber] = reader;
            return reader;
        }
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
        _mutable = new KvMemTable();
        _wal = next;
        Volatile.Write(ref _walNumber, walNumber);
        PublishState(version: null);
        _flushSignal.Release();
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

    private void FlushLoop()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                _flushSignal.Wait(_shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                FlushPending(_shutdown.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // FlushPending 已记录 _fault 并唤醒等待者；后台线程退出，需重新打开实例。
                return;
            }
        }
    }

    private void FlushPending(CancellationToken cancellationToken)
    {
        _flushGate.Wait(cancellationToken);
        try
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
        finally
        {
            _flushGate.Release();
        }
    }

    private void FlushOne(KvMemTable table, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        KvLsmFileMetadata file = WriteLevel0(_versions, table, _options);
        _options.TestHook?.Invoke(KvLsmEnginePhase.FlushAfterSstWritten);

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
            PublishState(_versions.AcquireCurrent());
            Interlocked.Increment(ref _flushCount);
            Monitor.PulseAll(_writeLock);
        }
        if (_options.BackgroundFlush)
            _compactionSignal.Release();

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

    private void CompactionLoop()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                _compactionSignal.Wait(_shutdown.Token);
                MaybeCompact(_shutdown.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // RunCompactionJob 已记录 _fault 并唤醒等待者。
                return;
            }
        }
    }

    private void MaybeCompact(CancellationToken cancellationToken)
    {
        _compactionGate.Wait(cancellationToken);
        try
        {
            while (true)
            {
                lock (_writeLock)
                {
                    if (_fault is not null || _disposed)
                        return;
                }

                KvVersion version = _versions.AcquireCurrent();
                try
                {
                    KvCompactionJob? job = KvCompactionPicker.Pick(version, _options.Compaction, _compactPointers);
                    if (job is null)
                        return;
                    RunCompactionJob(job, version, cancellationToken);
                }
                finally
                {
                    version.Release();
                }
            }
        }
        finally
        {
            _compactionGate.Release();
        }
    }

    private void RunCompactionJob(KvCompactionJob job, KvVersion version, CancellationToken cancellationToken)
    {
        try
        {
            KvVersionEdit edit = KvCompactionExecutor.Run(
                job,
                version,
                _versions,
                file => GetReader(file.FileNumber, cancellationToken).CreateIterator(cancellationToken),
                _options.SstWriterOptions,
                _options.Compaction,
                _options.UtcNowTicks(),
                cancellationToken);
            _options.TestHook?.Invoke(KvLsmEnginePhase.CompactionAfterOutputsWritten);
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

    private static KvBatchMutation[] Canonicalize(IReadOnlyList<KvBatchMutation> mutations)
    {
        if (mutations.Count == 0)
            throw new ArgumentException("KV-LSM batch must contain at least one mutation.", nameof(mutations));
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
        if (_mutable.TryGet(key, Sequence, out KvSstEntry entry))
            return ToEntry(entry);
        foreach (KvMemTable table in _immutables)
        {
            if (table.TryGet(key, Sequence, out entry))
                return ToEntry(entry);
        }

        foreach (KvLsmFileMetadata file in _version.Files(0))
        {
            if (Covers(file, key) && _engine.GetReader(file.FileNumber, cancellationToken).TryGet(key, Sequence, out entry, cancellationToken))
                return ToEntry(entry);
        }

        for (int level = 1; level < KvVersion.LevelCount; level++)
        {
            IReadOnlyList<KvLsmFileMetadata> files = _version.Files(level);
            int low = 0;
            int high = files.Count;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (KvInternalKey.UserKey(files[middle].LargestKey).SequenceCompareTo(key) < 0)
                    low = middle + 1;
                else
                    high = middle;
            }
            if (low < files.Count
                && Covers(files[low], key)
                && _engine.GetReader(files[low].FileNumber, cancellationToken).TryGet(key, Sequence, out entry, cancellationToken))
            {
                return ToEntry(entry);
            }
        }
        return null;
    }

    /// <summary>创建 <c>[lowerBound, upperBound)</c> 的双向用户视图迭代器（创建后需定位）。</summary>
    internal KvSnapshotIterator CreateIterator(byte[]? lowerBound = null, byte[]? upperBound = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
        var children = new List<IKvInternalIterator> { _mutable.CreateIterator() };
        foreach (KvMemTable table in _immutables)
            children.Add(table.CreateIterator());
        KvVersion pinned = _version.AddReference();
        try
        {
            foreach (KvLsmFileMetadata file in pinned.Files(0))
                children.Add(_engine.GetReader(file.FileNumber, cancellationToken).CreateIterator(cancellationToken));
            for (int level = 1; level < KvVersion.LevelCount; level++)
            {
                IReadOnlyList<KvLsmFileMetadata> files = pinned.Files(level);
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
            new VersionPinnedIterator(new KvMergingIterator(children), pinned),
            Sequence, ReadTimestampUtcTicks, lowerBound, upperBound);
    }

    /// <summary>释放版本引用（幂等）。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _version.Release();
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

    private sealed class VersionPinnedIterator(IKvInternalIterator inner, KvVersion version) : IKvInternalIterator
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
                version.Release();
        }
    }
}
