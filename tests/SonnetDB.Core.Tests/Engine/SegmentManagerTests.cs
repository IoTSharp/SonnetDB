using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>
/// <see cref="SegmentManager"/> 单元测试：验证段集合管理、索引快照原子替换与并发安全。
/// </summary>
public sealed class SegmentManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SegmentWriter _writer = new(new SegmentWriterOptions { FsyncOnCommit = false });

    public SegmentManagerTests()
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

    private string WriteSegment(long segId, ulong seriesId = 0xDEADUL, string field = "v",
        long minTs = 1000L, long maxTs = 2000L)
    {
        var mt = new MemTable();
        long lsn = 1;
        for (long ts = minTs; ts < maxTs; ts += 100)
            mt.Append(seriesId, ts, field, FieldValue.FromDouble((double)ts), lsn++);
        mt.Append(seriesId, maxTs, field, FieldValue.FromDouble((double)maxTs), lsn++);

        string path = SegPath(segId);
        _writer.WriteFrom(mt, segId, path);
        return path;
    }

    // ── 空目录 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Open_EmptyDirectory_SegmentCountZero()
    {
        using var mgr = SegmentManager.Open(_tempDir);
        Assert.Equal(0, mgr.SegmentCount);
        Assert.Empty(mgr.Readers);
        Assert.Equal(0, mgr.Index.SegmentCount);
    }

    [Fact]
    public void Open_TwoParameterOverload_RetainsPublicAbi()
    {
        var twoParameter = typeof(SegmentManager).GetMethod(
            nameof(SegmentManager.Open),
            [typeof(string), typeof(SegmentReaderOptions)]);
        var threeParameter = typeof(SegmentManager).GetMethod(
            nameof(SegmentManager.Open),
            [typeof(string), typeof(SegmentReaderOptions), typeof(string)]);

        Assert.NotNull(twoParameter);
        Assert.True(twoParameter.GetParameters()[1].HasDefaultValue);
        Assert.NotNull(threeParameter);
        Assert.False(threeParameter.GetParameters()[2].HasDefaultValue);
    }

    // ── 预写入 2 个段后 Open ─────────────────────────────────────────────────

    [Fact]
    public void Open_WithTwoSegments_SegmentCountTwo()
    {
        WriteSegment(1L, 0x1UL, "f", 1000L, 2000L);
        WriteSegment(2L, 0x2UL, "f", 3000L, 4000L);

        using var mgr = SegmentManager.Open(_tempDir);
        Assert.Equal(2, mgr.SegmentCount);
        Assert.Equal(2, mgr.Readers.Count);
    }

    [Fact]
    public void Open_WithTwoSegments_IndexContainsBothSeries()
    {
        WriteSegment(1L, 0x1UL, "f", 1000L, 2000L);
        WriteSegment(2L, 0x2UL, "f", 3000L, 4000L);

        using var mgr = SegmentManager.Open(_tempDir);
        var idx = mgr.Index;

        Assert.NotEmpty(idx.LookupCandidates(0x1UL, "f", 1000L, 2000L));
        Assert.NotEmpty(idx.LookupCandidates(0x2UL, "f", 3000L, 4000L));
    }

    // ── AddSegment ────────────────────────────────────────────────────────────

    [Fact]
    public void AddSegment_IndexSnapshotChanges()
    {
        using var mgr = SegmentManager.Open(_tempDir);
        var indexBefore = mgr.Index;

        string path = WriteSegment(1L);
        mgr.AddSegment(path);

        Assert.NotSame(indexBefore, mgr.Index);
        Assert.Equal(1, mgr.SegmentCount);
    }

    [Fact]
    public void AddSegment_NewSegmentIsQueryable()
    {
        using var mgr = SegmentManager.Open(_tempDir);

        string path = WriteSegment(1L, 0xABCUL, "usage", 5000L, 6000L);
        mgr.AddSegment(path);

        var candidates = mgr.Index.LookupCandidates(0xABCUL, "usage", 5000L, 6000L);
        Assert.NotEmpty(candidates);
    }

    [Fact]
    public void AddSegment_IndexBuildFailure_LeavesCandidateUnpublished()
    {
        WriteSegment(1L, 0x1UL, "f", 1000L, 2000L);
        using var mgr = SegmentManager.Open(_tempDir);
        var before = mgr.CurrentSnapshot;

        string failedPath = WriteSegment(2L, 0x2UL, "f", 3000L, 4000L);
        mgr.BeforeSegmentIndexBuildTestHook = static (_, segId) =>
        {
            if (segId == 2L)
                throw new InvalidOperationException("injected segment index failure");
        };

        try
        {
            Assert.Throws<InvalidOperationException>(() => mgr.AddSegment(failedPath));
        }
        finally
        {
            mgr.BeforeSegmentIndexBuildTestHook = null;
        }

        Assert.Same(before, mgr.CurrentSnapshot);
        Assert.Equal(new[] { 1L }, mgr.Readers.Select(static reader => reader.Header.SegmentId));

        string succeedingPath = WriteSegment(3L, 0x3UL, "f", 5000L, 6000L);
        mgr.AddSegment(succeedingPath);

        Assert.Equal(new[] { 1L, 3L }, mgr.Readers.Select(static reader => reader.Header.SegmentId));
        Assert.Equal(2, mgr.CachedIndexCount);
    }

    [Fact]
    public void SwapSegments_IndexBuildFailure_LeavesRemovedReadersPublished()
    {
        WriteSegment(1L, 0x1UL, "f", 1000L, 2000L);
        WriteSegment(2L, 0x2UL, "f", 3000L, 4000L);
        using var mgr = SegmentManager.Open(_tempDir);
        var before = mgr.CurrentSnapshot;

        string failedPath = WriteSegment(3L, 0x3UL, "f", 5000L, 6000L);
        mgr.BeforeSegmentIndexBuildTestHook = static (_, segId) =>
        {
            if (segId == 3L)
                throw new InvalidOperationException("injected segment index failure");
        };

        try
        {
            Assert.Throws<InvalidOperationException>(() => mgr.SwapSegments([1L, 2L], failedPath));
        }
        finally
        {
            mgr.BeforeSegmentIndexBuildTestHook = null;
        }

        Assert.Same(before, mgr.CurrentSnapshot);
        Assert.Equal(new[] { 1L, 2L }, mgr.Readers.Select(static reader => reader.Header.SegmentId));

        string succeedingPath = WriteSegment(4L, 0x4UL, "f", 7000L, 8000L);
        mgr.AddSegment(succeedingPath);

        Assert.Equal(new[] { 1L, 2L, 4L }, mgr.Readers.Select(static reader => reader.Header.SegmentId));
        Assert.Equal(3, mgr.CachedIndexCount);
    }

    // ── RemoveSegment ─────────────────────────────────────────────────────────

    [Fact]
    public void RemoveSegment_SegmentCountDecreases()
    {
        WriteSegment(1L, 0x1UL, "f", 1000L, 2000L);
        WriteSegment(2L, 0x2UL, "f", 3000L, 4000L);

        using var mgr = SegmentManager.Open(_tempDir);
        Assert.Equal(2, mgr.SegmentCount);

        bool removed = mgr.RemoveSegment(1L);
        Assert.True(removed);
        Assert.Equal(1, mgr.SegmentCount);
    }

    [Fact]
    public void RemoveSegment_RemovedSegmentNotQueryable()
    {
        WriteSegment(1L, 0xAAUL, "f", 1000L, 2000L);

        using var mgr = SegmentManager.Open(_tempDir);
        Assert.NotEmpty(mgr.Index.LookupCandidates(0xAAUL, "f", 1000L, 2000L));

        mgr.RemoveSegment(1L);
        Assert.Empty(mgr.Index.LookupCandidates(0xAAUL, "f", 1000L, 2000L));
    }

    [Fact]
    public void RemoveSegment_NonExistent_ReturnsFalse()
    {
        using var mgr = SegmentManager.Open(_tempDir);
        Assert.False(mgr.RemoveSegment(999L));
    }

    // ── 增量段索引缓存（C7）─────────────────────────────────────────────────

    [Fact]
    public void AddSegment_ReplacingSameId_RebuildsIndexFromNewReader()
    {
        // 同一 segId 先写 series 0xAA，再用不同内容（series 0xBB）覆盖重写并 AddSegment。
        // 增量索引缓存必须在替换时作废旧索引，否则会查到已不存在的 0xAA、查不到新的 0xBB。
        WriteSegment(1L, 0xAAUL, "f", 1000L, 2000L);
        using var mgr = SegmentManager.Open(_tempDir);
        Assert.NotEmpty(mgr.Index.LookupCandidates(0xAAUL, "f", 1000L, 2000L));

        // 覆盖重写 segId=1 的段文件为不同 series，然后 AddSegment 触发替换。
        var mt = new MemTable();
        mt.Append(0xBBUL, 1500L, "f", FieldValue.FromDouble(1.0), 1L);
        _writer.WriteFrom(mt, 1L, SegPath(1L));
        mgr.AddSegment(SegPath(1L));

        Assert.Empty(mgr.Index.LookupCandidates(0xAAUL, "f", 1000L, 2000L));
        Assert.NotEmpty(mgr.Index.LookupCandidates(0xBBUL, "f", 1000L, 2000L));
        Assert.Equal(1, mgr.SegmentCount);
    }

    [Fact]
    public void SwapSegments_RemovedSegmentsPrunedFromIndex_AddedIsQueryable()
    {
        WriteSegment(1L, 0x1UL, "f", 1000L, 2000L);
        WriteSegment(2L, 0x2UL, "f", 3000L, 4000L);
        using var mgr = SegmentManager.Open(_tempDir);

        // 合并 1、2 → 新段 3（series 0x3）。移除的段索引应从缓存修剪，未变段沿用缓存。
        var mt = new MemTable();
        mt.Append(0x3UL, 1500L, "f", FieldValue.FromDouble(1.0), 1L);
        _writer.WriteFrom(mt, 3L, SegPath(3L));
        mgr.SwapSegments([1L, 2L], SegPath(3L));

        Assert.Equal(1, mgr.SegmentCount);
        Assert.Empty(mgr.Index.LookupCandidates(0x1UL, "f", 1000L, 2000L));
        Assert.Empty(mgr.Index.LookupCandidates(0x2UL, "f", 3000L, 4000L));
        Assert.NotEmpty(mgr.Index.LookupCandidates(0x3UL, "f", 1000L, 2000L));
    }

    [Fact]
    public void AddManySegments_EachRemainsQueryable_IncrementalIndexConsistent()
    {
        using var mgr = SegmentManager.Open(_tempDir);

        const int count = 12;
        for (int i = 1; i <= count; i++)
        {
            var mt = new MemTable();
            mt.Append((ulong)i, 1000L + i, "f", FieldValue.FromDouble(i), 1L);
            _writer.WriteFrom(mt, i, SegPath(i));
            mgr.AddSegment(SegPath(i));
        }

        Assert.Equal(count, mgr.SegmentCount);
        for (int i = 1; i <= count; i++)
            Assert.NotEmpty(mgr.Index.LookupCandidates((ulong)i, "f", 1000L, 2000L));
    }

    [Fact]
    public void SwapSegments_OneForOne_PrunesRemovedIndexCache()
    {
        WriteSegment(1L, 0x1UL, "f", 1000L, 2000L);
        using var mgr = SegmentManager.Open(_tempDir);

        long currentId = 1L;
        for (long nextId = 2L; nextId <= 20L; nextId++)
        {
            WriteSegment(nextId, (ulong)nextId, "f", nextId * 1000L, nextId * 1000L + 500L);
            mgr.SwapSegments([currentId], SegPath(nextId));
            currentId = nextId;

            Assert.Equal(1, mgr.SegmentCount);
            Assert.Equal(mgr.SegmentCount, mgr.CachedIndexCount);
        }
    }

    [Fact]
    public void DropSegments_WithoutMatches_DoesNotPublishSnapshot()
    {
        WriteSegment(1L);
        using var mgr = SegmentManager.Open(_tempDir);
        var before = mgr.CurrentSnapshot;

        var dropped = mgr.DropSegments([99L]);

        Assert.Empty(dropped);
        Assert.Same(before, mgr.CurrentSnapshot);
    }

    [Fact]
    public void InitializeActiveMemTable_ReusesPublishedSegmentState()
    {
        WriteSegment(1L);
        using var mgr = SegmentManager.Open(_tempDir);
        var before = mgr.CurrentSnapshot;

        mgr.InitializeActiveMemTable(new MemTable());

        var after = mgr.CurrentSnapshot;
        Assert.NotSame(before, after);
        Assert.Same(before.Index, after.Index);
        Assert.Same(before.ReaderStates, after.ReaderStates);
        Assert.Same(before.Readers, after.Readers);
    }

    [Fact]
    public void SealActiveAndSwap_PreviousSnapshotRejectsLateAcquire()
    {
        WriteSegment(1L);
        using var mgr = SegmentManager.Open(_tempDir);
        mgr.InitializeActiveMemTable(new MemTable());
        var before = mgr.CurrentSnapshot;

        mgr.SealActiveAndSwap(new MemTable());

        bool acquired = before.TryAcquire();
        if (acquired)
            before.Release();

        Assert.False(acquired);
    }

    [Fact]
    public void PublishSegmentAndReleaseSealed_IndexBuildFailure_LeavesSealedTablePublished()
    {
        using var mgr = SegmentManager.Open(_tempDir);
        mgr.InitializeActiveMemTable(new MemTable());
        MemTable sealedTable = Assert.IsType<MemTable>(mgr.SealActiveAndSwap(new MemTable()));
        var before = mgr.CurrentSnapshot;

        string failedPath = WriteSegment(2L, 0x2UL, "f", 3000L, 4000L);
        mgr.BeforeSegmentIndexBuildTestHook = static (_, segId) =>
        {
            if (segId == 2L)
                throw new InvalidOperationException("injected segment index failure");
        };

        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                mgr.PublishSegmentAndReleaseSealed(failedPath, sealedTable));
        }
        finally
        {
            mgr.BeforeSegmentIndexBuildTestHook = null;
        }

        Assert.Same(before, mgr.CurrentSnapshot);
        Assert.Equal(0, mgr.SegmentCount);
        Assert.Equal(1, mgr.SealingCount);

        string succeedingPath = WriteSegment(3L, 0x3UL, "f", 5000L, 6000L);
        mgr.PublishSegmentAndReleaseSealed(succeedingPath, sealedTable);

        Assert.Equal(new[] { 3L }, mgr.Readers.Select(static reader => reader.Header.SegmentId));
        Assert.Equal(0, mgr.SealingCount);
        Assert.Equal(1, mgr.CachedIndexCount);
    }

    [Fact]
    public void PublishSegmentAndReleaseSealed_UnknownSealedTable_RejectsWithoutPublishingSegment()
    {
        using var mgr = SegmentManager.Open(_tempDir);
        mgr.InitializeActiveMemTable(new MemTable());
        MemTable sealedTable = Assert.IsType<MemTable>(mgr.SealActiveAndSwap(new MemTable()));
        var before = mgr.CurrentSnapshot;
        string path = WriteSegment(2L, 0x2UL, "f", 3000L, 4000L);

        var unknownSealedTable = new MemTable();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            mgr.PublishSegmentAndReleaseSealed(path, unknownSealedTable));

        Assert.Contains("sealing MemTable", error.Message, StringComparison.Ordinal);
        Assert.Same(before, mgr.CurrentSnapshot);
        Assert.Empty(mgr.Readers);
        Assert.Equal(1, mgr.SealingCount);

        mgr.PublishSegmentAndReleaseSealed(path, sealedTable);
        Assert.Equal(new[] { 2L }, mgr.Readers.Select(static reader => reader.Header.SegmentId));
        Assert.Equal(0, mgr.SealingCount);
    }

    // ── Dispose ──────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_Then_AddSegment_ThrowsObjectDisposedException()
    {
        var mgr = SegmentManager.Open(_tempDir);
        mgr.Dispose();

        string path = WriteSegment(1L);
        Assert.Throws<ObjectDisposedException>(() => mgr.AddSegment(path));
    }

    [Fact]
    public void Dispose_Then_RemoveSegment_ThrowsObjectDisposedException()
    {
        WriteSegment(1L);
        var mgr = SegmentManager.Open(_tempDir);
        mgr.Dispose();

        Assert.Throws<ObjectDisposedException>(() => mgr.RemoveSegment(1L));
    }

    [Fact]
    public void Dispose_ReadersAreDisposed()
    {
        WriteSegment(1L, 0x1UL, "f", 1000L, 2000L);

        SegmentReader capturedReader;
        using (var mgr = SegmentManager.Open(_tempDir))
        {
            capturedReader = mgr.Readers[0];
        }

        // After Dispose, reading block data from the reader should throw ObjectDisposedException
        Assert.Throws<ObjectDisposedException>(() =>
            capturedReader.ReadBlock(capturedReader.Blocks[0]));
    }

    // ── 并发安全 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_QueryAndAddSegment_NoExceptionsAndIndexConsistent()
    {
        const int readerCount = 50;
        const int addCount = 40;
        WriteSegment(1L, 0x1UL, "f", 1000L, 2000L);

        using var mgr = SegmentManager.Open(_tempDir);
        for (long segId = 2; segId <= addCount + 1; segId++)
            WriteSegment(segId, (ulong)segId, "f", segId * 1000L, segId * 1000L + 500L);

        using var cancellation = new CancellationTokenSource();
        using var phase = new Barrier(readerCount + 1);
        var completedReads = new int[readerCount];
        int completedAdds = 0;

        void WaitForPhase()
        {
            Assert.True(phase.SignalAndWait(TimeSpan.FromSeconds(10), cancellation.Token),
                "All readers and the add writer must reach the publication barrier.");
        }

        // 不能让忙循环占满线程池，再依赖相同线程池上的写者和取消计时器推进测试。
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

        // writer 锁内已加入候选 reader，但新索引尚未发布；读者仍须取得完整旧快照。
        mgr.BeforeSegmentIndexBuildTestHook = (_, _) =>
        {
            WaitForPhase();
            WaitForPhase();
        };

        var queryTasks = Enumerable.Range(0, readerCount).Select(readerId => StartWorker(() =>
        {
            for (int round = 0; round < addCount; round++)
            {
                long addedId = round + 2L;
                WaitForPhase();
                using var lease = mgr.AcquireSnapshot();
                Assert.Equal(round + 1, mgr.Index.SegmentCount);
                Assert.Equal(round + 1, mgr.Readers.Count);
                Assert.Equal(round + 1, lease.Readers.Count);
                Assert.Equal(round + 1, lease.Snapshot.Index.SegmentCount);
                Assert.Empty(lease.Snapshot.Index.LookupCandidates((ulong)addedId, "f", 0, long.MaxValue));
                foreach (var reader in lease.Readers)
                {
                    long segId = reader.Header.SegmentId;
                    Assert.Equal(segId, Assert.Single(lease.Snapshot.Index.LookupCandidates(
                        (ulong)segId, "f", 0, long.MaxValue)).SegmentId);
                }
                WaitForPhase();

                WaitForPhase();
                using var published = mgr.AcquireSnapshot();
                Assert.Equal(round + 2, published.Readers.Count);
                Assert.Equal(round + 2, published.Snapshot.Index.SegmentCount);
                Assert.Equal(addedId, Assert.Single(published.Snapshot.Index.LookupCandidates(
                    (ulong)addedId, "f", addedId * 1000L, addedId * 1000L + 501L)).SegmentId);
                var addedReader = Assert.Single(published.Readers, reader => reader.Header.SegmentId == addedId);
                var points = addedReader.DecodeBlock(Assert.Single(addedReader.Blocks));
                Assert.Equal(6, points.Length);
                for (int point = 0; point < points.Length; point++)
                {
                    long timestamp = addedId * 1000L + point * 100L;
                    Assert.Equal(timestamp, points[point].Timestamp);
                    Assert.Equal(FieldValue.FromDouble(timestamp), points[point].Value);
                }

                // 新发布不得改变仍被查询持有的旧索引与 reader 集合。
                Assert.Equal(round + 1, lease.Snapshot.Index.SegmentCount);
                Assert.Equal(round + 1, lease.Readers.Count);
                Assert.DoesNotContain(lease.Readers, reader => reader.Header.SegmentId == addedId);
                Assert.Empty(lease.Snapshot.Index.LookupCandidates((ulong)addedId, "f", 0, long.MaxValue));
                completedReads[readerId]++;
            }
        })).ToArray();

        var addTask = StartWorker(() =>
        {
            for (int round = 0; round < addCount; round++)
            {
                mgr.AddSegment(SegPath(round + 2L));
                completedAdds++;
                WaitForPhase();
            }
        });

        Task allWorkers = Task.WhenAll([.. queryTasks, addTask]);
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

        Assert.Equal(addCount, completedAdds);
        Assert.All(completedReads, count => Assert.Equal(addCount, count));
        Assert.Equal(addCount + 1, mgr.SegmentCount);
    }
}
