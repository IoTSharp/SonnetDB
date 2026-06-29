using SonnetDB.Vector.Compression;

namespace SonnetDB.Core.Tests.Vector.Compression;

public sealed class JacobiSvdTests
{
    [Fact]
    public void Decompose_IdentityMatrix_ReturnsIdentityFactors()
    {
        const int d = 4;
        float[] m = new float[d * d];
        for (int i = 0; i < d; i++) m[i * d + i] = 1f;

        float[] u = new float[d * d];
        float[] s = new float[d];
        float[] v = new float[d * d];
        JacobiSvd.Decompose(d, m, u, s, v);

        for (int i = 0; i < d; i++)
        {
            Assert.Equal(1f, s[i], 4);
        }
        AssertOrthogonal(u, d, 1e-4f);
        AssertOrthogonal(v, d, 1e-4f);
    }

    [Fact]
    public void Decompose_DiagonalMatrix_RecoversSingularValues()
    {
        const int d = 5;
        float[] diag = [3f, 1f, 4f, 1.5f, 2f];
        float[] m = new float[d * d];
        for (int i = 0; i < d; i++) m[i * d + i] = diag[i];

        float[] u = new float[d * d];
        float[] s = new float[d];
        float[] v = new float[d * d];
        JacobiSvd.Decompose(d, m, u, s, v);

        // 重建 M ≈ U · diag(S) · V^T，应等于原矩阵
        AssertReconstruct(m, u, s, v, d, 1e-4f);
        AssertOrthogonal(u, d, 1e-4f);
        AssertOrthogonal(v, d, 1e-4f);
    }

    [Fact]
    public void Decompose_RandomMatrix_ReconstructsAndIsOrthogonal()
    {
        const int d = 8;
        var rng = new Random(42);
        float[] m = new float[d * d];
        for (int i = 0; i < d * d; i++) m[i] = (float)(rng.NextDouble() * 2.0 - 1.0);

        float[] u = new float[d * d];
        float[] s = new float[d];
        float[] v = new float[d * d];
        JacobiSvd.Decompose(d, m, u, s, v);

        AssertReconstruct(m, u, s, v, d, 1e-3f);
        AssertOrthogonal(u, d, 1e-4f);
        AssertOrthogonal(v, d, 1e-4f);
        for (int i = 0; i < d; i++) Assert.True(s[i] >= 0f);
    }

    [Fact]
    public void SolveOrthogonalProcrustes_RandomMatrix_ProducesOrthogonalResult()
    {
        const int d = 6;
        var rng = new Random(7);
        float[] a = new float[d * d];
        for (int i = 0; i < d * d; i++) a[i] = (float)(rng.NextDouble() * 2.0 - 1.0);

        float[] r = new float[d * d];
        JacobiSvd.SolveOrthogonalProcrustes(d, a, r);

        AssertOrthogonal(r, d, 1e-4f);
    }

    [Fact]
    public void SolveOrthogonalProcrustes_AppliedToRotatedData_RecoversRotation()
    {
        // 构造已知正交矩阵 Q（2D 旋转扩展到 4D），生成 Y = Q · X，
        // 验证 Procrustes(X · Y^T) ≈ Q^T（即让 Q^T · Y 与 X 对齐）。
        // SolveOrthogonalProcrustes 求 R 最大化 tr(R · A)，A = X · Y^T 时
        // R = X · Y^T 的极分解的正交因子，即 Q^T。
        const int d = 4;
        float angle = 0.7f;
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        float[] q = new float[d * d];
        q[0 * d + 0] = c; q[0 * d + 1] = -s;
        q[1 * d + 0] = s; q[1 * d + 1] = c;
        q[2 * d + 2] = 1f;
        q[3 * d + 3] = 1f;

        const int n = 32;
        var rng = new Random(1);
        float[] x = new float[n * d];
        for (int i = 0; i < n * d; i++) x[i] = (float)(rng.NextDouble() * 2.0 - 1.0);

        // Y[i,:] = Q · X[i,:]
        float[] y = new float[n * d];
        for (int i = 0; i < n; i++)
        {
            for (int row = 0; row < d; row++)
            {
                float sum = 0f;
                for (int k = 0; k < d; k++) sum += q[row * d + k] * x[i * d + k];
                y[i * d + row] = sum;
            }
        }

        // A = X^T · Y : A[j,k] = Σ_i X[i,j] · Y[i,k]
        float[] a = new float[d * d];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < d; j++)
                for (int k = 0; k < d; k++)
                    a[j * d + k] += x[i * d + j] * y[i * d + k];

        float[] r = new float[d * d];
        JacobiSvd.SolveOrthogonalProcrustes(d, a, r);

        // R 应等于 Q（最大化 tr(R · A) = tr(R · n·Q^T) 当 R = Q）
        for (int i = 0; i < d; i++)
            for (int j = 0; j < d; j++)
            {
                Assert.Equal(q[i * d + j], r[i * d + j], 2);
            }
    }

    private static void AssertOrthogonal(ReadOnlySpan<float> matrix, int d, float tol)
    {
        // Q^T · Q ≈ I
        for (int i = 0; i < d; i++)
            for (int j = 0; j < d; j++)
            {
                float sum = 0f;
                for (int k = 0; k < d; k++) sum += matrix[k * d + i] * matrix[k * d + j];
                float expected = i == j ? 1f : 0f;
                Assert.True(MathF.Abs(sum - expected) < tol,
                    $"非正交：(Q^T Q)[{i},{j}] = {sum}, 期望 {expected}");
            }
    }

    private static void AssertReconstruct(
        ReadOnlySpan<float> m,
        ReadOnlySpan<float> u,
        ReadOnlySpan<float> s,
        ReadOnlySpan<float> v,
        int d,
        float tol)
    {
        // 验证 M ≈ U · diag(S) · V^T : M[i,j] = Σ_k U[i,k] · S[k] · V[j,k]
        for (int i = 0; i < d; i++)
            for (int j = 0; j < d; j++)
            {
                float sum = 0f;
                for (int k = 0; k < d; k++) sum += u[i * d + k] * s[k] * v[j * d + k];
                Assert.True(MathF.Abs(sum - m[i * d + j]) < tol,
                    $"重建偏差超阈：[{i},{j}] = {sum}, 期望 {m[i * d + j]}");
            }
    }
}
