using System.Runtime.InteropServices;
using SonnetDB.Catalog;
using SonnetDB.Query;
using SonnetDB.Storage.Segments;
using Xunit;
using Xunit.Abstractions;

namespace SonnetDB.Core.Tests.Storage.Segments;

/// <summary>
/// SonnetDB 端 HNSW 段内索引（<see cref="HnswVectorBlockIndex"/>）的 Recall@10 闭环测试。
/// <para>
/// 之前 README 用 <c>TBD</c> 表示召回率，是因为 BenchmarkDotNet 不捕获方法返回值。
/// 此测试把召回率从 "TBD" 升级为有断言、可在 CI 复现的实际数值，并通过 xUnit
/// 输出当次实测值供 README 引用。
/// </para>
/// </summary>
public sealed class HnswVectorBlockIndexRecallTests
{
    private readonly ITestOutputHelper _output;

    public HnswVectorBlockIndexRecallTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(5_000)]
    public void HnswRecall_At10_AtLeast90Percent_Cosine_384d(int vectorCount)
    {
        const int dimension = 384;
        const int k = 10;
        const int queryCount = 20;
        const int seed = 20260616;

        var vectorData = GenerateNormalizedVectors(vectorCount, dimension, seed);
        var timestamps = new long[vectorCount];
        for (int i = 0; i < timestamps.Length; i++)
            timestamps[i] = i;

        var queryIndexes = SelectQueryIndexes(vectorCount, queryCount, seed + 17);
        var groundTruth = new int[queryCount][];
        for (int i = 0; i < queryCount; i++)
        {
            groundTruth[i] = new int[k];
            ComputeExactTopK(
                GetQueryVector(vectorData, queryIndexes[i], dimension),
                vectorData,
                vectorCount,
                dimension,
                groundTruth[i]);
        }

        var index = HnswVectorBlockIndex.Build(
            blockIndex: 0,
            valPayload: MemoryMarshal.AsBytes(vectorData.AsSpan()),
            count: vectorCount,
            dimension: dimension,
            options: new HnswVectorIndexOptions(M: 16, Ef: 200, EfConstruction: 200));

        ReadOnlySpan<byte> payload = MemoryMarshal.AsBytes(vectorData.AsSpan());
        double recallSum = 0d;
        int unmatchedQueries = 0;
        for (int i = 0; i < queryCount; i++)
        {
            var hits = index.Search(
                GetQueryVector(vectorData, queryIndexes[i], dimension),
                payload,
                timestamps,
                k,
                KnnMetric.Cosine);
            double recall = ComputeRecallAt10(groundTruth[i], hits);
            recallSum += recall;
            if (recall < 1.0d)
                unmatchedQueries++;
        }

        double avgRecall = recallSum / queryCount;
        _output.WriteLine(
            $"HnswVectorBlockIndex Recall@{k} (N={vectorCount}, dim={dimension}, M=16, Ef=200, "
            + $"{queryCount} queries): avg={avgRecall:F4}, imperfect={unmatchedQueries}/{queryCount}");

        // 主流 HNSW 参数组（M=16, Ef=200）在 cosine 384-d 上应轻松达到 ≥ 0.90。
        // 长尾留 0.05 余量避免随机数据上偶发抖动。
        Assert.True(avgRecall >= 0.90d,
            $"HNSW Recall@{k} = {avgRecall:F4}, 期望 ≥ 0.90（N={vectorCount}）。");
    }

    private static ReadOnlySpan<float> GetQueryVector(float[] vectorData, int vectorIndex, int dimension)
        => vectorData.AsSpan(vectorIndex * dimension, dimension);

    private static float[] GenerateNormalizedVectors(int count, int dimension, int seed)
    {
        var rng = new Random(seed);
        var data = new float[checked(count * dimension)];
        var temp = new float[dimension];
        for (int v = 0; v < count; v++)
        {
            double norm2 = 0d;
            for (int d = 0; d < dimension; d++)
            {
                float value = (float)(rng.NextDouble() * 2.0 - 1.0);
                temp[d] = value;
                norm2 += value * value;
            }
            float invNorm = norm2 <= double.Epsilon ? 1f : (float)(1.0 / Math.Sqrt(norm2));
            int offset = v * dimension;
            for (int d = 0; d < dimension; d++)
                data[offset + d] = temp[d] * invNorm;
        }
        return data;
    }

    private static int[] SelectQueryIndexes(int vectorCount, int queryCount, int seed)
    {
        var rng = new Random(seed);
        var indexes = new int[queryCount];
        for (int i = 0; i < indexes.Length; i++)
            indexes[i] = rng.Next(vectorCount);
        return indexes;
    }

    private static void ComputeExactTopK(
        ReadOnlySpan<float> query,
        ReadOnlySpan<float> vectorData,
        int vectorCount,
        int dimension,
        int[] topIds)
    {
        var topDistances = new double[topIds.Length];
        Array.Fill(topIds, -1);
        Array.Fill(topDistances, double.PositiveInfinity);

        int worstIndex = 0;
        double worstDistance = double.PositiveInfinity;
        for (int v = 0; v < vectorCount; v++)
        {
            double distance = VectorDistance.ComputeCosine(query, vectorData.Slice(v * dimension, dimension));
            if (distance >= worstDistance)
                continue;
            topIds[worstIndex] = v;
            topDistances[worstIndex] = distance;
            worstDistance = topDistances[0];
            worstIndex = 0;
            for (int i = 1; i < topDistances.Length; i++)
            {
                if (topDistances[i] > worstDistance)
                {
                    worstDistance = topDistances[i];
                    worstIndex = i;
                }
            }
        }
    }

    private static double ComputeRecallAt10(int[] expected, IReadOnlyList<HnswAnnSearchResult> actual)
    {
        int matched = 0;
        for (int i = 0; i < actual.Count; i++)
        {
            int id = actual[i].PointIndex;
            for (int j = 0; j < expected.Length; j++)
            {
                if (expected[j] == id) { matched++; break; }
            }
        }
        return (double)matched / expected.Length;
    }
}
