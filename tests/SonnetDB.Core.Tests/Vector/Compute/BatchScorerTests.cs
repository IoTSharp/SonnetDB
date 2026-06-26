using SonnetDB.Vector.Compute;
using SonnetDB.Vector.Index.Flat;
using SonnetDB.Vector.Model;

namespace SonnetDB.Core.Tests.Vector.Compute;

/// <summary>
/// <see cref="IBatchScorer"/> 与 <see cref="CpuTensorPrimitivesScorer"/> 的单元测试，覆盖：
/// <list type="bullet">
///   <item>与 <see cref="Distance.Compute(ReadOnlySpan{float}, ReadOnlySpan{float}, Metric)"/> 在 scalar 路径下 bit-identical</item>
///   <item>输入校验</item>
///   <item><see cref="FlatIndex{TKey}"/> 注入自定义 scorer 后 Search 结果与默认路径完全一致</item>
/// </list>
/// </summary>
public class BatchScorerTests
{
    [Theory]
    [InlineData(Metric.L2)]
    [InlineData(Metric.Cosine)]
    [InlineData(Metric.InnerProduct)]
    [InlineData(Metric.DotProduct)]
    public void CpuScorer_BitIdentical_WithDistanceCompute(Metric metric)
    {
        const int Dim = 64;
        const int N = 17;
        var rng = new Random(42);
        float[] query = NewRandom(rng, Dim);
        float[] dataset = NewRandom(rng, N * Dim);
        float[] expected = new float[N];
        for (int i = 0; i < N; i++)
        {
            expected[i] = Distance.Compute(query, dataset.AsSpan(i * Dim, Dim), metric);
        }

        float[] actual = new float[N];
        CpuTensorPrimitivesScorer.Instance.Score(query, dataset, actual, metric);

        for (int i = 0; i < N; i++)
        {
            // 默认 CPU 实现内部直接转发到 Distance.Compute，结果须 bit-identical。
            Assert.Equal(expected[i], actual[i]);
        }
    }

    [Fact]
    public void CpuScorer_HammingMetric_Throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            CpuTensorPrimitivesScorer.Instance.Score(
                new float[4], new float[8], new float[2], Metric.Hamming));
    }

    [Fact]
    public void CpuScorer_DatasetLengthMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            CpuTensorPrimitivesScorer.Instance.Score(
                new float[4], new float[10] /* 不是 4 的整数倍 */, new float[2], Metric.L2));
    }

    [Fact]
    public void CpuScorer_EmptyScores_NoOp()
    {
        // 空数据集应被静默接受。
        CpuTensorPrimitivesScorer.Instance.Score(
            new float[4], ReadOnlySpan<float>.Empty, Span<float>.Empty, Metric.L2);
    }

    [Fact]
    public void FlatIndex_WithInjectedScorer_MatchesDefaultPath()
    {
        const int Dim = 16;
        const int N = 50;
        var rng = new Random(7);

        var defaultIdx = new FlatIndex<int>(Dim, Metric.L2, initialCapacity: N);
        var injectedIdx = new FlatIndex<int>(Dim, Metric.L2, initialCapacity: N, scorer: CpuTensorPrimitivesScorer.Instance);

        for (int i = 0; i < N; i++)
        {
            float[] v = NewRandom(rng, Dim);
            defaultIdx.Add(i, v);
            injectedIdx.Add(i, v);
        }

        float[] query = NewRandom(rng, Dim);
        const int K = 10;
        var defaultResults = new (int Key, float Score)[K];
        var injectedResults = new (int Key, float Score)[K];
        int dCount = defaultIdx.Search(query, K, defaultResults);
        int iCount = injectedIdx.Search(query, K, injectedResults);

        Assert.Equal(dCount, iCount);
        for (int i = 0; i < dCount; i++)
        {
            Assert.Equal(defaultResults[i].Key, injectedResults[i].Key);
            Assert.Equal(defaultResults[i].Score, injectedResults[i].Score);
        }
    }

    private static float[] NewRandom(Random rng, int n)
    {
        var arr = new float[n];
        for (int i = 0; i < n; i++)
        {
            arr[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }
        return arr;
    }
}
