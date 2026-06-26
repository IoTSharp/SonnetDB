using System.Runtime.InteropServices;

namespace SonnetDB.Vector.Format;

/// <summary>
/// Vamana / DiskANN 单个节点持久化时的固定头部（little-endian, Pack = 1）。
/// </summary>
/// <remarks>
/// <para>
/// 设计用于 M12 持久化阶段，每个节点在 <c>index.bin</c> 中按
/// <c>[VamanaNodeHeader][uint[R] neighbors]</c>（可选附加 <c>[float[D] vector]</c>）顺序紧排存储。
/// 节点定长 = sizeof(VamanaNodeHeader) + R * 4 (+ D * 4 if inline vectors)。
/// </para>
/// <para>
/// 修改本结构体布局时必须同步升级 <see cref="FileHeader.Version"/> 与
/// <see cref="VamanaFileHeader.Version"/> 并更新 CHANGELOG。
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VamanaNodeHeader
{
    /// <summary>节点 ID（与对应行号一致）。</summary>
    public uint NodeId;

    /// <summary>实际邻居数量（≤ <see cref="VamanaFileHeader.MaxDegree"/>）。</summary>
    public ushort NeighborCount;

    /// <summary>是否被标记为已删除（tombstone）。0 = 有效，1 = 已删除。</summary>
    public byte Tombstone;

    /// <summary>保留对齐字节，必须为 0。</summary>
    public byte Reserved0;
}
