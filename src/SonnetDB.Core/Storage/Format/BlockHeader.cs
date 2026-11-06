using System.Runtime.InteropServices;
using SonnetDB.Buffers;

namespace SonnetDB.Storage.Format;

/// <summary>
/// SonnetDB 段文件中每个 Block 的头部（v5 起固定 80 字节）。
/// <para>
/// 一个 Block 由 BlockHeader + FieldNameUtf8 + TimestampPayload + ValuePayload 组成。
/// </para>
/// <para>
/// 二进制布局（little-endian, Pack=1）：
/// <code>
/// Offset  Size  Field
/// 0       8     SeriesId                (序列唯一 ID)
/// 8       8     MinTimestamp            (本 Block 最小时间戳，毫秒 UTC)
/// 16      8     MaxTimestamp            (本 Block 最大时间戳，毫秒 UTC)
/// 24      4     Count                   (数据点数量)
/// 28      4     TimestampPayloadLength  (时间戳载荷字节数)
/// 32      4     ValuePayloadLength      (值载荷字节数)
/// 36      4     FieldNameUtf8Length     (字段名 UTF-8 字节数)
/// 40      1     Encoding                (<see cref="BlockEncoding"/>)
/// 41      1     FieldType               (<see cref="Format.FieldType"/>)
/// 42      2     AggregateFlags
/// 44      8     AggregateSum            (double, little-endian)
/// 52      8     AggregateMin            (double, little-endian, 仅 HasMinMax 为真时有效)
/// 60      8     AggregateMax            (double, little-endian, 仅 HasMinMax 为真时有效)
/// 68      4     GeoHashMin             (GEOPOINT Block 的最小 32-bit geohash 前缀，0 表示未写入)
/// 72      4     GeoHashMax             (GEOPOINT Block 的最大 32-bit geohash 前缀，0 表示未写入)
/// 76      4     Crc32
/// ─────────────────────────────────
/// Total  80
/// </code>
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BlockHeader
{
    /// <summary>序列唯一 ID（由 SeriesKey 哈希得到）。</summary>
    public ulong SeriesId;

    /// <summary>本 Block 内最小时间戳（毫秒 UTC）。</summary>
    public long MinTimestamp;

    /// <summary>本 Block 内最大时间戳（毫秒 UTC）。</summary>
    public long MaxTimestamp;

    /// <summary>本 Block 包含的数据点数量。</summary>
    public int Count;

    /// <summary>时间戳载荷字节数（跟在 FieldNameUtf8 之后）。</summary>
    public int TimestampPayloadLength;

    /// <summary>值载荷字节数（跟在 TimestampPayload 之后）。</summary>
    public int ValuePayloadLength;

    /// <summary>字段名 UTF-8 编码的字节数（跟在 BlockHeader 之后）。</summary>
    public int FieldNameUtf8Length;

    /// <summary>数据编码方式（见 <see cref="BlockEncoding"/>）。</summary>
    public BlockEncoding Encoding;

    /// <summary>字段数据类型（见 <see cref="Format.FieldType"/>）。</summary>
    public FieldType FieldType;

    /// <summary>
    /// 聚合元数据标记（按位组合）。
    /// <list type="bullet">
    ///   <item><c>0x01</c> <see cref="HasSumCount"/>: <see cref="AggregateSum"/> 已写入且对 Sum/Count/Avg 精度足够。</item>
    ///   <item><c>0x02</c> <see cref="HasMinMax"/>: <see cref="AggregateMin"/> / <see cref="AggregateMax"/> 为无损值，可用于 Min/Max。</item>
    /// </list>
    /// </summary>
    public short AggregateFlags;

    /// <summary>数值聚合的 Sum（统一按 double 持久化）。</summary>
    public double AggregateSum;

    /// <summary>
    /// 数值聚合的 Min（仅当 <see cref="HasMinMax"/> 置位时有效）。
    /// <para>
    /// v2 起统一为 8 字节 <see cref="double"/>，无损覆盖 Float64 / Int64 全部范围；
    /// Boolean 字段保留 0/1。
    /// </para>
    /// </summary>
    public double AggregateMin;

    /// <summary>数值聚合的 Max（编码方式同 <see cref="AggregateMin"/>）。</summary>
    public double AggregateMax;

    /// <summary>GEOPOINT 块的最小 32-bit geohash 前缀；0 表示未写入。</summary>
    public uint GeoHashMin;

    /// <summary>GEOPOINT 块的最大 32-bit geohash 前缀；0 表示未写入。</summary>
    public uint GeoHashMax;

    /// <summary><see cref="AggregateFlags"/> 的标记位：sum/count 已写入。</summary>
    public const short HasSumCount = 0x01;

    /// <summary><see cref="AggregateFlags"/> 的标记位：min/max 已无损写入。</summary>
    public const short HasMinMax = 0x02;

    /// <summary>块数据 CRC32 校验值（CRC32(FieldNameUtf8 ++ TimestampPayload ++ ValuePayload)）。</summary>
    public uint Crc32;

    /// <summary>
    /// 创建一个新的 <see cref="BlockHeader"/>，填写所有关键字段。
    /// </summary>
    /// <param name="seriesId">序列唯一 ID。</param>
    /// <param name="min">本 Block 最小时间戳（毫秒 UTC）。</param>
    /// <param name="max">本 Block 最大时间戳（毫秒 UTC）。</param>
    /// <param name="count">数据点数量。</param>
    /// <param name="fieldType">字段数据类型。</param>
    /// <param name="fieldNameLen">字段名 UTF-8 字节数。</param>
    /// <param name="tsLen">时间戳载荷字节数。</param>
    /// <param name="valLen">值载荷字节数。</param>
    /// <returns>已初始化的 <see cref="BlockHeader"/> 实例。</returns>
    public static BlockHeader CreateNew(
        ulong seriesId,
        long min,
        long max,
        int count,
        FieldType fieldType,
        int fieldNameLen,
        int tsLen,
        int valLen)
    {
        BlockHeader h = default;
        h.SeriesId = seriesId;
        h.MinTimestamp = min;
        h.MaxTimestamp = max;
        h.Count = count;
        h.FieldType = fieldType;
        h.FieldNameUtf8Length = fieldNameLen;
        h.TimestampPayloadLength = tsLen;
        h.ValuePayloadLength = valLen;
        h.Encoding = BlockEncoding.None;
        return h;
    }
}
