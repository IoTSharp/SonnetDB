using System.Runtime.InteropServices;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Storage.Segments;

public sealed class Int64CompressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-issue367-" + Guid.NewGuid().ToString("N"));

    public Int64CompressionTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void WriteFrom_ConstantQuality_ReducesPhysicalFileAndPreservesRange()
    {
        var table = new MemTable();
        for (int i = 0; i < 4096; i++)
            table.Append(1UL, 1000L + i, "quality", FieldValue.FromLong(192), i + 1L);

        string raw = Path.Combine(_root, "raw.SDBSEG");
        string compressed = Path.Combine(_root, "compressed.SDBSEG");
        new SegmentWriter(new SegmentWriterOptions
        {
            TimestampEncoding = BlockEncoding.DeltaTimestamp,
        }).WriteFrom(table, 1, raw);
        new SegmentWriter(new SegmentWriterOptions
        {
            TimestampEncoding = BlockEncoding.DeltaTimestamp,
            ValueEncoding = BlockEncoding.DeltaValue,
        }).WriteFrom(table, 2, compressed);

        Assert.True(new FileInfo(compressed).Length < new FileInfo(raw).Length / 2,
            $"raw={new FileInfo(raw).Length}, compressed={new FileInfo(compressed).Length}");
        using var reader = SegmentReader.Open(compressed);
        var block = Assert.Single(reader.Blocks);
        Assert.Equal(BlockEncoding.Int64Rle, block.ValueEncoding);
        Assert.Equal(10, block.ValuePayloadLength);
        var points = reader.DecodeBlockRange(block, 1999, 2001);
        Assert.Equal(new long[] { 1999, 2000, 2001 }, points.Select(point => point.Timestamp));
        Assert.All(points, point => Assert.Equal(192L, point.Value.AsLong()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(512)]
    public void WriteFrom_NonRepeatingInt64_FallsBackWithoutExpansion(int count)
    {
        var table = new MemTable();
        for (int i = 0; i < count; i++)
            table.Append(1, i, "quality", FieldValue.FromLong(unchecked((long)((ulong)i * 11400714819323198485UL))), i + 1);
        string path = Path.Combine(_root, "fallback.SDBSEG");
        new SegmentWriter(new SegmentWriterOptions { ValueEncoding = BlockEncoding.DeltaValue })
            .WriteFrom(table, 1, path);
        using var reader = SegmentReader.Open(path);
        var block = Assert.Single(reader.Blocks);
        Assert.Equal(BlockEncoding.DeltaValue, block.ValueEncoding);
        Assert.Equal(count * sizeof(long), block.ValuePayloadLength);
        Assert.Equal(count, reader.DecodeBlock(block).Length);
    }

    [Fact]
    public void Open_LegacyV6DeltaValueInt64_StillReadsRawExactly()
    {
        string path = Path.Combine(_root, "legacy-v6.SDBSEG");
        var table = new MemTable();
        long[] values = [long.MinValue, long.MaxValue, -1, 0, 1];
        for (int i = 0; i < values.Length; i++)
            table.Append(1, i, "quality", FieldValue.FromLong(values[i]), i + 1);
        new SegmentWriter(new SegmentWriterOptions { ValueEncoding = BlockEncoding.DeltaValue })
            .WriteFrom(table, 1, path);
        RewriteVersion(path, 6);
        for (int reopen = 0; reopen < 2; reopen++)
        {
            using var reader = SegmentReader.Open(path);
            Assert.Equal(values, reader.DecodeBlock(Assert.Single(reader.Blocks)).Select(point => point.Value.AsLong()));
        }
    }

    [Fact]
    public void Open_NewRleFlagInV6Segment_RejectsInsteadOfReinterpretingOldFormat()
    {
        string path = WriteConstantSegment();
        RewriteVersion(path, 6);
        Assert.Throws<SegmentCorruptedException>(() => SegmentReader.Open(path));
    }

    [Theory]
    [InlineData(0x20, FieldType.Int64)]
    [InlineData(0x12, FieldType.Int64)]
    [InlineData(0x10, FieldType.Float64)]
    public void Open_InvalidEncodingMetadata_Rejects(int flags, FieldType fieldType)
    {
        string path = WriteConstantSegment();
        byte[] bytes = File.ReadAllBytes(path);
        var header = MemoryMarshal.Read<BlockHeader>(bytes.AsSpan(FormatSizes.SegmentHeaderSize));
        header.Encoding = (BlockEncoding)flags;
        header.FieldType = fieldType;
        MemoryMarshal.Write(bytes.AsSpan(FormatSizes.SegmentHeaderSize), in header);
        File.WriteAllBytes(path, bytes);
        Assert.Throws<SegmentCorruptedException>(() => SegmentReader.Open(path));
    }

    [Fact]
    public void DecodeBlock_FlippedRlePayload_RejectsCrc()
    {
        string path = WriteConstantSegment();
        byte[] bytes = File.ReadAllBytes(path);
        var header = MemoryMarshal.Read<BlockHeader>(bytes.AsSpan(FormatSizes.SegmentHeaderSize));
        int valueOffset = FormatSizes.SegmentHeaderSize + FormatSizes.BlockHeaderSize
            + header.FieldNameUtf8Length + header.TimestampPayloadLength;
        bytes[valueOffset] ^= 0x40;
        File.WriteAllBytes(path, bytes);
        using var reader = SegmentReader.Open(path);
        Assert.Throws<SegmentCorruptedException>(() => reader.DecodeBlock(Assert.Single(reader.Blocks)));
    }

    [Fact]
    public void Open_FlushedSegmentAndRetainedWal_PreservesAllPointsAcrossTwoReopens()
    {
        string databaseRoot = Path.Combine(_root, "database");
        bool failFlush = false;
        var segmentOptions = new SegmentWriterOptions
        {
            TimestampEncoding = BlockEncoding.DeltaTimestamp,
            ValueEncoding = BlockEncoding.DeltaValue,
            FailAt = _ =>
            {
                if (failFlush)
                    throw new IOException("#367 保留 WAL 的确定性 flush 故障。");
            },
        };
        var options = new TsdbOptions
        {
            RootDirectory = databaseRoot,
            SyncWalOnEveryWrite = true,
            SegmentWriterOptions = segmentOptions,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        };
        using (var database = Tsdb.Open(options))
        {
            for (int i = 0; i < 64; i++)
                database.Write(Point.Create("plat_hist", i,
                    new Dictionary<string, string> { ["sensor"] = "1" },
                    new Dictionary<string, FieldValue> { ["quality"] = FieldValue.FromLong(i < 32 ? 192 : long.MinValue) }));
            database.FlushNow();
            for (int i = 64; i < 96; i++)
                database.Write(Point.Create("plat_hist", i,
                    new Dictionary<string, string> { ["sensor"] = "1" },
                    new Dictionary<string, FieldValue> { ["quality"] = FieldValue.FromLong(long.MaxValue) }));
            failFlush = true;
        }

        options = options with { SegmentWriterOptions = segmentOptions with { FailAt = null } };
        for (int reopen = 0; reopen < 2; reopen++)
        {
            using var database = Tsdb.Open(options);
            var series = Assert.Single(database.Catalog.Snapshot());
            var points = database.Query.Execute(new PointQuery(series.Id, "quality", new TimeRange(0, 96))).ToArray();
            Assert.Equal(96, points.Length);
            for (int i = 0; i < points.Length; i++)
            {
                Assert.Equal(i, points[i].Timestamp);
                Assert.Equal(i < 32 ? 192 : i < 64 ? long.MinValue : long.MaxValue, points[i].Value.AsLong());
            }
            if (reopen == 0)
                Assert.True(database.MemTable.PointCount >= 32, "未刷盘的后32点必须实际来自 WAL 重放。");
        }
    }

    private string WriteConstantSegment()
    {
        string path = Path.Combine(_root, "constant.SDBSEG");
        var table = new MemTable();
        for (int i = 0; i < 256; i++)
            table.Append(1, i, "quality", FieldValue.FromLong(192), i + 1);
        new SegmentWriter(new SegmentWriterOptions { ValueEncoding = BlockEncoding.DeltaValue })
            .WriteFrom(table, 1, path);
        return path;
    }

    private static void RewriteVersion(string path, int version)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var header = MemoryMarshal.Read<SegmentHeader>(bytes);
        header.FormatVersion = version;
        MemoryMarshal.Write(bytes, in header);
        int footerOffset = bytes.Length - FormatSizes.SegmentFooterSize;
        var footer = MemoryMarshal.Read<SegmentFooter>(bytes.AsSpan(footerOffset));
        footer.FormatVersion = version;
        footer.ComputeAndSetFooterChecksum();
        MemoryMarshal.Write(bytes.AsSpan(footerOffset), in footer);
        File.WriteAllBytes(path, bytes);
    }
}
