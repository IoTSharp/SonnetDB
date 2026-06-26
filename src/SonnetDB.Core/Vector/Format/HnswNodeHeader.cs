using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SonnetDB.Vector.Format;

/// <summary>
/// HNSW 单个节点持久化时的固定头部（little-endian, Pack = 1）。
/// </summary>
/// <remarks>
/// <para>
/// 设计用于 M5 持久化阶段，每个节点在 <c>index.bin</c> 中按 [Header][Neighbors per layer ...] 顺序存储。
/// 每层邻居为 <see cref="uint"/> 数组，长度由 <see cref="NeighborCounts"/> 指定（按层顺序），
/// 节点占用总字节数 = sizeof(HnswNodeHeader) + ∑NeighborCounts[i] * 4。
/// </para>
/// <para>
/// 修改本结构体布局时必须同步升级 <see cref="FileHeader.Version"/> 并更新 CHANGELOG。
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HnswNodeHeader
{
    /// <summary>节点 ID（与对应行号一致）。</summary>
    public uint NodeId;

    /// <summary>节点最高层级，邻居数组的层数 = <see cref="Level"/> + 1。</summary>
    public byte Level;

    /// <summary>是否被标记为已删除（tombstone）。0 = 有效，1 = 已删除。</summary>
    public byte Tombstone;

    /// <summary>保留对齐字节，必须为 0。</summary>
    public ushort Reserved0;

    /// <summary>各层邻居数量（最多 16 层，超出层数填 0）。</summary>
    public NeighborCounts16 NeighborCounts;
}

/// <summary>
/// 16 层 ushort 邻居数量内联缓冲，供 <see cref="HnswNodeHeader"/> 使用。
/// </summary>
[InlineArray(16)]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NeighborCounts16
{
    private ushort _e0;
}
