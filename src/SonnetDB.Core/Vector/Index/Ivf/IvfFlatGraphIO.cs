using System.Buffers.Binary;
using SonnetDB.Vector.Exceptions;
using SonnetDB.Vector.Model;

namespace SonnetDB.Vector.Index.Ivf;

/// <summary>
/// <c>ivfflat.bin</c>（IVF-Flat 倒排文件 + 训练状态持久化）的读写工具。
/// </summary>
/// <remarks>
/// <para>文件布局（little-endian, IsTrained=1 时附 centroids + inverted lists）：</para>
/// <code>
/// [Header: 48 bytes]
///   Magic[8]        ASCII "DIVF-F\0\0"
///   Version         uint32  (当前 1)
///   Dimensions      uint32
///   Metric          byte
///   IsTrained       byte
///   Reserved[2]
///   NList           uint32
///   NProbe          uint32
///   MaxIterations   uint32
///   RowCount        uint32
///   HasSeed         byte (0/1)
///   Reserved[3]
///   Seed            int32
///
/// rowToList: int32[RowCount]
///
/// 仅 IsTrained=1 时追加：
///   centroids: float[NList * Dimensions]
///   对每个 list ℓ ∈ [0, NList):
///     ListLen     uint32
///     RowIds      int32[ListLen]
/// </code>
/// <para>向量本身保留在同 segment 目录下的 <c>vectors.bin</c>（由 SegmentWriter 写出）。</para>
/// </remarks>
internal static class IvfFlatGraphIO
{
    private static readonly byte[] MagicAscii = "DIVF-F\0\0"u8.ToArray();
    public const uint CurrentVersion = 1;
    public const int HeaderSize = 48;

    public static void Write<TKey>(string path, IvfFlatIndexSnapshot<TKey> snapshot) where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(snapshot);

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            WriteHeader(fs, snapshot);
            WriteIntArray(fs, snapshot.RowToList);

            if (snapshot.IsTrained)
            {
                if (snapshot.Centroids is null || snapshot.InvertedLists is null)
                    throw new SonnetDbVectorException("ivfflat.bin: snapshot 已训练但缺少 centroids 或 inverted lists。");
                if (snapshot.Centroids.Length != checked(snapshot.Options.NList * snapshot.Dimensions))
                    throw new SonnetDbVectorException("ivfflat.bin: centroids 长度与 NList × Dimensions 不一致。");

                WriteFloatArray(fs, snapshot.Centroids);
                Span<byte> uintBuf = stackalloc byte[4];
                foreach (int[] listRows in snapshot.InvertedLists)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(uintBuf, (uint)listRows.Length);
                    fs.Write(uintBuf);
                    if (listRows.Length > 0)
                        WriteIntArray(fs, listRows);
                }
            }
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static void Read(
        string path,
        out int dimensions,
        out Metric metric,
        out IvfOptions options,
        out bool isTrained,
        out int rowCount,
        out int[] rowToList,
        out float[]? centroids,
        out int[][]? invertedLists)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path)) throw new SonnetDbVectorException($"ivfflat.bin 不存在：{path}");

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> hdr = stackalloc byte[HeaderSize];
        if (fs.Read(hdr) != HeaderSize)
            throw new SonnetDbVectorException("ivfflat.bin 损坏：header 不完整。");

        if (!hdr.Slice(0, 8).SequenceEqual(MagicAscii))
            throw new SonnetDbVectorException("ivfflat.bin 损坏：Magic 不匹配（期望 'DIVF-F'）。");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(8, 4));
        if (version != CurrentVersion)
            throw new SonnetDbVectorException($"ivfflat.bin 不支持的格式版本 {version}（期望 {CurrentVersion}）。");

        dimensions = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(12, 4));
        metric = (Metric)hdr[16];
        isTrained = hdr[17] != 0;
        int nList = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(20, 4));
        int nProbe = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(24, 4));
        int maxIter = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(28, 4));
        rowCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(32, 4));
        bool hasSeed = hdr[36] != 0;
        int seed = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(40, 4));
        if (dimensions <= 0 || nList <= 0 || rowCount < 0)
            throw new SonnetDbVectorException("ivfflat.bin 损坏：非法 header 字段。");

        options = new IvfOptions
        {
            NList = nList,
            NProbe = nProbe,
            MaxIterations = maxIter,
            Seed = hasSeed ? seed : null,
        };

        rowToList = ReadIntArray(fs, rowCount);
        centroids = null;
        invertedLists = null;

        if (isTrained)
        {
            centroids = ReadFloatArray(fs, nList * dimensions);
            invertedLists = new int[nList][];
            Span<byte> uintBuf = stackalloc byte[4];
            for (int i = 0; i < nList; i++)
            {
                if (fs.Read(uintBuf) != uintBuf.Length)
                    throw new SonnetDbVectorException($"ivfflat.bin: list {i} 长度缺失。");
                int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(uintBuf);
                if (len < 0 || len > rowCount)
                    throw new SonnetDbVectorException($"ivfflat.bin: list {i} 非法长度 {len}。");
                invertedLists[i] = len == 0 ? [] : ReadIntArray(fs, len);
            }
        }
    }

    private static void WriteHeader<TKey>(FileStream fs, IvfFlatIndexSnapshot<TKey> snapshot) where TKey : notnull
    {
        Span<byte> hdr = stackalloc byte[HeaderSize];
        MagicAscii.AsSpan().CopyTo(hdr);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(8, 4), CurrentVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(12, 4), (uint)snapshot.Dimensions);
        hdr[16] = (byte)snapshot.Metric;
        hdr[17] = snapshot.IsTrained ? (byte)1 : (byte)0;
        hdr[18] = hdr[19] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(20, 4), (uint)snapshot.Options.NList);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(24, 4), (uint)snapshot.Options.NProbe);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(28, 4), (uint)snapshot.Options.MaxIterations);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(32, 4), (uint)snapshot.Keys.Length);
        hdr[36] = snapshot.Options.Seed.HasValue ? (byte)1 : (byte)0;
        hdr[37] = hdr[38] = hdr[39] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(40, 4), snapshot.Options.Seed ?? 0);
        // 44..47 reserved (zero)
        fs.Write(hdr);
    }

    private static void WriteIntArray(FileStream fs, IReadOnlyList<int> values)
    {
        byte[] buf = new byte[values.Count * sizeof(int)];
        Span<byte> bs = buf;
        for (int i = 0; i < values.Count; i++)
            BinaryPrimitives.WriteInt32LittleEndian(bs.Slice(i * sizeof(int), sizeof(int)), values[i]);
        fs.Write(buf);
    }

    private static void WriteFloatArray(FileStream fs, float[] values)
    {
        byte[] buf = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, buf, 0, buf.Length);
        fs.Write(buf);
    }

    private static int[] ReadIntArray(FileStream fs, int count)
    {
        if (count == 0) return [];
        byte[] buf = new byte[count * sizeof(int)];
        if (fs.Read(buf, 0, buf.Length) != buf.Length)
            throw new SonnetDbVectorException("ivfflat.bin: int 数组读取不完整。");
        int[] result = new int[count];
        Span<byte> bs = buf;
        for (int i = 0; i < count; i++)
            result[i] = BinaryPrimitives.ReadInt32LittleEndian(bs.Slice(i * sizeof(int), sizeof(int)));
        return result;
    }

    private static float[] ReadFloatArray(FileStream fs, int count)
    {
        if (count == 0) return [];
        byte[] buf = new byte[count * sizeof(float)];
        if (fs.Read(buf, 0, buf.Length) != buf.Length)
            throw new SonnetDbVectorException("ivfflat.bin: float 数组读取不完整。");
        float[] result = new float[count];
        Buffer.BlockCopy(buf, 0, result, 0, buf.Length);
        return result;
    }
}
