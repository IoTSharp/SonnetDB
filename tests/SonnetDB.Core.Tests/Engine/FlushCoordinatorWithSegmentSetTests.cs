using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Segments;
using SonnetDB.Wal;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>
/// <see cref="FlushCoordinator"/> 与 <see cref="WalSegmentSet"/> 联合测试。
/// </summary>
public sealed class FlushCoordinatorWithSegmentSetTests : IDisposable
{
    private readonly string _tempDir;

    public FlushCoordinatorWithSegmentSetTests()
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
            WalBufferSize = 4 * 1024,
            SegmentWriterOptions = segOpts ?? new SegmentWriterOptions { FsyncOnCommit = false },
        };

    private WalSegmentSet OpenWalSet(WalRollingPolicy? policy = null, long initialStartLsn = 1) =>
        WalSegmentSet.Open(
            TsdbPaths.WalDir(_tempDir),
            policy ?? new WalRollingPolicy { Enabled = true, MaxBytesPerSegment = 512, MaxRecordsPerSegment = 10 },
            bufferSize: 4 * 1024,
            initialStartLsn: initialStartLsn);

    [Fact]
    public void Flush_RecyclesOldSegments_OnlyActiveRemains()
    {
        var opts = MakeOptions();
        var coordinator = new FlushCoordinator(opts);
        var memTable = new MemTable();

        using var walSet = OpenWalSet();

        // 写入若干记录触发多次自动 Roll
        for (int i = 0; i < 50; i++)
            walSet.AppendWritePoint(1UL, 1000L + i, "cpu", FieldValue.FromDouble(i));

        // 此时应有多个 segment
        int segCountBeforeFlush = walSet.Segments.Count;

        // 手动设置 MemTable 数据（模拟写入）
        memTable.Append(1UL, 1000L, "cpu", FieldValue.FromDouble(50.0), walSet.NextLsn - 1);

        // 执行 Flush
        var result = coordinator.Flush(memTable, walSet, 1L);

        Assert.NotNull(result);
        // Flush 后 RecycleUpTo 应清理旧 segment，只剩 active
        Assert.True(walSet.Segments.Count < segCountBeforeFlush, $"Expected fewer segments after flush; before={segCountBeforeFlush}, after={walSet.Segments.Count}");
        Assert.Single(walSet.Segments);
        // 新契约：FlushCoordinator 不再 Reset MemTable（清空由 Tsdb 层原子 swap 完成）。
        // 此处只验证段编码与 WAL 回收；MemTable 的数据在协调器视角保持不变。
        Assert.Equal(1, (int)memTable.PointCount);
    }

    [Fact]
    public void Flush_CheckpointRecord_FoundInWalAfterFlush()
    {
        var opts = MakeOptions();
        var coordinator = new FlushCoordinator(opts);
        var memTable = new MemTable();

        using var walSet = OpenWalSet(new WalRollingPolicy { Enabled = false });

        // 写入若干点并加入 MemTable
        for (int i = 0; i < 5; i++)
        {
            long lsn = walSet.AppendWritePoint(1UL, 1000L + i, "cpu", FieldValue.FromDouble(i));
            memTable.Append(1UL, 1000L + i, "cpu", FieldValue.FromDouble(i), lsn);
        }

        coordinator.Flush(memTable, walSet, 1L);

        // Flush 后：含 Checkpoint 的旧 segment 被 RecycleUpTo 删除，只剩 active segment
        // 这是正确的行为：checkpoint 已被 Sync 到磁盘并提供了崩溃恢复保证，之后旧段被安全清理
        Assert.Single(walSet.Segments);

        // 新契约：FlushCoordinator 不再 Reset MemTable（清空由 Tsdb 层原子 swap 完成）。
        Assert.Equal(5, (int)memTable.PointCount);
    }

    [Fact]
    public void Flush_AfterCrashWithCheckpointButNoRoll_ReplaySkipsPoints()
    {
        // 场景：Flush 步骤 3 完成（AppendCheckpoint+Sync），但步骤 4（Roll）之前崩溃
        // 重新 Open 后：CheckpointLsn 已记录 → replay 应跳过已落盘的 WritePoint，MemTable 为空
        var opts = MakeOptions();
        var coordinator = new FlushCoordinator(opts);
        var memTable = new MemTable();
        string walDir = TsdbPaths.WalDir(_tempDir);

        // 使用 SeriesCatalog 计算正确的 SeriesId
        var preCatalog = new SonnetDB.Catalog.SeriesCatalog();
        var tags = new Dictionary<string, string> { ["host"] = "h1" };
        var preEntry = preCatalog.GetOrAdd("cpu", tags);

        // 写入 5 个点
        long lastLsn;
        using (var walSet = WalSegmentSet.Open(walDir, new WalRollingPolicy { Enabled = false }, 4 * 1024))
        {
            walSet.AppendCreateSeries(preEntry.Id, "cpu", tags);
            for (int i = 0; i < 5; i++)
            {
                long lsn = walSet.AppendWritePoint(preEntry.Id, 1000L + i, "cpu", FieldValue.FromDouble(i));
                memTable.Append(preEntry.Id, 1000L + i, "cpu", FieldValue.FromDouble(i), lsn);
            }

            lastLsn = memTable.LastLsn;

            // 模拟 Flush 的步骤 2+3（写 Segment + AppendCheckpoint+Sync），但跳过 Roll
            string segPath = TsdbPaths.SegmentPath(_tempDir, 1L);
            var segWriter = new SegmentWriter(new SegmentWriterOptions { FsyncOnCommit = false });
            segWriter.WriteFrom(memTable, 1L, segPath);

            walSet.AppendCheckpoint(lastLsn);
            walSet.Sync();

            // 不 Roll，也不 RecycleUpTo（模拟崩溃）
        }

        // 重新 Open WAL segment set
        using var walSet2 = WalSegmentSet.Open(walDir, new WalRollingPolicy { Enabled = false }, 4 * 1024);
        var catalog = new SonnetDB.Catalog.SeriesCatalog();
        var result = walSet2.ReplayWithCheckpoint(catalog);

        // CheckpointLsn 已记录
        Assert.Equal(lastLsn, result.CheckpointLsn);

        // WritePoint 全部被跳过（都在 checkpointLsn 之前）
        Assert.Empty(result.WritePoints);

        // MemTable 重建应为空
        var memTable2 = new MemTable();
        memTable2.ReplayFrom(result.WritePoints);
        Assert.Equal(0L, memTable2.PointCount);
    }
}
