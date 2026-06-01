namespace SonnetDB.Engine;

/// <summary>
/// SonnetDB 磁盘目录结构的路径生成工具。集中管理所有文件和目录的路径计算逻辑。
/// <para>
/// 标准磁盘布局：
/// <code>
/// &lt;rootDir&gt;/
/// ├── catalog.SDBCAT
/// ├── wal/
/// │   └── active.SDBWAL
/// └── segments/
///     ├── 0000000000000001.SDBSEG
///     └── ...
/// </code>
/// </para>
/// </summary>
public static class TsdbPaths
{
    /// <summary>目录文件名（相对于根目录）。</summary>
    public const string CatalogFileName = "catalog.SDBCAT";

    /// <summary>WAL 子目录名。</summary>
    public const string WalDirName = "wal";

    /// <summary>活跃 WAL 文件名。</summary>
    public const string ActiveWalFileName = "active.SDBWAL";

    /// <summary>Segment 子目录名。</summary>
    public const string SegmentsDirName = "segments";

    /// <summary>KV 子目录名。</summary>
    public const string KvDirName = "kv";

    /// <summary>Segment 文件扩展名。</summary>
    public const string SegmentFileExtension = ".SDBSEG";

    /// <summary>legacy 向量索引 sidecar 文件扩展名。</summary>
    public const string VectorIndexFileExtension = ".SDBVIDX";

    /// <summary>legacy 扩展聚合 block sketch sidecar 文件扩展名。</summary>
    public const string AggregateIndexFileExtension = ".SDBAIDX";

    /// <summary>墓碑清单文件名（相对于根目录）。</summary>
    public const string TombstoneManifestFileName = "tombstones.tslmanifest";

    /// <summary>Measurement schema 文件名（相对于根目录）。</summary>
    public const string MeasurementSchemaFileName = "measurements.tslschema";

    /// <summary>
    /// 返回目录文件的完整路径：<c>{root}/catalog.SDBCAT</c>。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <returns>目录文件路径。</returns>
    public static string CatalogPath(string root) =>
        Path.Combine(root, CatalogFileName);

    /// <summary>
    /// 返回墓碑清单文件的完整路径：<c>{root}/tombstones.tslmanifest</c>。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <returns>墓碑清单文件路径。</returns>
    public static string TombstoneManifestPath(string root) =>
        Path.Combine(root, TombstoneManifestFileName);

    /// <summary>
    /// 返回 measurement schema 文件的完整路径：<c>{root}/measurements.tslschema</c>。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <returns>schema 文件路径。</returns>
    public static string MeasurementSchemaPath(string root) =>
        Path.Combine(root, MeasurementSchemaFileName);

    /// <summary>
    /// 返回 WAL 子目录的完整路径：<c>{root}/wal</c>。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <returns>WAL 目录路径。</returns>
    public static string WalDir(string root) =>
        Path.Combine(root, WalDirName);

    /// <summary>
    /// 返回活跃 WAL 文件的完整路径：<c>{root}/wal/active.SDBWAL</c>。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <returns>活跃 WAL 文件路径。</returns>
    public static string ActiveWalPath(string root) =>
        Path.Combine(root, WalDirName, ActiveWalFileName);

    /// <summary>
    /// 返回 Segment 子目录的完整路径：<c>{root}/segments</c>。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <returns>Segment 目录路径。</returns>
    public static string SegmentsDir(string root) =>
        Path.Combine(root, SegmentsDirName);

    /// <summary>
    /// 返回 KV 子目录的完整路径：<c>{root}/kv</c>。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <returns>KV 目录路径。</returns>
    public static string KvDir(string root) =>
        Path.Combine(root, KvDirName);

    /// <summary>
    /// 返回指定 SegmentId 对应的段文件完整路径：
    /// <c>{root}/segments/{segmentId:X16}.SDBSEG</c>。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <param name="segmentId">段唯一标识符（单调递增正整数）。</param>
    /// <returns>段文件路径。</returns>
    public static string SegmentPath(string root, long segmentId) =>
        Path.Combine(root, SegmentsDirName, $"{segmentId:X16}{SegmentFileExtension}");

    /// <summary>
    /// 返回指定 SegmentId 对应的 legacy 向量索引 sidecar 文件完整路径：
    /// <c>{root}/segments/{segmentId:X16}.SDBVIDX</c>。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <param name="segmentId">段唯一标识符。</param>
    /// <returns>legacy 向量索引 sidecar 文件路径。</returns>
    public static string VectorIndexPath(string root, long segmentId) =>
        Path.Combine(root, SegmentsDirName, $"{segmentId:X16}{VectorIndexFileExtension}");

    /// <summary>
    /// 根据段文件路径推导对应的 legacy 向量索引 sidecar 文件路径。
    /// </summary>
    /// <param name="segmentPath">段文件完整路径。</param>
    /// <returns>对应的 legacy sidecar 文件路径。</returns>
    public static string VectorIndexPathForSegment(string segmentPath)
    {
        ArgumentNullException.ThrowIfNull(segmentPath);
        return Path.ChangeExtension(segmentPath, VectorIndexFileExtension);
    }

    /// <summary>
    /// 返回指定 SegmentId 对应的 legacy 扩展聚合 sketch sidecar 文件完整路径：
    /// <c>{root}/segments/{segmentId:X16}.SDBAIDX</c>。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <param name="segmentId">段唯一标识符。</param>
    /// <returns>legacy 扩展聚合 sidecar 文件路径。</returns>
    public static string AggregateIndexPath(string root, long segmentId) =>
        Path.Combine(root, SegmentsDirName, $"{segmentId:X16}{AggregateIndexFileExtension}");

    /// <summary>
    /// 根据段文件路径推导对应的 legacy 扩展聚合 sketch sidecar 文件路径。
    /// </summary>
    /// <param name="segmentPath">段文件完整路径。</param>
    /// <returns>对应的 legacy sidecar 文件路径。</returns>
    public static string AggregateIndexPathForSegment(string segmentPath)
    {
        ArgumentNullException.ThrowIfNull(segmentPath);
        return Path.ChangeExtension(segmentPath, AggregateIndexFileExtension);
    }

    /// <summary>
    /// 尝试从文件名中解析 SegmentId（16 位十六进制 + .SDBSEG 扩展名）。
    /// </summary>
    /// <param name="fileName">仅文件名部分（不含目录），例如 "0000000000000042.SDBSEG"。</param>
    /// <param name="segmentId">解析成功时输出对应的 SegmentId；否则为 0。</param>
    /// <returns>解析成功返回 true，否则返回 false。</returns>
    public static bool TryParseSegmentId(string fileName, out long segmentId)
    {
        segmentId = 0;
        if (!fileName.EndsWith(SegmentFileExtension, StringComparison.OrdinalIgnoreCase))
            return false;

        string hex = Path.GetFileNameWithoutExtension(fileName);
        if (hex.Length != 16)
            return false;

        return long.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out segmentId);
    }

    /// <summary>
    /// 枚举根目录下所有已落盘的 Segment 文件，返回 (SegmentId, FilePath) 元组序列。
    /// 若 segments/ 子目录不存在，返回空序列。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <returns>(SegmentId, FilePath) 元组的枚举序列（顺序不保证）。</returns>
    public static IEnumerable<(long SegmentId, string Path)> EnumerateSegments(string root)
    {
        string dir = SegmentsDir(root);
        if (!Directory.Exists(dir))
            yield break;

        foreach (string file in Directory.EnumerateFiles(dir, $"*{SegmentFileExtension}"))
        {
            if (TryParseSegmentId(System.IO.Path.GetFileName(file), out long segId))
                yield return (segId, file);
        }
    }
}
