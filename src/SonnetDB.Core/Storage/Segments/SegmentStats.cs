using SonnetDB.Model;
using SonnetDB.Storage.Format;

namespace SonnetDB.Storage.Segments;

/// <summary>
/// 段文件级别的编码与字节统计快照（PR #31）。由 <see cref="SegmentReader.GetStats"/> 按需计算，
/// 用于运维巡检、压缩率对比、基准测试等场景；不会被持久化进段文件。
/// </summary>
public sealed record SegmentStats
{
    /// <summary>段内 Block 总数。</summary>
    public int BlockCount { get; init; }

    /// <summary>段内所有 Block 的数据点数之和。</summary>
    public int TotalPointCount { get; init; }

    /// <summary>所有 Block 字段名 UTF-8 字节总和。</summary>
    public long TotalFieldNameBytes { get; init; }

    /// <summary>所有 Block 时间戳载荷字节总和。</summary>
    public long TotalTimestampPayloadBytes { get; init; }

    /// <summary>所有 Block 值载荷字节总和。</summary>
    public long TotalValuePayloadBytes { get; init; }

    /// <summary>使用 V1（<see cref="BlockEncoding.None"/>）原始时间戳编码的 Block 数。</summary>
    public int RawTimestampBlocks { get; init; }

    /// <summary>使用 V2（<see cref="BlockEncoding.DeltaTimestamp"/>）编码的 Block 数。</summary>
    public int DeltaTimestampBlocks { get; init; }

    /// <summary>使用 V1（<see cref="BlockEncoding.None"/>）原始值编码的 Block 数。</summary>
    public int RawValueBlocks { get; init; }

    /// <summary>使用类型压缩（<see cref="BlockEncoding.DeltaValue"/> 或 <see cref="BlockEncoding.Int64Rle"/>）编码的 Block 数。</summary>
    public int DeltaValueBlocks { get; init; }

    /// <summary>按 <see cref="FieldType"/> 分组的子统计。</summary>
    public IReadOnlyDictionary<FieldType, FieldTypeStats> ByFieldType { get; init; }
        = new Dictionary<FieldType, FieldTypeStats>();

    /// <summary>
    /// 时间戳平均字节/点；当 <see cref="TotalPointCount"/> 为 0 时返回 0。
    /// </summary>
    public double AverageTimestampBytesPerPoint =>
        TotalPointCount == 0 ? 0d : (double)TotalTimestampPayloadBytes / TotalPointCount;

    /// <summary>
    /// 值平均字节/点；当 <see cref="TotalPointCount"/> 为 0 时返回 0。
    /// </summary>
    public double AverageValueBytesPerPoint =>
        TotalPointCount == 0 ? 0d : (double)TotalValuePayloadBytes / TotalPointCount;
}

/// <summary>
/// 按 <see cref="FieldType"/> 分组的统计：Block 数、点数、值载荷字节、类型压缩启用 Block 数。
/// </summary>
public sealed record FieldTypeStats(
    int BlockCount,
    int PointCount,
    long ValuePayloadBytes,
    int DeltaValueBlocks);
