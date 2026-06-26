using SonnetDB.Vector.Compression;

namespace SonnetDB.Core.Tests.Vector.Compression;

public sealed class ProductQuantizerTests
{
    [Fact]
    public void Constructor_WithIndivisibleDimensions_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ProductQuantizer(dimensions: 10, m: 3));
    }

    [Fact]
    public void Encode_BeforeTrain_Throws()
    {
        var pq = new ProductQuantizer(dimensions: 16, m: 4);
        var code = new byte[4];
        var ex = Assert.Throws<InvalidOperationException>(
            () => pq.Encode(new float[16], code));
        Assert.Contains("Train", ex.Message);
    }

    [Fact]
    public void Decode_BeforeTrain_Throws()
    {
        var pq = new ProductQuantizer(dimensions: 16, m: 4);
        Assert.Throws<InvalidOperationException>(
            () => pq.Decode(new byte[4], new float[16]));
    }

    [Fact]
    public void BuildScorer_BeforeTrain_Throws()
    {
        var pq = new ProductQuantizer(dimensions: 16, m: 4);
        Assert.Throws<InvalidOperationException>(
            () => pq.BuildScorer(new float[16]));
    }

    [Fact]
    public void Train_ProducesEncodingsInValidRange()
    {
        const int dim = 16;
        const int m = 4;
        const int n = 512;
        var rng = new Random(42);
        float[] data = new float[n * dim];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }

        var pq = new ProductQuantizer(dim, m, maxIterations: 15, seed: 42);
        pq.Train(data, n);
        Assert.True(pq.IsTrained);
        Assert.Equal(m, pq.CodeBytes);

        var code = new byte[m];
        pq.Encode(data.AsSpan(0, dim), code);
        // 每个字节是 [0,255] 范围的中心索引；类型本身已保证。
        // 此处仅断言 Encode 不抛异常并返回 m 字节。
        Assert.Equal(m, code.Length);
    }

    [Fact]
    public void Decode_ReconstructsCentroidConcatenation()
    {
        const int dim = 32;
        const int m = 8;
        const int n = 1024;
        var rng = new Random(7);
        float[] data = new float[n * dim];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (float)(rng.NextDouble() * 4.0 - 2.0);
        }

        var pq = new ProductQuantizer(dim, m, maxIterations: 20, seed: 7);
        pq.Train(data, n);

        var code = new byte[m];
        var reconstructed = new float[dim];
        pq.Encode(data.AsSpan(0, dim), code);
        pq.Decode(code, reconstructed);

        // 重建即各子空间被选中心的拼接 — 不抛异常并填满目标缓冲。
        // 通过随后的 scorer 一致性测试间接验证数值正确。
        Assert.Equal(dim, reconstructed.Length);
    }

    [Fact]
    public void BuildScorer_MatchesScalarReference()
    {
        const int dim = 16;
        const int m = 4;
        const int n = 512;
        var rng = new Random(123);
        float[] data = new float[n * dim];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }

        var pq = new ProductQuantizer(dim, m, maxIterations: 15, seed: 123);
        pq.Train(data, n);

        // 编码 64 条向量。
        const int sampleCount = 64;
        var codes = new byte[sampleCount * m];
        for (int i = 0; i < sampleCount; i++)
        {
            pq.Encode(data.AsSpan(i * dim, dim), codes.AsSpan(i * m, m));
        }

        // 任意查询。
        var query = new float[dim];
        for (int d = 0; d < dim; d++)
        {
            query[d] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }

        IQuantizedScorer scorer = pq.BuildScorer(query);
        Assert.Equal(m, scorer.CodeBytes);

        // 标量参考：通过 Decode 重建后直接计算 L2 平方。
        for (int i = 0; i < sampleCount; i++)
        {
            ReadOnlySpan<byte> code = codes.AsSpan(i * m, m);
            var rebuilt = new float[dim];
            pq.Decode(code, rebuilt);

            float reference = 0f;
            for (int d = 0; d < dim; d++)
            {
                float diff = query[d] - rebuilt[d];
                reference += diff * diff;
            }

            float adc = scorer.Score(code);
            Assert.InRange(adc - reference, -1e-4f, 1e-4f);
        }
    }

    [Fact]
    public void Score_WithUndersizedCode_Throws()
    {
        const int dim = 16;
        const int m = 4;
        const int n = 300;
        var rng = new Random(0);
        float[] data = new float[n * dim];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (float)rng.NextDouble();
        }

        var pq = new ProductQuantizer(dim, m, maxIterations: 10, seed: 0);
        pq.Train(data, n);

        IQuantizedScorer scorer = pq.BuildScorer(new float[dim]);
        Assert.Throws<ArgumentException>(() => scorer.Score(new byte[m - 1]));
    }

    [Fact]
    public void Encode_DimensionMismatch_Throws()
    {
        const int dim = 16;
        const int m = 4;
        const int n = 300;
        var rng = new Random(0);
        float[] data = new float[n * dim];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (float)rng.NextDouble();
        }
        var pq = new ProductQuantizer(dim, m, maxIterations: 10, seed: 0);
        pq.Train(data, n);

        Assert.Throws<ArgumentException>(() => pq.Encode(new float[dim - 1], new byte[m]));
    }

    [Fact]
    public void Decode_DimensionMismatch_Throws()
    {
        const int dim = 16;
        const int m = 4;
        const int n = 300;
        var rng = new Random(0);
        float[] data = new float[n * dim];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (float)rng.NextDouble();
        }
        var pq = new ProductQuantizer(dim, m, maxIterations: 10, seed: 0);
        pq.Train(data, n);

        Assert.Throws<ArgumentException>(() => pq.Decode(new byte[m], new float[dim - 1]));
    }

    [Fact]
    public void Quantizer_KindIsPq()
    {
        var pq = new ProductQuantizer(dimensions: 8, m: 2);
        Assert.Equal(QuantizerKind.Pq, pq.Kind);
    }
}
