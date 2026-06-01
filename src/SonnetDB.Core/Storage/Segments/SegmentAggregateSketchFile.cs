using System.Buffers.Binary;
using SonnetDB.Query.Functions.Aggregates;

namespace SonnetDB.Storage.Segments;

/// <summary>
/// `.SDBAIDX` 扩展聚合 sketch section 的读写工具。
/// <para>
/// v6 新段把该 section 内嵌在 `.SDBSEG` 的扩展区；独立 `.SDBAIDX` 文件仅用于读取旧段回退。
/// </para>
/// </summary>
internal static class SegmentAggregateSketchFile
{
    private static readonly byte[] Magic = "SDBAIDX1"u8.ToArray();
    private const int FormatVersion = 1;
    private const int HeaderSize = 32;
    private const int RecordHeaderSize = 32;
    private const int HasTDigest = 1;
    private const int HasHyperLogLog = 2;

    public static void Write(string path, IReadOnlyList<BlockAggregateSketch> sketches)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sketches);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        WriteTo(fs, sketches);
        fs.Flush(true);
    }

    internal static void WriteTo(Stream stream, IReadOnlyList<BlockAggregateSketch> sketches)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(sketches);

        WriteHeader(stream, sketches.Count);
        foreach (var sketch in sketches)
            WriteSketch(stream, sketch);
    }

    public static IReadOnlyDictionary<int, long> TryLoadOffsets(
        string segmentPath,
        IReadOnlyList<BlockDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(segmentPath);
        ArgumentNullException.ThrowIfNull(descriptors);

        string path = Engine.TsdbPaths.AggregateIndexPathForSegment(segmentPath);
        if (!File.Exists(path))
            return new Dictionary<int, long>();

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            int recordCount = ReadHeader(fs);
            var result = new Dictionary<int, long>(recordCount);
            for (int i = 0; i < recordCount; i++)
            {
                long offset = fs.Position;
                var header = ReadRecordHeader(fs);
                ValidateBlockIndex(header.BlockIndex, descriptors);
                result[header.BlockIndex] = offset;
                fs.Seek(header.TDigestLength + header.HyperLogLogLength, SeekOrigin.Current);
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

        long extensionEnd = extensionOffset + extensionLength;
        try
        {
            using var fs = new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (extensionOffset < 0 || extensionEnd > fs.Length)
                return new Dictionary<int, long>();

            fs.Seek(extensionOffset, SeekOrigin.Begin);
            while (fs.Position + 8 <= extensionEnd)
            {
                long sectionOffset = fs.Position;
                if (PeekMagicEquals(fs, Magic))
                    return LoadOffsetsFromCurrentSection(fs, descriptors, extensionEnd);

                if (PeekMagicEquals(fs, SegmentVectorIndexFile.SectionMagic))
                {
                    fs.Seek(sectionOffset, SeekOrigin.Begin);
                    if (!SegmentVectorIndexFile.TrySkipEmbeddedSection(fs, extensionEnd))
                        return new Dictionary<int, long>();
                    continue;
                }

                break;
            }

            return new Dictionary<int, long>();
        }
        catch
        {
            return new Dictionary<int, long>();
        }
    }

    public static bool TryLoadBlockAt(
        string segmentPath,
        long offset,
        IReadOnlyList<BlockDescriptor> descriptors,
        int targetBlockIndex,
        uint expectedBlockCrc32,
        out BlockAggregateSketch sketch)
    {
        ArgumentNullException.ThrowIfNull(segmentPath);
        ArgumentNullException.ThrowIfNull(descriptors);

        sketch = null!;
        if (targetBlockIndex < 0 || targetBlockIndex >= descriptors.Count || offset < HeaderSize)
            return false;

        string path = Engine.TsdbPaths.AggregateIndexPathForSegment(segmentPath);
        if (!File.Exists(path))
            return false;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _ = ReadHeader(fs);
            if (offset >= fs.Length)
                return false;

            fs.Seek(offset, SeekOrigin.Begin);
            var header = ReadRecordHeader(fs);
            if (header.BlockIndex != targetBlockIndex || header.BlockCrc32 != expectedBlockCrc32)
                return false;
            ValidateBlockIndex(header.BlockIndex, descriptors);

            byte[] tdigestBytes = ReadPayload(fs, header.TDigestLength);
            byte[] hllBytes = ReadPayload(fs, header.HyperLogLogLength);

            TDigest? digest = (header.Flags & HasTDigest) != 0 && tdigestBytes.Length > 0
                ? TDigest.Deserialize(tdigestBytes)
                : null;
            HyperLogLog? hll = (header.Flags & HasHyperLogLog) != 0 && hllBytes.Length > 0
                ? HyperLogLog.Deserialize(hllBytes)
                : null;

            if (digest is null && hll is null)
                return false;

            sketch = new BlockAggregateSketch(header.BlockIndex, header.BlockCrc32, header.ValueCount, digest, hll);
            return true;
        }
        catch
        {
            sketch = null!;
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
        uint expectedBlockCrc32,
        out BlockAggregateSketch sketch)
    {
        ArgumentNullException.ThrowIfNull(segmentPath);
        ArgumentNullException.ThrowIfNull(descriptors);

        sketch = null!;
        long extensionEnd = extensionOffset + extensionLength;
        if (targetBlockIndex < 0 || targetBlockIndex >= descriptors.Count)
            return false;
        if (offset < extensionOffset || offset >= extensionEnd)
            return false;

        try
        {
            using var fs = new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (extensionOffset < 0 || extensionEnd > fs.Length)
                return false;

            fs.Seek(offset, SeekOrigin.Begin);
            return TryReadSketchRecord(
                fs,
                descriptors,
                targetBlockIndex,
                expectedBlockCrc32,
                extensionEnd,
                out sketch);
        }
        catch
        {
            sketch = null!;
            return false;
        }
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
            var header = ReadRecordHeader(stream);
            ValidateBlockIndex(header.BlockIndex, descriptors);
            result[header.BlockIndex] = offset;
            stream.Seek(header.TDigestLength + header.HyperLogLogLength, SeekOrigin.Current);
            if (stream.Position > sectionEnd)
                throw new InvalidDataException("embedded SDBAIDX section exceeds extension range.");
        }

        return result;
    }

    private static bool TryReadSketchRecord(
        Stream stream,
        IReadOnlyList<BlockDescriptor> descriptors,
        int targetBlockIndex,
        uint expectedBlockCrc32,
        long sectionEnd,
        out BlockAggregateSketch sketch)
    {
        sketch = null!;
        var header = ReadRecordHeader(stream);
        if (header.BlockIndex != targetBlockIndex || header.BlockCrc32 != expectedBlockCrc32)
            return false;
        ValidateBlockIndex(header.BlockIndex, descriptors);

        byte[] tdigestBytes = ReadPayload(stream, header.TDigestLength);
        byte[] hllBytes = ReadPayload(stream, header.HyperLogLogLength);
        if (stream.Position > sectionEnd)
            return false;

        TDigest? digest = (header.Flags & HasTDigest) != 0 && tdigestBytes.Length > 0
            ? TDigest.Deserialize(tdigestBytes)
            : null;
        HyperLogLog? hll = (header.Flags & HasHyperLogLog) != 0 && hllBytes.Length > 0
            ? HyperLogLog.Deserialize(hllBytes)
            : null;

        if (digest is null && hll is null)
            return false;

        sketch = new BlockAggregateSketch(header.BlockIndex, header.BlockCrc32, header.ValueCount, digest, hll);
        return true;
    }

    private static bool PeekMagicEquals(Stream stream, ReadOnlySpan<byte> expectedMagic)
    {
        Span<byte> magic = stackalloc byte[8];
        FillBuffer(stream, magic);
        stream.Seek(-magic.Length, SeekOrigin.Current);
        return magic.SequenceEqual(expectedMagic);
    }

    private static void WriteHeader(Stream stream, int recordCount)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..12], FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..16], HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..20], recordCount);
        stream.Write(header);
    }

    private static int ReadHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        FillBuffer(stream, header);
        if (!header[..8].SequenceEqual(Magic))
            throw new InvalidDataException("SDBAIDX magic 不匹配。");

        int version = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
        if (version != FormatVersion)
            throw new InvalidDataException($"SDBAIDX 版本不支持：{version}。");

        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(header[12..16]);
        if (headerSize != HeaderSize)
            throw new InvalidDataException($"SDBAIDX HeaderSize={headerSize} 非法。");

        int recordCount = BinaryPrimitives.ReadInt32LittleEndian(header[16..20]);
        if (recordCount < 0)
            throw new InvalidDataException("SDBAIDX recordCount 不能为负。");

        return recordCount;
    }

    private static void WriteSketch(Stream stream, BlockAggregateSketch sketch)
    {
        byte[] tdigestBytes = sketch.TDigest?.Serialize() ?? [];
        byte[] hllBytes = sketch.HyperLogLog?.Serialize() ?? [];
        int flags = 0;
        if (tdigestBytes.Length > 0) flags |= HasTDigest;
        if (hllBytes.Length > 0) flags |= HasHyperLogLog;

        Span<byte> header = stackalloc byte[RecordHeaderSize];
        header.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(header[0..4], sketch.BlockIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], sketch.BlockCrc32);
        BinaryPrimitives.WriteInt64LittleEndian(header[8..16], sketch.ValueCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..20], tdigestBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..24], hllBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..28], flags);
        stream.Write(header);
        stream.Write(tdigestBytes);
        stream.Write(hllBytes);
    }

    private static RecordHeader ReadRecordHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[RecordHeaderSize];
        FillBuffer(stream, header);

        int blockIndex = BinaryPrimitives.ReadInt32LittleEndian(header[0..4]);
        uint blockCrc32 = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        long valueCount = BinaryPrimitives.ReadInt64LittleEndian(header[8..16]);
        int tdigestLength = BinaryPrimitives.ReadInt32LittleEndian(header[16..20]);
        int hllLength = BinaryPrimitives.ReadInt32LittleEndian(header[20..24]);
        int flags = BinaryPrimitives.ReadInt32LittleEndian(header[24..28]);

        if (valueCount < 0 || tdigestLength < 0 || hllLength < 0)
            throw new InvalidDataException("SDBAIDX record header 非法。");

        return new RecordHeader(blockIndex, blockCrc32, valueCount, tdigestLength, hllLength, flags);
    }

    private static byte[] ReadPayload(Stream stream, int length)
    {
        if (length == 0)
            return [];

        var bytes = new byte[length];
        FillBuffer(stream, bytes);
        return bytes;
    }

    private static void ValidateBlockIndex(int blockIndex, IReadOnlyList<BlockDescriptor> descriptors)
    {
        if (blockIndex < 0 || blockIndex >= descriptors.Count)
            throw new InvalidDataException("SDBAIDX 中的 blockIndex 越界。");
        if (descriptors[blockIndex].FieldType is not (Storage.Format.FieldType.Float64
            or Storage.Format.FieldType.Int64
            or Storage.Format.FieldType.Boolean))
        {
            throw new InvalidDataException("SDBAIDX 指向了非数值 block。");
        }
    }

    private static void FillBuffer(Stream stream, Span<byte> buffer)
    {
        int readTotal = 0;
        while (readTotal < buffer.Length)
        {
            int read = stream.Read(buffer[readTotal..]);
            if (read == 0)
                throw new InvalidDataException("SDBAIDX 文件截断。");
            readTotal += read;
        }
    }

    private readonly record struct RecordHeader(
        int BlockIndex,
        uint BlockCrc32,
        long ValueCount,
        int TDigestLength,
        int HyperLogLogLength,
        int Flags);
}
