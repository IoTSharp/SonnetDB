namespace SonnetDB.Vector.Compression;

/// <summary>
/// 纯托管 one-sided Jacobi SVD：对 D×D 方阵求解 <c>M = U · diag(S) · V^T</c>。
/// </summary>
/// <remarks>
/// <para>
/// 仅在量化器训练阶段调用（OPQ 求解 Procrustes），非热路径，使用 <see cref="double"/>
/// 内部累加保证数值稳定。算法采用经典 Rutishauser one-sided Jacobi rotations：
/// 反复对列对 (p, q) 应用旋转使 M 的列两两正交，最终 M 的列即 <c>U·diag(S)</c>，
/// 累积的旋转矩阵即 <c>V</c>。
/// </para>
/// <para>
/// 复杂度 O(sweeps · D^3)。对典型嵌入维度 D ≤ 1024 / sweeps ≤ 50 完全可接受。
/// </para>
/// </remarks>
internal static class JacobiSvd
{
    /// <summary>
    /// 对 D×D 方阵做 SVD：<c>matrix = u · diag(singularValues) · v^T</c>。
    /// </summary>
    /// <param name="d">矩阵维度。</param>
    /// <param name="matrix">输入矩阵，行优先 D×D。</param>
    /// <param name="u">输出左奇异矩阵 U，行优先 D×D，列正交。</param>
    /// <param name="singularValues">输出奇异值 σ_i，长度 D（非负，未必排序）。</param>
    /// <param name="v">输出右奇异矩阵 V，行优先 D×D，列正交。</param>
    /// <param name="maxSweeps">最大扫描轮数。</param>
    /// <param name="tolerance">收敛阈值（off-diagonal 平方和归一化）。</param>
    public static void Decompose(
        int d,
        ReadOnlySpan<float> matrix,
        Span<float> u,
        Span<float> singularValues,
        Span<float> v,
        int maxSweeps = 50,
        double tolerance = 1e-10)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(d);
        if (matrix.Length < d * d) throw new ArgumentException("matrix 长度不足 D×D。", nameof(matrix));
        if (u.Length < d * d) throw new ArgumentException("u 长度不足 D×D。", nameof(u));
        if (v.Length < d * d) throw new ArgumentException("v 长度不足 D×D。", nameof(v));
        if (singularValues.Length < d) throw new ArgumentException("singularValues 长度不足 D。", nameof(singularValues));

        // double 工作矩阵：a 初始为 matrix 的副本，最终列即 U·diag(S)；vw 初始为 I，最终为 V。
        double[] a = new double[d * d];
        double[] vw = new double[d * d];
        for (int i = 0; i < d * d; i++) a[i] = matrix[i];
        for (int i = 0; i < d; i++) vw[i * d + i] = 1.0;

        for (int sweep = 0; sweep < maxSweeps; sweep++)
        {
            double offSum = 0.0;
            for (int p = 0; p < d - 1; p++)
            {
                for (int q = p + 1; q < d; q++)
                {
                    double alpha = 0.0, beta = 0.0, gamma = 0.0;
                    for (int i = 0; i < d; i++)
                    {
                        double aip = a[i * d + p];
                        double aiq = a[i * d + q];
                        alpha += aip * aip;
                        beta += aiq * aiq;
                        gamma += aip * aiq;
                    }
                    offSum += gamma * gamma;

                    if (alpha <= 0.0 || beta <= 0.0)
                    {
                        continue;
                    }
                    if (gamma * gamma <= tolerance * alpha * beta)
                    {
                        continue;
                    }

                    // Jacobi 旋转：让旋转后列 p, q 内积归零。
                    double zeta = (beta - alpha) / (2.0 * gamma);
                    double t = zeta >= 0
                        ? 1.0 / (zeta + Math.Sqrt(1.0 + zeta * zeta))
                        : 1.0 / (zeta - Math.Sqrt(1.0 + zeta * zeta));
                    double c = 1.0 / Math.Sqrt(1.0 + t * t);
                    double s = t * c;

                    for (int i = 0; i < d; i++)
                    {
                        double aip = a[i * d + p];
                        double aiq = a[i * d + q];
                        a[i * d + p] = c * aip - s * aiq;
                        a[i * d + q] = s * aip + c * aiq;

                        double vip = vw[i * d + p];
                        double viq = vw[i * d + q];
                        vw[i * d + p] = c * vip - s * viq;
                        vw[i * d + q] = s * vip + c * viq;
                    }
                }
            }

            // off-diagonal 平方和归一化收敛判据
            double normSq = 0.0;
            for (int i = 0; i < d * d; i++) normSq += a[i] * a[i];
            if (normSq == 0.0 || offSum <= tolerance * normSq)
            {
                break;
            }
        }

        // 提取奇异值与 U：σ_j = ||a[:,j]||；u[:,j] = a[:,j] / σ_j。
        for (int j = 0; j < d; j++)
        {
            double normSq = 0.0;
            for (int i = 0; i < d; i++)
            {
                double v0 = a[i * d + j];
                normSq += v0 * v0;
            }
            double norm = Math.Sqrt(normSq);
            singularValues[j] = (float)norm;
            if (norm > 1e-30)
            {
                double inv = 1.0 / norm;
                for (int i = 0; i < d; i++)
                {
                    u[i * d + j] = (float)(a[i * d + j] * inv);
                }
            }
            else
            {
                // 退化列：选取与已有 U 列正交的标准基补齐，避免 U 奇异。
                for (int i = 0; i < d; i++)
                {
                    u[i * d + j] = i == j ? 1f : 0f;
                }
            }
        }

        for (int i = 0; i < d * d; i++)
        {
            v[i] = (float)vw[i];
        }
    }

    /// <summary>
    /// 求解正交 Procrustes：给定 D×D 方阵 <paramref name="matrix"/>，
    /// 返回最大化 <c>tr(R · matrix)</c> 的正交矩阵 R。
    /// </summary>
    /// <remarks>
    /// 解为 <c>R = V · U^T</c>，其中 <c>matrix = U · Σ · V^T</c>。
    /// </remarks>
    /// <param name="d">矩阵维度。</param>
    /// <param name="matrix">输入方阵 D×D 行优先。</param>
    /// <param name="rotation">输出正交矩阵 R，行优先 D×D。</param>
    public static void SolveOrthogonalProcrustes(int d, ReadOnlySpan<float> matrix, Span<float> rotation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(d);
        if (matrix.Length < d * d) throw new ArgumentException("matrix 长度不足 D×D。", nameof(matrix));
        if (rotation.Length < d * d) throw new ArgumentException("rotation 长度不足 D×D。", nameof(rotation));

        Span<float> u = new float[d * d];
        Span<float> sigma = new float[d];
        Span<float> v = new float[d * d];
        Decompose(d, matrix, u, sigma, v);

        // R = V · U^T : R[i,j] = Σ_k V[i,k] · U[j,k]
        for (int i = 0; i < d; i++)
        {
            for (int j = 0; j < d; j++)
            {
                float sum = 0f;
                for (int k = 0; k < d; k++)
                {
                    sum += v[i * d + k] * u[j * d + k];
                }
                rotation[i * d + j] = sum;
            }
        }
    }
}
