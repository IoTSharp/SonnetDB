using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>
/// <see cref="SegmentManager.DropSegments"/> 单元测试：验证原子移除、Dispose、索引更新及并发安全。
/// </summary>
public sealed class SegmentManagerDropTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SegmentWriter _writer = new(new SegmentWriterOptions { FsyncOnCommit = false });

    public SegmentManagerDropTests()
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

    private string WriteSegment(long segId, ulong seriesId = 1UL, int count = 10)
    {
        var mt = new MemTable();
        for (int i = 0; i < count; i++)
            mt.Append(seriesId, 1000L + i, "v", FieldValue.FromDouble(i), i + 1L);
        string path = SegPath(segId);
        _writer.WriteFrom(mt, segId, path);
        return path;
    }

    // ── DropSegments 后 SegmentCount 减少 ────────────────────────────────────

    [Fact]
    public void DropSegments_Multiple_SegmentCountDecreases()
    {
        WriteSegment(1);
        WriteSegment(2);
        WriteSegment(3);

        using var mgr = SegmentManager.Open(_tempDir);
        Assert.Equal(3, mgr.SegmentCount);

        var dropped = mgr.DropSegments([1L, 2L]);

        Assert.Equal(1, mgr.SegmentCount);
        Assert.Equal(2, dropped.Count);
    }

    // ── DropSegments 返回被移除的 reader（已 Disposed）────────────────────────

    [Fact]
    public void DropSegments_ReturnedReadersAreDisposed()
    {
        WriteSegment(1);
        WriteSegment(2);

        using var mgr = SegmentManager.Open(_tempDir);
        var dropped = mgr.DropSegments([1L, 2L]);

        Assert.Equal(2, dropped.Count);
        foreach (var reader in dropped)
            Assert.Throws<ObjectDisposedException>(() => reader.DecodeBlock(reader.Blocks[0]));
    }

    // ── DropSegments 后索引不含被移除段 ──────────────────────────────────────

    [Fact]
    public void DropSegments_RemovedSegmentsNotInIndex()
    {
        WriteSegment(1, seriesId: 0xAAUL);
        WriteSegment(2, seriesId: 0xBBUL);
        WriteSegment(3, seriesId: 0xCCUL);

        using var mgr = SegmentManager.Open(_tempDir);

        mgr.DropSegments([1L, 2L]);

        var index = mgr.Index;
        Assert.Equal(1, index.SegmentCount);

        // seg1 和 seg2 的 series 应无候选
        Assert.Empty(index.LookupCandidates(0xAAUL, "v", 0, long.MaxValue));
        Assert.Empty(index.LookupCandidates(0xBBUL, "v", 0, long.MaxValue));
        // seg3 仍在索引中
        Assert.NotEmpty(index.LookupCandidates(0xCCUL, "v", 0, long.MaxValue));
    }

    // ── DropSegments 空列表 → 不变 ───────────────────────────────────────────

    [Fact]
    public void DropSegments_EmptyList_NoChange()
    {
        WriteSegment(1);
        WriteSegment(2);

        using var mgr = SegmentManager.Open(_tempDir);
        var dropped = mgr.DropSegments([]);

        Assert.Equal(2, mgr.SegmentCount);
        Assert.Empty(dropped);
    }

    // ── DropSegments 不存在的 ID → 忽略 ─────────────────────────────────────

    [Fact]
    public void DropSegments_NonExistentId_Ignored()
    {
        WriteSegment(1);

        using var mgr = SegmentManager.Open(_tempDir);
        var dropped = mgr.DropSegments([99L, 100L]);

        Assert.Equal(1, mgr.SegmentCount);
        Assert.Empty(dropped);
    }

    // ── DropSegments 在 Dispose 后抛 ────────────────────────────────────────

    [Fact]
    public void DropSegments_AfterDispose_Throws()
    {
        WriteSegment(1);
        var mgr = SegmentManager.Open(_tempDir);
        mgr.Dispose();

        Assert.Throws<ObjectDisposedException>(() => mgr.DropSegments([1L]));
    }

    // ── 并发：50 个读者持有旧快照，跨越 40 轮实际删除和补充发布 ───────────

    [Fact]
    public async Task DropSegments_ConcurrentReadsAndDrop_NoExceptions()
    {
        const int readerCount = 50;
        const int dropCount = 40;
        for (int i = 1; i <= 10; i++)
            WriteSegment(i, seriesId: (ulong)i);

        using var mgr = SegmentManager.Open(_tempDir);
        for (int round = 0; round < dropCount; round++)
            WriteSegment(20L + round, seriesId: (ulong)(20 + round));

        using var cancellation = new CancellationTokenSource();
        using var phase = new Barrier(readerCount + 1);
        var completedReads = new int[readerCount];
        var droppedReaders = new List<SegmentReader>();
        int completedAdds = 0;

        void WaitForPhase()
        {
            Assert.True(phase.SignalAndWait(TimeSpan.FromSeconds(10), cancellation.Token),
                "All readers and the drop writer must reach the publication barrier.");
        }

        // 专用线程不占用取消计时器所依赖的线程池；固定轮次保证读写实际发生。
        Task StartWorker(Action work) => Task.Factory.StartNew(() =>
        {
            try
            {
                work();
            }
            catch
            {
                cancellation.Cancel();
                throw;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var readTasks = Enumerable.Range(0, readerCount).Select(readerId => StartWorker(() =>
        {
            for (int round = 0; round < dropCount; round++)
            {
                long removeId = round < 10 ? round + 1L : round + 10L;
                long addedId = 20L + round;
                using var lease = mgr.AcquireSnapshot();
                Assert.Equal(10, lease.Readers.Count);
                Assert.Equal(10, lease.Snapshot.Index.SegmentCount);
                var removedReader = Assert.Single(lease.Readers, reader => reader.Header.SegmentId == removeId);
                Assert.Equal(removeId, Assert.Single(lease.Snapshot.Index.LookupCandidates(
                    (ulong)removeId, "v", 1000L, 1010L)).SegmentId);
                WaitForPhase();

                // 与 Drop 并发读取将被移除的段；租约必须跨越删除发布保持有效。
                Assert.Equal(10, removedReader.DecodeBlock(Assert.Single(removedReader.Blocks)).Length);
                WaitForPhase();
                using (var afterDrop = mgr.AcquireSnapshot())
                {
                    Assert.Equal(9, afterDrop.Readers.Count);
                    Assert.Equal(9, afterDrop.Snapshot.Index.SegmentCount);
                    Assert.DoesNotContain(afterDrop.Readers, reader => reader.Header.SegmentId == removeId);
                    Assert.Empty(afterDrop.Snapshot.Index.LookupCandidates((ulong)removeId, "v", 1000L, 1010L));
                }

                // 删除后仍通过旧快照的索引找到并完整解码每个段。
                foreach (var reader in lease.Readers)
                {
                    long segId = reader.Header.SegmentId;
                    Assert.Equal(segId, Assert.Single(lease.Snapshot.Index.LookupCandidates(
                        (ulong)segId, "v", 1000L, 1010L)).SegmentId);
                    var points = reader.DecodeBlock(Assert.Single(reader.Blocks));
                    Assert.Equal(10, points.Length);
                    for (int point = 0; point < points.Length; point++)
                    {
                        Assert.Equal(1000L + point, points[point].Timestamp);
                        Assert.Equal(FieldValue.FromDouble(point), points[point].Value);
                    }
                }
                WaitForPhase();
                WaitForPhase();
                using var afterAdd = mgr.AcquireSnapshot();
                Assert.Equal(10, afterAdd.Readers.Count);
                Assert.Equal(10, afterAdd.Snapshot.Index.SegmentCount);
                Assert.Contains(afterAdd.Readers, reader => reader.Header.SegmentId == addedId);
                Assert.Equal(addedId, Assert.Single(afterAdd.Snapshot.Index.LookupCandidates(
                    (ulong)addedId, "v", 1000L, 1010L)).SegmentId);
                Assert.DoesNotContain(lease.Readers, reader => reader.Header.SegmentId == addedId);
                completedReads[readerId]++;
            }
        })).ToArray();

        var dropTask = StartWorker(() =>
        {
            for (int round = 0; round < dropCount; round++)
            {
                long removeId = round < 10 ? round + 1L : round + 10L;
                WaitForPhase();
                var dropped = Assert.Single(mgr.DropSegments([removeId]));
                Assert.Equal(removeId, dropped.Header.SegmentId);
                droppedReaders.Add(dropped);
                WaitForPhase();
                WaitForPhase();
                mgr.AddSegment(SegPath(20L + round));
                completedAdds++;
                WaitForPhase();
            }
        });

        Task allWorkers = Task.WhenAll([.. readTasks, dropTask]);
        try
        {
            await allWorkers.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await allWorkers.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception) when (allWorkers.IsCompleted)
            {
                // 上方 await 保留原始失败；这里只等待所有专用线程退出后再释放同步对象。
            }
        }

        Assert.Equal(dropCount, droppedReaders.Count);
        Assert.Equal(dropCount, completedAdds);
        Assert.All(completedReads, count => Assert.Equal(dropCount, count));
        Assert.Equal(10, mgr.SegmentCount);
        foreach (var dropped in droppedReaders)
            Assert.Throws<ObjectDisposedException>(() => dropped.DecodeBlock(dropped.Blocks[0]));
    }
}
