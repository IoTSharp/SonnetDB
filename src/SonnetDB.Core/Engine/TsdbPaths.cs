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
///     ├── v2/
///     │   └── 00/
///     │       └── 0000000000000000/
///     │           ├── 0000000000000001.SDBSEG
///     │           └── ...
///     └── 0000000000000001.SDBSEG  (legacy flat layout, read-compatible)
/// </code>
/// </para>
/// </summary>
public static class TsdbPaths
{
    private const long SegmentBucketSize = 4096L;
    private const string SegmentLayoutVersionDirName = "v2";

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

    /// <summary>关系表子目录名。</summary>
    public const string TablesDirName = "tables";

    /// <summary>JSON 文档集合子目录名。</summary>
    public const string DocumentsDirName = "documents";

    /// <summary>Segment 文件扩展名。</summary>
    public const string SegmentFileExtension = ".SDBSEG";

    /// <summary>legacy 向量索引 sidecar 文件扩展名。</summary>
    public const string VectorIndexFileExtension = ".SDBVIDX";

    /// <summary>legacy 扩展聚合 block sketch sidecar 文件扩展名。</summary>
    public const string AggregateIndexFileExtension = ".SDBAIDX";

    /// <summary>墓碑清单文件名（相对于根目录）。</summary>
    public const string TombstoneManifestFileName = "tombstones.tslmanifest";

    /// <summary>Segment 替换清单文件名（相对于根目录）。</summary>
    public const string SegmentReplacementManifestFileName = "segment-replacements.sdbmanifest";

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
    /// 返回 Segment 替换清单文件的完整路径：<c>{root}/segment-replacements.sdbmanifest</c>。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <returns>Segment 替换清单文件路径。</returns>
    public static string SegmentReplacementManifestPath(string root) =>
        Path.Combine(root, SegmentReplacementManifestFileName);

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
    /// 返回关系表子目录的完整路径：<c>{root}/tables</c>。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <returns>关系表目录路径。</returns>
    public static string TablesDir(string root) =>
        Path.Combine(root, TablesDirName);

    /// <summary>
    /// 返回 JSON 文档集合子目录的完整路径：<c>{root}/documents</c>。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <returns>JSON 文档集合目录路径。</returns>
    public static string DocumentsDir(string root) =>
        Path.Combine(root, DocumentsDirName);

    /// <summary>
    /// 返回指定 SegmentId 对应的段文件完整路径：
    /// <c>{root}/segments/v2/{bucketLo:X2}/{bucket:X16}/{segmentId:X16}.SDBSEG</c>。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <param name="segmentId">段唯一标识符（单调递增正整数）。</param>
    /// <returns>段文件路径。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="segmentId"/> 小于等于 0 时抛出。</exception>
    public static string SegmentPath(string root, long segmentId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentId);
        string bucket = SegmentBucketName(segmentId);
        string bucketGroup = SegmentBucketGroupName(segmentId);
        return Path.Combine(root, SegmentsDirName, SegmentLayoutVersionDirName, bucketGroup, bucket, SegmentFileName(segmentId));
    }

    /// <summary>
    /// 返回旧版平铺布局下指定 SegmentId 对应的段文件完整路径：
    /// <c>{root}/segments/{segmentId:X16}.SDBSEG</c>。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <param name="segmentId">段唯一标识符（单调递增正整数）。</param>
    /// <returns>旧版段文件路径。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="segmentId"/> 小于等于 0 时抛出。</exception>
    public static string LegacySegmentPath(string root, long segmentId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentId);
        return Path.Combine(root, SegmentsDirName, SegmentFileName(segmentId));
    }

    /// <summary>
    /// 返回指定 SegmentId 对应的 legacy 向量索引 sidecar 文件完整路径：
    /// 与主段文件使用相同目录布局。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <param name="segmentId">段唯一标识符。</param>
    /// <returns>legacy 向量索引 sidecar 文件路径。</returns>
    public static string VectorIndexPath(string root, long segmentId) =>
        Path.ChangeExtension(SegmentPath(root, segmentId), VectorIndexFileExtension);

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
    /// 与主段文件使用相同目录布局。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <param name="segmentId">段唯一标识符。</param>
    /// <returns>legacy 扩展聚合 sidecar 文件路径。</returns>
    public static string AggregateIndexPath(string root, long segmentId) =>
        Path.ChangeExtension(SegmentPath(root, segmentId), AggregateIndexFileExtension);

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
            System.Globalization.CultureInfo.InvariantCulture, out segmentId)
            && segmentId > 0;
    }

    /// <summary>
    /// 尝试解析数据库根目录中指定 SegmentId 的现存段文件路径。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <param name="segmentId">段唯一标识符。</param>
    /// <param name="path">找到时输出段文件路径；未找到时为空字符串。</param>
    /// <returns>找到段文件返回 true，否则返回 false。</returns>
    public static bool TryGetSegmentPath(string root, long segmentId, out string path)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentId);

        string currentPath = SegmentPath(root, segmentId);
        if (File.Exists(currentPath))
        {
            path = currentPath;
            return true;
        }

        string legacyPath = LegacySegmentPath(root, segmentId);
        if (File.Exists(legacyPath))
        {
            path = legacyPath;
            return true;
        }

        foreach (var (existingId, existingPath) in EnumerateSegments(root))
        {
            if (existingId == segmentId)
            {
                path = existingPath;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    /// <summary>
    /// 枚举指定 SegmentId 可能遗留的主段与 sidecar 文件路径，用于 Compaction / Retention 清理旧段。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <param name="segmentId">段唯一标识符。</param>
    /// <returns>去重后的候选文件路径。</returns>
    public static IReadOnlyList<string> SegmentArtifactPaths(string root, long segmentId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentId);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            SegmentPath(root, segmentId),
            LegacySegmentPath(root, segmentId),
        };

        foreach (var (existingId, existingPath) in EnumerateSegments(root))
        {
            if (existingId == segmentId)
                paths.Add(existingPath);
        }

        var segmentPaths = paths.ToArray();
        foreach (string segmentPath in segmentPaths)
        {
            paths.Add(VectorIndexPathForSegment(segmentPath));
            paths.Add(AggregateIndexPathForSegment(segmentPath));
        }

        return paths.ToArray();
    }

    /// <summary>
    /// 枚举根目录下所有已落盘的 Segment 文件，返回 (SegmentId, FilePath) 元组序列。
    /// 同时兼容新版分层布局与旧版 <c>segments/</c> 平铺布局；若同一 SegmentId 同时存在，
    /// 优先返回新版分层路径。
    /// </summary>
    /// <param name="root">数据库根目录路径。</param>
    /// <returns>(SegmentId, FilePath) 元组的枚举序列（顺序不保证）。</returns>
    public static IEnumerable<(long SegmentId, string Path)> EnumerateSegments(string root)
    {
        string dir = SegmentsDir(root);
        if (!Directory.Exists(dir))
            yield break;

        var segments = new Dictionary<long, string>();

        foreach (string file in Directory.EnumerateFiles(dir, $"*{SegmentFileExtension}", SearchOption.TopDirectoryOnly))
        {
            if (TryParseSegmentId(System.IO.Path.GetFileName(file), out long segId))
                segments[segId] = file;
        }

        string layeredRoot = Path.Combine(dir, SegmentLayoutVersionDirName);
        if (Directory.Exists(layeredRoot))
        {
            foreach (string file in Directory.EnumerateFiles(layeredRoot, $"*{SegmentFileExtension}", SearchOption.AllDirectories))
            {
                if (TryParseSegmentId(System.IO.Path.GetFileName(file), out long segId))
                    segments[segId] = file;
            }
        }

        foreach (var pair in segments)
            yield return (pair.Key, pair.Value);
    }

    private static string SegmentFileName(long segmentId) =>
        $"{segmentId:X16}{SegmentFileExtension}";

    private static string SegmentBucketName(long segmentId)
    {
        long bucket = (segmentId - 1) / SegmentBucketSize;
        return $"{bucket:X16}";
    }

    private static string SegmentBucketGroupName(long segmentId)
    {
        long bucket = (segmentId - 1) / SegmentBucketSize;
        return $"{bucket & 0xFF:X2}";
    }
}
