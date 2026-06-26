using SonnetDB.Vector.Compression;

namespace SonnetDB.Core.Tests.Vector.Compression;

public sealed class ResidualQuantizerTests
{
    [Fact]
    public void Constructor_WithInvalidArguments_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResidualQuantizer(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResidualQuantizer(16, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResidualQuantizer(16, 2, maxIterations: 0));
    }

    [Fact]
    public void Encode_BeforeTrain_Throws()
    {
        var rq = new ResidualQuantizer(dimensions: 16, levels: 2);
        var ex = Assert.Throws<InvalidOperationException>(
            () => rq.Encode(new float[16], new byte[2]));
        Assert.Contains("Train", ex.Message);
    }

    [Fact]
    public void Decode_BeforeTrain_Throws()
    {
        var rq = new ResidualQuantizer(dimensions: 16, levels: 2);
        Assert.Throws<InvalidOperationException>(
            () => rq.Decode(new byte[2], new float[16]));
    }

    [Fact]
    public void BuildScorer_BeforeTrain_Throws()
    {
        var rq = new ResidualQuantizer(dimensions: 16, levels: 2);
        Assert.Throws<InvalidOperationException>(
            () => rq.BuildScorer(new float[16]));
    }

    [Fact]
    public void Train_WithCountBelowKsub_Throws()
    {
        var rq = new ResidualQuantizer(dimensions: 8, levels: 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => rq.Train(new float[8 * 100], 100));
    }

    [Fact]
    public void Quantizer_KindIsRq()
    {
        var rq = new ResidualQuantizer(dimensions: 8, levels: 2);
        Assert.Equal(QuantizerKind.Rq, rq.Kind);
        Assert.Equal(2, rq.CodeBytes);
        Assert.Equal(2, rq.Levels);
        Assert.False(rq.IsTrained);
    }

    [Fact]
    public void Train_RoundTripEncodeDecode_ProducesValidCode()
    {
        const int dim = 32;
        const int levels = 4;
        const int n = 512;
        float[] data = GenerateGaussianClusters(n, dim, clusters: 16, seed: 1);

        var rq = new ResidualQuantizer(dim, levels, maxIterations: 15, seed: 1);
        rq.Train(data, n);
        Assert.True(rq.IsTrained);

        Span<byte> code = stackalloc byte[levels];
        Span<float> recon = stackalloc float[dim];
        rq.Encode(data.AsSpan(0, dim), code);
        rq.Decode(code, recon);

        Assert.Equal(levels, code.Length);
        // 解码结果应在合理量级内。
        for (int d = 0; d < dim; d++)
        {
            Assert.True(float.IsFinite(recon[d]));
        }
    }

    [Fact]
    public void Train_HigherLevelsReduceReconstructionError()
    {
        const int dim = 32;
        const int n = 1024;
        float[] data = GenerateGaussianClusters(n, dim, clusters: 32, seed: 11);

        float mse2 = MeasureMse(data, n, dim, levels: 2, seed: 11);
        float mse4 = MeasureMse(data, n, dim, levels: 4, seed: 11);
        float mse8 = MeasureMse(data, n, dim, levels: 8, seed: 11);

        Assert.True(mse4 < mse2, $"MSE 应随级数下降：mse2={mse2}, mse4={mse4}");
        Assert.True(mse8 < mse4, $"MSE 应随级数下降：mse4={mse4}, mse8={mse8}");
    }

    [Fact]
    public void BuildScorer_MatchesDecodeThenL2()
    {
        const int dim = 32;
        const int levels = 4;
        const int n = 512;
        float[] data = GenerateGaussianClusters(n, dim, clusters: 16, seed: 3);

        var rq = new ResidualQuantizer(dim, levels, maxIterations: 15, seed: 3);
        rq.Train(data, n);

        var query = new float[dim];
        var rng = new Random(99);
        for (int d = 0; d < dim; d++)
        {
            query[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }

        var scorer = rq.BuildScorer(query);
        var code = new byte[levels];
        var recon = new float[dim];

        for (int i = 0; i < 32; i++)
        {
            rq.Encode(data.AsSpan(i * dim, dim), code);
            rq.Decode(code, recon);

            float refScore = 0f;
            for (int d = 0; d < dim; d++)
            {
                float diff = query[d] - recon[d];
                refScore += diff * diff;
            }

            float adcScore = scorer.Score(code);
            float tol = MathF.Max(1e-2f, refScore * 1e-4f);
            Assert.True(MathF.Abs(adcScore - refScore) < tol,
                $"i={i}: adc={adcScore}, ref={refScore}, tol={tol}");
        }
    }

    [Fact]
    public void Train_RecallAcceptable_OnSyntheticDataset()
    {
        // 召回率回归：构造 32 维高斯簇数据，RQ 4 级（4 字节预算，对应 ROADMAP 8–16 字节范围内的低端配置）。
        const int dim = 32;
        const int levels = 4;
        const int n = 1024;
        const int queries = 50;
        const int topK = 10;
        float[] data = GenerateGaussianClusters(n, dim, clusters: 32, seed: 17);

        var rq = new ResidualQuantizer(dim, levels, maxIterations: 20, seed: 17);
        rq.Train(data, n);

        // 编码全部库向量。
        byte[] codes = new byte[n * levels];
        for (int i = 0; i < n; i++)
        {
            rq.Encode(data.AsSpan(i * dim, dim), codes.AsSpan(i * levels, levels));
        }

        var rng = new Random(23);
        int hits = 0;
        int total = 0;
        for (int q = 0; q < queries; q++)
        {
            // 用某条库向量加噪作为查询，确保有可识别的近邻。
            int sourceIdx = rng.Next(n);
            var query = new float[dim];
            for (int d = 0; d < dim; d++)
            {
                query[d] = data[sourceIdx * dim + d] + (float)(rng.NextDouble() * 0.1 - 0.05);
            }

            // 真实 top-K（暴力 L2²）。
            var trueTop = TopK(query, data, n, dim, topK);
            // 近似 top-K（ADC scorer）。
            var scorer = rq.BuildScorer(query);
            var approxTop = TopKApprox(scorer, codes, n, levels, topK);

            foreach (int idx in approxTop)
            {
                if (Array.IndexOf(trueTop, idx) >= 0)
                {
                    hits++;
                }
            }
            total += topK;
        }

        float recall = (float)hits / total;
        Assert.True(recall >= 0.80f, $"RQ 4 级 Recall@10 = {recall}, 期望 ≥ 0.80");
    }

    private static float MeasureMse(float[] data, int count, int dim, int levels, int seed)
    {
        var rq = new ResidualQuantizer(dim, levels, maxIterations: 20, seed: seed);
        rq.Train(data, count);

        var code = new byte[levels];
        var recon = new float[dim];
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            rq.Encode(data.AsSpan(i * dim, dim), code);
            rq.Decode(code, recon);
            for (int d = 0; d < dim; d++)
            {
                float diff = data[i * dim + d] - recon[d];
                sum += diff * diff;
            }
        }
        return (float)(sum / (count * dim));
    }

    private static float[] GenerateGaussianClusters(int n, int dim, int clusters, int seed)
    {
        var rng = new Random(seed);
        // 簇中心。
        var centers = new float[clusters * dim];
        for (int c = 0; c < clusters; c++)
        {
            for (int d = 0; d < dim; d++)
            {
                centers[c * dim + d] = (float)(rng.NextDouble() * 4.0 - 2.0);
            }
        }
        var data = new float[n * dim];
        for (int i = 0; i < n; i++)
        {
            int c = i % clusters;
            for (int d = 0; d < dim; d++)
            {
                // 簇中心 + 小高斯扰动（Box-Muller）。
                double u1 = 1.0 - rng.NextDouble();
                double u2 = 1.0 - rng.NextDouble();
                double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                data[i * dim + d] = centers[c * dim + d] + (float)(z * 0.3);
            }
        }
        return data;
    }

    private static int[] TopK(float[] query, float[] data, int count, int dim, int k)
    {
        var dists = new float[count];
        for (int i = 0; i < count; i++)
        {
            float s = 0;
            for (int d = 0; d < dim; d++)
            {
                float diff = query[d] - data[i * dim + d];
                s += diff * diff;
            }
            dists[i] = s;
        }
        var indices = Enumerable.Range(0, count).ToArray();
        Array.Sort(dists, indices);
        return indices.AsSpan(0, k).ToArray();
    }

    private static int[] TopKApprox(IQuantizedScorer scorer, byte[] codes, int count, int levels, int k)
    {
        var dists = new float[count];
        for (int i = 0; i < count; i++)
        {
            dists[i] = scorer.Score(codes.AsSpan(i * levels, levels));
        }
        var indices = Enumerable.Range(0, count).ToArray();
        Array.Sort(dists, indices);
        return indices.AsSpan(0, k).ToArray();
    }
}
