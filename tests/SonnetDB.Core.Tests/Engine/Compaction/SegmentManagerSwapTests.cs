using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Engine.Compaction;

/// <summary>
/// <see cref="SegmentManager.SwapSegments"/> 单元测试：验证原子移除 + 加入段、Dispose 旧 reader、并发安全。
/// </summary>
public sealed class SegmentManagerSwapTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SegmentWriter _writer = new(new SegmentWriterOptions { FsyncOnCommit = false });

    public SegmentManagerSwapTests()
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

    // ── SwapSegments 后 SegmentCount 正确 ────────────────────────────────────

    [Fact]
    public void SwapSegments_RemoveTwoAddOne_SegmentCountOne()
    {
        // 写 seg1, seg2
        WriteSegment(1);
        WriteSegment(2);

        using var mgr = SegmentManager.Open(_tempDir);
        Assert.Equal(2, mgr.SegmentCount);

        // 写 seg3（合并结果）
        WriteSegment(3);

        var newReader = mgr.SwapSegments(new long[] { 1, 2 }, SegPath(3));

        Assert.Equal(1, mgr.SegmentCount);
        Assert.Single(mgr.Readers);
        Assert.Equal(3L, newReader.Header.SegmentId);
    }

    // ── SwapSegments 后旧 reader 已 Dispose ──────────────────────────────────

    [Fact]
    public void SwapSegments_OldReadersAreDisposed()
    {
        WriteSegment(1);
        WriteSegment(2);

        using var mgr = SegmentManager.Open(_tempDir);
        var oldReaders = mgr.Readers.ToList();

        WriteSegment(3);
        mgr.SwapSegments(new long[] { 1, 2 }, SegPath(3));

        // 访问 Disposed 的 reader 会抛异常（DecodeBlock 时）
        foreach (var old in oldReaders)
        {
            // SegmentReader.Dispose() 将 _bytes = null，调用 ReadBlock 时会抛 ObjectDisposedException
            Assert.Throws<ObjectDisposedException>(() => old.DecodeBlock(old.Blocks[0]));
        }
    }

    // ── SwapSegments 后索引更新 ───────────────────────────────────────────────

    [Fact]
    public void SwapSegments_IndexIsRebuildWithNewSegment()
    {
        WriteSegment(1, seriesId: 0xA1UL);
        WriteSegment(2, seriesId: 0xA2UL);

        using var mgr = SegmentManager.Open(_tempDir);

        // 新段包含两个 series
        var mt = new MemTable();
        for (int i = 0; i < 5; i++)
        {
            mt.Append(0xA1UL, 1000L + i, "v", FieldValue.FromDouble(i), i + 1L);
            mt.Append(0xA2UL, 1000L + i, "v", FieldValue.FromDouble(i), i + 100L);
        }
        _writer.WriteFrom(mt, 3L, SegPath(3));

        mgr.SwapSegments(new long[] { 1, 2 }, SegPath(3));

        var index = mgr.Index;
        Assert.Equal(1, index.SegmentCount);

        var refs = index.LookupCandidates(0xA1UL, "v", 0, long.MaxValue);
        Assert.NotEmpty(refs);
    }

    // ── 并发：50 查询线程跨越 40 次实际发布，验证快照和 reader 租约 ─────────

    [Fact]
    public async Task SwapSegments_ConcurrentReadsAndSwap_NoExceptions()
    {
        const int readerCount = 50;
        const int swapCount = 40;
        WriteSegment(1, seriesId: 1UL);
        WriteSegment(2, seriesId: 1UL);
        WriteSegment(3, seriesId: 1UL);

        using var mgr = SegmentManager.Open(_tempDir);
        for (int round = 0; round < swapCount; round++)
            WriteSegment(10L + round, seriesId: 1UL);

        using var cancellation = new CancellationTokenSource();
        using var phase = new Barrier(readerCount + 1);
        var completedReads = new int[readerCount];
        int completedSwaps = 0;

        void WaitForPhase()
        {
            Assert.True(phase.SignalAndWait(TimeSpan.FromSeconds(10), cancellation.Token),
                "All readers and the swap writer must reach the publication barrier.");
        }

        // 忙循环不能占满线程池后再依赖同一线程池上的取消定时器停止。
        // 专用线程与固定轮次既避免调度饥饿，也确保每个读者实际跨越每次发布。
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

        // 此钩子位于 writer 锁内、候选集合已变更而新快照尚未发布的位置。
        // 此时所有读者都必须能取得完整旧快照，并持有租约直到发布结束。
        mgr.BeforeSegmentIndexBuildTestHook = (_, _) =>
        {
            WaitForPhase();
            WaitForPhase();
        };

        var readTasks = Enumerable.Range(0, readerCount).Select(readerId => StartWorker(() =>
        {
            for (int round = 0; round < swapCount; round++)
            {
                WaitForPhase();
                using var lease = mgr.AcquireSnapshot();
                Assert.Equal(3, mgr.Index.SegmentCount);
                Assert.Equal(3, mgr.Readers.Count);
                Assert.Equal(3, lease.Snapshot.Index.SegmentCount);
                Assert.Equal(3, lease.Readers.Count);
                Assert.DoesNotContain(lease.Readers, reader => reader.Header.SegmentId == 10L + round);
                WaitForPhase();

                WaitForPhase();
                Assert.Contains(mgr.Readers, reader => reader.Header.SegmentId == 10L + round);
                foreach (var reader in lease.Readers)
                {
                    var points = reader.DecodeBlock(Assert.Single(reader.Blocks));
                    Assert.Equal(10, points.Length);
                    for (int point = 0; point < points.Length; point++)
                    {
                        Assert.Equal(1000L + point, points[point].Timestamp);
                        Assert.Equal(FieldValue.FromDouble(point), points[point].Value);
                    }
                }
                completedReads[readerId]++;
            }
        })).ToArray();

        var swapTask = StartWorker(() =>
        {
            for (int round = 0; round < swapCount; round++)
            {
                long removeId = mgr.Readers[0].Header.SegmentId;
                mgr.SwapSegments([removeId], SegPath(10L + round));
                completedSwaps++;
                WaitForPhase();
            }
        });

        Task allWorkers = Task.WhenAll([.. readTasks, swapTask]);
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
            mgr.BeforeSegmentIndexBuildTestHook = null;
        }

        Assert.Equal(swapCount, completedSwaps);
        Assert.All(completedReads, count => Assert.Equal(swapCount, count));
        Assert.Equal(3, mgr.SegmentCount);
    }
}
