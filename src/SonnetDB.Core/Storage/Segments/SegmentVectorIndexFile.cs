using System.Buffers.Binary;

namespace SonnetDB.Storage.Segments;

/// <summary>
/// `.SDBVIDX` 向量索引 section 的读写工具。
/// <para>
/// v6 新段把该 section 内嵌在 `.SDBSEG` 的扩展区；V2 section 只保存 DotVector 索引重建元数据，
/// 实际 HNSW 图由 DotVector 在首次查询时从 SonnetDB block payload 重建并缓存。
/// </para>
/// </summary>
internal static class SegmentVectorIndexFile
{
    private static readonly byte[] Magic = "SDBVIDX2"u8.ToArray();
    internal static ReadOnlySpan<byte> SectionMagic => Magic;
    private const int FormatVersion = 2;
    private const int HeaderSize = 32;
    private const int RecordSize = 32;

    /// <summary>
    /// 把多个 block 的 DotVector HNSW 索引元数据写入 sidecar 文件。
    /// </summary>
    /// <param name="path">目标 legacy sidecar 文件路径。</param>
    /// <param name="blocks">待写入的 block 索引元数据集合。</param>
    public static void Write(string path, IReadOnlyList<VectorIndexBlockMetadata> blocks)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(blocks);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        WriteTo(fs, blocks);
        fs.Flush(true);
    }

    internal static void WriteTo(Stream stream, IReadOnlyList<VectorIndexBlockMetadata> blocks)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(blocks);

        WriteHeader(stream, blocks.Count);
        foreach (var block in blocks)
            WriteRecord(stream, block);
    }

    /// <summary>
    /// 尝试读取 legacy sidecar 中各 block 索引的文件偏移；不反序列化 HNSW 图。
    /// </summary>
    /// <param name="segmentPath">段文件路径。</param>
    /// <param name="descriptors">段内 block 描述符。</param>
    /// <returns>按 block index 建立的 legacy sidecar 偏移表。</returns>
    public static IReadOnlyDictionary<int, long> TryLoadOffsets(
        string segmentPath,
        IReadOnlyList<BlockDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(segmentPath);
        ArgumentNullException.ThrowIfNull(descriptors);

        string path = Engine.TsdbPaths.VectorIndexPathForSegment(segmentPath);
        if (!File.Exists(path))
            return new Dictionary<int, long>();

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            int blockCount = ReadHeader(fs);
            var result = new Dictionary<int, long>(blockCount);
            for (int i = 0; i < blockCount; i++)
            {
                long offset = fs.Position;
                var metadata = ReadRecord(fs);
                ValidateBlockIndex(metadata.BlockIndex, descriptors);
                result[metadata.BlockIndex] = offset;
            }

            return result;
        }
        catch
        {
            return new Dictionary<int, long>();
        }
    }

    internal static IReadOnlyDictionary<int, long> TryLoadEmbeddedOffsets(
        string segmentPath,
        long extensionOffset,
        long extensionLength,
        IReadOnlyList<BlockDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(segmentPath);
        ArgumentNullException.ThrowIfNull(descriptors);

        if (extensionLength <= 0)
            return new Dictionary<int, long>();

        long sectionEnd = extensionOffset + extensionLength;
        try
        {
            using var fs = new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (extensionOffset < 0 || sectionEnd > fs.Length)
                return new Dictionary<int, long>();

            fs.Seek(extensionOffset, SeekOrigin.Begin);
            return PeekMagicEquals(fs, Magic)
                ? LoadOffsetsFromCurrentSection(fs, descriptors, sectionEnd)
                : new Dictionary<int, long>();
        }
        catch
        {
            return new Dictionary<int, long>();
        }
    }

    /// <summary>
    /// 尝试从指定 sidecar 偏移读取单个 block 的 DotVector 索引元数据。
    /// </summary>
    /// <param name="segmentPath">段文件路径。</param>
    /// <param name="offset">legacy sidecar 内索引起始偏移。</param>
    /// <param name="descriptors">段内 block 描述符。</param>
    /// <param name="targetBlockIndex">目标 block index。</param>
    /// <param name="metadata">读取成功时返回的索引元数据。</param>
    /// <returns>读取成功返回 true，否则返回 false。</returns>
    public static bool TryLoadBlockAt(
        string segmentPath,
        long offset,
        IReadOnlyList<BlockDescriptor> descriptors,
        int targetBlockIndex,
        out VectorIndexBlockMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(segmentPath);
        ArgumentNullException.ThrowIfNull(descriptors);

        metadata = default;
        if (targetBlockIndex < 0 || targetBlockIndex >= descriptors.Count)
            return false;
        if (descriptors[targetBlockIndex].FieldType != Storage.Format.FieldType.Vector)
            return false;
        if (offset < HeaderSize)
            return false;

        string path = Engine.TsdbPaths.VectorIndexPathForSegment(segmentPath);
        if (!File.Exists(path))
            return false;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _ = ReadHeader(fs);
            if (offset >= fs.Length)
                return false;

            fs.Seek(offset, SeekOrigin.Begin);
            var loaded = ReadRecord(fs);
            if (loaded.BlockIndex != targetBlockIndex)
                return false;
            ValidateBlockIndex(loaded.BlockIndex, descriptors);

            metadata = loaded;
            return true;
        }
        catch
        {
            metadata = default;
            return false;
        }
    }

    internal static bool TryLoadEmbeddedBlockAt(
        string segmentPath,
        long offset,
        long extensionOffset,
        long extensionLength,
        IReadOnlyList<BlockDescriptor> descriptors,
        int targetBlockIndex,
        out VectorIndexBlockMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(segmentPath);
        ArgumentNullException.ThrowIfNull(descriptors);

        metadata = default;
        long extensionEnd = extensionOffset + extensionLength;
        if (targetBlockIndex < 0 || targetBlockIndex >= descriptors.Count)
            return false;
        if (descriptors[targetBlockIndex].FieldType != Storage.Format.FieldType.Vector)
            return false;
        if (offset < extensionOffset || offset >= extensionEnd)
            return false;

        try
        {
            using var fs = new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (extensionOffset < 0 || extensionEnd > fs.Length)
                return false;

            fs.Seek(offset, SeekOrigin.Begin);
            var loaded = ReadRecord(fs);
            if (fs.Position > extensionEnd || loaded.BlockIndex != targetBlockIndex)
                return false;
            ValidateBlockIndex(loaded.BlockIndex, descriptors);

            metadata = loaded;
            return true;
        }
        catch
        {
            metadata = default;
            return false;
        }
    }

    internal static bool TrySkipEmbeddedSection(Stream stream, long sectionEnd)
    {
        try
        {
            int blockCount = ReadHeader(stream);
            for (int i = 0; i < blockCount; i++)
            {
                _ = ReadRecord(stream);
                if (stream.Position > sectionEnd)
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteHeader(Stream stream, int blockCount)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..12], FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..16], HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..20], blockCount);
        stream.Write(header);
    }

    private static int ReadHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        FillBuffer(stream, header);
        if (!header[..8].SequenceEqual(Magic))
            throw new InvalidDataException("SDBVIDX magic 不匹配。");

        int version = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
        if (version != FormatVersion)
            throw new InvalidDataException($"SDBVIDX 版本不支持：{version}。");

        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(header[12..16]);
        if (headerSize != HeaderSize)
            throw new InvalidDataException($"SDBVIDX HeaderSize={headerSize} 非法。");

        int blockCount = BinaryPrimitives.ReadInt32LittleEndian(header[16..20]);
        if (blockCount < 0)
            throw new InvalidDataException("SDBVIDX blockCount 不能为负。");

        return blockCount;
    }

    private static IReadOnlyDictionary<int, long> LoadOffsetsFromCurrentSection(
        Stream stream,
        IReadOnlyList<BlockDescriptor> descriptors,
        long sectionEnd)
    {
        int recordCount = ReadHeader(stream);
        var result = new Dictionary<int, long>(recordCount);
        for (int i = 0; i < recordCount; i++)
        {
            long offset = stream.Position;
            var metadata = ReadRecord(stream);
            ValidateBlockIndex(metadata.BlockIndex, descriptors);
            result[metadata.BlockIndex] = offset;
            if (stream.Position > sectionEnd)
                throw new InvalidDataException("embedded SDBVIDX section exceeds extension range.");
        }

        return result;
    }

    private static void WriteRecord(Stream stream, VectorIndexBlockMetadata metadata)
    {
        Span<byte> record = stackalloc byte[RecordSize];
        record.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(record[0..4], metadata.BlockIndex);
        BinaryPrimitives.WriteInt32LittleEndian(record[4..8], metadata.Count);
        BinaryPrimitives.WriteInt32LittleEndian(record[8..12], metadata.Dimension);
        BinaryPrimitives.WriteInt32LittleEndian(record[12..16], metadata.M);
        BinaryPrimitives.WriteInt32LittleEndian(record[16..20], metadata.Ef);
        BinaryPrimitives.WriteUInt32LittleEndian(record[20..24], metadata.BlockCrc32);
        stream.Write(record);
    }

    private static VectorIndexBlockMetadata ReadRecord(Stream stream)
    {
        Span<byte> record = stackalloc byte[RecordSize];
        FillBuffer(stream, record);
        var metadata = new VectorIndexBlockMetadata(
            BinaryPrimitives.ReadInt32LittleEndian(record[0..4]),
            BinaryPrimitives.ReadInt32LittleEndian(record[4..8]),
            BinaryPrimitives.ReadInt32LittleEndian(record[8..12]),
            BinaryPrimitives.ReadInt32LittleEndian(record[12..16]),
            BinaryPrimitives.ReadInt32LittleEndian(record[16..20]),
            BinaryPrimitives.ReadUInt32LittleEndian(record[20..24]));

        if (metadata.Count <= 0 || metadata.Dimension <= 0 || metadata.M < 2 || metadata.Ef <= 0)
            throw new InvalidDataException("SDBVIDX 含有非法的 block 索引参数。");

        return metadata;
    }

    private static bool PeekMagicEquals(Stream stream, ReadOnlySpan<byte> expectedMagic)
    {
        Span<byte> magic = stackalloc byte[8];
        FillBuffer(stream, magic);
        stream.Seek(-magic.Length, SeekOrigin.Current);
        return magic.SequenceEqual(expectedMagic);
    }

    private static void ValidateBlockIndex(int blockIndex, IReadOnlyList<BlockDescriptor> descriptors)
    {
        if (blockIndex < 0 || blockIndex >= descriptors.Count)
            throw new InvalidDataException("SDBVIDX 中的 blockIndex 越界。");
        if (descriptors[blockIndex].FieldType != Storage.Format.FieldType.Vector)
            throw new InvalidDataException("SDBVIDX 指向了非 VECTOR block。");
    }

    private static void FillBuffer(Stream stream, Span<byte> buffer)
    {
        int readTotal = 0;
        while (readTotal < buffer.Length)
        {
            int read = stream.Read(buffer[readTotal..]);
            if (read == 0)
                throw new InvalidDataException("SDBVIDX 文件截断。");
            readTotal += read;
        }
    }
}
