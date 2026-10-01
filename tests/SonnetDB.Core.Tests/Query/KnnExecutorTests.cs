using System.Runtime.InteropServices;
using SonnetDB.Query;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Query;

/// <summary>
/// <see cref="KnnExecutor"/> 内部候选合并逻辑测试。
/// </summary>
public sealed class KnnExecutorTests
{
    [Fact]
    public void CollectIndexedBlockCandidates_WithPartialAnnHitsAndExactFallback_DoesNotDuplicateAcceptedPoint()
    {
        float[] vectorData =
        [
            1f, 0f,
            0.8f, 0.6f,
            0f, 1f,
        ];
        long[] timestamps = [1000L, 2000L, 3000L];
        var annHits = new[]
        {
            new VectorSearchResult(
                PointIndex: 1,
                Timestamp: 2000L,
                Distance: VectorDistance.ComputeCosine([1f, 0f], [0.8f, 0.6f])),
        };

        using var candidates = new KnnExecutor.BoundedCandidateSet(2, "embedding", tombstones: null);
        KnnExecutor.CollectIndexedBlockCandidates(
            queryVector: [1f, 0f],
            valPayload: MemoryMarshal.AsBytes(vectorData.AsSpan()),
            timestamps: timestamps,
            annHits: annHits,
            pointCount: timestamps.Length,
            k: 2,
            candidateLimit: 1,
            metric: KnnMetric.Cosine,
            timeRange: new TimeRange(1500L, 3000L),
            seriesId: 42UL,
            candidates: candidates);

        var results = candidates.GetResults();
        Assert.Equal(2, results.Count);
        Assert.Equal(1, results.Count(static candidate => candidate.Timestamp == 2000L));
        Assert.Equal(1, results.Count(static candidate => candidate.Timestamp == 3000L));
    }
}
