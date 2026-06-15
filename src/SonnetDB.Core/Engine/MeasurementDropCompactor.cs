using SonnetDB.Catalog;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Segments;
using SonnetDB.Wal;

namespace SonnetDB.Engine;

/// <summary>
/// 为 <c>DROP MEASUREMENT</c> 重写 Segment，物理移除目标 series 的所有 block。
/// </summary>
internal static class MeasurementDropCompactor
{
    public static bool RewriteWithoutSeries(
        Tsdb owner,
        SegmentReader reader,
        IReadOnlySet<ulong> droppedSeriesIds,
        long newSegmentId,
        string newSegmentPath,
        out SegmentBuildResult? result)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(droppedSeriesIds);
        ArgumentNullException.ThrowIfNull(newSegmentPath);

        var buckets = new List<MemTableSeries>();
        var touched = false;

        foreach (var block in reader.Blocks)
        {
            if (droppedSeriesIds.Contains(block.SeriesId))
            {
                touched = true;
                continue;
            }

            var bucket = new MemTableSeries(
                new SeriesFieldKey(block.SeriesId, block.FieldName),
                block.FieldType);
            foreach (var point in reader.DecodeBlock(block))
                bucket.Append(point.Timestamp, point.Value);
            buckets.Add(bucket);
        }

        if (!touched)
        {
            result = null;
            return false;
        }

        if (buckets.Count == 0)
        {
            result = null;
            return true;
        }

        var writer = new SegmentWriter(owner.CompactionWriterOptions);
        IReadOnlyDictionary<SeriesFieldKey, VectorIndexDefinition>? vectorIndexes = null;
        if (owner.Catalog.Count > 0 && owner.Measurements.Count > 0)
            vectorIndexes = VectorIndexBuildMap.Build(buckets, owner.Catalog, owner.Measurements);
        result = writer.Write(buckets, newSegmentId, newSegmentPath, vectorIndexes);
        WalCheckpointFile.FlushDirectoryBestEffort(TsdbPaths.SegmentsDir(owner.RootDirectory));
        return true;
    }
}
