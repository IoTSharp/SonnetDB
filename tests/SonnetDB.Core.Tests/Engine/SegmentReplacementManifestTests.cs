using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>
/// <see cref="SegmentReplacementManifest"/> 的修剪与抑制快照测试（S7 / #210）。
/// </summary>
public sealed class SegmentReplacementManifestTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SegmentWriter _writer = new(new SegmentWriterOptions { FsyncOnCommit = false });

    public SegmentReplacementManifestTests()
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

    private void WriteSegment(long segId, ulong seriesId = 1UL)
    {
        var mt = new MemTable();
        mt.Append(seriesId, 1000L, "v", FieldValue.FromDouble(1.0), 1L);
        _writer.WriteFrom(mt, segId, SegPath(segId));
    }

    [Fact]
    public void Commit_WithSourcesStillOnDisk_KeepsRecord()
    {
        // 源段仍在盘上时，committed 记录必须保留（用于抑制其在启动时被重新加载）。
        WriteSegment(1);
        WriteSegment(2);
        WriteSegment(100); // replacement

        SegmentReplacementManifest.CommitReplacement(_tempDir, 100, new long[] { 1, 2 });

        var manifest = SegmentReplacementManifest.LoadForRoot(_tempDir);
        Assert.Single(manifest.Records);

        var suppressed = manifest.GetSegmentIdsToSuppress(_tempDir);
        Assert.Contains(1L, suppressed); // 源段被抑制
        Assert.Contains(2L, suppressed);
        Assert.DoesNotContain(100L, suppressed); // replacement 可读，不抑制
    }

    [Fact]
    public void Commit_ThenSourceAndReplacementDeleted_PrunesObsoleteRecordOnNextMutate()
    {
        // committed 后源段与 replacement 段都被物理删除（replacement 在后续轮次又被并入更新的段）：
        // 下次 Mutate 时该记录应被修剪，避免 committed 记录无限累积（S7）。
        WriteSegment(1);
        WriteSegment(2);
        WriteSegment(100); // replacement

        SegmentReplacementManifest.CommitReplacement(_tempDir, 100, new long[] { 1, 2 });
        Assert.Single(SegmentReplacementManifest.LoadForRoot(_tempDir).Records);

        // 源段与 replacement 都删除（模拟 100 在下一轮又被并入 200 并清理）。
        File.Delete(SegPath(1));
        File.Delete(SegPath(2));
        File.Delete(SegPath(100));

        // 触发一次新的 Mutate（无关的 pending 记录），修剪应发生。
        WriteSegment(3);
        WriteSegment(200);
        SegmentReplacementManifest.RecordPendingReplacement(_tempDir, 200, new long[] { 3 });

        var records = SegmentReplacementManifest.LoadForRoot(_tempDir).Records;
        // 旧的 (100←1,2) committed 记录已被修剪；仅剩新的 pending 记录。
        Assert.DoesNotContain(records, r => r.ReplacementSegmentId == 100);
        Assert.Contains(records, r => r.ReplacementSegmentId == 200);
    }

    [Fact]
    public void Commit_ReplacementAliveSourcesDeleted_KeepsRecord()
    {
        // committed 后源段删除但 replacement 仍在盘上（作为活段）：记录保留，符合"source 与
        // replacement 都不存在才修剪"的语义——replacement 仍是活段，尚未被下一轮并入。
        WriteSegment(1);
        WriteSegment(100);
        SegmentReplacementManifest.CommitReplacement(_tempDir, 100, new long[] { 1 });
        File.Delete(SegPath(1));

        WriteSegment(2);
        SegmentReplacementManifest.RecordPendingReplacement(_tempDir, 2, new long[] { 99 });

        var records = SegmentReplacementManifest.LoadForRoot(_tempDir).Records;
        Assert.Contains(records, r => r.ReplacementSegmentId == 100); // replacement 100 仍活，记录保留
    }

    [Fact]
    public void Commit_ReplacementMissing_SuppressesReplacementNotSources()
    {
        // committed 但 replacement 段不在盘上（崩溃在 rename 前）：抑制 replacement，保留源段可加载。
        WriteSegment(1);
        WriteSegment(2);
        // 不写 replacement 段 100。

        SegmentReplacementManifest.CommitReplacement(_tempDir, 100, new long[] { 1, 2 });

        var manifest = SegmentReplacementManifest.LoadForRoot(_tempDir);
        var suppressed = manifest.GetSegmentIdsToSuppress(_tempDir);
        Assert.Contains(100L, suppressed);      // 不可读的 replacement 被抑制
        Assert.DoesNotContain(1L, suppressed);  // 源段保留可加载（数据不丢）
        Assert.DoesNotContain(2L, suppressed);
    }

    [Fact]
    public void ChainedCompactions_PruneKeepsManifestBounded()
    {
        // 链式 compaction：每轮 replacement 成为下一轮的 source 并被清理。稳态记录数应保持有界，
        // 而非随轮数线性增长（S7）。
        long liveId = 1;
        WriteSegment(liveId);
        for (int round = 0; round < 30; round++)
        {
            long src = liveId;
            long repl = src + 1;
            WriteSegment(repl);
            SegmentReplacementManifest.CommitReplacement(_tempDir, repl, new long[] { src });
            File.Delete(SegPath(src)); // 旧活段被并入后删除
            liveId = repl;
        }

        var records = SegmentReplacementManifest.LoadForRoot(_tempDir).Records;
        // 每轮上一条 committed 的 source+replacement 都在下一轮清理后被修剪；稳态仅保留最近一条。
        Assert.True(records.Count <= 2,
            $"manifest 记录数应被修剪保持有界，实际 {records.Count}。");
    }

    [Fact]
    public void Open_DeletesSuppressedOrphanSegmentFiles()
    {
        // S12：committed replacement 的源段文件在上次删除失败后残留（崩溃 / 文件锁），
        // 且被 manifest 抑制不再加载。SegmentManager.Open 应在启动时清理这些孤儿文件。
        WriteSegment(1);
        WriteSegment(2);
        WriteSegment(100); // replacement 可读

        SegmentReplacementManifest.CommitReplacement(_tempDir, 100, new long[] { 1, 2 });

        // 源段文件仍在盘上（模拟删除失败残留的孤儿）。
        Assert.True(File.Exists(SegPath(1)));
        Assert.True(File.Exists(SegPath(2)));

        using var mgr = SegmentManager.Open(_tempDir, SegmentReaderOptions.Default);

        // 只加载 replacement；被抑制的源段孤儿文件应被清理删除。
        Assert.Equal(1, mgr.SegmentCount);
        Assert.False(File.Exists(SegPath(1)), "被抑制的孤儿源段 1 应被清理");
        Assert.False(File.Exists(SegPath(2)), "被抑制的孤儿源段 2 应被清理");
        Assert.True(File.Exists(SegPath(100)), "活的 replacement 段 100 应保留");
    }

    [Fact]
    public void Open_DeletesUnreadableSuppressedReplacementFile()
    {
        // committed 但 replacement 段损坏/不完整（被抑制）：其残留文件也应被清理，源段保留可加载。
        WriteSegment(1);
        WriteSegment(2);
        // 写一个"损坏"的 replacement 文件（非法内容，SegmentReader.Open 会失败）。
        File.WriteAllBytes(SegPath(100), new byte[] { 0, 1, 2, 3, 4 });

        SegmentReplacementManifest.CommitReplacement(_tempDir, 100, new long[] { 1, 2 });

        using var mgr = SegmentManager.Open(_tempDir, SegmentReaderOptions.Default);

        // replacement 不可读 → 抑制并清理；源段保留加载（数据不丢）。
        Assert.False(File.Exists(SegPath(100)), "不可读的被抑制 replacement 应被清理");
        Assert.Equal(2, mgr.SegmentCount);
    }
}
