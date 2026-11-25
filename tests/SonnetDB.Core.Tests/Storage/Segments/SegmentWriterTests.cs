using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Storage.Segments;

/// <summary>
/// <see cref="SegmentWriter"/> 单元测试：验证文件布局、不变量、错误处理。
/// </summary>
public sealed class SegmentWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SegmentWriter _writer = new(new SegmentWriterOptions { FsyncOnCommit = false });

    public SegmentWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string TempPath(string name = "test.SDBSEG") =>
        Path.Combine(_tempDir, name);

    // ── 空 MemTable ──────────────────────────────────────────────────────────

    [Fact]
    public void EmptyMemTable_WritesFileOfExactly128Bytes()
    {
        string path = TempPath();
        var memTable = new MemTable();

        var result = _writer.WriteFrom(memTable, 1L, path);

        Assert.True(File.Exists(path));
        Assert.Equal(FormatSizes.SegmentHeaderSize + FormatSizes.SegmentFooterSize, result.TotalBytes);
        Assert.Equal(128L, result.TotalBytes);
        Assert.Equal(0, result.BlockCount);
        AssertSegmentInvariants(path);
    }

    [Fact]
    public void EmptyMemTable_FooterHasCorrectOffsets()
    {
        string path = TempPath();
        var result = _writer.WriteFrom(new MemTable(), 42L, path);

        byte[] bytes = File.ReadAllBytes(path);
        var footer = MemoryMarshal.Read<SegmentFooter>(bytes.AsSpan(bytes.Length - FormatSizes.SegmentFooterSize));

        Assert.Equal(0, footer.IndexCount);
        Assert.Equal(FormatSizes.SegmentHeaderSize, (int)footer.IndexOffset);
        Assert.Equal(128L, footer.FileLength);
        Assert.True(footer.IsValid());
    }

    // ── 单桶 1000 个 Double 点 ──────────────────────────────────────────────

    [Fact]
    public void SingleBucket_1000DoublePoints_FileExistsAndHeaderCorrect()
    {
        string path = TempPath();
        var memTable = BuildMemTableWithDoubleBucket(1000);

        var result = _writer.WriteFrom(memTable, 99L, path);

        Assert.True(File.Exists(path));
        Assert.Equal(1, result.BlockCount);

        byte[] bytes = File.ReadAllBytes(path);
        var segHeader = MemoryMarshal.Read<SegmentHeader>(bytes.AsSpan(0, FormatSizes.SegmentHeaderSize));
        Assert.Equal(99L, segHeader.SegmentId);
        Assert.Equal(1, segHeader.BlockCount);
        Assert.True(segHeader.IsValid());
    }

    [Fact]
    public void SingleBucket_SplitsByMaxPoints_RoundTripsDuplicatesAndKeepsIndexesValid()
    {
        string path = TempPath();
        var memTable = new MemTable();
        var options = new SegmentWriterOptions
        {
            FsyncOnCommit = false,
            MaxPointsPerBlock = 3,
        };

        for (int i = 0; i < 7; i++)
            memTable.Append(1UL, i < 3 ? 1000L : 2000L + i, "usage", FieldValue.FromDouble(i), i + 1L);

        var result = new SegmentWriter(options).WriteFrom(memTable, 1L, path);

        Assert.Equal(3, result.BlockCount);
        using var reader = SegmentReader.Open(path);
        Assert.Equal(3, reader.BlockCount);
        var decoded = reader.Blocks.SelectMany(block => reader.DecodeBlock(block)).ToArray();
        Assert.Equal(7, decoded.Length);
        Assert.Equal([0d, 1d, 2d, 3d, 4d, 5d, 6d], decoded.Select(static point => point.Value.AsDouble()));
        Assert.Equal([1000L, 1000L, 1000L, 2003L, 2004L, 2005L, 2006L], decoded.Select(static point => point.Timestamp));
        AssertSegmentInvariants(path);
    }

    [Fact]
    public void SealedWrite_PreservesStableOrderingWithoutPublishingBucketSnapshots()
    {
        var memTable = new MemTable();
        memTable.Append(1, 3000, "usage", FieldValue.FromDouble(0), 1);
        memTable.Append(1, 1000, "usage", FieldValue.FromDouble(1), 2);
        memTable.Append(1, 3000, "usage", FieldValue.FromDouble(2), 3);

        string sealedPath = TempPath("sealed.SDBSEG");
        _writer.WriteFromSealed(memTable, 1L, sealedPath, vectorIndexes: null);
        var series = Assert.Single(memTable.SnapshotAll());
        Assert.False(series.SnapshotForSegmentWrite().Equals(series.SnapshotForSegmentWrite()));
        var querySnapshot = series.Snapshot();
        Assert.True(querySnapshot.Equals(series.SnapshotForSegmentWrite()));

        string regularPath = TempPath("regular.SDBSEG");
        _writer.WriteFrom(memTable, 1L, regularPath);
        byte[] sealedBytes = File.ReadAllBytes(sealedPath);
        byte[] regularBytes = File.ReadAllBytes(regularPath);
        Assert.True(sealedBytes.AsSpan(FormatSizes.SegmentHeaderSize).SequenceEqual(
            regularBytes.AsSpan(FormatSizes.SegmentHeaderSize)));
        using var reader = SegmentReader.Open(sealedPath);
        var points = reader.DecodeBlock(Assert.Single(reader.Blocks));
        Assert.Equal([1000L, 3000L, 3000L], points.Select(static point => point.Timestamp));
        Assert.Equal([1d, 0d, 2d], points.Select(static point => point.Value.AsDouble()));
        AssertSegmentInvariants(sealedPath);
    }

    [Fact]
    public void ManyNumericBuckets_EmbeddedSketchBytesMatchCanonicalFormat()
    {
        const int blockCount = 192;
        var memTable = new MemTable();
        for (int i = 0; i < blockCount; i++)
            memTable.Append((ulong)i + 1, 1000 + i, "count", FieldValue.FromLong(i), i + 1);

        string path = TempPath("numeric-buckets.SDBSEG");
        var result = _writer.WriteFromSealed(memTable, 1, path, vectorIndexes: null);
        using var reader = SegmentReader.Open(path);
        Assert.Equal(blockCount, result.BlockCount);
        Assert.Equal(blockCount, reader.Blocks.Count);
        using var expected = new MemoryStream();
        var sketches = new List<BlockAggregateSketch>(blockCount);
        for (int i = 0; i < blockCount; i++)
        {
            var block = reader.Blocks[i];
            Assert.True(BlockAggregateSketch.TryBuild(i, block.Crc32, block.FieldType,
                reader.DecodeBlock(block), out var sketch));
            sketches.Add(sketch);
        }

        SegmentAggregateSketchFile.WriteTo(expected, sketches);
        using var file = File.OpenRead(path);
        long extensionOffset = result.IndexOffset + (long)FormatSizes.BlockIndexEntrySize * blockCount;
        Assert.Equal(expected.Length, result.FooterOffset - extensionOffset);
        file.Position = extensionOffset;
        byte[] actual = new byte[checked((int)expected.Length)];
        file.ReadExactly(actual);
        Assert.True(expected.GetBuffer().AsSpan(0, actual.Length).SequenceEqual(actual));
        Assert.Empty(Directory.EnumerateFiles(_tempDir, "*.aidx.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void SingleBucket_1000DoublePoints_BlockHeaderFieldsCorrect()
    {
        string path = TempPath();
        const int count = 1000;
        const long minTs = 1000L;
        const long maxTs = minTs + (count - 1) * 100L;
        const ulong seriesId = 0xDEADBEEFUL;
        const string fieldName = "value";

        var memTable = new MemTable();
        for (int i = 0; i < count; i++)
            memTable.Append(seriesId, minTs + i * 100L, fieldName, FieldValue.FromDouble(i * 1.5), i + 1L);

        _writer.WriteFrom(memTable, 1L, path);

        byte[] bytes = File.ReadAllBytes(path);
        var blockHeader = MemoryMarshal.Read<BlockHeader>(bytes.AsSpan(FormatSizes.SegmentHeaderSize, FormatSizes.BlockHeaderSize));

        Assert.Equal(seriesId, blockHeader.SeriesId);
        Assert.Equal(minTs, blockHeader.MinTimestamp);
        Assert.Equal(maxTs, blockHeader.MaxTimestamp);
        Assert.Equal(count, blockHeader.Count);
        Assert.Equal(FieldType.Float64, blockHeader.FieldType);
        Assert.Equal(count * 8, blockHeader.TimestampPayloadLength);
        Assert.Equal(count * 8, blockHeader.ValuePayloadLength);
        Assert.Equal(BlockEncoding.None, blockHeader.Encoding);
        AssertSegmentInvariants(path);
    }

    [Fact]
    public void SingleBucket_1000DoublePoints_BlockHeaderCrc32MatchesRecomputed()
    {
        string path = TempPath();
        const int count = 1000;
        const ulong seriesId = 1UL;
        const string fieldName = "v";

        var memTable = new MemTable();
        for (int i = 0; i < count; i++)
            memTable.Append(seriesId, i * 10L, fieldName, FieldValue.FromDouble(i), i + 1L);

        _writer.WriteFrom(memTable, 1L, path);

        byte[] bytes = File.ReadAllBytes(path);
        var blockHeader = MemoryMarshal.Read<BlockHeader>(bytes.AsSpan(FormatSizes.SegmentHeaderSize, FormatSizes.BlockHeaderSize));

        // Locate payload regions
        int blockHeaderEnd = FormatSizes.SegmentHeaderSize + FormatSizes.BlockHeaderSize;
        int fieldNameLen = blockHeader.FieldNameUtf8Length;
        int tsPayloadLen = blockHeader.TimestampPayloadLength;
        int valPayloadLen = blockHeader.ValuePayloadLength;

        var fieldNameBytes = bytes.AsSpan(blockHeaderEnd, fieldNameLen);
        var tsBytes = bytes.AsSpan(blockHeaderEnd + fieldNameLen, tsPayloadLen);
        var valBytes = bytes.AsSpan(blockHeaderEnd + fieldNameLen + tsPayloadLen, valPayloadLen);

        // Recompute CRC32
        var crc = new Crc32();
        crc.Append(fieldNameBytes);
        crc.Append(tsBytes);
        crc.Append(valBytes);
        uint expectedCrc32 = crc.GetCurrentHashAsUInt32();

        Assert.Equal(expectedCrc32, blockHeader.Crc32);
    }

    [Fact]
    public void SingleBucket_1000DoublePoints_BlockIndexEntry_CorrectOffsetAndHash()
    {
        string path = TempPath();
        const string fieldName = "usage";
        const ulong seriesId = 42UL;
        const int count = 1000;

        var memTable = new MemTable();
        for (int i = 0; i < count; i++)
            memTable.Append(seriesId, i * 10L, fieldName, FieldValue.FromDouble(i), i + 1L);

        _writer.WriteFrom(memTable, 1L, path);

        byte[] bytes = File.ReadAllBytes(path);
        var footer = MemoryMarshal.Read<SegmentFooter>(bytes.AsSpan(bytes.Length - FormatSizes.SegmentFooterSize));
        var entry = MemoryMarshal.Read<BlockIndexEntry>(bytes.AsSpan((int)footer.IndexOffset, FormatSizes.BlockIndexEntrySize));

        Assert.Equal(seriesId, entry.SeriesId);
        Assert.Equal((long)FormatSizes.SegmentHeaderSize, entry.FileOffset);

        // Verify FieldNameHash
        byte[] fieldNameUtf8 = Encoding.UTF8.GetBytes(fieldName);
        int expectedHash = (int)XxHash32.HashToUInt32(fieldNameUtf8);
        Assert.Equal(expectedHash, entry.FieldNameHash);

        AssertSegmentInvariants(path);
    }

    // ── 多 series 多 field ───────────────────────────────────────────────────

    [Fact]
    public void MultiSeriesMultiField_30Buckets_AllInvariants()
    {
        string path = TempPath();
        var memTable = new MemTable();

        for (int s = 0; s < 10; s++)
        {
            ulong sid = (ulong)(s + 1);
            for (int f = 0; f < 3; f++)
            {
                string fieldName = $"field{f}";
                for (int p = 0; p < 10; p++)
                    memTable.Append(sid, p * 100L, fieldName, FieldValue.FromDouble(p * 1.0), p + 1L);
            }
        }

        var result = _writer.WriteFrom(memTable, 1L, path);

        Assert.Equal(30, result.BlockCount);
        AssertSegmentInvariants(path);
    }

    [Fact]
    public void MultiSeriesMultiField_SortedBySeriesIdThenFieldName()
    {
        string path = TempPath();
        var memTable = new MemTable();

        // Add in non-sorted order
        memTable.Append(3UL, 1000L, "zeta", FieldValue.FromDouble(1.0), 1L);
        memTable.Append(1UL, 2000L, "beta", FieldValue.FromDouble(2.0), 2L);
        memTable.Append(2UL, 3000L, "alpha", FieldValue.FromDouble(3.0), 3L);
        memTable.Append(1UL, 4000L, "alpha", FieldValue.FromDouble(4.0), 4L);

        _writer.WriteFrom(memTable, 1L, path);

        byte[] bytes = File.ReadAllBytes(path);
        var footer = MemoryMarshal.Read<SegmentFooter>(bytes.AsSpan(bytes.Length - FormatSizes.SegmentFooterSize));

        // Expected order: (1,alpha), (1,beta), (2,alpha), (3,zeta)
        var entries = new BlockIndexEntry[footer.IndexCount];
        for (int i = 0; i < footer.IndexCount; i++)
        {
            int off = (int)footer.IndexOffset + i * FormatSizes.BlockIndexEntrySize;
            entries[i] = MemoryMarshal.Read<BlockIndexEntry>(bytes.AsSpan(off, FormatSizes.BlockIndexEntrySize));
        }

        Assert.Equal(4, entries.Length);
        Assert.Equal(1UL, entries[0].SeriesId);
        Assert.Equal(1UL, entries[1].SeriesId);
        Assert.Equal(2UL, entries[2].SeriesId);
        Assert.Equal(3UL, entries[3].SeriesId);

        AssertSegmentInvariants(path);
    }

    [Fact]
    public void MultiSeriesMultiField_TsPayloadAndValPayloadLengthsMatchBlockHeader()
    {
        string path = TempPath();
        var memTable = new MemTable();

        for (int i = 0; i < 5; i++)
            memTable.Append(1UL, i * 100L, "f", FieldValue.FromDouble(i), i + 1L);

        _writer.WriteFrom(memTable, 1L, path);

        byte[] bytes = File.ReadAllBytes(path);
        var bh = MemoryMarshal.Read<BlockHeader>(bytes.AsSpan(FormatSizes.SegmentHeaderSize, FormatSizes.BlockHeaderSize));

        Assert.Equal(5 * 8, bh.TimestampPayloadLength);
        Assert.Equal(5 * 8, bh.ValuePayloadLength);

        AssertSegmentInvariants(path);
    }

    // ── String FieldType ─────────────────────────────────────────────────────

    [Fact]
    public void StringFieldType_WithChineseAndEmptyStrings_RoundTripCorrect()
    {
        string path = TempPath();
        var memTable = new MemTable();

        string[] values = ["hello", "世界", "", "SonnetDB测试", "ok"];
        for (int i = 0; i < values.Length; i++)
            memTable.Append(1UL, i * 100L, "msg", FieldValue.FromString(values[i]), i + 1L);

        _writer.WriteFrom(memTable, 1L, path);

        byte[] bytes = File.ReadAllBytes(path);
        var bh = MemoryMarshal.Read<BlockHeader>(bytes.AsSpan(FormatSizes.SegmentHeaderSize, FormatSizes.BlockHeaderSize));

        Assert.Equal(FieldType.String, bh.FieldType);
        Assert.Equal(values.Length, bh.Count);

        // Decode value payload and verify
        int payloadStart = FormatSizes.SegmentHeaderSize + FormatSizes.BlockHeaderSize + bh.FieldNameUtf8Length + bh.TimestampPayloadLength;
        int offset = 0;
        foreach (string expected in values)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(payloadStart + offset, 4));
            offset += 4;
            string decoded = Encoding.UTF8.GetString(bytes, payloadStart + offset, len);
            Assert.Equal(expected, decoded);
            offset += len;
        }

        AssertSegmentInvariants(path);
    }

    // ── 原子替换 ─────────────────────────────────────────────────────────────

    [Fact]
    public void AtomicRename_TempFileNotLeftAfterSuccess()
    {
        string path = TempPath();

        _writer.WriteFrom(new MemTable(), 1L, path);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void OverwriteExistingTarget_NewContentIsApplied()
    {
        string path = TempPath();

        // Write first version (empty)
        _writer.WriteFrom(new MemTable(), 1L, path);
        long firstSize = new FileInfo(path).Length;

        // Write second version with data
        var memTable = new MemTable();
        memTable.Append(1UL, 1000L, "v", FieldValue.FromDouble(1.0), 1L);
        _writer.WriteFrom(memTable, 2L, path);
        long secondSize = new FileInfo(path).Length;

        Assert.True(secondSize > firstSize);

        byte[] bytes = File.ReadAllBytes(path);
        var segHeader = MemoryMarshal.Read<SegmentHeader>(bytes.AsSpan(0, FormatSizes.SegmentHeaderSize));
        Assert.Equal(2L, segHeader.SegmentId);
        Assert.Equal(1, segHeader.BlockCount);

        AssertSegmentInvariants(path);
    }

    [Fact]
    public void TempFileConflict_ThrowsIOException()
    {
        string path = TempPath();
        string tempPath = path + ".tmp";

        // Create conflicting temp file
        File.WriteAllText(tempPath, "conflict");

        Assert.Throws<IOException>(() =>
            _writer.WriteFrom(new MemTable(), 1L, path));

        // Target file should not exist (was never created)
        Assert.False(File.Exists(path));
        // Temp file should still exist (we didn't delete what we didn't create)
        Assert.True(File.Exists(tempPath));
    }

    // ── SegmentFooter IndexCrc32 ─────────────────────────────────────────────

    [Fact]
    public void SegmentFooter_IndexCrc32_MatchesRecomputed()
    {
        string path = TempPath();
        var memTable = new MemTable();

        for (int i = 0; i < 3; i++)
            memTable.Append((ulong)(i + 1), 1000L * i, "v", FieldValue.FromDouble(i), i + 1L);

        _writer.WriteFrom(memTable, 1L, path);

        byte[] bytes = File.ReadAllBytes(path);
        var footer = MemoryMarshal.Read<SegmentFooter>(bytes.AsSpan(bytes.Length - FormatSizes.SegmentFooterSize));

        int indexTotalBytes = footer.IndexCount * FormatSizes.BlockIndexEntrySize;
        var indexBytes = bytes.AsSpan((int)footer.IndexOffset, indexTotalBytes);

        var crc = new Crc32();
        crc.Append(indexBytes);
        uint expectedCrc32 = crc.GetCurrentHashAsUInt32();

        Assert.Equal(expectedCrc32, footer.Crc32);
    }

    // ── 不变量 helper ────────────────────────────────────────────────────────

    /// <summary>校验段文件的所有结构不变量。</summary>
    internal static void AssertSegmentInvariants(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);

        Assert.True(bytes.Length >= FormatSizes.SegmentHeaderSize + FormatSizes.SegmentFooterSize,
            "File must be at least 128 bytes (header + footer).");

        // 1. SegmentHeader at offset 0 must be valid
        var segHeader = MemoryMarshal.Read<SegmentHeader>(bytes.AsSpan(0, FormatSizes.SegmentHeaderSize));
        Assert.True(segHeader.IsValid(), "SegmentHeader must have valid magic and version.");

        // 2. SegmentFooter at last 64 bytes must be valid
        var footer = MemoryMarshal.Read<SegmentFooter>(bytes.AsSpan(bytes.Length - FormatSizes.SegmentFooterSize));
        Assert.True(footer.IsValid(), "SegmentFooter must have valid magic and version.");

        // 3. v6 allows an embedded extension area between BlockIndexEntry[] and the footer.
        long indexEnd = footer.IndexOffset + (long)footer.IndexCount * FormatSizes.BlockIndexEntrySize;
        long footerStart = bytes.Length - FormatSizes.SegmentFooterSize;
        if (footer.FormatVersion >= 6)
        {
            Assert.True(indexEnd <= footerStart,
                $"Index area ends at {indexEnd}, beyond footer start {footerStart}.");
        }
        else
        {
            Assert.Equal(indexEnd, footerStart);
        }

        // 4. FileLength in footer == actual file length
        Assert.Equal((long)bytes.Length, footer.FileLength);

        // 5. SegmentHeader.BlockCount == footer.IndexCount
        Assert.Equal(segHeader.BlockCount, footer.IndexCount);

        if (segHeader.FormatVersion >= 6)
        {
            Assert.True(segHeader.TryReadFooterMiniCopy(out var mini), "SegmentHeader mini-footer copy must exist for v6 segments.");
            Assert.Equal(footer.IndexCount, mini.IndexCount);
            Assert.Equal(footer.IndexOffset, mini.IndexOffset);
            Assert.Equal(footer.FileLength, mini.FileLength);
            Assert.Equal(footer.Crc32, mini.IndexCrc32);
        }

        // 6. Each BlockIndexEntry.FileOffset points to valid BlockHeader (SeriesId matches)
        for (int i = 0; i < footer.IndexCount; i++)
        {
            int entryOff = (int)footer.IndexOffset + i * FormatSizes.BlockIndexEntrySize;
            var entry = MemoryMarshal.Read<BlockIndexEntry>(bytes.AsSpan(entryOff, FormatSizes.BlockIndexEntrySize));

            int blockOff = (int)entry.FileOffset;
            Assert.True(blockOff + FormatSizes.BlockHeaderSize <= bytes.Length,
                $"BlockIndexEntry[{i}].FileOffset {blockOff} out of range.");

            var blockHeader = MemoryMarshal.Read<BlockHeader>(bytes.AsSpan(blockOff, FormatSizes.BlockHeaderSize));
            Assert.Equal(entry.SeriesId, blockHeader.SeriesId);
        }
    }

    [Fact]
    public void GeoPointBucket_WritesGeoHashRangeInBlockHeader()
    {
        string path = TempPath();
        var memTable = new MemTable();
        memTable.Append(1UL, 1000, "position", FieldValue.FromGeoPoint(39.9042, 116.4074), 1);
        memTable.Append(1UL, 2000, "position", FieldValue.FromGeoPoint(31.2304, 121.4737), 2);

        _writer.WriteFrom(memTable, 99L, path);

        byte[] bytes = File.ReadAllBytes(path);
        var blockHeader = MemoryMarshal.Read<BlockHeader>(bytes.AsSpan(FormatSizes.SegmentHeaderSize, FormatSizes.BlockHeaderSize));
        uint h1 = GeoHash32.Encode(39.9042, 116.4074);
        uint h2 = GeoHash32.Encode(31.2304, 121.4737);
        Assert.Equal(Math.Min(h1, h2), blockHeader.GeoHashMin);
        Assert.Equal(Math.Max(h1, h2), blockHeader.GeoHashMax);

        using var reader = SegmentReader.Open(path);
        var descriptor = Assert.Single(reader.Blocks);
        Assert.True(descriptor.HasGeoHashRange);
        Assert.Equal(blockHeader.GeoHashMin, descriptor.GeoHashMin);
        Assert.Equal(blockHeader.GeoHashMax, descriptor.GeoHashMax);
    }

    // ── 辅助方法 ──────────────────────────────────────────────────────────────

    private static MemTable BuildMemTableWithDoubleBucket(int count)
    {
        var mt = new MemTable();
        for (int i = 0; i < count; i++)
            mt.Append(1UL, i * 100L, "value", FieldValue.FromDouble(i * 1.0), i + 1L);
        return mt;
    }
}
