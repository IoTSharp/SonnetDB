using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Query;

/// <summary>
/// <see cref="QueryEngine"/> 的 SegmentReader 映射缓存测试。
/// </summary>
public sealed class QueryEngineReaderMapCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SegmentWriter _writer = new(new SegmentWriterOptions { FsyncOnCommit = false });

    public QueryEngineReaderMapCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, TsdbPaths.SegmentsDirName));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Execute_AfterAddSegment_UsesNewSnapshotReaderMap()
    {
        using var manager = SegmentManager.Open(_tempDir);
        var engine = CreateQueryEngine(manager);

        Assert.Empty(QueryPoints(engine));

        string path = WriteSegment(1L, valueOffset: 0d);
        manager.AddSegment(path);

        var points = QueryPoints(engine);

        Assert.Equal(10, points.Length);
        Assert.Equal(0d, points[0].Value.AsDouble(), precision: 10);
    }

    [Fact]
    public void Execute_AfterSwapSegments_UsesNewSnapshotReaderMap()
    {
        WriteSegment(1L, valueOffset: 0d);
        using var manager = SegmentManager.Open(_tempDir);
        var engine = CreateQueryEngine(manager);

        Assert.Equal(0d, QueryPoints(engine)[0].Value.AsDouble(), precision: 10);

        string newPath = WriteSegment(2L, valueOffset: 100d);
        manager.SwapSegments([1L], newPath);

        var points = QueryPoints(engine);

        Assert.Equal(10, points.Length);
        Assert.Equal(100d, points[0].Value.AsDouble(), precision: 10);
    }

    [Fact]
    public void Execute_AfterSegmentManagerDispose_DoesNotUseCachedReaderMap()
    {
        WriteSegment(1L, valueOffset: 0d);
        var manager = SegmentManager.Open(_tempDir);
        var engine = CreateQueryEngine(manager);

        Assert.Equal(10, QueryPoints(engine).Length);

        manager.Dispose();

        Assert.Throws<ObjectDisposedException>(() => QueryPoints(engine));
    }

    [Fact]
    public async Task Execute_ConcurrentQueryAndCompactionSwap_NoStaleReaderExceptions()
    {
        const int readerCount = 24;
        const int swapCount = 40;
        WriteSegment(1L, valueOffset: 0d);
        using var manager = SegmentManager.Open(_tempDir);
        var engine = CreateQueryEngine(manager);
        for (int round = 0; round < swapCount; round++)
            WriteSegment(2L + round, valueOffset: (round + 1) * 100d);

        using var cancellation = new CancellationTokenSource();
        using var phase = new Barrier(readerCount + 1);
        var completedQueries = new int[readerCount];
        int completedSwaps = 0;

        void WaitForPhase()
        {
            Assert.True(phase.SignalAndWait(TimeSpan.FromSeconds(10), cancellation.Token),
                "All queries and the compaction writer must reach the publication barrier.");
        }

        static void AssertPoint(DataPoint point, int index, double valueOffset)
        {
            Assert.Equal(1000L + index, point.Timestamp);
            Assert.Equal(FieldValue.FromDouble(valueOffset + index), point.Value);
        }

        // 专用任务不会占满需要调度停止回调的线程池；固定轮次保证所有查询
        // 都实际跨越 compaction，不能因定时器早于 writer 运行而空跑通过。
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

        manager.BeforeSegmentIndexBuildTestHook = (_, _) =>
        {
            WaitForPhase();
            WaitForPhase();
        };

        var queryTasks = Enumerable.Range(0, readerCount).Select(readerId => StartWorker(() =>
        {
            for (int round = 0; round < swapCount; round++)
            {
                WaitForPhase();
                // 暂停在第一个结果后，保留 QueryEngine 自己持有的旧快照租约。
                using var pendingQuery = engine.Execute(
                    new PointQuery(1UL, "v", new TimeRange(1000L, 1009L))).GetEnumerator();
                Assert.True(pendingQuery.MoveNext());
                AssertPoint(pendingQuery.Current, 0, round * 100d);
                WaitForPhase();

                WaitForPhase();
                // 发布后的查询必须更新共享 reader map；仍在途的旧查询则继续
                // 返回原快照的全部值，不能访问提前关闭或错配的新 reader。
                var currentPoints = QueryPoints(engine);
                Assert.Equal(10, currentPoints.Length);
                for (int point = 0; point < currentPoints.Length; point++)
                    AssertPoint(currentPoints[point], point, (round + 1) * 100d);

                for (int point = 1; point < 10; point++)
                {
                    Assert.True(pendingQuery.MoveNext());
                    AssertPoint(pendingQuery.Current, point, round * 100d);
                }
                Assert.False(pendingQuery.MoveNext());
                completedQueries[readerId] += 2;
            }
        })).ToArray();

        var swapTask = StartWorker(() =>
        {
            for (int round = 0; round < swapCount; round++)
            {
                manager.SwapSegments([1L + round], SegmentPath(2L + round));
                completedSwaps++;
                WaitForPhase();
            }
        });

        Task allWorkers = Task.WhenAll([.. queryTasks, swapTask]);
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
            manager.BeforeSegmentIndexBuildTestHook = null;
        }

        Assert.Equal(swapCount, completedSwaps);
        Assert.All(completedQueries, count => Assert.Equal(swapCount * 2, count));
        Assert.Single(manager.Readers);
    }

    private string SegmentPath(long segmentId) => TsdbPaths.SegmentPath(_tempDir, segmentId);

    private string WriteSegment(long segmentId, double valueOffset)
    {
        var memTable = new MemTable();
        for (int i = 0; i < 10; i++)
        {
            memTable.Append(
                1UL,
                1000L + i,
                "v",
                FieldValue.FromDouble(valueOffset + i),
                i + 1L);
        }

        string path = SegmentPath(segmentId);
        _writer.WriteFrom(memTable, segmentId, path);
        return path;
    }

    private static QueryEngine CreateQueryEngine(SegmentManager manager)
        => new(new MemTable(), manager, new SeriesCatalog());

    private static DataPoint[] QueryPoints(QueryEngine engine)
        => engine.Execute(new PointQuery(1UL, "v", new TimeRange(1000L, 1009L))).ToArray();
}
