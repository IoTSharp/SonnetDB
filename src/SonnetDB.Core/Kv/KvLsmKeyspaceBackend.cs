using System.Diagnostics;
using System.Text;
using SonnetDB.Exceptions;
using SonnetDB.Kv.Lsm;

namespace SonnetDB.Kv;

/// <summary>
/// 以 <see cref="KvLsmEngine"/> 作为存储的 <see cref="KvKeyspace"/> 后端（K4b），可以独占一个引擎，
/// 也可以是共享 <see cref="KvLsmStore"/> 中按 <c>StorageId</c> 前缀划分的逻辑 keyspace（K6）。
/// </summary>
/// <remarks>
/// <para>
/// 语义合同（与 legacy 引擎的差异经用户确认，记录于 docs/design/kv-lsm-storage-engine.md 第 7.5 节）：
/// </para>
/// <list type="bullet">
///   <item><description>读取不写入：普通读取碰到已过期的 key 只把它视为不存在，不再写惰性删除，也不推高 <c>LastSequence</c>。</description></item>
///   <item><description>统一 fail closed：任何 WAL 追加、同步或后台 flush/compaction 失败都会让 keyspace 进入只读，
///   之后写入抛出 <see cref="IOException"/>；<see cref="KvKeyspace.IsWriteCommitOutcomeUnknown"/> 对首个故障异常返回 true。</description></item>
///   <item><description>一次 <c>Clear</c> 推进 generation 并消耗一个 sequence（与 legacy 一致）。</description></item>
/// </list>
/// <para>
/// 原子读取-判断-提交的操作（Set NX/XX、CAS、GetAndSet、条件批、DeleteMany 等）都在引擎写锁内完成：
/// 读取用 O(1) 快照，提交用 <see cref="KvLsmEngine.ApplyUnderWriteLock"/>，期间不会插入其他提交。
/// </para>
/// <para>
/// 共享模式下，所有 key 在进入引擎前加上 8 字节前缀、读出时去掉前缀；每次提交在同一批次中写入本逻辑 keyspace 的
/// sequence 元数据，因此 <see cref="LastSequence"/> 只随本 keyspace 的提交变化。<c>Clear</c> 写入覆盖本前缀的 range tombstone，
/// flush/compaction/WAL 同步作用于整个共享实例。
/// </para>
/// </remarks>
internal sealed class KvLsmKeyspaceBackend : IDisposable
{
    /// <summary>LSM 布局标记文件（CURRENT 由版本集管理）。</summary>
    internal const string LsmMarkerFileName = KvLsmPaths.CurrentFileName;

    private readonly KvLsmEngine _engine;
    private readonly KvOptions _options;
    private readonly KvDiskReadBudget _readBudget;
    private readonly KvLsmStore? _store;
    private readonly long _storageId;
    private readonly byte[] _keyPrefix;
    private readonly byte[]? _keyPrefixUpper;
    private byte[]? _expirationMaintenanceCursor;
    private int _indexRebuildScopeDepth;

    private KvLsmKeyspaceBackend(KvLsmEngine engine, KvOptions options, KvDiskReadBudget readBudget, KvLsmStore? store, long storageId)
    {
        _engine = engine;
        _options = options;
        _readBudget = readBudget;
        _store = store;
        _storageId = storageId;
        _keyPrefix = store is null ? [] : KvLsmStore.Prefix(storageId);
        _keyPrefixUpper = store is null ? null : PrefixUpperBound(_keyPrefix);
    }

    /// <summary>底层引擎（诊断与测试用）。</summary>
    internal KvLsmEngine Engine => _engine;

    /// <summary>是否是共享实例中的逻辑 keyspace。</summary>
    internal bool IsShared => _store is not null;

    /// <summary>共享实例的身份；独立实例为空。</summary>
    internal KvLsmStore? Store => _store;

    /// <summary>共享实例中的编号；独立实例为 0。</summary>
    internal long StorageId => _storageId;

    /// <summary>当前最后 sequence（共享模式下为本逻辑 keyspace 最后一次提交的 sequence）。</summary>
    internal long LastSequence => _store is null ? _engine.LastSequence : _store.GetLastSequence(_storageId);

    /// <summary>当前 generation。</summary>
    internal long Generation => _store is null ? _engine.Generation : _store.GetGeneration(_storageId);

    /// <summary>活跃 WAL 字节数。</summary>
    internal long ActiveWalLength => _engine.ActiveWalLength;

    /// <summary>活跃 WAL 文件路径。</summary>
    internal string ActiveWalPath => _engine.ActiveWalPath;

    /// <summary>
    /// 判断 <paramref name="rootDirectory"/> 中是否已经存在 legacy 布局的数据（状态文件、WAL 或 generation 元数据）。
    /// 存在时即使选择了 LSM 引擎也继续用 legacy 打开（决策：K7 之前不自动迁移）。v3 LSM 布局标记不算 legacy 数据。
    /// </summary>
    internal static bool HasLegacyData(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
            return false;
        if (File.Exists(Path.Combine(rootDirectory, KvGenerationFile.FileName)) && !KvLsmLayoutMarkerFile.TryRead(rootDirectory, out _))
            return true;
        if (File.Exists(KvKeyspace.ActiveWalPath(rootDirectory)))
            return true;
        foreach (string directory in new[] { KvKeyspace.SnapshotsDirectory(rootDirectory), KvKeyspace.SegmentsDirectory(rootDirectory) })
        {
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                return true;
        }
        string wal = KvKeyspace.WalDirectory(rootDirectory);
        return Directory.Exists(wal)
            && Directory.EnumerateFiles(wal).Any(static path => Path.GetFileName(path).StartsWith("sealed-", StringComparison.Ordinal));
    }

    /// <summary>判断目录是否已经是独立 LSM 布局（有 CURRENT，或有独立布局标记）。</summary>
    internal static bool IsLsmLayout(string rootDirectory)
        => File.Exists(Path.Combine(rootDirectory, LsmMarkerFileName))
            || (KvLsmLayoutMarkerFile.TryRead(rootDirectory, out KvLsmLayoutMarker marker) && marker.Kind == KvLsmLayoutKind.Standalone);

    /// <summary>由 <see cref="KvOptions"/> 生成引擎选项（独立实例与共享实例共用）。</summary>
    internal static KvLsmEngineOptions CreateEngineOptions(KvOptions options, KvDiskReadBudget readBudget)
        => new()
        {
            SyncWalOnEveryWrite = options.SyncWalOnEveryWrite,
            WalBufferSize = options.WalBufferSize,
            WriteBackpressureTimeout = options.CheckpointWriteBackpressureTimeout > TimeSpan.Zero
                ? options.CheckpointWriteBackpressureTimeout
                : TimeSpan.FromSeconds(30),
            ShutdownTimeout = options.CheckpointShutdownTimeout > TimeSpan.Zero
                ? options.CheckpointShutdownTimeout
                : TimeSpan.FromSeconds(5),
            ReadBudget = readBudget,
            // 进程级共享：块缓存、写缓冲预算与后台调度都不随 keyspace 数量线性增长（K5c）。
            BlockCache = KvBlockCache.Shared,
            WriteBufferManager = KvWriteBufferManager.Shared,
            Scheduler = KvLsmBackgroundScheduler.Shared,
            CompactionPauseReason = new KvLsmCompactionThrottle(options).GetReason,
        };

    /// <summary>打开（或创建）独立 LSM 引擎，并确保目录带有独立布局标记（降级防线）。调用方已持有目录生命周期租约。</summary>
    internal static KvLsmKeyspaceBackend Open(string rootDirectory, KvOptions options, KvDiskReadBudget readBudget)
    {
        KvLsmEngine engine = KvLsmEngine.Open(rootDirectory, CreateEngineOptions(options, readBudget));
        try
        {
            if (!KvLsmLayoutMarkerFile.TryRead(rootDirectory, out _))
                KvLsmLayoutMarkerFile.Write(rootDirectory, new KvLsmLayoutMarker(KvLsmLayoutKind.Standalone, 0));
        }
        catch
        {
            engine.Dispose();
            throw;
        }
        return new KvLsmKeyspaceBackend(engine, options, readBudget, store: null, storageId: 0);
    }

    /// <summary>打开共享实例中的逻辑 keyspace（不拥有引擎）。</summary>
    /// <exception cref="KeyNotFoundException">编号未在共享实例中登记。</exception>
    internal static KvLsmKeyspaceBackend OpenShared(KvLsmStore store, long storageId, KvOptions options, KvDiskReadBudget readBudget)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!store.Contains(storageId))
            throw new KeyNotFoundException($"KV-LSM shared store '{store.RootDirectory}' has no logical keyspace #{storageId}.");
        return new KvLsmKeyspaceBackend(store.Engine, options, readBudget, store, storageId);
    }

    /// <summary>首个让引擎拒绝写入的异常。</summary>
    internal Exception? Fault => _engine.Fault;

    // ───────────────────────────── key 映射 ─────────────────────────────

    private byte[] Physical(ReadOnlySpan<byte> key)
    {
        byte[] physical = new byte[_keyPrefix.Length + key.Length];
        _keyPrefix.CopyTo(physical, 0);
        key.CopyTo(physical.AsSpan(_keyPrefix.Length));
        return physical;
    }

    private byte[]? PhysicalOptional(byte[]? key) => key is null ? null : Physical(key);

    // ───────────────────────────── 读取 ─────────────────────────────

    /// <summary>当前时刻可见的条目（不写惰性删除）。</summary>
    internal KvValueEntry? GetVisible(ReadOnlySpan<byte> key, DateTimeOffset utcNow, CancellationToken cancellationToken)
    {
        using KvLsmSnapshot snapshot = _engine.AcquireSnapshot(utcNow);
        return ToVisible(snapshot.GetRaw(_keyPrefix.Length == 0 ? key : Physical(key), cancellationToken), utcNow);
    }

    /// <summary>当前最新的 Put 版本（含已到期）；不存在或已删除返回 null。</summary>
    internal KvValueEntry? GetRaw(ReadOnlySpan<byte> key, CancellationToken cancellationToken)
    {
        using KvLsmSnapshot snapshot = _engine.AcquireSnapshot(DateTimeOffset.MinValue.ToUniversalTime());
        return ToValueEntry(snapshot.GetRaw(_keyPrefix.Length == 0 ? key : Physical(key), cancellationToken));
    }

    internal T? ReadProjectedValue<T>(ReadOnlySpan<byte> key, KvValueProjection<T> project) where T : class
    {
        using KvLsmSnapshot snapshot = _engine.AcquireSnapshot(DateTimeOffset.UtcNow);
        KvSstEntry? raw = snapshot.GetRaw(_keyPrefix.Length == 0 ? key : Physical(key));
        return raw is { } entry && (entry.ExpiresAtUtcTicks == 0 || entry.ExpiresAtUtcTicks > snapshot.ReadTimestampUtcTicks)
            ? project(entry.Value.Span) : null;
    }

    /// <summary>
    /// 有界点查：先检查 value 长度，超过 <paramref name="maxValueBytes"/> 时抛出
    /// <see cref="PayloadLimitExceededException"/>，不复制 value。
    /// </summary>
    /// <remarks>
    /// memtable 与已缓存的 SST 块中长度检查不分配 value；冷 SST 块仍需整块读取（v1 SST 的 value 内联在数据块中），
    /// 这是相对 legacy“读取前拒绝”的已知差异，记录在设计文档第 7.5 节。
    /// </remarks>
    internal KvEntry? GetVisibleBounded(byte[] key, int maxValueBytes, DateTimeOffset utcNow, CancellationToken cancellationToken)
    {
        using KvLsmSnapshot snapshot = _engine.AcquireSnapshot(DateTimeOffset.MinValue.ToUniversalTime());
        KvSstEntry? raw = snapshot.GetRaw(_keyPrefix.Length == 0 ? key : Physical(key), cancellationToken);
        if (raw is not { } entry || (entry.ExpiresAtUtcTicks != 0 && entry.ExpiresAtUtcTicks <= utcNow.UtcTicks))
            return null;
        if (entry.Value.Length > maxValueBytes)
            throw new PayloadLimitExceededException(maxValueBytes);
        return new KvEntry(
            key.ToArray(),
            entry.Value.ToArray(),
            entry.Sequence,
            entry.ExpiresAtUtcTicks == 0 ? null : new DateTimeOffset(entry.ExpiresAtUtcTicks, TimeSpan.Zero));
    }

    /// <summary>
    /// 按 key 升序访问 <paramref name="prefix"/> 下 <paramref name="afterKey"/> 之后的全部 Put 版本（含已到期），
    /// 回调期间 <see cref="KvLsmCursor.Key"/>/<see cref="KvLsmCursor.Value"/> 有效且未复制，
    /// 调用方可先按长度计费再复制。回调返回 false 时停止。
    /// </summary>
    internal void ScanForward(byte[] prefix, byte[]? afterKey, CancellationToken cancellationToken, Func<KvLsmCursor, bool> visit)
    {
        byte[] physicalPrefix = Physical(prefix);
        byte[]? physicalAfter = PhysicalOptional(afterKey);
        using KvLsmSnapshot snapshot = _engine.AcquireSnapshot(DateTimeOffset.MinValue.ToUniversalTime());
        using KvSnapshotIterator iterator = snapshot.CreateIterator(
            physicalPrefix.Length == 0 ? null : physicalPrefix,
            PrefixUpperBound(physicalPrefix),
            cancellationToken);
        if (physicalAfter is not null && KvKeyComparer.Instance.Compare(physicalAfter, physicalPrefix) >= 0)
        {
            iterator.Seek(physicalAfter);
            if (iterator.Valid && iterator.Key.SequenceEqual(physicalAfter))
                iterator.MoveNext();
        }
        else
        {
            iterator.SeekToFirst();
        }

        var cursor = new KvLsmCursor(iterator, _keyPrefix.Length);
        for (; iterator.Valid; iterator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!iterator.Key.StartsWith(physicalPrefix) || !visit(cursor))
                return;
        }
    }

    /// <summary>
    /// 取得引擎快照并包装为 <see cref="KvReadSnapshot"/>。快照持有读取预算的 state 引用，
    /// 使数据库关闭后仍可完成读取（与 legacy 磁盘租约合同一致），最后一个引用释放时归还。
    /// </summary>
    internal KvReadSnapshot AcquireReadSnapshot()
    {
        KvLsmSnapshot snapshot = _engine.AcquireSnapshot(DateTimeOffset.UtcNow);
        try
        {
            _readBudget.AddStateReference();
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
        return new KvReadSnapshot(new KvReadSnapshotState(snapshot, _keyPrefix, _readBudget.ReleaseStateReference));
    }

    /// <summary>
    /// 枚举当前所有 Put 版本（包括已到期的条目，不包括已删除的），按 key 升序；调用方自行判断 TTL。
    /// </summary>
    internal IEnumerable<KeyValuePair<byte[], KvValueEntry>> EnumerateRaw(
        byte[] prefix,
        byte[]? startInclusive,
        byte[]? endExclusive,
        byte[]? afterKey,
        CancellationToken cancellationToken)
    {
        // 读取时刻取最小值，使到期条目也可见；物理回收由 compaction 与 CleanExpired 负责。
        KvLsmSnapshot snapshot = _engine.AcquireSnapshot(DateTimeOffset.MinValue.ToUniversalTime());
        return EnumerateCore(snapshot, _keyPrefix, prefix, startInclusive, endExclusive, afterKey, descending: false, () => cancellationToken, ownsSnapshot: true);
    }

    /// <summary>以 <see cref="KvReadSnapshot"/> 的语义枚举一个快照（供 <see cref="KvReadSnapshotState"/> 使用）。</summary>
    /// <param name="snapshot">引擎快照。</param>
    /// <param name="keyPrefix">逻辑 keyspace 的物理前缀；独立实例为空。</param>
    /// <param name="prefix">用户 key 前缀。</param>
    /// <param name="startInclusive">用户 key 下界。</param>
    /// <param name="endExclusive">用户 key 上界。</param>
    /// <param name="afterKey">排他续扫位置。</param>
    /// <param name="descending">是否降序。</param>
    /// <param name="cancellationTokenProvider">取消令牌来源。</param>
    internal static IEnumerable<KeyValuePair<byte[], KvValueEntry>> EnumerateSnapshot(
        KvLsmSnapshot snapshot,
        byte[] keyPrefix,
        byte[] prefix,
        byte[]? startInclusive,
        byte[]? endExclusive,
        byte[]? afterKey,
        bool descending,
        Func<CancellationToken> cancellationTokenProvider)
        => EnumerateCore(snapshot, keyPrefix, prefix, startInclusive, endExclusive, afterKey, descending, cancellationTokenProvider, ownsSnapshot: false);

    private static byte[] Concat(byte[] keyPrefix, byte[] key)
        => keyPrefix.Length == 0 ? key : [.. keyPrefix, .. key];

    private static IEnumerable<KeyValuePair<byte[], KvValueEntry>> EnumerateCore(
        KvLsmSnapshot snapshot,
        byte[] keyPrefix,
        byte[] userPrefix,
        byte[]? userStart,
        byte[]? userEnd,
        byte[]? userAfter,
        bool descending,
        Func<CancellationToken> cancellationTokenProvider,
        bool ownsSnapshot)
    {
        try
        {
            byte[] prefix = Concat(keyPrefix, userPrefix);
            byte[]? startInclusive = userStart is null ? null : Concat(keyPrefix, userStart);
            byte[]? endExclusive = userEnd is null ? null : Concat(keyPrefix, userEnd);
            byte[]? afterKey = userAfter is null ? null : Concat(keyPrefix, userAfter);
            int skip = keyPrefix.Length;

            // 下界 = max(prefix, start)；上界 = min(PrefixUpper(prefix), end)。afterKey 为排他边界。
            byte[] lower = prefix;
            if (startInclusive is not null && KvKeyComparer.Instance.Compare(startInclusive, lower) > 0)
                lower = startInclusive;
            byte[]? upper = PrefixUpperBound(prefix);
            if (endExclusive is not null && (upper is null || KvKeyComparer.Instance.Compare(endExclusive, upper) < 0))
                upper = endExclusive;
            if (upper is not null && KvKeyComparer.Instance.Compare(lower, upper) >= 0)
                yield break;

            using KvSnapshotIterator iterator = snapshot.CreateIterator(
                lower.Length == 0 ? null : lower,
                upper,
                cancellationTokenProvider());
            if (!descending)
            {
                if (afterKey is not null && KvKeyComparer.Instance.Compare(afterKey, lower) >= 0)
                {
                    iterator.Seek(afterKey);
                    if (iterator.Valid && iterator.Key.SequenceEqual(afterKey))
                        iterator.MoveNext();
                }
                else
                {
                    iterator.SeekToFirst();
                }

                for (; iterator.Valid; iterator.MoveNext())
                {
                    cancellationTokenProvider().ThrowIfCancellationRequested();
                    if (!iterator.Key.StartsWith(prefix))
                        yield break;
                    yield return Materialize(iterator, skip);
                }
            }
            else
            {
                if (afterKey is not null && (upper is null || KvKeyComparer.Instance.Compare(afterKey, upper) < 0))
                {
                    iterator.SeekForPrev(afterKey);
                    if (iterator.Valid && iterator.Key.SequenceEqual(afterKey))
                        iterator.MovePrev();
                }
                else
                {
                    iterator.SeekToLast();
                }

                for (; iterator.Valid; iterator.MovePrev())
                {
                    cancellationTokenProvider().ThrowIfCancellationRequested();
                    if (KvKeyComparer.Instance.Compare(iterator.Key.ToArray(), lower) < 0 || !iterator.Key.StartsWith(prefix))
                        yield break;
                    yield return Materialize(iterator, skip);
                }
            }
        }
        finally
        {
            if (ownsSnapshot)
                snapshot.Dispose();
        }
    }

    private static KeyValuePair<byte[], KvValueEntry> Materialize(KvSnapshotIterator iterator, int skip)
        => new(
            iterator.Key[skip..].ToArray(),
            new KvValueEntry(
                iterator.Value.ToArray(),
                iterator.Sequence,
                iterator.ExpiresAtUtcTicks == 0 ? null : new DateTimeOffset(iterator.ExpiresAtUtcTicks, TimeSpan.Zero)));

    /// <summary>覆盖指定前缀的最小排他上界；空前缀或全 0xFF 前缀没有有限上界。</summary>
    internal static byte[]? PrefixUpperBound(ReadOnlySpan<byte> prefix)
    {
        if (prefix.IsEmpty)
            return null;
        byte[] upper = prefix.ToArray();
        for (int i = upper.Length - 1; i >= 0; i--)
        {
            if (upper[i] == byte.MaxValue)
                continue;
            upper[i]++;
            return upper[..(i + 1)];
        }
        return null;
    }

    // ───────────────────────────── 写入 ─────────────────────────────

    /// <summary>
    /// 在引擎写锁内执行读取-判断-提交；写锁等待遵守 legacy 合同：带可取消令牌或 <paramref name="bounded"/> 时
    /// 以 <see cref="KvOptions.CheckpointWriteBackpressureTimeout"/> 为上限。
    /// </summary>
    internal T Execute<T>(Func<T> action, CancellationToken cancellationToken, bool bounded = false)
    {
        TimeSpan? timeout = cancellationToken.CanBeCanceled || bounded
            ? (_options.CheckpointWriteBackpressureTimeout > TimeSpan.Zero ? _options.CheckpointWriteBackpressureTimeout : TimeSpan.FromSeconds(30))
            : null;
        try
        {
            return _engine.ExecuteUnderWriteLock(action, cancellationToken, timeout);
        }
        catch (InvalidOperationException exception) when (exception.InnerException is { } fault && ReferenceEquals(fault, _engine.Fault))
        {
            throw ReadOnly(fault);
        }
    }

    /// <summary>
    /// 在写锁内提交一批变更（调用方必须处于 <see cref="Execute"/> 回调中）。最后取消点在 WAL 追加之前。
    /// </summary>
    internal long CommitLocked(
        IReadOnlyList<KvBatchMutation> mutations,
        IReadOnlyList<KvRangeDeletion> rangeDeletions,
        long? expectedSequence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // 提交开始后不再观察取消：传入 None，避免背压等待之后的取消把已提交的结果误报为取消。
            if (_store is null)
                return _engine.ApplyUnderWriteLock(mutations, rangeDeletions, expectedSequence, CancellationToken.None);

            // 共享模式：写锁内下一 sequence 稳定；本 keyspace 的 sequence 元数据与数据同批提交。
            long next = _engine.NextSequence;
            var physical = new List<KvBatchMutation>(mutations.Count + 1);
            foreach (KvBatchMutation mutation in mutations)
                physical.Add(new KvBatchMutation(Physical(mutation.Key), mutation.Value, mutation.ExpiresAtUtc));
            if (_store.RowCountMutation(_storageId, next, mutations) is { } count)
                physical.Add(count);
            physical.Add(KvLsmStore.SequenceMutation(_storageId, next));
            var ranges = new List<KvRangeDeletion>(rangeDeletions.Count);
            foreach (KvRangeDeletion range in rangeDeletions)
                ranges.Add(new KvRangeDeletion(Physical(range.StartInclusive), Physical(range.EndExclusive)));
            long sequence = _engine.ApplyUnderWriteLock(physical, ranges, expectedSequence ?? next, CancellationToken.None);
            _store.OnCommitted(_storageId, sequence);
            return sequence;
        }
        catch (InvalidOperationException exception) when (exception.InnerException is { } fault && ReferenceEquals(fault, _engine.Fault))
        {
            throw ReadOnly(fault);
        }
    }

    /// <summary>在写锁内读取 <paramref name="key"/> 的最新 Put 版本（含已到期）。</summary>
    internal KvValueEntry? GetRawLocked(ReadOnlySpan<byte> key, CancellationToken cancellationToken)
        => GetRaw(key, cancellationToken);

    /// <summary>在写锁内判断 <paramref name="key"/> 当前是否存在（含已到期）。</summary>
    internal bool ExistsRawLocked(ReadOnlySpan<byte> key) => GetRaw(key, CancellationToken.None) is not null;

    /// <summary>评估条件批的前置条件；返回第一个失败下标，全部成立返回 -1。</summary>
    internal int FindFailedPreconditionLocked(IReadOnlyList<KvBatchPrecondition> conditions, DateTimeOffset utcNow)
    {
        for (int i = 0; i < conditions.Count; i++)
        {
            KvBatchPrecondition condition = conditions[i];
            switch (condition.Kind)
            {
                case KvBatchPreconditionKind.KeyVersionEquals:
                    {
                        KvValueEntry? entry = GetRaw(condition.Operand, CancellationToken.None);
                        long version = entry is not null && !entry.IsExpired(utcNow) ? entry.Version : 0;
                        if (version != condition.ExpectedVersion)
                            return i;
                        break;
                    }
                case KvBatchPreconditionKind.KeyExists:
                    {
                        KvValueEntry? entry = GetRaw(condition.Operand, CancellationToken.None);
                        if (entry is null || entry.IsExpired(utcNow))
                            return i;
                        break;
                    }
                default:
                    {
                        foreach (KeyValuePair<byte[], KvValueEntry> pair in EnumerateRaw(condition.Operand, null, null, null, CancellationToken.None))
                        {
                            if (!pair.Value.IsExpired(utcNow))
                                return i;
                        }
                        break;
                    }
            }
        }
        return -1;
    }

    /// <summary>把 LSM 故障映射为 legacy 的只读 <see cref="IOException"/>。</summary>
    internal static IOException ReadOnly(Exception fault)
        => new("KV keyspace is read-only after a WAL or committed maintenance failure; reopen it before writing.", fault);

    // ───────────────────────────── 维护 ─────────────────────────────

    /// <summary>刷盘全部 memtable 并返回刷盘时的 <c>LastSequence</c>（共享模式下刷整个共享实例）。</summary>
    internal long CreateSnapshot()
    {
        long sequence = LastSequence;
        RunMaintenance(() => _engine.Flush());
        return Math.Max(sequence, LastSequence);
    }

    /// <summary>刷盘并把全部 SST 合并到最底层，返回完成时的 <c>LastSequence</c>（共享模式下作用于整个共享实例）。</summary>
    internal long Compact()
    {
        long sequence = LastSequence;
        RunMaintenance(() => _engine.CompactAll());
        return Math.Max(sequence, LastSequence);
    }

    /// <summary>fsync 活跃 WAL。</summary>
    internal void SyncWal(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunMaintenance(_engine.SyncWal);
    }

    /// <summary>
    /// 清空 keyspace 并推进 generation，消耗一个 sequence（与 legacy 一致）。独立实例切换引擎 generation 并丢弃全部文件；
    /// 共享实例在同一批次中写入覆盖本前缀的 range tombstone 与 generation + 1 的元数据。
    /// </summary>
    internal KvClearResult Clear()
    {
        int removed = 0;
        ScanForward([], null, CancellationToken.None, _ =>
        {
            removed++;
            return true;
        });

        if (_store is null)
        {
            RunMaintenance(() => _engine.Clear());
            return new KvClearResult(removed, _engine.Generation, 0);
        }

        long generation = Execute(() =>
        {
            _engine.WaitForWriteBudgetUnderLock(CancellationToken.None);
            long next = _engine.NextSequence;
            KvBatchMutation record = _store.ClearRecordMutation(_storageId, out long nextGeneration);
            byte[] maintenance = KvLsmStore.MaintenancePrefix(_storageId);
            // Metadata is outside the table's data prefix. Delete old generation markers in
            // the same commit; the new row count uses the same sequence as the tombstone.
            bool counted = _store.ReadRowCount(_storageId).HasValue;
            var metadata = new List<KvBatchMutation> { record, KvLsmStore.SequenceMutation(_storageId, next) };
            if (counted)
                metadata.Add(_store.EmptyRowCountMutation(_storageId, nextGeneration, next));
            long sequence = _engine.ApplyUnderWriteLock(
                metadata,
                [new KvRangeDeletion(_keyPrefix, _keyPrefixUpper!),
                    new KvRangeDeletion(maintenance, KvLsmKeyspaceBackend.PrefixUpperBound(maintenance)!)],
                next,
                CancellationToken.None);
            _store.OnCleared(_storageId, nextGeneration, sequence);
            return nextGeneration;
        }, CancellationToken.None);
        return new KvClearResult(removed, generation, 0);
    }

    /// <summary>进入索引恢复预算 scope（LSM 没有覆盖层预算，仅维护深度计数）。</summary>
    internal IDisposable EnterIndexRebuildScope()
    {
        Interlocked.Increment(ref _indexRebuildScopeDepth);
        return new Scope(this);
    }

    /// <summary>当前是否处于索引恢复预算 scope 内（决定单批次 WAL 上限使用哪个选项）。</summary>
    internal bool IndexRebuildScopeActive => Volatile.Read(ref _indexRebuildScopeDepth) > 0;

    /// <summary>后台过期清理的续扫游标。</summary>
    internal byte[]? ExpirationMaintenanceCursor
    {
        get => _expirationMaintenanceCursor;
        set => _expirationMaintenanceCursor = value;
    }

    /// <summary>生成诊断信息中与存储相关的计数（共享模式下为整个共享实例）。</summary>
    internal (int Mutable, int Frozen, long WalBytes) DiagnosticsCounts()
    {
        (long mutable, long immutable) = _engine.MemTableEntryCounts;
        return ((int)Math.Min(int.MaxValue, mutable), (int)Math.Min(int.MaxValue, immutable), _engine.ActiveWalLength);
    }

    /// <summary>关闭独立引擎（WAL 关闭失败按原类型抛出，legacy 合同）；共享模式下不关闭共享实例。</summary>
    public void Dispose()
    {
        if (_store is null)
            _engine.Dispose();
    }

    private void RunMaintenance(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException exception) when (exception.InnerException is { } fault && ReferenceEquals(fault, _engine.Fault))
        {
            throw ReadOnly(fault);
        }
    }

    internal static KvValueEntry? ToValueEntry(KvSstEntry? raw)
        => raw is { } entry
            ? new KvValueEntry(
                entry.Value.ToArray(),
                entry.Sequence,
                entry.ExpiresAtUtcTicks == 0 ? null : new DateTimeOffset(entry.ExpiresAtUtcTicks, TimeSpan.Zero))
            : null;

    private static KvValueEntry? ToVisible(KvSstEntry? raw, DateTimeOffset utcNow)
    {
        KvValueEntry? entry = ToValueEntry(raw);
        return entry is null || entry.IsExpired(utcNow) ? null : entry;
    }

    /// <summary>INCR/DECR 的十进制解析（与 legacy 相同的错误合同）。</summary>
    internal static long ParseInteger(byte[] value)
    {
        string text = Encoding.UTF8.GetString(value);
        if (!long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long parsed))
            throw new InvalidOperationException("KV value 不是有效的 UTF-8 十进制整数，无法执行 INCR/DECR。");
        return parsed;
    }

    private sealed class Scope(KvLsmKeyspaceBackend owner) : IDisposable
    {
        private KvLsmKeyspaceBackend? _owner = owner;

        public void Dispose()
        {
            KvLsmKeyspaceBackend? current = Interlocked.Exchange(ref _owner, null);
            if (current is not null && Interlocked.Decrement(ref current._indexRebuildScopeDepth) < 0)
                throw new InvalidOperationException("KV index rebuild budget scope is not active.");
        }
    }
}

/// <summary>
/// <see cref="KvLsmKeyspaceBackend.ScanForward"/> 回调中的当前条目：key 已去掉逻辑 keyspace 前缀，
/// key/value 只在回调期间有效，需要保留时由调用方复制。
/// </summary>
internal sealed class KvLsmCursor
{
    private readonly KvSnapshotIterator _iterator;
    private readonly int _skip;

    internal KvLsmCursor(KvSnapshotIterator iterator, int skip)
    {
        _iterator = iterator;
        _skip = skip;
    }

    /// <summary>用户 key（不含逻辑 keyspace 前缀）。</summary>
    internal ReadOnlySpan<byte> Key => _iterator.Key[_skip..];

    /// <summary>value。</summary>
    internal ReadOnlySpan<byte> Value => _iterator.Value;

    /// <summary>到期时间（UTC ticks），0 表示无 TTL。</summary>
    internal long ExpiresAtUtcTicks => _iterator.ExpiresAtUtcTicks;

    /// <summary>条目 sequence。</summary>
    internal long Sequence => _iterator.Sequence;
}
