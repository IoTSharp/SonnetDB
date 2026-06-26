using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SonnetDB.Vector.Model;

namespace SonnetDB.Vector.Compute;

/// <summary>
/// 向量距离函数实现，基于 <see cref="TensorPrimitives"/> 与 <see cref="Vector{T}"/>
/// 提供 SIMD 加速。在支持 AVX-512 的 x64 平台上自动使用 Vector512&lt;float&gt;，
/// 在 ARM64 上自动使用 NEON，其他平台退回 scalar 实现。
/// </summary>
/// <remarks>
/// 全部实现遵循 AGENTS.md 的 safe-only 约束（M0~M7 不使用 unsafe）。
/// </remarks>
public static class Distance
{
    /// <summary>
    /// 计算两个向量的 L2（欧氏）距离平方。
    /// 排序时优先使用平方距离，避免不必要的开方。
    /// </summary>
    /// <param name="a">向量 A（长度须与 B 相同）。</param>
    /// <param name="b">向量 B。</param>
    /// <returns>L2 距离平方（非负值）。</returns>
    /// <exception cref="ArgumentException">当 <paramref name="a"/> 与 <paramref name="b"/> 长度不同时抛出。</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float L2Squared(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ThrowIfLengthMismatch(a, b);
        return L2SquaredCore(a, b);
    }

    /// <summary>
    /// 计算两个向量的 L2（欧氏）距离。
    /// </summary>
    /// <param name="a">向量 A。</param>
    /// <param name="b">向量 B。</param>
    /// <returns>L2 距离（非负值）。</returns>
    public static float L2(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ThrowIfLengthMismatch(a, b);
        // TensorPrimitives.Distance 内部已使用 SIMD，等价于 sqrt(L2Squared)。
        return a.Length == 0 ? 0f : TensorPrimitives.Distance(a, b);
    }

    /// <summary>
    /// 计算两个向量的余弦距离（1 - 余弦相似度）。
    /// 当任一向量范数为 0 时返回 1（最大距离）。
    /// </summary>
    /// <param name="a">向量 A。</param>
    /// <param name="b">向量 B。</param>
    /// <returns>余弦距离，正常情况下范围 [0, 2]。</returns>
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ThrowIfLengthMismatch(a, b);
        if (a.Length == 0)
        {
            return 1f;
        }

        // 自行计算以处理零向量边界条件，TensorPrimitives.CosineSimilarity 在零向量时会产生 NaN。
        (float dot, float normASq, float normBSq) = DotAndNorms(a, b);
        float denom = MathF.Sqrt(normASq) * MathF.Sqrt(normBSq);
        if (denom <= float.Epsilon)
        {
            return 1f;
        }

        float similarity = dot / denom;
        // 数值误差可能让结果略微越界 [-1, 1]，做一次裁剪以保证 Cosine ∈ [0, 2]。
        if (similarity > 1f) { similarity = 1f; }
        else if (similarity < -1f) { similarity = -1f; }
        return 1f - similarity;
    }

    /// <summary>
    /// 计算两个向量的内积（点积）。
    /// </summary>
    /// <param name="a">向量 A。</param>
    /// <param name="b">向量 B。</param>
    /// <returns>内积值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float InnerProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ThrowIfLengthMismatch(a, b);
        return a.Length == 0 ? 0f : TensorPrimitives.Dot(a, b);
    }

    /// <summary>
    /// 计算两个已归一化向量的点积，并返回其作为距离度量的值（1 - dot）。
    /// 调用方需保证向量已 L2 归一化；否则结果近似余弦距离但精度较差。
    /// </summary>
    /// <param name="a">向量 A（应为单位向量）。</param>
    /// <param name="b">向量 B（应为单位向量）。</param>
    /// <returns>1 - 内积，等价于已归一化向量的余弦距离。</returns>
    public static float DotProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ThrowIfLengthMismatch(a, b);
        if (a.Length == 0)
        {
            return 1f;
        }

        return 1f - TensorPrimitives.Dot(a, b);
    }

    /// <summary>
    /// 计算两个二值（位）向量的汉明距离（不同 bit 的数量）。
    /// 实现使用 <see cref="BitOperations.PopCount(ulong)"/> 加速。
    /// </summary>
    /// <param name="a">二值向量 A（按字节存储，长度须与 B 相同）。</param>
    /// <param name="b">二值向量 B。</param>
    /// <returns>不同 bit 的总数。</returns>
    /// <exception cref="ArgumentException">当 <paramref name="a"/> 与 <paramref name="b"/> 长度不同时抛出。</exception>
    public static int Hamming(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException(
                $"二值向量长度不匹配：a.Length={a.Length}, b.Length={b.Length}。");
        }

        int count = 0;
        // 64 位块批量 XOR + popcount，剩余字节逐个处理。
        ReadOnlySpan<ulong> a64 = MemoryMarshal.Cast<byte, ulong>(a);
        ReadOnlySpan<ulong> b64 = MemoryMarshal.Cast<byte, ulong>(b);
        for (int i = 0; i < a64.Length; i++)
        {
            count += BitOperations.PopCount(a64[i] ^ b64[i]);
        }

        int processed = a64.Length * sizeof(ulong);
        for (int i = processed; i < a.Length; i++)
        {
            count += BitOperations.PopCount((uint)(a[i] ^ b[i]));
        }

        return count;
    }

    /// <summary>
    /// 根据 <see cref="Metric"/> 枚举分派到对应距离函数（仅适用于 fp32 实数向量）。
    /// </summary>
    /// <param name="a">向量 A。</param>
    /// <param name="b">向量 B。</param>
    /// <param name="metric">度量类型。</param>
    /// <returns>距离或相似度值（语义取决于 metric，见 <see cref="Metric"/> 注释）。</returns>
    /// <exception cref="NotSupportedException">当指定 <see cref="Metric.Hamming"/> 时抛出（应改用 <see cref="Hamming"/> 重载）。</exception>
    public static float Compute(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Metric metric)
        => metric switch
        {
            Metric.L2 => L2Squared(a, b),
            Metric.Cosine => Cosine(a, b),
            Metric.InnerProduct => InnerProduct(a, b),
            Metric.DotProduct => DotProduct(a, b),
            Metric.Hamming => throw new NotSupportedException(
                "Hamming 度量需要二值向量，请直接调用 Distance.Hamming(ReadOnlySpan<byte>, ReadOnlySpan<byte>)。"),
            _ => throw new NotSupportedException($"尚未支持的度量类型：{metric}。"),
        };

    /// <summary>
    /// L2 距离平方的 scalar 参考实现（仅供测试与跨 SIMD 一致性校验）。
    /// </summary>
    /// <param name="a">向量 A。</param>
    /// <param name="b">向量 B。</param>
    /// <returns>L2 距离平方。</returns>
    internal static float L2SquaredScalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ThrowIfLengthMismatch(a, b);
        double sum = 0d;
        for (int i = 0; i < a.Length; i++)
        {
            double diff = (double)a[i] - b[i];
            sum += diff * diff;
        }
        return (float)sum;
    }

    /// <summary>
    /// 内积的 scalar 参考实现（仅供测试）。
    /// </summary>
    internal static float InnerProductScalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ThrowIfLengthMismatch(a, b);
        double sum = 0d;
        for (int i = 0; i < a.Length; i++)
        {
            sum += (double)a[i] * b[i];
        }
        return (float)sum;
    }

    /// <summary>
    /// 余弦距离的 scalar 参考实现（仅供测试）。
    /// </summary>
    internal static float CosineScalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ThrowIfLengthMismatch(a, b);
        double dot = 0d, na = 0d, nb = 0d;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        double denom = Math.Sqrt(na) * Math.Sqrt(nb);
        if (denom <= double.Epsilon)
        {
            return 1f;
        }
        double sim = dot / denom;
        if (sim > 1d) { sim = 1d; }
        else if (sim < -1d) { sim = -1d; }
        return (float)(1d - sim);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ThrowIfLengthMismatch(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException(
                $"向量维度不匹配：a.Length={a.Length}, b.Length={b.Length}。");
        }
    }

    private static float L2SquaredCore(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length == 0)
        {
            return 0f;
        }

        int i = 0;
        float sum = 0f;

        if (System.Numerics.Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            int width = Vector<float>.Count;
            int simdEnd = a.Length - (a.Length % width);
            Vector<float> acc = Vector<float>.Zero;
            for (; i < simdEnd; i += width)
            {
                Vector<float> va = new(a.Slice(i, width));
                Vector<float> vb = new(b.Slice(i, width));
                Vector<float> diff = va - vb;
                acc += diff * diff;
            }
            sum = System.Numerics.Vector.Sum(acc);
        }

        for (; i < a.Length; i++)
        {
            float diff = a[i] - b[i];
            sum += diff * diff;
        }

        return sum;
    }

    private static (float Dot, float NormASq, float NormBSq) DotAndNorms(
        ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int i = 0;
        float dot = 0f, na = 0f, nb = 0f;

        if (System.Numerics.Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            int width = Vector<float>.Count;
            int simdEnd = a.Length - (a.Length % width);
            Vector<float> vDot = Vector<float>.Zero;
            Vector<float> vNa = Vector<float>.Zero;
            Vector<float> vNb = Vector<float>.Zero;
            for (; i < simdEnd; i += width)
            {
                Vector<float> va = new(a.Slice(i, width));
                Vector<float> vb = new(b.Slice(i, width));
                vDot += va * vb;
                vNa += va * va;
                vNb += vb * vb;
            }
            dot = System.Numerics.Vector.Sum(vDot);
            na = System.Numerics.Vector.Sum(vNa);
            nb = System.Numerics.Vector.Sum(vNb);
        }

        for (; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return (dot, na, nb);
    }
}
