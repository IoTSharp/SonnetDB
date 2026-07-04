using SonnetDB.Catalog;
using SonnetDB.Model;

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

    private WalWriter? _activeWriter;
    private long _activeRecordCount;
    private long _syncCount;
    private bool _disposed;

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

    private WalSegmentSet(string walDirectory, WalRollingPolicy policy, int bufferSize, List<WalSegmentInfo> segments, WalWriter activeWriter)
    {
        WalDirectory = walDirectory;
        Policy = policy;
        _bufferSize = bufferSize;
        _segments = segments;
        _activeWriter = activeWriter;
        _activeRecordCount = 0;
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
    {
        ArgumentNullException.ThrowIfNull(walDirectory);

        policy ??= WalRollingPolicy.Default;
        Directory.CreateDirectory(walDirectory);

        // 1. 升级 legacy active.SDBWAL（若存在）
        WalSegmentLayout.UpgradeLegacyIfPresent(walDirectory);

        // 2. 枚举目录中全部合法 segment
        var infos = WalSegmentLayout.Enumerate(walDirectory);
        var segments = new List<WalSegmentInfo>(infos);

        WalWriter activeWriter;

        if (segments.Count == 0)
        {
            // 3. 空目录：创建第一个 segment
            string path = WalSegmentLayout.SegmentPath(walDirectory, initialStartLsn);
            activeWriter = WalWriter.Open(path, startLsn: initialStartLsn, bufferSize: bufferSize);
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
            activeWriter = WalWriter.Open(activeInfo.Path, startLsn: activeInfo.StartLsn, bufferSize: bufferSize);
            // 更新最后一条（active）的文件长度
            segments[^1] = activeInfo with
            {
                FileLength = activeWriter.BytesWritten,
                HasLastLsn = true,
                LastLsn = activeWriter.NextLsn - 1,
            };
        }

        return new WalSegmentSet(walDirectory, policy, bufferSize, segments, activeWriter);
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
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentOutOfRangeException.ThrowIfNegative(durableCheckpointLsn);
        ArgumentOutOfRangeException.ThrowIfNegative(durableTombstoneCheckpointLsn);

        IReadOnlyList<WalSegmentInfo> snapshot;
        lock (_sync)
        {
            ThrowIfDisposed();
            snapshot = CreateSegmentSnapshotLocked();
        }

        long checkpointLsn = durableCheckpointLsn;
        long lastLsn = 0;
        var writePoints = new List<WritePointRecord>();
        var deleteRecords = new List<DeleteRecord>();
        long deleteCheckpointLsn = Math.Max(checkpointLsn, durableTombstoneCheckpointLsn);

        foreach (var seg in snapshot)
        {
            if (!File.Exists(seg.Path))
                continue;

            if (seg.HasLastLsn && seg.LastLsn <= checkpointLsn)
            {
                if (seg.LastLsn > lastLsn)
                    lastLsn = seg.LastLsn;
                continue;
            }

            using var reader = WalReader.Open(seg.Path);
            foreach (var record in reader.Replay())
            {
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
                        if (cp.CheckpointLsn > checkpointLsn)
                        {
                            checkpointLsn = cp.CheckpointLsn;
                            deleteCheckpointLsn = Math.Max(checkpointLsn, durableTombstoneCheckpointLsn);
                            RemoveCheckpointedWritePoints(writePoints, checkpointLsn);
                            RemoveCheckpointedDeleteRecords(deleteRecords, deleteCheckpointLsn);
                        }
                        break;

                    case WritePointRecord wp:
                        if (wp.Lsn > checkpointLsn)
                            writePoints.Add(wp);
                        break;

                    case DeleteRecord del:
                        if (del.Lsn > deleteCheckpointLsn)
                            deleteRecords.Add(del);
                        break;
                }
            }
        }

        return new WalReplayResult(checkpointLsn, lastLsn, writePoints, deleteRecords);
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
        if (!Policy.Enabled)
            return;

        bool bytesExceeded = _activeWriter!.BytesWritten >= Policy.MaxBytesPerSegment;
        bool recordsExceeded = _activeRecordCount >= Policy.MaxRecordsPerSegment;

        if (bytesExceeded || recordsExceeded)
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

        long newStartLsn = _activeWriter.NextLsn;
        SyncActiveWriterLocked();
        _activeWriter.Dispose();
        _activeWriter = null;

        string newPath = WalSegmentLayout.SegmentPath(WalDirectory, newStartLsn);
        _activeWriter = WalWriter.Open(newPath, startLsn: newStartLsn, bufferSize: _bufferSize);
        _segments.Add(new WalSegmentInfo(newStartLsn, newPath, 0)
        {
            HasLastLsn = true,
            LastLsn = newStartLsn - 1,
        });
        _activeRecordCount = 0;
    }

    private void SyncActiveWriterLocked()
    {
        // WAL fsync 单点计量（M17 #89）：所有 fsync（每写 fsync / group-commit / flush checkpoint /
        // Delete 强制同步）都汇聚到这里。未监听时 Enabled=false 短路，不取时间戳。
        long startTimestamp = Diagnostics.SonnetDbMeter.WalFsyncDuration.Enabled
            ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

        _activeWriter!.Sync();
        _syncCount++;
        UpdateActiveSegmentInfoLocked();

        if (startTimestamp != 0)
        {
            Diagnostics.SonnetDbMeter.WalFsyncDuration.Record(
                System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WalSegmentSet));
    }
}
