using SonnetDB.Vector.Compression;

namespace SonnetDB.Core.Tests.Vector.Compression;

public sealed class ScalarQuantizer8Tests
{
    [Fact]
    public void Constructor_WithZeroDimension_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScalarQuantizer8(0));
    }

    [Fact]
    public void Encode_BeforeTrain_Throws()
    {
        var sq = new ScalarQuantizer8(8);
        byte[] code = new byte[8];
        Assert.Throws<InvalidOperationException>(() => sq.Encode(new float[8], code));
    }

    [Fact]
    public void Train_WithMismatchedLength_Throws()
    {
        var sq = new ScalarQuantizer8(4);
        Assert.Throws<ArgumentException>(() => sq.Train(new float[7], 2));
    }

    [Fact]
    public void Train_OnRandomSamples_LearnsPerDimMinMax()
    {
        const int dim = 16;
        const int n = 200;
        var rng = new Random(42);
        var data = new float[n * dim];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (float)(rng.NextDouble() * 2.0 - 1.0); // [-1, 1]
        }

        var sq = new ScalarQuantizer8(dim);
        sq.Train(data, n);

        Assert.True(sq.IsTrained);
        Assert.Equal(QuantizerKind.Sq8, sq.Kind);
        Assert.Equal(dim, sq.Dimensions);
        Assert.Equal(dim, sq.CodeBytes);

        // 重算 min 验证
        for (int d = 0; d < dim; d++)
        {
            float expectedMin = float.MaxValue;
            float expectedMax = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                float v = data[i * dim + d];
                if (v < expectedMin)
                {
                    expectedMin = v;
                }
                if (v > expectedMax)
                {
                    expectedMax = v;
                }
            }
            Assert.Equal(expectedMin, sq.Min[d], 5);
            Assert.Equal((expectedMax - expectedMin) / 255f, sq.Scale[d], 5);
        }
    }

    [Fact]
    public void RoundTrip_DecodeAfterEncode_WithinTolerance()
    {
        const int dim = 32;
        const int n = 500;
        var rng = new Random(7);
        var data = new float[n * dim];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (float)(rng.NextDouble() * 10.0 - 5.0); // [-5, 5]
        }

        var sq = new ScalarQuantizer8(dim);
        sq.Train(data, n);

        byte[] code = new byte[dim];
        float[] decoded = new float[dim];
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<float> row = data.AsSpan(i * dim, dim);
            sq.Encode(row, code);
            sq.Decode(code, decoded);

            for (int d = 0; d < dim; d++)
            {
                float step = sq.Scale[d];
                // 解量化误差严格 ≤ step / 2 + 1 ulp 容忍
                float err = MathF.Abs(decoded[d] - row[d]);
                Assert.True(
                    err <= step * 0.5f + 1e-5f,
                    $"维度 {d} 行 {i}：err={err}, step={step}, original={row[d]}, decoded={decoded[d]}");
            }
        }
    }

    [Fact]
    public void Encode_ProducesByteRangeUtilization()
    {
        const int dim = 8;
        const int n = 256;
        var data = new float[n * dim];
        // 在每个维度上让数据均匀覆盖 [0, 1]
        for (int i = 0; i < n; i++)
        {
            for (int d = 0; d < dim; d++)
            {
                data[i * dim + d] = i / (float)(n - 1);
            }
        }

        var sq = new ScalarQuantizer8(dim);
        sq.Train(data, n);

        byte[] code = new byte[dim];
        sq.Encode(data.AsSpan(0, dim), code);
        Assert.All(code, b => Assert.Equal(0, b));

        sq.Encode(data.AsSpan((n - 1) * dim, dim), code);
        Assert.All(code, b => Assert.Equal(255, b));

        // 中点应落在约 127~128
        sq.Encode(data.AsSpan((n / 2) * dim, dim), code);
        Assert.All(code, b => Assert.InRange(b, 126, 129));
    }

    [Fact]
    public void Train_OnConstantDimension_HandlesZeroRangeGracefully()
    {
        const int dim = 4;
        const int n = 10;
        var data = new float[n * dim];
        for (int i = 0; i < n; i++)
        {
            data[i * dim + 0] = 3.14f;             // 常量维度
            data[i * dim + 1] = i * 0.1f;
            data[i * dim + 2] = -i * 0.2f;
            data[i * dim + 3] = 1.0f;              // 常量维度
        }

        var sq = new ScalarQuantizer8(dim);
        sq.Train(data, n);

        byte[] code = new byte[dim];
        float[] decoded = new float[dim];
        sq.Encode(data.AsSpan(5 * dim, dim), code);
        sq.Decode(code, decoded);

        // 常量维度反量化应等于训练时记录的最小值
        Assert.Equal(3.14f, decoded[0], 5);
        Assert.Equal(1.0f, decoded[3], 5);
    }

    [Fact]
    public void Encode_BufferTooSmall_Throws()
    {
        var sq = new ScalarQuantizer8(8);
        sq.Train(new float[8], 1);
        Assert.Throws<ArgumentException>(() => sq.Encode(new float[8], new byte[4]));
    }

    [Fact]
    public void Encode_DimensionMismatch_Throws()
    {
        var sq = new ScalarQuantizer8(8);
        sq.Train(new float[8], 1);
        Assert.Throws<ArgumentException>(() => sq.Encode(new float[6], new byte[8]));
    }
}
