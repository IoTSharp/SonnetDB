using SonnetDB.Catalog;
using SonnetDB.Memory;
using SonnetDB.Storage.Segments;
using SonnetDB.Wal;

namespace SonnetDB.Engine;

/// <summary>
/// MemTable → Segment 的 Flush 协调器。串行运行，保证写 Segment、写 WAL Checkpoint、持久化 checkpoint LSN、Roll WAL、回收旧段、重置 MemTable 按顺序可见。
/// </summary>
/// <remarks>
/// 崩溃恢复语义：
/// <list type="bullet">
///   <item><description>若 step 2 之前崩溃 → 重启后 WAL 仍含全部 record，replay 重建 MemTable。</description></item>
///   <item><description>若 step 2 完成但 step 3 之前崩溃 → 段文件存在但 WAL 无 Checkpoint。重启时 replay 全部 record（允许冗余）。</description></item>
///   <item><description>若 step 3 完成（AppendCheckpoint+Sync）但 durable checkpoint 文件未完成 → WAL checkpoint 仍可让 replay 跳过已落盘 WritePoint。</description></item>
///   <item><description>若 durable checkpoint 文件写入中途崩溃 → 恢复时忽略 tmp/坏文件，并回退 WAL replay。</description></item>
///   <item><description>若 step 4（Roll）完成但 step 5（RecycleUpTo）之前崩溃 → 旧 segment 仍存在，重启后 RecycleUpTo 将在下次 Flush 时清理。</description></item>
///   <item><description>若全部步骤完成 → 旧 segment 已删除，新 active segment 从 nextLsn 开始。</description></item>
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
    /// 执行一次 Flush：将 MemTable 写出为 Segment，追加 WAL Checkpoint，持久化 checkpoint LSN，Roll WAL，回收旧 WAL segment，然后重置 MemTable。
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

        // 步骤 2：写 Segment（临时文件 + 原子 rename，由 SegmentWriter 保证）
        string segPath = TsdbPaths.SegmentPath(_options.RootDirectory, segmentId);
        var segWriter = new SegmentWriter(_options.SegmentWriterOptions);
        IReadOnlyDictionary<Model.SeriesFieldKey, VectorIndexDefinition>? vectorIndexes = null;
        if (seriesCatalog is not null && measurementCatalog is not null)
            vectorIndexes = VectorIndexBuildMap.Build(memTable.SnapshotAll(), seriesCatalog, measurementCatalog);
        var result = segWriter.WriteFrom(memTable, segmentId, segPath, vectorIndexes);
        WalCheckpointFile.FlushDirectoryBestEffort(TsdbPaths.SegmentsDir(_options.RootDirectory));

        // 步骤 3：追加 WAL Checkpoint + Sync
        long checkpointRecordLsn = walSet.AppendCheckpoint(lastLsnBeforeFlush);
        walSet.Sync();

        // 步骤 3.5：把 durable checkpoint LSN 独立落盘。该文件只在对应 Segment 仍存在且长度匹配时生效；
        // 若崩溃发生在 tmp 写入、rename 或目录 flush 的中间状态，恢复路径会忽略它并完整 replay WAL。
        WalCheckpointFile.Save(
            WalSegmentLayout.CheckpointPath(TsdbPaths.WalDir(_options.RootDirectory)),
            new WalCheckpointState(
                lastLsnBeforeFlush,
                result.SegmentId,
                result.TotalBytes,
                DateTime.UtcNow.Ticks));

        // 步骤 4：主动 Roll（确保 checkpoint 之前的数据归到一个已封存的 segment，
        //         使 active segment 的 startLsn 严格 > checkpointLsn，避免 RecycleUpTo 误删）
        walSet.Roll();

        // 步骤 5：回收已 checkpoint 的旧 WAL segment
        // 使用 checkpoint 记录本身的 LSN（= lastLsnBeforeFlush + 1），
        // 保证包含 Checkpoint 记录的 segment 也能被正确回收
        walSet.RecycleUpTo(checkpointRecordLsn);

        // 注：MemTable 的清空不在此处进行。调用方（Tsdb.FlushNowLocked）在本方法返回后，
        // 通过 SegmentManager 一次原子发布"接入新段 + 换成新空 MemTable"，
        // 使 flush 期间旧数据始终恰好可见一次（修 #190）。

        return result;
    }

    /// <summary>
    /// Phase 2 泵专用 Flush：把已密封的不可变 MemTable 编码落盘，写 durable checkpoint 文件，
    /// 并以 <paramref name="sealLsn"/> 为阈值回收旧 WAL segment。
    /// <para>
    /// 前置：调用方（<c>Tsdb.SealAndEnqueueLocked</c>）已在 <c>_writeSync</c> 内完成
    /// <c>AppendCheckpoint(sealLsn)</c> + <c>Roll()</c>，把被密封数据的 WAL 记录与 seal 之后的
    /// 并发写入用 roll 边界隔开。因此本方法<b>不再</b>做 checkpoint/roll，只回收：
    /// <c>RecycleUpTo(sealLsn)</c> 精确删除被密封数据的段，绝不触及并发写入记录。
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

        // 步骤 1：编码落盘（临时文件 + 原子 rename + fsync）。崩溃发生在此之前/之中时，
        // 尚未写任何 checkpoint，replay 会完整回放被密封数据（冗余但不丢）。
        string segPath = TsdbPaths.SegmentPath(_options.RootDirectory, segmentId);
        var segWriter = new SegmentWriter(_options.SegmentWriterOptions);
        IReadOnlyDictionary<Model.SeriesFieldKey, VectorIndexDefinition>? vectorIndexes = null;
        if (seriesCatalog is not null && measurementCatalog is not null)
            vectorIndexes = VectorIndexBuildMap.Build(sealedTable.SnapshotAll(), seriesCatalog, measurementCatalog);
        var result = segWriter.WriteFrom(sealedTable, segmentId, segPath, vectorIndexes);
        WalCheckpointFile.FlushDirectoryBestEffort(TsdbPaths.SegmentsDir(_options.RootDirectory));

        // 步骤 2：durable checkpoint 文件（段已落盘后才写；崩溃时若段不完整则回退完整 WAL replay）。
        WalCheckpointFile.Save(
            WalSegmentLayout.CheckpointPath(TsdbPaths.WalDir(_options.RootDirectory)),
            new WalCheckpointState(sealLsn, result.SegmentId, result.TotalBytes, DateTime.UtcNow.Ticks));

        // 步骤 3：段已持久化后，才向 WAL 追加 checkpoint 记录并 Sync。
        // 崩溃安全：只有段真正落盘后 WAL 里才出现 checkpoint(sealLsn)，replay 才会跳过 ≤ sealLsn 的写入。
        // checkpoint 记录落到 seal 时 Roll 出的新 active 段（startLsn = sealLsn+1），不影响被密封段的回收阈值。
        long checkpointRecordLsn = walSet.AppendCheckpoint(sealLsn);
        walSet.Sync();

        // 步骤 4：回收被密封数据所在的段（lastLsn = sealLsn ≤ sealLsn）。
        // seal 时的 Roll 保证被密封数据完整落在 roll 之前的段；并发写入与 checkpoint 记录在新段
        // （startLsn = sealLsn+1 > sealLsn），天然保留。用 checkpointRecordLsn 作阈值以连同可能因
        // 字节阈值单独成段的 checkpoint 记录段一并回收——但仅当其后没有并发写入时才会命中（见 RecycleUpTo
        // 遇到第一个 lastLsn > 阈值即停止），故对并发写入安全。
        _ = checkpointRecordLsn;
        walSet.RecycleUpTo(sealLsn);

        return result;
    }
}
