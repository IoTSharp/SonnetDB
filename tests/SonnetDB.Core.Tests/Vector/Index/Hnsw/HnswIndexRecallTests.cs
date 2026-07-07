using SonnetDB.Vector.Index.Hnsw;
using SonnetDB.Vector.Model;
using SonnetDB.Vector.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace SonnetDB.Core.Tests.Vector.Index.Hnsw;

/// <summary>
/// canonical <see cref="HnswIndex{TKey}"/> 的 Recall@10 闭环测试。
/// <para>
/// BenchmarkDotNet 不捕获方法返回值，故用有断言、可在 CI 复现的实测值把召回率从 "TBD"
/// 升级为固定门槛，并通过 xUnit 输出当次实测值供 README 引用。
/// </para>
/// </summary>
public sealed class HnswIndexRecallTests
{
    private readonly ITestOutputHelper _output;

    public HnswIndexRecallTests(ITestOutputHelper output)
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

        using var index = new HnswIndex<int>(
            dimension,
            Metric.Cosine,
            new HnswOptions { M = 16, EfConstruction = 200, EfSearch = 200, Seed = seed });
        for (int row = 0; row < vectorCount; row++)
            index.Add(row, vectorData.AsSpan(row * dimension, dimension));

        var buffer = new (int Key, float Score)[k];
        double recallSum = 0d;
        int unmatchedQueries = 0;
        for (int i = 0; i < queryCount; i++)
        {
            int written = index.Search(GetQueryVector(vectorData, queryIndexes[i], dimension), k, buffer);
            double recall = ComputeRecallAt10(groundTruth[i], buffer, written);
            recallSum += recall;
            if (recall < 1.0d)
                unmatchedQueries++;
        }

        double avgRecall = recallSum / queryCount;
        _output.WriteLine(
            $"HnswIndex Recall@{k} (N={vectorCount}, dim={dimension}, M=16, Ef=200, "
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

    private static double ComputeRecallAt10(int[] expected, (int Key, float Score)[] actual, int actualCount)
    {
        int matched = 0;
        for (int i = 0; i < actualCount; i++)
        {
            int id = actual[i].Key;
            for (int j = 0; j < expected.Length; j++)
            {
                if (expected[j] == id) { matched++; break; }
            }
        }
        return (double)matched / expected.Length;
    }
}
