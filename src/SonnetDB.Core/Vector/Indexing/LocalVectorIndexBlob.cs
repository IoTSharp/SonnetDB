using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SonnetDB.Vector.Index.Hnsw;
using SonnetDB.Vector.IO;
using SonnetDB.Vector.Model;
using SonnetDB.Vector.Primitives;

namespace SonnetDB.Vector.Indexing;

/// <summary>
/// SonnetDB 本地向量索引 blob 编解码器。
/// </summary>
public static class LocalVectorIndexBlob
{
    private static readonly byte[] Magic = "DVIXB001"u8.ToArray();
    private const int FormatVersion = 1;
    private const int HeaderSize = 48;

    /// <summary>
    /// 写入 HNSW 本地索引 blob。
    /// </summary>
    public static uint Write(Stream stream, IVectorIndexReader reader)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(reader);
        if (reader is not LocalVectorIndexReader { Index: HnswIndex<int> hnswIndex } localReader)
            throw new NotSupportedException("Only SonnetDB local HNSW vector index readers can be serialized as index blobs.");

        var snapshot = hnswIndex.CreateSnapshot();
        var payload = BuildHnswPayload(snapshot);
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..12], FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..16], HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..20], (int)localReader.Algorithm);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..24], (int)localReader.Metric);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..28], localReader.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..32], localReader.Dimension);
        BinaryPrimitives.WriteInt32LittleEndian(header[32..36], snapshot.Options.M);
        BinaryPrimitives.WriteInt32LittleEndian(header[36..40], snapshot.Options.EfConstruction);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..44], snapshot.Options.EfSearch);
        BinaryPrimitives.WriteInt32LittleEndian(header[44..48], snapshot.Options.Seed ?? 0);

        byte[] checksumBuffer = new byte[checked(header.Length + payload.Length)];
        header.CopyTo(checksumBuffer);
        payload.CopyTo(checksumBuffer.AsSpan(header.Length));
        uint checksum = Crc32.Compute(checksumBuffer);

        stream.Write(header);
        stream.Write(payload);
        return checksum;
    }

    /// <summary>
    /// 从 blob 读取 SonnetDB 本地向量索引 reader。
    /// </summary>
    public static IVectorIndexReader Read(Stream stream, int length, uint expectedCrc32)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(length, HeaderSize);

        byte[] blob = new byte[length];
        FillBuffer(stream, blob);
        uint actualCrc32 = Crc32.Compute(blob);
        if (actualCrc32 != expectedCrc32)
            throw new InvalidDataException($"SonnetDB vector index blob CRC32 mismatch: expected 0x{expectedCrc32:X8}, got 0x{actualCrc32:X8}.");

        ReadOnlySpan<byte> header = blob.AsSpan(0, HeaderSize);
        if (!header[..8].SequenceEqual(Magic))
            throw new InvalidDataException("SonnetDB vector index blob magic mismatch.");

        int version = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported SonnetDB vector index blob version: {version}.");

        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(header[12..16]);
        if (headerSize != HeaderSize)
            throw new InvalidDataException($"Invalid SonnetDB vector index blob header size: {headerSize}.");

        var algorithm = (VectorIndexAlgorithm)BinaryPrimitives.ReadInt32LittleEndian(header[16..20]);
        var metric = (KnnMetric)BinaryPrimitives.ReadInt32LittleEndian(header[20..24]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header[24..28]);
        int dimension = BinaryPrimitives.ReadInt32LittleEndian(header[28..32]);
        int m = BinaryPrimitives.ReadInt32LittleEndian(header[32..36]);
        int efConstruction = BinaryPrimitives.ReadInt32LittleEndian(header[36..40]);
        int efSearch = BinaryPrimitives.ReadInt32LittleEndian(header[40..44]);
        int seed = BinaryPrimitives.ReadInt32LittleEndian(header[44..48]);

        if (algorithm != VectorIndexAlgorithm.Hnsw)
            throw new InvalidDataException($"Unsupported SonnetDB vector index blob algorithm: {algorithm}.");

        var snapshot = ReadHnswPayload(
            blob.AsSpan(HeaderSize),
            count,
            dimension,
            ToMetric(metric),
            new HnswOptions { M = m, EfConstruction = efConstruction, EfSearch = efSearch, Seed = seed == 0 ? null : seed });
        return new LocalVectorIndexReader(HnswIndex<int>.FromSnapshot(snapshot), algorithm, metric);
    }

    private static byte[] BuildHnswPayload(HnswIndexSnapshot<int> snapshot)
    {
        using var ms = new MemoryStream();
        WriteInt32(ms, snapshot.EntryPoint);
        WriteInt32(ms, snapshot.EntryLevel);
        WriteInt32(ms, snapshot.Tombstones.Length);
        foreach (int row in snapshot.Tombstones)
            WriteInt32(ms, row);
        foreach (int key in snapshot.Keys)
            WriteInt32(ms, key);
        foreach (int level in snapshot.Levels)
            WriteInt32(ms, level);
        ms.Write(MemoryMarshal.AsBytes<float>(snapshot.Vectors));
        WriteInt32(ms, snapshot.Neighbors.Length);
        foreach (var rowLayers in snapshot.Neighbors)
        {
            WriteInt32(ms, rowLayers.Length);
            foreach (var neighbors in rowLayers)
            {
                WriteInt32(ms, neighbors.Length);
                foreach (int neighbor in neighbors)
                    WriteInt32(ms, neighbor);
            }
        }
        return ms.ToArray();
    }

    private static HnswIndexSnapshot<int> ReadHnswPayload(
        ReadOnlySpan<byte> payload,
        int count,
        int dimension,
        Metric metric,
        HnswOptions options)
    {
        int offset = 0;
        int entryPoint = ReadInt32(payload, ref offset);
        int entryLevel = ReadInt32(payload, ref offset);
        int tombstoneCount = ReadInt32(payload, ref offset);
        var tombstones = new int[tombstoneCount];
        for (int i = 0; i < tombstones.Length; i++)
            tombstones[i] = ReadInt32(payload, ref offset);
        var keys = new int[count];
        for (int i = 0; i < keys.Length; i++)
            keys[i] = ReadInt32(payload, ref offset);
        var levels = new int[count];
        for (int i = 0; i < levels.Length; i++)
            levels[i] = ReadInt32(payload, ref offset);
        int vectorLength = checked(count * dimension);
        int vectorBytes = checked(vectorLength * sizeof(float));
        if (offset + vectorBytes > payload.Length)
            throw new InvalidDataException("SonnetDB HNSW vector blob vector payload is truncated.");
        var vectors = MemoryMarshal.Cast<byte, float>(payload.Slice(offset, vectorBytes)).ToArray();
        offset += vectorBytes;
        int rowCount = ReadInt32(payload, ref offset);
        if (rowCount != count)
            throw new InvalidDataException("SonnetDB HNSW vector blob neighbor row count does not match metadata.");
        var neighbors = new int[rowCount][][];
        for (int row = 0; row < rowCount; row++)
        {
            int layerCount = ReadInt32(payload, ref offset);
            neighbors[row] = new int[layerCount][];
            for (int layer = 0; layer < layerCount; layer++)
            {
                int neighborCount = ReadInt32(payload, ref offset);
                neighbors[row][layer] = new int[neighborCount];
                for (int i = 0; i < neighborCount; i++)
                    neighbors[row][layer][i] = ReadInt32(payload, ref offset);
            }
        }

        if (offset != payload.Length)
            throw new InvalidDataException("SonnetDB HNSW vector blob contains trailing bytes.");

        return new HnswIndexSnapshot<int>(
            dimension,
            metric,
            options,
            vectors,
            keys,
            levels,
            neighbors,
            tombstones,
            entryPoint,
            entryLevel);
    }

    private static Metric ToMetric(KnnMetric metric)
        => metric switch
        {
            KnnMetric.L2 => Metric.L2,
            KnnMetric.Cosine => Metric.Cosine,
            KnnMetric.InnerProduct => Metric.InnerProduct,
            _ => throw new InvalidDataException($"Unsupported SonnetDB vector index blob metric: {metric}."),
        };

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static int ReadInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        if (offset + sizeof(int) > source.Length)
            throw new InvalidDataException("SonnetDB HNSW vector blob is truncated.");
        int value = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        return value;
    }

    private static void FillBuffer(Stream stream, Span<byte> buffer)
    {
        int readTotal = 0;
        while (readTotal < buffer.Length)
        {
            int read = stream.Read(buffer[readTotal..]);
            if (read == 0)
                throw new InvalidDataException("SonnetDB vector index blob is truncated.");
            readTotal += read;
        }
    }
}
