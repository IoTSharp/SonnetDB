using SonnetDB.Catalog;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Segments;
using SonnetDB.Wal;

namespace SonnetDB.Engine;

/// <summary>
/// MemTable → Segment 的 Flush 协调器。串行运行，保证 pending 发布标记、段文件、独立 checkpoint、
/// WAL checkpoint、Roll WAL、旧段回收按恢复安全顺序可见。
/// </summary>
/// <remarks>
/// 崩溃恢复语义：
/// <list type="bullet">
///   <item><description>若段发布前崩溃 → WAL 仍含全部 record，replay 重建 MemTable。</description></item>
///   <item><description>若段 rename 后、独立 checkpoint 前崩溃 → pending 标记使重启删除孤立段，再完整 replay WAL，不产生重复点。</description></item>
///   <item><description>若独立 checkpoint 已完成 → checkpoint 覆盖对应 LSN，重启可安全加载段并跳过已落盘 WAL。</description></item>
///   <item><description>若 WAL checkpoint / Roll / 回收中崩溃 → 独立 checkpoint 已是恢复权威，遗留 WAL 仅会在后续 Flush 时回收。</description></item>
/// </list>
/// </remarks>
public sealed class FlushCoordinator
{
    private readonly TsdbOptions _options;

    /// <summary>
    /// 创建 <see cref="FlushCoordinator"/> 实例。
    /// </summary>
    /// <param name="options">引擎选项。</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null 时抛出。</exception>
    public FlushCoordinator(TsdbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// 执行一次 Flush：将 MemTable 写出为 Segment，持久化 checkpoint LSN，追加 WAL Checkpoint，Roll WAL，回收旧 WAL segment。
    /// </summary>
    /// <param name="memTable">要 Flush 的 MemTable 实例。</param>
    /// <param name="walSet">当前活跃的 WAL segment 集合管理器。</param>
    /// <param name="segmentId">本次生成 Segment 的唯一标识符（单调递增）。</param>
    /// <param name="tombstones">可选的 <see cref="TombstoneTable"/>；若非 null，Flush 前先持久化墓碑清单。</param>
    /// <param name="seriesCatalog">可选的 Series 目录；提供时会按 schema 为声明了向量索引的字段构建段内索引 section。</param>
    /// <param name="measurementCatalog">可选的 measurement schema 目录；需与 <paramref name="seriesCatalog"/> 一起提供。</param>
    /// <returns>
    /// Segment 构建结果；若 MemTable 为空则返回 null（不触碰 WAL，不创建 Segment）。
    /// </returns>
    /// <exception cref="ArgumentNullException">任何必选参数为 null 时抛出。</exception>
    public SegmentBuildResult? Flush(
        MemTable memTable,
        WalSegmentSet walSet,
        long segmentId,
        TombstoneTable? tombstones = null,
        SeriesCatalog? seriesCatalog = null,
        MeasurementCatalog? measurementCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(memTable);
        ArgumentNullException.ThrowIfNull(walSet);

        // 步骤 0：持久化墓碑清单（在 WriteSegment 之前；确保 checkpoint 后可安全回收含 delete 的旧 WAL segment）
        // 仅当有墓碑或清单文件已存在（需要更新/清空）时才写入，避免不必要的 I/O
        if (tombstones != null)
        {
            string manifestPath = TsdbPaths.TombstoneManifestPath(_options.RootDirectory);
            if (tombstones.Count > 0 || File.Exists(manifestPath))
                TombstoneManifestCodec.Save(manifestPath, tombstones.All);
        }

        // 步骤 1：检查 MemTable 是否为空
        if (memTable.PointCount == 0)
            return null;

        // 记录 flush 前的 lastLsn（用于 Checkpoint 记录）
        long lastLsnBeforeFlush = memTable.LastLsn;

        var vectorIndexes = VectorIndexBuildMap.BuildForSegment(memTable.SnapshotAll(), seriesCatalog, measurementCatalog);
        var result = WriteCommittedSegment(
            memTable,
            segmentId,
            lastLsnBeforeFlush,
            vectorIndexes);

        // 独立 checkpoint 已先完成；WAL checkpoint 以下均为回收优化，恢复仍以独立 checkpoint 为准。
        _ = walSet.AppendCheckpoint(lastLsnBeforeFlush);
        walSet.Sync();

        // 主动 Roll（确保 checkpoint 之前的数据归到一个已封存的 segment，
        //         使 active segment 的 startLsn 严格 > checkpointLsn，避免 RecycleUpTo 误删）
        walSet.Roll();

        // 回收已 checkpoint 的旧 WAL segment，但保留承载 checkpoint record 的最后一段。
        // 后续 pending Flush marker 必须能从前一个 durable checkpoint 的下一条 LSN 连续
        // 证明 WAL 可重放；若提前回收该 record，就无法区分合法 checkpoint 和丢失写入。
        // 下一次成功 Flush 会在更高数据 LSN 上回收这一个短暂保留段。
        walSet.RecycleUpTo(lastLsnBeforeFlush);

        // 注：MemTable 的清空不在此处进行。调用方（Tsdb.FlushNowLocked）在本方法返回后，
        // 通过 SegmentManager 一次原子发布"接入新段 + 换成新空 MemTable"，
        // 使 flush 期间旧数据始终恰好可见一次（修 #190）。

        return result;
    }

    /// <summary>
    /// Phase 2 泵专用 Flush：把已密封的不可变 MemTable 编码落盘并写 durable checkpoint 文件。
    /// <para>
    /// 前置：调用方（<c>Tsdb.SealAndEnqueueLocked</c>）已在 <c>_writeSync</c> 内完成
    /// <c>Roll()</c>，把被密封数据的 WAL 记录与 seal 之后的并发写入用 roll 边界隔开。
    /// 本方法不修改 WAL；调用方必须在新段成功接入读快照后调用
    /// <see cref="FinalizeSealedFlush"/>，再追加 WAL checkpoint 和回收旧段。
    /// </para>
    /// <para>在 flush 泵线程调用（锁外，不持有 <c>Tsdb._writeSync</c>）。</para>
    /// </summary>
    /// <param name="sealedTable">已密封的不可变 MemTable。</param>
    /// <param name="walSet">当前活跃的 WAL segment 集合管理器。</param>
    /// <param name="sealLsn">密封瞬间捕获的 LastLsn（= checkpoint 值 = 回收阈值）。</param>
    /// <param name="segmentId">为本次 flush 预分配的段 ID。</param>
    /// <param name="tombstones">可选墓碑表。</param>
    /// <param name="seriesCatalog">可选 series 目录（向量索引构建）。</param>
    /// <param name="measurementCatalog">可选 measurement 目录。</param>
    /// <returns>段构建结果；空表返回 null。</returns>
    public SegmentBuildResult? FlushSealed(
        MemTable sealedTable,
        WalSegmentSet walSet,
        long sealLsn,
        long segmentId,
        TombstoneTable? tombstones = null,
        SeriesCatalog? seriesCatalog = null,
        MeasurementCatalog? measurementCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(sealedTable);
        ArgumentNullException.ThrowIfNull(walSet);

        if (tombstones != null)
        {
            string manifestPath = TsdbPaths.TombstoneManifestPath(_options.RootDirectory);
            if (tombstones.Count > 0 || File.Exists(manifestPath))
                TombstoneManifestCodec.Save(manifestPath, tombstones.All);
        }

        if (sealedTable.PointCount == 0)
            return null;

        var vectorIndexes = VectorIndexBuildMap.BuildForSegment(sealedTable.SnapshotAll(), seriesCatalog, measurementCatalog);
        var result = WriteCommittedSegment(
            sealedTable,
            segmentId,
            sealLsn,
            vectorIndexes);

        return result;
    }

    /// <summary>
    /// 在已发布段成功接入读快照后，提交密封 Flush 对应的 WAL checkpoint 并回收旧 WAL。
    /// </summary>
    /// <param name="walSet">当前活跃的 WAL segment 集合管理器。</param>
    /// <param name="sealLsn">密封瞬间捕获的 LastLsn。</param>
    /// <exception cref="ArgumentNullException"><paramref name="walSet"/> 为 null 时抛出。</exception>
    internal void FinalizeSealedFlush(WalSegmentSet walSet, long sealLsn)
    {
        ArgumentNullException.ThrowIfNull(walSet);
        ArgumentOutOfRangeException.ThrowIfNegative(sealLsn);

        // 只有 SegmentManager 已成功接入并验证新段后，才允许 WAL checkpoint 越过 sealLsn。
        // 这样发布失败时 WAL 仍完整保留，可由下次启动回放恢复；并发写入位于 seal 时 Roll
        // 创建的新 active segment（startLsn > sealLsn），不会被本次回收触及。
        _ = walSet.AppendCheckpoint(sealLsn);
        walSet.Sync();
        walSet.RecycleUpTo(sealLsn);
    }

    private SegmentBuildResult WriteCommittedSegment(
        MemTable memTable,
        long segmentId,
        long checkpointLsn,
        IReadOnlyDictionary<SeriesFieldKey, SonnetDB.Catalog.VectorIndexDefinition>? vectorIndexes)
    {
        string walDirectory = TsdbPaths.WalDir(_options.RootDirectory);
        string publicationPath = WalSegmentLayout.FlushPublicationPath(walDirectory, segmentId);

        // 先写 marker 再让 SegmentWriter 执行 rename。若在 writer 内进程终止，重启路径可据此
        // 删除尚未被 checkpoint 承诺的段，而不是把它和 WAL replay 一起暴露给查询。
        FlushPublicationState publication = FlushPublicationFile.SavePending(
            publicationPath,
            segmentId,
            checkpointLsn);

        string segmentPath = TsdbPaths.SegmentPath(_options.RootDirectory, segmentId);
        // 分层段目录在首次创建时需要从叶目录向上刷新到第一个已存在的父目录。
        // 稳态下同一 bucket 会复用目录，此时只需刷新叶目录即可，避免每个 Flush
        // 都对 segments/v2、bucket group 及数据库根目录重复 fsync。
        IReadOnlyList<string> publicationDirectories =
            PrepareSegmentPublicationDirectories(segmentPath);
        var writer = new SegmentWriter(_options.SegmentWriterOptions);
        SegmentBuildResult result = writer.WriteFrom(memTable, segmentId, segmentPath, vectorIndexes);
        FlushSegmentPublicationDirectories(publicationDirectories);

        // checkpoint 是提交点：它同时绑定 segment id、长度和可跳过的 WAL LSN。
        WalCheckpointFile.Save(
            WalSegmentLayout.CheckpointPath(walDirectory),
            new WalCheckpointState(
                checkpointLsn,
                result.SegmentId,
                result.TotalBytes,
                DateTime.UtcNow.Ticks));

        // 下一次 Flush 覆盖单一 checkpoint 文件前，必须先把本段 marker 持久化为 Committed。
        // 否则“前一段未确认、后一段 checkpoint 已覆盖更大 LSN”无法证明前段数据是否也落盘。
        // checkpoint Save 已经刷新过 WAL 目录。提交 marker 的 rename 需要再刷新一次
        // 目录以建立“checkpoint 后 marker”顺序；清理 marker 可省略目录 fsync，
        // 因为清理失败/掉电只会留下可安全重试的 Committed marker。
        FlushPublicationFile.MarkCommittedRequired(
            publicationPath,
            publication,
            flushDirectory: false);
        DirectoryFsync.FlushRequired(walDirectory);

        // 清理失败只留下已提交 marker；checkpoint 和 marker 已完成，不得把成功 flush 变成失败。
        FlushPublicationFile.TryClearCommitted(publicationPath, flushDirectory: false);
        return result;
    }

    private IReadOnlyList<string> PrepareSegmentPublicationDirectories(string segmentPath)
    {
        IReadOnlyList<string> chain = GetSegmentPublicationDirectoryChain(
            _options.RootDirectory,
            segmentPath);

        // Directory.CreateDirectory 创建整条缺失链；记录最靠近叶目录的已存在父目录，
        // 这样发布后只需刷新受本次创建影响的目录项以及段文件所在叶目录。
        int firstExisting = chain.Count - 1;
        for (int i = 0; i < chain.Count; i++)
        {
            if (Directory.Exists(chain[i]))
            {
                firstExisting = i;
                break;
            }
        }

        Directory.CreateDirectory(chain[0]);
        return chain.Take(firstExisting + 1).ToArray();
    }

    private static void FlushSegmentPublicationDirectories(IReadOnlyList<string> directories)
    {
        foreach (string directory in directories)
        {
            // 新建分层 bucket 时，仅 fsync 叶目录不能保证其在父目录中的目录项也已落盘。
            // 稳态 bucket 已存在，因此只刷新叶目录即可保证段文件 rename 可恢复。
            DirectoryFsync.FlushRequired(directory);
        }
    }

    /// <summary>
    /// 获取最终段发布后需要从叶目录向数据库根目录依次刷新的目录链。
    /// </summary>
    /// <param name="rootDirectory">数据库根目录。</param>
    /// <param name="segmentPath">最终段文件路径。</param>
    /// <returns>从段所在目录到数据库根目录（含）的刷新顺序。</returns>
    /// <exception cref="ArgumentException">段路径没有父目录或不在数据库根目录内时抛出。</exception>
    internal static IReadOnlyList<string> GetSegmentPublicationDirectoryChain(
        string rootDirectory,
        string segmentPath)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        ArgumentNullException.ThrowIfNull(segmentPath);

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        string? segmentDirectory = Path.GetDirectoryName(Path.GetFullPath(segmentPath));
        if (string.IsNullOrEmpty(segmentDirectory))
            throw new ArgumentException("Segment path must have a parent directory.", nameof(segmentPath));

        string current = Path.TrimEndingDirectorySeparator(segmentDirectory);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!string.Equals(current, root, comparison)
            && !current.StartsWith(rootWithSeparator, comparison))
        {
            throw new ArgumentException(
                "Segment path must be located within the database root directory.",
                nameof(segmentPath));
        }

        var directories = new List<string>();
        while (true)
        {
            directories.Add(current);
            if (string.Equals(current, root, comparison))
                return directories;

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent))
            {
                throw new ArgumentException(
                    "Segment path cannot be resolved to the database root directory.",
                    nameof(segmentPath));
            }

            string normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            if (string.Equals(normalizedParent, current, comparison))
            {
                throw new ArgumentException(
                    "Segment path cannot be resolved to the database root directory.",
                    nameof(segmentPath));
            }

            current = normalizedParent;
        }
    }
}
