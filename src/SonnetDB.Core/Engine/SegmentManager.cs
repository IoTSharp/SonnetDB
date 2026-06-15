using SonnetDB.Storage.Segments;

namespace SonnetDB.Engine;

/// <summary>
/// 已打开的 <see cref="SegmentReader"/> 集合的所有者。
/// <list type="bullet">
///   <item><description>启动时扫描 segments/ 目录，构建初始集合 + 索引快照；</description></item>
///   <item><description>Flush 完成后，调用 <see cref="AddSegment"/> 接入新段，重建索引快照（原子替换）；</description></item>
///   <item><description>进程关闭或显式 Dispose 时关闭所有 <see cref="SegmentReader"/>。</description></item>
/// </list>
/// 线程安全：内部 lock 保护"重建+替换"，读取通过 volatile 字段做无锁读。
/// </summary>
public sealed class SegmentManager : IDisposable
{
    private readonly object _lock = new();
    private readonly SegmentReaderOptions? _readerOptions;
    private readonly Dictionary<long, SegmentReaderLeaseState> _readerById = new();
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

        manager.RebuildSnapshotsUnsafe();
        return manager;
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
    /// 重建索引快照（调用方必须持有 <c>_lock</c>）。
    /// </summary>
    private void RebuildSnapshotsLocked(IReadOnlyList<SegmentReaderLeaseState>? readersToDispose = null)
    {
        RebuildSnapshotsUnsafe(readersToDispose);
    }

    /// <summary>
    /// 重建索引快照（单线程初始化时调用，无需持有锁）。
    /// </summary>
    private void RebuildSnapshotsUnsafe(IReadOnlyList<SegmentReaderLeaseState>? readersToDispose = null)
    {
        var ordered = _readerById
            .OrderBy(static kvp => kvp.Key)
            .ToList();

        var indices = new List<SegmentIndex>(ordered.Count);
        foreach (var (segId, state) in ordered)
            indices.Add(SegmentIndex.Build(state.Reader, segId));

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
        var newSnapshot = new SegmentManagerSnapshot(index, readers);
        Volatile.Write(ref _snapshot, newSnapshot);
        oldSnapshot.Retire(readersToDispose);
    }
}
