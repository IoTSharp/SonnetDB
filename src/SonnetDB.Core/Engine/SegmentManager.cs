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
    // 高位为关闭位，低位为已取得快照租约的查询数。Dispose 先关闭入口并 retire
    // 当前 reader；Tsdb 据此延后归还跨进程根目录 lease，直到所有在途查询归还租约。
    private long _snapshotLeaseAdmissionState;
    private readonly TaskCompletionSource _snapshotLeasesDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    // 段发布必须保持 SegmentId 顺序。字典仅作为锁内的可变索引；已发布快照持有独立的
    // 排序 reader 数组，因此可以原地更新而无需每次 flush/compaction 复制整个字典。
    // 失败时调用方恢复本次变更，快照仍保持原子性（M19 #124）。
    private SortedDictionary<long, SegmentReaderLeaseState> _readerById = new();
    // 每段 SegmentIndex 缓存：段内容不可变，SegmentIndex.Build 对固定 (reader, segId) 确定，
    // 故只在段首次加入时构建一次，add/swap/drop 时增量复用未变段的索引，避免每次全量重建（C7）。
    private Dictionary<long, SegmentIndex> _indexById = new();
    private MemTable? _activeMemTable;
    private IReadOnlyList<MemTable> _sealingMemTables = EmptyMemTables;
    private SegmentManagerSnapshot _snapshot = new(MultiSegmentIndex.Empty, Array.Empty<SegmentReaderLeaseState>());
    private bool _disposed;

    private const long SnapshotLeaseAdmissionClosed = long.MinValue;

    /// <summary>当前所有已打开的 <see cref="SegmentReader"/> 快照（按 SegmentId 升序）。</summary>
    public IReadOnlyList<SegmentReader> Readers => CurrentSnapshot.Readers;

    /// <summary>当前索引快照（无锁读取，与 <see cref="Readers"/> 来自同一个已发布快照）。</summary>
    public MultiSegmentIndex Index => CurrentSnapshot.Index;

    /// <summary>当前已加载的段数量。</summary>
    public int SegmentCount => Index.SegmentCount;

    /// <summary>当前缓存的不可变段索引数量，仅供内部诊断与回归测试。</summary>
    internal int CachedIndexCount
    {
        get
        {
            lock (_lock)
                return _indexById.Count;
        }
    }

    /// <summary>
    /// 在构建新段索引前调用的测试钩子。生产路径保持为 null；仅用于验证索引构建失败时的发布原子性。
    /// </summary>
    internal Action<SegmentReader, long>? BeforeSegmentIndexBuildTestHook { get; set; }

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
        => Open(rootDirectory, readerOptions, segmentTemporarySuffix: null);

    /// <summary>
    /// 扫描 <paramref name="rootDirectory"/> 下 segments/ 子目录，打开所有已落盘段文件，
    /// 并按指定的临时文件后缀清理被替换清单抑制的残留文件。
    /// </summary>
    /// <param name="rootDirectory">数据库根目录路径。</param>
    /// <param name="readerOptions">段读取选项；为 null 时使用 <see cref="SegmentReaderOptions.Default"/>。</param>
    /// <param name="segmentTemporarySuffix">
    /// 段写入器临时文件后缀；为 null 时使用 <see cref="SegmentWriterOptions.Default.TempFileSuffix"/>。
    /// 启动清理仅针对替换清单抑制的段及其 sidecar，不会扫描或删除无关文件。
    /// </param>
    /// <returns>已初始化的 <see cref="SegmentManager"/> 实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rootDirectory"/> 为 null 时抛出。</exception>
    public static SegmentManager Open(
        string rootDirectory,
        SegmentReaderOptions? readerOptions,
        string? segmentTemporarySuffix)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);

        string temporarySuffix = segmentTemporarySuffix ?? SegmentWriterOptions.Default.TempFileSuffix;

        var manager = new SegmentManager(readerOptions);
        var readers = new SortedDictionary<long, SegmentReaderLeaseState>();

        try
        {
            var suppressedSegmentIds = SegmentReplacementManifest
                .LoadForRoot(rootDirectory)
                .GetSegmentIdsToSuppress(rootDirectory, readerOptions);

            var segments = TsdbPaths.EnumerateSegments(rootDirectory)
                .Where(t => !suppressedSegmentIds.Contains(t.SegmentId))
                .OrderBy(static t => t.SegmentId)
                .ToList();

            foreach (var (segId, path) in segments)
            {
                // 未被 replacement manifest 抑制的 canonical .SDBSEG 都是已经发布的
                // 数据。启动时静默跳过损坏段会让已回收的 WAL 无法补回其数据，造成
                // 不可见的数据丢失；未提交的 flush/compaction 候选在到达这里前已由
                // publication marker 或 replacement manifest 排除。
                try
                {
                    var reader = SegmentReader.Open(path, readerOptions);
                    readers[segId] = new SegmentReaderLeaseState(reader);
                }
                catch (SegmentCorruptedException) when (IsZeroLengthOrphan(path))
                {
                    // 兼容旧版本/管理工具留下的零字节占位文件。它不能包含已发布数据；
                    // 非空 canonical 文件仍必须抛出，避免把真实损坏误当作临时文件。
                }
            }

            // S12：清理被 manifest 抑制、但因上次删除失败（Windows 文件锁等）残留在盘上的死段文件及其
            // 向量/聚合索引 sidecar。此前这些孤儿文件被 Open 跳过加载后就再无人删除，永久泄漏磁盘；
            // 现在启动时重试清理。删除仍失败的文件下次启动会再次尝试（幂等）。
            CleanupSuppressedOrphans(rootDirectory, suppressedSegmentIds, temporarySuffix);

            var snapshot = manager.CreatePreparedSnapshotLocked(
                readers,
                invalidatedSegmentId: null,
                manager._activeMemTable,
                manager._sealingMemTables,
                out var indexes);
            manager.CommitPreparedSnapshotLocked(
                readers,
                indexes,
                snapshot,
                manager._activeMemTable,
                manager._sealingMemTables,
                Array.Empty<SegmentReaderLeaseState>());
            return manager;
        }
        catch
        {
            DisposeUnpublishedReaderStates(readers.Values);
            throw;
        }
    }

    private static bool IsZeroLengthOrphan(string path)
    {
        try
        {
            return new FileInfo(path).Length == 0L;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
    }

    /// <summary>
    /// 清理数据库启动时遗留的 SegmentWriter 临时文件。
    /// </summary>
    /// <remarks>
    /// 此方法只匹配 <c>&lt;16 位十六进制&gt;.SDBSEG&lt;后缀&gt;</c> 文件，并递归处理
    /// <c>segments/</c> 下的目录（包括分层 v2 布局）。调用方应在取得数据库根目录所有权后、
    /// 扫描段文件前调用。直接调用 <see cref="Open(string, SegmentReaderOptions?, string?)"/>
    /// 不会执行此全量清理，因此不会误删无关临时文件。
    /// </remarks>
    /// <param name="rootDirectory">数据库根目录路径。</param>
    /// <param name="segmentTemporarySuffix">段写入器使用的临时文件后缀。</param>
    internal static void CleanupAllSegmentTemporaryFiles(
        string rootDirectory,
        string segmentTemporarySuffix)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        ArgumentNullException.ThrowIfNull(segmentTemporarySuffix);

        if (!IsSafeTemporarySuffix(segmentTemporarySuffix))
            return;

        string segmentsDirectory;
        try
        {
            segmentsDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(TsdbPaths.SegmentsDir(rootDirectory)));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return;
        }

        if (segmentsDirectory.Length == 0 || !Directory.Exists(segmentsDirectory))
            return;

        // 不跟随 junction / symlink 目录，避免数据库目录中的重解析点把清理范围带到根目录之外。
        if (IsReparsePoint(segmentsDirectory))
            return;

        string comparisonRoot = segmentsDirectory + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(segmentsDirectory);

        while (pendingDirectories.Count > 0)
        {
            string directory = pendingDirectories.Pop();
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (string file in files)
            {
                if (!IsPathWithinRoot(file, comparisonRoot, comparison)
                    || !TryParseSegmentTemporaryFileName(
                        Path.GetFileName(file),
                        segmentTemporarySuffix,
                        comparison,
                        out _))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // 临时文件仍被占用时留待下次启动重试；不影响已发布段的可见性。
                }
                catch (UnauthorizedAccessException)
                {
                    // 权限/只读等瞬时错误同上。
                }
            }

            string[] childDirectories;
            try
            {
                childDirectories = Directory.EnumerateDirectories(
                    directory,
                    "*",
                    SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (string childDirectory in childDirectories)
            {
                if (!IsPathWithinRoot(childDirectory, comparisonRoot, comparison)
                    || IsReparsePoint(childDirectory))
                {
                    continue;
                }

                pendingDirectories.Push(childDirectory);
            }
        }
    }

    private static bool IsSafeTemporarySuffix(string suffix)
    {
        if (suffix.Length == 0 || Path.IsPathRooted(suffix))
            return false;

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            if (suffix.IndexOf(invalid) >= 0)
                return false;
        }

        return suffix.IndexOf(Path.DirectorySeparatorChar) < 0
            && suffix.IndexOf(Path.AltDirectorySeparatorChar) < 0;
    }

    private static bool IsPathWithinRoot(
        string path,
        string rootWithSeparator,
        StringComparison comparison)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(rootWithSeparator, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            // 无法确认目录目标时宁可跳过，避免把未知路径当作数据库子目录递归。
            return true;
        }
    }

    private static bool TryParseSegmentTemporaryFileName(
        string fileName,
        string temporarySuffix,
        StringComparison comparison,
        out long segmentId)
    {
        segmentId = 0;
        if (fileName.Length <= temporarySuffix.Length
            || !fileName.EndsWith(temporarySuffix, comparison))
        {
            return false;
        }

        string segmentFileName = fileName[..^temporarySuffix.Length];
        return TsdbPaths.TryParseSegmentId(segmentFileName, out segmentId);
    }

    /// <summary>
    /// 删除被 manifest 抑制的死段在盘上的残留文件（段主文件 + 向量 / 聚合索引 sidecar）。
    /// 逐文件尝试删除，失败（如仍被占用）不抛出——下次启动会再次重试，保证最终清理（S12）。
    /// </summary>
    private static void CleanupSuppressedOrphans(
        string rootDirectory,
        IReadOnlySet<long> suppressedSegmentIds,
        string segmentTemporarySuffix)
    {
        ArgumentNullException.ThrowIfNull(segmentTemporarySuffix);

        if (suppressedSegmentIds.Count == 0)
            return;

        foreach (long segId in suppressedSegmentIds)
        {
            if (segId <= 0)
                continue;

            foreach (string artifactPath in TsdbPaths.SegmentArtifactPaths(rootDirectory, segId))
            {
                TryDeleteSuppressedArtifact(artifactPath);

                // SegmentWriter 以 "<artifactPath><TempFileSuffix>" 写入临时文件。
                // 只处理与已知 artifact 同目录的候选路径，避免恶意/误配置后缀把清理范围
                // 扩展到 segments/ 之外；无关的 .tmp 文件仍保持不变。
                if (segmentTemporarySuffix.Length == 0)
                    continue;

                string temporaryPath = artifactPath + segmentTemporarySuffix;
                string? artifactDirectory = Path.GetDirectoryName(artifactPath);
                string? temporaryDirectory = Path.GetDirectoryName(temporaryPath);
                if (string.IsNullOrEmpty(artifactDirectory)
                    || !string.Equals(
                        artifactDirectory,
                        temporaryDirectory,
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                {
                    continue;
                }

                TryDeleteSuppressedArtifact(temporaryPath);
            }
        }
    }

    private static void TryDeleteSuppressedArtifact(string artifactPath)
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
            SegmentReaderLeaseState? previousReader = null;
            try
            {
                long segId = reader.Header.SegmentId;
                SegmentReaderLeaseState[] replaced = _readerById.TryGetValue(segId, out previousReader)
                    ? [previousReader]
                    : [];
                _readerById[segId] = new SegmentReaderLeaseState(reader);

                var snapshot = CreatePreparedSnapshotLocked(
                    _readerById,
                    segId,
                    _activeMemTable,
                    _sealingMemTables,
                    out var nextIndexes);
                CommitPreparedSnapshotLocked(
                    _readerById,
                    nextIndexes,
                    snapshot,
                    _activeMemTable,
                    _sealingMemTables,
                    replaced);
                return reader;
            }
            catch
            {
                if (previousReader is null)
                    _readerById.Remove(reader.Header.SegmentId);
                else
                    _readerById[reader.Header.SegmentId] = previousReader;
                DisposeUnpublishedReader(reader);
                throw;
            }
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

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var newReader = SegmentReader.Open(addedPath, _readerOptions);
            var previousReaders = new Dictionary<long, SegmentReaderLeaseState?>(removeIds.Count + 1);
            try
            {
                long newSegId = newReader.Header.SegmentId;
                var toDispose = new List<SegmentReaderLeaseState>(removeIds.Count + 1);
                foreach (long segId in removeIds)
                {
                    if (!previousReaders.ContainsKey(segId))
                        previousReaders[segId] = _readerById.TryGetValue(segId, out var existing) ? existing : null;

                    if (_readerById.TryGetValue(segId, out var old))
                    {
                        _readerById.Remove(segId);
                        AddReaderToRetire(toDispose, old);
                    }
                }

                if (!previousReaders.ContainsKey(newSegId))
                    previousReaders[newSegId] = _readerById.TryGetValue(newSegId, out var previous) ? previous : null;

                // 调用方可能未在 removeIds 中包含同 ID 的旧段；替换时也必须让旧 reader 在
                // 旧快照的最后一个租约释放后关闭，不能只覆盖字典项。
                if (_readerById.TryGetValue(newSegId, out var replaced))
                    AddReaderToRetire(toDispose, replaced);

                _readerById[newSegId] = new SegmentReaderLeaseState(newReader);
                var snapshot = CreatePreparedSnapshotLocked(
                    _readerById,
                    newSegId,
                    _activeMemTable,
                    _sealingMemTables,
                    out var nextIndexes);
                CommitPreparedSnapshotLocked(
                    _readerById,
                    nextIndexes,
                    snapshot,
                    _activeMemTable,
                    _sealingMemTables,
                    toDispose);
                return newReader;
            }
            catch
            {
                foreach (var (segId, previous) in previousReaders)
                {
                    if (previous is null)
                        _readerById.Remove(segId);
                    else
                        _readerById[segId] = previous;
                }
                DisposeUnpublishedReader(newReader);
                throw;
            }
        }
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
                    AddReaderToRetire(toDispose, old);
                }
            }

            if (toDispose.Count == 0)
                return Array.Empty<SegmentReader>();

            try
            {
                var snapshot = CreatePreparedSnapshotLocked(
                    _readerById,
                    invalidatedSegmentId: null,
                    _activeMemTable,
                    _sealingMemTables,
                    out var nextIndexes);
                CommitPreparedSnapshotLocked(
                    _readerById,
                    nextIndexes,
                    snapshot,
                    _activeMemTable,
                    _sealingMemTables,
                    toDispose);
            }
            catch
            {
                foreach (var state in toDispose)
                    _readerById[state.Reader.Header.SegmentId] = state;
                throw;
            }
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
            try
            {
                var snapshot = CreatePreparedSnapshotLocked(
                    _readerById,
                    invalidatedSegmentId: null,
                    _activeMemTable,
                    _sealingMemTables,
                    out var nextIndexes);
                CommitPreparedSnapshotLocked(
                    _readerById,
                    nextIndexes,
                    snapshot,
                    _activeMemTable,
                    _sealingMemTables,
                    [reader]);
            }
            catch
            {
                _readerById[segmentId] = reader;
                throw;
            }
            return true;
        }
    }

    /// <summary>
    /// 停止新的读快照并关闭全部 <see cref="SegmentReader"/>。
    /// 已取得的快照会继续持有其 reader，直到自行归还租约。
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            // 关闭入口后，已经成功取得 admission 的查询仍可从当前快照完成读取。
            // 当前快照会立刻 retire，但其中 reader 会由 lease 延迟释放；Tsdb 据此不会
            // 在旧 reader 仍被流式查询使用时过早归还根目录的跨进程所有权。
            CloseSnapshotLeaseAdmission();
            _disposed = true;
            var readersToDispose = new List<SegmentReaderLeaseState>(_readerById.Values);
            _readerById.Clear();
            var nextIndexes = new Dictionary<long, SegmentIndex>();
            var snapshot = new SegmentManagerSnapshot(
                MultiSegmentIndex.Empty,
                Array.Empty<SegmentReaderLeaseState>(),
                _activeMemTable,
                _sealingMemTables);

            CommitPreparedSnapshotLocked(
                _readerById,
                nextIndexes,
                snapshot,
                _activeMemTable,
                _sealingMemTables,
                readersToDispose);
        }
    }

    /// <summary>
    /// 关闭 manager，并返回所有已取得的读快照均已归还 reader lease 时完成的任务。
    /// 供 <see cref="Tsdb"/> 延后释放根目录跨进程所有权，避免流式查询与新实例并存访问段文件。
    /// </summary>
    internal Task DisposeAndGetSnapshotLeaseDrainTask()
    {
        Dispose();
        long state = Volatile.Read(ref _snapshotLeaseAdmissionState);
        return (state & ~SnapshotLeaseAdmissionClosed) == 0
            ? Task.CompletedTask
            : _snapshotLeasesDrained.Task;
    }

    internal SegmentManagerSnapshotLease AcquireSnapshot(
        int maxReaders = int.MaxValue, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryEnterSnapshotLeaseAdmission())
                throw new ObjectDisposedException(GetType().Name);

            var snapshot = CurrentSnapshot;
            // 在逐个 reader 取得租约之前拒绝过大诊断快照，避免有界采样隐含全库遍历。
            if (snapshot.Readers.Count > maxReaders)
            {
                ExitSnapshotLeaseAdmission();
                throw new SonnetDB.Exceptions.TimeSeriesPreflightBudgetException("已加载段数量超过 MaxSources。");
            }
            if (snapshot.TryAcquire())
                return new SegmentManagerSnapshotLease(snapshot, this);

            ExitSnapshotLeaseAdmission();
        }
    }

    internal void ReleaseSnapshotLease(SegmentManagerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            snapshot.Release();
        }
        finally
        {
            ExitSnapshotLeaseAdmission();
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
            var snapshot = CreateMemTableSnapshotLocked(activeMemTable, EmptyMemTables);
            CommitMemTableSnapshotLocked(activeMemTable, EmptyMemTables, snapshot);
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
            SegmentReaderLeaseState? previousReader = null;
            try
            {
                long segId = reader.Header.SegmentId;
                SegmentReaderLeaseState[] replaced = _readerById.TryGetValue(segId, out previousReader)
                    ? [previousReader]
                    : [];
                _readerById[segId] = new SegmentReaderLeaseState(reader);

                var snapshot = CreatePreparedSnapshotLocked(
                    _readerById,
                    segId,
                    freshActive,
                    _sealingMemTables,
                    out var nextIndexes);
                CommitPreparedSnapshotLocked(
                    _readerById,
                    nextIndexes,
                    snapshot,
                    freshActive,
                    _sealingMemTables,
                    replaced);
                return reader;
            }
            catch
            {
                if (previousReader is null)
                    _readerById.Remove(reader.Header.SegmentId);
                else
                    _readerById[reader.Header.SegmentId] = previousReader;
                DisposeUnpublishedReader(reader);
                throw;
            }
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
            IReadOnlyList<MemTable> nextSealing = _sealingMemTables;
            if (sealedTable is not null)
            {
                var next = new List<MemTable>(_sealingMemTables.Count + 1);
                next.AddRange(_sealingMemTables);
                next.Add(sealedTable);
                nextSealing = next;
            }

            var snapshot = CreateMemTableSnapshotLocked(freshActive, nextSealing);
            CommitMemTableSnapshotLocked(freshActive, nextSealing, snapshot);
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
            SegmentReaderLeaseState? previousReader = null;
            try
            {
                long segId = reader.Header.SegmentId;
                SegmentReaderLeaseState[] replaced = _readerById.TryGetValue(segId, out previousReader)
                    ? [previousReader]
                    : [];
                _readerById[segId] = new SegmentReaderLeaseState(reader);
                var nextSealing = RemoveSealed(_sealingMemTables, sealedTable, out bool removed);
                if (!removed)
                {
                    throw new InvalidOperationException(
                        "Cannot publish a flushed segment because its sealing MemTable is no longer registered.");
                }

                var snapshot = CreatePreparedSnapshotLocked(
                    _readerById,
                    segId,
                    _activeMemTable,
                    nextSealing,
                    out var nextIndexes);
                CommitPreparedSnapshotLocked(
                    _readerById,
                    nextIndexes,
                    snapshot,
                    _activeMemTable,
                    nextSealing,
                    replaced);
                return reader;
            }
            catch
            {
                if (previousReader is null)
                    _readerById.Remove(reader.Header.SegmentId);
                else
                    _readerById[reader.Header.SegmentId] = previousReader;
                DisposeUnpublishedReader(reader);
                throw;
            }
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

            var nextSealing = RemoveSealed(_sealingMemTables, sealedTable, out bool removed);
            if (!removed)
                return;

            var snapshot = CreateMemTableSnapshotLocked(_activeMemTable, nextSealing);
            CommitMemTableSnapshotLocked(_activeMemTable, nextSealing, snapshot);
        }
    }

    private static IReadOnlyList<MemTable> RemoveSealed(
        IReadOnlyList<MemTable> sealingMemTables,
        MemTable sealedTable,
        out bool removed)
    {
        int idx = -1;
        for (int i = 0; i < sealingMemTables.Count; i++)
        {
            if (ReferenceEquals(sealingMemTables[i], sealedTable))
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
        {
            removed = false;
            return sealingMemTables;
        }

        removed = true;
        if (sealingMemTables.Count == 1)
            return EmptyMemTables;

        var next = new List<MemTable>(sealingMemTables.Count - 1);
        for (int i = 0; i < sealingMemTables.Count; i++)
        {
            if (i != idx)
                next.Add(sealingMemTables[i]);
        }

        return next;
    }

    /// <summary>当前 sealing（正在 flush）的 MemTable 数量（无锁读取）。</summary>
    internal int SealingCount => CurrentSnapshot.SealingMemTables.Count;

    /// <summary>
    /// 为纯 MemTable 变更准备快照。所有可能分配都发生在字段提交前，避免异常后内部字段与已发布快照脱节。
    /// </summary>
    private SegmentManagerSnapshot CreateMemTableSnapshotLocked(
        MemTable? activeMemTable,
        IReadOnlyList<MemTable> sealingMemTables)
    {
        var current = CurrentSnapshot;
        return new SegmentManagerSnapshot(
            current.Index,
            current.ReaderStates,
            activeMemTable,
            sealingMemTables,
            current.Readers);
    }

    /// <summary>
    /// 发布已准备好的纯 MemTable 快照。调用方必须在进入此方法前完成所有可能抛出的工作。
    /// </summary>
    private void CommitMemTableSnapshotLocked(
        MemTable? activeMemTable,
        IReadOnlyList<MemTable> sealingMemTables,
        SegmentManagerSnapshot snapshot)
    {
        var oldSnapshot = CurrentSnapshot;
        _activeMemTable = activeMemTable;
        _sealingMemTables = sealingMemTables;
        Volatile.Write(ref _snapshot, snapshot);
        // reader 状态由新旧快照共享，因此只退休旧快照以拒绝延迟 Acquire，不退休任何 reader。
        oldSnapshot.Retire(Array.Empty<SegmentReaderLeaseState>());
    }

    /// <summary>
    /// 针对候选 reader 集合构建完整的索引缓存和快照，但不修改任何已发布状态。
    /// 未被 <paramref name="invalidatedSegmentId"/> 标记且仍存在的段复用旧索引；其余段在候选集合中构建。
    /// </summary>
    private SegmentManagerSnapshot CreatePreparedSnapshotLocked(
        SortedDictionary<long, SegmentReaderLeaseState> candidateReaders,
        long? invalidatedSegmentId,
        MemTable? activeMemTable,
        IReadOnlyList<MemTable> sealingMemTables,
        out Dictionary<long, SegmentIndex> candidateIndexes)
    {
        var indices = new SegmentIndex[candidateReaders.Count];
        var readerStates = new SegmentReaderLeaseState[candidateReaders.Count];
        var nextIndexes = new Dictionary<long, SegmentIndex>(candidateReaders.Count);
        int position = 0;

        foreach (var (segId, state) in candidateReaders)
        {
            SegmentIndex index;
            if (invalidatedSegmentId != segId
                && _indexById.TryGetValue(segId, out var cachedIndex))
            {
                index = cachedIndex;
            }
            else
            {
                BeforeSegmentIndexBuildTestHook?.Invoke(state.Reader, segId);
                index = SegmentIndex.Build(state.Reader, segId);
            }

            nextIndexes.Add(segId, index);
            indices[position] = index;
            readerStates[position] = state;
            position++;
        }

        var multiIndex = new MultiSegmentIndex(indices);
        candidateIndexes = nextIndexes;
        return new SegmentManagerSnapshot(multiIndex, readerStates, activeMemTable, sealingMemTables);
    }

    /// <summary>
    /// 提交已经完整构建的候选集合。这里不做索引构建或快照分配，因此不会在半更新状态下抛出。
    /// </summary>
    private void CommitPreparedSnapshotLocked(
        SortedDictionary<long, SegmentReaderLeaseState> candidateReaders,
        Dictionary<long, SegmentIndex> candidateIndexes,
        SegmentManagerSnapshot snapshot,
        MemTable? activeMemTable,
        IReadOnlyList<MemTable> sealingMemTables,
        IReadOnlyList<SegmentReaderLeaseState> readersToDispose)
    {
        var oldSnapshot = CurrentSnapshot;
        _readerById = candidateReaders;
        _indexById = candidateIndexes;
        _activeMemTable = activeMemTable;
        _sealingMemTables = sealingMemTables;
        Volatile.Write(ref _snapshot, snapshot);
        oldSnapshot.Retire(readersToDispose);
    }

    /// <summary>
    /// 为一次读取取得快照 admission。成功 CAS 是查询与 Dispose 的线性化点：先成功的
    /// 查询可完成，后开始的查询在关闭期间被拒绝。
    /// </summary>
    private bool TryEnterSnapshotLeaseAdmission()
    {
        while (true)
        {
            long observed = Volatile.Read(ref _snapshotLeaseAdmissionState);
            if ((observed & SnapshotLeaseAdmissionClosed) != 0)
                return false;

            if (Interlocked.CompareExchange(
                    ref _snapshotLeaseAdmissionState,
                    observed + 1,
                    observed) == observed)
            {
                return true;
            }
        }
    }

    private void ExitSnapshotLeaseAdmission()
    {
        long remaining = Interlocked.Decrement(ref _snapshotLeaseAdmissionState);
        if ((remaining & SnapshotLeaseAdmissionClosed) != 0
            && (remaining & ~SnapshotLeaseAdmissionClosed) == 0)
        {
            _snapshotLeasesDrained.TrySetResult();
        }
    }

    private void CloseSnapshotLeaseAdmission()
    {
        while (true)
        {
            long observed = Volatile.Read(ref _snapshotLeaseAdmissionState);
            if ((observed & SnapshotLeaseAdmissionClosed) != 0)
                return;

            if (Interlocked.CompareExchange(
                    ref _snapshotLeaseAdmissionState,
                    observed | SnapshotLeaseAdmissionClosed,
                    observed) == observed)
            {
                return;
            }
        }
    }

    private static void AddReaderToRetire(
        ICollection<SegmentReaderLeaseState> readersToRetire,
        SegmentReaderLeaseState reader)
    {
        foreach (var existing in readersToRetire)
        {
            if (ReferenceEquals(existing, reader))
                return;
        }

        readersToRetire.Add(reader);
    }

    private static void DisposeUnpublishedReader(SegmentReader reader)
    {
        try
        {
            reader.Dispose();
        }
        catch
        {
            // 保留原始构建异常；未发布 reader 的清理错误不能覆盖它。
        }
    }

    private static void DisposeUnpublishedReaderStates(IEnumerable<SegmentReaderLeaseState> readerStates)
    {
        foreach (var state in readerStates)
            state.Retire();
    }
}
