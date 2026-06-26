using System.Buffers.Binary;
using SonnetDB.Vector.Exceptions;
using SonnetDB.Vector.Model;

namespace SonnetDB.Vector.Index.Hnsw;

/// <summary>
/// <c>hnsw.bin</c>（HNSW 图持久化文件）的读写工具。
/// </summary>
/// <remarks>
/// <para>文件布局（little-endian）：</para>
/// <code>
/// [Header: 48 bytes]
///   Magic[8]        ASCII "DHNSW\0\0\0"
///   Version         uint32  (当前 1)
///   Dimensions      uint32
///   Metric          byte
///   Reserved[3]
///   M               uint32  (HnswOptions.M)
///   EfConstruction  uint32
///   EfSearch        uint32
///   EntryPoint      int32   (-1 = 空索引)
///   EntryLevel      int32   (-1 = 空索引)
///   NodeCount       uint32
///
/// 对每个 node r ∈ [0, NodeCount):
///   Level           int32   (本节点顶层，0 ≤ Level)
///   Tombstone       byte
///   Reserved[3]
///   对每层 L ∈ [0, Level]:
///     NeighborCount uint32
///     Neighbors     int32[NeighborCount]
/// </code>
/// <para>HNSW 每个节点的层数与每层邻居数都可变，所以采用顺序读写（不可 mmap+定长偏移）。</para>
/// </remarks>
internal static class HnswGraphIO
{
    private static readonly byte[] MagicAscii = "DHNSW\0\0\0"u8.ToArray();
    public const uint CurrentVersion = 1;
    public const int NoEntryPoint = -1;
    public const int NoEntryLevel = -1;
    public const int HeaderSize = 48;

    /// <summary>原子写入 <c>hnsw.bin</c>（.tmp + Move）。</summary>
    public static void Write(
        string path,
        int dimensions,
        Metric metric,
        HnswOptions options,
        int entryPoint,
        int entryLevel,
        IReadOnlyList<int> levels,
        IReadOnlyList<int[][]> neighbors,
        IReadOnlyCollection<int> tombstones)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(neighbors);
        ArgumentNullException.ThrowIfNull(tombstones);
        if (levels.Count != neighbors.Count)
        {
            throw new SonnetDbVectorException(
                $"hnsw.bin: levels.Count={levels.Count} 与 neighbors.Count={neighbors.Count} 不一致。");
        }

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            WriteHeader(fs, dimensions, metric, options, entryPoint, entryLevel, neighbors.Count);

            Span<byte> nodeHdrBuf = stackalloc byte[8];
            Span<byte> uintBuf = stackalloc byte[4];
            for (int row = 0; row < neighbors.Count; row++)
            {
                int level = levels[row];
                int[][] rowLayers = neighbors[row];
                if (level < 0 || rowLayers.Length != level + 1)
                {
                    throw new SonnetDbVectorException(
                        $"hnsw.bin: row {row} level={level} 与 rowLayers.Length={rowLayers.Length} 不一致。");
                }

                BinaryPrimitives.WriteInt32LittleEndian(nodeHdrBuf, level);
                nodeHdrBuf[4] = tombstones.Contains(row) ? (byte)1 : (byte)0;
                nodeHdrBuf[5] = 0;
                nodeHdrBuf[6] = 0;
                nodeHdrBuf[7] = 0;
                fs.Write(nodeHdrBuf);

                for (int layer = 0; layer <= level; layer++)
                {
                    int[] nbr = rowLayers[layer];
                    BinaryPrimitives.WriteUInt32LittleEndian(uintBuf, (uint)nbr.Length);
                    fs.Write(uintBuf);
                    if (nbr.Length == 0) continue;

                    byte[] bytes = new byte[nbr.Length * sizeof(int)];
                    Span<byte> bs = bytes;
                    for (int i = 0; i < nbr.Length; i++)
                    {
                        if (nbr[i] < 0 || nbr[i] >= neighbors.Count)
                        {
                            throw new SonnetDbVectorException(
                                $"hnsw.bin: row {row} layer {layer} neighbor #{i} = {nbr[i]} 越界。");
                        }
                        BinaryPrimitives.WriteInt32LittleEndian(bs.Slice(i * sizeof(int), sizeof(int)), nbr[i]);
                    }
                    fs.Write(bytes);
                }
            }

            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>顺序读 <c>hnsw.bin</c>。返回所有节点的 level、邻居和 tombstone 集合。</summary>
    public static void Read(
        string path,
        out int dimensions,
        out Metric metric,
        out HnswOptions options,
        out int entryPoint,
        out int entryLevel,
        out int[] levels,
        out int[][][] neighbors,
        out HashSet<int> tombstones)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            throw new SonnetDbVectorException($"hnsw.bin 不存在：{path}");

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> headerBuf = stackalloc byte[HeaderSize];
        if (fs.Read(headerBuf) != HeaderSize)
            throw new SonnetDbVectorException("hnsw.bin 损坏：header 不完整。");

        ReadOnlySpan<byte> magic = headerBuf.Slice(0, 8);
        if (!magic.SequenceEqual(MagicAscii))
            throw new SonnetDbVectorException("hnsw.bin 损坏：Magic 不匹配（期望 'DHNSW'）。");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.Slice(8, 4));
        if (version != CurrentVersion)
            throw new SonnetDbVectorException($"hnsw.bin 不支持的格式版本 {version}（期望 {CurrentVersion}）。");

        dimensions = (int)BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.Slice(12, 4));
        byte metricByte = headerBuf[16];
        metric = (Metric)metricByte;
        int m = (int)BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.Slice(20, 4));
        int efC = (int)BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.Slice(24, 4));
        int efS = (int)BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.Slice(28, 4));
        entryPoint = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.Slice(32, 4));
        entryLevel = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.Slice(36, 4));
        int nodeCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.Slice(40, 4));

        if (nodeCount < 0) throw new SonnetDbVectorException($"hnsw.bin: 非法 NodeCount={nodeCount}。");
        options = new HnswOptions { M = m, EfConstruction = efC, EfSearch = efS };

        levels = new int[nodeCount];
        neighbors = new int[nodeCount][][];
        tombstones = new HashSet<int>();

        Span<byte> nodeHdrBuf = stackalloc byte[8];
        Span<byte> uintBuf = stackalloc byte[4];
        for (int row = 0; row < nodeCount; row++)
        {
            if (fs.Read(nodeHdrBuf) != nodeHdrBuf.Length)
                throw new SonnetDbVectorException($"hnsw.bin: row {row} node 头不完整。");
            int level = BinaryPrimitives.ReadInt32LittleEndian(nodeHdrBuf.Slice(0, 4));
            byte tombstone = nodeHdrBuf[4];
            if (level < 0)
                throw new SonnetDbVectorException($"hnsw.bin: row {row} 非法 level={level}。");

            levels[row] = level;
            if (tombstone != 0) tombstones.Add(row);

            var rowLayers = new int[level + 1][];
            for (int layer = 0; layer <= level; layer++)
            {
                if (fs.Read(uintBuf) != uintBuf.Length)
                    throw new SonnetDbVectorException($"hnsw.bin: row {row} layer {layer} 计数缺失。");
                int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(uintBuf);
                if (count < 0 || count > nodeCount)
                    throw new SonnetDbVectorException($"hnsw.bin: row {row} layer {layer} 非法 neighbor 数 {count}。");

                int[] nbr = new int[count];
                if (count > 0)
                {
                    byte[] bytes = new byte[count * sizeof(int)];
                    if (fs.Read(bytes, 0, bytes.Length) != bytes.Length)
                        throw new SonnetDbVectorException($"hnsw.bin: row {row} layer {layer} 邻居体不完整。");
                    Span<byte> bs = bytes;
                    for (int i = 0; i < count; i++)
                    {
                        int n = BinaryPrimitives.ReadInt32LittleEndian(bs.Slice(i * sizeof(int), sizeof(int)));
                        if (n < 0 || n >= nodeCount)
                            throw new SonnetDbVectorException($"hnsw.bin: row {row} layer {layer} neighbor #{i}={n} 越界。");
                        nbr[i] = n;
                    }
                }
                rowLayers[layer] = nbr;
            }
            neighbors[row] = rowLayers;
        }
    }

    private static void WriteHeader(
        FileStream fs,
        int dimensions,
        Metric metric,
        HnswOptions options,
        int entryPoint,
        int entryLevel,
        int nodeCount)
    {
        Span<byte> hdr = stackalloc byte[HeaderSize];
        MagicAscii.AsSpan().CopyTo(hdr);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(8, 4), CurrentVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(12, 4), (uint)dimensions);
        hdr[16] = (byte)metric;
        hdr[17] = hdr[18] = hdr[19] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(20, 4), (uint)options.M);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(24, 4), (uint)options.EfConstruction);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(28, 4), (uint)options.EfSearch);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(32, 4), entryPoint);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(36, 4), entryLevel);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(40, 4), (uint)nodeCount);
        // 44..47 reserved (zero)
        fs.Write(hdr);
    }
}
