using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SonnetDB.Vector.Format;

/// <summary>
/// Vamana / DiskANN 索引文件（<c>index.bin</c>）的根头部（little-endian, Pack = 1）。
/// </summary>
/// <remarks>
/// <para>
/// 文件布局：
/// <code>
/// [VamanaFileHeader]
/// [Node 0 : VamanaNodeHeader + uint[MaxDegree] neighbors (+ float[Dimensions] vector if InlineVectors=1)]
/// [Node 1 : ...]
/// ...
/// </code>
/// </para>
/// <para>
/// Magic = "DVAN\0\0\0\0"（8 字节 ASCII，前 4 字节为 <c>D V A N</c>）。
/// 修改本结构体布局时必须同步升级 <see cref="Version"/> 与 <see cref="FileHeader.Version"/> 并更新 CHANGELOG。
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VamanaFileHeader
{
    /// <summary>Magic 标识符，固定值 "DVAN\0\0\0\0"（8 字节 ASCII）。</summary>
    public Magic8 Magic;

    /// <summary>格式版本号，当前为 1。布局变更时必须递增。</summary>
    public uint Version;

    /// <summary>每个节点最大邻居数（R）。</summary>
    public uint MaxDegree;

    /// <summary>RobustPrune 的 alpha 系数（典型 1.2）。</summary>
    public float Alpha;

    /// <summary>入口点节点 ID（搜索从此节点出发）。</summary>
    public uint EntryPointId;

    /// <summary>节点总数。</summary>
    public uint NodeCount;

    /// <summary>向量维度。</summary>
    public uint Dimensions;

    /// <summary>距离度量类型，对应 <see cref="Model.Metric"/>。</summary>
    public byte MetricKind;

    /// <summary>是否在 index.bin 中内联存储向量。0 = 仅图结构，向量另存于 vectors.bin；1 = 内联（DiskANN 经典布局）。</summary>
    public byte InlineVectors;

    /// <summary>保留字段，必须填 0。</summary>
    public Reserved14 Reserved;
}

/// <summary>
/// 14 字节保留缓冲，供 <see cref="VamanaFileHeader"/> 对齐与未来扩展。
/// </summary>
[InlineArray(14)]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Reserved14
{
    private byte _e0;
}

/// <summary>
/// <see cref="VamanaFileHeader"/> 相关常量。
/// </summary>
public static class VamanaFileHeaderConstants
{
    /// <summary>Magic ASCII 字符串 "DVAN"（前 4 字节）。</summary>
    public static ReadOnlySpan<byte> MagicAscii => "DVAN"u8;

    /// <summary>当前 Vamana 文件格式版本。</summary>
    public const uint CurrentVersion = 1;
}
