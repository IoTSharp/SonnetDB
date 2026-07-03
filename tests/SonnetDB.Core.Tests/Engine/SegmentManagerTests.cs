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
        WriteSegment(1L, 0x1UL, "f", 1000L, 2000L);

        using var mgr = SegmentManager.Open(_tempDir);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // 50 个查询线程
        var queryTasks = Enumerable.Range(0, 50).Select(i => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var idx = mgr.Index;
                    var candidates = idx.LookupCandidates(0x1UL, "f", 1000L, 2000L);
                    int count = idx.SegmentCount;
                    GC.KeepAlive(candidates);
                    GC.KeepAlive(count);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        })).ToList();

        // 1 个 AddSegment 线程
        long nextSegId = 2L;
        var addTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    long segId = Interlocked.Increment(ref nextSegId);
                    string path = WriteSegment(segId, (ulong)segId, "f",
                        segId * 1000L, segId * 1000L + 500L);
                    mgr.AddSegment(path);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    exceptions.Add(ex);
                }
            }
        });

        await Task.WhenAll(queryTasks.Append(addTask));

        Assert.Empty(exceptions);
        Assert.True(mgr.SegmentCount >= 1);
    }
}
