using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using SonnetDB.Buffers;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Storage.Segments;

/// <summary>
/// <see cref="SegmentReader"/> 单元测试：验证段文件解析、索引查找、损坏检测。
/// </summary>
public sealed class SegmentReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SegmentWriter _writer = new(new SegmentWriterOptions { FsyncOnCommit = false });

    public SegmentReaderTests()
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

    // ── 空段（128B）────────────────────────────────────────────────────────────

    [Fact]
    public void EmptySegment_OpenSucceeds_BlockCountZero()
    {
        string path = TempPath();
        _writer.WriteFrom(new MemTable(), 1L, path);

        using var reader = SegmentReader.Open(path);

        Assert.Equal(0, reader.BlockCount);
        Assert.Empty(reader.Blocks);
        Assert.Equal(128L, reader.FileLength);
        Assert.Equal(path, reader.Path);
    }

    [Fact]
    public void EmptySegment_FindAll_ReturnEmpty()
    {
        string path = TempPath();
        _writer.WriteFrom(new MemTable(), 1L, path);

        using var reader = SegmentReader.Open(path);

        Assert.Empty(reader.FindBySeries(1UL));
        Assert.Empty(reader.FindBySeriesAndField(1UL, "usage"));
        Assert.Empty(reader.FindByTimeRange(0, long.MaxValue));
    }

    // ── 单 series 单 field 1000 Double 点 ────────────────────────────────────

    [Fact]
    public void Single1000DoublePoints_HeaderFooterCorrect()
    {
        string path = TempPath();
        const long minTs = 1000L;
        const ulong seriesId = 0xDEADBEEFUL;
        const string fieldName = "value";
        const int count = 1000;

        var mt = new MemTable();
        for (int i = 0; i < count; i++)
            mt.Append(seriesId, minTs + i * 100L, fieldName, FieldValue.FromDouble(i * 1.5), i + 1L);

        _writer.WriteFrom(mt, 99L, path);

        using var reader = SegmentReader.Open(path);

        Assert.True(reader.Header.IsValid());
        Assert.True(reader.Footer.IsValid());
        Assert.Equal(99L, reader.Header.SegmentId);
        Assert.Equal(1, reader.Header.BlockCount);
    }

    [Fact]
    public void Single1000DoublePoints_BlockDescriptorCorrect()
    {
        string path = TempPath();
        const long minTs = 1000L;
        const ulong seriesId = 42UL;
        const string fieldName = "temperature";
        const int count = 1000;
        long maxTs = minTs + (count - 1) * 100L;

        var mt = new MemTable();
        for (int i = 0; i < count; i++)
            mt.Append(seriesId, minTs + i * 100L, fieldName, FieldValue.FromDouble(i * 2.0), i + 1L);

        _writer.WriteFrom(mt, 1L, path);

        using var reader = SegmentReader.Open(path);

        Assert.Equal(1, reader.BlockCount);

        var d = reader.Blocks[0];
        Assert.Equal(0, d.Index);
        Assert.Equal(seriesId, d.SeriesId);
        Assert.Equal(minTs, d.MinTimestamp);
        Assert.Equal(maxTs, d.MaxTimestamp);
        Assert.Equal(count, d.Count);
        Assert.Equal(FieldType.Float64, d.FieldType);
        Assert.Equal(fieldName, d.FieldName);
        Assert.Equal(FormatSizes.SegmentHeaderSize, d.FileOffset);
        Assert.True(d.Crc32 != 0);
    }

    [Fact]
    public void Single1000DoublePoints_DecodeBlock_AllPointsCorrect()
    {
        string path = TempPath();
        const long minTs = 0L;
        const ulong seriesId = 1UL;
        const string fieldName = "v";
        const int count = 1000;

        var mt = new MemTable();
        for (int i = 0; i < count; i++)
            mt.Append(seriesId, minTs + i * 10L, fieldName, FieldValue.FromDouble(i * 1.0), i + 1L);

        _writer.WriteFrom(mt, 1L, path);

        using var reader = SegmentReader.Open(path);
        var points = reader.DecodeBlock(reader.Blocks[0]);

        Assert.Equal(count, points.Length);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(minTs + i * 10L, points[i].Timestamp);
            Assert.Equal(FieldType.Float64, points[i].Value.Type);
            Assert.Equal(i * 1.0, points[i].Value.AsDouble(), precision: 10);
        }
    }

    [Fact]
    public void Single1000DoublePoints_DecodeBlockRange_CorrectSubset()
    {
        string path = TempPath();
        const ulong seriesId = 1UL;
        const int count = 1000;

        var mt = new MemTable();
        for (int i = 0; i < count; i++)
            mt.Append(seriesId, i * 10L, "v", FieldValue.FromDouble(i * 1.0), i + 1L);

        _writer.WriteFrom(mt, 1L, path);

        using var reader = SegmentReader.Open(path);
        // Range: timestamp [100, 200] → indices 10..20 inclusive (11 points)
        var points = reader.DecodeBlockRange(reader.Blocks[0], 100L, 200L);

        Assert.Equal(11, points.Length);
        Assert.Equal(100L, points[0].Timestamp);
        Assert.Equal(200L, points[^1].Timestamp);

        foreach (var p in points)
            Assert.InRange(p.Timestamp, 100L, 200L);
    }

    // ── 多 series × 多 field ───────────────────────────────────────────────────

    [Fact]
    public void MultiSeriesMultiField_FindBySeries_ReturnsCorrectBlocks()
    {
        string path = TempPath();
        var mt = BuildMixedMemTable(seriesCount: 5, fieldsPerSeries: 3);
        _writer.WriteFrom(mt, 1L, path);

        using var reader = SegmentReader.Open(path);

        for (ulong sid = 1UL; sid <= 5UL; sid++)
        {
            var blocks = reader.FindBySeries(sid);
            Assert.Equal(3, blocks.Count);
            foreach (var b in blocks)
                Assert.Equal(sid, b.SeriesId);
        }
    }

    [Fact]
    public void MultiSeriesMultiField_FindBySeries_PreservesBlockDescriptorOrder()
    {
        string path = TempPath();
        var mt = BuildMixedMemTable(seriesCount: 4, fieldsPerSeries: 4);
        _writer.WriteFrom(mt, 1L, path);

        using var reader = SegmentReader.Open(path);

        var expected = reader.Blocks
            .Where(static b => b.SeriesId == 3UL)
            .Select(static b => b.Index)
            .ToArray();
        var actual = reader.FindBySeries(3UL);

        Assert.Equal(["field0", "field1", "field2", "field3"], actual.Select(static b => b.FieldName).ToArray());
        Assert.Equal(expected, actual.Select(static b => b.Index).ToArray());
    }

    [Fact]
    public void MultiSeriesMultiField_FindBySeriesAndField_OneResult()
    {
        string path = TempPath();
        var mt = BuildMixedMemTable(seriesCount: 5, fieldsPerSeries: 3);
        _writer.WriteFrom(mt, 1L, path);

        using var reader = SegmentReader.Open(path);

        var blocks = reader.FindBySeriesAndField(1UL, "field0");
        Assert.Single(blocks);
        Assert.Equal(1UL, blocks[0].SeriesId);
        Assert.Equal("field0", blocks[0].FieldName);
    }

    [Fact]
    public void MultiSeriesMultiField_FindBySeriesAndField_PreservesMatchingOrder()
    {
        string path = TempPath();
        var series = new List<MemTableSeries>
        {
            BuildSeries(1UL, "value", 0L),
            BuildSeries(2UL, "other", 100L),
            BuildSeries(1UL, "value", 200L),
            BuildSeries(1UL, "status", 300L),
        };
        _writer.Write(series, 1L, path);

        using var reader = SegmentReader.Open(path);

        var expected = reader.Blocks
            .Where(static b => b.SeriesId == 1UL && b.FieldName == "value")
            .Select(static b => b.Index)
            .ToArray();
        var actual = reader.FindBySeriesAndField(1UL, "value");

        Assert.Equal(2, actual.Count);
        Assert.Equal(expected, actual.Select(static b => b.Index).ToArray());
        Assert.All(actual, static b =>
        {
            Assert.Equal(1UL, b.SeriesId);
            Assert.Equal("value", b.FieldName);
        });
    }

    [Fact]
    public void MultiSeriesMultiField_FindBySeriesAndField_UnknownFieldReturnsEmpty()
    {
        string path = TempPath();
        var mt = BuildMixedMemTable(seriesCount: 2, fieldsPerSeries: 2);
        _writer.WriteFrom(mt, 1L, path);

        using var reader = SegmentReader.Open(path);

        var blocks = reader.FindBySeriesAndField(1UL, "nonexistent");
        Assert.Empty(blocks);
    }

    [Fact]
    public void MultiSeriesMultiField_FindBySeries_UnknownSeriesReturnsEmpty()
    {
        string path = TempPath();
        var mt = BuildMixedMemTable(seriesCount: 2, fieldsPerSeries: 2);
        _writer.WriteFrom(mt, 1L, path);

        using var reader = SegmentReader.Open(path);

        Assert.Same(Array.Empty<BlockDescriptor>(), reader.FindBySeries(99UL));
        Assert.Same(Array.Empty<BlockDescriptor>(), reader.FindBySeriesAndField(99UL, "field0"));
    }

    [Fact]
    public void MultiSeriesMultiField_FindByTimeRange_ReturnsOnlyOverlapping()
    {
        string path = TempPath();
        var mt = new MemTable();
        // series 1: timestamps 0-9 (by 1)
        for (int i = 0; i < 10; i++)
            mt.Append(1UL, i, "a", FieldValue.FromDouble(i), i + 1L);
        // series 2: timestamps 100-109
        for (int i = 0; i < 10; i++)
            mt.Append(2UL, 100 + i, "a", FieldValue.FromDouble(i), 100 + i + 1L);

        _writer.WriteFrom(mt, 1L, path);

        using var reader = SegmentReader.Open(path);

        var overlapping = reader.FindByTimeRange(5, 15);
        // Only series 1 overlaps [5,15] (max=9 >= 5 and min=0 <= 15)
        // Series 2: min=100 > 15, no overlap
        Assert.Single(overlapping);
        Assert.Equal(1UL, overlapping[0].SeriesId);

        var noOverlap = reader.FindByTimeRange(200, 300);
        Assert.Empty(noOverlap);

        var all = reader.FindByTimeRange(0, 200);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void FindByTimeRange_WithOverlappingBlocksAndInclusiveBounds_ReturnsSegmentOrder()
    {
        string path = TempPath();
        var series = new List<MemTableSeries>
        {
            BuildRangeSeries(1UL, "v", 0L, 100L),
            BuildRangeSeries(2UL, "v", 101L, 200L),
            BuildRangeSeries(3UL, "v", 50L, 150L),
            BuildRangeSeries(4UL, "v", 201L, 300L),
        };
        _writer.Write(series, 1L, path);

        using var reader = SegmentReader.Open(path);

        var blocks = reader.FindByTimeRange(100L, 101L);

        Assert.Equal([1UL, 2UL, 3UL], blocks.Select(static b => b.SeriesId).ToArray());
        Assert.Equal([0, 1, 2], blocks.Select(static b => b.Index).ToArray());
    }

    [Fact]
    public void FindByTimeRange_WithOutOfOrderBlockTimestamps_PreservesSegmentBlockOrder()
    {
        string path = TempPath();
        var series = new List<MemTableSeries>
        {
            BuildRangeSeries(1UL, "v", 300L, 310L),
            BuildRangeSeries(2UL, "v", 0L, 10L),
            BuildRangeSeries(3UL, "v", 100L, 110L),
            BuildRangeSeries(4UL, "v", 200L, 210L),
        };
        _writer.Write(series, 1L, path);

        using var reader = SegmentReader.Open(path);

        var blocks = reader.FindByTimeRange(5L, 305L);

        Assert.Equal([1UL, 2UL, 3UL, 4UL], blocks.Select(static b => b.SeriesId).ToArray());
        Assert.Equal([0, 1, 2, 3], blocks.Select(static b => b.Index).ToArray());
    }

    [Fact]
    public void FindByTimeRange_WithLargeBlockList_IsFasterThanLinearScan()
    {
        string path = TempPath();
        const int BlockCount = 8192;
        const int Iterations = 1000;
        long targetTimestamp = BlockCount - 1L;
        var series = BuildLargeOutOfOrderTimeSeries(BlockCount);
        _writer.Write(series, 1L, path);

        using var reader = SegmentReader.Open(path);
        Assert.Equal(BlockCount, reader.BlockCount);
        Assert.Single(reader.FindByTimeRange(targetTimestamp, targetTimestamp));
        Assert.Single(LinearFindByTimeRange(reader.Blocks, targetTimestamp, targetTimestamp));

        long optimizedTicks = MeasureRepeated(
            Iterations,
            () => reader.FindByTimeRange(targetTimestamp, targetTimestamp),
            out int optimizedCount);
        long linearTicks = MeasureRepeated(
            Iterations,
            () => LinearFindByTimeRange(reader.Blocks, targetTimestamp, targetTimestamp),
            out int linearCount);

        Assert.Equal(Iterations, optimizedCount);
        Assert.Equal(linearCount, optimizedCount);
        double speedup = (double)linearTicks / Math.Max(1L, optimizedTicks);
        Assert.True(
            speedup >= 3.0d,
            $"Expected indexed time-range lookup to be at least 3x faster than a linear scan. " +
            $"Indexed={optimizedTicks} ticks, linear={linearTicks} ticks, speedup={speedup:0.00}x.");
    }

    [Fact]
    public void AllFieldTypes_DecodeBlock_AllCorrect()
    {
        string path = TempPath();
        var mt = new MemTable();
        const int count = 10;

        // Float64
        for (int i = 0; i < count; i++)
            mt.Append(1UL, i * 10L, "f64", FieldValue.FromDouble(i * 1.1), i + 1L);

        // Int64
        for (int i = 0; i < count; i++)
            mt.Append(2UL, i * 10L, "i64", FieldValue.FromLong(i * 100L), count + i + 1L);

        // Boolean
        for (int i = 0; i < count; i++)
            mt.Append(3UL, i * 10L, "bool", FieldValue.FromBool(i % 2 == 0), count * 2 + i + 1L);

        // String (含中文)
        for (int i = 0; i < count; i++)
            mt.Append(4UL, i * 10L, "字段名", FieldValue.FromString($"值{i}"), count * 3 + i + 1L);

        _writer.WriteFrom(mt, 1L, path);

        using var reader = SegmentReader.Open(path);

        // Float64
        {
            var blocks = reader.FindBySeriesAndField(1UL, "f64");
            Assert.Single(blocks);
            var pts = reader.DecodeBlock(blocks[0]);
            for (int i = 0; i < count; i++)
                Assert.Equal(i * 1.1, pts[i].Value.AsDouble(), precision: 10);
        }

        // Int64
        {
            var blocks = reader.FindBySeriesAndField(2UL, "i64");
            Assert.Single(blocks);
            var pts = reader.DecodeBlock(blocks[0]);
            for (int i = 0; i < count; i++)
                Assert.Equal(i * 100L, pts[i].Value.AsLong());
        }

        // Boolean
        {
            var blocks = reader.FindBySeriesAndField(3UL, "bool");
            Assert.Single(blocks);
            var pts = reader.DecodeBlock(blocks[0]);
            for (int i = 0; i < count; i++)
                Assert.Equal(i % 2 == 0, pts[i].Value.AsBool());
        }

        // String with Chinese field name
        {
            var blocks = reader.FindBySeriesAndField(4UL, "字段名");
            Assert.Single(blocks);
            var pts = reader.DecodeBlock(blocks[0]);
            for (int i = 0; i < count; i++)
                Assert.Equal($"值{i}", pts[i].Value.AsString());
        }
    }

    // ── CRC 校验失败 ──────────────────────────────────────────────────────────

    [Fact]
    public void BlockCrcFailure_ReadBlock_ThrowsSegmentCorruptedException()
    {
        string path = TempPath();
        var mt = new MemTable();
        mt.Append(1UL, 0L, "v", FieldValue.FromDouble(1.0), 1L);
        _writer.WriteFrom(mt, 1L, path);

        // 篡改 Block payload 的第一个字节
        byte[] bytes = File.ReadAllBytes(path);
        int payloadStart = FormatSizes.SegmentHeaderSize + FormatSizes.BlockHeaderSize;
        bytes[payloadStart] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        using var reader = SegmentReader.Open(path, new SegmentReaderOptions { VerifyBlockCrc = true });
        var ex = Assert.Throws<SegmentCorruptedException>(() => reader.ReadBlock(reader.Blocks[0]));
        Assert.Equal(path, ex.SegmentPath);
    }

    [Fact]
    public void BlockCrcFailure_VerifyDisabled_DoesNotThrow()
    {
        string path = TempPath();
        var mt = new MemTable();
        mt.Append(1UL, 0L, "v", FieldValue.FromDouble(1.0), 1L);
        _writer.WriteFrom(mt, 1L, path);

        byte[] bytes = File.ReadAllBytes(path);
        int payloadStart = FormatSizes.SegmentHeaderSize + FormatSizes.BlockHeaderSize;
        bytes[payloadStart] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        using var reader = SegmentReader.Open(path, new SegmentReaderOptions { VerifyBlockCrc = false });
        // Should not throw
        var data = reader.ReadBlock(reader.Blocks[0]);
        Assert.NotNull(data.Descriptor.FieldName);
    }

    [Fact]
    public void IndexCrcFailure_Open_ThrowsSegmentCorruptedException()
    {
        string path = TempPath();
        var mt = new MemTable();
        mt.Append(1UL, 0L, "v", FieldValue.FromDouble(1.0), 1L);
        _writer.WriteFrom(mt, 1L, path);

        // 篡改 BlockIndexEntry 区域的第一个字节
        byte[] bytes = File.ReadAllBytes(path);
        var footer = MemoryMarshal.Read<SegmentFooter>(bytes.AsSpan(bytes.Length - FormatSizes.SegmentFooterSize));
        bytes[(int)footer.IndexOffset] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.Throws<SegmentCorruptedException>(() =>
            SegmentReader.Open(path, new SegmentReaderOptions { VerifyIndexCrc = true }));
    }

    // ── #195：Footer 自校验 CRC ────────────────────────────────────────────────

    [Fact]
    public void FooterChecksum_FreshV6Segment_IsPresentAndValid()
    {
        string path = TempPath();
        var mt = new MemTable();
        mt.Append(1UL, 0L, "v", FieldValue.FromDouble(1.0), 1L);
        _writer.WriteFrom(mt, 1L, path);

        byte[] bytes = File.ReadAllBytes(path);
        var footer = MemoryMarshal.Read<SegmentFooter>(bytes.AsSpan(bytes.Length - FormatSizes.SegmentFooterSize));

        // 新写入的 v6 段应携带非 0 的 footer 自校验，且自洽。
        Assert.Equal(TsdbMagic.SegmentFormatVersion, footer.FormatVersion);
        Assert.NotEqual(0u, footer.FooterChecksum);
        Assert.True(footer.VerifyFooterChecksum());
    }

    [Fact]
    public void FooterChecksum_CoveredFieldBitFlip_IsDetected()
    {
        // 直接在结构层验证：翻转覆盖区内任一字段（IndexOffset），自校验必须失败。
        var footer = SegmentFooter.CreateNew(indexCount: 3, indexOffset: 128, fileLength: 4096);
        footer.Crc32 = 0xDEADBEEF;
        footer.ComputeAndSetFooterChecksum();
        Assert.True(footer.VerifyFooterChecksum());

        var flipped = footer;
        flipped.IndexOffset ^= 0x100; // 位翻转，可能仍满足某些布局等式
        Assert.False(flipped.VerifyFooterChecksum());

        var flippedLen = footer;
        flippedLen.FileLength ^= 0x1;
        Assert.False(flippedLen.VerifyFooterChecksum());

        var flippedCount = footer;
        flippedCount.IndexCount ^= 0x1;
        Assert.False(flippedCount.VerifyFooterChecksum());
    }

    [Fact]
    public void FooterChecksum_LegacyZeroChecksum_SkipsVerification()
    {
        // 旧文件语义：FooterChecksum==0 视为无该校验，直接通过（向后读兼容）。
        var footer = SegmentFooter.CreateNew(indexCount: 1, indexOffset: 128, fileLength: 256);
        Assert.Equal(0u, footer.FooterChecksum);
        Assert.True(footer.VerifyFooterChecksum());
    }

    [Fact]
    public void IndexCrcFailure_VerifyDisabled_DoesNotThrow()
    {
        string path = TempPath();
        var mt = new MemTable();
        mt.Append(1UL, 0L, "v", FieldValue.FromDouble(1.0), 1L);
        _writer.WriteFrom(mt, 1L, path);

        byte[] bytes = File.ReadAllBytes(path);
        var footer = MemoryMarshal.Read<SegmentFooter>(bytes.AsSpan(bytes.Length - FormatSizes.SegmentFooterSize));
        // Also fix the blockindexentry bytes so that open won't fail due to inconsistency
        // Actually, if we only tamper with the index CRC check disabled, Open should succeed
        // But we need to keep the data consistent for Open to parse correctly
        // Let's only flip a single bit that affects CRC but not struct parse
        bytes[(int)footer.IndexOffset + 40] ^= 0x01; // Reserved byte
        File.WriteAllBytes(path, bytes);

        using var reader = SegmentReader.Open(path, new SegmentReaderOptions { VerifyIndexCrc = false });
        // Should succeed
        Assert.Equal(1, reader.BlockCount);
    }

    // ── Magic / Version 错误 ──────────────────────────────────────────────────

    [Fact]
    public void CorruptedMagic_Open_ThrowsSegmentCorruptedException()
    {
        string path = TempPath();
        _writer.WriteFrom(new MemTable(), 1L, path);

        byte[] bytes = File.ReadAllBytes(path);
        bytes[0] = 0xFF; // 破坏 SegmentHeader.Magic 第一个字节
        File.WriteAllBytes(path, bytes);

        Assert.Throws<SegmentCorruptedException>(() => SegmentReader.Open(path));
    }

    [Fact]
    public void CorruptedVersion_Open_ThrowsSegmentCorruptedException()
    {
        string path = TempPath();
        _writer.WriteFrom(new MemTable(), 1L, path);

        byte[] bytes = File.ReadAllBytes(path);
        // FormatVersion is at offset 8 (4 bytes), set to wrong value
        bytes[8] = 0x99;
        bytes[9] = 0x99;
        bytes[10] = 0x99;
        bytes[11] = 0x99;
        File.WriteAllBytes(path, bytes);

        Assert.Throws<SegmentCorruptedException>(() => SegmentReader.Open(path));
    }

    // ── 文件截断 ──────────────────────────────────────────────────────────────

    [Fact]
    public void TruncatedFile_Open_ThrowsSegmentCorruptedException()
    {
        string path = TempPath();
        _writer.WriteFrom(new MemTable(), 1L, path);

        byte[] bytes = File.ReadAllBytes(path);
        byte[] truncated = bytes[..^1]; // 截断最后一字节
        File.WriteAllBytes(path, truncated);

        var ex = Assert.Throws<SegmentCorruptedException>(() => SegmentReader.Open(path));
        Assert.Contains("mini-footer", ex.Message);
        Assert.Contains("128", ex.Message);
    }

    [Fact]
    public void TooShortFile_Open_ThrowsSegmentCorruptedException()
    {
        string path = TempPath();
        File.WriteAllBytes(path, new byte[10]); // 远小于最小合法长度

        Assert.Throws<SegmentCorruptedException>(() => SegmentReader.Open(path));
    }

    // ── Footer.FooterOffset 不一致 ──────────────────────────────────────────────

    [Fact]
    public void FooterOffsetMismatch_WithHeaderMiniFooterFallback_OpenSucceeds()
    {
        string path = TempPath();
        _writer.WriteFrom(new MemTable(), 1L, path);

        byte[] bytes = File.ReadAllBytes(path);
        // IndexOffset 在 SegmentFooter 的 offset 16（相对于 footer start）
        // Footer 从 bytes.Length - 64 开始
        // Magic(8) + FormatVersion(4) + IndexCount(4) + IndexOffset(8) = offset 24 into footer
        int footerStart = bytes.Length - FormatSizes.SegmentFooterSize;
        int indexOffsetPos = footerStart + 16; // Magic(8)+FormatVersion(4)+IndexCount(4)=16
        // 设置 IndexOffset 为一个错误的值
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(indexOffsetPos, 8), 999L);
        File.WriteAllBytes(path, bytes);

        using var reader = SegmentReader.Open(path);
        Assert.Equal(0, reader.BlockCount);
        Assert.Equal(FormatSizes.SegmentHeaderSize, reader.Footer.IndexOffset);
    }

    [Fact]
    public void FooterOffsetMismatch_WithoutMiniFooter_ThrowsSegmentCorruptedException()
    {
        string path = TempPath();
        _writer.WriteFrom(new MemTable(), 1L, path);

        byte[] bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), 5);
        bytes.AsSpan(36, FormatSizes.SegmentHeaderSize - 36).Clear();
        int footerStart = bytes.Length - FormatSizes.SegmentFooterSize;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(footerStart + 8, 4), 5);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(footerStart + 16, 8), 999L);
        File.WriteAllBytes(path, bytes);

        Assert.Throws<SegmentCorruptedException>(() => SegmentReader.Open(path));
    }

    // ── BlockHeader 与 IndexEntry 不一致 ──────────────────────────────────────

    [Fact]
    public void BlockHeaderIndexMismatch_SeriesId_Open_ThrowsSegmentCorruptedException()
    {
        string path = TempPath();
        var mt = new MemTable();
        mt.Append(1UL, 0L, "v", FieldValue.FromDouble(1.0), 1L);
        _writer.WriteFrom(mt, 1L, path);

        // 篡改 BlockIndexEntry.SeriesId（第一个字节）
        byte[] bytes = File.ReadAllBytes(path);
        var footer = MemoryMarshal.Read<SegmentFooter>(bytes.AsSpan(bytes.Length - FormatSizes.SegmentFooterSize));
        // SeriesId is first 8 bytes in BlockIndexEntry
        bytes[(int)footer.IndexOffset] ^= 0x01;
        File.WriteAllBytes(path, bytes);

        // Open with VerifyIndexCrc=false so we skip IndexCrc check and check BlockHeader-Index consistency
        Assert.Throws<SegmentCorruptedException>(() =>
            SegmentReader.Open(path, new SegmentReaderOptions { VerifyIndexCrc = false }));
    }

    // ── MinTimestamp / MaxTimestamp ───────────────────────────────────────────

    [Fact]
    public void MinMaxTimestamp_SingleBlock_Correct()
    {
        string path = TempPath();
        var mt = new MemTable();
        mt.Append(1UL, 100L, "v", FieldValue.FromDouble(1.0), 1L);
        mt.Append(1UL, 200L, "v", FieldValue.FromDouble(2.0), 2L);
        mt.Append(1UL, 300L, "v", FieldValue.FromDouble(3.0), 3L);
        _writer.WriteFrom(mt, 1L, path);

        using var reader = SegmentReader.Open(path);

        Assert.Equal(100L, reader.MinTimestamp);
        Assert.Equal(300L, reader.MaxTimestamp);
    }

    [Fact]
    public void EmptySegment_MinMaxTimestamp_DefaultValues()
    {
        string path = TempPath();
        _writer.WriteFrom(new MemTable(), 1L, path);

        using var reader = SegmentReader.Open(path);

        Assert.Equal(long.MaxValue, reader.MinTimestamp);
        Assert.Equal(long.MinValue, reader.MaxTimestamp);
    }

    // ── Dispose 后操作 ────────────────────────────────────────────────────────

    [Fact]
    public void AfterDispose_ReadBlock_ThrowsObjectDisposedException()
    {
        string path = TempPath();
        var mt = new MemTable();
        mt.Append(1UL, 0L, "v", FieldValue.FromDouble(1.0), 1L);
        _writer.WriteFrom(mt, 1L, path);

        SegmentReader reader = SegmentReader.Open(path);
        var desc = reader.Blocks[0];
        reader.Dispose();

        Assert.Throws<ObjectDisposedException>(() => reader.ReadBlock(desc));
    }

    // ── 辅助方法 ──────────────────────────────────────────────────────────────

    private static MemTable BuildMixedMemTable(int seriesCount, int fieldsPerSeries)
    {
        var mt = new MemTable();
        for (int s = 0; s < seriesCount; s++)
        {
            ulong sid = (ulong)(s + 1);
            for (int f = 0; f < fieldsPerSeries; f++)
            {
                string fieldName = $"field{f}";
                for (int p = 0; p < 10; p++)
                    mt.Append(sid, p * 10L, fieldName, FieldValue.FromDouble(p * 1.0), s * fieldsPerSeries * 10 + f * 10 + p + 1L);
            }
        }
        return mt;
    }

    private static MemTableSeries BuildSeries(ulong seriesId, string fieldName, long startTimestamp)
    {
        var series = new MemTableSeries(new SeriesFieldKey(seriesId, fieldName), FieldType.Float64);
        for (int i = 0; i < 3; i++)
            series.Append(startTimestamp + i, FieldValue.FromDouble(i));
        return series;
    }

    private static MemTableSeries BuildRangeSeries(ulong seriesId, string fieldName, long minTimestamp, long maxTimestamp)
    {
        var series = new MemTableSeries(new SeriesFieldKey(seriesId, fieldName), FieldType.Float64);
        series.Append(minTimestamp, FieldValue.FromDouble(minTimestamp));
        if (maxTimestamp != minTimestamp)
            series.Append(maxTimestamp, FieldValue.FromDouble(maxTimestamp));
        return series;
    }

    private static List<MemTableSeries> BuildLargeOutOfOrderTimeSeries(int blockCount)
    {
        var series = new List<MemTableSeries>(blockCount);
        for (int i = 0; i < blockCount; i++)
        {
            long timestamp = blockCount - i - 1L;
            series.Add(BuildRangeSeries((ulong)(i + 1), "v", timestamp, timestamp));
        }

        return series;
    }

    private static long MeasureRepeated(
        int iterations,
        Func<IReadOnlyList<BlockDescriptor>> query,
        out int totalCount)
    {
        int count = 0;
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            count += query().Count;
        stopwatch.Stop();

        totalCount = count;
        return stopwatch.ElapsedTicks;
    }

    private static IReadOnlyList<BlockDescriptor> LinearFindByTimeRange(
        IReadOnlyList<BlockDescriptor> blocks,
        long from,
        long toInclusive)
    {
        List<BlockDescriptor>? result = null;
        foreach (var block in blocks)
        {
            if (block.MinTimestamp <= toInclusive && block.MaxTimestamp >= from)
            {
                result ??= [];
                result.Add(block);
            }
        }

        return result is null ? Array.Empty<BlockDescriptor>() : result;
    }
}
