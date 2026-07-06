using System.Buffers.Binary;

namespace SonnetDB.Storage.Segments;

/// <summary>
/// `.SDBVIDX` 向量索引 section 的读写工具。
/// <para>
/// v6 新段把该 section 内嵌在 `.SDBSEG` 的扩展区；V2 section 保存 SonnetDB 向量索引 blob manifest，
/// 并在 record 后持久化 SonnetDB 本地向量索引 blob。blob 缺失或损坏时，可按 manifest 从 SonnetDB block payload 重建。
/// </para>
/// </summary>
internal static class SegmentVectorIndexFile
{
    private static readonly byte[] Magic = "SDBVIDX2"u8.ToArray();
    internal static ReadOnlySpan<byte> SectionMagic => Magic;
    private const int FormatVersion = 4;
    private const int HeaderSize = 32;
    private const int RecordSize = 68;
    // #223 之前的 record 布局（无 Metric / EfConstruction 两个尾部 int32）。
    private const int RecordSizeV3 = 60;

    private static int RecordSizeForVersion(int version) => version >= 4 ? RecordSize : RecordSizeV3;

    /// <summary>
    /// 把多个 block 的 SonnetDB HNSW 向量索引元数据写入 sidecar 文件。
    /// </summary>
    /// <param name="path">目标 legacy sidecar 文件路径。</param>
    /// <param name="blocks">待写入的 block 索引与 blob 集合。</param>
    public static void Write(string path, IReadOnlyList<VectorIndexBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(blocks);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        WriteTo(fs, blocks);
        fs.Flush(true);
    }

    internal static void WriteTo(Stream stream, IReadOnlyList<VectorIndexBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(blocks);

        WriteHeader(stream, blocks.Count);
        foreach (var block in blocks)
        {
            var metadata = block.Metadata with { BlobOffset = stream.Position + RecordSize };
            WriteRecord(stream, metadata);
            if (metadata.HasPersistentBlob)
            {
                if (block.Blob.Length != metadata.BlobLength)
                    throw new InvalidDataException("SDBVIDX SonnetDB vector index blob length does not match manifest.");
                stream.Write(block.Blob);
            }
        }
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
            int blockCount = ReadHeader(fs, out int version);
            var result = new Dictionary<int, long>(blockCount);
            for (int i = 0; i < blockCount; i++)
            {
                long offset = fs.Position;
                var metadata = ReadRecord(fs, version);
                ValidateBlockIndex(metadata.BlockIndex, descriptors);
                result[metadata.BlockIndex] = offset;
                SkipBlob(fs, metadata, fs.Length);
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
    /// 尝试从指定 sidecar 偏移读取单个 block 的 SonnetDB 向量索引元数据。
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
            _ = ReadHeader(fs, out int version);
            if (offset >= fs.Length)
                return false;

            fs.Seek(offset, SeekOrigin.Begin);
            var loaded = ReadRecord(fs, version);
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

            // 读取内嵌 section 头以确定 record 布局版本（v3 60B / v4 68B）。
            fs.Seek(extensionOffset, SeekOrigin.Begin);
            if (!PeekMagicEquals(fs, Magic))
                return false;
            _ = ReadHeader(fs, out int version);

            fs.Seek(offset, SeekOrigin.Begin);
            var loaded = ReadRecord(fs, version);
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

    internal static IReadOnlyDictionary<int, VectorIndexBlockMetadata> TryLoadEmbeddedManifest(
        string segmentPath,
        long extensionOffset,
        long extensionLength,
        IReadOnlyList<BlockDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(segmentPath);
        ArgumentNullException.ThrowIfNull(descriptors);

        if (extensionLength <= 0)
            return new Dictionary<int, VectorIndexBlockMetadata>();

        long sectionEnd = extensionOffset + extensionLength;
        try
        {
            using var fs = new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (extensionOffset < 0 || sectionEnd > fs.Length)
                return new Dictionary<int, VectorIndexBlockMetadata>();

            fs.Seek(extensionOffset, SeekOrigin.Begin);
            if (!PeekMagicEquals(fs, Magic))
                return new Dictionary<int, VectorIndexBlockMetadata>();

            int recordCount = ReadHeader(fs, out int version);
            var result = new Dictionary<int, VectorIndexBlockMetadata>(recordCount);
            for (int i = 0; i < recordCount; i++)
            {
                var metadata = ReadRecord(fs, version);
                ValidateBlockIndex(metadata.BlockIndex, descriptors);
                result[metadata.BlockIndex] = metadata;
                SkipBlob(fs, metadata, sectionEnd);
            }

            return result;
        }
        catch (Exception ex) when (IsRecoverableVectorIndexReadError(ex))
        {
            return new Dictionary<int, VectorIndexBlockMetadata>();
        }
    }

    internal static bool TryOpenEmbeddedBlob(
        string segmentPath,
        long extensionOffset,
        long extensionLength,
        VectorIndexBlockMetadata metadata,
        out Stream stream)
    {
        ArgumentNullException.ThrowIfNull(segmentPath);

        stream = null!;
        if (!metadata.HasPersistentBlob)
            return false;

        long extensionEnd = extensionOffset + extensionLength;
        if (metadata.BlobOffset < extensionOffset || metadata.BlobOffset + metadata.BlobLength > extensionEnd)
            return false;

        try
        {
            var fs = new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (extensionOffset < 0 || extensionEnd > fs.Length)
            {
                fs.Dispose();
                return false;
            }

            fs.Seek(metadata.BlobOffset, SeekOrigin.Begin);
            stream = fs;
            return true;
        }
        catch (Exception ex) when (IsRecoverableVectorIndexReadError(ex))
        {
            stream = null!;
            return false;
        }
    }

    internal static bool TryOpenBlob(string segmentPath, VectorIndexBlockMetadata metadata, out Stream stream)
    {
        ArgumentNullException.ThrowIfNull(segmentPath);

        stream = null!;
        if (!metadata.HasPersistentBlob)
            return false;

        string path = Engine.TsdbPaths.VectorIndexPathForSegment(segmentPath);
        if (!File.Exists(path))
            return false;

        try
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (metadata.BlobOffset < 0 || metadata.BlobOffset + metadata.BlobLength > fs.Length)
            {
                fs.Dispose();
                return false;
            }

            fs.Seek(metadata.BlobOffset, SeekOrigin.Begin);
            stream = fs;
            return true;
        }
        catch (Exception ex) when (IsRecoverableVectorIndexReadError(ex))
        {
            stream = null!;
            return false;
        }
    }

    internal static bool TrySkipEmbeddedSection(Stream stream, long sectionEnd)
    {
        try
        {
            int blockCount = ReadHeader(stream, out int version);
            for (int i = 0; i < blockCount; i++)
            {
                var metadata = ReadRecord(stream, version);
                SkipBlob(stream, metadata, sectionEnd);
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

    private static int ReadHeader(Stream stream) => ReadHeader(stream, out _);

    private static int ReadHeader(Stream stream, out int version)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        FillBuffer(stream, header);
        if (!header[..8].SequenceEqual(Magic))
            throw new InvalidDataException("SDBVIDX magic 不匹配。");

        version = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
        // #223：v3（旧）与 v4（含 Metric/EfConstruction）均可读；record 大小按版本区分。
        if (version is < 3 or > FormatVersion)
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
        int recordCount = ReadHeader(stream, out int version);
        var result = new Dictionary<int, long>(recordCount);
        for (int i = 0; i < recordCount; i++)
        {
            long offset = stream.Position;
            var metadata = ReadRecord(stream, version);
            ValidateBlockIndex(metadata.BlockIndex, descriptors);
            result[metadata.BlockIndex] = offset;
            SkipBlob(stream, metadata, sectionEnd);
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
        BinaryPrimitives.WriteInt32LittleEndian(record[12..16], metadata.IndexKind);
        BinaryPrimitives.WriteInt32LittleEndian(record[16..20], metadata.M);
        BinaryPrimitives.WriteInt32LittleEndian(record[20..24], metadata.Ef);
        BinaryPrimitives.WriteInt32LittleEndian(record[24..28], metadata.Extra1);
        BinaryPrimitives.WriteInt32LittleEndian(record[28..32], metadata.Extra2);
        BinaryPrimitives.WriteInt32LittleEndian(record[32..36], metadata.Extra3);
        BinaryPrimitives.WriteUInt32LittleEndian(record[36..40], metadata.BlockCrc32);
        BinaryPrimitives.WriteInt64LittleEndian(record[40..48], metadata.BlobOffset);
        BinaryPrimitives.WriteInt32LittleEndian(record[48..52], metadata.BlobLength);
        BinaryPrimitives.WriteUInt32LittleEndian(record[52..56], metadata.BlobCrc32);
        BinaryPrimitives.WriteInt32LittleEndian(record[56..60], (int)metadata.Flags);
        // #223（v4）：尾部追加 Metric + EfConstruction。
        BinaryPrimitives.WriteInt32LittleEndian(record[60..64], metadata.Metric);
        BinaryPrimitives.WriteInt32LittleEndian(record[64..68], metadata.EfConstruction);
        stream.Write(record);
    }

    private static VectorIndexBlockMetadata ReadRecord(Stream stream) => ReadRecord(stream, FormatVersion);

    private static VectorIndexBlockMetadata ReadRecord(Stream stream, int version)
    {
        int recordSize = RecordSizeForVersion(version);
        Span<byte> record = stackalloc byte[RecordSize];
        FillBuffer(stream, record[..recordSize]);

        // v3 段无 Metric / EfConstruction：Metric 默认 Cosine(0)，EfConstruction 由下方按 max(Ef,200) 回填。
        int metric = version >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(record[60..64]) : 0;
        int efConstruction = version >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(record[64..68]) : 0;

        int ef = BinaryPrimitives.ReadInt32LittleEndian(record[20..24]);
        var metadata = new VectorIndexBlockMetadata(
            BinaryPrimitives.ReadInt32LittleEndian(record[0..4]),
            BinaryPrimitives.ReadInt32LittleEndian(record[4..8]),
            BinaryPrimitives.ReadInt32LittleEndian(record[8..12]),
            BinaryPrimitives.ReadInt32LittleEndian(record[12..16]),
            BinaryPrimitives.ReadInt32LittleEndian(record[16..20]),
            ef,
            BinaryPrimitives.ReadInt32LittleEndian(record[24..28]),
            BinaryPrimitives.ReadInt32LittleEndian(record[28..32]),
            BinaryPrimitives.ReadInt32LittleEndian(record[32..36]),
            BinaryPrimitives.ReadUInt32LittleEndian(record[36..40]),
            BinaryPrimitives.ReadInt64LittleEndian(record[40..48]),
            BinaryPrimitives.ReadInt32LittleEndian(record[48..52]),
            BinaryPrimitives.ReadUInt32LittleEndian(record[52..56]),
            (VectorIndexManifestFlags)BinaryPrimitives.ReadInt32LittleEndian(record[56..60]),
            metric,
            version >= 4 ? efConstruction : Math.Max(ef, 200));

        if (metadata.Count <= 0 || metadata.Dimension <= 0 || metadata.IndexKind <= 0 || metadata.M <= 0 || metadata.Ef <= 0)
            throw new InvalidDataException("SDBVIDX 含有非法的 block 索引参数。");
        if (metadata.Metric is < 0 or > 2)
            throw new InvalidDataException("SDBVIDX 含有非法的向量度量。");
        if (metadata.HasPersistentBlob && (metadata.BlobOffset < HeaderSize || metadata.BlobLength <= 0 || metadata.BlobCrc32 == 0))
            throw new InvalidDataException("SDBVIDX 含有非法的 SonnetDB vector index blob manifest。");

        return metadata;
    }

    private static void SkipBlob(Stream stream, VectorIndexBlockMetadata metadata, long sectionEnd)
    {
        if (!metadata.HasPersistentBlob)
            return;

        if (metadata.BlobOffset < stream.Position || metadata.BlobOffset + metadata.BlobLength > sectionEnd)
            throw new InvalidDataException("SDBVIDX SonnetDB vector index blob 越界。");

        stream.Seek(metadata.BlobOffset + metadata.BlobLength, SeekOrigin.Begin);
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

    private static bool IsRecoverableVectorIndexReadError(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException;
}
