using System.Runtime.InteropServices;
using SonnetDB.Buffers;

namespace SonnetDB.Storage.Format;

/// <summary>
/// SonnetDB WAL 文件（.SDBWAL）的文件头（固定 64 字节）。
/// <para>
/// 二进制布局（little-endian）：
/// <code>
/// Offset  Size  Field
/// 0       8     Magic              ("SDBWALv1")
/// 8       4     FormatVersion      (当前 = 1)
/// 12      4     HeaderSize         (= 64)
/// 16      8     CreatedAtUtcTicks
/// 24      8     FirstLsn           (本文件首条记录的 LSN)
/// 32      32    Reserved           (填 0)
/// ─────────────────────────────────
/// Total  64
/// </code>
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WalFileHeader
{
    internal const int AtomicPointFormatVersion = 2;

    /// <summary>WAL 文件 magic（"SDBWALv1"，8 字节）。</summary>
    public InlineBytes8 Magic;

    /// <summary>文件格式版本号（当前 = <see cref="TsdbMagic.FormatVersion"/>）。</summary>
    public int FormatVersion;

    /// <summary>本头部的字节大小（= <see cref="FormatSizes.WalFileHeaderSize"/>）。</summary>
    public int HeaderSize;

    /// <summary>文件创建时间（UTC Ticks）。</summary>
    public long CreatedAtUtcTicks;

    /// <summary>本文件首条 WAL 记录的 LSN（日志序列号）。</summary>
    public long FirstLsn;

    /// <summary>保留字节（全 0，32 字节）。</summary>
    public InlineBytes32 Reserved;

    /// <summary>
    /// 创建一个新的 <see cref="WalFileHeader"/>，自动填写 magic、版本号和创建时间。
    /// </summary>
    /// <param name="firstLsn">本文件首条记录的 LSN。</param>
    /// <returns>已初始化的 <see cref="WalFileHeader"/> 实例。</returns>
    public static WalFileHeader CreateNew(long firstLsn)
    {
        WalFileHeader h = default;
        TsdbMagic.Wal.CopyTo(h.Magic.AsSpan());
        h.FormatVersion = TsdbMagic.FormatVersion;
        h.HeaderSize = FormatSizes.WalFileHeaderSize;
        h.CreatedAtUtcTicks = DateTime.UtcNow.Ticks;
        h.FirstLsn = firstLsn;
        return h;
    }

    internal static WalFileHeader CreateNewVersion(long firstLsn, int formatVersion)
    {
        WalFileHeader h = CreateNew(firstLsn);
        h.FormatVersion = formatVersion;
        return h;
    }

    /// <summary>
    /// 校验文件头是否有效（magic 一致、版本匹配、头部大小正确）。
    /// </summary>
    /// <returns>有效返回 <c>true</c>，否则返回 <c>false</c>。</returns>
    public readonly bool IsValid() =>
        Magic.AsReadOnlySpan().SequenceEqual(TsdbMagic.Wal) &&
        FormatVersion == TsdbMagic.FormatVersion &&
        HeaderSize == FormatSizes.WalFileHeaderSize;

    internal readonly bool IsValidFor(int formatVersion) =>
        Magic.AsReadOnlySpan().SequenceEqual(TsdbMagic.Wal) &&
        FormatVersion == formatVersion &&
        HeaderSize == FormatSizes.WalFileHeaderSize &&
        (formatVersion != AtomicPointFormatVersion || (FirstLsn > 0 && FirstLsn < long.MaxValue));
}
