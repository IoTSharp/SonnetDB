using System.Diagnostics;
using SonnetDB.Catalog;

namespace SonnetDB.Wal;

/// <summary>
/// WAL 回放辅助类，将 WAL 记录应用到 <see cref="SeriesCatalog"/> 并 yield 出写入点流。
/// </summary>
public static class WalReplay
{
    /// <summary>
    /// 对 catalog 应用 CreateSeries 记录，并返回回放出的 WritePoint 序列。
    /// </summary>
    /// <param name="walPath">WAL 文件路径。</param>
    /// <param name="catalog">目标序列目录，CreateSeries 记录将被应用到此 catalog。</param>
    /// <returns>回放出的 <see cref="WritePointRecord"/> 序列（按 LSN 顺序）。</returns>
    /// <exception cref="ArgumentNullException">任何参数为 null 时抛出。</exception>
    /// <exception cref="InvalidDataException">
    /// CreateSeries 记录的 SeriesId 与 catalog 重新计算的结果不一致时抛出。
    /// </exception>
    public static IEnumerable<WritePointRecord> ReplayInto(string walPath, SeriesCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(walPath);
        ArgumentNullException.ThrowIfNull(catalog);

        using var reader = WalReader.OpenForRecovery(walPath, new());
        foreach (var record in reader.Replay())
        {
            switch (record)
            {
                case CreateSeriesRecord csRecord:
                    var entry = catalog.GetOrAdd(csRecord.Measurement, csRecord.Tags);
                    if (entry.Id != csRecord.SeriesId)
                        throw new InvalidDataException(
                            $"WAL CreateSeries SeriesId mismatch for '{csRecord.Measurement}': " +
                            $"WAL={csRecord.SeriesId}, computed={entry.Id}.");
                    break;

                case WritePointRecord wpRecord:
                    yield return wpRecord;
                    break;

                case AtomicPointRecord point:
                    foreach (var field in point.Fields)
                        yield return new WritePointRecord(point.Lsn, point.TimestampUtcTicks,
                            point.SeriesId, point.PointTimestamp, field.Key, field.Value);
                    break;

                case CheckpointRecord cpRecord:
                    Trace.WriteLine($"[WalReplay] Checkpoint LSN={cpRecord.CheckpointLsn}");
                    break;

                case TruncateRecord trRecord:
                    Trace.WriteLine($"[WalReplay] Truncate LSN={trRecord.Lsn}");
                    break;
            }
        }
    }

    /// <summary>
    /// 对 catalog 应用 CreateSeries 记录，返回包含 Checkpoint LSN、最后 LSN 及过滤后写入点列表的结果。
    /// 仅返回 LSN 严格大于最大 CheckpointLsn 的 WritePoint 记录，跳过已落盘的冗余回放。
    /// 采用两遍扫描策略：第一遍获取最大 CheckpointLsn 和最后 LSN，第二遍应用过滤。
    /// </summary>
    /// <param name="walPath">WAL 文件路径。</param>
    /// <param name="catalog">目标序列目录，CreateSeries 记录将被应用到此 catalog。</param>
    /// <returns>包含 Checkpoint LSN、最后 LSN 及过滤后写入点列表的 <see cref="WalReplayResult"/>。</returns>
    /// <exception cref="ArgumentNullException">任何参数为 null 时抛出。</exception>
    /// <exception cref="InvalidDataException">
    /// CreateSeries 记录的 SeriesId 与 catalog 重新计算的结果不一致时抛出。
    /// </exception>
    public static WalReplayResult ReplayIntoWithCheckpoint(string walPath, SeriesCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(walPath);
        ArgumentNullException.ThrowIfNull(catalog);

        // 第一遍：扫描最大 CheckpointLsn 和最后一条记录的 LSN
        long checkpointLsn = 0;
        long lastLsn = 0;

        using (var reader1 = WalReader.OpenForRecovery(walPath, new()))
        {
            foreach (var record in reader1.Replay())
            {
                lastLsn = record.Lsn;
                if (record is CheckpointRecord cpRecord && cpRecord.CheckpointLsn > checkpointLsn)
                    checkpointLsn = cpRecord.CheckpointLsn;
            }
        }

        // 第二遍：应用 catalog + 按 checkpointLsn 过滤 WritePoint
        var writePoints = new List<WritePointRecord>();
        var atomicPoints = new List<AtomicPointRecord>();
        var deleteRecords = new List<DeleteRecord>();

        using (var reader2 = WalReader.OpenForRecovery(walPath, new()))
        {
            foreach (var record in reader2.Replay())
            {
                switch (record)
                {
                    case CreateSeriesRecord csRecord:
                        // 始终应用到 catalog（幂等）
                        var entry = catalog.GetOrAdd(csRecord.Measurement, csRecord.Tags);
                        if (entry.Id != csRecord.SeriesId)
                            throw new InvalidDataException(
                                $"WAL CreateSeries SeriesId mismatch for '{csRecord.Measurement}': " +
                                $"WAL={csRecord.SeriesId}, computed={entry.Id}.");
                        break;

                    case WritePointRecord wpRecord:
                        if (wpRecord.Lsn > checkpointLsn)
                            writePoints.Add(wpRecord);
                        break;

                    case AtomicPointRecord point:
                        if (point.Lsn > checkpointLsn)
                            atomicPoints.Add(point);
                        break;

                    case DeleteRecord delRecord:
                        if (delRecord.Lsn > checkpointLsn)
                            deleteRecords.Add(delRecord);
                        break;
                }
            }
        }

        return new WalReplayResult(checkpointLsn, lastLsn, writePoints, deleteRecords)
        {
            AtomicPoints = atomicPoints,
        };
    }
}

/// <summary>
/// WAL 回放（含 Checkpoint 跳过）的结果。
/// </summary>
/// <param name="CheckpointLsn">WAL 中见到的最大 Checkpoint LSN；0 表示未见到任何 Checkpoint。</param>
/// <param name="LastLsn">WAL 中最后一条合法记录的 LSN（含被跳过的记录）；0 表示 WAL 为空。</param>
/// <param name="WritePoints">按 LSN 升序排列的写入点列表，仅包含 LSN 严格大于 CheckpointLsn 的记录。</param>
/// <param name="DeleteRecords">按 LSN 升序排列的删除记录列表，仅包含 LSN 严格大于 CheckpointLsn 的记录。</param>
public sealed record WalReplayResult(
    long CheckpointLsn,
    long LastLsn,
    IReadOnlyList<WritePointRecord> WritePoints,
    IReadOnlyList<DeleteRecord> DeleteRecords)
{
    /// <summary>整点 WAL v2 的回放记录；v1 回放始终为空。</summary>
    public IReadOnlyList<AtomicPointRecord> AtomicPoints { get; init; } = [];
}
