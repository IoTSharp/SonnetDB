using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SonnetDB.Vector.Exceptions;
using SonnetDB.Vector.Format;
using SonnetDB.Vector.Model;

namespace SonnetDB.Vector.Index.DiskAnn;

/// <summary>
/// <c>vamana.bin</c>（DiskANN 图持久化文件）的读写工具。
/// </summary>
/// <remarks>
/// <para>
/// 文件布局（little-endian, Pack=1）：
/// <code>
/// [VamanaFileHeader   : 48 bytes]
/// 对每个 row r ∈ [0, NodeCount):
///   [VamanaNodeHeader : 8 bytes]
///   [uint[MaxDegree]  : 4·R bytes]   // 未使用槽位填 <see cref="EmptySlot"/> (0xFFFFFFFF)
/// </code>
/// </para>
/// <para>
/// 每个 node 条目定长 = <c>8 + 4·MaxDegree</c> 字节，便于 mmap 后按 row 直接计算偏移：
/// <c>offset(r) = sizeof(VamanaFileHeader) + r * (8 + 4·MaxDegree)</c>。
/// </para>
/// <para>
/// <see cref="VamanaFileHeader.InlineVectors"/> 当前固定为 0，向量数据保留在
/// 同 segment 目录下的 <c>vectors.bin</c>。
/// </para>
/// <para>
/// 全程使用 <see cref="MemoryMappedViewAccessor.Read{T}(long, out T)"/> /
/// <see cref="MemoryMappedViewAccessor.ReadArray{T}(long, T[], int, int)"/> 与
/// <see cref="System.Buffers.Binary.BinaryPrimitives"/> 等 Safe API，不使用 <c>unsafe</c>。
/// </para>
/// </remarks>
internal static class VamanaGraphIO
{
    /// <summary>未使用邻居槽位的哨兵值。</summary>
    public const uint EmptySlot = uint.MaxValue;

    /// <summary>表示"无入口点"（空索引）的 EntryPoint 值。</summary>
    public const uint NoEntryPoint = uint.MaxValue;

    private static int HeaderSize => Unsafe.SizeOf<VamanaFileHeader>();
    private static int NodeHeaderSize => Unsafe.SizeOf<VamanaNodeHeader>();

    /// <summary>
    /// 写入一个 <c>vamana.bin</c> 文件。采用 <c>.tmp + Move</c> 原子替换。
    /// </summary>
    public static void Write(
        string path,
        int dimensions,
        Metric metric,
        VamanaOptions options,
        int entryPoint,
        IReadOnlyList<int[]> neighbors,
        IReadOnlyCollection<int> tombstones)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(neighbors);
        ArgumentNullException.ThrowIfNull(tombstones);
        WriteCore(path, dimensions, options, metric, entryPoint, neighbors, tombstones);
    }

    private static void WriteCore(
        string path,
        int dimensions,
        VamanaOptions options,
        Metric metric,
        int entryPoint,
        IReadOnlyList<int[]> neighbors,
        IReadOnlyCollection<int> tombstones)
    {
        if (options.MaxDegree <= 0 || options.MaxDegree > ushort.MaxValue)
        {
            throw new SonnetDbVectorException($"非法 MaxDegree：{options.MaxDegree}。");
        }
        int maxDegree = options.MaxDegree;
        int nodeEntrySize = NodeHeaderSize + maxDegree * sizeof(uint);
        long total = (long)HeaderSize + (long)neighbors.Count * nodeEntrySize;

        // 头部
        VamanaFileHeader header = default;
        VamanaFileHeaderConstants.MagicAscii.CopyTo(
            MemoryMarshal.CreateSpan(ref Unsafe.As<Magic8, byte>(ref header.Magic), 8));
        header.Version = VamanaFileHeaderConstants.CurrentVersion;
        header.MaxDegree = (uint)maxDegree;
        header.Alpha = (float)options.Alpha;
        header.EntryPointId = entryPoint < 0 ? NoEntryPoint : (uint)entryPoint;
        header.NodeCount = (uint)neighbors.Count;
        header.Dimensions = (uint)dimensions;
        header.MetricKind = (byte)metric;
        header.InlineVectors = 0;

        // 写到临时缓冲，然后一次性落盘（小图直接 in-memory，大图分块）
        byte[] buffer = new byte[total];
        Span<byte> span = buffer;
        MemoryMarshal.Write(span, in header);
        int offset = HeaderSize;

        uint[] slotsScratch = new uint[maxDegree];

        for (int row = 0; row < neighbors.Count; row++)
        {
            int[] nbr = neighbors[row];
            if (nbr.Length > maxDegree)
            {
                throw new SonnetDbVectorException(
                    $"row {row} 邻居数 {nbr.Length} 超出 MaxDegree {maxDegree}。");
            }
            VamanaNodeHeader nh = default;
            nh.NodeId = (uint)row;
            nh.NeighborCount = (ushort)nbr.Length;
            nh.Tombstone = tombstones.Contains(row) ? (byte)1 : (byte)0;
            nh.Reserved0 = 0;
            MemoryMarshal.Write(span[offset..], in nh);
            offset += NodeHeaderSize;

            // 邻居 uint slots：有效条目 + 哨兵填充
            for (int i = 0; i < nbr.Length; i++)
            {
                int n = nbr[i];
                if (n < 0)
                {
                    throw new SonnetDbVectorException($"row {row} 出现负邻居 id {n}。");
                }
                slotsScratch[i] = (uint)n;
            }
            for (int i = nbr.Length; i < maxDegree; i++)
            {
                slotsScratch[i] = EmptySlot;
            }
            MemoryMarshal.AsBytes(slotsScratch).CopyTo(span[offset..]);
            offset += maxDegree * sizeof(uint);
        }

        // 原子写入
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        string tmp = path + ".tmp";
        using (FileStream fs = new(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(buffer, 0, buffer.Length);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// 通过 mmap 读取 <c>vamana.bin</c>，返回头部 + 每行邻居数组 + tombstone 集合。
    /// </summary>
    public static void Read(
        string path,
        out VamanaFileHeader header,
        out List<int[]> neighbors,
        out HashSet<int> tombstones)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            throw new SonnetDbVectorException($"vamana.bin 不存在：{path}");
        }

        FileInfo fi = new(path);
        if (fi.Length < HeaderSize)
        {
            throw new SonnetDbVectorException($"vamana.bin 损坏：长度 {fi.Length} 小于头部 {HeaderSize}。");
        }

        using MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        using MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(
            0, fi.Length, MemoryMappedFileAccess.Read);

        accessor.Read(0, out header);

        // Magic 校验
        ReadOnlySpan<byte> magic = MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<Magic8, byte>(ref header.Magic), 8);
        if (!magic[..4].SequenceEqual(VamanaFileHeaderConstants.MagicAscii))
        {
            throw new SonnetDbVectorException("vamana.bin 损坏：Magic 不匹配（期望 'DVAN'）。");
        }
        if (header.Version != VamanaFileHeaderConstants.CurrentVersion)
        {
            throw new SonnetDbVectorException(
                $"不支持的 vamana.bin 格式版本：{header.Version}（期望 {VamanaFileHeaderConstants.CurrentVersion}）。");
        }
        if (header.MaxDegree == 0 || header.MaxDegree > ushort.MaxValue)
        {
            throw new SonnetDbVectorException($"vamana.bin 损坏：非法 MaxDegree {header.MaxDegree}。");
        }

        int maxDegree = (int)header.MaxDegree;
        int nodeEntrySize = NodeHeaderSize + maxDegree * sizeof(uint);
        long expected = (long)HeaderSize + (long)header.NodeCount * nodeEntrySize;
        if (fi.Length != expected)
        {
            throw new SonnetDbVectorException(
                $"vamana.bin 长度 {fi.Length} 与预期 {expected} 不一致。");
        }

        neighbors = new List<int[]>((int)header.NodeCount);
        tombstones = new HashSet<int>();
        uint[] slotsBuf = new uint[maxDegree];

        long offset = HeaderSize;
        for (uint row = 0; row < header.NodeCount; row++)
        {
            accessor.Read(offset, out VamanaNodeHeader nh);
            offset += NodeHeaderSize;
            if (nh.NodeId != row)
            {
                throw new SonnetDbVectorException(
                    $"vamana.bin 损坏：row {row} 节点 NodeId={nh.NodeId} 不匹配。");
            }
            if (nh.NeighborCount > maxDegree)
            {
                throw new SonnetDbVectorException(
                    $"vamana.bin 损坏：row {row} NeighborCount {nh.NeighborCount} > MaxDegree {maxDegree}。");
            }
            accessor.ReadArray(offset, slotsBuf, 0, maxDegree);
            offset += (long)maxDegree * sizeof(uint);

            int count = nh.NeighborCount;
            int[] nbr = new int[count];
            for (int i = 0; i < count; i++)
            {
                uint v = slotsBuf[i];
                if (v == EmptySlot || v >= header.NodeCount)
                {
                    throw new SonnetDbVectorException(
                        $"vamana.bin 损坏：row {row} 邻居 #{i} 非法值 {v}。");
                }
                nbr[i] = (int)v;
            }
            neighbors.Add(nbr);

            if (nh.Tombstone != 0)
            {
                tombstones.Add((int)row);
            }
        }
    }
}
