using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SonnetDB.Buffers;

namespace SonnetDB.Storage.Format;

/// <summary>
/// SonnetDB 目录文件（.SDBCAT）的文件头（固定 64 字节）。
/// <para>
/// 二进制布局（little-endian）：
/// <code>
/// Offset  Size  Field
/// 0       8     Magic              ("SDBCATv1")
/// 8       4     FormatVersion      (1 = legacy, 2 = 整点 WAL 目录)
/// 12      4     HeaderSize         (= 64)
/// 16      8     CreatedAtUtcTicks
/// 24      8     LastModifiedUtcTicks
/// 32      4     EntryCount
/// 36      4     Reserved0
/// 40      24    Reserved
/// ─────────────────────────────────
/// Total  64
/// </code>
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CatalogFileHeader
{
    /// <summary>目录文件 magic（"SDBCATv1"，8 字节）。</summary>
    public InlineBytes8 Magic;

    /// <summary>文件格式版本号；1 为 legacy，2 声明默认整点 WAL 的目录降级屏障。</summary>
    public int FormatVersion;

    /// <summary>本头部的字节大小（= <see cref="FormatSizes.CatalogFileHeaderSize"/>）。</summary>
    public int HeaderSize;

    /// <summary>文件创建时间（UTC Ticks）。</summary>
    public long CreatedAtUtcTicks;

    /// <summary>文件最后修改时间（UTC Ticks）。</summary>
    public long LastModifiedUtcTicks;

    /// <summary>目录中的序列条目数量。</summary>
    public int EntryCount;

    /// <summary>保留字段（填 0）。</summary>
    public uint Reserved0;

    /// <summary>保留字节（全 0，24 字节）。</summary>
    public InlineBytes24 Reserved;

    /// <summary>
    /// 创建一个新的 <see cref="CatalogFileHeader"/>，自动填写 magic、版本号、当前 UTC 时间和条目数量。
    /// </summary>
    /// <param name="entryCount">目录中的条目数量。</param>
    /// <returns>已初始化的 <see cref="CatalogFileHeader"/> 实例。</returns>
    public static CatalogFileHeader CreateNew(int entryCount)
    {
        CatalogFileHeader h = default;
        TsdbMagic.Catalog.CopyTo(h.Magic.AsSpan());
        h.FormatVersion = TsdbMagic.FormatVersion;
        h.HeaderSize = Unsafe.SizeOf<CatalogFileHeader>();
        long now = DateTime.UtcNow.Ticks;
        h.CreatedAtUtcTicks = now;
        h.LastModifiedUtcTicks = now;
        h.EntryCount = entryCount;
        return h;
    }

    /// <summary>
    /// 校验文件头是否有效（magic 一致、版本匹配、头部大小正确）。
    /// </summary>
    /// <returns>有效返回 <c>true</c>，否则返回 <c>false</c>。</returns>
    public readonly bool IsValid() =>
        Magic.AsReadOnlySpan().SequenceEqual(TsdbMagic.Catalog) &&
        FormatVersion is TsdbMagic.FormatVersion or 2 &&
        HeaderSize == FormatSizes.CatalogFileHeaderSize;
}
