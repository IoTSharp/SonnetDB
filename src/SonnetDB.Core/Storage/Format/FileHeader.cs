using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SonnetDB.Buffers;

namespace SonnetDB.Storage.Format;

/// <summary>
/// SonnetDB 预留容器文件头（固定 64 字节，当前目录型持久化未使用）。
/// <para>
/// 二进制布局（little-endian）：
/// <code>
/// Offset  Size  Field
/// 0       8     Magic              ("SONNETDB\0\0")
/// 8       4     FormatVersion      (当前 = 1)
/// 12      4     HeaderSize         (= 64)
/// 16      8     CreatedAtUtcTicks
/// 24      8     LastModifiedUtcTicks
/// 32      8     PageSize           (预留，第一版填 0)
/// 40      16    InstanceId         (GUID 字节)
/// 56      8     Reserved
/// ─────────────────────────────────
/// Total  64
/// </code>
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FileHeader
{
    /// <summary>文件 magic（"SONNETDB\0\0"，8 字节）。</summary>
    public InlineBytes8 Magic;

    /// <summary>文件格式版本号（当前 = <see cref="TsdbMagic.FormatVersion"/>）。</summary>
    public int FormatVersion;

    /// <summary>本头部的字节大小（= 64）。</summary>
    public int HeaderSize;

    /// <summary>文件创建时间（UTC Ticks）。</summary>
    public long CreatedAtUtcTicks;

    /// <summary>文件最后修改时间（UTC Ticks）。</summary>
    public long LastModifiedUtcTicks;

    /// <summary>页大小预留字段（当前目录型持久化填 0）。</summary>
    public long PageSize;

    /// <summary>实例标识（GUID 字节，第一版可填 0 或随机）。</summary>
    public InlineBytes16 InstanceId;

    /// <summary>保留字节（全 0）。</summary>
    public InlineBytes8 Reserved;

    /// <summary>
    /// 创建一个新的 <see cref="FileHeader"/>，自动填写 magic、版本号、当前 UTC 时间。
    /// </summary>
    /// <returns>已初始化的 <see cref="FileHeader"/> 实例。</returns>
    public static FileHeader CreateNew()
    {
        FileHeader h = default;
        TsdbMagic.File.CopyTo(h.Magic.AsSpan());
        h.FormatVersion = TsdbMagic.FormatVersion;
        h.HeaderSize = Unsafe.SizeOf<FileHeader>();
        long now = DateTime.UtcNow.Ticks;
        h.CreatedAtUtcTicks = now;
        h.LastModifiedUtcTicks = now;
        h.PageSize = 0;
        return h;
    }

    /// <summary>
    /// 校验文件头是否有效（magic 一致且版本匹配）。
    /// </summary>
    /// <returns>有效返回 <c>true</c>，否则返回 <c>false</c>。</returns>
    public readonly bool IsValid() =>
        Magic.AsReadOnlySpan().SequenceEqual(TsdbMagic.File) &&
        FormatVersion == TsdbMagic.FormatVersion;
}
