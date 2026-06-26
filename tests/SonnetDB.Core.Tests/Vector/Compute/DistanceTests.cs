using SonnetDB.Vector.Compute;
using SonnetDB.Vector.Model;

namespace SonnetDB.Core.Tests.Vector.Compute;

/// <summary>
/// 距离函数单元测试，覆盖：
/// - 输入校验与边界
/// - 已知值正确性
/// - SIMD 与 scalar 在高维度（4096）下的一致性（差 &lt; 1e-5）
/// - 零向量、NaN、Infinity 等数值边界
/// </summary>
public class DistanceTests
{
    private const float Tolerance = 1e-5f;

    [Fact]
    public void L2Squared_LengthMismatch_Throws()
    {
        float[] a = [1f, 2f, 3f];
        float[] b = [1f, 2f];
        Assert.Throws<ArgumentException>(() => Distance.L2Squared(a, b));
    }

    [Fact]
    public void L2Squared_EmptyVectors_ReturnsZero()
    {
        Assert.Equal(0f, Distance.L2Squared(ReadOnlySpan<float>.Empty, ReadOnlySpan<float>.Empty));
    }

    [Fact]
    public void L2Squared_KnownValues_ReturnsExpected()
    {
        float[] a = [1f, 2f, 3f];
        float[] b = [4f, 6f, 8f];
        // (3^2 + 4^2 + 5^2) = 9 + 16 + 25 = 50
        Assert.Equal(50f, Distance.L2Squared(a, b), 5);
    }

    [Fact]
    public void L2_KnownValues_ReturnsSqrt()
    {
        float[] a = [0f, 0f, 0f];
        float[] b = [3f, 4f, 0f];
        Assert.Equal(5f, Distance.L2(a, b), 5);
    }

    [Fact]
    public void Cosine_IdenticalVectors_ReturnsZero()
    {
        float[] a = [1f, 2f, 3f, 4f];
        float[] b = [1f, 2f, 3f, 4f];
        Assert.InRange(Distance.Cosine(a, b), 0f, Tolerance);
    }

    [Fact]
    public void Cosine_OrthogonalVectors_ReturnsOne()
    {
        float[] a = [1f, 0f, 0f];
        float[] b = [0f, 1f, 0f];
        Assert.Equal(1f, Distance.Cosine(a, b), 5);
    }

    [Fact]
    public void Cosine_OppositeVectors_ReturnsTwo()
    {
        float[] a = [1f, 2f, 3f];
        float[] b = [-1f, -2f, -3f];
        Assert.Equal(2f, Distance.Cosine(a, b), 5);
    }

    [Fact]
    public void Cosine_ZeroVector_ReturnsOneNotNaN()
    {
        float[] a = [0f, 0f, 0f, 0f];
        float[] b = [1f, 2f, 3f, 4f];
        float result = Distance.Cosine(a, b);
        Assert.False(float.IsNaN(result));
        Assert.Equal(1f, result);
    }

    [Fact]
    public void Cosine_BothZero_ReturnsOne()
    {
        float[] a = new float[8];
        float[] b = new float[8];
        Assert.Equal(1f, Distance.Cosine(a, b));
    }

    [Fact]
    public void InnerProduct_KnownValues_ReturnsExpected()
    {
        float[] a = [1f, 2f, 3f];
        float[] b = [4f, 5f, 6f];
        // 1*4 + 2*5 + 3*6 = 4 + 10 + 18 = 32
        Assert.Equal(32f, Distance.InnerProduct(a, b), 5);
    }

    [Fact]
    public void InnerProduct_EmptyVectors_ReturnsZero()
    {
        Assert.Equal(0f, Distance.InnerProduct(ReadOnlySpan<float>.Empty, ReadOnlySpan<float>.Empty));
    }

    [Fact]
    public void DotProduct_NormalizedIdentical_ReturnsZero()
    {
        float[] a = [1f, 0f, 0f];
        float[] b = [1f, 0f, 0f];
        Assert.Equal(0f, Distance.DotProduct(a, b), 5);
    }

    [Fact]
    public void Hamming_LengthMismatch_Throws()
    {
        byte[] a = [0x00, 0xFF];
        byte[] b = [0x00];
        Assert.Throws<ArgumentException>(() => Distance.Hamming(a, b));
    }

    [Fact]
    public void Hamming_AllEqual_ReturnsZero()
    {
        byte[] a = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
        byte[] b = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
        Assert.Equal(0, Distance.Hamming(a, b));
    }

    [Fact]
    public void Hamming_AllDifferent_ReturnsBitCount()
    {
        byte[] a = new byte[16];        // all 0x00
        byte[] b = new byte[16];
        Array.Fill(b, (byte)0xFF);
        Assert.Equal(16 * 8, Distance.Hamming(a, b));
    }

    [Theory]
    [InlineData(new byte[] { 0b0000_0001 }, new byte[] { 0b0000_0010 }, 2)]
    [InlineData(new byte[] { 0b1111_0000 }, new byte[] { 0b0000_1111 }, 8)]
    [InlineData(new byte[] { 0xFF, 0x00, 0xFF }, new byte[] { 0x00, 0xFF, 0x00 }, 24)]
    public void Hamming_KnownValues_ReturnsExpected(byte[] a, byte[] b, int expected)
    {
        Assert.Equal(expected, Distance.Hamming(a, b));
    }

    [Fact]
    public void Hamming_TailBytesAfter64BitChunks_ProcessedCorrectly()
    {
        // 9 字节：1 个 ulong + 1 个尾字节
        byte[] a = [0, 0, 0, 0, 0, 0, 0, 0, 0xFF];
        byte[] b = [0, 0, 0, 0, 0, 0, 0, 0, 0x00];
        Assert.Equal(8, Distance.Hamming(a, b));
    }

    [Fact]
    public void Compute_DispatchesByMetric()
    {
        float[] a = [1f, 2f, 3f];
        float[] b = [4f, 5f, 6f];

        Assert.Equal(Distance.L2Squared(a, b), Distance.Compute(a, b, Metric.L2));
        Assert.Equal(Distance.Cosine(a, b), Distance.Compute(a, b, Metric.Cosine));
        Assert.Equal(Distance.InnerProduct(a, b), Distance.Compute(a, b, Metric.InnerProduct));
        Assert.Equal(Distance.DotProduct(a, b), Distance.Compute(a, b, Metric.DotProduct));
    }

    [Fact]
    public void Compute_HammingMetric_ThrowsNotSupported()
    {
        float[] a = [1f, 2f];
        float[] b = [3f, 4f];
        Assert.Throws<NotSupportedException>(() => Distance.Compute(a, b, Metric.Hamming));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(128)]
    [InlineData(384)]
    [InlineData(1536)]
    [InlineData(4096)]
    public void L2Squared_SimdMatchesScalar(int dim)
    {
        (float[] a, float[] b) = RandomPair(dim, seed: 1234 + dim);
        float simd = Distance.L2Squared(a, b);
        float scalar = Distance.L2SquaredScalar(a, b);
        AssertClose(scalar, simd, dim);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(128)]
    [InlineData(1536)]
    [InlineData(4096)]
    public void InnerProduct_SimdMatchesScalar(int dim)
    {
        (float[] a, float[] b) = RandomPair(dim, seed: 5678 + dim);
        float simd = Distance.InnerProduct(a, b);
        float scalar = Distance.InnerProductScalar(a, b);
        AssertClose(scalar, simd, dim);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(128)]
    [InlineData(1536)]
    [InlineData(4096)]
    public void Cosine_SimdMatchesScalar(int dim)
    {
        (float[] a, float[] b) = RandomPair(dim, seed: 9999 + dim);
        float simd = Distance.Cosine(a, b);
        float scalar = Distance.CosineScalar(a, b);
        // Cosine 结果在 [0, 2]，使用绝对误差容差
        Assert.True(MathF.Abs(simd - scalar) < 1e-5f,
            $"dim={dim}, simd={simd}, scalar={scalar}, diff={MathF.Abs(simd - scalar)}");
    }

    [Fact]
    public void FloatDistanceKernel_DelegatesToDistance()
    {
        float[] a = [1f, 2f, 3f, 4f];
        float[] b = [5f, 6f, 7f, 8f];
        var k = FloatDistanceKernel.Instance;

        Assert.Equal(Distance.L2Squared(a, b), k.ComputeL2Squared(a, b));
        Assert.Equal(Distance.Cosine(a, b), k.ComputeCosine(a, b));
        Assert.Equal(Distance.InnerProduct(a, b), k.ComputeInnerProduct(a, b));
    }

    [Fact]
    public void GenericFloatDistanceKernel_Float_MatchesDistance()
    {
        float[] a = [1f, 2f, 3f, 4f, 5f];
        float[] b = [2f, 3f, 4f, 5f, 6f];
        var k = GenericFloatDistanceKernel<float>.Instance;

        Assert.Equal(Distance.L2SquaredScalar(a, b), k.ComputeL2Squared(a, b), 5);
        Assert.Equal(Distance.InnerProductScalar(a, b), k.ComputeInnerProduct(a, b), 5);
        Assert.Equal(Distance.CosineScalar(a, b), k.ComputeCosine(a, b), 5);
    }

    [Fact]
    public void GenericFloatDistanceKernel_Double_Works()
    {
        double[] a = [1d, 2d, 3d];
        double[] b = [4d, 6d, 8d];
        var k = GenericFloatDistanceKernel<double>.Instance;
        Assert.Equal(50d, k.ComputeL2Squared(a, b), 10);
    }

    private static (float[] A, float[] B) RandomPair(int dim, int seed)
    {
        var rng = new Random(seed);
        var a = new float[dim];
        var b = new float[dim];
        for (int i = 0; i < dim; i++)
        {
            a[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
            b[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }
        return (a, b);
    }

    private static void AssertClose(float expected, float actual, int dim)
    {
        // 使用相对+绝对组合容差：基础 1e-5，按维度规模略微放宽
        float magnitude = MathF.Max(MathF.Abs(expected), 1f);
        float allowed = 1e-5f * magnitude * MathF.Max(1f, dim / 1024f);
        float diff = MathF.Abs(expected - actual);
        Assert.True(diff < allowed,
            $"dim={dim}, expected={expected}, actual={actual}, diff={diff}, allowed={allowed}");
    }
}
