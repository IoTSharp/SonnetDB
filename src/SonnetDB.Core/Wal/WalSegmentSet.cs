using SonnetDB.Catalog;
using SonnetDB.Model;
using SonnetDB.Storage.Format;

namespace SonnetDB.Wal;

/// <summary>
/// WAL segment 集合管理器：对外暴露"追加 / Sync / Roll / Replay / 按 CheckpointLsn 回收"等操作；
/// 内部维护当前 active <see cref="WalWriter"/> 与历史 segment 文件名列表。
/// 单写者；多读者（Replay / EnumerateSegments）线程安全。
/// </summary>
public sealed class WalSegmentSet : IDisposable
{
    private readonly object _sync = new();
    private readonly List<WalSegmentInfo> _segments;
    private readonly int _bufferSize;
    private readonly bool _atomicPoints;
    private readonly AtomicPointWalRecoveryOptions? _recoveryOptions;

    private WalWriter? _activeWriter;
    private long _activeRecordCount;
    private long _syncCount;
    private bool _disposed;
    private Exception? _writeFault;

    internal Action? AfterAtomicPointWriteTestHook { get; set; }

    internal void FlushUnacknowledgedPointBytesToOsForTest()
    {
        lock (_sync)
            _activeWriter!.FlushUnacknowledgedPointBytesToOsForTest();
    }

    internal bool IsWriteFaulted => _writeFault is not null || _activeWriter?.HasAppendFault == true;

    internal void ThrowIfWriteFaulted()
    {
        if (IsWriteFaulted)
            throw new InvalidOperationException("WAL write result is unknown; close, reopen and reconcile before writing or checkpointing.", _writeFault);
    }

    /// <summary>WAL 子目录路径。</summary>
    public string WalDirectory { get; }

    /// <summary>滚动策略。</summary>
    public WalRollingPolicy Policy { get; }

    /// <summary>当前 active segment 的起始 LSN。</summary>
    public long ActiveStartLsn
    {
        get
        {
            lock (_sync)
                return _segments.Count > 0 ? _segments[^1].StartLsn : 1L;
        }
    }

    /// <summary>当前 active segment 的文件路径。</summary>
    public string ActiveSegmentPath
    {
        get
        {
            lock (_sync)
                return _activeWriter?.Path ?? string.Empty;
        }
    }

    /// <summary>下一个将分配的 LSN。</summary>
    public long NextLsn
    {
        get
        {
            lock (_sync)
                return _activeWriter?.NextLsn ?? 1L;
        }
    }

    /// <summary>所有 segment 信息（含 active），按 StartLsn 升序排列的快照。</summary>
    public IReadOnlyList<WalSegmentInfo> Segments
    {
        get
        {
            lock (_sync)
                return CreateSegmentSnapshotLocked();
        }
    }

    internal long SyncCount
    {
        get
        {
            lock (_sync)
                return _syncCount;
        }
    }

    private WalSegmentSet(string walDirectory, WalRollingPolicy policy, int bufferSize, List<WalSegmentInfo> segments, WalWriter activeWriter,
        bool atomicPoints = false, AtomicPointWalRecoveryOptions? recoveryOptions = null)
    {
        WalDirectory = walDirectory;
        Policy = policy;
        _bufferSize = bufferSize;
        _segments = segments;
        _activeWriter = activeWriter;
        _activeRecordCount = activeWriter.NextLsn - segments[^1].StartLsn;
        _atomicPoints = atomicPoints;
        _recoveryOptions = recoveryOptions;
    }

    /// <summary>
    /// 打开（或创建）一个 WAL segment 集合。
    /// <list type="number">
    ///   <item><description>自动升级 legacy <c>active.SDBWAL</c>（若存在）；</description></item>
    ///   <item><description>枚举目录中全部合法 segment，按 startLsn 升序；</description></item>
    ///   <item><description>若目录为空，以 <paramref name="initialStartLsn"/> 创建第一个 segment；</description></item>
    ///   <item><description>否则选 startLsn 最大者为 active，用 WalWriter 续写。</description></item>
    /// </list>
    /// </summary>
    /// <param name="walDirectory">WAL 子目录路径（不存在时自动创建）。</param>
    /// <param name="policy">滚动策略；为 null 时使用 <see cref="WalRollingPolicy.Default"/>。</param>
    /// <param name="bufferSize">写缓冲区大小（字节），默认 64 KB。</param>
    /// <param name="initialStartLsn">首次创建时的起始 LSN（默认 1）。</param>
    /// <returns>已初始化的 <see cref="WalSegmentSet"/> 实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="walDirectory"/> 为 null 时抛出。</exception>
    public static WalSegmentSet Open(
        string walDirectory,
        WalRollingPolicy? policy = null,
        int bufferSize = 64 * 1024,
        long initialStartLsn = 1)
        => OpenCore(walDirectory, policy, bufferSize, initialStartLsn, atomicPoints: false, null, default);

    /// <summary>打开默认时序整点 WAL 集合；旧 v1 段只读恢复，新记录写入独立 v2 段。</summary>
    /// <param name="walDirectory">WAL 子目录。</param>
    /// <param name="policy">整点间滚动策略。</param>
    /// <param name="bufferSize">写缓冲字节数。</param>
    /// <param name="initialStartLsn">空目录的首条序列号。</param>
    /// <param name="recoveryOptions">恢复记录、字节与墙钟预算。</param>
    /// <param name="cancellationToken">打开、校验和截尾前的取消令牌。</param>
    /// <returns>仅使用整点写入的集合。</returns>
    public static WalSegmentSet OpenAtomicPoints(string walDirectory, WalRollingPolicy? policy = null,
        int bufferSize = 64 * 1024, long initialStartLsn = 1,
        AtomicPointWalRecoveryOptions? recoveryOptions = null, CancellationToken cancellationToken = default)
        => OpenCore(walDirectory, policy, bufferSize, initialStartLsn, atomicPoints: true,
            recoveryOptions ?? new(), cancellationToken);

    private static WalSegmentSet OpenCore(string walDirectory, WalRollingPolicy? policy, int bufferSize,
        long initialStartLsn, bool atomicPoints, AtomicPointWalRecoveryOptions? recoveryOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(walDirectory);
        recoveryOptions?.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        long recoveryStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        using var recoveryDeadline = atomicPoints
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (recoveryDeadline is not null)
        {
            recoveryDeadline.CancelAfter(recoveryOptions!.EffectiveTimeout);
            cancellationToken = recoveryDeadline.Token;
        }

        policy ??= WalRollingPolicy.Default;
        Directory.CreateDirectory(walDirectory);

        // 1. v2 发现阶段先校验数量/期限，再允许 legacy 名称升级或 active 修尾。
        IReadOnlyList<WalSegmentInfo>? discovered = atomicPoints
            ? WalSegmentLayout.EnumerateForAtomicRecovery(walDirectory, recoveryOptions!, recoveryStarted, cancellationToken)
            : null;
        bool hadLegacy = File.Exists(System.IO.Path.Combine(walDirectory, WalSegmentLayout.LegacyActiveFileName));
        WalSegmentLayout.UpgradeLegacyIfPresent(walDirectory, cancellationToken);

        // 2. 枚举目录中全部合法 segment
        var infos = atomicPoints
            ? hadLegacy
                ? WalSegmentLayout.EnumerateForAtomicRecovery(walDirectory, recoveryOptions!, recoveryStarted, cancellationToken)
                : discovered!
            : WalSegmentLayout.Enumerate(walDirectory);
        var segments = new List<WalSegmentInfo>(infos);
        if (atomicPoints)
            ValidateRecoveryBudgetBeforeMutation(segments, recoveryOptions!, recoveryStarted, cancellationToken);

        WalWriter activeWriter;

        if (segments.Count == 0)
        {
            // 3. 空目录：创建第一个 segment
            string path = WalSegmentLayout.SegmentPath(walDirectory, initialStartLsn);
            activeWriter = OpenWriter(path, initialStartLsn, bufferSize, atomicPoints, recoveryOptions, cancellationToken);
            segments.Add(new WalSegmentInfo(initialStartLsn, path, 0)
            {
                HasLastLsn = true,
                LastLsn = initialStartLsn - 1,
            });
        }
        else
        {
            PopulateDerivedLastLsns(segments);

            // 4. 选 startLsn 最大的 segment 为 active，续写
            var activeInfo = segments[^1];
            // WalWriter.Open 会扫描已有记录并续写，起始 LSN 由文件内容决定
            if (atomicPoints && WalReader.ReadFormatVersion(activeInfo.Path) != WalFileHeader.AtomicPointFormatVersion)
            {
                // 保持旧记录字节，不把历史逐字段写入重编码成整点。持独占写租约后校验旧尾，
                // 仅在合法记录末端建立新 v2 段；不越过无法证明的旧 torn suffix。
                using var legacyLease = new FileStream(activeInfo.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                using var legacy = WalReader.OpenForRecovery(activeInfo.Path, recoveryOptions, cancellationToken);
                foreach (WalRecord record in legacy.Replay())
                    _ = record;
                long suffixBytes = legacyLease.Length - legacy.LastValidOffset;
                if (suffixBytes != 0 && !(suffixBytes == FormatSizes.WalLastLsnFooterSize
                    && activeInfo.HasLastLsn && activeInfo.LastLsn == legacy.LastLsn))
                    throw new InvalidDataException("Legacy WAL has an unverified suffix; preserve and repair it before upgrading to atomic Point writes.");
                long newStart = checked(legacy.LastLsn + 1);
                cancellationToken.ThrowIfCancellationRequested();
                if (newStart == activeInfo.StartLsn)
                {
                    // 空旧段与新段同名；保留其原字节为非活动格式档案，再发布 v2 同名头。
                    legacy.Dispose();
                    legacyLease.Dispose();
                    File.Move(activeInfo.Path, activeInfo.Path + ".legacy-empty-v1");
                    DirectoryFsync.FlushRequired(walDirectory);
                    segments.RemoveAt(segments.Count - 1);
                }
                activeWriter = OpenWriter(WalSegmentLayout.SegmentPath(walDirectory, newStart), newStart,
                    bufferSize, atomicPoints, recoveryOptions, cancellationToken);
                segments.Add(new WalSegmentInfo(newStart, activeWriter.Path, activeWriter.BytesWritten)
                {
                    HasLastLsn = true,
                    LastLsn = newStart - 1,
                });
                activeInfo = segments[^1];
            }
            else
            {
                activeWriter = OpenWriter(activeInfo.Path, activeInfo.StartLsn, bufferSize, atomicPoints, recoveryOptions, cancellationToken);
            }
            // 更新最后一条（active）的文件长度
            segments[^1] = activeInfo with
            {
                FileLength = activeWriter.BytesWritten,
                HasLastLsn = true,
                LastLsn = activeWriter.NextLsn - 1,
            };
        }

        return new WalSegmentSet(walDirectory, policy, bufferSize, segments, activeWriter, atomicPoints, recoveryOptions);
    }

    private static WalWriter OpenWriter(string path, long startLsn, int bufferSize, bool atomicPoints,
        AtomicPointWalRecoveryOptions? recoveryOptions, CancellationToken cancellationToken)
        => atomicPoints
            ? WalWriter.OpenAtomicPoints(path, startLsn, bufferSize, recoveryOptions, cancellationToken)
            : WalWriter.Open(path, startLsn, bufferSize);

    private static void ValidateRecoveryBudgetBeforeMutation(IReadOnlyList<WalSegmentInfo> segments,
        AtomicPointWalRecoveryOptions options, long started, CancellationToken cancellationToken)
    {
        long bytes = 0;
        int records = 0;
        if (segments.Count > options.MaxRecords)
            throw new InvalidDataException("WAL recovery exceeds its segment count budget.");
        foreach (WalSegmentInfo segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= options.EffectiveTimeout)
                throw new InvalidDataException("WAL recovery exceeds its wall-clock budget.");
            bytes = checked(bytes + segment.FileLength);
            if (bytes > options.MaxReadBytes)
                throw new InvalidDataException("WAL recovery exceeds its total file byte budget.");
            using var reader = WalReader.OpenForRecovery(segment.Path, options, cancellationToken);
            foreach (WalRecord record in reader.Replay())
            {
                _ = record;
                cancellationToken.ThrowIfCancellationRequested();
                if (++records > options.MaxRecords)
                    throw new InvalidDataException("WAL recovery exceeds its total record budget.");
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= options.EffectiveTimeout)
                    throw new InvalidDataException("WAL recovery exceeds its wall-clock budget.");
            }
        }
    }

    /// <summary>追加单个完整 Point，滚动只发生在该 Point 完整写入之后。</summary>
    /// <param name="seriesId">序列标识。</param>
    /// <param name="pointTimestamp">Unix 毫秒时间戳。</param>
    /// <param name="fields">完整字段集合。</param>
    /// <param name="cancellationToken">首次写入前的取消令牌。</param>
    /// <returns>整点共享 LSN。</returns>
    public long AppendAtomicPoint(ulong seriesId, long pointTimestamp, IReadOnlyDictionary<string, FieldValue> fields,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fields);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfWriteFaulted();
            bool appended = false;
            try
            {
                int payloadBytes = sizeof(int);
                foreach (var field in fields)
                    payloadBytes = checked(payloadBytes + sizeof(int) + WalPayloadCodec.MeasureWritePoint(field.Key, field.Value));
                EnsureRecordCapacityLocked(payloadBytes);
                _activeWriter!.AfterAtomicPointWriteTestHook = AfterAtomicPointWriteTestHook;
                long lsn = _activeWriter.AppendAtomicPoint(seriesId, pointTimestamp, fields, cancellationToken);
                appended = true;
                _activeRecordCount++;
                UpdateActiveSegmentInfoLocked();
                RollIfNeeded();
                return lsn;
            }
            catch (Exception ex) when (appended || _activeWriter?.HasAppendFault == true)
            {
                _writeFault = ex;
                throw;
            }
        }
    }

    /// <summary>
    /// 追加一条 WritePoint 记录，返回分配的 LSN。若超过滚动阈值则自动 Roll。
    /// </summary>
    /// <param name="seriesId">序列唯一标识。</param>
    /// <param name="pointTimestamp">数据点时间戳（Unix 毫秒）。</param>
    /// <param name="fieldName">字段名称。</param>
    /// <param name="value">字段值。</param>
    /// <returns>分配的 LSN。</returns>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public long AppendWritePoint(ulong seriesId, long pointTimestamp, string fieldName, FieldValue value)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfWriteFaulted();
            long lsn = _activeWriter!.AppendWritePoint(seriesId, pointTimestamp, fieldName, value);
            _activeRecordCount++;
            UpdateActiveSegmentInfoLocked();
            RollIfNeeded();
            return lsn;
        }
    }

    /// <summary>
    /// 追加一条 CreateSeries 记录，返回分配的 LSN。若超过滚动阈值则自动 Roll。
    /// </summary>
    /// <param name="seriesId">序列唯一标识。</param>
    /// <param name="measurement">Measurement 名称。</param>
    /// <param name="tags">Tag 键值对。</param>
    /// <returns>分配的 LSN。</returns>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public long AppendCreateSeries(ulong seriesId, string measurement, IReadOnlyDictionary<string, string> tags)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfWriteFaulted();
            EnsureRecordCapacityLocked(WalPayloadCodec.MeasureCreateSeries(measurement, tags));
            long lsn = _activeWriter!.AppendCreateSeries(seriesId, measurement, tags);
            _activeRecordCount++;
            UpdateActiveSegmentInfoLocked();
            RollIfNeeded();
            return lsn;
        }
    }

    /// <summary>
    /// 追加一条 Checkpoint 记录，返回分配的 LSN。
    /// </summary>
    /// <param name="checkpointLsn">检查点 LSN（截止该 LSN 的数据已落盘）。</param>
    /// <returns>分配的 LSN。</returns>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public long AppendCheckpoint(long checkpointLsn)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfWriteFaulted();
            EnsureRecordCapacityLocked(sizeof(long));
            long lsn = _activeWriter!.AppendCheckpoint(checkpointLsn);
            _activeRecordCount++;
            UpdateActiveSegmentInfoLocked();
            return lsn;
        }
    }

    /// <summary>
    /// 追加一条 Delete 记录，返回分配的 LSN。若超过滚动阈值则自动 Roll。
    /// </summary>
    /// <param name="seriesId">序列唯一标识。</param>
    /// <param name="fieldName">字段名称。</param>
    /// <param name="fromTimestamp">删除时间窗起始时间戳（Unix 毫秒，闭区间）。</param>
    /// <param name="toTimestamp">删除时间窗结束时间戳（Unix 毫秒，闭区间）。</param>
    /// <returns>分配的 LSN。</returns>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public long AppendDelete(ulong seriesId, string fieldName, long fromTimestamp, long toTimestamp)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfWriteFaulted();
            EnsureRecordCapacityLocked(WalPayloadCodec.MeasureDelete(fieldName));
            long lsn = _activeWriter!.AppendDelete(seriesId, fieldName, fromTimestamp, toTimestamp);
            _activeRecordCount++;
            UpdateActiveSegmentInfoLocked();
            RollIfNeeded();
            return lsn;
        }
    }

    /// <summary>
    /// 强制 fsync，确保 active segment 数据持久化到磁盘。
    /// </summary>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public void Sync()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfWriteFaulted();
            SyncActiveWriterLocked();
        }
    }

    /// <summary>
    /// 把 active segment 的写缓冲区 flush 到 OS（不 fsync）。使已 append 的 WAL 记录进入内核
    /// page cache，从而普通进程崩溃后可恢复；掉电仍可能丢失。开销远低于 <see cref="Sync"/>（#196）。
    /// </summary>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public void FlushToOs()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfWriteFaulted();
            _activeWriter!.Flush();
        }
    }

    /// <summary>
    /// 主动滚动：关闭当前 active segment 并以 NextLsn 打开新 segment。
    /// 若当前 active segment 只含文件头（无记录），跳过滚动（避免产生空 segment）。
    /// </summary>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public void Roll()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfWriteFaulted();
            RollLocked();
        }
    }

    /// <summary>
    /// 按 checkpointLsn 回收：删除所有最后一条记录的 LSN ≤ checkpointLsn 的旧 segment。
    /// active segment 永不删除。
    /// </summary>
    /// <param name="checkpointLsn">检查点 LSN（含）。</param>
    /// <returns>被删除的 segment 数量。</returns>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public int RecycleUpTo(long checkpointLsn)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            int count = 0;
            // 保留 active（最后一个），只遍历非 active 段
            int limit = _segments.Count - 1;
            for (int i = 0; i < limit; i++)
            {
                var info = _segments[i];
                // 该 segment 最后一条的 LSN = 下一个 segment.StartLsn - 1
                long nextStart = _segments[i + 1].StartLsn;
                long lastLsn = nextStart - 1;
                if (lastLsn <= checkpointLsn)
                {
                    try { File.Delete(info.Path); } catch { /* 容忍删除失败 */ }
                    _segments.RemoveAt(i);
                    i--;
                    limit--;
                    count++;
                }
                else
                {
                    // 段按 startLsn 升序，遇到第一个不满足的即可停止
                    break;
                }
            }
            return count;
        }
    }

    /// <summary>
    /// 顺序回放全部 segment（含 active），单次扫描记录并按 checkpoint LSN 过滤 WritePoint。
    /// </summary>
    /// <param name="catalog">目标序列目录；CreateSeries 记录将被幂等应用到此 catalog。</param>
    /// <returns>包含 CheckpointLsn、LastLsn 及过滤后写入点列表的 <see cref="WalReplayResult"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> 为 null 时抛出。</exception>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public WalReplayResult ReplayWithCheckpoint(SeriesCatalog catalog)
        => ReplayWithCheckpoint(catalog, durableCheckpointLsn: 0);

    /// <summary>
    /// 顺序回放全部 segment（含 active），并把已独立持久化的 checkpoint LSN 纳入过滤下界。
    /// </summary>
    /// <param name="catalog">目标序列目录；CreateSeries 记录将被幂等应用到此 catalog。</param>
    /// <param name="durableCheckpointLsn">已通过独立 checkpoint 元数据确认持久化的 LSN；0 表示不存在。</param>
    /// <returns>包含 CheckpointLsn、LastLsn 及过滤后写入点列表的 <see cref="WalReplayResult"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> 为 null 时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="durableCheckpointLsn"/> 小于 0 时抛出。</exception>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public WalReplayResult ReplayWithCheckpoint(SeriesCatalog catalog, long durableCheckpointLsn)
        => ReplayWithCheckpoint(catalog, durableCheckpointLsn, durableTombstoneCheckpointLsn: 0);

    internal WalReplayResult ReplayWithCheckpoint(
        SeriesCatalog catalog,
        long durableCheckpointLsn,
        long durableTombstoneCheckpointLsn)
        => ReplayWithCheckpoint(
            catalog,
            durableCheckpointLsn,
            durableTombstoneCheckpointLsn,
            highestVerifiedCheckpointLsn: long.MaxValue,
            cancellationToken: default);

    /// <summary>
    /// 顺序回放 WAL，并限制可由 WAL CheckpointRecord 推进的最大已验证边界。
    /// </summary>
    /// <remarks>
    /// 独立 checkpoint 文件丢失或损坏时，WAL 中的 checkpoint 记录本身不足以证明
    /// 对应 Segment 仍存在。嵌入式引擎启动路径会传入已验证的 durable 边界，防止
    /// 仅因 WAL 中残留记录而静默跳过已回收的数据；公开兼容重载仍保留历史行为。
    /// </remarks>
    internal WalReplayResult ReplayWithCheckpoint(
        SeriesCatalog catalog,
        long durableCheckpointLsn,
        long durableTombstoneCheckpointLsn,
        long highestVerifiedCheckpointLsn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentOutOfRangeException.ThrowIfNegative(durableCheckpointLsn);
        ArgumentOutOfRangeException.ThrowIfNegative(durableTombstoneCheckpointLsn);
        ArgumentOutOfRangeException.ThrowIfNegative(highestVerifiedCheckpointLsn);
        if (highestVerifiedCheckpointLsn < durableCheckpointLsn)
        {
            throw new ArgumentOutOfRangeException(
                nameof(highestVerifiedCheckpointLsn),
                "最高已验证 checkpoint 不能小于 durable checkpoint。");
        }

        IReadOnlyList<WalSegmentInfo> snapshot;
        lock (_sync)
        {
            ThrowIfDisposed();
            snapshot = CreateSegmentSnapshotLocked();
        }

        long checkpointLsn = durableCheckpointLsn;
        long lastLsn = 0;
        var writePoints = new List<WritePointRecord>();
        var atomicPoints = new List<AtomicPointRecord>();
        var deleteRecords = new List<DeleteRecord>();
        long deleteCheckpointLsn = Math.Max(checkpointLsn, durableTombstoneCheckpointLsn);

        long recoveryStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        long recoveredBytes = 0;
        int recoveredRecords = 0;
        for (int segmentIndex = 0; segmentIndex < snapshot.Count; segmentIndex++)
        {
            WalSegmentInfo seg = snapshot[segmentIndex];
            cancellationToken.ThrowIfCancellationRequested();
            if (_recoveryOptions is not null)
            {
                recoveredBytes = checked(recoveredBytes + seg.FileLength);
                if (recoveredBytes > _recoveryOptions.MaxReadBytes)
                    throw new InvalidDataException("WAL recovery exceeds its total file byte budget.");
            }
            if (!File.Exists(seg.Path))
                continue;

            if (seg.HasLastLsn && seg.LastLsn <= checkpointLsn)
            {
                if (seg.LastLsn > lastLsn)
                    lastLsn = seg.LastLsn;
                continue;
            }

            int formatVersion = WalReader.ReadFormatVersion(seg.Path);
            using var reader = formatVersion == WalFileHeader.AtomicPointFormatVersion
                ? WalReader.OpenAtomicPoints(seg.Path, _recoveryOptions, cancellationToken)
                : WalReader.Open(seg.Path);
            if (reader.FirstLsn != seg.StartLsn)
                throw new InvalidDataException("WAL file name and header first LSN disagree.");
            foreach (var record in reader.Replay())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_recoveryOptions is not null && (++recoveredRecords > _recoveryOptions.MaxRecords
                    || System.Diagnostics.Stopwatch.GetElapsedTime(recoveryStarted) >= _recoveryOptions.EffectiveTimeout))
                    throw new InvalidDataException("WAL recovery exceeds its total record or wall-clock budget.");
                lastLsn = record.Lsn;
                switch (record)
                {
                    case CreateSeriesRecord cs:
                        var entry = catalog.GetOrAdd(cs.Measurement, cs.Tags);
                        if (entry.Id != cs.SeriesId)
                            throw new InvalidDataException(
                                $"WAL CreateSeries SeriesId mismatch for '{cs.Measurement}': " +
                                $"WAL={cs.SeriesId}, computed={entry.Id}.");
                        break;

                    case CheckpointRecord cp:
                        if (cp.CheckpointLsn > highestVerifiedCheckpointLsn)
                        {
                            throw new InvalidDataException(
                                $"WAL checkpoint LSN {cp.CheckpointLsn} exceeds the highest verified durable "
                                + $"checkpoint LSN {highestVerifiedCheckpointLsn}. Recovery is rejected to avoid "
                                + "skipping data whose published Segment cannot be proven present.");
                        }

                        if (cp.CheckpointLsn > checkpointLsn)
                        {
                            checkpointLsn = cp.CheckpointLsn;
                            deleteCheckpointLsn = Math.Max(checkpointLsn, durableTombstoneCheckpointLsn);
                            RemoveCheckpointedWritePoints(writePoints, checkpointLsn);
                            RemoveCheckpointedAtomicPoints(atomicPoints, checkpointLsn);
                            RemoveCheckpointedDeleteRecords(deleteRecords, deleteCheckpointLsn);
                        }
                        break;

                    case WritePointRecord wp:
                        if (wp.Lsn > checkpointLsn)
                            writePoints.Add(wp);
                        break;

                    case AtomicPointRecord point:
                        if (point.Lsn > checkpointLsn)
                            atomicPoints.Add(point);
                        break;

                    case DeleteRecord del:
                        if (del.Lsn > deleteCheckpointLsn)
                            deleteRecords.Add(del);
                        break;
                }
            }
            if (_atomicPoints && segmentIndex + 1 < snapshot.Count
                && reader.LastLsn != snapshot[segmentIndex + 1].StartLsn - 1)
                throw new InvalidDataException("WAL recovery cannot cross a damaged or missing record before a later segment.");
        }

        return new WalReplayResult(checkpointLsn, lastLsn, writePoints, deleteRecords)
        {
            AtomicPoints = atomicPoints,
        };
    }

    /// <summary>
    /// 关闭写入器并刷盘（fsync）。不删除任何 segment 文件。
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                _activeWriter?.Sync();
            }
            finally
            {
                _activeWriter?.Dispose();
                _activeWriter = null;
            }
        }
    }

    // ── 私有辅助 ─────────────────────────────────────────────────────────────

    private static void PopulateDerivedLastLsns(List<WalSegmentInfo> segments)
    {
        for (int i = 0; i < segments.Count - 1; i++)
        {
            if (!segments[i].HasLastLsn)
            {
                segments[i] = segments[i] with
                {
                    HasLastLsn = true,
                    LastLsn = segments[i + 1].StartLsn - 1,
                };
            }
        }
    }

    private IReadOnlyList<WalSegmentInfo> CreateSegmentSnapshotLocked()
    {
        var snapshot = _segments.ToList();
        PopulateDerivedLastLsns(snapshot);
        return snapshot.AsReadOnly();
    }

    private void UpdateActiveSegmentInfoLocked()
    {
        if (_activeWriter is null || _segments.Count == 0)
            return;

        var active = _segments[^1];
        _segments[^1] = active with
        {
            FileLength = _activeWriter.BytesWritten,
            HasLastLsn = true,
            LastLsn = _activeWriter.NextLsn - 1,
        };
    }

    private static void RemoveCheckpointedWritePoints(List<WritePointRecord> records, long checkpointLsn)
    {
        int removeCount = 0;
        while (removeCount < records.Count && records[removeCount].Lsn <= checkpointLsn)
            removeCount++;

        if (removeCount > 0)
            records.RemoveRange(0, removeCount);
    }

    private static void RemoveCheckpointedAtomicPoints(List<AtomicPointRecord> records, long checkpointLsn)
    {
        int removeCount = 0;
        while (removeCount < records.Count && records[removeCount].Lsn <= checkpointLsn)
            removeCount++;

        if (removeCount > 0)
            records.RemoveRange(0, removeCount);
    }

    private static void RemoveCheckpointedDeleteRecords(List<DeleteRecord> records, long checkpointLsn)
    {
        int removeCount = 0;
        while (removeCount < records.Count && records[removeCount].Lsn <= checkpointLsn)
            removeCount++;

        if (removeCount > 0)
            records.RemoveRange(0, removeCount);
    }

    /// <summary>
    /// 若当前 active segment 超过滚动阈值，在锁内执行 Roll。
    /// 调用方必须持有 <c>_sync</c> 锁。
    /// </summary>
    private void RollIfNeeded()
    {
        bool bytesExceeded = Policy.Enabled && _activeWriter!.BytesWritten >= Policy.MaxBytesPerSegment;
        bool recordsExceeded = Policy.Enabled && _activeRecordCount >= Policy.MaxRecordsPerSegment;
        recordsExceeded |= _atomicPoints && _recoveryOptions is not null && _activeRecordCount >= _recoveryOptions.MaxRecords;

        if (bytesExceeded || recordsExceeded)
            RollLocked();
    }

    private void EnsureRecordCapacityLocked(int payloadBytes)
    {
        if (!_atomicPoints || _recoveryOptions is null)
            return;
        long recordBytes = (long)FormatSizes.WalRecordHeaderSize + payloadBytes;
        long minimumFileBytes = FormatSizes.WalFileHeaderSize + recordBytes + FormatSizes.WalLastLsnFooterSize;
        if (minimumFileBytes > _recoveryOptions.MaxReadBytes)
            throw new ArgumentOutOfRangeException(nameof(payloadBytes), "A WAL record exceeds the configured recovery byte budget.");
        if (_activeWriter!.BytesWritten + recordBytes + FormatSizes.WalLastLsnFooterSize > _recoveryOptions.MaxReadBytes
            || _activeRecordCount >= _recoveryOptions.MaxRecords)
            RollLocked();
    }

    /// <summary>
    /// 执行滚动：关闭 active，打开新 segment。
    /// 若 active 仅含文件头（BytesWritten == WalFileHeaderSize），跳过以避免产生空 segment。
    /// 调用方必须持有 <c>_sync</c> 锁。
    /// </summary>
    private void RollLocked()
    {
        if (_activeWriter == null)
            return;

        // 避免产生只含文件头的空 segment
        if (_activeWriter.BytesWritten <= Storage.Format.FormatSizes.WalFileHeaderSize)
            return;

        try
        {
            long newStartLsn = _activeWriter.NextLsn;
            SyncActiveWriterLocked();
            _activeWriter.Dispose();
            _activeWriter = null;

            string newPath = WalSegmentLayout.SegmentPath(WalDirectory, newStartLsn);
            _activeWriter = OpenWriter(newPath, newStartLsn, _bufferSize, _atomicPoints, _recoveryOptions, default);
            _segments.Add(new WalSegmentInfo(newStartLsn, newPath, 0)
            {
                HasLastLsn = true,
                LastLsn = newStartLsn - 1,
            });
            _activeRecordCount = 0;
        }
        catch (Exception ex)
        {
            _writeFault = ex;
            throw;
        }
    }

    private void SyncActiveWriterLocked()
    {
        // WAL fsync 单点计量（M17 #89）：所有 fsync（每写 fsync / group-commit / flush checkpoint /
        // Delete 强制同步）都汇聚到这里。未监听时 Enabled=false 短路，不取时间戳。
        long startTimestamp = Diagnostics.SonnetDbMeter.StartWalFsyncTiming();

        _activeWriter!.Sync();
        _syncCount++;
        UpdateActiveSegmentInfoLocked();

        Diagnostics.SonnetDbMeter.RecordTsdbWalFsync(startTimestamp);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WalSegmentSet));
    }
}
