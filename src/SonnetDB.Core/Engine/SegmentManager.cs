using SonnetDB.Memory;
using SonnetDB.Storage.Segments;

namespace SonnetDB.Engine;

/// <summary>
/// 已打开的 <see cref="SegmentReader"/> 集合的所有者，同时作为引擎读快照（SuperVersion）的唯一原子发布者。
/// <list type="bullet">
///   <item><description>启动时扫描 segments/ 目录，构建初始集合 + 索引快照；</description></item>
///   <item><description>Flush 完成后，调用 <see cref="AddSegment"/> 接入新段，重建索引快照（原子替换）；</description></item>
///   <item><description>持有当前 active / sealing <see cref="MemTable"/> 引用，随每次快照发布一并原子切换，</description></item>
///   <item><description>使查询通过单次 <see cref="AcquireSnapshot"/> 拿到 {active + sealing MemTable + segments} 一致视图；</description></item>
///   <item><description>进程关闭或显式 Dispose 时关闭所有 <see cref="SegmentReader"/>。</description></item>
/// </list>
/// 线程安全：内部 lock 保护"重建+替换"，读取通过 volatile 字段做无锁读。
/// </summary>
public sealed class SegmentManager : IDisposable
{
    private static readonly IReadOnlyList<MemTable> EmptyMemTables = Array.Empty<MemTable>();

    private readonly object _lock = new();
    private readonly SegmentReaderOptions? _readerOptions;
    private readonly Dictionary<long, SegmentReaderLeaseState> _readerById = new();
    // 每段 SegmentIndex 缓存：段内容不可变，SegmentIndex.Build 对固定 (reader, segId) 确定，
    // 故只在段首次加入时构建一次，add/swap/drop 时增量复用未变段的索引，避免每次全量重建（C7）。
    private readonly Dictionary<long, SegmentIndex> _indexById = new();
    private MemTable? _activeMemTable;
    private IReadOnlyList<MemTable> _sealingMemTables = EmptyMemTables;
    private SegmentManagerSnapshot _snapshot = new(MultiSegmentIndex.Empty, Array.Empty<SegmentReaderLeaseState>());
    private bool _disposed;

    /// <summary>当前所有已打开的 <see cref="SegmentReader"/> 快照（按 SegmentId 升序）。</summary>
    public IReadOnlyList<SegmentReader> Readers => CurrentSnapshot.Readers;

    /// <summary>当前索引快照（无锁读取，与 <see cref="Readers"/> 来自同一个已发布快照）。</summary>
    public MultiSegmentIndex Index => CurrentSnapshot.Index;

    /// <summary>当前已加载的段数量。</summary>
    public int SegmentCount => Index.SegmentCount;

    internal SegmentManagerSnapshot CurrentSnapshot => Volatile.Read(ref _snapshot);

    private SegmentManager(SegmentReaderOptions? readerOptions)
    {
        _readerOptions = readerOptions;
    }

    /// <summary>
    /// 扫描 <paramref name="rootDirectory"/> 下 segments/ 子目录，打开所有已落盘段文件，
    /// 构建初始 <see cref="SegmentManager"/> 实例。
    /// </summary>
    /// <param name="rootDirectory">数据库根目录路径。</param>
    /// <param name="readerOptions">段读取选项；为 null 时使用 <see cref="SegmentReaderOptions.Default"/>。</param>
    /// <returns>已初始化的 <see cref="SegmentManager"/> 实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rootDirectory"/> 为 null 时抛出。</exception>
    public static SegmentManager Open(string rootDirectory, SegmentReaderOptions? readerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);

        var manager = new SegmentManager(readerOptions);

        var suppressedSegmentIds = SegmentReplacementManifest
            .LoadForRoot(rootDirectory)
            .GetSegmentIdsToSuppress(rootDirectory);

        var segments = TsdbPaths.EnumerateSegments(rootDirectory)
            .Where(t => !suppressedSegmentIds.Contains(t.SegmentId))
            .OrderBy(static t => t.SegmentId)
            .ToList();

        foreach (var (segId, path) in segments)
        {
            try
            {
                var reader = SegmentReader.Open(path, readerOptions);
                manager._readerById[segId] = new SegmentReaderLeaseState(reader);
            }
            catch (SegmentCorruptedException)
            {
                // 跳过损坏或不完整的段文件（如崩溃中断的临时写入），不阻止引擎启动。
            }
        }

        // S12：清理被 manifest 抑制、但因上次删除失败（Windows 文件锁等）残留在盘上的死段文件及其
        // 向量/聚合索引 sidecar。此前这些孤儿文件被 Open 跳过加载后就再无人删除，永久泄漏磁盘；
        // 现在启动时重试清理。删除仍失败的文件下次启动会再次尝试（幂等）。
        CleanupSuppressedOrphans(rootDirectory, suppressedSegmentIds);

        manager.RebuildSnapshotsUnsafe();
        return manager;
    }

    /// <summary>
    /// 删除被 manifest 抑制的死段在盘上的残留文件（段主文件 + 向量 / 聚合索引 sidecar）。
    /// 逐文件尝试删除，失败（如仍被占用）不抛出——下次启动会再次重试，保证最终清理（S12）。
    /// </summary>
    private static void CleanupSuppressedOrphans(string rootDirectory, IReadOnlySet<long> suppressedSegmentIds)
    {
        if (suppressedSegmentIds.Count == 0)
            return;

        foreach (long segId in suppressedSegmentIds)
        {
            if (segId <= 0)
                continue;

            foreach (string artifactPath in TsdbPaths.SegmentArtifactPaths(rootDirectory, segId))
            {
                try
                {
                    if (File.Exists(artifactPath))
                        File.Delete(artifactPath);
                }
                catch (IOException)
                {
                    // 文件仍被占用等瞬时错误：留待下次启动重试，不阻塞引擎启动。
                }
                catch (UnauthorizedAccessException)
                {
                    // 权限 / 只读等瞬时错误：同上，下次启动重试。
                }
            }
        }
    }

    /// <summary>
    /// 把新写入的段加入集合，重建并发布索引快照。返回新段对应的 <see cref="SegmentReader"/>。
    /// </summary>
    /// <param name="path">新段文件的完整路径。</param>
    /// <returns>已打开的 <see cref="SegmentReader"/> 实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> 为 null 时抛出。</exception>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public SegmentReader AddSegment(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var reader = SegmentReader.Open(path, _readerOptions);
            long segId = reader.Header.SegmentId;
            SegmentReaderLeaseState[] replaced = _readerById.TryGetValue(segId, out var oldReader)
                ? [oldReader]
                : [];
            _readerById[segId] = new SegmentReaderLeaseState(reader);
            _indexById.Remove(segId); // 同 id 段被替换：作废旧索引缓存，让 rebuild 用新 reader 重建。
            RebuildSnapshotsLocked(replaced);
            return reader;
        }
    }

    /// <summary>
    /// 原子地移除多个旧段并加入一个新段，重建并发布索引快照。
    /// <para>在锁内一次性完成"移除旧 Reader + 打开新 Reader + 重建快照"，避免中间状态可见。</para>
    /// </summary>
    /// <param name="removeIds">要移除的段 ID 列表。</param>
    /// <param name="addedPath">新段的文件路径。</param>
    /// <returns>已打开的新段 <see cref="SegmentReader"/> 实例。</returns>
    /// <exception cref="ArgumentNullException">任何参数为 null 时抛出。</exception>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public SegmentReader SwapSegments(IReadOnlyList<long> removeIds, string addedPath)
    {
        ArgumentNullException.ThrowIfNull(removeIds);
        ArgumentNullException.ThrowIfNull(addedPath);

        List<SegmentReaderLeaseState> toDispose;
        SegmentReader newReader;

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            newReader = SegmentReader.Open(addedPath, _readerOptions);
            long newSegId = newReader.Header.SegmentId;

            toDispose = new List<SegmentReaderLeaseState>(removeIds.Count);
            foreach (long segId in removeIds)
            {
                if (_readerById.TryGetValue(segId, out var old))
                {
                    _readerById.Remove(segId);
                    toDispose.Add(old);
                }
            }

            _readerById[newSegId] = new SegmentReaderLeaseState(newReader);
            _indexById.Remove(newSegId); // 防御：新段 id 若与缓存冲突则作废，rebuild 以新 reader 重建。
            RebuildSnapshotsLocked(toDispose);
        }

        return newReader;
    }

    /// <summary>
    /// 原子地移除多个段（仅移除，不添加新段），重建并发布索引快照。
    /// <para>
    /// 与 <see cref="SwapSegments"/> 不同：<c>DropSegments</c> 仅移除，不添加。
    /// 适用于 Retention TTL 直接淘汰整段过期数据。
    /// </para>
    /// </summary>
    /// <param name="ids">要移除的段 ID 列表。</param>
    /// <returns>被成功移除的 <see cref="SegmentReader"/> 列表（仅供诊断；调用方不应再 Dispose）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ids"/> 为 null 时抛出。</exception>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public IReadOnlyList<SegmentReader> DropSegments(IReadOnlyList<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        List<SegmentReaderLeaseState> toDispose;

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            toDispose = new List<SegmentReaderLeaseState>(ids.Count);
            foreach (long segId in ids)
            {
                if (_readerById.TryGetValue(segId, out var old))
                {
                    _readerById.Remove(segId);
                    toDispose.Add(old);
                }
            }

            RebuildSnapshotsLocked(toDispose);
        }

        return toDispose.Select(static state => state.Reader).ToArray();
    }

    /// <summary>
    /// 移除指定段（用于未来 Compaction），关闭对应 <see cref="SegmentReader"/> 后重建索引。
    /// </summary>
    /// <param name="segmentId">要移除的段唯一标识符。</param>
    /// <returns>找到并成功移除返回 <c>true</c>；未找到返回 <c>false</c>。</returns>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public bool RemoveSegment(long segmentId)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_readerById.TryGetValue(segmentId, out var reader))
                return false;

            _readerById.Remove(segmentId);
            RebuildSnapshotsLocked([reader]);
            return true;
        }
    }

    /// <summary>
    /// 关闭全部 <see cref="SegmentReader"/>。
    /// </summary>
    public void Dispose()
    {
        List<SegmentReaderLeaseState> readersToDispose;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            readersToDispose = new List<SegmentReaderLeaseState>(_readerById.Values);
            _readerById.Clear();
            _indexById.Clear();
            PublishSnapshotUnsafe(MultiSegmentIndex.Empty, Array.Empty<SegmentReaderLeaseState>(), readersToDispose);
        }
    }

    internal SegmentManagerSnapshotLease AcquireSnapshot()
    {
        while (true)
        {
            var snapshot = CurrentSnapshot;
            if (snapshot.TryAcquire())
                return new SegmentManagerSnapshotLease(snapshot);
        }
    }

    /// <summary>
    /// 设置初始活跃 <see cref="MemTable"/> 并发布，使查询能通过统一快照读到它。
    /// 仅在引擎构造时调用一次（此时无并发读者）。
    /// </summary>
    /// <param name="activeMemTable">初始活跃 MemTable。</param>
    /// <exception cref="ArgumentNullException"><paramref name="activeMemTable"/> 为 null 时抛出。</exception>
    internal void InitializeActiveMemTable(MemTable activeMemTable)
    {
        ArgumentNullException.ThrowIfNull(activeMemTable);
        lock (_lock)
        {
            _activeMemTable = activeMemTable;
            _sealingMemTables = EmptyMemTables;
            RepublishMemTablesLocked();
        }
    }

    /// <summary>
    /// 原子地接入 flush 产出的新段，并把活跃 MemTable 替换为新的空实例——两步在同一次
    /// <see cref="Volatile.Write"/> 中完成。这样 flush 期间旧 MemTable 一直是活跃查询源，
    /// 发布瞬间数据从 MemTable 原子地转移到 segment，查询绝不会看到"两处都无"（修 #190）
    /// 或"两处都有"。Phase 1 在 <c>_writeSync</c> 内同步调用，sealing 列表保持为空。
    /// </summary>
    /// <param name="path">新段文件的完整路径。</param>
    /// <param name="freshActive">替换进来的新空 MemTable。</param>
    /// <returns>已打开的新段 <see cref="SegmentReader"/> 实例。</returns>
    /// <exception cref="ArgumentNullException">任何参数为 null 时抛出。</exception>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    internal SegmentReader AddSegmentAndSwapActive(string path, MemTable freshActive)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(freshActive);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var reader = SegmentReader.Open(path, _readerOptions);
            long segId = reader.Header.SegmentId;
            SegmentReaderLeaseState[] replaced = _readerById.TryGetValue(segId, out var oldReader)
                ? [oldReader]
                : [];
            _readerById[segId] = new SegmentReaderLeaseState(reader);
            _indexById.Remove(segId); // 同 id 段被替换：作废旧索引缓存。
            _activeMemTable = freshActive;
            RebuildSnapshotsLocked(replaced);
            return reader;
        }
    }

    /// <summary>当前活跃 MemTable（无锁读取，来自已发布快照）。</summary>
    internal MemTable? ActiveMemTable => CurrentSnapshot.ActiveMemTable;

    /// <summary>
    /// Phase 2 密封：把当前活跃 MemTable 换成新空实例，并把旧实例追加到 sealing 列表，
    /// 一次原子发布。返回被密封的旧 MemTable，供 flush 泵在锁外编码落盘。发布后查询把
    /// sealing 表一并合并，保证 flush 期间旧数据恰好可见一次（无漏无重）。
    /// <para>此方法 O(1)，不做任何 I/O；由 <c>Tsdb</c> 在 <c>_writeSync</c> 内调用。</para>
    /// </summary>
    /// <param name="freshActive">替换进来的新空 MemTable。</param>
    /// <returns>被密封的旧活跃 MemTable；当前无活跃 MemTable 时返回 null（不应发生）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="freshActive"/> 为 null 时抛出。</exception>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    internal MemTable? SealActiveAndSwap(MemTable freshActive)
    {
        ArgumentNullException.ThrowIfNull(freshActive);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var sealedTable = _activeMemTable;
            _activeMemTable = freshActive;
            if (sealedTable is not null)
            {
                var next = new List<MemTable>(_sealingMemTables.Count + 1);
                next.AddRange(_sealingMemTables);
                next.Add(sealedTable);
                _sealingMemTables = next;
            }

            RepublishMemTablesLocked();
            return sealedTable;
        }
    }

    /// <summary>
    /// Phase 2 完成发布：接入 flush 产出的新段，并把对应的已密封 MemTable 从 sealing 列表移除，
    /// 一次原子发布。发布前查询看到 {…sealing(含该表) + 旧段}，发布后看到 {…sealing(不含) + 含新段}，
    /// 数据在切换瞬间从 sealing MemTable 原子转移到 segment（无漏无重）。
    /// <para>由 flush 泵在锁外调用（仅取 SegmentManager 自身的 <c>_lock</c>，不涉及 <c>_writeSync</c>）。</para>
    /// </summary>
    /// <param name="path">新段文件的完整路径。</param>
    /// <param name="sealedTable">已完成 flush 的 sealing MemTable（将被移除）。</param>
    /// <returns>已打开的新段 <see cref="SegmentReader"/> 实例。</returns>
    /// <exception cref="ArgumentNullException">任何参数为 null 时抛出。</exception>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    internal SegmentReader PublishSegmentAndReleaseSealed(string path, MemTable sealedTable)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sealedTable);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var reader = SegmentReader.Open(path, _readerOptions);
            long segId = reader.Header.SegmentId;
            SegmentReaderLeaseState[] replaced = _readerById.TryGetValue(segId, out var oldReader)
                ? [oldReader]
                : [];
            _readerById[segId] = new SegmentReaderLeaseState(reader);
            _indexById.Remove(segId); // 同 id 段被替换：作废旧索引缓存。
            RemoveSealedLocked(sealedTable);
            RebuildSnapshotsLocked(replaced);
            return reader;
        }
    }

    /// <summary>
    /// Phase 2 关闭路径：把已密封 MemTable 从 sealing 列表移除并重新发布（不接入新段）。
    /// 用于 flush 失败或 Dispose 排空时保持不变式一致。
    /// </summary>
    /// <param name="sealedTable">要移除的 sealing MemTable。</param>
    internal void ReleaseSealed(MemTable sealedTable)
    {
        ArgumentNullException.ThrowIfNull(sealedTable);
        lock (_lock)
        {
            if (_disposed)
                return;
            if (RemoveSealedLocked(sealedTable))
                RepublishMemTablesLocked();
        }
    }

    private bool RemoveSealedLocked(MemTable sealedTable)
    {
        int idx = -1;
        for (int i = 0; i < _sealingMemTables.Count; i++)
        {
            if (ReferenceEquals(_sealingMemTables[i], sealedTable))
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
            return false;

        if (_sealingMemTables.Count == 1)
        {
            _sealingMemTables = EmptyMemTables;
        }
        else
        {
            var next = new List<MemTable>(_sealingMemTables.Count - 1);
            for (int i = 0; i < _sealingMemTables.Count; i++)
            {
                if (i != idx)
                    next.Add(_sealingMemTables[i]);
            }
            _sealingMemTables = next;
        }

        return true;
    }

    /// <summary>当前 sealing（正在 flush）的 MemTable 数量（无锁读取）。</summary>
    internal int SealingCount => CurrentSnapshot.SealingMemTables.Count;

    /// <summary>
    /// 在不改动段集合的前提下，用当前 memtable 字段重新发布一份快照（调用方必须持有 <c>_lock</c>）。
    /// 复用现有 reader 状态，不重建段索引（memtable 切换不影响段索引）。
    /// </summary>
    private void RepublishMemTablesLocked()
    {
        var current = CurrentSnapshot;
        var newSnapshot = new SegmentManagerSnapshot(
            current.Index,
            SnapshotReaderStatesLocked(),
            _activeMemTable,
            _sealingMemTables);
        Volatile.Write(ref _snapshot, newSnapshot);
        // 段读取器未变化，旧快照无需 Retire 任何 reader（沿用共享 SegmentReaderLeaseState 实例）。
    }

    private SegmentReaderLeaseState[] SnapshotReaderStatesLocked()
        => _readerById
            .OrderBy(static kvp => kvp.Key)
            .Select(static kvp => kvp.Value)
            .ToArray();

    /// <summary>
    /// 重建索引快照（调用方必须持有 <c>_lock</c>）。
    /// </summary>
    private void RebuildSnapshotsLocked(IReadOnlyList<SegmentReaderLeaseState>? readersToDispose = null)
    {
        RebuildSnapshotsUnsafe(readersToDispose);
    }

    /// <summary>
    /// 重建索引快照（单线程初始化时调用，无需持有锁）。
    /// 增量复用 <see cref="_indexById"/> 缓存：未变段的 <see cref="SegmentIndex"/> 直接复用，
    /// 仅新加入的段调用一次 <see cref="SegmentIndex.Build"/>；已移除段从缓存修剪（C7）。
    /// </summary>
    private void RebuildSnapshotsUnsafe(IReadOnlyList<SegmentReaderLeaseState>? readersToDispose = null)
    {
        var ordered = _readerById
            .OrderBy(static kvp => kvp.Key)
            .ToList();

        // 修剪缓存中不再存在的段索引（swap/drop 移除的段）。
        if (_indexById.Count > ordered.Count)
        {
            var liveIds = new HashSet<long>(ordered.Count);
            foreach (var (segId, _) in ordered)
                liveIds.Add(segId);

            List<long>? staleIds = null;
            foreach (var cachedId in _indexById.Keys)
            {
                if (!liveIds.Contains(cachedId))
                    (staleIds ??= []).Add(cachedId);
            }

            if (staleIds is not null)
                foreach (var staleId in staleIds)
                    _indexById.Remove(staleId);
        }

        var indices = new List<SegmentIndex>(ordered.Count);
        foreach (var (segId, state) in ordered)
        {
            if (!_indexById.TryGetValue(segId, out var index))
            {
                index = SegmentIndex.Build(state.Reader, segId);
                _indexById[segId] = index;
            }
            indices.Add(index);
        }

        var newIndex = new MultiSegmentIndex(indices);
        var newReaders = (IReadOnlyList<SegmentReaderLeaseState>)ordered.Select(static kvp => kvp.Value).ToArray();

        PublishSnapshotUnsafe(newIndex, newReaders, readersToDispose ?? Array.Empty<SegmentReaderLeaseState>());
    }

    private void PublishSnapshotUnsafe(
        MultiSegmentIndex index,
        IReadOnlyList<SegmentReaderLeaseState> readers,
        IReadOnlyList<SegmentReaderLeaseState> readersToDispose)
    {
        var oldSnapshot = CurrentSnapshot;
        var newSnapshot = new SegmentManagerSnapshot(index, readers, _activeMemTable, _sealingMemTables);
        Volatile.Write(ref _snapshot, newSnapshot);
        oldSnapshot.Retire(readersToDispose);
    }
}
