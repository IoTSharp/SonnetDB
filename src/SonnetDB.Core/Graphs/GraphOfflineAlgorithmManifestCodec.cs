using System.Buffers.Binary;
using System.IO.Hashing;

namespace SonnetDB.Graphs;

internal static class GraphOfflineAlgorithmManifestCodec
{
    internal const string FileName = "manifest.sdbgraph";

    private const int FormatVersion = 1;
    private const int HeaderSize = 192;
    private const int FooterSize = 8;
    private const int MaximumManifestBytes = HeaderSize + Graphs.Storage.GraphKeyCodec.MaxEncodedKeyBytes + 12;
    private static readonly byte[] Magic = "SDBGALG1"u8.ToArray();

    internal static GraphOfflineAlgorithmState? Load(string path, Guid expectedStorageId)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            return null;

        byte[] encoded;
        using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (source.Length < HeaderSize + sizeof(uint) + FooterSize
                || source.Length > MaximumManifestBytes)
            {
                throw new InvalidDataException("Graph offline algorithm manifest 长度无效。");
            }
            encoded = new byte[checked((int)source.Length)];
            source.ReadExactly(encoded);
        }

        ReadOnlySpan<byte> span = encoded;
        if (!span[..Magic.Length].SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(span[8..]) != FormatVersion
            || BinaryPrimitives.ReadInt32LittleEndian(span[12..]) != HeaderSize
            || !span[^FooterSize..].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Graph offline algorithm manifest header 无效。");
        }

        int payloadLength = span.Length - sizeof(uint) - FooterSize;
        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(span[payloadLength..]);
        uint actualCrc = Crc32.HashToUInt32(span[..payloadLength]);
        if (expectedCrc != actualCrc)
            throw new InvalidDataException("Graph offline algorithm manifest CRC32 不匹配。");

        Guid storageId = new(span[16..32]);
        Guid operationId = new(span[32..48]);
        var phase = (GraphOfflineAlgorithmPhase)BinaryPrimitives.ReadInt32LittleEndian(span[48..]);
        int flags = BinaryPrimitives.ReadInt32LittleEndian(span[52..]);
        int pageRankGeneration = BinaryPrimitives.ReadInt32LittleEndian(span[104..]);
        int communityGeneration = BinaryPrimitives.ReadInt32LittleEndian(span[112..]);
        int afterKeyLength = BinaryPrimitives.ReadInt32LittleEndian(span[140..]);
        if (storageId != expectedStorageId
            || operationId == Guid.Empty
            || !Enum.IsDefined(phase)
            || (flags & ~15) != 0
            || pageRankGeneration is < 0 or > 1
            || communityGeneration is < 0 or > 1
            || afterKeyLength < 0
            || afterKeyLength > Graphs.Storage.GraphKeyCodec.MaxEncodedKeyBytes
            || encoded.Length != HeaderSize + afterKeyLength + sizeof(uint) + FooterSize
            || span[184..HeaderSize].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new InvalidDataException("Graph offline algorithm manifest 字段无效。");
        }

        long sourceSequence = ReadNonNegativeInt64(span[56..], "source sequence");
        long vertexCount = ReadNonNegativeInt64(span[64..], "vertex count");
        long edgeCount = ReadNonNegativeInt64(span[72..], "edge count");
        long vertexRecordsLength = ReadNonNegativeInt64(span[80..], "vertex records length");
        long workUnits = ReadNonNegativeInt64(span[88..], "work units");
        int pageRankIterations = ReadNonNegativeInt32(span[100..], "PageRank iterations");
        int communityIterations = ReadNonNegativeInt32(span[108..], "community iterations");
        long publishedVertices = ReadNonNegativeInt64(span[120..], "published vertices");
        long spillBytes = ReadNonNegativeInt64(span[128..], "spill bytes");
        long memoryBudgetBytes = ReadNonNegativeInt64(span[144..], "memory budget");
        if (publishedVertices > vertexCount
            || memoryBudgetBytes < 256 * 1024
            || (phase is not (GraphOfflineAlgorithmPhase.ScanVertices or GraphOfflineAlgorithmPhase.ScanEdges)
                && afterKeyLength != 0)
            || (phase == GraphOfflineAlgorithmPhase.ScanVertices && edgeCount != 0))
        {
            throw new InvalidDataException("Graph offline algorithm manifest continuation 无效。");
        }

        return new GraphOfflineAlgorithmState
        {
            StorageId = storageId,
            OperationId = operationId,
            ConfigurationHash = span[152..184].ToArray(),
            Phase = phase,
            SourceSequence = sourceSequence,
            MemoryBudgetBytes = memoryBudgetBytes,
            AfterKey = span.Slice(HeaderSize, afterKeyLength).ToArray(),
            VertexCount = vertexCount,
            EdgeCount = edgeCount,
            VertexRecordsLength = vertexRecordsLength,
            WorkUnits = workUnits,
            PageRankInitialized = (flags & 1) != 0,
            PageRankConverged = (flags & 2) != 0,
            CommunityInitialized = (flags & 4) != 0,
            CommunityConverged = (flags & 8) != 0,
            PageRankGeneration = pageRankGeneration,
            PageRankIterations = pageRankIterations,
            CommunityGeneration = communityGeneration,
            CommunityIterations = communityIterations,
            PublishedVertices = publishedVertices,
            SpillBytes = spillBytes,
        };
    }

    internal static void Save(string path, GraphOfflineAlgorithmState state)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(state);
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("Graph offline algorithm manifest 必须包含父目录。", nameof(path));
        if (state.ConfigurationHash.Length != 32)
            throw new ArgumentException("Graph offline algorithm configuration hash 长度无效。", nameof(state));

        int length = checked(HeaderSize + state.AfterKey.Length + sizeof(uint) + FooterSize);
        byte[] encoded = new byte[length];
        Span<byte> span = encoded;
        Magic.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], HeaderSize);
        state.StorageId.TryWriteBytes(span[16..32]);
        state.OperationId.TryWriteBytes(span[32..48]);
        BinaryPrimitives.WriteInt32LittleEndian(span[48..], (int)state.Phase);
        int flags = (state.PageRankInitialized ? 1 : 0)
            | (state.PageRankConverged ? 2 : 0)
            | (state.CommunityInitialized ? 4 : 0)
            | (state.CommunityConverged ? 8 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(span[52..], flags);
        BinaryPrimitives.WriteInt64LittleEndian(span[56..], state.SourceSequence);
        BinaryPrimitives.WriteInt64LittleEndian(span[64..], state.VertexCount);
        BinaryPrimitives.WriteInt64LittleEndian(span[72..], state.EdgeCount);
        BinaryPrimitives.WriteInt64LittleEndian(span[80..], state.VertexRecordsLength);
        BinaryPrimitives.WriteInt64LittleEndian(span[88..], state.WorkUnits);
        BinaryPrimitives.WriteInt32LittleEndian(span[100..], state.PageRankIterations);
        BinaryPrimitives.WriteInt32LittleEndian(span[104..], state.PageRankGeneration);
        BinaryPrimitives.WriteInt32LittleEndian(span[108..], state.CommunityIterations);
        BinaryPrimitives.WriteInt32LittleEndian(span[112..], state.CommunityGeneration);
        BinaryPrimitives.WriteInt64LittleEndian(span[120..], state.PublishedVertices);
        BinaryPrimitives.WriteInt64LittleEndian(span[128..], state.SpillBytes);
        BinaryPrimitives.WriteInt32LittleEndian(span[140..], state.AfterKey.Length);
        BinaryPrimitives.WriteInt64LittleEndian(span[144..], state.MemoryBudgetBytes);
        state.ConfigurationHash.CopyTo(span[152..184]);
        state.AfterKey.CopyTo(span[HeaderSize..]);

        int payloadLength = HeaderSize + state.AfterKey.Length;
        uint crc = Crc32.HashToUInt32(span[..payloadLength]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[payloadLength..], crc);
        Magic.CopyTo(span[(payloadLength + sizeof(uint))..]);

        Directory.CreateDirectory(directory);
        string temporaryPath = path + ".tmp";
        try
        {
            using (var destination = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.WriteThrough))
            {
                destination.Write(encoded);
                destination.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
            SonnetDB.Wal.DirectoryFsync.FlushRequired(directory);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static int ReadNonNegativeInt32(ReadOnlySpan<byte> source, string name)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(source);
        return value >= 0
            ? value
            : throw new InvalidDataException($"Graph offline algorithm manifest {name} 无效。");
    }

    private static long ReadNonNegativeInt64(ReadOnlySpan<byte> source, string name)
    {
        long value = BinaryPrimitives.ReadInt64LittleEndian(source);
        return value >= 0
            ? value
            : throw new InvalidDataException($"Graph offline algorithm manifest {name} 无效。");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Preserve the durable-save failure.
        }
    }
}
