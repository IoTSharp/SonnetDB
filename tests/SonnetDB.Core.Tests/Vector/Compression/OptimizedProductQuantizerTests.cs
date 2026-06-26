using SonnetDB.Vector.Compression;

namespace SonnetDB.Core.Tests.Vector.Compression;

public sealed class OptimizedProductQuantizerTests
{
    [Fact]
    public void Constructor_WithIndivisibleDimensions_Throws()
    {
        Assert.Throws<ArgumentException>(() => new OptimizedProductQuantizer(dimensions: 10, m: 3));
    }

    [Fact]
    public void Constructor_WithNonPositiveOpqIterations_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OptimizedProductQuantizer(dimensions: 16, m: 4, opqIterations: 0));
    }

    [Fact]
    public void Encode_BeforeTrain_Throws()
    {
        var opq = new OptimizedProductQuantizer(dimensions: 16, m: 4);
        Assert.Throws<InvalidOperationException>(
            () => opq.Encode(new float[16], new byte[4]));
    }

    [Fact]
    public void Decode_BeforeTrain_Throws()
    {
        var opq = new OptimizedProductQuantizer(dimensions: 16, m: 4);
        Assert.Throws<InvalidOperationException>(
            () => opq.Decode(new byte[4], new float[16]));
    }

    [Fact]
    public void BuildScorer_BeforeTrain_Throws()
    {
        var opq = new OptimizedProductQuantizer(dimensions: 16, m: 4);
        Assert.Throws<InvalidOperationException>(
            () => opq.BuildScorer(new float[16]));
    }

    [Fact]
    public void Quantizer_KindIsOpq()
    {
        var opq = new OptimizedProductQuantizer(dimensions: 16, m: 4);
        Assert.Equal(QuantizerKind.Opq, opq.Kind);
        Assert.Equal(4, opq.CodeBytes);
        Assert.Equal(4, opq.M);
        Assert.Equal(16, opq.Dimensions);
    }

    [Fact]
    public void Train_ProducesOrthogonalRotation()
    {
        const int d = 16;
        const int n = 512;
        float[] data = GenerateAnisotropicGaussian(d, n, seed: 11);

        var opq = new OptimizedProductQuantizer(d, m: 4, opqIterations: 5, pqMaxIterations: 10, seed: 11);
        opq.Train(data, n);

        Assert.True(opq.IsTrained);
        ReadOnlySpan<float> r = opq.Rotation;
        // R^T · R ≈ I
        for (int i = 0; i < d; i++)
        {
            for (int j = 0; j < d; j++)
            {
                float sum = 0f;
                for (int k = 0; k < d; k++) sum += r[k * d + i] * r[k * d + j];
                float expected = i == j ? 1f : 0f;
                Assert.True(MathF.Abs(sum - expected) < 1e-3f,
                    $"R 非正交：(R^T R)[{i},{j}] = {sum}");
            }
        }
    }

    [Fact]
    public void Train_RoundTripEncodeDecode_PreservesShape()
    {
        const int d = 16;
        const int n = 512;
        float[] data = GenerateAnisotropicGaussian(d, n, seed: 22);
        var opq = new OptimizedProductQuantizer(d, m: 4, opqIterations: 5, pqMaxIterations: 10, seed: 22);
        opq.Train(data, n);

        Span<byte> code = stackalloc byte[4];
        Span<float> reconstructed = stackalloc float[d];
        opq.Encode(data.AsSpan(0, d), code);
        opq.Decode(code, reconstructed);

        // 编码长度应等于 M
        Assert.Equal(4, opq.CodeBytes);
        // 重建结果应为有限实数
        for (int i = 0; i < d; i++) Assert.True(float.IsFinite(reconstructed[i]));
    }

    [Fact]
    public void BuildScorer_MatchesDecodeThenL2()
    {
        const int d = 16;
        const int n = 256;
        float[] data = GenerateAnisotropicGaussian(d, n, seed: 33);
        var opq = new OptimizedProductQuantizer(d, m: 4, opqIterations: 5, pqMaxIterations: 10, seed: 33);
        opq.Train(data, n);

        // 取一条样本作为 query
        var query = data.AsSpan(0, d).ToArray();
        var scorer = opq.BuildScorer(query);

        // 在另一些样本上比较：scorer.Score(code) vs L2²(R·query, decode(code))
        Span<byte> code = stackalloc byte[4];
        Span<float> decoded = stackalloc float[d];

        // 计算旋转后的 query：通过 BuildScorer 已经隐式做了；这里直接对比 L2²(query, R^T·decoded)
        for (int i = 1; i < 10; i++)
        {
            opq.Encode(data.AsSpan(i * d, d), code);
            opq.Decode(code, decoded);
            float adcScore = scorer.Score(code);
            float refScore = 0f;
            for (int j = 0; j < d; j++)
            {
                float diff = decoded[j] - query[j];
                refScore += diff * diff;
            }
            // 容忍少量浮点累加误差：取相对+绝对联合阈值
            float tol = MathF.Max(1e-2f, refScore * 1e-4f);
            Assert.True(MathF.Abs(adcScore - refScore) < tol,
                $"ADC ({adcScore}) 与解码后 L2² ({refScore}) 偏差过大");
        }
    }

    [Fact]
    public void Train_ReducesReconstructionErrorVsInitialPq()
    {
        // 在各向异性数据上，OPQ 训练后的重建均方误差应不大于纯 PQ 基线（含一定容忍度）。
        const int d = 16;
        const int n = 512;
        const int m = 4;
        float[] data = GenerateAnisotropicGaussian(d, n, seed: 44);

        // 基线：纯 PQ
        var pq = new ProductQuantizer(d, m, maxIterations: 20, seed: 44);
        pq.Train(data, n);
        double pqMse = ComputeReconstructionMse(pq, data, n, d);

        // OPQ
        var opq = new OptimizedProductQuantizer(d, m, opqIterations: 8, pqMaxIterations: 20, seed: 44);
        opq.Train(data, n);
        double opqMse = ComputeReconstructionMse(opq, data, n, d);

        // OPQ 不应显著差于纯 PQ；通常显著更优。容忍 5% 上浮以避免随机种子边界。
        Assert.True(opqMse <= pqMse * 1.05,
            $"OPQ 重建 MSE ({opqMse}) 不应显著差于 PQ ({pqMse})");
    }

    private static double ComputeReconstructionMse(IVectorQuantizer q, float[] data, int n, int d)
    {
        Span<byte> code = stackalloc byte[256];
        Span<float> recon = stackalloc float[2048];
        Span<byte> codeBuf = code[..q.CodeBytes];
        Span<float> reconBuf = recon[..d];
        double sumSq = 0;
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<float> x = data.AsSpan(i * d, d);
            q.Encode(x, codeBuf);
            q.Decode(codeBuf, reconBuf);
            for (int j = 0; j < d; j++)
            {
                double diff = x[j] - reconBuf[j];
                sumSq += diff * diff;
            }
        }
        return sumSq / (n * d);
    }

    /// <summary>
    /// 构造各向异性高斯数据：前 d/2 维方差大，后 d/2 维方差小。
    /// 这种结构 OPQ 应该能旋转到 PQ 子空间更均衡的方向。
    /// </summary>
    private static float[] GenerateAnisotropicGaussian(int d, int n, int seed)
    {
        var rng = new Random(seed);
        float[] data = new float[n * d];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < d; j++)
            {
                double u1 = 1.0 - rng.NextDouble();
                double u2 = 1.0 - rng.NextDouble();
                double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                double scale = j < d / 2 ? 5.0 : 0.5;
                data[i * d + j] = (float)(z * scale);
            }
        }
        return data;
    }
}
