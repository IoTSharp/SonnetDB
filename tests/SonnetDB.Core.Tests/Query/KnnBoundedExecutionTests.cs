using System.Runtime.InteropServices;
using SonnetDB.Engine;
using SonnetDB.Query;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Segments;
using Xunit;
using Xunit.Abstractions;

namespace SonnetDB.Core.Tests.Query;

/// <summary>M42 measurement KNN 候选保留、确定排序、取消和工作集准入回归。</summary>
public sealed class KnnBoundedExecutionTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-m42-knn-" + Guid.NewGuid().ToString("N"));

    /// <summary>删除本测试拥有的数据库目录。</summary>
    public void Dispose()
    {
        string resolved = Path.GetFullPath(_root);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetDirectoryName(resolved), ignoreCase: true);
        if (Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }

    /// <summary>完整精确 oracle 与持续扫描的有界候选集必须逐项一致。</summary>
    [Fact]
    public void BoundedCandidates_WithManyTiedCandidates_MatchesExactOracle()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var candidates = new KnnExecutor.BoundedCandidateSet(7, "embedding", null, deadline.Token);
        var oracle = new List<(double Distance, long Timestamp, ulong SeriesId)>(4_096);
        for (int index = 4_095; index >= 0; index--)
        {
            double distance = index % 19;
            long timestamp = index % 101;
            ulong seriesId = (ulong)index;
            candidates.Add(distance, timestamp, seriesId);
            oracle.Add((distance, timestamp, seriesId));
            Assert.InRange(candidates.Count, 1, 7);
        }

        var expected = oracle.OrderBy(static value => value.Distance)
            .ThenBy(static value => value.Timestamp).ThenBy(static value => value.SeriesId).Take(7).ToArray();
        var actual = candidates.GetResults().Select(static value => (value.Distance, value.Timestamp, value.SeriesId)).ToArray();
        Assert.Equal(expected, actual);
    }

    /// <summary>并发扫描仍共用一个 K 堆；并列候选排序与调度无关。</summary>
    [Fact]
    public void BoundedCandidates_WithConcurrentWriters_RetainsGlobalTopK()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var candidates = new KnnExecutor.BoundedCandidateSet(5, "embedding", null, deadline.Token);
        Parallel.For(0, 4_096, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = deadline.Token },
            index => candidates.Add(index % 17, index, (ulong)index));

        Assert.Equal(5, candidates.Count);
        Assert.Equal(new long[] { 0, 17, 34, 51, 68 }, candidates.GetResults().Select(static value => value.Timestamp));
    }

    /// <summary>堆已填满后即使不断替换候选，也不会按扫描行数继续分配候选对象。</summary>
    [Fact]
    public void BoundedCandidates_WithFixedK_AllocationDoesNotGrowWithCandidateCount()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var candidates = new KnnExecutor.BoundedCandidateSet(8, "embedding", null, deadline.Token);
        for (int index = 0; index < 256; index++)
            candidates.Add(-index, index, 1);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 256; index < 100_256; index++)
            candidates.Add(-index, index, 1);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine("Fixed K=8; 100000 replacement candidates; additional managed allocation={0} bytes.", allocated);
        Assert.InRange(allocated, 0, 4_096);
        Assert.Equal(8, candidates.Count);
        Assert.Equal(100_255L, candidates.GetResults()[0].Timestamp);
    }

    /// <summary>新增候选须逐个预留查询预算，替换候选不重复扣减；失败后全部归还。</summary>
    [Fact]
    public void BoundedCandidates_WithExhaustedBudget_FailsAndReleasesReservation()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        using var scope = SqlQueryResources.EnterRoot(database, new SqlExecutionOptions
        {
            BlockingOperatorMemoryLimitBytes = 2 * KnnExecutor.BoundedCandidateSet.EstimatedBytesPerCandidate,
        });
        using (var candidates = new KnnExecutor.BoundedCandidateSet(3, "embedding", null,
            resources: SqlQueryResources.Current))
        {
            candidates.Add(1, 1000, 1);
            candidates.Add(2, 2000, 1);
            Assert.Throws<InvalidOperationException>(() => candidates.Add(3, 3000, 1));
            Assert.Equal(2 * KnnExecutor.BoundedCandidateSet.EstimatedBytesPerCandidate, database.SqlMemoryBudget.ReservedBytes);
        }
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>超大 K 不应触发按 K 预分配，预算只随真实保留候选增加。</summary>
    [Fact]
    public void BoundedCandidates_WithHugeK_DoesNotPreallocateRequestedCapacity()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        using var candidates = new KnnExecutor.BoundedCandidateSet(int.MaxValue, "embedding", null);
        candidates.Add(0, 1000, 1);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 8_192);
        Assert.Single(candidates.GetResults());
    }

    /// <summary>取消发生后候选与结果读取都必须拒绝继续执行。</summary>
    [Fact]
    public void BoundedCandidates_WithCancellationAfterAdmission_RejectsFurtherWork()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var candidates = new KnnExecutor.BoundedCandidateSet(3, "embedding", null, cancellation.Token);
        candidates.Add(1, 1000, 1);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => candidates.Add(0, 2000, 1));
        Assert.Throws<OperationCanceledException>(() => candidates.GetResults());
    }

    /// <summary>即使没有匹配序列，执行器也必须传播入口取消。</summary>
    [Fact]
    public void Execute_WithCancelledTokenAndNoSeries_ThrowsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => KnnExecutor.Execute([], MultiSegmentIndex.Empty, [], [],
            "embedding", new float[] { 1f, 0f }, 1, KnnMetric.Cosine, new TimeRange(0, 1000), null,
            cancellationToken: cancellation.Token));
    }

    /// <summary>全部 ANN 命中进入有界堆，并列命中须按时间戳确定选取。</summary>
    [Fact]
    public void CollectIndexedBlockCandidates_WithTiedAnnHits_UsesDeterministicTimestampOrder()
    {
        using var candidates = new KnnExecutor.BoundedCandidateSet(2, "embedding", null);
        var hits = new[]
        {
            new VectorSearchResult(2, 3000, 0),
            new VectorSearchResult(1, 2000, 0),
            new VectorSearchResult(0, 1000, 0),
        };
        KnnExecutor.CollectIndexedBlockCandidates([1f, 0f], ReadOnlySpan<byte>.Empty, [1000, 2000, 3000],
            hits, 3, 2, 3, KnnMetric.Cosine, new TimeRange(0, 4000), 1, candidates);
        Assert.Equal(new long[] { 1000, 2000 }, candidates.GetResults().Select(static value => value.Timestamp));
    }

    /// <summary>部分 ANN 落在时间窗外时精确补偿必须覆盖整个窗，且保留不超过 K 个候选。</summary>
    [Fact]
    public void CollectIndexedBlockCandidates_WithNarrowWindowFallback_ReturnsExactWindowTopK()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var candidates = new KnnExecutor.BoundedCandidateSet(2, "embedding", null, deadline.Token);
        float[] vectors = [1f, 0f, 0f, 1f, 0.8f, 0.6f, 1f, 0f];
        var hits = new[] { new VectorSearchResult(0, 1000, 0), new VectorSearchResult(1, 2000, 1) };
        KnnExecutor.CollectIndexedBlockCandidates([1f, 0f], MemoryMarshal.AsBytes(vectors.AsSpan()),
            [1000, 2000, 3000, 4000], hits, 4, 2, 2, KnnMetric.Cosine, new TimeRange(2000, 4000), 1, candidates);
        Assert.Equal(new long[] { 4000, 3000 }, candidates.GetResults().Select(static value => value.Timestamp));
    }
}
