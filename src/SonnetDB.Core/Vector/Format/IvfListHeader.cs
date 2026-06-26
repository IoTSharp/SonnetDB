using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SonnetDB.Vector.Format;

/// <summary>
/// IVF（Inverted File）单个倒排列表持久化时的固定头部（little-endian, Pack = 1）。
/// </summary>
/// <remarks>
/// <para>
/// 设计用于 M5 持久化阶段，每个倒排列表在 <c>index.bin</c> 中按
/// [Header][PostingPayload] 顺序存储。
/// PostingPayload 的字节布局由 <see cref="ListKind"/> 决定：
/// <list type="bullet">
///   <item><see cref="IvfListKind.Flat"/>：4 字节行号 + 维度 × 4 字节 float 向量，共 <see cref="VectorCount"/> 条。</item>
///   <item><see cref="IvfListKind.Pq"/>：4 字节行号 + <see cref="CodeBytes"/> 字节 PQ 编码，共 <see cref="VectorCount"/> 条。</item>
/// </list>
/// </para>
/// <para>
/// 修改本结构体布局时必须同步升级 <see cref="FileHeader.Version"/> 并更新 CHANGELOG。
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct IvfListHeader
{
    /// <summary>列表 ID（即聚类中心索引）。</summary>
    public uint ListId;

    /// <summary>本列表中的向量条数。</summary>
    public uint VectorCount;

    /// <summary>PostingPayload 在 <c>index.bin</c> 中的起始字节偏移。</summary>
    public long DataOffset;

    /// <summary>向量维度（与所在集合一致）。</summary>
    public uint Dimensions;

    /// <summary>列表存储格式：<see cref="IvfListKind.Flat"/> 或 <see cref="IvfListKind.Pq"/>。</summary>
    public byte ListKind;

    /// <summary>每条 PQ 编码的字节数（IVF-PQ 的 M），<see cref="ListKind"/> 为 Flat 时填 0。</summary>
    public byte CodeBytes;

    /// <summary>保留字段，必须为 0。</summary>
    public ushort Reserved0;

    /// <summary>保留字段，必须为 0。</summary>
    public uint Reserved1;

    /// <summary>
    /// 将头部以 little-endian 写入 <paramref name="destination"/>。
    /// </summary>
    /// <param name="destination">目标字节缓冲，长度需 ≥ <see cref="Size"/>。</param>
    public readonly void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException(
                $"目标缓冲长度不足：需要 ≥ {Size}，实际 {destination.Length}。",
                nameof(destination));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], ListId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4, 4), VectorCount);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(8, 8), DataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(16, 4), Dimensions);
        destination[20] = ListKind;
        destination[21] = CodeBytes;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(22, 2), Reserved0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(24, 4), Reserved1);
    }

    /// <summary>
    /// 从 little-endian 字节缓冲读取一个头部。
    /// </summary>
    /// <param name="source">源字节缓冲，长度需 ≥ <see cref="Size"/>。</param>
    public static IvfListHeader ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
        {
            throw new ArgumentException(
                $"源缓冲长度不足：需要 ≥ {Size}，实际 {source.Length}。",
                nameof(source));
        }
        return new IvfListHeader
        {
            ListId = BinaryPrimitives.ReadUInt32LittleEndian(source[..4]),
            VectorCount = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4, 4)),
            DataOffset = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(8, 8)),
            Dimensions = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(16, 4)),
            ListKind = source[20],
            CodeBytes = source[21],
            Reserved0 = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(22, 2)),
            Reserved1 = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(24, 4)),
        };
    }

    /// <summary>头部固定字节大小（28 字节）。</summary>
    public const int Size = 28;
}

/// <summary>
/// IVF 倒排列表的存储格式标识。
/// </summary>
public enum IvfListKind : byte
{
    /// <summary>原始 float32 向量存储（IVF-Flat）。</summary>
    Flat = 0,

    /// <summary>乘积量化编码存储（IVF-PQ）。</summary>
    Pq = 1,
}
