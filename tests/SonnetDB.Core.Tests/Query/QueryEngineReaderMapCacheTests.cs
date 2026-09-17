using System.Collections.Concurrent;
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
        WriteSegment(1L, valueOffset: 0d);
        using var manager = SegmentManager.Open(_tempDir);
        var engine = CreateQueryEngine(manager);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exceptions = new ConcurrentBag<Exception>();

        var queryTasks = Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var points = QueryPoints(engine);
                        if (points.Length != 10)
                        {
                            exceptions.Add(new InvalidOperationException(
                                $"Expected 10 points, got {points.Length}."));
                            continue;
                        }

                        Assert.Equal(1000L, points[0].Timestamp);
                        Assert.Equal(1009L, points[^1].Timestamp);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        exceptions.Add(ex);
                    }
                }
            }))
            .ToArray();

        long nextSegmentId = 1L;
        var swapTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    long segmentId = Interlocked.Increment(ref nextSegmentId);
                    string path = WriteSegment(segmentId, valueOffset: segmentId * 100d);
                    var current = manager.Readers;
                    if (current.Count == 0)
                        continue;

                    long removeId = current[0].Header.SegmentId;
                    manager.SwapSegments([removeId], path);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    exceptions.Add(ex);
                }
            }
        });

        await Task.WhenAll([.. queryTasks, swapTask]);

        Assert.Empty(exceptions);
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
