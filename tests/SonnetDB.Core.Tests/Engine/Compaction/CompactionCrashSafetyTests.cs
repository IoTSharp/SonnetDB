using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Engine.Compaction;

/// <summary>
/// Compaction 崩溃安全测试：验证 rename 前崩溃、rename 后删除前崩溃时的数据完整性保证。
/// </summary>
public sealed class CompactionCrashSafetyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SegmentWriter _writer = new(new SegmentWriterOptions { FsyncOnCommit = false });
    private readonly SegmentReaderOptions _readerOpts = SegmentReaderOptions.Default;

    public CompactionCrashSafetyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, TsdbPaths.SegmentsDirName));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string SegPath(long segId) => TsdbPaths.SegmentPath(_tempDir, segId);

    private SegmentReader WriteSegment(long segId, int count, long tsBase, ulong seriesId = 1UL)
    {
        var mt = new MemTable();
        for (int i = 0; i < count; i++)
            mt.Append(seriesId, tsBase + i, "v", FieldValue.FromDouble(i), i + 1L);
        _writer.WriteFrom(mt, segId, SegPath(segId));
        return SegmentReader.Open(SegPath(segId), _readerOpts);
    }

    // ── 崩溃场景 A：Execute 之后、Commit 之前崩溃 ───────────────────────────
    // 结果：pending manifest 已声明新段尚未提交；重启后跳过新段，仅加载旧段。

    [Fact]
    public void CrashAfterExecuteBeforeCommit_PendingReplacementIgnoredOnRestart()
    {
        using var r1 = WriteSegment(1, 50, tsBase: 1000);
        using var r2 = WriteSegment(2, 50, tsBase: 2000);

        var plan = new CompactionPlan(0, new long[] { 1, 2 }.AsReadOnly());
        var readerDict = new Dictionary<long, SegmentReader> { [1] = r1, [2] = r2 };
        var compactor = new SegmentCompactor(new SegmentWriterOptions { FsyncOnCommit = false });

        SegmentReplacementManifest.RecordPendingReplacement(_tempDir, 100, plan.SourceSegmentIds);
        var result = compactor.Execute(plan, readerDict, 100, SegPath(100));

        // 旧段文件仍在
        Assert.True(File.Exists(SegPath(1)), "旧段 1 应仍然存在");
        Assert.True(File.Exists(SegPath(2)), "旧段 2 应仍然存在");
        // 新段文件已落盘
        Assert.True(File.Exists(SegPath(100)), "新段 100 应已落盘");

        // "重启"：pending 状态的新段不加载，避免新旧段重复。
        var mgr = SegmentManager.Open(_tempDir);
        int totalPoints = 0;
        foreach (var reader in mgr.Readers)
            foreach (var block in reader.Blocks)
                totalPoints += block.Count;

        Assert.Equal(2, mgr.SegmentCount);
        Assert.Equal(100, totalPoints);
        mgr.Dispose();
    }

    // ── 崩溃场景 B：Commit/Swap 之后、Delete 旧段之前崩溃 ───────────────────
    // 结果：新段已提交，旧段文件仍在磁盘；重启后旧段被 manifest 过滤。

    [Fact]
    public void CrashAfterCommitBeforeDelete_SupersededSegmentsIgnoredOnRestart()
    {
        WriteSegment(1, 30, tsBase: 1000);
        WriteSegment(2, 30, tsBase: 2000);

        using var mgr = SegmentManager.Open(_tempDir);
        Assert.Equal(2, mgr.SegmentCount);

        // 写合并段
        var r1 = mgr.Readers[0];
        var r2 = mgr.Readers[1];
        var plan = new CompactionPlan(0, new long[] { 1, 2 }.AsReadOnly());
        var readerDict = new Dictionary<long, SegmentReader> { [1] = r1, [2] = r2 };
        var compactor = new SegmentCompactor(new SegmentWriterOptions { FsyncOnCommit = false });
        var result = compactor.Execute(plan, readerDict, 100, SegPath(100));

        SegmentReplacementManifest.CommitReplacement(_tempDir, 100, plan.SourceSegmentIds);
        mgr.SwapSegments(new long[] { 1, 2 }, SegPath(100));

        // 不删除旧文件（模拟崩溃）
        // 旧文件应仍在
        Assert.True(File.Exists(SegPath(1)), "旧段 1 应仍在磁盘");
        Assert.True(File.Exists(SegPath(2)), "旧段 2 应仍在磁盘");
        Assert.True(File.Exists(SegPath(100)), "新段 100 应在磁盘");

        // "重启"后：只加载已提交的新段。
        var mgr2 = SegmentManager.Open(_tempDir);
        Assert.Equal(1, mgr2.SegmentCount);
        Assert.Equal(100, mgr2.Readers[0].Header.SegmentId);

        int totalPoints = 0;
        foreach (var reader in mgr2.Readers)
            foreach (var block in reader.Blocks)
                totalPoints += block.Count;

        Assert.Equal(60, totalPoints);
        mgr2.Dispose();
    }

    [Fact]
    public void PendingReplacementWithoutSegment_AdvancesNextSegmentIdOnReopen()
    {
        WriteSegment(1, 20, tsBase: 1000);
        SegmentReplacementManifest.RecordPendingReplacement(_tempDir, 100, new long[] { 1 });

        using var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _tempDir,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        });

        Assert.True(db.NextSegmentId > 100);
    }

    // ── 临时文件不影响合法段扫描 ─────────────────────────────────────────────

    [Fact]
    public void SpuriousTempFile_IsIgnoredOnOpen()
    {
        WriteSegment(1, 20, tsBase: 1000);

        // 留一个 .tmp 临时文件（模拟崩溃在 rename 之前）
        string tmpFile = SegPath(99) + ".tmp";
        File.WriteAllBytes(tmpFile, new byte[64]); // 无效内容

        // SegmentManager.Open 只扫描 .SDBSEG，.tmp 应被忽略
        using var mgr = SegmentManager.Open(_tempDir);
        Assert.Equal(1, mgr.SegmentCount);
    }
}
