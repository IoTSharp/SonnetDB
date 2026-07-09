using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Storage.Segments;

/// <summary>
/// <see cref="SegmentReader"/> memory-mapped 读取路径测试。
/// </summary>
public sealed class SegmentReaderMemoryMappedTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SegmentWriter _writer = new(new SegmentWriterOptions { FsyncOnCommit = false });

    public SegmentReaderMemoryMappedTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void DefaultOptions_EnableMemoryMappedReaderForLargeSegments()
    {
        Assert.True(SegmentReaderOptions.Default.UseMemoryMappedFileForLargeSegments);
        Assert.Equal(64L * 1024L * 1024L, SegmentReaderOptions.Default.MemoryMappedFileThresholdBytes);
    }

    [Fact]
    public void Open_DefaultOptions_UsesByteArrayReader()
    {
        string path = TempPath("default.SDBSEG");
        _writer.WriteFrom(BuildMixedMemTable(), 1L, path);

        using var reader = SegmentReader.Open(path);

        Assert.False(reader.UsesMemoryMappedStorage);
        Assert.Equal(2, reader.BlockCount);
    }

    [Fact]
    public void Open_WithMemoryMappedEnabledBelowThreshold_UsesByteArrayFallback()
    {
        string path = TempPath("below-threshold.SDBSEG");
        _writer.WriteFrom(BuildMixedMemTable(), 2L, path);

        using var reader = SegmentReader.Open(path, new SegmentReaderOptions
        {
            UseMemoryMappedFileForLargeSegments = true,
            MemoryMappedFileThresholdBytes = long.MaxValue,
        });

        Assert.False(reader.UsesMemoryMappedStorage);
        Assert.Equal(2, reader.BlockCount);
    }

    [Fact]
    public void Open_WithMemoryMappedEnabled_DecodesBlocksOnCurrentPlatform()
    {
        string path = TempPath("mmap.SDBSEG");
        _writer.WriteFrom(BuildMixedMemTable(), 3L, path);

        using var reader = SegmentReader.Open(path, new SegmentReaderOptions
        {
            UseMemoryMappedFileForLargeSegments = true,
            MemoryMappedFileThresholdBytes = 0,
        });

        Assert.True(reader.UsesMemoryMappedStorage);

        var numericBlock = Assert.Single(reader.FindBySeriesAndField(1UL, "value"));
        var numericPoints = reader.DecodeBlockRange(numericBlock, 1002L, 1004L);
        Assert.Equal([1002L, 1003L, 1004L], numericPoints.Select(static p => p.Timestamp).ToArray());
        Assert.Equal([2.0, 3.0, 4.0], numericPoints.Select(static p => p.Value.AsDouble()).ToArray());

        var stringBlock = Assert.Single(reader.FindBySeriesAndField(2UL, "status"));
        var data = reader.ReadBlock(stringBlock);
        Assert.Equal("status".Length, data.FieldNameUtf8.Length);
        var stringPoints = BlockDecoder.Decode(stringBlock, data.TimestampPayload, data.ValuePayload);
        Assert.Equal(["s0", "s1", "s2"], stringPoints.Select(static p => p.Value.AsString()).ToArray());
    }

    [Fact]
    public void Dispose_AfterMemoryMappedOpen_ReleasesFileHandle()
    {
        string path = TempPath("dispose.SDBSEG");
        _writer.WriteFrom(BuildMixedMemTable(), 4L, path);

        var reader = SegmentReader.Open(path, new SegmentReaderOptions
        {
            UseMemoryMappedFileForLargeSegments = true,
            MemoryMappedFileThresholdBytes = 0,
        });

        Assert.True(reader.UsesMemoryMappedStorage);
        reader.Dispose();

        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    private string TempPath(string name) => Path.Combine(_tempDir, name);

    private static MemTable BuildMixedMemTable()
    {
        var memTable = new MemTable();
        long lsn = 1L;
        for (int i = 0; i < 8; i++)
        {
            memTable.Append(1UL, 1000L + i, "value", FieldValue.FromDouble(i), lsn++);
        }

        for (int i = 0; i < 3; i++)
        {
            memTable.Append(2UL, 2000L + i, "status", FieldValue.FromString($"s{i}"), lsn++);
        }

        return memTable;
    }
}
