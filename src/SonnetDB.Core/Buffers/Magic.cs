namespace SonnetDB.Buffers;

/// <summary>SonnetDB 文件格式魔数与版本常量。</summary>
public static class TsdbMagic
{
    /// <summary>主文件 magic："SONNETDB"（8 字节 ASCII，无填充）。</summary>
    public static ReadOnlySpan<byte> File => "SONNETDB"u8;

    /// <summary>段文件 magic："SDBSEGv1"（8 字节）。</summary>
    public static ReadOnlySpan<byte> Segment => "SDBSEGv1"u8;

    /// <summary>WAL 文件 magic："SDBWALv1"（8 字节）。</summary>
    public static ReadOnlySpan<byte> Wal => "SDBWALv1"u8;

    /// <summary>目录文件 magic："SDBCATv1"（8 字节）。</summary>
    public static ReadOnlySpan<byte> Catalog => "SDBCATv1"u8;

    /// <summary>当前 SonnetDB 容器文件格式版本号（FileHeader / WAL / Catalog 等通用文件）。</summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// 段文件（<c>.SDBSEG</c>）格式版本号。
    /// <para>
    /// v2（PR #50）：<see cref="SonnetDB.Storage.Format.BlockHeader"/> 中 <c>AggregateMin</c> /
    /// <c>AggregateMax</c> 由 4 字节窄类型位模式（Float64 走 <c>float</c>、Int64 走 <c>int32</c>）
    /// 升级为 8 字节 <see cref="double"/>，无损覆盖 Float64 / Int64 全部范围；
    /// BlockHeader 大小由 64B 增至 72B；旧 v1 段文件被 <c>SegmentReader</c> 拒绝。
    /// </para>
    /// <para>
    /// v3（PR #58 c）：在 <see cref="SonnetDB.Storage.Format.BlockEncoding"/> 中新增
    /// <see cref="SonnetDB.Storage.Format.BlockEncoding.VectorRaw"/> 标记位，并允许
    /// <see cref="SonnetDB.Storage.Format.BlockHeader.FieldType"/> 取
    /// <see cref="SonnetDB.Storage.Format.FieldType.Vector"/>。Block 整体结构与
    /// <see cref="SonnetDB.Storage.Format.BlockHeader"/> 大小不变；仅会在新增 VECTOR 列时实际写入。
    /// 读取层兼容 v2（v2 段文件不会包含 Vector Block，按原路径读取）。
    /// </para>
    /// <para>
    /// v4（PR #70）：新增 <see cref="SonnetDB.Storage.Format.FieldType.GeoPoint"/> 与
    /// <see cref="SonnetDB.Storage.Format.BlockEncoding.GeoPointRaw"/>，每个点按
    /// <c>lat(8) + lon(8)</c> 小端 float64 固定 16 字节编码。BlockHeader 布局不变；
    /// 读取层兼容 v3（v3 段文件不会包含 GeoPoint Block）。
    /// </para>
    /// <para>
    /// v5（PR #76）：<see cref="SonnetDB.Storage.Format.BlockHeader"/> 新增
    /// <c>GeoHashMin</c> / <c>GeoHashMax</c> 两个 32-bit geohash 前缀字段，BlockHeader
    /// 大小由 72B 增至 80B，用于 GEOPOINT Block 级空间剪枝；读取层兼容 v4。
    /// </para>
    /// <para>
    /// v6：<see cref="SonnetDB.Storage.Format.SegmentHeader"/> 的保留区写入
    /// SegmentFooter mini-footer 摘要副本，主 <see cref="SonnetDB.Storage.Format.SegmentFooter"/>
    /// 仍保持 64B 并位于文件末尾；旧 `.SDBVIDX` / `.SDBAIDX` sidecar 对应的 HNSW 与
    /// 聚合 sketch section 改为写入主 `.SDBSEG` 的索引区与 Footer 之间。读取层兼容 v4/v5，
    /// v6 在尾部 footer 损坏时可提供更精确诊断。
    /// </para>
    /// <para>
    /// v7：新增独立 Int64Rle 值编码，旧 DeltaValue Int64 仍为原始 8 字节。
    /// Header/BlockHeader/Footer 布局不变；读取层兼容 v2-v6，旧引擎明确拒绝 v7。
    /// </para>
    /// </summary>
    public const int SegmentFormatVersion = 7;

    /// <summary>
    /// SegmentReader 允许读取的段格式版本集合（含历史只读兼容版本）。
    /// </summary>
    public static ReadOnlySpan<int> SupportedSegmentFormatVersions => [2, 3, 4, 5, 6, 7];

    /// <summary>构造文件 magic <see cref="InlineBytes8"/>。</summary>
    /// <returns>内容为 <c>"SONNETDB"</c>（8 字节 ASCII）的 <see cref="InlineBytes8"/> 实例。</returns>
    public static InlineBytes8 CreateFileMagic()
    {
        InlineBytes8 magic = default;
        File.CopyTo(magic.AsSpan());
        return magic;
    }

    /// <summary>构造段 magic <see cref="InlineBytes8"/>。</summary>
    /// <returns>内容为 <c>"SDBSEGv1"</c> 的 <see cref="InlineBytes8"/> 实例。</returns>
    public static InlineBytes8 CreateSegmentMagic()
    {
        InlineBytes8 magic = default;
        Segment.CopyTo(magic.AsSpan());
        return magic;
    }

    /// <summary>构造 WAL magic <see cref="InlineBytes8"/>。</summary>
    /// <returns>内容为 <c>"SDBWALv1"</c> 的 <see cref="InlineBytes8"/> 实例。</returns>
    public static InlineBytes8 CreateWalMagic()
    {
        InlineBytes8 magic = default;
        Wal.CopyTo(magic.AsSpan());
        return magic;
    }

    /// <summary>构造目录 magic <see cref="InlineBytes8"/>。</summary>
    /// <returns>内容为 <c>"SDBCATv1"</c> 的 <see cref="InlineBytes8"/> 实例。</returns>
    public static InlineBytes8 CreateCatalogMagic()
    {
        InlineBytes8 magic = default;
        Catalog.CopyTo(magic.AsSpan());
        return magic;
    }
}
