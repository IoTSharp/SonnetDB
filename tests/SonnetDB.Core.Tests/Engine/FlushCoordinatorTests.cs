using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Segments;
using SonnetDB.Wal;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>
/// <see cref="FlushCoordinator"/> 单元测试。
/// </summary>
public sealed class FlushCoordinatorTests : IDisposable
{
    private readonly string _tempDir;

    public FlushCoordinatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(TsdbPaths.WalDir(_tempDir));
        Directory.CreateDirectory(TsdbPaths.SegmentsDir(_tempDir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private TsdbOptions MakeOptions(SegmentWriterOptions? segOpts = null) =>
        new TsdbOptions
        {
            RootDirectory = _tempDir,
            WalBufferSize = 64 * 1024,
            SegmentWriterOptions = segOpts ?? new SegmentWriterOptions { FsyncOnCommit = false },
        };

    private WalSegmentSet OpenWalSet(long initialStartLsn = 1) =>
        WalSegmentSet.Open(TsdbPaths.WalDir(_tempDir), new WalRollingPolicy { Enabled = false }, bufferSize: 64 * 1024, initialStartLsn: initialStartLsn);

    [Fact]
    public void Flush_EmptyMemTable_ReturnsNull_NoSegmentCreated()
    {
        var options = MakeOptions();
        var coordinator = new FlushCoordinator(options);
        var memTable = new MemTable();
        using var walSet = OpenWalSet();

        var result = coordinator.Flush(memTable, walSet, 1L);

        Assert.Null(result);

        // Segment 目录下不应有任何文件
        var segments = TsdbPaths.EnumerateSegments(_tempDir).ToList();
        Assert.Empty(segments);
    }

    [Fact]
    public void Flush_NonEmptyMemTable_CreatesSegment_DoesNotResetMemTable()
    {
        var options = MakeOptions();
        var coordinator = new FlushCoordinator(options);
        var memTable = new MemTable();

        // 写入几个点
        memTable.Append(1UL, 1000L, "cpu", FieldValue.FromDouble(50.0), 1L);
        memTable.Append(1UL, 2000L, "cpu", FieldValue.FromDouble(60.0), 2L);

        using var walSet = OpenWalSet(initialStartLsn: 3);

        var result = coordinator.Flush(memTable, walSet, 1L);

        // 应返回非 null 结果
        Assert.NotNull(result);
        Assert.Equal(1L, result.SegmentId);

        // Segment 文件应存在
        string expectedSegPath = TsdbPaths.SegmentPath(_tempDir, 1L);
        Assert.True(File.Exists(expectedSegPath));

        // 新契约：FlushCoordinator 不再 Reset MemTable（清空由 Tsdb 层原子 swap 完成，修 #190）。
        // 协调器视角下 MemTable 的数据保持不变。
        Assert.Equal(2, (int)memTable.PointCount);
        Assert.Equal(1, memTable.SeriesCount);
        Assert.Equal(1000L, memTable.MinTimestamp);
        Assert.Equal(2000L, memTable.MaxTimestamp);
    }

    [Fact]
    public void Flush_CheckpointRecord_WrittenToWal()
    {
        // 验证 Flush 后：含 Checkpoint 记录的旧 segment 被正确回收，只剩 active
        string root2 = _tempDir + "_v2";
        Directory.CreateDirectory(TsdbPaths.WalDir(root2));
        Directory.CreateDirectory(TsdbPaths.SegmentsDir(root2));
        var opts2 = new TsdbOptions
        {
            RootDirectory = root2,
            WalBufferSize = 64 * 1024,
            SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
        };
        var coordinator2 = new FlushCoordinator(opts2);
        var memTable2 = new MemTable();
        memTable2.Append(1UL, 1000L, "v", FieldValue.FromDouble(1.0), 5L);
        memTable2.Append(1UL, 2000L, "v", FieldValue.FromDouble(2.0), 6L);
        memTable2.Append(1UL, 3000L, "v", FieldValue.FromDouble(3.0), 7L);

        using (var walSet2 = WalSegmentSet.Open(TsdbPaths.WalDir(root2), new WalRollingPolicy { Enabled = false }, 64 * 1024, initialStartLsn: 8))
        {
            coordinator2.Flush(memTable2, walSet2, 1L);

            // Flush 后：Checkpoint 已被写入并 Sync，然后 Roll+RecycleUpTo 回收了含 Checkpoint 的旧段
            // 只剩 active segment（新段）
            var walSegments = WalSegmentLayout.Enumerate(TsdbPaths.WalDir(root2));
            Assert.True(walSegments.Count >= 1, "Should have at least the active segment");

            // 新契约：FlushCoordinator 不再 Reset MemTable（清空由 Tsdb 层原子 swap 完成）。
            Assert.Equal(3, (int)memTable2.PointCount);
        }

        try { Directory.Delete(root2, recursive: true); } catch { }
    }

    [Fact]
    public void FlushCoordinator_NullOptions_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => new FlushCoordinator(null!));
    }

    [Fact]
    public void Flush_NullMemTable_ThrowsArgumentNull()
    {
        var options = MakeOptions();
        var coordinator = new FlushCoordinator(options);
        using var walSet = OpenWalSet();

        Assert.Throws<ArgumentNullException>(() =>
            coordinator.Flush(null!, walSet, 1L));
    }

    [Fact]
    public void Flush_NullWalSet_ThrowsArgumentNull()
    {
        var options = MakeOptions();
        var coordinator = new FlushCoordinator(options);
        var memTable = new MemTable();
        memTable.Append(1UL, 1000L, "v", FieldValue.FromDouble(1.0), 1L);

        Assert.Throws<ArgumentNullException>(() =>
            coordinator.Flush(memTable, null!, 1L));
    }

    [Fact]
    public void Flush_SegmentFileExistsAfterFlush()
    {
        var options = MakeOptions();
        var coordinator = new FlushCoordinator(options);
        var memTable = new MemTable();

        memTable.Append(42UL, 9999L, "temperature", FieldValue.FromDouble(36.6), 1L);

        using var walSet = OpenWalSet(initialStartLsn: 2);

        var result = coordinator.Flush(memTable, walSet, 7L);
        Assert.NotNull(result);
        Assert.Equal(7L, result.SegmentId);
        Assert.True(File.Exists(TsdbPaths.SegmentPath(_tempDir, 7L)));
        Assert.True(result.TotalBytes > 0);
    }
}
